using Microsoft.Extensions.Options;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// Hält berechnete Maschinen-Reports kurz vor (je Zeitraum und Vergleich), damit offene Report-Seiten und mehrere Rollen die
/// Datenbank nicht bei jedem Aufruf belasten. Der Report hängt nicht von der Rolle ab; sie bestimmt nur den Standardzeitraum.
/// </summary>
public sealed class MachineReportCache(IOptions<ReportsOptions> options, TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, MachineReportResponse Report)> _entries = [];

    public void Dispose() => _gate.Dispose();

    public async Task<MachineReportResponse> GetAsync(string range, bool compare, MachineReportService service, CancellationToken cancellationToken)
    {
        var key = $"{range}|{compare}";
        if (TryGetFresh(key, out var fresh)) return fresh;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFresh(key, out var again)) return again;

            var report = await service.BuildAsync(string.Empty, range, compare, cancellationToken);
            _entries[key] = (timeProvider.GetUtcNow(), report);
            return report;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private bool TryGetFresh(string key, out MachineReportResponse report)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry) && timeProvider.GetUtcNow() - entry.At < options.Value.MachineCacheDuration)
            {
                report = entry.Report;
                return true;
            }
        }

        report = null!;
        return false;
    }
}
