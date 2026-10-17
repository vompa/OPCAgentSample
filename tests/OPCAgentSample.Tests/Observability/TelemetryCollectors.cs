using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OPCAgentSample.Server.Observability;

namespace OPCAgentSample.Tests.Observability;

/// <summary>Eine gemessene Zahl mit ihren Tags.</summary>
internal sealed record Measured(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
{
    public string Tag(string name) => Tags.TryGetValue(name, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}

/// <summary>
/// Sammelt alle Messwerte des Meters <c>OPCAgentSample</c> (ohne OpenTelemetry-Pipeline). Die Meter sind prozessweit; parallel
/// laufende Tests schreiben ebenfalls hinein. Tests filtern deshalb auf eindeutige Tag-Werte.
/// </summary>
internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();

    public MetricCollector(string meterName = AppTelemetry.Name)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(Record);
        _listener.SetMeasurementEventCallback<double>(Record);
        _listener.SetMeasurementEventCallback<int>(Record);
        _listener.Start();
    }

    public ConcurrentQueue<Measured> Items { get; } = new();

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct
    {
        Items.Enqueue(new Measured(instrument.Name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture), tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
    }

    public double Sum(string instrument, Func<Measured, bool>? where = null) =>
        Items.Where(m => m.Instrument == instrument && (where?.Invoke(m) ?? true)).Sum(m => m.Value);

    public int Count(string instrument, Func<Measured, bool>? where = null) =>
        Items.Count(m => m.Instrument == instrument && (where?.Invoke(m) ?? true));

    public void Dispose() => _listener.Dispose();
}

/// <summary>Sammelt beendete Spans der ActivitySource <c>OPCAgentSample</c>.</summary>
internal sealed class SpanCollector : IDisposable
{
    private readonly ActivityListener _listener;

    public SpanCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Items.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public ConcurrentQueue<Activity> Items { get; } = new();

    public void Dispose() => _listener.Dispose();
}
