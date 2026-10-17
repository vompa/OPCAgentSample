using OPCAgentSample.Server.Infrastructure.Redis;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Health;

/// <summary>
/// <c>GET /api/health</c>, anonym, ohne Details nach außen. 200 <c>healthy</c>: Redis antwortet und die Stammdaten stehen darin.
/// 503 <c>unhealthy</c>: Redis fehlt oder ist leer (der Server baut sich dann beim nächsten Aufnahmeschritt aus dem Seed neu auf).
/// </summary>
public static class HealthEndpoint
{
    public const string Route = "/api/health";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(3);

    public static IEndpointRouteBuilder MapHealthEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, CheckAsync).AllowAnonymous().WithName("Health");
        return endpoints;
    }

    private static async Task<IResult> CheckAsync(RedisAccess redis, MasterDataStore masterData, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_timeout);

        var up = await redis.IsAvailableAsync() && await redis.TryAsync(_ => masterData.IsSeededAsync(), false, "health seed");
        return up
            ? Results.Json(new HealthStatus("healthy"))
            : Results.Json(new HealthStatus("unhealthy"), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

/// <summary>Antwort des Health-Endpunkts: nur ein Wort.</summary>
public sealed record HealthStatus(string Status);
