namespace OPCAgentSample.Server.DemoAccess;

/// <summary>
/// Welche Pfade bei eingeschaltetem Demo-Zugang eine gültige Sitzung verlangen: die API (<c>/api/…</c>) außer Health und
/// Anmeldung (<c>/api/demo/…</c>). Statische Dateien der Oberfläche und <c>/mcp</c>
/// (eigene API-Key-Prüfung) bleiben unberührt.
/// </summary>
public static class DemoAccessRules
{
    public const string HealthPath = "/api/health";

    public static bool RequiresSession(PathString path)
    {
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWithSegments("/api/demo", StringComparison.OrdinalIgnoreCase)) return false;
        return !path.Value!.TrimEnd('/').Equals(HealthPath, StringComparison.OrdinalIgnoreCase);
    }
}
