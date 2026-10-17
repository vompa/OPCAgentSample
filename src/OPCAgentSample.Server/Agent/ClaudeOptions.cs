using Microsoft.Extensions.Options;

namespace OPCAgentSample.Server.Agent;

/// <summary>
/// Einstellungen der Claude-Anbindung (Abschnitt <c>Claude</c>). Der API-Key kommt nur aus User-Secrets oder der Umgebungsvariable
/// <c>Claude__ApiKey</c>, nie aus <c>appsettings.json</c>. Ohne Key bleibt die Referenz-Engine mit Kennzeichnung aktiv.
/// </summary>
public sealed class ClaudeOptions
{
    public const string SectionName = "Claude";

    public const string DefaultModel = "claude-haiku-5-5";

    /// <summary>API-Key für Anthropic. Bleibt auf dem Server; er wird weder geloggt noch an die Oberfläche geschickt.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Genau diese Modell-ID, ohne Datumssuffix.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// <c>low</c>, <c>medium</c>, <c>high</c> oder <c>max</c>. Standard <c>medium</c>: Die Frage verlangt Tool-Auswahl und eine
    /// zahlengetreue Antwort; <c>low</c> spart Kosten, wählt aber öfter zu wenige Tools.
    /// </summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Obergrenze der Ausgabe je Modellaufruf (adaptives Denken zählt mit).</summary>
    public int MaxTokens { get; set; } = 6000;

    /// <summary>Modellaufrufe mit Tools je Frage; danach folgt ein Abschlussaufruf ohne Tools (ehrliche Teilantwort).</summary>
    public int MaxRounds { get; set; } = 6;

    /// <summary>Tool-Aufrufe insgesamt je Frage.</summary>
    public int MaxToolCalls { get; set; } = 12;

    /// <summary>Länge eines Tool-Ergebnisses, die an das Modell geht; längere Ergebnisse werden gekürzt.</summary>
    public int MaxToolResultChars { get; set; } = 16_000;

    public int MaxQuestionLength { get; set; } = 400;

    public int RunTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Wiederholungsschutz: So lange (Sekunden) liefert der Server dieselbe Frage desselben Besitzers in derselben Rolle aus dem Speicher
    /// aus, ohne Claude zu fragen (keine Tokens, kein Kontingent). 0 schaltet den Schutz aus.
    /// </summary>
    public int RepeatWindowSeconds { get; set; } = 300;

    /// <summary>Fragen je Besitzer und Tag (UTC).</summary>
    public int DailyQuestionsPerOwner { get; set; } = 30;

    /// <summary>Tokens (Eingabe, Cache-Schreiben und Ausgabe) je Besitzer und Tag.</summary>
    public int DailyTokensPerOwner { get; set; } = 400_000;

    /// <summary>Fragen aller Besitzer zusammen je Tag: Obergrenze der Kosten einer öffentlichen Demo.</summary>
    public int DailyQuestionsTotal { get; set; } = 500;

    /// <summary>Optional: Adresse des eigenen <c>/mcp</c>-Endpunkts. Standard: <c>http://127.0.0.1:{Port der Anfrage}/mcp</c>. Nur Loopback.</summary>
    public string? McpUrl { get; set; }

    /// <summary>Claude ist angebunden, sobald ein Key gesetzt ist.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);

    public string? Validate()
    {
        if (!Enabled) return null;

        if (string.IsNullOrWhiteSpace(Model)) return "Claude:Model fehlt.";
        if (ParseEffort(Effort) is null) return "Claude:Effort muss low, medium, high oder max sein.";
        if (MaxTokens is < 1024 or > 32_000) return "Claude:MaxTokens muss zwischen 1024 und 32000 liegen.";
        if (MaxRounds is < 1 or > 10) return "Claude:MaxRounds muss zwischen 1 und 10 liegen.";
        if (MaxToolCalls is < 1 or > 40) return "Claude:MaxToolCalls muss zwischen 1 und 40 liegen.";
        if (MaxToolResultChars is < 1000 or > 100_000) return "Claude:MaxToolResultChars muss zwischen 1000 und 100000 liegen.";
        if (MaxQuestionLength is < 10 or > 500) return "Claude:MaxQuestionLength muss zwischen 10 und 500 liegen.";
        if (RunTimeoutSeconds is < 5 or > 600) return "Claude:RunTimeoutSeconds muss zwischen 5 und 600 liegen.";
        if (RepeatWindowSeconds is < 0 or > 3600) return "Claude:RepeatWindowSeconds muss zwischen 0 und 3600 liegen.";
        if (DailyQuestionsPerOwner < 1 || DailyTokensPerOwner < 1 || DailyQuestionsTotal < 1)
            return "Claude: Die Tageslimits müssen mindestens 1 sein.";

        if (McpUrl is not null && (!Uri.TryCreate(McpUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback))
            return "Claude:McpUrl muss eine absolute http-Adresse auf localhost sein.";

        return null;
    }

    /// <summary>Wandelt den Text in die Stufe um; <see langword="null"/> bei unbekanntem Wert.</summary>
    public static AgentEffort? ParseEffort(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "low" => AgentEffort.Low,
        "medium" => AgentEffort.Medium,
        "high" => AgentEffort.High,
        "max" => AgentEffort.Max,
        _ => null,
    };
}

public enum AgentEffort
{
    Low,
    Medium,
    High,
    Max,
}

/// <summary>Meldet Konfigurationsfehler beim Start mit Klartext statt erst bei der ersten Frage.</summary>
public sealed class ClaudeOptionsValidator : IValidateOptions<ClaudeOptions>
{
    public ValidateOptionsResult Validate(string? name, ClaudeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
