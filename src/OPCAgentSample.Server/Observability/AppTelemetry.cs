using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OPCAgentSample.Server.Observability;

/// <summary>
/// Die eigenen Messpunkte des Servers: ein <see cref="Meter"/> und eine <see cref="ActivitySource"/> namens
/// <c>OPCAgentSample</c>. Die Namen sind Konstanten, weil Exporteure und Auswertungen auf sie verweisen.
/// Datenschutz: Weder Metriken noch Spans enthalten Frage- oder Antworttexte oder Schlüssel.
/// </summary>
public static class AppTelemetry
{
    public const string Name = "OPCAgentSample";

    public const string McpToolCalls = "mcp.tool.calls";
    public const string McpToolDuration = "mcp.tool.duration";
    public const string ClaudeQuestions = "claude.questions";
    public const string ClaudeQuestionDuration = "claude.question.duration";
    public const string ClaudeTokens = "claude.tokens";
    public const string ClaudeQuotaDenials = "claude.quota.denials";
    public const string CollectorLag = "collector.lag";
    public const string IngestRecords = "ingest.records";
    public const string LiveSseConnections = "live.sse.connections";

    /// <summary>Span-Namen.</summary>
    public static class Spans
    {
        public const string McpTool = "mcp.tool";
        public const string ClaudeQuestion = "claude.question";
        public const string ClaudeModelCall = "claude.model_call";
        public const string CollectorIngestWindow = "collector.ingest_window";
        public const string RedisCommand = "redis.command";
    }

    /// <summary>Art der Tokens für <see cref="ClaudeTokens"/>.</summary>
    public static class TokenKinds
    {
        public const string Input = "input";
        public const string Output = "output";
        public const string CacheRead = "cache_read";
        public const string CacheWrite = "cache_write";
    }

    /// <summary>Ergebnis eines MCP-Tool-Aufrufs (wie im Audit-Log).</summary>
    public static class Results
    {
        public const string Ok = "ok";
        public const string Denied = "denied";
        public const string Error = "error";
    }

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name, "1.0.0");

    private static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>(McpToolCalls, description: "MCP-Tool-Aufrufe nach Tool, Rolle und Ergebnis (ok/denied/error)");

    private static readonly Histogram<double> ToolDuration =
        Meter.CreateHistogram<double>(McpToolDuration, unit: "ms", description: "Dauer eines MCP-Tool-Aufrufs");

    private static readonly Counter<long> Questions =
        Meter.CreateCounter<long>(ClaudeQuestions, description: "An Claude gestellte Fragen (nach dem Kontingent)");

    private static readonly Histogram<double> QuestionDuration =
        Meter.CreateHistogram<double>(ClaudeQuestionDuration, unit: "s", description: "Dauer einer Frage von Anfang bis Antwort");

    private static readonly Counter<long> TokensCounter =
        Meter.CreateCounter<long>(ClaudeTokens, description: "Verbrauchte Tokens nach Art (input/output/cache_read/cache_write)");

    private static readonly Counter<long> QuotaDenials =
        Meter.CreateCounter<long>(ClaudeQuotaDenials, description: "Fragen, die das Tageskontingent abgelehnt hat");

    private static readonly Counter<long> IngestRecordsCounter =
        Meter.CreateCounter<long>(IngestRecords, description: "In Redis aufgenommene Datensätze");

    private static Func<DateTimeOffset>? Clock;
    private static long WatermarkTicks;
    private static int SseConnections;
    private static long QuotaDenialsTotal;
    private static long IngestedTotal;

    // Gauges lesen den Zustand beim Export; sie halten keine Verweise auf Dienste.
    private static readonly ObservableGauge<double> Lag = Meter.CreateObservableGauge(
        CollectorLag, ObserveLag, unit: "s", description: "Rückstand der Aufnahme: Simulationsuhr minus Wasserstand");

    private static readonly ObservableGauge<int> Sse = Meter.CreateObservableGauge(
        LiveSseConnections, () => Volatile.Read(ref SseConnections), description: "Offene Live-Verbindungen (SSE)");

    /// <summary>Stellt sicher, dass die Gauge-Instrumente angelegt sind (statische Felder).</summary>
    internal static bool GaugesRegistered => Lag is not null && Sse is not null;

    public static void ToolCall(string tool, string role, string result, double durationMs)
    {
        ToolCalls.Add(1, new("tool", tool), new("role", role), new("result", result));
        ToolDuration.Record(durationMs, new KeyValuePair<string, object?>("tool", tool));
    }

    public static void Question(string role) => Questions.Add(1, new KeyValuePair<string, object?>("role", role));

    public static void QuestionFinished(double seconds) => QuestionDuration.Record(seconds);

    public static void QuotaDenied(string reason)
    {
        Interlocked.Increment(ref QuotaDenialsTotal);
        QuotaDenials.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public static void Tokens(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        void Add(string kind, long value)
        {
            if (value > 0) TokensCounter.Add(value, new("kind", kind), new("model", model));
        }

        Add(TokenKinds.Input, input);
        Add(TokenKinds.Output, output);
        Add(TokenKinds.CacheRead, cacheRead);
        Add(TokenKinds.CacheWrite, cacheWrite);
    }

    public static void Ingested(string source, long records)
    {
        Interlocked.Add(ref IngestedTotal, records);
        IngestRecordsCounter.Add(records, new KeyValuePair<string, object?>("source", source));
    }


    /// <summary>Die Simulationsuhr, gegen die der Gauge <see cref="CollectorLag"/> den Wasserstand misst (die zuletzt gesetzte gilt).</summary>
    public static void UseSimulationClock(Func<DateTimeOffset> clock) => Volatile.Write(ref Clock, clock);

    /// <summary>Merkt den Wasserstand der Aufnahme (Ende des zuletzt aufgenommenen Fensters).</summary>
    public static void RecordWatermark(DateTimeOffset watermarkUtc) => Volatile.Write(ref WatermarkTicks, watermarkUtc.UtcTicks);

    public static void SseOpened() => Interlocked.Increment(ref SseConnections);

    public static void SseClosed() => Interlocked.Decrement(ref SseConnections);

    /// <summary>Zählerstände seit Prozessstart für die Report-Seite (die Meter selbst sind nur für den Export lesbar).</summary>
    public static long QuotaDenialsSinceStart => Interlocked.Read(ref QuotaDenialsTotal);

    public static long IngestedRecordsSinceStart => Interlocked.Read(ref IngestedTotal);

    public static int OpenSseConnections => Volatile.Read(ref SseConnections);

    /// <summary>Rückstand der Aufnahme in Sekunden (Simulationsuhr minus Wasserstand); <see langword="null"/>, solange beides fehlt.</summary>
    public static double? CollectorLagSeconds => ObserveLag().Select(m => (double?)m.Value).FirstOrDefault();

    private static IEnumerable<Measurement<double>> ObserveLag()
    {
        var clock = Volatile.Read(ref Clock);
        var watermark = Volatile.Read(ref WatermarkTicks);
        if (clock is null || watermark == 0) yield break;

        yield return new Measurement<double>(Math.Max(0, (clock().UtcTicks - watermark) / (double)TimeSpan.TicksPerSecond));
    }
}
