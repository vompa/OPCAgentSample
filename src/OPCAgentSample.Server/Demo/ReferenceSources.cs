namespace OPCAgentSample.Server.Demo;

// REFERENZ-/VORSCHAU-IMPLEMENTIERUNGEN der Fachinhalte (Daten aus dem Mockup, Stand Do 15.10.2026 09:42).

/// <summary>Rollenprofile aus den Referenzdaten.</summary>
public sealed class ReferenceRoleProfileSource : IRoleProfileSource
{
    private static readonly Lazy<ProfilesFile> s_file = new(() => ReferenceDataFiles.Read<ProfilesFile>("profiles.json"));

    public IReadOnlyList<string> RoleIds => [.. s_file.Value.Profiles.Keys];

    public RoleProfile? Find(string roleId) => s_file.Value.Profiles.GetValueOrDefault(roleId);

    private sealed record ProfilesFile(IReadOnlyDictionary<string, RoleProfile> Profiles);
}

/// <summary>Verbindungsstatus der Referenz-Implementierung: OPC UA verbunden, 17 Tools.</summary>
public sealed class ReferencePlantStatusSource : IPlantStatusSource
{
    private const int ReferenceToolCount = 17;

    public Task<PlantStatus> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PlantStatus(OpcConnected: true, ReferenceToolCount));
}
