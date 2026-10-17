namespace OPCAgentSample.Domain;

/// <summary>Bereich einer Halle; bestimmt die Gruppierung in Übersicht und Kennzahlen.</summary>
public enum Area
{
    Pressing,
    Buffer,
    Curing,
    Packaging,
}

/// <summary>Stationstyp im OPC-UA-Informationsmodell (Spezifikation Abschnitt 4).</summary>
public enum StationType
{
    RotaryTable,
    Buffer,
    Oven,
    Packaging,
}

/// <summary>Eine Station einer Halle. <see cref="Id"/> ist die fachliche Kennung (z. B. RT-01).</summary>
public sealed record Station(string Id, string Name, string HallId, Area Area, StationType Type);

/// <summary>Aufbau des Werks. Nur Halle A ist ausmodelliert; Hallen B und C liefern nur Zähler und Zustände.</summary>
public static class PlantLayout
{
    public const string PlantId = "Werk";
    public const string HallA = "A";
    public const string HallB = "B";
    public const string HallC = "C";

    public static IReadOnlyList<string> Halls { get; } = [HallA, HallB, HallC];

    public static IReadOnlyList<Station> HallAStations { get; } =
    [
        new("RT-01", "Rundtisch 1", HallA, Area.Pressing, StationType.RotaryTable),
        new("RT-02", "Rundtisch 2", HallA, Area.Pressing, StationType.RotaryTable),
        new("RT-03", "Rundtisch 3", HallA, Area.Pressing, StationType.RotaryTable),
        new("RT-04", "Rundtisch 4", HallA, Area.Pressing, StationType.RotaryTable),
        new("PU-OFEN", "Puffer vor den Öfen", HallA, Area.Buffer, StationType.Buffer),
        new("OF-01", "Aushärteofen 1", HallA, Area.Curing, StationType.Oven),
        new("OF-02", "Aushärteofen 2", HallA, Area.Curing, StationType.Oven),
        new("VP-01", "Verpackung 1", HallA, Area.Packaging, StationType.Packaging),
        new("VP-02", "Verpackung 2", HallA, Area.Packaging, StationType.Packaging),
    ];

    /// <summary>Die acht Produktionsstationen der Halle A (ohne Puffer), wie sie die Anlagenliste zeigt.</summary>
    public static IReadOnlyList<Station> HallAProductionStations { get; } =
        HallAStations.Where(s => s.Area != Area.Buffer).ToList();

    public static Station Get(string stationId) =>
        HallAStations.FirstOrDefault(s => s.Id == stationId)
        ?? throw new ArgumentException($"Unbekannte Station '{stationId}'.", nameof(stationId));

    public static bool IsKnown(string stationId) => HallAStations.Any(s => s.Id == stationId);
}

/// <summary>Betriebszustand einer Station (OPC-Knoten Betriebszustand).</summary>
public enum OperatingState
{
    Running,
    Idle,
    Setup,
    Fault,
    PlannedMaintenance,
    WaitingForMaterial,
    WaitingForUpstream,
    RunningWithoutOrder,
}

/// <summary>Kategorie eines Zeit- oder Mengenverlusts (Spezifikation Abschnitt 6) mit ihrer Wirkung auf die OEE.</summary>
public enum LossCategory
{
    /// <summary>Störung über 5 Minuten (Verfügbarkeit).</summary>
    Breakdown,

    /// <summary>Kurzstopp unter 5 Minuten (Leistung).</summary>
    MinorStop,

    /// <summary>Taktverlust (Leistung).</summary>
    SpeedLoss,

    /// <summary>Warten auf Material: Mischung, Gewebe, Kartons (Verfügbarkeit).</summary>
    MaterialWait,

    /// <summary>Warten auf die Vorstufe (Verfügbarkeit).</summary>
    UpstreamWait,

    /// <summary>Qualitätssperre oder Ausschuss (Qualität).</summary>
    QualityHold,

    /// <summary>Personal fehlt, organisatorisch (Verfügbarkeit).</summary>
    StaffMissing,

    /// <summary>Rüsten, geplant und getrennt ausgewiesen.</summary>
    Setup,

    /// <summary>Geplante Wartung, getrennt ausgewiesen.</summary>
    PlannedMaintenance,

    /// <summary>Produktion über Auftrag oder vor Bedarf; keine OEE-Wirkung, eigene Kennzahl.</summary>
    Overproduction,

    /// <summary>Rüsten über der Soll-Rüstzeit; der Soll-Anteil ist geplant, der Überhang ungeplant (Verfügbarkeit).</summary>
    SetupOverrun,
}

/// <summary>OEE-Faktor, auf den eine Verlustkategorie wirkt.</summary>
public enum OeeFactor
{
    Availability,
    Performance,
    Quality,
    Planned,
    None,
}

public static class LossCategoryExtensions
{
    public static OeeFactor Factor(this LossCategory category) => category switch
    {
        LossCategory.Breakdown or LossCategory.MaterialWait or LossCategory.UpstreamWait
            or LossCategory.StaffMissing or LossCategory.SetupOverrun => OeeFactor.Availability,
        LossCategory.MinorStop or LossCategory.SpeedLoss => OeeFactor.Performance,
        LossCategory.QualityHold => OeeFactor.Quality,
        LossCategory.Setup or LossCategory.PlannedMaintenance => OeeFactor.Planned,
        LossCategory.Overproduction => OeeFactor.None,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    public static bool IsPlanned(this LossCategory category) => category.Factor() == OeeFactor.Planned;
}

/// <summary>Ampel einer Station; das Statuswort wird immer zusätzlich zur Farbe angezeigt.</summary>
public enum TrafficLight
{
    Running,
    Warning,
    Problem,
}

public static class TrafficLightExtensions
{
    /// <summary>Statuswort der Oberfläche: Läuft, Warnung, Problem.</summary>
    public static string Label(this TrafficLight light) => light switch
    {
        TrafficLight.Running => "Läuft",
        TrafficLight.Warning => "Warnung",
        TrafficLight.Problem => "Problem",
        _ => throw new ArgumentOutOfRangeException(nameof(light), light, null),
    };

    /// <summary>Der schlechteste Wert bestimmt die Ampel.</summary>
    public static TrafficLight Worst(this IEnumerable<TrafficLight> lights) =>
        lights.DefaultIfEmpty(TrafficLight.Running).Max();
}
