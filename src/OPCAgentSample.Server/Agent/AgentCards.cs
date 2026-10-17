using System.Globalization;
using System.Text.Json;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Agent;

/// <summary>
/// Karten, die der Server selbst erzeugt, wenn Claude keine brauchbare Antwort liefert: Ablehnung, Kontingent, Grenzen und Fehler.
/// Sie nennen ehrlich, was fehlt, und enthalten nie Details der Anbindung (Schlüssel, Fehlertexte der API).
/// </summary>
public static class AgentCards
{
    private const string NoToolSource = "Keine Daten abgefragt";

    public static AnswerCard Notice(string headline, string reason, string dataAsOf, IReadOnlyList<string>? todo = null) =>
        new(headline, todo ?? [], new AnswerTable(["Thema", "Daten", "Bewertung"], []), Chart: null, Why: [reason], dataAsOf, [NoToolSource], Prompt: null, Form: "Text");

    public static AnswerCard QuestionTooLong(int limit, string dataAsOf) =>
        Notice($"Die Frage ist zu lang (mehr als {limit} Zeichen).", "Bitte stelle die Frage kürzer, am besten zu einer Station, einer Schicht oder einem Thema.", dataAsOf);

    public static AnswerCard QuotaExhausted(QuotaDenial denial, string dataAsOf) => denial switch
    {
        QuotaDenial.OwnerTokens => Notice(
            "Das Tageskontingent an Tokens für diese Demo ist aufgebraucht.",
            "Claude beantwortet heute keine weiteren Fragen von diesem Zugang. Morgen geht es wieder; die bisherigen Antworten bleiben im Verlauf.", dataAsOf),
        QuotaDenial.TotalQuestions => Notice(
            "Die Demo hat heute ihr Kostenlimit erreicht.",
            "Alle Besucher zusammen haben das Tageslimit an Fragen verbraucht. Morgen geht es wieder; die bisherigen Antworten bleiben im Verlauf.", dataAsOf),
        _ => Notice(
            "Das Tageskontingent an Fragen für diese Demo ist aufgebraucht.",
            "Von diesem Zugang sind heute keine weiteren Fragen an Claude möglich. Morgen geht es wieder; die bisherigen Antworten bleiben im Verlauf.", dataAsOf),
    };

    public static AnswerCard ModelFailure(ModelFailureKind kind, string dataAsOf) => kind switch
    {
        ModelFailureKind.RateLimited => Notice(
            "Claude ist gerade ausgelastet.", "Das Ratenlimit der Anbindung ist erreicht. Bitte frage in ein bis zwei Minuten noch einmal.", dataAsOf),
        ModelFailureKind.ServerError => Notice(
            "Claude ist vorübergehend nicht verfügbar.", "Der Dienst meldet eine Störung oder Überlastung. Bitte versuche es gleich noch einmal.", dataAsOf),
        ModelFailureKind.Connection => Notice(
            "Die Verbindung zu Claude ist gestört.", "Der Server erreicht den Dienst gerade nicht. Bitte versuche es gleich noch einmal.", dataAsOf),
        ModelFailureKind.Timeout => Notice(
            "Die Antwort hat zu lange gedauert.", "Claude hat innerhalb der Zeitgrenze keine Antwort geliefert. Eine engere Frage (eine Station, eine Schicht) geht schneller.", dataAsOf),
        ModelFailureKind.Misconfigured => Notice(
            "Die Anbindung an Claude ist fehlerhaft eingerichtet.", "Bitte informiere den Betreiber der Demo. Die Einzelheiten stehen im Server-Log.", dataAsOf),
        _ => Notice(
            "Claude konnte die Frage nicht beantworten.", "Es ist ein unerwarteter Fehler aufgetreten. Bitte versuche es noch einmal.", dataAsOf),
    };

    public static AnswerCard Refused(string dataAsOf) => Notice(
        "Claude beantwortet diese Frage nicht.",
        "Das Modell hat die Anfrage abgelehnt. Frage bitte zu Anlage, Kennzahlen, Wartung oder Rückverfolgung der Halle.", dataAsOf);

    public static AnswerCard TooLongAnswer(string dataAsOf) => Notice(
        "Die Antwort wurde wegen der Längenbegrenzung abgebrochen.",
        "Claude ist nicht fertig geworden. Eine engere Frage (eine Station, eine Schicht, ein Thema) führt zu einer kürzeren Antwort.", dataAsOf);

    public static AnswerCard InvalidCard(string dataAsOf) => Notice(
        "Die Antwort von Claude war nicht brauchbar.",
        "Die Antwortkarte hat die Prüfung des Servers nicht bestanden und wird nicht angezeigt. Bitte stelle die Frage noch einmal.", dataAsOf);

    public static AnswerCard UnexpectedStop(string dataAsOf) => Notice(
        "Claude hat die Antwort nicht abgeschlossen.", "Das Modell ist unerwartet stehen geblieben. Bitte stelle die Frage noch einmal.", dataAsOf);

    /// <summary>Wenn auch der Abschlussaufruf nach Erreichen der Grenzen keine Karte liefert: nur das, was der Server sicher weiß.</summary>
    public static AnswerCard LimitReached(int toolCalls, int maxToolCalls, int rounds, IReadOnlyList<string> toolsUsed, string dataAsOf) => new(
        "Die Frage war für die Grenzen der Demo zu umfangreich; es gibt nur eine Teilantwort.",
        Todo: ["Stelle die Frage enger, z. B. zu einer Station, einer Schicht oder einem Thema."],
        new AnswerTable(["Grenze", "Wert", "Bewertung"],
        [
            new AnswerTableRow(["Tool-Aufrufe", string.Create(CultureInfo.InvariantCulture, $"{toolCalls} von höchstens {maxToolCalls}")], Rating.Warn),
            new AnswerTableRow(["Modellrunden", rounds.ToString(CultureInfo.InvariantCulture)], Rating.Info),
        ]),
        Chart: null,
        Why: [$"Abgefragt wurden: {(toolsUsed.Count == 0 ? "nichts" : string.Join(", ", toolsUsed))}. Claude hat daraus keine verwertbare Antwort geformt."],
        dataAsOf, [NoToolSource], Prompt: null, Form: "Tabelle");
}

/// <summary>Datenstand und Quellen aus einem Tool-Ergebnis (<c>source_until</c>, <c>datenstand</c>, <c>quelle</c>, jedes Ergebnis trägt sie).</summary>
public sealed record ToolFacts(DateTimeOffset? SourceUntilUtc, string? StampText, IReadOnlyList<string> Sources)
{
    public static readonly ToolFacts None = new(null, null, []);

    public static ToolFacts Parse(string resultText)
    {
        try
        {
            using var document = JsonDocument.Parse(resultText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;

            DateTimeOffset? until = root.TryGetProperty("source_until", out var u) && u.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
            var stamp = root.TryGetProperty("datenstand", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var sources = root.TryGetProperty("quelle", out var q) && q.ValueKind == JsonValueKind.String
                ? (q.GetString() ?? string.Empty).Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            return new ToolFacts(until, stamp, sources);
        }
        catch (JsonException)
        {
            return None;
        }
    }
}
