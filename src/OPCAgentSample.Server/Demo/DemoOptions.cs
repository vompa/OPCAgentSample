using Microsoft.Extensions.Options;
using OPCAgentSample.Server.DemoAccess;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.Demo;

/// <summary>
/// Einstellungen der Demo (Abschnitt <c>Demo</c>): Streaming-Eindruck der Referenz-Engine und der Zugang der öffentlichen
/// Live-Demo (gemeinsamer Zugangscode und benannte Einladungs-Keys, signiertes Sitzungs-Cookie). Der Zugang ist standardmäßig aus; eingeschaltet
/// müssen alle Geheimnisse gesetzt sein, sonst startet die Anwendung nicht.
/// </summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";
    public const int MinCookieKeyLength = 32;
    public const int DefaultSessionHours = 8;

    /// <summary>Pause zwischen den Schritten einer Antwort (Streaming-Eindruck der Referenz-Engine).</summary>
    public TimeSpan StreamDelay { get; set; } = TimeSpan.FromMilliseconds(90);

    /// <summary>Schaltet den Zugang per Zugangscode ein: Die API verlangt dann eine gültige Sitzung.</summary>
    public bool Enabled { get; set; }

    /// <summary>SHA-256 (64 Hex-Zeichen) des Zugangscodes, den der Betreiber an Eingeladene gibt.</summary>
    public string AccessCodeSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Benannte Einladungs-Keys neben dem Zugangscode, Format <c>name=sha256hex;name2=sha256hex</c> (leer = keine). Ein String,
    /// damit er als ein Secret bzw. <c>Demo__InviteCodes</c> übergeben werden kann; Details in <see cref="DemoInviteCodes"/>.
    /// </summary>
    public string InviteCodes { get; set; } = string.Empty;

    /// <summary>Schlüssel (mindestens 32 Zeichen) zum Signieren der Sitzung; bleibt über Neustarts gleich, damit Anmeldungen halten.</summary>
    public string CookieKey { get; set; } = string.Empty;

    /// <summary>Gültigkeit einer Sitzung in Stunden.</summary>
    public int SessionHours { get; set; } = DefaultSessionHours;

    /// <summary>Die gültigen Einladungs-Keys; bei fehlerhafter Konfiguration (die <see cref="Validate"/> meldet) leer.</summary>
    public IReadOnlyList<DemoInviteCode> ParseInviteCodes() =>
        DemoInviteCodes.TryParse(InviteCodes, out var codes, out _) ? codes : [];

    /// <summary>Fehlermeldung bei ungültiger Konfiguration, sonst <see langword="null"/>. Ausgeschaltet wird nichts geprüft.</summary>
    public string? Validate()
    {
        if (!Enabled) return null;

        if (!ApiKeyHasher.TryParseHash(AccessCodeSha256, out _))
            return "Demo:AccessCodeSha256 muss ein SHA-256 als 64 Hex-Zeichen sein.";
        if (!DemoInviteCodes.TryParse(InviteCodes, out _, out var inviteError))
            return inviteError;
        if (CookieKey.Length < MinCookieKeyLength)
            return $"Demo:CookieKey ist zu kurz (mindestens {MinCookieKeyLength} Zeichen).";
        if (SessionHours is < 1 or > 72)
            return "Demo:SessionHours muss zwischen 1 und 72 liegen.";
        return null;
    }
}

/// <summary>Meldet Konfigurationsfehler des Zugangs beim Start mit Klartext statt erst beim ersten Aufruf.</summary>
public sealed class DemoOptionsValidator : IValidateOptions<DemoOptions>
{
    public ValidateOptionsResult Validate(string? name, DemoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
