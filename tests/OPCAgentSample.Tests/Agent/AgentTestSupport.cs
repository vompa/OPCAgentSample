using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Agent;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Agent;

/// <summary>Nachgebauter Anthropic-Client: liefert skriptierte Antworten und merkt sich jede Anfrage. Es gibt keinen echten Aufruf.</summary>
internal sealed class ScriptedModelClient : IClaudeModelClient
{
    private readonly Queue<Func<MessageCreateParams, ModelReply>> _script = new();

    public List<MessageCreateParams> Requests { get; } = [];

    /// <summary>Wenn gesetzt, meldet der Client die Textblöcke der Antwort in Stücken dieser Länge als Text-Deltas (wie ein Stream).</summary>
    public int? DeltaSize { get; set; }

    public ScriptedModelClient Then(ModelReply reply)
    {
        _script.Enqueue(_ => reply);
        return this;
    }

    public ScriptedModelClient Then(Func<MessageCreateParams, ModelReply> reply)
    {
        _script.Enqueue(reply);
        return this;
    }

    public ScriptedModelClient ThenFail(ModelFailureKind kind)
    {
        _script.Enqueue(_ => throw new ModelCallException(kind, "skriptierter Fehler"));
        return this;
    }

    public Task<ModelReply> CreateAsync(MessageCreateParams request, Action<string>? onTextDelta, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_script.Count == 0) throw new InvalidOperationException("Das Skript des nachgebauten Clients ist zu Ende: unerwarteter Modellaufruf.");

        var reply = _script.Dequeue()(request);
        if (onTextDelta is not null && DeltaSize is { } size)
        {
            foreach (var text in reply.Blocks.OfType<ModelText>().Select(b => b.Text))
            {
                for (var start = 0; start < text.Length; start += size) onTextDelta(text.Substring(start, Math.Min(size, text.Length - start)));
            }
        }

        return Task.FromResult(reply);
    }
}

/// <summary>Nachgebaute Tools mit festen Ergebnissen; merkt sich Rolle und Aufrufe.</summary>
internal sealed class FakeToolbox : IAgentToolbox
{
    public static readonly IReadOnlyList<AgentTool> DefaultTools =
    [
        new("get_shift_summary", "Schichtbericht", Schema("scope")),
        new("get_active_alarms", "Alarme", Schema("scope")),
        new("get_hall_overview", "Hallenübersicht", Schema("hallId")),
    ];

    public Func<string, string, IReadOnlyDictionary<string, JsonElement>, ToolOutcome> Handler { get; set; } =
        (_, tool, _) => new ToolOutcome(false, Result(tool));

    public IReadOnlyList<AgentTool> Tools { get; set; } = DefaultTools;

    public List<(string Role, string Tool, string Arguments)> Calls { get; } = [];

    public int Connections { get; private set; }

    public Task<IReadOnlyList<AgentTool>> ListToolsAsync(CancellationToken cancellationToken) => Task.FromResult(Tools);

    public Task<IAgentToolConnection> ConnectAsync(string roleId, CancellationToken cancellationToken)
    {
        Connections++;
        return Task.FromResult<IAgentToolConnection>(new Connection(this, roleId));
    }

    /// <summary>Ein Tool-Ergebnis mit Datenstand 09:40 Ortszeit und Quelle, wie jedes Tool des echten Servers sie trägt.</summary>
    public static string Result(string tool, string source = "Redis") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["tool"] = tool,
            ["wert"] = 92,
            ["source_until"] = "2026-10-15T07:40:00Z",
            ["datenstand"] = "Do 15.10. 09:40",
            ["quelle"] = source,
        });

    private static JsonElement Schema(string property) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object> { [property] = new { type = "string", description = "Bereich" } },
            required = new[] { property },
        });

    private sealed class Connection(FakeToolbox owner, string role) : IAgentToolConnection
    {
        public Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
        {
            lock (owner.Calls) owner.Calls.Add((role, name, JsonSerializer.Serialize(arguments)));
            return Task.FromResult(owner.Handler(role, name, arguments));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Bausteine für skriptierte Modellantworten.</summary>
internal static class Replies
{
    public const string ValidCard =
        """{"headline":"RT-03 steht seit 05:12 und kostet Menge.","todo":["Instandhaltung beim Greifer von RT-03 nachfragen."],"table":{"rows":[{"topic":"RT-03","data":"Störung seit 05:12","rating":"crit"},{"topic":"RT-01","data":"Mischung bis 07:20","rating":"warn"}]},"chart":null,"why":["Der Greifer klemmt."]}""";

    public static ModelUsage Usage(long input = 1000, long output = 200, long cacheRead = 0, long cacheWrite = 0) => new(input, output, cacheRead, cacheWrite);

    public static ModelReply Tools(params (string Id, string Name, string Args)[] calls) =>
        new("tool_use", [.. calls.Select(c => (ModelBlock)new ModelToolUse(c.Id, c.Name, JsonDocument.Parse(c.Args).RootElement.Clone()))], Usage());

    public static ModelReply ToolsWithThinking(params (string Id, string Name, string Args)[] calls) =>
        new("tool_use",
            [new ModelThinking("", "sig-1"), .. calls.Select(c => (ModelBlock)new ModelToolUse(c.Id, c.Name, JsonDocument.Parse(c.Args).RootElement.Clone()))],
            Usage());

    public static ModelReply Card(string json = ValidCard, ModelUsage? usage = null) => new("end_turn", [new ModelText(json)], usage ?? Usage());

    public static ModelReply Stop(string reason, params ModelBlock[] blocks) => new(reason, blocks, Usage());
}

/// <summary>Baut die Engine mit nachgebauten Abhängigkeiten (kein Docker, kein Netz).</summary>
internal sealed class EngineHarness
{
    public EngineHarness(Action<ClaudeOptions>? configure = null, Microsoft.Extensions.Logging.ILogger<ClaudeAnswerEngine>? logger = null)
    {
        Options = new ClaudeOptions { ApiKey = "sk-ant-test-geheimer-schluessel" };
        configure?.Invoke(Options);
        Counters = new MemoryQuotaCounters();
        Quota = new ClaudeQuota(Counters, Microsoft.Extensions.Options.Options.Create(Options), Time);
        Engine = new ClaudeAnswerEngine(Model, Toolbox, Quota, Microsoft.Extensions.Options.Options.Create(Options), Clock, logger ?? NullLogger<ClaudeAnswerEngine>.Instance);
    }

    public ClaudeOptions Options { get; }

    public ScriptedModelClient Model { get; } = new();

    public FakeToolbox Toolbox { get; } = new();

    public MemoryQuotaCounters Counters { get; }

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 7, 42, 0, TimeSpan.Zero));

    public ManualDemoClock Clock { get; } = new(new DateTimeOffset(2026, 10, 15, 7, 42, 0, TimeSpan.Zero));

    public ClaudeQuota Quota { get; }

    public ClaudeAnswerEngine Engine { get; }

    public async Task<List<AnswerStep>> AskAsync(string question = "Wie ist die Lage?", string role = "hall_lead", string owner = "code:abc123")
    {
        var steps = new List<AnswerStep>();
        await foreach (var step in Engine.AskAsync(new AskRequest(role, question, owner))) steps.Add(step);
        return steps;
    }
}

internal static class RequestJson
{
    /// <summary>Die Anfrage an die API als JSON, wie das SDK sie sendet (Nachrichten, System, Tools).</summary>
    public static JsonElement Messages(MessageCreateParams request) =>
        JsonDocument.Parse("[" + string.Join(",", request.Messages.Select(m => m.ToString())) + "]").RootElement.Clone();

    public static JsonElement Tools(MessageCreateParams request) =>
        JsonDocument.Parse("[" + string.Join(",", (request.Tools ?? []).Select(t => t.ToString())) + "]").RootElement.Clone();

    public static JsonElement System(MessageCreateParams request) => JsonDocument.Parse(request.System!.ToString()!).RootElement.Clone();
}
