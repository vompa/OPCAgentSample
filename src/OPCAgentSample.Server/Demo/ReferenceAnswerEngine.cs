using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace OPCAgentSample.Server.Demo;

/// <summary>
/// REFERENZ-/VORSCHAU-IMPLEMENTIERUNG der Antwort-Engine: ordnet die Frage (exakter Text oder Kurztitel) einer
/// Antwort aus dem Mockup zu und liefert sonst eine ehrliche Karte. Jede Karte trägt die Kennzeichnung <see cref="NoticeText"/>. Mit API-Key ersetzt sie die ClaudeAnswerEngine.
/// </summary>
public sealed class ReferenceAnswerEngine(IOptions<DemoOptions> options, IDemoClock clock) : IAnswerEngine, ISampleHistorySource
{
    /// <summary>Kennzeichnung jeder Antwort dieser Engine: Es ist keine Antwort von Claude.</summary>
    public const string NoticeText = "Vorbereitete Antwort – Claude ist nicht angebunden";

    internal const string UnansweredHeadline = "Diese Frage beantwortet die Demo erst mit angebundenem Claude.";

    private static readonly Lazy<AnswersFile> s_file = new(() => ReferenceDataFiles.Read<AnswersFile>("answers.json"));

    public async IAsyncEnumerable<AnswerStep> AskAsync(
        AskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var answer = Find(request.RoleId, request.Question);
        if (answer is null)
        {
            await DelayAsync(cancellationToken);
            yield return new AnswerReadyStep(BuildUnansweredCard() with { Notice = NoticeText });
            yield break;
        }

        foreach (var call in answer.ToolCalls)
        {
            await DelayAsync(cancellationToken);
            yield return new ToolCallStep(call);
        }

        await DelayAsync(cancellationToken);
        yield return new AnswerReadyStep(answer.Card with { Notice = NoticeText });
    }

    public SampleTurn? GetSample(string roleId)
    {
        if (!s_file.Value.SampleByRole.TryGetValue(roleId, out var id)) return null;
        var answer = s_file.Value.Answers.Single(a => a.Id == id);
        return new SampleTurn(answer.Question, answer.Card, answer.ToolCalls);
    }

    private static ReferenceAnswer? Find(string roleId, string question)
    {
        var wanted = Normalize(question);
        var matches = s_file.Value.Answers
            .Where(a => Normalize(a.Question) == wanted || (a.ShortTitle is not null && Normalize(a.ShortTitle) == wanted))
            .ToList();
        // Fragen aus dem Postfach können rollenübergreifend sein; die eigene Rolle hat Vorrang.
        return matches.FirstOrDefault(a => a.Role == roleId) ?? matches.FirstOrDefault();
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('?', '.', '!').ToLowerInvariant();

    private AnswerCard BuildUnansweredCard() => new(
        UnansweredHeadline,
        Todo: [],
        new AnswerTable(["Thema", "Daten", "Bewertung"], []),
        Chart: null,
        Why:
        [
            "Im Sample ruft Claude zuerst die Tool-Liste des MCP-Servers ab, wählt die passenden Tools, "
            + "verknüpft die Ergebnisse und wählt die Darstellung nach Art der Daten. Diese Vorschau kennt nur die Fragen der Favoriten und des Postfachs.",
        ],
        DataAsOf: DemoTime.TimeText(clock.UtcNow),
        Sources: ["Referenzdaten ohne Live-Verbindung"],
        Prompt: null,
        Form: "wählt Claude zur Laufzeit");

    private Task DelayAsync(CancellationToken cancellationToken) =>
        options.Value.StreamDelay > TimeSpan.Zero ? Task.Delay(options.Value.StreamDelay, cancellationToken) : Task.CompletedTask;

    private sealed record AnswersFile(IReadOnlyDictionary<string, string> SampleByRole, IReadOnlyList<ReferenceAnswer> Answers);

    private sealed record ReferenceAnswer(
        string Id, string Role, string Question, string? ShortTitle, AnswerCard Card, IReadOnlyList<ToolCall> ToolCalls);
}

/// <summary>Die Demo-Uhr der Referenz-Implementierung: fest auf Do 15.10.2026 09:42 Ortszeit (07:42 UTC).</summary>
public sealed class ReferenceDemoClock : IDemoClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 10, 15, 7, 42, 0, TimeSpan.Zero);
}
