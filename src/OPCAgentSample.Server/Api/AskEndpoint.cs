using System.Diagnostics;
using System.Text.Json;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Features.ChatHistory;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// <c>POST /api/ask</c> <c>{ role, question, sessionId?, fresh?, report? }</c> (<c>report</c> = <c>{ kind, range, findingId }</c>: die Frage stammt
/// aus der Auffälligkeit eines Reports, den Kontext dazu berechnet der Server) antwortet als Server-Sent Events:
/// <c>tool_call</c> (je Aufruf: tool, parameters, durationMs, source, dataSource), <c>answer_draft</c> (<c>{ headline, todo }</c>:
/// Anfang der Karte, noch während Claude schreibt; nur Anzeige, ersetzt durch <c>answer</c>), <c>answer</c> (geprüfte Antwortkarte),
/// <c>done</c> (Token mit Cache-Anteilen, Datenstand, Sitzung, <c>repeat</c> bei einer gespeicherten Antwort auf dieselbe Frage). Erst danach wird der Turn über <see cref="ChatHistoryService"/> gespeichert.
/// </summary>
public static class AskEndpoint
{
    public const int MaxQuestionLength = 500;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAskEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/ask", (AskBody? body, HttpContext http, RoleResolver resolver, IAnswerEngine engine,
                ChatHistoryService history, IDemoClock clock, AuditLog audit, ReportAskContextResolver reports) =>
            resolver.WithRoleAsync(body?.Role, http, context =>
                AskAsync(body!, context, http, engine, history, clock, audit, reports)));
        return endpoints;
    }

    internal static NewChatTurn ToNewTurn(SampleTurn sample, DateTimeOffset dataAsOf) =>
        new(sample.Question, JsonSerializer.SerializeToElement(sample.Card, s_json),
            JsonSerializer.SerializeToElement(sample.ToolCalls, s_json), dataAsOf);

    private static async Task<IResult> AskAsync(
        AskBody body, RoleContext context, HttpContext http, IAnswerEngine engine,
        ChatHistoryService history, IDemoClock clock, AuditLog audit, ReportAskContextResolver reports)
    {
        var question = body.Question?.Trim();
        if (string.IsNullOrEmpty(question)) return ApiResults.Invalid("Die Frage fehlt.");
        if (question.Length > MaxQuestionLength) return ApiResults.Invalid($"Die Frage ist länger als {MaxQuestionLength} Zeichen.");

        if (body.Report?.Validate() is { } invalidReport) return ApiResults.Invalid(invalidReport);

        var sessionId = await ResolveSessionAsync(body.SessionId, context, history, http.RequestAborted);
        if (sessionId is null) return ApiResults.SessionNotFound(); // nie in eine fremde Sitzung schreiben

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var reportContext = body.Report is null ? null : await reports.ResolveAsync(body.Report, http.RequestAborted);
        await http.Response.StartAsync(http.RequestAborted);

        var stopwatch = Stopwatch.StartNew();
        var toolCalls = new List<ToolCall>();
        AnswerReadyStep? ready = null;
        await foreach (var step in engine.AskAsync(new AskRequest(context.Role.Id, question, context.Owner.Key, body.Fresh, reportContext), http.RequestAborted))
        {
            switch (step)
            {
                case ToolCallStep toolStep:
                    toolCalls.Add(toolStep.Call);
                    await WriteEventAsync(http, "tool_call", toolStep.Call);
                    break;
                case AnswerDraftStep draftStep:
                    await WriteEventAsync(http, "answer_draft", new { headline = draftStep.Headline, todo = draftStep.Todo });
                    break;
                case AnswerReadyStep answerStep:
                    ready = answerStep;
                    await WriteEventAsync(http, "answer", answerStep.Card);
                    break;
            }
        }

        if (ready is null)
        {
            await WriteEventAsync(http, "error", new { message = "Die Antwort-Engine hat keine Antwortkarte geliefert." });
            return Results.Empty;
        }

        // Datenstand und Speichern erst nach der Antwort; der Turn bleibt auch erhalten, wenn der Client schon fort ist.
        var dataAsOf = ready.DataAsOfUtc ?? clock.UtcNow;
        var turn = new NewChatTurn(
            question, JsonSerializer.SerializeToElement(ready.Card, s_json), JsonSerializer.SerializeToElement(toolCalls, s_json),
            dataAsOf, ready.InputTokens, ready.OutputTokens, ready.CacheReadTokens, ready.CacheWriteTokens,
            (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 1, int.MaxValue), // mindestens 1 ms, denn 0 heißt „nicht erfasst“
            ready.Repeat);
        var saved = await history.AppendTurnAsync(context.Owner, sessionId.Value, turn, CancellationToken.None);
        await audit.RecordAsync(AuditChannels.Rest, ApiEndpoints.Principal(context), ready.Repeat ? "ask.repeat" : "ask", question,
            AuditOutcomes.Ok, cancellationToken: CancellationToken.None);

        await WriteEventAsync(http, "done", new AskDone(
            sessionId.Value, saved.Sequence, new TokenUsage(ready.InputTokens, ready.OutputTokens, ready.CacheReadTokens, ready.CacheWriteTokens), dataAsOf, DemoTime.TimeText(dataAsOf), ready.Repeat));
        return Results.Empty;
    }

    /// <summary>Ohne Angabe die letzte Sitzung des Besitzers (oder eine neue); mit Angabe nur eine eigene Sitzung.</summary>
    private static async Task<Guid?> ResolveSessionAsync(
        Guid? requested, RoleContext context, ChatHistoryService history, CancellationToken cancellationToken)
    {
        if (requested is { } id)
            return await history.GetSessionAsync(context.Owner, id, cancellationToken) is null ? null : id;

        var package = await history.GetOrStartLatestSessionAsync(context.Owner, context.Role.Id, cancellationToken);
        return package.Session.Id;
    }

    private static async Task WriteEventAsync<T>(HttpContext http, string name, T data)
    {
        var json = JsonSerializer.Serialize(data, s_json);
        await http.Response.WriteAsync($"event: {name}\ndata: {json}\n\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }
}
