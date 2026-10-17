using System.Globalization;
using Microsoft.Extensions.Options;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Kpis;

namespace OPCAgentSample.Server.Live;

/// <summary>
/// Anlagenübersicht aus den Stationszuständen in Redis (<c>st:{id}</c>) und der Ampel der
/// Kennzahlen-Bibliothek. Der Produktionsleiter sieht statt der Stationen die Hallen mit der OEE der Kalenderwoche.
/// </summary>
public sealed class LiveHallOverviewSource(
    StationStateStore states,
    KpiBlockService blocks,
    RollupReader reader,
    ShiftCalendar calendar,
    IDemoClock clock,
    IOptions<PlantOptions> plant) : IHallOverviewSource
{
    private const string ProductionLeadRoleId = "production_lead";
    private static readonly (string Id, string Title, string KpiLabel, Area Area)[] s_groups =
    [
        ("pressen", "Pressen", "Ist/Soll", Area.Pressing),
        ("öfen", "Öfen", "Belegung", Area.Curing),
        ("verpackung", "Verpackung", "Ist/Soll", Area.Packaging),
    ];

    public async Task<HallOverviewSnapshot> GetAsync(string roleId, CancellationToken cancellationToken = default)
    {
        var snapshot = (await states.GetAsync(cancellationToken)).Snapshot;
        return roleId == ProductionLeadRoleId
            ? await PlantAsync(snapshot, cancellationToken)
            : Hall(snapshot);
    }

    private HallOverviewSnapshot Hall(StationSnapshot snapshot)
    {
        var groups = snapshot.Stations.Count == 0
            ? []
            : s_groups.Select(g => new StationGroup(
                g.Id, g.Title, g.KpiLabel,
                [.. snapshot.Stations.Where(s => s.Area == g.Area).Select(ToInfo)])).ToList();
        return new HallOverviewSnapshot(plant.Value.HallId, $"Halle {plant.Value.HallId}", ToClimate(snapshot.Climate), groups);
    }

    private async Task<HallOverviewSnapshot> PlantAsync(StationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var week = calendar.WeekPeriodOf(clock.UtcNow);
        var weekNumber = ISOWeek.GetWeekOfYear(calendar.ToLocal(week.StartUtc).DateTime);
        var label = $"OEE KW {weekNumber}";
        var halls = new List<StationInfo>();
        if (snapshot.Stations.Count > 0) halls.Add(await HallAAsync(snapshot, week, label, cancellationToken));
        foreach (var (hall, stationId) in plant.Value.CounterStations.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            halls.Add(await CounterHallAsync(hall, stationId, week, label, cancellationToken));
        }

        return new HallOverviewSnapshot("plant", "Werk", Climate: null, [new StationGroup("hallen", "Hallen", label, halls)]);
    }

    private async Task<StationInfo> HallAAsync(StationSnapshot snapshot, ProductionPeriod week, string label, CancellationToken ct)
    {
        var block = (await blocks.GetAsync(week, ct)).Block;
        var oee = OeeCalculator.From(block, OeeStations());
        var problems = snapshot.Stations.Count(s => s.Light == TrafficLight.Problem);
        var status = problems >= 2 ? StationStatus.Problem
            : snapshot.Stations.Any(s => s.Light != TrafficLight.Running) ? StationStatus.Warning
            : StationStatus.Running;
        var worst = snapshot.Stations
            .Where(s => s.Light != TrafficLight.Running)
            .OrderByDescending(s => s.Light)
            .Take(2)
            .Select(s => $"{s.StationId} {s.Message}");
        var message = string.Join(", ", worst);
        return new StationInfo(
            $"Halle {plant.Value.HallId}", status, string.Empty, new StationKpi($"{OeeResult.Percent(oee.Oee)} %", label),
            message.Length > 0 ? message : "Im Plan");
    }

    /// <summary>Hallen, die nur Zähler liefern (B, C): OEE der Kalenderwoche aus ihren Tageszählern.</summary>
    private async Task<StationInfo> CounterHallAsync(string hall, string stationId, ProductionPeriod week, string label, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stored = await reader.ReadBlockAsync([stationId], RollupLevel.Week, week.Key);
        if (!stored.Block.Stations.TryGetValue(stationId, out var counter)) return new StationInfo($"Halle {hall}", StationStatus.NoData, string.Empty, new StationKpi("–", label), "Für diese Woche liegen keine Kennzahlen vor");

        var oee = OeeCalculator.From(counter.Figures);
        return new StationInfo($"Halle {hall}", StationStatus.Running, string.Empty, new StationKpi($"{OeeResult.Percent(oee.Oee)} %", label), "Im Plan");
    }

    /// <summary>Stationen der Hallen-OEE: Pressen und Verpackung (Öfen werden über die Belegung bewertet).</summary>
    private static IEnumerable<string> OeeStations() =>
        PlantLayout.HallAStations.Where(s => s.Area is Area.Pressing or Area.Packaging).Select(s => s.Id);

    private static StationInfo ToInfo(StationState state) => new(
        state.StationId, Status(state.Light), state.ArticleLabel, new StationKpi(state.KpiValue, state.KpiUnit), state.Message);

    private static string Status(TrafficLight light) => light switch
    {
        TrafficLight.Problem => StationStatus.Problem,
        TrafficLight.Warning => StationStatus.Warning,
        _ => StationStatus.Running,
    };

    private ClimateInfo? ToClimate(ClimateState? climate)
    {
        if (climate is null) return null;

        var options = plant.Value;
        var delta = climate.Celsius - climate.CelsiusAtShiftStart;
        var shiftStart = calendar.ToLocal(calendar.ShiftPeriodOf(climate.SourceUntilUtc).StartUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
        var tooltip = $"Sollbereich für Mischungen: {options.ClimateMinCelsius:0}–{options.ClimateMaxCelsius:0} °C, "
            + $"{options.ClimateMinHumidity:0}–{options.ClimateMaxHumidity:0} % rF · seit {shiftStart} {Comma(delta, "+0.0;-0.0;0.0")} °C";
        return new ClimateInfo(
            $"{Comma(climate.Celsius, "0.0")} °C",
            $"{Math.Round(climate.Humidity, MidpointRounding.AwayFromZero):0} % rF",
            climate.InRange ? "Hallenklima im Sollbereich" : "Hallenklima außerhalb des Sollbereichs",
            tooltip,
            climate.InRange ? StationStatus.Running : StationStatus.Warning);
    }

    /// <summary>Zahl mit Dezimalkomma wie in der Oberfläche, unabhängig von der Kultur des Rechners.</summary>
    private static string Comma(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture).Replace('.', ',');
}

/// <summary>Meldungen des laufenden Produktionstags aus dem Redis-Stream (<c>ev:A:{prodtag}</c>), neueste zuerst.</summary>
public sealed class LiveFeedSource(EventFeed feed, IDemoClock clock) : IFeedSource
{
    public Task<IReadOnlyList<FeedEntry>> GetAllAsync(CancellationToken cancellationToken = default) =>
        feed.ReadAsync(clock.UtcNow, cancellationToken);
}
