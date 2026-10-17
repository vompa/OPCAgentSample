using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Script;

/// <summary>
/// Drehbuch der Halle A, KW 41 und KW 42 (Spezifikation Abschnitt 6) als Daten. Alle Zeiten Ortszeit Wien.
/// Die Summen der KW 42 (Gewebezuführung 6,4 h / 14, Mischung 4,1 h, Ofen 3,5 h, Rüsten über Soll 3,2 h,
/// Formwechsel 2,0 h / 3, Etiketten 1,1 h) ergeben sich aus diesen Zeilen; Ausgabe-Vorgaben kalibrieren die
/// Stückzahlen der Referenzschichten.
/// </summary>
public static class HallAScript
{
    private const string Rt01 = "RT-01";
    private const string Rt02 = "RT-02";
    private const string Rt03 = "RT-03";
    private const string Rt04 = "RT-04";
    private const string Vp01 = "VP-01";
    private const string Vp02 = "VP-02";
    private const string Of01 = "OF-01";

    private static readonly TimeSpan s_pressSetup = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_packagingSetup = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan s_potLife = TimeSpan.FromHours(3);

    public static IReadOnlyList<ScriptedLoss> Losses(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return [.. Week41Losses(at), .. Week42Losses(at), .. LabelStops(at), .. LiveLosses(at)];
    }

    private static IEnumerable<ScriptedLoss> Week41Losses(LocalClock at)
    {
        // Gewebezuführung
        yield return Fabric("D41-01", Rt03, at.Oct(5, "07:40"), 38, Assemblies.FabricFeed2, "Greifer 2 klemmt", "Erster Hinweis auf Muster");
        yield return Fabric("D41-02", Rt03, at.Oct(7, "02:30"), 28, Assemblies.FabricFeed2, "Greifer 2 klemmt", "Reaktionszeit Bereitschaft 14 min");
        yield return Fabric("D41-03", Rt01, at.Oct(7, "15:40"), 19, Assemblies.FabricFeed1, "Greifer 1 nimmt Gewebe nicht auf");
        yield return Fabric("D41-04", Rt04, at.Oct(6, "11:20"), 17, Assemblies.FabricFeed2, "Greifer 2 klemmt");
        yield return Fabric("D41-05", Rt03, at.Oct(8, "16:30"), 47, Assemblies.FabricFeed2, "Greifer 2 klemmt", "Greifer justiert");
        yield return Fabric("D41-06", Rt03, at.Oct(9, "10:15"), 25, Assemblies.FabricFeed2, "Greifer 2 klemmt");
        yield return Fabric("D41-07", Rt02, at.Oct(9, "03:40"), 31, Assemblies.FabricFeed2, "Gewebe doppelt gegriffen", "Reaktionszeit Bereitschaft 16 min");

        // Warten auf Mischung (Nachtschicht-Muster)
        yield return Mixture("D41-10", Rt02, at.Oct(8, "01:10"), 52, "Mischung A60 zu spät", "Nachtschicht-Muster");
        yield return Mixture("D41-11", Rt01, at.Oct(7, "03:30"), 35, "Mischung A60 zu spät");
        yield return Mixture("D41-12", Rt01, at.Oct(9, "01:00"), 40, "Mischung A60 zu spät");

        yield return OvenWait("D41-15", Vp02, at.Oct(7, "09:30"), 25, "Wartet auf Ø-180-Charge aus OF-02");
        yield return MouldChange("D41-16", Rt03, at.Oct(7, "10:30"), 40, "Form gerissen, Ersatzform eingebaut");
        yield return new("D41-17", Vp02, LossCategory.Breakdown, LossReasons.StrapperFault, Assemblies.Strapper,
            "Umreifer Bandführung", at.Oct(7, "17:10"), Minutes(22));
        yield return Unspecified("D41-18", Rt02, at.Oct(7, "04:30"), 15);
        yield return Unspecified("D41-19", Vp01, at.Oct(9, "02:10"), 20);
        yield return new("D41-20", Rt04, LossCategory.StaffMissing, LossReasons.StaffMissing, Assemblies.Station,
            "Nachtschicht nicht besetzt", at.Oct(9, "22:00"), Minutes(480), Note: "organisatorisch");
        yield return new("D41-21", Rt04, LossCategory.QualityHold, LossReasons.QualityHold, Assemblies.Mould,
            "Dickenabweichung nach Formwechsel", at.Oct(8, "07:20"), Minutes(12), AffectedPieces: 340,
            Note: "Rüsten über Soll: Form nicht eingemessen");
        yield return new("D41-22", Rt01, LossCategory.Overproduction, LossReasons.Overproduction, Assemblies.Station,
            "Auftrag 4688 um 19:10 erreicht, Folgeauftrag nicht freigegeben", at.Oct(6, "19:10"), Minutes(170),
            AffectedPieces: 2_050, Note: "Lager über Bedarf, 2 Ofenchargen zusätzlich");
    }

    private static IEnumerable<ScriptedLoss> Week42Losses(LocalClock at)
    {
        // Gewebezuführung: 14 Ereignisse, 384 min, davon 9 an RT-03 (immer Greifer 2)
        yield return Fabric("D42-01", Rt03, at.Oct(12, "08:20"), 22, Assemblies.FabricFeed2, "Greifer 2 klemmt");
        yield return Fabric("D42-02", Rt03, at.Oct(13, "01:30"), 34, Assemblies.FabricFeed2, "Greifer 2 klemmt", "Reaktionszeit Bereitschaft 18 min");
        yield return Fabric("D42-03", Rt03, at.Oct(13, "16:40"), 18, Assemblies.FabricFeed2, "Greifer 2 klemmt");
        yield return Fabric("D42-04", Rt03, at.Oct(14, "03:10"), 31, Assemblies.FabricFeed2, "Greifer 2 klemmt", "Reaktionszeit Bereitschaft 15 min");
        yield return Fabric("D42-05", Rt03, at.Oct(14, "10:05"), 16, Assemblies.FabricFeed2, "Greifer 2 klemmt");
        yield return Fabric("D42-06", Rt03, at.Oct(14, "15:20"), 21, Assemblies.FabricFeed2, "Greifer 2 klemmt");
        yield return Fabric("D42-07", Rt03, at.Oct(14, "18:05"), 17, Assemblies.FabricFeed2, "Greifer 2 klemmt");
        yield return Fabric("D42-08", Rt03, at.Oct(14, "20:40"), 19, Assemblies.FabricFeed2, "Greifer 2 klemmt", "Greifer erneut justiert");
        yield return Fabric("D42-09", Rt03, at.Oct(15, "05:12"), 113, Assemblies.FabricFeed2, "Gewebegreifer 2 klemmt",
            "Notiz Nachtschicht: Greifer 2 klemmt, Instandhaltung informiert");
        yield return Fabric("D42-10", Rt01, at.Oct(12, "15:30"), 18, Assemblies.FabricFeed1, "Greifer 1 nimmt Gewebe nicht auf");
        yield return Fabric("D42-11", Rt02, at.Oct(13, "09:10"), 21, Assemblies.FabricFeed2, "Gewebe doppelt gegriffen");
        yield return Fabric("D42-12", Rt04, at.Oct(14, "07:30"), 15, Assemblies.FabricFeed2, "Gewebe verrutscht");
        yield return Fabric("D42-13", Rt01, at.Oct(14, "02:20"), 22, Assemblies.FabricFeed1, "Greifer 1 nimmt Gewebe nicht auf", "Reaktionszeit Bereitschaft 12 min");
        yield return Fabric("D42-14", Rt02, at.Oct(14, "23:40"), 17, Assemblies.FabricFeed2, "Gewebe doppelt gegriffen");

        // Warten auf Mischung: 246 min, alle nachts; Mi-Nacht RT-01 + RT-02 = 138 min (2,3 h)
        yield return Mixture("D42-20", Rt01, at.Oct(14, "03:00"), 58, "Mischung A60 zu spät");
        yield return Mixture("D42-21", Rt02, at.Oct(14, "02:40"), 50, "Mischung A60 zu spät");
        yield return Mixture("D42-22", Rt01, at.Oct(15, "00:30"), 66, "Mischung A60 zu spät", "Mischraum nachts nur einfach besetzt");
        yield return Mixture("D42-23", Rt02, at.Oct(15, "01:15"), 72, "Mischung A60 zu spät", "Mischraum nachts nur einfach besetzt");

        // Warten auf Ofen: Ursachenkette RT-02 Überproduktion → Puffer voll → Ø-180-Platten warten → VP-02 steht
        yield return OvenWait("D42-30", Rt04, at.Oct(13, "04:10"), 35, "Puffer voll (58 Platten), Ø-180-Platten können nicht abgelegt werden");
        yield return OvenWait("D42-31", Rt04, at.Oct(13, "06:50"), 70, "Ø-180-Platten warten vor OF-02");
        yield return OvenWait("D42-32", Vp02, at.Oct(13, "08:30"), 45, "VP-02 steht: keine Ø-180-Charge aus OF-02");
        yield return OvenWait("D42-33", Vp02, at.Oct(15, "00:40"), 23, "Wartet auf Ø-180-Charge aus OF-02");
        yield return OvenWait("D42-34", Vp02, at.Oct(15, "09:05"), 95, "Wartet auf Ø-180-Charge aus OF-02", "Kein eigenes Problem: OF-02 voll bis 10:15");

        // Formwechsel ungeplant: 3 Ereignisse, 120 min
        yield return MouldChange("D42-40", Rt04, at.Oct(13, "02:30"), 35, "Form F-180-02 Ausbruch, Ersatzform");
        yield return MouldChange("D42-41", Rt02, at.Oct(13, "06:40"), 45, "Form gerissen, Form F-125-07 eingebaut");
        yield return MouldChange("D42-42", Rt01, at.Oct(14, "17:00"), 40, "Form verschlissen, vorzeitig gewechselt");

        yield return new("D42-45", Vp02, LossCategory.Breakdown, LossReasons.StrapperFault, Assemblies.Strapper,
            "Umreifer Bandspannung", at.Oct(13, "17:20"), Minutes(18));
        yield return new("D42-46", Of01, LossCategory.Breakdown, LossReasons.OvenTemperatureFault, Assemblies.OvenZone2,
            "Temperaturabweichung Zone 2, Charge +25 min", at.Oct(12, "09:40"), Minutes(25), Note: "Ofenbelegung verschiebt sich");
        yield return Unspecified("D42-47", Rt04, at.Oct(15, "03:20"), 25);
        yield return new("D42-50", Vp01, LossCategory.StaffMissing, LossReasons.StaffMissing, Assemblies.Station,
            "Pausenablöse fehlt", at.Oct(15, "02:00"), Minutes(45), Note: "organisatorisch");
        yield return new("D42-51", Rt03, LossCategory.SpeedLoss, LossReasons.SpeedLoss, Assemblies.FabricFeed2,
            "Takt reduziert gefahren wegen Greifer 2", at.Oct(14, "22:00"), TimeSpan.FromMinutes(432),
            Note: "Nachtschicht: Greifer 2 klemmt sonst häufiger");
        yield return Unspecified("D42-48", Vp02, at.Oct(15, "04:00"), 20);
        yield return new("D42-49", Vp01, LossCategory.MinorStop, LossReasons.CartonJam, Assemblies.CartonErector,
            "Kartonstau", at.Oct(15, "08:51"), Minutes(1));

        yield return new("D42-60", Rt02, LossCategory.Overproduction, LossReasons.Overproduction, Assemblies.Station,
            "Auftrag 4705 (Ø 125 A60) zwei Tage vor Bedarf gefertigt", at.Oct(13, "01:48"), TimeSpan.FromMinutes(252),
            AffectedPieces: 3_600, Note: "Puffer vor den Öfen erreicht 58 Platten");
        yield return new("D42-61", Rt04, LossCategory.SpeedLoss, LossReasons.SpeedLoss, Assemblies.Hydraulics,
            "Presskraftaufbau steigt 0,8 → 1,1 s, Taktzeit +9 %", at.Oct(15, "08:30"), TimeSpan.FromMinutes(330),
            Acknowledged: false, Note: "Hydraulik-Sichtprüfung empfohlen");
    }

    /// <summary>„Etiketten leer“ KW 42: 23 Kurzstopps, zusammen 66 min (1,1 h).</summary>
    private static IEnumerable<ScriptedLoss> LabelStops(LocalClock at)
    {
        (string Station, int Day, string Time)[] threeMinuteStops =
        [
            (Rt01, 12, "09:12"), (Rt02, 12, "13:05"), (Rt03, 12, "16:22"), (Rt04, 12, "20:48"), (Rt01, 12, "23:35"),
            (Rt02, 13, "04:52"), (Rt03, 15, "01:20"), (Rt04, 15, "00:15"), (Rt01, 14, "23:20"), (Rt02, 15, "03:10"),
            (Rt03, 13, "23:11"), (Rt04, 14, "05:07"), (Rt01, 14, "08:33"), (Rt02, 14, "12:44"), (Rt03, 14, "13:52"),
            (Rt04, 14, "16:38"), (Rt01, 14, "20:12"), (Rt02, 14, "22:48"), (Rt03, 15, "02:55"), (Rt04, 15, "03:58"),
            (Rt01, 15, "04:41"),
        ];
        var number = 0;
        foreach (var (station, day, time) in threeMinuteStops)
        {
            yield return Label($"E42-{++number:00}", station, at.Oct(day, time), 3);
        }

        yield return Label("E42-22", Rt03, at.Oct(14, "11:30"), 1);
        yield return Label("E42-23", Rt02, at.Oct(15, "08:14"), 2);

        // KW 41 (ohne Summenvorgabe)
        (string Station, int Day, string Time)[] week41 =
        [
            (Rt01, 5, "10:20"), (Rt02, 5, "18:40"), (Rt03, 6, "09:15"), (Rt04, 6, "16:05"), (Rt01, 7, "07:50"),
            (Rt02, 7, "20:30"), (Rt03, 8, "11:45"), (Rt04, 8, "19:10"), (Rt01, 9, "08:25"), (Rt02, 9, "15:35"),
        ];
        number = 0;
        foreach (var (station, day, time) in week41)
        {
            yield return Label($"E41-{++number:00}", station, at.Oct(day, time), 3);
        }
    }

    /// <summary>Ereignisse nach der Demo-Uhr (Live-Fortsetzung).</summary>
    private static IEnumerable<ScriptedLoss> LiveLosses(LocalClock at)
    {
        yield return new("D42-70", Vp01, LossCategory.MinorStop, "Kurzstopp Kontrollwaage", Assemblies.Checkweigher,
            "Kontrollwaage Fehlmenge", at.Oct(15, "10:12"), Minutes(2));
    }

    public static IReadOnlyList<ScriptedSetup> Setups(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            // KW 41
            PressSetup("S41-01", Rt01, at.Oct(6, "22:00"), 10),
            PressSetup("S41-02", Rt04, at.Oct(8, "06:00"), 15),
            PressSetup("S41-03", Rt02, at.Oct(7, "14:00"), 12),
            PressSetup("S41-04", Rt01, at.Oct(8, "14:00"), 25),
            PressSetup("S41-05", Rt03, at.Oct(9, "14:00"), 14),
            PackagingSetup("S41-06", Vp01, at.Oct(6, "14:00"), 10),
            PackagingSetup("S41-07", Vp02, at.Oct(8, "12:00"), 9),

            // KW 42: 11 Rüstvorgänge, Überhang zusammen 194 min (3,2 h, Ø +18 min)
            PressSetup("S42-01", Rt01, at.Oct(12, "07:30"), 16),
            PressSetup("S42-02", Rt02, at.Oct(12, "10:30"), 15),
            PackagingSetup("S42-03", Vp02, at.Oct(12, "12:00"), 14),
            PackagingSetup("S42-04", Vp01, at.Oct(12, "14:30"), 17),
            PressSetup("S42-05", Rt04, at.Oct(12, "18:00"), 19),
            PackagingSetup("S42-06", Vp01, at.Oct(13, "10:00"), 12),
            PressSetup("S42-07", Rt01, at.Oct(13, "11:00"), 22),
            PressSetup("S42-08", Rt03, at.Oct(13, "14:00"), 18),
            PackagingSetup("S42-09", Vp02, at.Oct(14, "07:00"), 20),
            PressSetup("S42-10", Rt04, at.Oct(14, "13:00"), 21),
            PackagingSetup("S42-11", Vp01, at.Oct(15, "03:00"), 20),

            // Live: Rüsten RT-02 auf Ø 115 (Formwartung W-3381 bündelbar)
            PressSetup("S42-12", Rt02, at.Oct(15, "11:30"), 0),
        ];
    }

    public static IReadOnlyList<OrderAssignment> OrderAssignments(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new(Rt01, at.Oct(5, "06:00"), "4688", "A-125-A60"),
            new(Rt01, at.Oct(6, "19:10"), null, "A-125-A60"),
            new(Rt01, at.Oct(6, "22:40"), "4690", "A-115-A60"),
            new(Rt01, at.Oct(8, "14:55"), "4694", "A-125-A60"),
            new(Rt01, at.Oct(12, "08:16"), "4697", "A-115-A60"),
            new(Rt01, at.Oct(13, "11:52"), "4700", "A-125-A60"),
            new(Rt01, at.Oct(14, "22:00"), "4702", "A-125-A60"),
            new(Rt01, at.Oct(15, "08:55"), "4716", "A-125-A60"),
            new(Rt01, at.Oct(15, "11:30"), "4721", "A-125-A60"),

            new(Rt02, at.Oct(5, "06:00"), "4689", "A-125-A60"),
            new(Rt02, at.Oct(7, "14:42"), "4693", "A-115-A60"),
            new(Rt02, at.Oct(12, "11:15"), "4699", "A-125-A60"),
            new(Rt02, at.Oct(13, "01:48"), "4705", "A-125-A60"),
            new(Rt02, at.Oct(13, "06:00"), "4698", "A-125-A60"),
            new(Rt02, at.Oct(15, "12:00"), "4724", "A-115-A60"),

            new(Rt03, at.Oct(5, "06:00"), "4691", "A-125-A46"),
            new(Rt03, at.Oct(9, "14:44"), "4701", "A-115-A46"),
            new(Rt03, at.Oct(13, "14:48"), "4711", "A-125-A46"),

            new(Rt04, at.Oct(5, "06:00"), "4692", "A-180-A36"),
            new(Rt04, at.Oct(8, "06:45"), "4696", "A-180-A30"),
            new(Rt04, at.Oct(12, "18:49"), "4703", "A-180-A36"),
            new(Rt04, at.Oct(14, "13:51"), "4707", "A-180-A30"),

            new(Vp01, at.Oct(5, "06:00"), null, "K-125-25"),
            new(Vp01, at.Oct(12, "15:07"), null, "K-115-25"),
            new(Vp01, at.Oct(13, "10:32"), null, "K-125-25"),
            new(Vp02, at.Oct(5, "06:00"), null, "K-180-10"),
        ];
    }

    public static IReadOnlyList<BatchAssignment> BatchAssignments(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new(Rt01, at.Oct(5, "00:00"), "M-2410-02"),
            new(Rt01, at.Oct(7, "06:00"), "M-2410-06"),
            new(Rt01, at.Oct(9, "06:00"), "M-2410-09"),
            new(Rt01, at.Oct(13, "06:00"), "M-2410-13"),
            new(Rt01, at.Oct(14, "22:00"), "M-2410-17"),
            new(Rt02, at.Oct(5, "00:00"), "M-2410-02"),
            new(Rt02, at.Oct(7, "06:00"), "M-2410-06"),
            new(Rt02, at.Oct(9, "06:00"), "M-2410-09"),
            new(Rt02, at.Oct(12, "06:00"), "M-2410-12"),
            new(Rt02, at.Oct(13, "06:00"), "M-2410-13"),
            new(Rt02, at.Oct(14, "22:00"), "M-2410-17"),
            new(Rt03, at.Oct(5, "00:00"), "M-2410-01"),
            new(Rt03, at.Oct(8, "06:00"), "M-2410-07"),
            new(Rt03, at.Oct(12, "06:00"), "M-2410-11"),
            new(Rt03, at.Oct(14, "06:00"), "M-2410-15"),
            new(Rt04, at.Oct(5, "00:00"), "M-2410-03"),
            new(Rt04, at.Oct(8, "06:00"), "M-2410-08"),
            new(Rt04, at.Oct(12, "18:00"), "M-2410-14"),
            new(Rt04, at.Oct(14, "13:00"), "M-2410-16"),
        ];
    }

    /// <summary>
    /// Kalibrierte Stückzahlen: Referenzschichten (Mi 14.10. Früh/Spät/Nacht, Do 15.10. bis 09:42) und die
    /// Überproduktionsfenster. Außerhalb gilt der Nutzungsgrad der Station.
    /// </summary>
    public static IReadOnlyList<OutputTarget> OutputTargets(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            // KW 41: Überproduktion RT-01 nach Auftragsende 4688, Qualitätssperre RT-04
            new(Rt01, at.Oct(6, "14:00"), at.Oct(6, "19:10"), 3_740),
            new(Rt01, at.Oct(6, "19:10"), at.Oct(6, "22:00"), 2_050),
            new(Rt04, at.Oct(8, "06:45"), at.Oct(8, "07:20"), 340),

            // Mo 12.10. Nacht: RT-02 fertigt 4705 vor Bedarf (118 % Ist/Soll)
            new(Rt02, at.Oct(12, "22:00"), at.Oct(13, "01:48"), 3_244),
            new(Rt02, at.Oct(13, "01:48"), at.Oct(13, "06:00"), 3_600),

            // Mi 14.10. Früh (Hallen-OEE 82 %)
            new(Rt01, at.Oct(14, "06:00"), at.Oct(14, "14:00"), 6_450),
            new(Rt02, at.Oct(14, "06:00"), at.Oct(14, "14:00"), 6_600),
            new(Rt03, at.Oct(14, "06:00"), at.Oct(14, "14:00"), 6_380),
            new(Rt04, at.Oct(14, "06:00"), at.Oct(14, "14:00"), 5_027),
            new(Vp01, at.Oct(14, "06:00"), at.Oct(14, "14:00"), 2_419),
            new(Vp02, at.Oct(14, "06:00"), at.Oct(14, "14:00"), 605),

            // Mi 14.10. Spät (Hallen-OEE 80 %)
            new(Rt01, at.Oct(14, "14:00"), at.Oct(14, "22:00"), 5_945),
            new(Rt02, at.Oct(14, "14:00"), at.Oct(14, "22:00"), 6_486),
            new(Rt03, at.Oct(14, "14:00"), at.Oct(14, "22:00"), 5_716),
            new(Rt04, at.Oct(14, "14:00"), at.Oct(14, "22:00"), 5_760),
            new(Vp01, at.Oct(14, "14:00"), at.Oct(14, "22:00"), 2_390),
            new(Vp02, at.Oct(14, "14:00"), at.Oct(14, "22:00"), 630),

            // Mi 14.10. Nacht (Schichtübergabe 92 %, Hallen-OEE 72 %)
            new(Rt01, at.Oct(14, "22:00"), at.Oct(15, "06:00"), 5_916),
            new(Rt02, at.Oct(14, "22:00"), at.Oct(15, "06:00"), 5_684),
            new(Rt03, at.Oct(14, "22:00"), at.Oct(15, "06:00"), 4_118),
            new(Rt04, at.Oct(14, "22:00"), at.Oct(15, "06:00"), 5_626),
            new(Vp01, at.Oct(14, "22:00"), at.Oct(15, "06:00"), 2_228),
            new(Vp02, at.Oct(14, "22:00"), at.Oct(15, "06:00"), 616),

            // Do 15.10. bis 09:42 (Startzustand Abschnitt 7); RT-01: Auftrag 4702 endet 08:55 mit 8.400 Stück
            new(Rt01, at.Oct(15, "06:00"), at.Oct(15, "08:55"), 2_484),
            new(Rt01, at.Oct(15, "08:55"), at.Oct(15, "09:42"), 656, ContinueAfter: true),
            new(Rt02, at.Oct(15, "06:00"), at.Oct(15, "09:42"), 3_015, ContinueAfter: true),
            new(Rt03, at.Oct(15, "06:00"), at.Oct(15, "09:42"), 2_235, ContinueAfter: true),
            new(Rt04, at.Oct(15, "06:00"), at.Oct(15, "09:42"), 2_760, ContinueAfter: true),
            new(Vp01, at.Oct(15, "06:00"), at.Oct(15, "09:42"), 1_240, ContinueAfter: true),
            new(Vp02, at.Oct(15, "06:00"), at.Oct(15, "09:42"), 266, ContinueAfter: true),
        ];
    }

    public static IReadOnlyList<ScrapWindow> ScrapWindows(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            // Montag-Anfahren: erhöhter Ausschuss in der ersten Stunde
            .. new[] { Rt01, Rt02, Rt03, Rt04 }.SelectMany(station => new ScrapWindow[]
            {
                new(station, at.Oct(5, "06:00"), at.Oct(5, "07:00"), 0.025, ScrapReason.Breakage),
                new(station, at.Oct(12, "06:00"), at.Oct(12, "07:00"), 0.025, ScrapReason.Breakage),
            }),

            new(Rt04, at.Oct(8, "06:45"), at.Oct(8, "07:20"), 1.0, ScrapReason.Thickness),

            // Charge M-2410-17 ab Mi 22:00: Füllgewicht zu hoch
            new(Rt01, at.Oct(14, "22:00"), at.Oct(15, "06:00"), 0.064, ScrapReason.Weight),
            new(Rt01, at.Oct(15, "06:00"), at.Oct(15, "14:00"), 0.025, ScrapReason.Weight),
            new(Rt02, at.Oct(14, "22:00"), at.Oct(15, "06:00"), 0.076, ScrapReason.Weight),
            new(Rt02, at.Oct(15, "06:00"), at.Oct(15, "14:00"), 0.025, ScrapReason.Weight),

            // Wiederanlauf RT-03 nach Störungen Greifer 2, Anlauf RT-04 nach Rüsten
            new(Rt03, at.Oct(14, "15:41"), at.Oct(14, "16:11"), 0.05, ScrapReason.Fabric),
            new(Rt03, at.Oct(14, "18:22"), at.Oct(14, "18:52"), 0.05, ScrapReason.Fabric),
            new(Rt03, at.Oct(14, "20:59"), at.Oct(14, "21:29"), 0.05, ScrapReason.Fabric),
            new(Rt03, at.Oct(15, "07:05"), at.Oct(15, "07:35"), 0.06, ScrapReason.Fabric),
            new(Rt04, at.Oct(14, "13:51"), at.Oct(14, "14:30"), 0.07, ScrapReason.Thickness),
        ];
    }

    public static IReadOnlyList<FillWeightShift> FillWeightShifts(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        var start = at.Oct(5, "00:00");
        return
        [
            new(Rt01, at.Oct(14, "22:00"), TimeSpan.FromMinutes(30), 1.4),
            new(Rt02, at.Oct(14, "22:00"), TimeSpan.FromMinutes(30), 1.6),
            new(Rt03, start, TimeSpan.Zero, 0.1),
            new(Rt04, start, TimeSpan.Zero, 0.1),
        ];
    }

    public static IReadOnlyList<BuildUpDrift> BuildUpDrifts(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return [new(Rt04, at.Oct(15, "08:30"), TimeSpan.FromMinutes(72), 0.8, 1.105, 1.45, 1.293)];
    }

    /// <summary>Mischungsgebinde in der Nacht zum Do und am Do (sonst periodisch alle 3 h).</summary>
    public static IReadOnlyList<MixtureDelivery> MixtureDeliveries(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new(Rt01, at.Oct(14, "22:00"), 340, s_potLife),
            new(Rt01, at.Oct(15, "01:10"), 340, s_potLife),
            new(Rt01, at.Oct(15, "04:20"), 340, s_potLife),
            new(Rt01, at.Oct(15, "07:20"), 340, s_potLife),
            new(Rt02, at.Oct(14, "22:00"), 340, s_potLife),
            new(Rt02, at.Oct(15, "02:30"), 340, s_potLife),
            new(Rt02, at.Oct(15, "05:30"), 340, s_potLife),
            new(Rt02, at.Oct(15, "08:30"), 340, s_potLife),
        ];
    }

    public static IReadOnlyList<ScriptedAlarm> Alarms(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new("A-0941", Rt04, AlarmKind.ConditionWarning, AlarmSeverity.Medium, Assemblies.Hydraulics,
                "Zustandswarnung Presskraftaufbau 1,1 s (Soll 0,8 s)", at.Oct(15, "09:41"), null),
            new("A-0938", Rt01, AlarmKind.ProcessWarning, AlarmSeverity.Low, Assemblies.Material,
                "Mischung A60: Verarbeitbarkeit endet 10:20", at.Oct(15, "09:38"), at.Oct(15, "10:20")),
            new("A-0930", Rt02, AlarmKind.ProcessWarning, AlarmSeverity.Low, Assemblies.Dosing,
                "Füllgewicht über Warngrenze (113,7 g)", at.Oct(15, "09:30"), null),
            new("A-0920", "OF-02", AlarmKind.ProcessWarning, AlarmSeverity.Low, Assemblies.Station,
                "Ofen voll, 42 Platten im Puffer davor", at.Oct(15, "09:20"), at.Oct(15, "10:15")),
            new("A-0912", Vp01, AlarmKind.Notice, AlarmSeverity.Info, Assemblies.Material,
                "Kartonmagazin nachgefüllt", at.Oct(15, "09:12"), at.Oct(15, "09:12")),
            new("A-0905", Vp02, AlarmKind.Notice, AlarmSeverity.Info, Assemblies.Station,
                "Wartet auf Ø-180-Charge aus OF-02", at.Oct(15, "09:05"), at.Oct(15, "10:40")),
            new("A-0705", Rt03, AlarmKind.Notice, AlarmSeverity.Info, Assemblies.FabricFeed2,
                "Gewebegreifer instandgesetzt, Rundtisch läuft", at.Oct(15, "07:05"), at.Oct(15, "07:05")),
        ];
    }

    /// <summary>Puffer vor den Öfen: Platten (Spitze 58 nach der Überproduktion Mo-Nacht, 42 zur Demo-Uhr).</summary>
    public static IReadOnlyList<ProfilePoint> BufferPlates(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new(at.Oct(5, "00:00"), 0), new(at.Oct(5, "06:00"), 0), new(at.Oct(5, "09:00"), 26),
            new(at.Oct(6, "22:00"), 38), new(at.Oct(7, "02:00"), 31), new(at.Oct(9, "12:00"), 30),
            new(at.Oct(10, "06:00"), 18), new(at.Oct(10, "12:00"), 0), new(at.Oct(12, "06:00"), 0),
            new(at.Oct(12, "09:00"), 27), new(at.Oct(12, "22:00"), 34), new(at.Oct(13, "02:30"), 52),
            new(at.Oct(13, "04:30"), 58), new(at.Oct(13, "08:30"), 50), new(at.Oct(13, "14:00"), 36),
            new(at.Oct(14, "12:00"), 33), new(at.Oct(15, "06:00"), 34), new(at.Oct(15, "09:42"), 42),
            new(at.Oct(15, "10:30"), 36), new(at.Oct(16, "12:00"), 30), new(at.Oct(17, "06:00"), 18),
            new(at.Oct(17, "12:00"), 0),
        ];
    }

    public static IReadOnlyList<ProfilePoint> BufferMeanWait(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return
        [
            new(at.Oct(5, "00:00"), 0), new(at.Oct(5, "06:00"), 0), new(at.Oct(5, "09:00"), 30),
            new(at.Oct(9, "12:00"), 32), new(at.Oct(10, "06:00"), 20), new(at.Oct(10, "12:00"), 0),
            new(at.Oct(12, "06:00"), 0), new(at.Oct(12, "09:00"), 30), new(at.Oct(13, "04:30"), 78),
            new(at.Oct(13, "14:00"), 40), new(at.Oct(15, "06:00"), 36), new(at.Oct(15, "09:42"), 45),
            new(at.Oct(15, "10:30"), 38), new(at.Oct(17, "06:00"), 20), new(at.Oct(17, "12:00"), 0),
        ];
    }

    public static IReadOnlyList<OvenDelay> OvenDelays(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return [new(Of01, at.Oct(12, "09:40"), TimeSpan.FromMinutes(25))];
    }

    public static IReadOnlyList<QuietWindow> QuietWindows(LocalClock at)
    {
        ArgumentNullException.ThrowIfNull(at);
        return [new(at.Oct(15, "06:00"), at.Oct(15, "09:42"))];
    }

    private static TimeSpan Minutes(int minutes) => TimeSpan.FromMinutes(minutes);

    private static ScriptedLoss Fabric(string id, string station, DateTimeOffset start, int minutes, string assembly, string cause, string? note = null) =>
        new(id, station, LossCategory.Breakdown, LossReasons.FabricFeedFault, assembly, cause, start, Minutes(minutes), Note: note);

    private static ScriptedLoss Mixture(string id, string station, DateTimeOffset start, int minutes, string cause, string? note = null) =>
        new(id, station, LossCategory.MaterialWait, LossReasons.MixtureWait, Assemblies.Material, cause, start, Minutes(minutes), Note: note);

    private static ScriptedLoss OvenWait(string id, string station, DateTimeOffset start, int minutes, string cause, string? note = null) =>
        new(id, station, LossCategory.UpstreamWait, LossReasons.OvenWait, Assemblies.Station, cause, start, Minutes(minutes), Note: note);

    private static ScriptedLoss MouldChange(string id, string station, DateTimeOffset start, int minutes, string cause) =>
        new(id, station, LossCategory.Breakdown, LossReasons.UnplannedMouldChange, Assemblies.Mould, cause, start, Minutes(minutes));

    private static ScriptedLoss Unspecified(string id, string station, DateTimeOffset start, int minutes) =>
        new(id, station, LossCategory.Breakdown, LossReasons.Unspecified, Assemblies.Station, "Stillstand ohne Grund", start, Minutes(minutes),
            Acknowledged: false, Note: "Grund nicht nachgetragen");

    private static ScriptedLoss Label(string id, string station, DateTimeOffset start, int minutes) =>
        new(id, station, LossCategory.MinorStop, LossReasons.LabelsEmpty, Assemblies.LabelFeed, "Etikettenzufuhr leer", start, Minutes(minutes));

    private static ScriptedSetup PressSetup(string id, string station, DateTimeOffset start, int overrunMinutes) =>
        new(id, station, start, s_pressSetup, Minutes(overrunMinutes));

    private static ScriptedSetup PackagingSetup(string id, string station, DateTimeOffset start, int overrunMinutes) =>
        new(id, station, start, s_packagingSetup, Minutes(overrunMinutes));
}
