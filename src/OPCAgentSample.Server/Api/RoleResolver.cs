using OPCAgentSample.Server.Features.ChatHistory;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Api;

/// <summary>Rolle, Benutzer und Besitzer des Verlaufs/der Favoriten für eine Anfrage.</summary>
public sealed record RoleContext(AppRole Role, AppUser User, ChatOwner Owner);

/// <summary>
/// Ermittelt den Besitzer des Verlaufs und der Favoriten. Lokal ist das der Benutzer der gewählten Rolle
/// (<see cref="LocalUserChatOwnerResolver"/>); die Live-Demo ersetzt die Komponente durch Zugangscode bzw. Browser-Token.
/// </summary>
public interface IChatOwnerResolver
{
    ChatOwner Resolve(HttpContext httpContext, AppUser user);
}

public sealed class LocalUserChatOwnerResolver : IChatOwnerResolver
{
    public ChatOwner Resolve(HttpContext httpContext, AppUser user) => ChatOwner.ForUser(user.Id);
}

/// <summary>Löst den Parameter <c>role</c> der API auf (Rollenkennung aus den Stammdaten, Standard Schichtleiter).</summary>
public sealed class RoleResolver(MasterDataStore masterData, IChatOwnerResolver ownerResolver)
{
    public const string DefaultRoleId = "shift_lead";

    public async Task<RoleContext?> ResolveAsync(string? roleId, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var id = string.IsNullOrWhiteSpace(roleId) ? DefaultRoleId : roleId.Trim();
        var data = await masterData.GetAsync(cancellationToken);
        if (!data.Roles.TryGetValue(id, out var role)) return null;

        var user = data.Users.Where(u => u.RoleId == id).OrderBy(u => u.Id, StringComparer.Ordinal).FirstOrDefault();
        return user is null ? null : new RoleContext(role, user, ownerResolver.Resolve(httpContext, user));
    }

    public async Task<IReadOnlyList<AppRole>> ListRolesAsync(CancellationToken cancellationToken) =>
        [.. (await masterData.GetAsync(cancellationToken)).Roles.Values];
}

internal static class ApiResults
{
    public static IResult UnknownRole(string? roleId) =>
        Results.Problem(
            title: "Unbekannte Rolle",
            detail: $"Die Rolle '{roleId}' gibt es nicht.",
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Code im Problem-JSON (<c>code</c>), auf den die Oberfläche gezielt reagiert.</summary>
    public const string SessionNotFoundCode = "session-not-found";

    /// <summary>Die angegebene Sitzung gehört nicht (mehr) diesem Besitzer, etwa nach einer neuen Anmeldung oder einem Rollenwechsel.</summary>
    public static IResult SessionNotFound() =>
        Results.Problem(
            title: "Sitzung nicht gefunden",
            detail: "Die Sitzung gehört nicht zu dieser Anmeldung. Der Verlauf wird neu geladen.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = SessionNotFoundCode });

    public static IResult Invalid(string detail) =>
        Results.Problem(title: "Ungültige Anfrage", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    public static async Task<IResult> WithRoleAsync(
        this RoleResolver resolver, string? roleId, HttpContext httpContext, Func<RoleContext, Task<IResult>> handler)
    {
        var context = await resolver.ResolveAsync(roleId, httpContext, httpContext.RequestAborted);
        return context is null ? UnknownRole(roleId) : await handler(context);
    }
}
