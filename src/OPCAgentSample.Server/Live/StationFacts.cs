using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.MasterData;
using OPCAgentSample.Server.Mcp;

namespace OPCAgentSample.Server.Live;

internal sealed record ArticleInfo(string Id, int DiameterMm, string Grit, int PiecesPerCarton, double FillWeightTargetGrams);

/// <summary>Taktwerte eines Rundtischs: letzter Artikel und Charge, Mittelwerte der letzten 5 bzw. 15 Minuten.</summary>
internal sealed record CycleFacts(
    string ArticleId, string BatchId, double TargetCycleSeconds, double? MeanCycleSeconds, double? MeanBuildUpSeconds, double? FillWeightMeanGrams);

internal sealed record MouldFacts(string MouldId, string StationId, int Strokes, int StrokeLimit, double StrokesPerMinute);

/// <summary>
/// Alle Messwerte, die der <see cref="StationStateBuilder"/> aus Redis braucht, in wenigen Abfragen gelesen (Zustand, Ringpuffer der
/// letzten Takte, Minutenreihen der letzten halben Stunde, Formhübe, Ofenbelegung der Schicht).
/// </summary>
internal sealed class StationFacts
{
    private static readonly TimeSpan s_cycleWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_fillWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_minuteWindow = TimeSpan.FromMinutes(31);
    private static readonly TimeSpan s_mixtureWindow = TimeSpan.FromHours(4);

    private readonly Dictionary<string, (OperatingState State, DateTimeOffset Since, string? OrderId)> _states = [];
    private readonly Dictionary<(string Station, string Variable), IReadOnlyList<(DateTimeOffset At, double Value)>> _series = [];
    private readonly List<MouldFacts> _moulds = [];

    public Dictionary<string, ArticleInfo> Articles { get; } = [];

    public Dictionary<string, string> BatchGrit { get; } = [];

    public Dictionary<string, CycleFacts> Cycles { get; } = [];

    public Dictionary<string, string> PackagingArticles { get; } = [];

    public Dictionary<string, OccupancyFigures> OvenOccupancies { get; } = [];

    public HashSet<string> BreakdownStations { get; } = [];

    public double? ClimateAtShiftStart { get; private set; }

    public (OperatingState State, DateTimeOffset Since, string? OrderId) StateOf(string stationId, DateTimeOffset until) =>
        _states.TryGetValue(stationId, out var state) ? state : (OperatingState.Idle, until, null);

    public IReadOnlyList<(DateTimeOffset At, double Value)> Series(string stationId, string variable) =>
        _series.TryGetValue((stationId, variable), out var list) ? list : [];

    public double? LastValue(string stationId, string variable) =>
        _series.TryGetValue((stationId, variable), out var list) && list.Count > 0 ? list[^1].Value : null;

    public MouldFacts? MouldOf(string stationId) => _moulds.FirstOrDefault(m => m.StationId == stationId);

    public static async Task<StationFacts> LoadAsync(
        RollupReader reader, PlantQueries queries, MasterDataSnapshot data, ShiftCalendar calendar, DateTimeOffset until,
        ProductionPeriod shift, PlantOptions plant)
    {
        var facts = new StationFacts();
        facts.LoadMasterData(data);
        foreach (var station in PlantLayout.HallAStations)
        {
            if (await reader.ReadStateAtAsync(station.Id, until) is { } state) facts._states[station.Id] = (state.State, state.TimestampUtc, state.OrderId);

            switch (station.Type)
            {
                case StationType.RotaryTable:
                    await facts.LoadCyclesAsync(reader, station.Id, until);
                    break;
                case StationType.Packaging:
                    if (await reader.ReadLastPackagingArticleAsync(station.Id) is { } article) facts.PackagingArticles[station.Id] = article;
                    break;
                default:
                    break;
            }

            await facts.LoadMinuteSeriesAsync(reader, station, until);
        }

        foreach (var mould in await queries.MouldsAsync(until))
        {
            facts._moulds.Add(new MouldFacts(mould.MouldId, mould.StationId, mould.Strokes, mould.StrokeLimit, mould.StrokesPerMinute));
        }

        foreach (var oven in PlantLayout.HallAStations.Where(s => s.Type == StationType.Oven))
        {
            var occupied = await reader.ReadRangeAsync(oven.Id, MinuteVariables.OccupiedPlaces, shift.StartUtc, until, calendar);
            facts.OvenOccupancies[oven.Id] = new OccupancyFigures(occupied.Sum, (double)plant.OvenPlaces * occupied.Count);
        }

        foreach (var loss in await reader.ReadLossesAsync(shift.StartUtc, until))
        {
            if (loss.Category == LossCategory.Breakdown) facts.BreakdownStations.Add(loss.StationId);
        }

        foreach (var variable in new[] { MinuteVariables.Temperature, MinuteVariables.Humidity })
        {
            facts._series[(MinuteVariables.HallAClimateSource, variable)] = await reader.ReadMinutesAsync(
                MinuteVariables.HallAClimateSource, variable, until - s_minuteWindow, until);
        }

        facts.ClimateAtShiftStart = await reader.ReadClimateAtShiftStartAsync(shift.Key);
        return facts;
    }

    private void LoadMasterData(MasterDataSnapshot data)
    {
        foreach (var article in data.Articles.Values)
        {
            Articles[article.Id] = new ArticleInfo(article.Id, article.DiameterMm, article.Grit, article.PiecesPerCarton, (double)article.FillWeightTargetGrams);
        }

        foreach (var batch in data.MixtureBatches.Keys)
        {
            if (data.GritOfBatch(batch) is { } grit) BatchGrit[batch] = grit;
        }
    }

    /// <summary>Letzter Artikel und Charge aus dem Ringpuffer; Mittelwerte der letzten 5 (Takt, Aufbau) bzw. 15 Minuten (Füllgewicht).</summary>
    private async Task LoadCyclesAsync(RollupReader reader, string stationId, DateTimeOffset until)
    {
        var recent = await reader.ReadRecentCyclesAsync(stationId, until);
        if (recent.Count == 0) return;

        var last = recent[^1];
        var fiveMinutes = recent.Where(c => c.TimestampUtc >= until - s_cycleWindow).ToList();
        var fifteenMinutes = recent.Where(c => c.TimestampUtc >= until - s_fillWindow).ToList();
        Cycles[stationId] = new CycleFacts(
            last.ArticleId, last.MixtureBatchId, last.TargetCycleSeconds,
            fiveMinutes.Count > 0 ? fiveMinutes.Average(c => c.CycleTimeSeconds) : null,
            fiveMinutes.Count > 0 ? fiveMinutes.Average(c => c.PressForceBuildUpSeconds) : null,
            fifteenMinutes.Count > 0 ? fifteenMinutes.Average(c => c.FillWeightGrams) : null);
    }

    /// <summary>Minutenreihen, die der Zustand braucht: Material, Ofen, Puffer (Mischungsfüllstand vier Stunden, alles andere eine halbe Stunde).</summary>
    private async Task LoadMinuteSeriesAsync(RollupReader reader, Station station, DateTimeOffset until)
    {
        string[] variables = station.Type switch
        {
            StationType.RotaryTable => [MinuteVariables.MixtureLevelKg, MinuteVariables.FabricMagazine, MinuteVariables.LabelMagazine],
            StationType.Packaging => [MinuteVariables.CartonMagazine, MinuteVariables.LabelMagazine],
            StationType.Oven => [MinuteVariables.OccupiedPlaces, MinuteVariables.Zone2Temperature],
            StationType.Buffer => [MinuteVariables.PlateCount, MinuteVariables.MeanWaitMinutes],
            _ => [],
        };
        foreach (var variable in variables)
        {
            var window = variable == MinuteVariables.MixtureLevelKg ? s_mixtureWindow : s_minuteWindow;
            _series[(station.Id, variable)] = await reader.ReadMinutesAsync(station.Id, variable, until - window, until);
        }
    }
}
