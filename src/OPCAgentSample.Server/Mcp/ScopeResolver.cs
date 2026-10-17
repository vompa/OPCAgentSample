using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Configuration;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Aufgelöster Bereich eines Tools: Stationen der Halle A (inklusive Puffer) und die Zählerstationen der Hallen B/C
/// (<c>HALLE-B</c>, <c>HALLE-C</c>), die nur Tageskennzahlen liefern.
/// </summary>
public sealed record ResolvedScope(string Label, IReadOnlyList<Station> Stations, IReadOnlyList<string> CounterStations, bool IsWholePlant)
{
    public IEnumerable<string> HallAIds => Stations.Select(s => s.Id);

    /// <summary>Stationen der Halle-OEE: Pressen und Verpackung (Öfen werden über die Belegung bewertet).</summary>
    public IEnumerable<string> OeeStationIds =>
        Stations.Where(s => s.Area is Area.Pressing or Area.Packaging).Select(s => s.Id).Concat(CounterStations);

    public IEnumerable<string> AllKpiStationIds => Stations.Where(s => s.Area != Area.Buffer).Select(s => s.Id).Concat(CounterStations);

    public bool IsSingleStation => Stations.Count == 1 && CounterStations.Count == 0;

    public bool HasHallA => Stations.Count > 0;
}

/// <summary>
/// Löst den Parameter <c>scope</c>/<c>machineId</c> der Tools auf (Halle, Werk, Bereich, Station, Liste) und prüft ihn
/// gegen die Rolle des Clients: Schichtleiter und Hallenleiterin sehen Halle A, der Produktionsleiter das Werk.
/// </summary>
public sealed partial class ScopeResolver(McpCaller caller, IOptions<PlantOptions> plant)
{
    private const string ValidScopes =
        "Halle A, Werk, Halle B, Halle C, einen Bereich (Pressen, Öfen, Verpackung, Puffer) oder eine Station (RT-01 bis RT-04, "
        + "OF-01, OF-02, VP-01, VP-02, PU-OFEN)";

    public Task<ResolvedScope> ResolveAsync(string? scope, CancellationToken cancellationToken, string? defaultScope = null) =>
        ResolveAsync(string.IsNullOrWhiteSpace(scope) ? null : JsonSerializer.SerializeToElement(scope), cancellationToken, defaultScope);

    public async Task<ResolvedScope> ResolveAsync(JsonElement? scope, CancellationToken cancellationToken, string? defaultScope = null)
    {
        var wholePlant = await caller.SeesWholePlantAsync(cancellationToken);
        var tokens = Tokens(scope);
        if (tokens.Count == 0) tokens = [defaultScope ?? (wholePlant ? "Werk" : $"Halle {plant.Value.HallId}")];

        var stations = new List<Station>();
        var counters = new List<string>();
        var labels = new List<string>();
        var plantWide = false;
        foreach (var token in tokens)
        {
            var resolved = ResolveToken(token);
            if (resolved.NeedsPlant && !wholePlant)
            {
                var title = (await caller.GetRoleAsync(cancellationToken)).Title;
                throw caller.Deny(
                    $"Keine Berechtigung: Die Rolle {title} sieht nur Halle A. '{token}' liegt außerhalb. "
                    + "Frage nach Halle A, einem Bereich oder einer Station der Halle A.");
            }

            plantWide |= resolved.IsPlant;
            labels.Add(resolved.Label);
            foreach (var station in resolved.Stations.Where(s => stations.All(known => known.Id != s.Id))) stations.Add(station);
            foreach (var counter in resolved.Counters.Where(c => !counters.Contains(c, StringComparer.Ordinal))) counters.Add(counter);
        }

        var label = plantWide ? "Werk" : string.Join(", ", labels.Distinct(StringComparer.Ordinal));
        return new ResolvedScope(label, stations, counters, plantWide);
    }

    /// <summary>Filter nach Stationstyp (Parameter <c>stationType</c>): Rundtisch, Presse, Ofen, Verpackung, Puffer.</summary>
    public static Area? ParseArea(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Normalize(text) switch
        {
            "rundtisch" or "rundtische" or "presse" or "pressen" or "rt" => Area.Pressing,
            "ofen" or "oefen" or "aushaerteofen" or "of" => Area.Curing,
            "verpackung" or "verpackungen" or "vp" => Area.Packaging,
            "puffer" or "pu-ofen" => Area.Buffer,
            _ => throw new McpException($"Unbekannter Stationstyp '{text}'. Gültig: Rundtisch, Ofen, Verpackung, Puffer."),
        };
    }

    private (string Label, IReadOnlyList<Station> Stations, IReadOnlyList<string> Counters, bool NeedsPlant, bool IsPlant) ResolveToken(string token)
    {
        var hall = plant.Value.HallId;
        var normalized = Normalize(token);
        var hallA = PlantLayout.HallAStations;
        switch (normalized)
        {
            case "werk" or "plant" or "gesamt" or "alle hallen" or "alle":
                return ("Werk", hallA, [.. plant.Value.CounterStations.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value)], true, true);
            case "halle a" or "a" or "hallea":
                return ($"Halle {hall}", hallA, [], false, false);
            case "halle b" or "b" or "halleb":
                return ("Halle B", [], [plant.Value.CounterStations["B"]], true, false);
            case "halle c" or "c" or "hallec":
                return ("Halle C", [], [plant.Value.CounterStations["C"]], true, false);
            default:
                break;
        }

        if (AreaOf(normalized) is { } area)
        {
            var areaStations = hallA.Where(s => s.Area == area).ToList();
            return (AreaLabel(area), areaStations, [], false, false);
        }

        var id = StationId(normalized);
        var station = id is null ? null : hallA.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (station is null) throw new McpException($"Unbekannter Bereich '{token}'. Gültig: {ValidScopes}.");

        return (station.Id, [station], [], false, false);
    }

    private static Area? AreaOf(string normalized) => normalized switch
    {
        "pressen" or "rundtische" or "presse" => Area.Pressing,
        "oefen" or "ofen" or "aushaerteoefen" => Area.Curing,
        "verpackung" or "verpackungen" => Area.Packaging,
        "puffer" => Area.Buffer,
        _ => null,
    };

    private static string AreaLabel(Area area) => area switch
    {
        Area.Pressing => "Pressen",
        Area.Curing => "Öfen",
        Area.Packaging => "Verpackung",
        _ => "Puffer",
    };

    /// <summary>Stationskennung aus einer Schreibweise wie <c>rt-04</c>, <c>RT04</c> oder <c>Rundtisch 4</c>.</summary>
    private static string? StationId(string normalized)
    {
        if (normalized == "pu-ofen" || normalized == "puofen") return "PU-OFEN";
        var match = StationPattern().Match(normalized);
        if (!match.Success) return null;

        var prefix = match.Groups["kind"].Value switch
        {
            "rundtisch" or "rt" => "RT",
            "ofen" or "of" => "OF",
            _ => "VP",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}-{int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture):00}");
    }

    private static List<string> Tokens(JsonElement? scope)
    {
        var tokens = new List<string>();
        if (scope is not { } element) return tokens;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                tokens.AddRange(Split(element.GetString()));
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
                {
                    tokens.AddRange(Split(item.GetString()));
                }

                break;
            case JsonValueKind.Null or JsonValueKind.Undefined:
                break;
            default:
                throw new McpException("Der Bereich muss ein Text oder eine Liste von Texten sein.");
        }

        return tokens;
    }

    private static string[] Split(string? text) =>
        (text ?? string.Empty).Split([',', ';', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace("ö", "oe", StringComparison.Ordinal).Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ü", "ue", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<kind>rt|rundtisch|of|ofen|vp|verpackung)[\s-]*0*(?<n>\d{1,2})$")]
    private static partial Regex StationPattern();
}
