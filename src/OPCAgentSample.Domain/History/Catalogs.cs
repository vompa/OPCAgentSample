namespace OPCAgentSample.Domain.History;

/// <summary>Namen der Minutenwert-Variablen (Vertrag für die Minutenwerte und die OPC-Knoten).</summary>
public static class MinuteVariables
{
    /// <summary>Kennung der Klimamessung Halle A als Quelle von Minutenwerten.</summary>
    public const string HallAClimateSource = "A-KLIMA";

    // Zähler seit Schichtbeginn (alle Produktionsstationen)
    public const string GoodCount = "StueckGut";
    public const string ScrapCount = "StueckAusschuss";
    public const string EnergyKwh = "EnergieKwh";

    // Rundtisch
    public const string MixtureLevelKg = "MischungFuellstandKg";
    public const string FabricMagazine = "Gewebemagazin";
    public const string LabelMagazine = "Etikettenmagazin";
    public const string OilTemperature = "Oeltemperatur";
    public const string SystemPressure = "Systemdruck";
    public const string MouldTemperature = "Formtemperatur";

    // Verpackung
    public const string CartonMagazine = "Kartonmagazin";
    public const string CheckweigherRejects = "KontrollwaageFehlmengen";

    // Ofen
    public const string OccupiedPlaces = "BelegtePlaetze";
    public const string Zone1Temperature = "Zone1Temperatur";
    public const string Zone2Temperature = "Zone2Temperatur";
    public const string Zone3Temperature = "Zone3Temperatur";

    // Puffer
    public const string PlateCount = "AnzahlPlatten";
    public const string MeanWaitMinutes = "MittlereWartezeit";
    public const string OldestWaitMinutes = "AeltesteWartezeit";

    // Klima
    public const string Temperature = "Temperatur";
    public const string Humidity = "Luftfeuchte";
}

/// <summary>Pareto-Gründe für Stillstände (Feld <see cref="LossEvent.Reason"/>).</summary>
public static class LossReasons
{
    public const string FabricFeedFault = "Störung Gewebezuführung";
    public const string MixtureWait = "Warten auf Mischung";
    public const string OvenWait = "Warten auf Ofen";
    public const string SetupOverrun = "Rüsten über Soll";
    public const string UnplannedMouldChange = "Formwechsel ungeplant";
    public const string LabelsEmpty = "Etiketten leer";
    public const string StrapperFault = "Störung Umreifer";
    public const string OvenTemperatureFault = "Störung Ofentemperatur";
    public const string CartonJam = "Kartonstau";
    public const string StaffMissing = "Personal fehlt";
    public const string QualityHold = "Qualitätssperre";
    public const string Unspecified = "Sonstiges (ohne Grund)";
    public const string MinorStop = "Kurzstopp";
    public const string Setup = "Rüsten";
    public const string PlannedMaintenance = "Wartung geplant";
    public const string StartupLoss = "Anlaufverlust";
    public const string SpeedLoss = "Taktverlust";
    public const string Overproduction = "Überproduktion";
}

/// <summary>Baugruppen (Feld <see cref="LossEvent.Assembly"/> und <see cref="AlarmRecord.Assembly"/>).</summary>
public static class Assemblies
{
    public const string Station = "Station";
    public const string FabricFeed1 = "Gewebezuführung 1";
    public const string FabricFeed2 = "Gewebezuführung 2";
    public const string Dosing = "Dosierung";
    public const string Stripper = "Abstreifer";
    public const string LabelFeed = "Gewebezuführung 2 + Etikett";
    public const string Press = "Presse";
    public const string Hydraulics = "Hydraulik";
    public const string Ejector = "Ausstoß";
    public const string Mould = "Form";
    public const string CartonErector = "Kartonaufrichter";
    public const string Checkweigher = "Kontrollwaage";
    public const string Strapper = "Umreifer";
    public const string OvenZone2 = "Zone 2";
    public const string Material = "Material";
}
