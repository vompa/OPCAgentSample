using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Features.ChatHistory;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// Verlauf wie Claude Code (Spezifikation Abschnitt 16) über <see cref="ChatHistoryService"/>:
/// <c>GET /api/history/latest</c>, <c>GET /api/history/sessions</c>, <c>GET /api/history/sessions/{id}</c>,
/// <c>POST /api/history/sessions</c> (Neue Sitzung), <c>GET /api/history/recent-questions</c>.
/// </summary>
public static class HistoryEndpoints
{
    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/history");

        group.MapGet("/latest", (string? role, HttpContext http, RoleResolver resolver, ChatHistoryService history,
                ISampleHistorySource samples, IDemoClock clock) =>
            resolver.WithRoleAsync(role, http, async context =>
                Results.Ok(await LoadLatestAsync(context, history, samples, clock, http.RequestAborted))));

        group.MapGet("/sessions", (string? role, HttpContext http, RoleResolver resolver, ChatHistoryService history) =>
            resolver.WithRoleAsync(role, http, async context =>
                Results.Ok(await history.ListSessionsAsync(context.Owner, cancellationToken: http.RequestAborted))));

        group.MapGet("/sessions/{id:guid}", (Guid id, string? role, HttpContext http, RoleResolver resolver, ChatHistoryService history) =>
            resolver.WithRoleAsync(role, http, async context =>
                await history.GetSessionAsync(context.Owner, id, http.RequestAborted) is { } package
                    ? Results.Ok(package)
                    : Results.NotFound()));

        group.MapPost("/sessions", (string? role, HttpContext http, RoleResolver resolver, ChatHistoryService history) =>
            resolver.WithRoleAsync(role, http, async context =>
                Results.Ok(await history.StartSessionAsync(context.Owner, context.Role.Id, http.RequestAborted))));

        group.MapGet("/recent-questions", (string? role, HttpContext http, RoleResolver resolver, ChatHistoryService history) =>
            resolver.WithRoleAsync(role, http, async context =>
                Results.Ok(await history.GetRecentQuestionsAsync(context.Owner, http.RequestAborted))));
        return endpoints;
    }

    /// <summary>
    /// Letzte Sitzung. Beim allerersten Besuch (noch keine Sitzung) wird ein vorbelegter Beispielverlauf der Rolle
    /// als gespeicherte Frage angelegt, damit die Oberfläche nicht leer beginnt.
    /// </summary>
    private static async Task<ChatSessionPackage> LoadLatestAsync(
        RoleContext context, ChatHistoryService history, ISampleHistorySource samples, IDemoClock clock,
        CancellationToken cancellationToken)
    {
        var latest = await history.GetLatestSessionAsync(context.Owner, cancellationToken);
        if (latest is not null) return latest;

        var session = await history.StartSessionAsync(context.Owner, context.Role.Id, cancellationToken);
        if (samples.GetSample(context.Role.Id) is { } sample)
        {
            await history.AppendTurnAsync(context.Owner, session.Id, AskEndpoint.ToNewTurn(sample, clock.UtcNow), cancellationToken);
            return await history.GetLatestSessionAsync(context.Owner, cancellationToken) ?? new ChatSessionPackage(session, []);
        }

        return new ChatSessionPackage(session, []);
    }
}
