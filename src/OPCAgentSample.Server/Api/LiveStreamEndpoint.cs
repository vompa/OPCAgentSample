using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Live;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// <c>GET /api/live?role=</c>: Live-Status als Server-Sent Events (Ereignisse <c>station</c>, <c>alarm</c>, <c>feed</c>,
/// <c>inbox</c>, <c>clock</c>, Format siehe <see cref="LiveStreamService"/>). Die Id der <c>feed</c>-Ereignisse ist die
/// Redis-Stream-Id; ein Wiederverbinden mit dem Header <c>Last-Event-ID</c> (oder dem Parameter <c>lastEventId</c>, wenn der Client
/// selbst neu verbindet) liefert die verpassten Meldungen nach. Je Besitzer sind nur wenige Verbindungen erlaubt (sonst 429).
/// </summary>
public static class LiveStreamEndpoint
{
    public const string LastEventIdHeader = "Last-Event-ID";

    public static IEndpointRouteBuilder MapLiveStreamEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/live", (string? role, string? lastEventId, HttpContext http, RoleResolver resolver,
                LiveHub hub, LiveStreamService stream, IOptions<LiveOptions> options) =>
            resolver.WithRoleAsync(role, http, context =>
                StreamAsync(context, lastEventId, http, hub, stream, options.Value)));
        return endpoints;
    }

    private static async Task<IResult> StreamAsync(
        RoleContext context, string? lastEventIdParameter, HttpContext http, LiveHub hub, LiveStreamService stream, LiveOptions options)
    {
        using var subscription = hub.TryRegister(context.Owner.Key, options.MaxConnectionsPerOwner);
        if (subscription is null)
        {
            return Results.Problem(
                title: "Zu viele Live-Verbindungen",
                detail: $"Pro Benutzer sind höchstens {options.MaxConnectionsPerOwner} gleichzeitige Live-Verbindungen erlaubt.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var response = http.Response;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // nginx: Antwort nicht puffern
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(http.RequestAborted);

        var lastEventId = http.Request.Headers[LastEventIdHeader].FirstOrDefault() ?? lastEventIdParameter;
        try
        {
            await stream.RunAsync(context, lastEventId, subscription, new SseWriter(response), http.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Der Client hat die Verbindung beendet (Normalfall).
        }
        catch (IOException)
        {
            // Verbindung abgebrochen.
        }

        return Results.Empty;
    }
}
