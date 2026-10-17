using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Agent;

/// <summary>Eine gespeicherte, geprüfte Antwort von Claude samt ihrer Tool-Aufrufe.</summary>
public sealed record CachedAnswer(AnswerCard Card, IReadOnlyList<ToolCall> ToolCalls, DateTimeOffset? DataAsOfUtc, DateTimeOffset StoredAtUtc);

/// <summary>
/// Wiederholungsschutz um die Claude-Engine: Stellt derselbe Besitzer in derselben Rolle dieselbe Frage innerhalb von
/// <see cref="ClaudeOptions.RepeatWindowSeconds"/> noch einmal, liefert er die gespeicherte Karte sofort aus. Das kostet weder
/// Tokens noch Kontingent, weil die Engine gar nicht erst aufgerufen wird. Das ist zulässig, weil jede Frage ohne Gesprächsverlauf an
/// Claude geht: Dieselbe Frage hat daher dieselbe Antwortgrundlage, egal in welchem Gespräch sie fällt.
/// <list type="bullet">
/// <item>Gespeichert werden nur geprüfte Claude-Karten (<see cref="AnswerReadyStep.Cacheable"/>), nie Fehler- oder Grenzkarten.</item>
/// <item>Eine gleichzeitige gleiche Anfrage (Doppelklick) wartet auf das Ergebnis der ersten, statt Claude erneut aufzurufen.
/// Scheitert die erste, fragt die wartende selbst.</item>
/// <item>Bei einem Treffer werden die gespeicherten Tool-Aufrufe wiederholt ausgegeben, damit das MCP-Band wie bei der
/// ersten Antwort gefüllt ist; sie werden nicht erneut ausgeführt.</item>
/// <item><see cref="AskRequest.Fresh"/> umgeht den Speicher und überschreibt den Eintrag mit der neuen Antwort.</item>
/// </list>
/// Der Speicher liegt nur im Prozess (kleiner Zeitwert; nach einem Neustart oder auf einer anderen Instanz wird neu gefragt).
/// </summary>
public sealed partial class RepeatingAnswerEngine(
    IAnswerEngine inner, IOptions<ClaudeOptions> options, TimeProvider time, ILogger<RepeatingAnswerEngine> logger) : IAnswerEngine
{
    /// <summary>Obergrenze der Einträge; darüber werden abgelaufene und dann die ältesten verworfen.</summary>
    internal const int MaxEntries = 1000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async IAsyncEnumerable<AnswerStep> AskAsync(AskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = TimeSpan.FromSeconds(options.Value.RepeatWindowSeconds);
        if (window <= TimeSpan.Zero)
        {
            await foreach (var step in inner.AskAsync(request, cancellationToken)) yield return step;
            yield break;
        }

        var key = Key(request);
        var claim = request.Fresh ? Lead(key) : await JoinOrLeadAsync(key, window, cancellationToken);
        if (claim.Hit is { } hit)
        {
            LogRepeat(request.RoleId);
            foreach (var call in hit.ToolCalls) yield return new ToolCallStep(call);
            yield return new AnswerReadyStep(hit.Card, DataAsOfUtc: hit.DataAsOfUtc, Repeat: true);
            yield break;
        }

        var entry = claim.Lead!;
        var toolCalls = new List<ToolCall>();
        try
        {
            await foreach (var step in inner.AskAsync(request, cancellationToken))
            {
                if (step is ToolCallStep toolStep)
                {
                    toolCalls.Add(toolStep.Call);
                }
                else if (step is AnswerReadyStep ready)
                {
                    // Sofort veröffentlichen: Wartende brauchen nicht auf das Ausliefern an den eigenen Client zu warten.
                    Publish(key, entry, ready.Cacheable ? new CachedAnswer(ready.Card, toolCalls, ready.DataAsOfUtc, time.GetUtcNow()) : null);
                }

                yield return step;
            }
        }
        finally
        {
            // Abbruch oder Fehler vor der Antwort: Wartende dürfen nicht hängen bleiben und fragen selbst.
            Publish(key, entry, null);
        }
    }

    /// <summary>Besitzer, Rolle, normalisierte Frage und Quelle des Report-Kontexts; der Besitzer ist wie in der Engine die Kennung des Besuchers, sonst die Rolle.</summary>
    internal static string Key(AskRequest request) =>
        string.Join('\u001f', request.OwnerKey ?? $"role:{request.RoleId}", request.RoleId, Normalize(request.Question), request.Report?.Key ?? string.Empty);

    /// <summary>Kleinschreibung, zusammengefasste Leerzeichen, ohne <c>? . !</c> am Ende: „Wie ist die Lage ?“ und „wie ist die lage“ sind dieselbe Frage.</summary>
    internal static string Normalize(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var words = question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words).ToLowerInvariant().TrimEnd('?', '.', '!', ' ');
    }

    private bool Expired(CachedAnswer answer, TimeSpan window) => time.GetUtcNow() - answer.StoredAtUtc >= window;

    private Claim Lead(string key)
    {
        var entry = new Entry();
        TrimWhenFull();
        _entries[key] = entry;
        return new Claim(null, entry);
    }

    private async Task<Claim> JoinOrLeadAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        while (true)
        {
            var candidate = new Entry();
            var existing = _entries.GetOrAdd(key, candidate);
            if (ReferenceEquals(existing, candidate))
            {
                TrimWhenFull();
                return new Claim(null, candidate);
            }

            // Läuft noch: auf das Ergebnis der ersten Anfrage warten.
            var answer = await existing.Completion.Task.WaitAsync(cancellationToken);
            if (answer is not null && !Expired(answer, window)) return new Claim(answer, null);

            // Fehlgeschlagen oder abgelaufen: den alten Eintrag ersetzen (nur einer von mehreren Wartenden gewinnt, die anderen lesen neu).
            if (_entries.TryUpdate(key, candidate, existing)) return new Claim(null, candidate);
        }
    }

    /// <summary>Schließt den Eintrag ab. Ohne Antwort verschwindet er, damit die nächste Frage neu fragt; ein zweiter Aufruf ist wirkungslos.</summary>
    private void Publish(string key, Entry entry, CachedAnswer? answer)
    {
        if (!entry.Completion.TrySetResult(answer)) return;
        if (answer is null) _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
    }

    private void TrimWhenFull()
    {
        if (_entries.Count < MaxEntries) return;

        var now = time.GetUtcNow();
        var window = TimeSpan.FromSeconds(options.Value.RepeatWindowSeconds);
        foreach (var (key, entry) in _entries)
        {
            if (entry.Completion.Task is { IsCompletedSuccessfully: true } done && (done.Result is null || now - done.Result.StoredAtUtc >= window))
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }

        // Immer noch voll (viele Fragen in kurzer Zeit): die ältesten fertigen Einträge verwerfen.
        if (_entries.Count < MaxEntries) return;

        foreach (var (key, entry) in _entries
                     .Where(e => e.Value.Completion.Task.IsCompletedSuccessfully && e.Value.Completion.Task.Result is not null)
                     .OrderBy(e => e.Value.Completion.Task.Result!.StoredAtUtc)
                     .Take(MaxEntries / 10))
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Wiederholte Frage in der Rolle {Role}: gespeicherte Antwort ausgeliefert, keine Tokens, kein Kontingent")]
    private partial void LogRepeat(string role);

    private sealed class Entry
    {
        public TaskCompletionSource<CachedAnswer?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Claim(CachedAnswer? Hit, Entry? Lead);
}
