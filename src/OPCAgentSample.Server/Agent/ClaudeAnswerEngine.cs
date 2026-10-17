using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Mcp;
using OPCAgentSample.Server.Observability;

namespace OPCAgentSample.Server.Agent;

/// <summary>
/// Die Antwort-Engine mit Claude. Manuelle Tool-Schleife (statt <c>BetaToolRunner</c>), weil der Server pro Tool-Aufruf sofort ein
/// Ereignis streamen, Laufzeit und Datenstand erfassen, Grenzen für Runden und Aufrufe mit ehrlicher Teilantwort durchsetzen und
/// Tokens je Frage buchen muss. Der Runner liefert dafür keine Einhängepunkte und liegt im Beta-Namespace.
/// Jeder Tool-Aufruf läuft über den eigenen MCP-Endpunkt mit der Rolle des Fragenden (Scoping und Audit wie bei externem Zugriff).
/// </summary>
public sealed partial class ClaudeAnswerEngine(
    IClaudeModelClient model,
    IAgentToolbox toolbox,
    ClaudeQuota quota,
    IOptions<ClaudeOptions> options,
    IDemoClock clock,
    ILogger<ClaudeAnswerEngine> logger) : IAnswerEngine
{
    private const string LimitMessage =
        "Die Grenze der Tool-Aufrufe für diese Frage ist erreicht. Dieser Aufruf wurde nicht ausgeführt. Antworte mit den vorhandenen Ergebnissen und sage ehrlich, was fehlt.";

    public async IAsyncEnumerable<AnswerStep> AskAsync(AskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        var stamp = DataStamp(clock.UtcNow);

        if (request.Question.Length > settings.MaxQuestionLength)
        {
            yield return new AnswerReadyStep(AgentCards.QuestionTooLong(settings.MaxQuestionLength, stamp));
            yield break;
        }

        var ownerKey = request.OwnerKey ?? $"role:{request.RoleId}";
        var decision = await quota.TryAcquireAsync(ownerKey);
        if (!decision.Allowed)
        {
            AppTelemetry.QuotaDenied(decision.Denial.ToString());
            LogQuotaDenied(decision.Denial);
            yield return new AnswerReadyStep(AgentCards.QuotaExhausted(decision.Denial, stamp));
            yield break;
        }

        AppTelemetry.Question(request.RoleId);
        // Der Span trägt nur Rolle und Zähler, nie den Fragetext.
        using var questionSpan = AppTelemetry.Source.StartActivity(AppTelemetry.Spans.ClaudeQuestion, ActivityKind.Internal);
        questionSpan?.SetTag("claude.role", request.RoleId);
        var run = new Run { Context = questionSpan?.Context ?? default };
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RunTimeoutSeconds));

        try
        {
            await foreach (var step in RunAsync(request, ownerKey, run, timeout.Token, cancellationToken))
                yield return step;
        }
        finally
        {
            // Auch bei Abbruch des Clients zählen die verbrauchten Tokens; die Buchung darf nicht am Abbruch scheitern.
            await quota.AddTokensAsync(ownerKey, run.QuotaTokens);
            AppTelemetry.QuestionFinished(Stopwatch.GetElapsedTime(started).TotalSeconds);
            questionSpan?.SetTag("claude.model_calls", run.ModelCalls);
            questionSpan?.SetTag("claude.tool_calls", run.ToolCalls);
            LogUsage(run.ModelCalls, run.ToolCalls, run.Input, run.Output, run.CacheRead, run.CacheWrite);
        }
    }

    private async IAsyncEnumerable<AnswerStep> RunAsync(
        AskRequest request, string ownerKey, Run run, [EnumeratorCancellation] CancellationToken token, CancellationToken clientToken)
    {
        var settings = options.Value;
        var ownerHash = ClaudeRequest.OwnerHash(ownerKey);
        var plantNow = clock.UtcNow;
        var stamp = DataStamp(plantNow);

        var connected = await ConnectAsync(request.RoleId, token, clientToken);
        if (connected.Failure is { } connectFailure)
        {
            yield return new AnswerReadyStep(AgentCards.ModelFailure(connectFailure, stamp));
            yield break;
        }

        await using var connection = connected.Connection!;
        var tools = connected.Tools!;
        var toolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var turns = new List<AgentTurn> { new UserQuestionTurn(ClaudeRequest.QuestionText(request.RoleId, Fmt.Stamp(plantNow), request.Question, request.Report?.Text)) };

        for (var round = 1; round <= settings.MaxRounds + 1; round++)
        {
            // Nach der letzten Runde mit Tools folgt ein Abschlussaufruf ohne Tools: ehrliche Teilantwort statt Abbruch.
            var wrapUp = round > settings.MaxRounds;
            // Der Modellaufruf läuft nebenher; seine Text-Deltas kommen über einen Kanal, damit der Entwurf der Karte
            // (Überschrift zuerst) schon vor dem Ende des Aufrufs als Schritt hinausgeht.
            var deltas = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            var callTask = CallModelStreamedAsync(
                ClaudeRequest.Build(settings, tools, turns, ownerHash, allowTools: !wrapUp), round, run, deltas.Writer, token, clientToken);
            var extractor = new AnswerDraftExtractor();
            await foreach (var delta in deltas.Reader.ReadAllAsync(CancellationToken.None))
            {
                if (extractor.TryAppend(delta, out var draft)) yield return new AnswerDraftStep(draft.Headline, draft.Todo);
            }

            var call = await callTask;
            if (call.Failure is { } failure)
            {
                yield return run.Ready(AgentCards.ModelFailure(failure, stamp));
                yield break;
            }

            var reply = call.Reply!;
            run.AddUsage(reply.Usage);

            var final = Conclude(reply, run, stamp, wrapUp);
            if (final is not null)
            {
                yield return final;
                yield break;
            }

            // Ab hier: stop_reason tool_use mit mindestens einem vollständigen Tool-Aufruf.
            var uses = reply.Blocks.OfType<ModelToolUse>().ToList();
            turns.Add(new AssistantTurn(reply.Blocks));

            var executions = new List<Task<ExecutedTool>>();
            foreach (var use in uses)
            {
                if (run.ToolCalls >= settings.MaxToolCalls)
                {
                    executions.Add(Task.FromResult(ExecutedTool.Skipped(use, LimitMessage)));
                    continue;
                }

                run.ToolCalls++;
                executions.Add(ExecuteAsync(use, toolNames, connection, settings.MaxToolResultChars, run.Context, token));
            }

            // Jeder Aufruf wird gemeldet, sobald er fertig ist; die Ergebnisse gehen gesammelt und in Aufrufreihenfolge zurück.
            var pending = executions.ToList();
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                var executed = await finished;
                if (!executed.Skip) yield return new ToolCallStep(executed.ToCall());
            }

            var results = new List<ToolResultItem>();
            foreach (var execution in executions)
            {
                var executed = await execution;
                if (!executed.Skip)
                {
                    run.ToolsUsed.Add(executed.Use.Name);
                    run.Facts.Add(executed.Facts);
                }

                results.Add(new ToolResultItem(executed.Use.Id, executed.Text, executed.IsError));
            }

            turns.Add(new ToolResultsTurn(results));
            token.ThrowIfCancellationRequested();
        }

        // Unerreichbar: Die letzte Runde ist ein Abschlussaufruf, der immer in Conclude endet.
        yield return new AnswerReadyStep(AgentCards.LimitReached(run.ToolCalls, settings.MaxToolCalls, run.ModelCalls, run.ToolsUsed.Distinct().ToList(), stamp));
    }

    /// <summary>
    /// Wertet die Antwort aus. <see langword="null"/>: Tool-Runde. Sonst die endgültige Karte (oder eine ehrliche Fehlerkarte);
    /// <c>refusal</c> und <c>max_tokens</c> werden vor dem Lesen der Inhalte geprüft, Haiku hat keinen serverseitigen Fallback.
    /// </summary>
    private AnswerReadyStep? Conclude(ModelReply reply, Run run, string stamp, bool wrapUp)
    {
        AnswerReadyStep Ready(AnswerCard card) => run.Ready(card);

        switch (reply.StopReason)
        {
            case "refusal":
                LogStop("refusal");
                return Ready(AgentCards.Refused(stamp));
            case "max_tokens":
                LogStop("max_tokens");
                return Ready(AgentCards.TooLongAnswer(stamp));
            case "tool_use" when !wrapUp && reply.Blocks.OfType<ModelToolUse>().Any():
                return null;
            case "end_turn" or "tool_use" or "stop_sequence":
                break;
            default:
                LogStop(reply.StopReason ?? "(keiner)");
                return Ready(AgentCards.UnexpectedStop(stamp));
        }

        var text = string.Concat(reply.Blocks.OfType<ModelText>().Select(b => b.Text)).Trim();
        if (!AnswerCardFormat.TryRead(text, out var card, out var problem))
        {
            LogInvalidCard(problem);
            LogRejectedCardText(text.Length > RejectedTextLogLength ? text[..RejectedTextLogLength] + " …" : text);
            return wrapUp && run.ToolCalls > 0
                ? Ready(AgentCards.LimitReached(run.ToolCalls, options.Value.MaxToolCalls, run.ModelCalls, run.ToolsUsed.Distinct().ToList(), stamp))
                : Ready(AgentCards.InvalidCard(stamp));
        }

        return run.Ready(Complete(card, run, stamp), cacheable: true);
    }

    /// <summary>Datenstand und Quellen stammen aus den Tool-Ergebnissen, nicht vom Modell.</summary>
    private AnswerCard Complete(AnswerCard card, Run run, string stamp)
    {
        var sources = run.Facts.SelectMany(f => f.Sources).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return card with
        {
            DataAsOf = run.DataAsOfUtc() is { } until ? DataStamp(until) : stamp,
            Sources = sources.Count > 0 ? sources : ["Keine Daten abgefragt"],
        };
    }

    /// <summary>Uhrzeit (09:42), bei anderem Tag als die Demo-Uhr mit Wochentag und Datum.</summary>
    private string DataStamp(DateTimeOffset utc) =>
        DemoTime.ToLocal(utc).Date == DemoTime.ToLocal(clock.UtcNow).Date ? DemoTime.TimeText(utc) : Fmt.Stamp(utc);

    private async Task<Connected> ConnectAsync(string roleId, CancellationToken token, CancellationToken clientToken)
    {
        try
        {
            var tools = await toolbox.ListToolsAsync(token);
            if (tools.Count == 0) return Connected.Fail(ModelFailureKind.Other);

            var connection = await toolbox.ConnectAsync(roleId, token);
            return new Connected(connection, tools, null);
        }
        catch (OperationCanceledException) when (!clientToken.IsCancellationRequested)
        {
            return Connected.Fail(ModelFailureKind.Timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogToolboxFailed(ex.GetType().Name);
            return Connected.Fail(ModelFailureKind.Other);
        }
    }

    /// <summary>Wie <see cref="CallModelTracedAsync"/>, schließt aber am Ende den Kanal der Text-Deltas (auch bei Fehler und Abbruch).</summary>
    private async Task<ModelCallResult> CallModelStreamedAsync(
        Anthropic.Models.Messages.MessageCreateParams request, int round, Run run, ChannelWriter<string> deltas,
        CancellationToken token, CancellationToken clientToken)
    {
        try
        {
            return await CallModelTracedAsync(request, round, run, text => deltas.TryWrite(text), token, clientToken);
        }
        finally
        {
            deltas.TryComplete();
        }
    }

    /// <summary>Ein Modellaufruf als Span <c>claude.model_call</c> (Modell, Runde, Tokens, Stopp-Grund) samt Token-Metrik.</summary>
    private async Task<ModelCallResult> CallModelTracedAsync(
        Anthropic.Models.Messages.MessageCreateParams request, int round, Run run, Action<string>? onTextDelta,
        CancellationToken token, CancellationToken clientToken)
    {
        var modelName = options.Value.Model;
        using var span = AppTelemetry.Source.StartActivity(AppTelemetry.Spans.ClaudeModelCall, ActivityKind.Client, run.Context);
        span?.SetTag("claude.model", modelName);
        span?.SetTag("claude.round", round);

        var call = await CallModelAsync(request, onTextDelta, token, clientToken);
        if (call.Reply is { } reply)
        {
            var usage = reply.Usage;
            span?.SetTag("claude.stop_reason", reply.StopReason);
            span?.SetTag("claude.tokens.input", usage.InputTokens);
            span?.SetTag("claude.tokens.output", usage.OutputTokens);
            span?.SetTag("claude.tokens.cache_read", usage.CacheReadTokens);
            span?.SetTag("claude.tokens.cache_write", usage.CacheWriteTokens);
            AppTelemetry.Tokens(modelName, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens);
        }
        else
        {
            span?.SetTag("claude.failure", call.Failure?.ToString());
            span?.SetStatus(ActivityStatusCode.Error);
        }

        return call;
    }

    private async Task<ModelCallResult> CallModelAsync(
        Anthropic.Models.Messages.MessageCreateParams request, Action<string>? onTextDelta, CancellationToken token, CancellationToken clientToken)
    {
        try
        {
            return new ModelCallResult(await model.CreateAsync(request, onTextDelta, token), null);
        }
        catch (ModelCallException ex)
        {
            LogModelFailed(ex.Kind, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return new ModelCallResult(null, ex.Kind);
        }
        catch (OperationCanceledException) when (!clientToken.IsCancellationRequested)
        {
            LogModelFailed(ModelFailureKind.Timeout, nameof(OperationCanceledException));
            return new ModelCallResult(null, ModelFailureKind.Timeout);
        }
    }

    private static async Task<ExecutedTool> ExecuteAsync(
        ModelToolUse use, HashSet<string> knownTools, IAgentToolConnection connection, int maxChars, ActivityContext parent, CancellationToken token)
    {
        // Client-Span des Aufrufs: Die HTTP-Anfrage an /mcp trägt ihn weiter, der Server-Span mcp.tool hängt darunter.
        using var span = AppTelemetry.Source.StartActivity("claude.tool_call", ActivityKind.Client, parent);
        span?.SetTag("mcp.tool.name", knownTools.Contains(use.Name) ? use.Name : "(unbekannt)");
        var watch = Stopwatch.StartNew();
        ToolOutcome outcome;
        if (!knownTools.Contains(use.Name))
        {
            outcome = new ToolOutcome(true, $"Das Tool '{use.Name}' gibt es nicht.");
        }
        else if (use.Input.ValueKind != JsonValueKind.Object)
        {
            outcome = new ToolOutcome(true, "Die Argumente müssen ein JSON-Objekt sein.");
        }
        else
        {
            try
            {
                var arguments = use.Input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                outcome = await connection.CallAsync(use.Name, arguments, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = new ToolOutcome(true, "Das Tool ist ausgefallen. Versuche es nicht erneut.");
            }
        }

        watch.Stop();
        if (outcome.IsError) span?.SetStatus(ActivityStatusCode.Error);
        var text = outcome.Text.Length > maxChars ? outcome.Text[..maxChars] + " … [gekürzt]" : outcome.Text;
        return new ExecutedTool(use, outcome.IsError, text, ToolFacts.Parse(outcome.Text), (int)watch.ElapsedMilliseconds, Skip: false);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Claude-Frage: {ModelCalls} Modellaufrufe, {ToolCalls} Tool-Aufrufe, Tokens Eingabe {Input}, Ausgabe {Output}, Cache gelesen {CacheRead}, Cache geschrieben {CacheWrite}")]
    private partial void LogUsage(int modelCalls, int toolCalls, long input, long output, long cacheRead, long cacheWrite);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claude-Aufruf fehlgeschlagen: {Kind} ({ExceptionType})")]
    private partial void LogModelFailed(ModelFailureKind kind, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claude-Frage beendet mit stop_reason {StopReason}")]
    private partial void LogStop(string stopReason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Antwortkarte von Claude verworfen: {Problem}")]
    private partial void LogInvalidCard(string problem);

    /// <summary>Gekürzter Text der verworfenen Karte, nur auf Debug-Ebene (zur Fehlersuche am Prompt bzw. Schema).</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Verworfene Antwortkarte: {Text}")]
    private partial void LogRejectedCardText(string text);

    private const int RejectedTextLogLength = 1500;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tageskontingent erreicht: {Denial}")]
    private partial void LogQuotaDenied(QuotaDenial denial);

    [LoggerMessage(Level = LogLevel.Error, Message = "Verbindung zu den MCP-Tools fehlgeschlagen ({ExceptionType})")]
    private partial void LogToolboxFailed(string exceptionType);

    private sealed record Connected(IAgentToolConnection? Connection, IReadOnlyList<AgentTool>? Tools, ModelFailureKind? Failure)
    {
        public static Connected Fail(ModelFailureKind kind) => new(null, null, kind);
    }

    private sealed record ModelCallResult(ModelReply? Reply, ModelFailureKind? Failure);

    /// <summary>Zustand einer Frage: Zähler, Datenstand der Tool-Ergebnisse und verwendete Tools.</summary>
    private sealed class Run
    {
        /// <summary>Kontext des Spans der Frage; Kindspans geben ihn ausdrücklich an (Iteratoren behalten <c>Activity.Current</c> nicht).</summary>
        public ActivityContext Context { get; init; }

        public int ModelCalls { get; private set; }

        public int ToolCalls { get; set; }

        public long Input { get; private set; }

        public long Output { get; private set; }

        public long CacheRead { get; private set; }

        public long CacheWrite { get; private set; }

        public List<ToolFacts> Facts { get; } = [];

        public List<string> ToolsUsed { get; } = [];

        /// <summary>Tokens, die auf das Kontingent zählen: Eingabe, Cache-Schreiben und Ausgabe (gelesener Cache kostet fast nichts).</summary>
        public long QuotaTokens => Input + Output + CacheWrite;

        public AnswerReadyStep Ready(AnswerCard card, bool cacheable = false) =>
            new(card, Clamp(Input), Clamp(Output), Clamp(CacheRead), Clamp(CacheWrite), DataAsOfUtc(), cacheable);

        private static int Clamp(long value) => (int)Math.Min(value, int.MaxValue);

        public void AddUsage(ModelUsage usage)
        {
            ModelCalls++;
            Input += usage.InputTokens;
            Output += usage.OutputTokens;
            CacheRead += usage.CacheReadTokens;
            CacheWrite += usage.CacheWriteTokens;
        }

        /// <summary>Der späteste Datenstand der Tool-Ergebnisse; <see langword="null"/>, wenn kein Tool Daten geliefert hat.</summary>
        public DateTimeOffset? DataAsOfUtc()
        {
            DateTimeOffset? latest = null;
            foreach (var until in Facts.Select(f => f.SourceUntilUtc))
            {
                if (until is { } value && (latest is null || value > latest)) latest = value;
            }

            return latest;
        }
    }

    private sealed record ExecutedTool(ModelToolUse Use, bool IsError, string Text, ToolFacts Facts, int DurationMs, bool Skip)
    {
        public static ExecutedTool Skipped(ModelToolUse use, string text) => new(use, true, text, ToolFacts.None, 0, Skip: true);

        /// <summary>Verweigerung durch das Rollen-Scoping ist kein Fehler des Tools und wird eigens ausgewiesen.</summary>
        private bool IsDenied => IsError && Text.StartsWith("Keine Berechtigung", StringComparison.Ordinal);

        public ToolCall ToCall() => new(
            Use.Name,
            Use.Input,
            DurationMs,
            Source: "mcp",
            DataSource: Facts.Sources.Count > 0
                ? string.Join(" + ", Facts.Sources)
                : IsDenied ? "Verweigert (Rolle)" : IsError ? "Fehler" : "Berechnung",
            DataAsOf: Facts.StampText,
            Outcome: IsDenied ? "denied" : IsError ? "error" : "ok");
    }
}
