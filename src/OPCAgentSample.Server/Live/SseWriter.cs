using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace OPCAgentSample.Server.Live;

/// <summary>
/// Schreibt Server-Sent Events (UTF-8) und leert den Puffer nach jedem Eintrag. Format eines Ereignisses:
/// <c>id: …</c> (nur <c>feed</c>), <c>event: name</c>, <c>data: json</c>, Leerzeile; Kommentare beginnen mit <c>:</c>.
/// Eine Verbindung schreibt aus einer einzigen Schleife, daher braucht der Writer keine Sperre.
/// </summary>
public sealed class SseWriter(HttpResponse response)
{
    private long _lastWrite = Stopwatch.GetTimestamp();

    /// <summary>Zeit seit dem letzten Schreiben (für den Heartbeat).</summary>
    public TimeSpan IdleTime => Stopwatch.GetElapsedTime(_lastWrite);

    public Task WriteRetryAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        WriteAsync(string.Create(CultureInfo.InvariantCulture, $"retry: {(long)delay.TotalMilliseconds}\n\n"), cancellationToken);

    public Task WriteCommentAsync(string comment, CancellationToken cancellationToken) =>
        WriteAsync($": {comment}\n\n", cancellationToken);

    public Task WriteEventAsync(string name, string json, string? id, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        if (id is not null) _ = text.Append("id: ").Append(id).Append('\n');
        _ = text.Append("event: ").Append(name).Append('\n').Append("data: ").Append(json).Append("\n\n");
        return WriteAsync(text.ToString(), cancellationToken);
    }

    private async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        await response.WriteAsync(text, Encoding.UTF8, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        _lastWrite = Stopwatch.GetTimestamp();
    }
}
