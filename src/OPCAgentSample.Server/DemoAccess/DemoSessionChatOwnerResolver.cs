using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Features.ChatHistory;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.DemoAccess;

/// <summary>
/// Besitzer des Verlaufs in der Live-Demo: je Browser-Sitzung <em>und</em> Rolle. Die Benutzer sind fiktiv und für alle
/// Besucher gleich; so sehen Besucher nie die Fragen anderer. Kontingent und Live-Verbindungsgrenze hängen am
/// <see cref="ChatOwner.Key"/> und gelten damit ebenfalls je Sitzung und Rolle. Ohne eingeschalteten Zugang
/// (<c>Demo:Enabled</c>, lokaler Betrieb) bleibt der Benutzer der Rolle der Besitzer.
/// </summary>
public sealed class DemoSessionChatOwnerResolver(DemoSessionReader reader, IOptions<DemoOptions> options) : IChatOwnerResolver
{
    /// <summary>Wird nur ohne Sitzung benutzt; die Middleware lässt solche Anfragen auf der API nie durch.</summary>
    internal const string NoSession = "ohne-sitzung";

    public ChatOwner Resolve(HttpContext httpContext, AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!options.Value.Enabled) return ChatOwner.ForUser(user.Id);

        var sessionId = reader.Read(httpContext)?.Id ?? NoSession;
        return ChatOwner.ForBrowserToken($"{sessionId}:{user.Id}");
    }
}
