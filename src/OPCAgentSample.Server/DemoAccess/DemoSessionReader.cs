using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.DemoAccess;

/// <summary>Liest die Sitzung einer Anfrage: Cookie <c>demo_session</c> oder <c>Authorization: Bearer</c> (gleiches Format).</summary>
public sealed class DemoSessionReader(IOptions<DemoOptions> options, TimeProvider clock)
{
    private const string BearerPrefix = "Bearer ";
    private const string ItemKey = "OPCAgentSample.DemoSession";

    /// <summary>Die gültige Sitzung der Anfrage oder <see langword="null"/>; das Ergebnis wird je Anfrage gemerkt.</summary>
    public DemoSessionInfo? Read(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(ItemKey, out var cached)) return cached as DemoSessionInfo;

        var session = Validate(context.Request);
        context.Items[ItemKey] = session;
        return session;
    }

    private DemoSessionInfo? Validate(HttpRequest request)
    {
        var key = options.Value.CookieKey;
        var now = clock.GetUtcNow();

        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            && DemoSession.Validate(key, header[BearerPrefix.Length..].Trim(), now) is { } bearer)
        {
            return bearer;
        }

        return DemoSession.Validate(key, request.Cookies[DemoSession.CookieName], now);
    }
}
