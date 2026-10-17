namespace OPCAgentSample.Domain.Opc;

/// <summary>
/// Schema der String-NodeIds im Namespace <see cref="NamespaceUri"/>. Gemeinsamer Vertrag von Simulator (OPC-UA-Server) und Collector (OPC-UA-Client).
/// Aufbau: <c>Halle&lt;Id&gt;.&lt;Bereich&gt;.&lt;Station&gt;.&lt;Gruppe&gt;.&lt;Variable&gt;</c>, z. B.
/// <c>ns=2;s=HalleA.Pressen.RT-04.Prozess.PresskraftAufbauzeit</c>. Alle Bestandteile sind ASCII (ohne Umlaute) und
/// ändern sich nicht zur Laufzeit.
/// </summary>
public static class NodeIdScheme
{
    /// <summary>Namespace-URI des Anlagenmodells. Der Server legt sie auf den Index <see cref="ExpectedNamespaceIndex"/>.</summary>
    public const string NamespaceUri = "urn:vompa:opc-agent-sample:plant";

    /// <summary>Erwarteter Namespace-Index; die NodeIds lauten "ns=2;s=...".</summary>
    public const ushort ExpectedNamespaceIndex = 2;

    public const string PlantFolder = "Werk";
    public const string ShiftCalendarObject = "Schichtkalender";
    public const string SimulationObject = "Werk.Simulation";
    public const string ClimateObject = "HalleA.Klima";
    public const string TypesFolder = "Typen";

    public static string Hall(string hallId) => $"Halle{hallId}";

    /// <summary>Ordner des Bereichs unter der Halle (Pressen, Puffer, Oefen, Verpackung).</summary>
    public static string AreaFolder(Area area) => area switch
    {
        Area.Pressing => "Pressen",
        Area.Buffer => "Puffer",
        Area.Curing => "Oefen",
        Area.Packaging => "Verpackung",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    public static string AreaFolderId(string hallId, Area area) => $"{Hall(hallId)}.{AreaFolder(area)}";

    public static string Station(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return $"{AreaFolderId(station.HallId, station.Area)}.{station.Id}";
    }

    public static string Group(Station station, string group) => $"{Station(station)}.{group}";

    public static string Variable(Station station, string group, string variable) => $"{Group(station, group)}.{variable}";

    /// <summary>Alarm-Condition einer Station; <paramref name="alarmId"/> ist die Alarm-Id des Generators (z. B. A-0941).</summary>
    public static string Alarm(Station station, string alarmId) => $"{Group(station, StationGroups.Alarms)}.{alarmId}";

    public static string ClimateVariable(string variable) => $"{ClimateObject}.{variable}";

    public static string ShiftVariable(string variable) => $"{ShiftCalendarObject}.{variable}";

    public static string SimulationVariable(string variable) => $"{SimulationObject}.{variable}";

    /// <summary>Verdichtete Zähler der Hallen B und C: <c>HalleB.Zaehler.&lt;Woche&gt;.&lt;Schicht&gt;.&lt;Variable&gt;</c>.</summary>
    public static string HallCounter(string hallId, string weekKey, string shiftCode, string variable) =>
        $"{Hall(hallId)}.{StationGroups.Counters}.{weekKey}.{shiftCode}.{variable}";

    public static string HallState(string hallId) => $"{Hall(hallId)}.{StationGroups.State}.{StationVariables.OperatingState}";

    public static string Type(string typeName) => $"{TypesFolder}.{typeName}";
}

/// <summary>Namen der Variablengruppen (Ordner unter einer Station).</summary>
public static class StationGroups
{
    public const string Identity = "Identitaet";
    public const string State = "Zustand";
    public const string Order = "Auftrag";
    public const string Counters = "Zaehler";
    public const string Energy = "Energie";
    public const string Events = "Ereignisse";
    public const string Alarms = "Alarme";
    public const string Process = "Prozess";
    public const string Cycle = "Zyklus";
    public const string SubStations = "Teilstationen";
    public const string Material = "Material";
    public const string Tool = "Werkzeug";
    public const string Hydraulics = "Hydraulik";
    public const string ScrapReasons = "Ausschussgruende";
    public const string Capacity = "Kapazitaet";
    public const string ActiveCharge = "LaufendeCharge";
    public const string Temperature = "Temperatur";
    public const string Planning = "Planung";
    public const string Safety = "Sicherheit";
    public const string Inventory = "Bestand";
    public const string Packaging = "Verpackung";
}

/// <summary>Namen der Variablen (Browse- und NodeId-Bestandteil).</summary>
public static class StationVariables
{
    // Identität
    public const string StationId = "StationId";
    public const string Designation = "Bezeichnung";
    public const string AreaName = "Bereich";
    public const string Manufacturer = "Hersteller";
    public const string YearOfConstruction = "Baujahr";

    // Zustand
    public const string OperatingState = "Betriebszustand";
    public const string StateSince = "ZustandSeit";
    public const string MachineryItemState = "MachineryItemState";

    // Auftrag
    public const string OrderId = "AuftragsNr";
    public const string ArticleId = "ArtikelNr";
    public const string OrderQuantity = "SollMenge";
    public const string OrderActual = "IstMenge";
    public const string TargetCycle = "SollTakt";
    public const string ShiftPlanQuantity = "PlanmengeSchicht";

    // Zähler und Energie
    public const string GoodPieces = "StueckGut";
    public const string ScrapPieces = "StueckAusschuss";
    public const string OperatingHours = "Betriebsstunden";
    public const string TotalCycles = "ZyklenGesamt";
    public const string EnergyKwh = "KwhSeitSchichtbeginn";

    // Alarme
    public const string ActiveAlarms = "AktiveAlarme";
    public const string ActiveMessages = "AktiveMeldungen";

    // Prozess (Rundtisch)
    public const string CycleTime = "Taktzeit";
    public const string CycleTimeMean = "TaktzeitMittel5Min";
    public const string CycleTimeTarget = "TaktzeitSoll";
    public const string PressForce = "Presskraft";
    public const string PressForceBuildUp = "PresskraftAufbauzeit";
    public const string PressTime = "Presszeit";
    public const string MouldTemperature = "Formtemperatur";
    public const string FillWeight = "Fuellgewicht";
    public const string FillWeightMean = "FuellgewichtMittel";
    public const string FillWeightTarget = "FuellgewichtSoll";
    public const string FillWeightTolerance = "FuellgewichtToleranz";
    public const string Thickness = "Dicke";

    // Zyklus-Metadaten (je Takt)
    public const string CycleTargetTime = "SollTaktzeit";
    public const string CycleOrderId = "AuftragsNr";
    public const string CycleArticleId = "ArtikelNr";
    public const string CycleMixtureBatch = "MischungChargenId";
    public const string CycleScrapReason = "Ausschussgrund";

    // Material
    public const string Mixture = "Mischung";
    public const string BatchId = "ChargenId";
    public const string MixtureLevelKg = "FuellstandKg";
    public const string ProcessableUntil = "VerarbeitbarBis";
    public const string FabricMagazine = "Gewebemagazin";
    public const string LabelMagazine = "Etikettenmagazin";
    public const string CartonMagazine = "Kartonmagazin";

    // Werkzeug
    public const string MouldId = "FormId";
    public const string StrokesSinceChange = "HuebeSeitWechsel";
    public const string StrokeLimit = "Standzeitgrenze";

    // Hydraulik
    public const string OilTemperature = "Oeltemperatur";
    public const string SystemPressure = "Systemdruck";

    // Ausschussgründe
    public const string ScrapWeight = "Gewicht";
    public const string ScrapThickness = "Dicke";
    public const string ScrapFabric = "Gewebe";
    public const string ScrapLabel = "Etikett";
    public const string ScrapBreakage = "Bruch";

    // Ofen
    public const string Places = "Chargenplaetze";
    public const string OccupiedPlaces = "BelegtePlaetze";
    public const string Occupancy = "Belegung";
    public const string OccupancyNow = "BelegungAktuell";
    public const string ChargeId = "ChargenId";
    public const string ChargeDiameter = "Durchmesser";
    public const string ChargePlates = "Ofenplatten";
    public const string ProfileStep = "Profilschritt";
    public const string RemainingMinutes = "RestzeitMin";
    public const string Zone1Actual = "Zone1Ist";
    public const string Zone2Actual = "Zone2Ist";
    public const string Zone3Actual = "Zone3Ist";
    public const string Zone1Target = "Zone1Soll";
    public const string Zone2Target = "Zone2Soll";
    public const string Zone3Target = "Zone3Soll";
    public const string NextRelease = "NaechsteFreigabe";
    public const string AssignmentRule = "Zuordnungsregel";
    public const string DoorLockState = "TuerverriegelungZustand";
    public const string LastInspection = "LetztePruefung";

    // Puffer
    public const string BufferCapacity = "Kapazitaet";
    public const string PlateCount = "AnzahlPlatten";
    public const string MeanWait = "MittlereWartezeit";
    public const string OldestWait = "AeltesteWartezeit";

    // Verpackung
    public const string CartonType = "Kartontyp";
    public const string PiecesPerCarton = "StueckJeKarton";
    public const string CartonsActual = "KartonsIst";
    public const string CartonsTarget = "KartonsSoll";
    public const string CheckweigherRejects = "KontrollwaageFehlmengen";
    public const string StrapperState = "UmreiferZustand";

    // Teilstationen: <Name> + "Zustand" bzw. "Stoerzaehler"
    public const string StateSuffix = "Zustand";
    public const string FaultCountSuffix = "Stoerzaehler";

    // Klima
    public const string Temperature = "Temperatur";
    public const string Humidity = "Luftfeuchte";
    public const string TemperatureMin = "TemperaturMin";
    public const string TemperatureMax = "TemperaturMax";
    public const string HumidityMin = "LuftfeuchteMin";
    public const string HumidityMax = "LuftfeuchteMax";
    public const string InRange = "ImSollbereich";

    // Schichtkalender und Simulation
    public const string CurrentShift = "AktuelleSchicht";
    public const string ShiftKey = "SchichtSchluessel";
    public const string ShiftStart = "Beginn";
    public const string ShiftEnd = "Ende";
    public const string ProductionDay = "Produktionstag";
    public const string InProduction = "InProduktion";
    public const string SimulationTime = "Zeit";
    public const string SimulationSpeed = "Geschwindigkeit";
    public const string HistoryStart = "HistorieBeginn";

    // Hallen B und C
    public const string PlannedQuantity = "Planmenge";
    public const string RunMinutes = "LaufzeitMin";
    public const string PlanningMinutes = "PlanbelegungMin";
}

/// <summary>
/// Ereignis-Variablen je Station (Gruppe <see cref="StationGroups.Events"/>): historisierte String-Variablen, deren Werte
/// die Datensätze der Domäne als JSON enthalten (System.Text.Json, PascalCase, Enums als Text). Zeitstempel des Wertes =
/// <c>TimestampUtc</c> des Datensatzes. Wechselnde Stände eines Ereignisses (Beginn, Ende) erscheinen als eigene Werte
/// mit derselben <c>Id</c>.
/// </summary>
public static class EventVariables
{
    public const string Losses = "Verluste";
    public const string Alarms = "Alarmmeldungen";
    public const string OrderEvents = "Auftragsereignisse";
    public const string StateChanges = "Zustandswechsel";
    public const string ShiftPlans = "Schichtplaene";
    public const string PressLots = "Pressenlose";
    public const string OvenCharges = "Ofenchargen";
    public const string Pallets = "Paletten";
    public const string UnitCounts = "Minutenzaehlung";
}
