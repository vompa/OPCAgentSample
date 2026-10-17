using OPCAgentSample.Snapshot;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Snapshot;

/// <summary>
/// Die eingecheckte Momentaufnahme (Vorschau-Daten der Oberfläche, <c>src/OPCAgentSample.Web/public/snapshot</c>) muss dem
/// aktuellen Code entsprechen: Die Tests erzeugen sie mit denselben Funktionen wie das Werkzeug <c>OPCAgentSample.Snapshot</c>
/// (Szenario-Historie in Redis, HTTP-API) und vergleichen Datei für Datei.
/// </summary>
[Collection(InfrastructureFixture.Name)]
public sealed class SnapshotTests(InfrastructureFixture infrastructure)
{
    private const int MaxTotalKilobytes = 150;

    private const string RegenerateHint =
        "Die Momentaufnahme ist veraltet. Neu erzeugen (Redis nötig, z. B. docker compose up -d redis):\n"
        + "  dotnet run --project src/OPCAgentSample.Snapshot -- src/OPCAgentSample.Web/public/snapshot\n"
        + "und die geänderten Dateien einchecken.";

    private static Task<IReadOnlyDictionary<string, string>>? s_generated;

    /// <summary>Die Momentaufnahme wird einmal je Testlauf erzeugt (Aufnahme der Historie in Datenbank 15 des Test-Redis).</summary>
    private Task<IReadOnlyDictionary<string, string>> BuildAsync() =>
        s_generated ??= SnapshotTool.BuildAsync(infrastructure.RedisConnectionString);

    private static string SnapshotDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OPCAgentSample.slnx")))
                return Path.Combine(directory.FullName, "src", "OPCAgentSample.Web", "public", "snapshot");
        }

        throw new DirectoryNotFoundException("OPCAgentSample.slnx nicht gefunden.");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public async Task Checked_in_snapshot_matches_the_current_code()
    {
        var generated = await BuildAsync();
        var directory = SnapshotDirectory();

        var checkedIn = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Select(Path.GetFileName).Order().ToList() : [];
        Assert.True(generated.Keys.Order().SequenceEqual(checkedIn!), $"Dateien weichen ab (erwartet: {string.Join(", ", generated.Keys)}). {RegenerateHint}");
        foreach (var (name, content) in generated)
        {
            var actual = Normalize(await File.ReadAllTextAsync(Path.Combine(directory, name)));
            Assert.True(actual == content, $"{name} entspricht nicht dem aktuellen Code. {RegenerateHint}");
        }
    }

    [Fact]
    public async Task Snapshot_stays_small_and_covers_every_role_with_prepared_answers()
    {
        var generated = await BuildAsync();

        Assert.True(generated.Values.Sum(c => c.Length) / 1024.0 < MaxTotalKilobytes, "Die Momentaufnahme soll unter 150 KB bleiben.");
        Assert.Contains(SnapshotBuilder.FileOf("shift_lead"), generated.Keys);
        Assert.Contains(SnapshotBuilder.FileOf("hall_lead"), generated.Keys);
        Assert.Contains(SnapshotBuilder.FileOf("production_lead"), generated.Keys);
        Assert.Contains(SnapshotBuilder.ReportsFile, generated.Keys);
        foreach (var role in new[] { "shift_lead", "hall_lead", "production_lead" })
        {
            using var document = System.Text.Json.JsonDocument.Parse(generated[SnapshotBuilder.FileOf(role)]);
            Assert.Equal(5, document.RootElement.GetProperty("answers").EnumerateObject().Count());
            Assert.Equal(1, document.RootElement.GetProperty("history").GetProperty("turns").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("shift_lead", "shift")]
    [InlineData("hall_lead", "week")]
    [InlineData("production_lead", "fourWeeks")]
    public async Task Snapshot_holds_the_machine_report_of_every_role_in_its_default_range_at_0942(string role, string range)
    {
        var generated = await BuildAsync();

        using var document = System.Text.Json.JsonDocument.Parse(generated[SnapshotBuilder.MachinesFileOf(role)]);
        var report = document.RootElement;
        Assert.Equal(role, report.GetProperty("role").GetString());
        Assert.Equal(range, report.GetProperty("range").GetString());
        Assert.Equal("Do 15.10. 09:42", report.GetProperty("dataAsOf").GetString());
        Assert.Equal(8, report.GetProperty("stations").GetArrayLength());
    }
}
