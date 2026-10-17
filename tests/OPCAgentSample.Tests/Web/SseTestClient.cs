using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OPCAgentSample.Tests.Web;

/// <summary>Ein Eintrag eines Server-Sent-Events-Stroms: Ereignis (Name, Id, Daten), Kommentar oder <c>retry</c>.</summary>
public sealed record SseFrame(string? Event, string? Id, string? Data, string? Comment, int? Retry)
{
    public JsonElement Json => JsonDocument.Parse(Data ?? throw new InvalidOperationException("Kein data-Feld.")).RootElement.Clone();
}

/// <summary>
/// Liest einen SSE-Strom im Hintergrund und sammelt die Einträge. Der Test wartet mit <see cref="WaitForAsync"/> auf einen
/// sichtbaren Eintrag (großzügige Zeitgrenze) und liest danach <see cref="Frames"/>.
/// </summary>
public sealed class SseTestClient : IAsyncDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly HttpResponseMessage _response;
    private readonly List<SseFrame> _frames = [];
    private Task _reader = Task.CompletedTask;

    private SseTestClient(HttpResponseMessage response) => _response = response;

    public HttpStatusCode StatusCode => _response.StatusCode;

    public string? ContentType => _response.Content.Headers.ContentType?.MediaType;

    public string CacheControl => _response.Headers.CacheControl?.ToString() ?? string.Empty;

    /// <summary>Momentaufnahme der bisher gelesenen Einträge in Eingangsreihenfolge.</summary>
    public IReadOnlyList<SseFrame> Frames
    {
        get
        {
            lock (_frames) return [.. _frames];
        }
    }

    public static async Task<SseTestClient> ConnectAsync(HttpClient http, string path, string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var client = new SseTestClient(response);
        if (response.IsSuccessStatusCode) client._reader = Task.Run(() => client.ReadAsync(client._cancellation.Token));
        return client;
    }

    /// <summary>Wartet auf den <paramref name="skip"/>+1-ten Eintrag, der die Bedingung erfüllt, und liefert ihn.</summary>
    public async Task<SseFrame> WaitForAsync(Func<SseFrame, bool> predicate, int skip = 0)
    {
        using var timeout = new CancellationTokenSource(s_timeout);
        while (true)
        {
            var match = Frames.Where(predicate).Skip(skip).FirstOrDefault();
            if (match is not null) return match;

            if (timeout.IsCancellationRequested)
                throw new TimeoutException($"Kein passender SSE-Eintrag. Bisher: {string.Join(" | ", Frames.Select(Describe))}");

            await Task.Delay(25);
        }
    }

    private static string Describe(SseFrame frame) =>
        frame.Comment is not null ? $":{frame.Comment}" : frame.Retry is not null ? $"retry {frame.Retry}" : $"{frame.Event} {frame.Id}";

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            string? name = null;
            string? id = null;
            var data = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    if (name is not null) Add(new SseFrame(name, id, string.Join('\n', data), null, null));
                    (name, id) = (null, null);
                    data.Clear();
                }
                else if (line.StartsWith(':')) Add(new SseFrame(null, null, null, line[1..].Trim(), null));
                else if (line.StartsWith("retry:", StringComparison.Ordinal))
                    Add(new SseFrame(null, null, null, null, int.Parse(line[6..].Trim(), CultureInfo.InvariantCulture)));
                else if (line.StartsWith("id:", StringComparison.Ordinal)) id = line[3..].Trim();
                else if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].TrimStart());
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException or InvalidOperationException)
        {
            // Test beendet die Verbindung.
        }
    }

    private void Add(SseFrame frame)
    {
        lock (_frames) _frames.Add(frame);
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _response.Dispose();
        await _reader;
        _cancellation.Dispose();
    }
}
