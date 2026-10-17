using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.DemoAccess;

/// <summary>Ein benannter Einladungs-Key: der Name erscheint im Protokoll, vom Key gibt es nur den SHA-256.</summary>
public sealed record DemoInviteCode(string Name, byte[] Sha256);

/// <summary>
/// Liste der Einladungs-Keys aus <c>Demo:InviteCodes</c> im Format <c>name=sha256hex;name2=sha256hex</c> (ein einziger
/// String, damit er als ein Secret bzw. eine Umgebungsvariable übergeben werden kann). Leer heißt: keine Einladungen.
/// </summary>
public static partial class DemoInviteCodes
{
    public const string SettingName = "Demo:InviteCodes";

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamePattern();

    /// <summary>Liest die Liste; bei einem Fehler ist <paramref name="error"/> gesetzt und <paramref name="codes"/> leer.</summary>
    public static bool TryParse(string? setting, out IReadOnlyList<DemoInviteCode> codes, out string? error)
    {
        var parsed = new List<DemoInviteCode>();
        codes = parsed;
        error = null;
        if (string.IsNullOrWhiteSpace(setting)) return true;

        foreach (var entry in setting.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? entry : entry[..separator].Trim();
            if (separator < 0 || !NamePattern().IsMatch(name))
            {
                return Fail(parsed, out error,
                    $"{SettingName}: Jeder Eintrag braucht die Form name=sha256hex; der Name besteht aus 1 bis 40 Zeichen [a-z0-9-].");
            }

            if (parsed.Exists(code => code.Name == name))
                return Fail(parsed, out error, $"{SettingName}: Der Name „{name}“ kommt mehrfach vor.");
            if (!ApiKeyHasher.TryParseHash(entry[(separator + 1)..].Trim(), out var hash))
                return Fail(parsed, out error, $"{SettingName}: Der Hash von „{name}“ muss ein SHA-256 als 64 Hex-Zeichen sein.");

            parsed.Add(new DemoInviteCode(name, hash));
        }

        return true;
    }

    /// <summary>
    /// Name des passenden Einladungs-Keys oder <see langword="null"/>. Verglichen wird in konstanter Zeit über alle Einträge,
    /// damit weder Timing noch die Position eines Treffers etwas verrät.
    /// </summary>
    public static string? Find(IReadOnlyList<DemoInviteCode> codes, string code)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(code);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        string? match = null;
        foreach (var invite in codes)
        {
            if (CryptographicOperations.FixedTimeEquals(actual, invite.Sha256)) match = invite.Name;
        }

        return match;
    }

    private static bool Fail(List<DemoInviteCode> parsed, out string? error, string message)
    {
        parsed.Clear();
        error = message;
        return false;
    }
}
