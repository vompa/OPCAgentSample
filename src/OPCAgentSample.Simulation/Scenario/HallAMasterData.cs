using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Simulation.Scenario;

/// <summary>
/// Stammdaten der Halle A (simuliert, auf die Referenzantworten kalibriert). Der Server spielt diese Werte aus seinen
/// Seed-Dateien nach Redis ein; Mengen kalibrierter Aufträge entsprechen der vom Generator gefertigten Menge (Tests sichern das ab).
/// </summary>
public static class HallAMasterData
{
    public static IReadOnlyList<Article> Articles { get; } =
    [
        new("A-115-A60", "Trennscheibe Ø 115 × 1,0 mm A60", 115, "A60", 1.0, 3.55, 98, 1.5, 380, 0.95m),
        new("A-115-A46", "Trennscheibe Ø 115 × 1,6 mm A46", 115, "A46", 1.6, 3.55, 98, 1.5, 390, 1.05m),
        new("A-125-A60", "Trennscheibe Ø 125 × 1,0 mm A60", 125, "A60", 1.0, 3.73, 112, 1.5, 420, 1.10m),
        new("A-125-A46", "Trennscheibe Ø 125 × 1,6 mm A46", 125, "A46", 1.6, 3.73, 112, 1.5, 430, 1.25m),
        new("A-180-A30", "Trennscheibe Ø 180 × 3,0 mm A30", 180, "A30", 3.0, 4.2, 112, 1.5, 680, 2.40m),
        new("A-180-A36", "Trennscheibe Ø 180 × 2,5 mm A36", 180, "A36", 2.5, 4.2, 112, 1.5, 670, 2.30m),
        new("A-230-A36", "Trennscheibe Ø 230 × 3,0 mm A36", 230, "A36", 3.0, 5.2, 112, 1.5, 820, 3.10m),
        new("A-230-A30", "Trennscheibe Ø 230 × 3,0 mm A30", 230, "A30", 3.0, 5.2, 112, 1.5, 830, 3.20m),
    ];

    public static IReadOnlyList<PackagingArticle> PackagingArticles { get; } =
    [
        new("K-125-25", "Karton Ø 125, 25 Stück", 125, 25, 10.12, 0.60m),
        new("K-115-25", "Karton Ø 115, 25 Stück", 115, 25, 9.8, 0.55m),
        new("K-180-10", "Karton Ø 180, 10 Stück", 180, 10, 37.1, 0.80m),
    ];

    public static IReadOnlyList<ProductionStationSpec> ProductionStations { get; } =
    [
        new("RT-01", IsPress: true, 6_700, 6_700, 5_800, 4.0m, BaseSlowdown: 0.02, DefaultUtilization: 0.81),
        new("RT-02", IsPress: true, 6_700, 6_700, 5_800, 4.0m, BaseSlowdown: 0.02, DefaultUtilization: 0.81),
        new("RT-03", IsPress: true, 6_700, 6_700, 5_800, 33.7m, BaseSlowdown: 0.02, DefaultUtilization: 0.81),
        new("RT-04", IsPress: true, 6_700, 6_700, 5_800, 4.2m, BaseSlowdown: 0.0, DefaultUtilization: 0.81),
        new("VP-01", IsPress: false, 2_740, 2_740, 2_250, 2.0m, BaseSlowdown: 0.0, DefaultUtilization: 0.83),
        new("VP-02", IsPress: false, 900, 900, 700, 2.0m, BaseSlowdown: 0.0, DefaultUtilization: 0.83),
    ];

    public static ClimateRange ClimateRange { get; } = new(18, 26, 40, 60);

    /// <summary>Beginn der Tageskennzahlen (Historie B): ein Quartal vor dem Rohdatenfenster.</summary>
    public static DateOnly DailyHistoryStart { get; } = new(2026, 7, 1);

    /// <summary>MTBF/MTTR der letzten 90 Tage (Referenz „Lohnt sich eine Überholung von RT-03?“).</summary>
    public static IReadOnlyList<ReliabilityTarget> ReliabilityTargets { get; } =
    [
        new("RT-01", 82, 37, 0.25),
        new("RT-02", 77, 36, 0.25),
        new("RT-03", 31, 42, 0.78),
        new("RT-04", 74, 38, 0.25),
        new("VP-01", 90, 32, 0),
        new("VP-02", 85, 34, 0),
        new("OF-01", 180, 42, 0),
        new("OF-02", 180, 42, 0),
    ];

    /// <summary>Anteil ungeplanter Stillstände seit Juli (Referenz „Instandhaltung“).</summary>
    public static IReadOnlyList<UnplannedShareTarget> UnplannedShares { get; } =
    [
        new(7, 0.48), new(8, 0.45), new(9, 0.39), new(10, 0.33),
    ];

    public static ModelParameters Model { get; } = new(
        MinorStopsPerShift: 2,
        MinorStopTotalPerShift: TimeSpan.FromMinutes(3),
        MinStartupLoss: TimeSpan.FromMinutes(15),
        MaxStartupLoss: TimeSpan.FromMinutes(20),
        StartupSpeedFactor: 0.6,
        StartupCycleStretch: 1.08,
        CycleNoiseSigma: 0.008,
        CycleNoiseLimit: 0.02,
        FillWeightSigmaGrams: 0.5,
        ThicknessSigmaMm: 0.012,
        PressForceSigma: 0.01,
        BuildUpNominalSeconds: 0.8,
        BuildUpSigmaSeconds: 0.01,
        BaseScrapRate: 0.007,
        StartupScrapRate: 0.02,
        PackagingScrapRate: 0.002,
        ClimateMeanCelsius: 24.034,
        ClimateAmplitudeCelsius: 1.898,
        ClimateColdestHour: 5.0,
        HumidityMean: 47.0,
        HumidityAmplitude: 3.0);

    public static IReadOnlyList<MixtureBatch> MixtureBatches(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new("M-2410-01", "A46", 1.38, at.Oct(5, "05:00"), "unauffällig"),
            new("M-2410-02", "A60", 1.38, at.Oct(5, "05:10"), "unauffällig"),
            new("M-2410-03", "A36", 1.40, at.Oct(5, "05:20"), "unauffällig"),
            new("M-2410-06", "A60", 1.38, at.Oct(7, "05:10"), "unauffällig"),
            new("M-2410-07", "A46", 1.39, at.Oct(8, "05:00"), "unauffällig"),
            new("M-2410-08", "A30", 1.41, at.Oct(8, "05:20"), "unauffällig"),
            new("M-2410-09", "A60", 1.37, at.Oct(9, "05:10"), "unauffällig"),
            new("M-2410-11", "A46", 1.38, at.Oct(12, "05:00"), "unauffällig"),
            new("M-2410-12", "A60", 1.38, at.Oct(12, "05:10"), "unauffällig"),
            new("M-2410-13", "A60", 1.39, at.Oct(13, "05:10"), "unauffällig"),
            new("M-2410-14", "A36", 1.40, at.Oct(12, "17:00"), "unauffällig"),
            new("M-2410-15", "A46", 1.38, at.Oct(14, "05:00"), "unauffällig"),
            new("M-2410-16", "A30", 1.41, at.Oct(14, "12:00"), "unauffällig"),
            new("M-2410-17", "A60", 1.46, at.Oct(14, "21:15"), "Schüttdichte über Rezeptwert (Soll 1,38 kg/l)"),
        ];
    }

    public static IReadOnlyList<ProductionOrder> Orders(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new("4688", PlantLayout.HallA, "RT-01", "A-125-A60", 27_697, at.Oct(5, "06:00"), at.Oct(6, "19:10"), at.Oct(5, "06:00"), at.Oct(8, "14:00")),
            new("4689", PlantLayout.HallA, "RT-02", "A-125-A60", 41_731, at.Oct(5, "06:00"), at.Oct(7, "14:00"), at.Oct(5, "06:00"), at.Oct(9, "14:00")),
            new("4690", PlantLayout.HallA, "RT-01", "A-115-A60", 30_192, at.Oct(6, "22:30"), at.Oct(8, "14:00"), at.Oct(6, "22:00"), at.Oct(12, "06:00")),
            new("4691", PlantLayout.HallA, "RT-03", "A-125-A46", 75_564, at.Oct(5, "06:00"), at.Oct(9, "14:00"), at.Oct(5, "06:00"), at.Oct(12, "06:00")),
            new("4692", PlantLayout.HallA, "RT-04", "A-180-A36", 48_604, at.Oct(5, "06:00"), at.Oct(8, "06:00"), at.Oct(5, "06:00"), at.Oct(9, "06:00")),
            new("4693", PlantLayout.HallA, "RT-02", "A-115-A60", 52_112, at.Oct(7, "14:30"), at.Oct(12, "10:30"), at.Oct(7, "14:00"), at.Oct(13, "06:00")),
            new("4694", PlantLayout.HallA, "RT-01", "A-125-A60", 29_743, at.Oct(8, "14:30"), at.Oct(12, "07:30"), at.Oct(8, "14:00"), at.Oct(13, "06:00")),
            new("4696", PlantLayout.HallA, "RT-04", "A-180-A30", 34_584, at.Oct(8, "06:30"), at.Oct(12, "18:00"), at.Oct(8, "06:00"), at.Oct(13, "06:00")),
            new("4697", PlantLayout.HallA, "RT-01", "A-115-A60", 20_766, at.Oct(12, "08:16"), at.Oct(13, "11:00"), at.Oct(12, "06:00"), at.Oct(14, "06:00")),
            new("4698", PlantLayout.HallA, "RT-02", "A-125-A60", 39_663, at.Oct(13, "06:00"), at.Oct(15, "11:20"), at.Oct(13, "06:00"), at.Oct(16, "06:00")),
            new("4699", PlantLayout.HallA, "RT-02", "A-125-A60", 11_269, at.Oct(12, "11:15"), at.Oct(13, "01:48"), at.Oct(12, "06:00"), at.Oct(14, "06:00")),
            new("4700", PlantLayout.HallA, "RT-01", "A-125-A60", 24_982, at.Oct(13, "11:52"), at.Oct(14, "22:00"), at.Oct(13, "06:00"), at.Oct(15, "06:00")),
            new("4701", PlantLayout.HallA, "RT-03", "A-115-A46", 36_398, at.Oct(9, "14:44"), at.Oct(13, "14:00"), at.Oct(9, "14:00"), at.Oct(14, "06:00")),
            new("4702", PlantLayout.HallA, "RT-01", "A-125-A60", 8_400, at.Oct(14, "22:00"), at.Oct(15, "08:55"), at.Oct(14, "22:00"), at.Oct(15, "14:00")),
            new("4703", PlantLayout.HallA, "RT-04", "A-180-A36", 27_120, at.Oct(12, "18:49"), at.Oct(14, "13:00"), at.Oct(12, "18:00"), at.Oct(15, "06:00")),
            new("4705", PlantLayout.HallA, "RT-02", "A-125-A60", 3_600, at.Oct(14, "06:00"), at.Oct(14, "11:00"), at.Oct(15, "06:00"), at.Oct(15, "14:00")),
            new("4707", PlantLayout.HallA, "RT-04", "A-180-A30", 36_000, at.Oct(14, "13:51"), at.Oct(16, "22:00"), at.Oct(14, "13:00"), at.Oct(19, "06:00")),
            new("4711", PlantLayout.HallA, "RT-03", "A-125-A46", 38_350, at.Oct(13, "14:48"), at.Oct(15, "21:01"), at.Oct(13, "14:00"), at.Oct(16, "22:00")),
            new("4716", PlantLayout.HallA, "RT-01", "A-125-A60", 2_400, at.Oct(15, "08:55"), at.Oct(15, "11:30"), at.Oct(15, "08:55"), at.Oct(16, "06:00")),
            new("4721", PlantLayout.HallA, "RT-01", "A-125-A60", 30_000, at.Oct(15, "11:30"), at.Oct(16, "22:00"), at.Oct(15, "11:30"), at.Oct(19, "06:00")),
            new("4724", PlantLayout.HallA, "RT-02", "A-115-A60", 20_000, at.Oct(15, "12:00"), at.Oct(16, "14:00"), at.Oct(15, "12:00"), at.Oct(19, "06:00")),
            new("4730", PlantLayout.HallB, null, "A-230-A36", 24_000, at.Oct(12, "06:00"), at.Oct(19, "22:00"), at.Oct(12, "06:00"), at.Oct(20, "14:00"), ProducedAtDemo: 13_920),
            new("4733", PlantLayout.HallB, null, "A-230-A30", 22_000, at.Oct(13, "06:00"), at.Oct(19, "22:00"), at.Oct(13, "06:00"), at.Oct(20, "14:00"), ProducedAtDemo: 9_020),
        ];
    }

    public static IReadOnlyList<Mould> Moulds(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new("F-125-07", "RT-02", 125, Nests: 4, StrokeLimit: 10_072, at.Oct(13, "07:25"), ForecastStrokesPerDay: 4_700),
            new("F-180-03", "RT-04", 180, Nests: 4, StrokeLimit: 35_900, at.Oct(8, "06:45"), ForecastStrokesPerDay: 4_100),
        ];
    }

    public static IReadOnlyList<OvenSpec> Ovens(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new("OF-01", Places: 5, PlatesPerPlace: 10, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(60),
                LoadInterval: TimeSpan.FromMinutes(62), LoadAnchorUtc: at.Oct(15, "05:50"), DiameterMm: 125,
                AssignmentRule: "Ø 115 und Ø 125", HoldTemperatureCelsius: 180, DowntimeEuroPerMinute: 2.0m),
            new("OF-02", Places: 5, PlatesPerPlace: 10, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(60),
                LoadInterval: TimeSpan.FromSeconds(2985), LoadAnchorUtc: at.Oct(15, "06:15"), DiameterMm: 180,
                AssignmentRule: "alle Ø 180", HoldTemperatureCelsius: 180, DowntimeEuroPerMinute: 2.0m),
        ];
    }

    public static IReadOnlyList<ConsumableSpec> Consumables { get; } =
    [
        new("RT-01", MinuteVariables.FabricMagazine, 4_400, 2, TimeSpan.FromMinutes(150), TimeSpan.FromMinutes(110)),
        new("RT-02", MinuteVariables.FabricMagazine, 3_065, 2, TimeSpan.FromMinutes(110), TimeSpan.FromMinutes(78)),
        new("RT-03", MinuteVariables.FabricMagazine, 4_400, 2, TimeSpan.FromMinutes(150), TimeSpan.FromMinutes(125)),
        new("RT-04", MinuteVariables.FabricMagazine, 5_000, 2, TimeSpan.FromMinutes(180), TimeSpan.FromMinutes(30)),
        new("RT-01", MinuteVariables.LabelMagazine, 4_000, 1, TimeSpan.FromMinutes(240), TimeSpan.FromMinutes(30)),
        new("RT-02", MinuteVariables.LabelMagazine, 4_000, 1, TimeSpan.FromMinutes(240), TimeSpan.FromMinutes(90)),
        new("RT-03", MinuteVariables.LabelMagazine, 4_000, 1, TimeSpan.FromMinutes(240), TimeSpan.FromMinutes(40)),
        new("RT-04", MinuteVariables.LabelMagazine, 4_000, 1, TimeSpan.FromMinutes(240), TimeSpan.FromMinutes(193)),
        new("VP-01", MinuteVariables.CartonMagazine, 742, 1, TimeSpan.FromMinutes(130), TimeSpan.FromMinutes(2)),
        new("VP-02", MinuteVariables.CartonMagazine, 400, 1, TimeSpan.FromMinutes(200), TimeSpan.FromMinutes(90)),
        new("VP-01", MinuteVariables.LabelMagazine, 2_000, 1, TimeSpan.FromMinutes(300), TimeSpan.FromMinutes(100)),
        new("VP-02", MinuteVariables.LabelMagazine, 1_000, 1, TimeSpan.FromMinutes(300), TimeSpan.FromMinutes(100)),
    ];
}
