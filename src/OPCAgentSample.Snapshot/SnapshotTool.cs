using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OPCAgentSample.Domain;
using OPCAgentSample.Server;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Ingestion;
using StackExchange.Redis;

namespace OPCAgentSample.Snapshot;

/// <summary>
/// Kommandozeile: <c>dotnet run --project src/OPCAgentSample.Snapshot -- &lt;Zielordner&gt; [--redis &lt;Verbindung&gt;]</c>.
/// Braucht einen erreichbaren Redis (Datenbank 15, wird geleert); Standard ist der lokale Container aus <c>docker-compose.yml</c>.
/// Nimmt die Szenario-Historie auf, startet den Server in-process (Umgebung „Testing“, Referenz-Engine, Demo-Zugang aus) und
/// schreibt die Dateien von <see cref="SnapshotBuilder"/>.
/// </summary>
public static class SnapshotTool
{
    private const string DefaultRedis = "localhost:6379";
    private const int RedisDatabase = 15;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            await Console.Error.WriteLineAsync("Aufruf: OPCAgentSample.Snapshot <Zielordner> [--redis <Verbindung>]");
            return 2;
        }

        var target = Path.GetFullPath(args[0]);
        var files = await BuildAsync(OptionOf(args, "--redis") ?? DefaultRedis);
        Directory.CreateDirectory(target);
        foreach (var stale in Directory.GetFiles(target, "*.json")) File.Delete(stale);
        foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(target, name), content);
        Console.WriteLine($"{files.Count} Dateien, {files.Values.Sum(c => c.Length) / 1024.0:F1} KB nach {target}");
        return 0;
    }

    /// <summary>Erzeugt die Dateien gegen Redis; die Aufnahme der Historie dauert einige Sekunden.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> BuildAsync(string redis)
    {
        await FlushRedisAsync(redis);
        await using var app = ServerHost.Build(
            new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory },
            configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = $"{redis},defaultDatabase={RedisDatabase}",
                ["Opc:Enabled"] = "false",
                ["Demo:Enabled"] = "false",
                ["Web:ServeUi"] = "false",
                ["Demo:StreamDelay"] = "00:00:00",
                ["Claude:ApiKey"] = string.Empty,
                ["Observability:OtlpEnabled"] = "false",
                ["Redis:CommandTimeout"] = "00:00:02",
                ["urls"] = "http://127.0.0.1:0",
                ["Logging:LogLevel:Default"] = "Warning",
            }));

        var source = GeneratedHistorySource.CreateDefault();
        await app.StartAsync();
        try
        {
            await app.Services.GetRequiredService<HistoryIngestor>().IngestAsync(source, app.Services.GetRequiredService<IDemoClock>().UtcNow);
            var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            return await SnapshotBuilder.BuildAsync(client);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static string? OptionOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task FlushRedisAsync(string redis)
    {
        var options = ConfigurationOptions.Parse(redis);
        options.AllowAdmin = true;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(options);
        foreach (var endpoint in connection.GetEndPoints()) await connection.GetServer(endpoint).FlushDatabaseAsync(RedisDatabase);
    }
}
