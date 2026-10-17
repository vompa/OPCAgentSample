using System.ComponentModel.DataAnnotations;
using OPCAgentSample.Domain;

namespace OPCAgentSample.Server.Configuration;

/// <summary>Datenaufnahme aus den Historienquellen (Abschnitt Ingestion).</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Abstand, in dem der Hintergrunddienst nach dem Aufholen neue Datensätze holt.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Einstellungen der Anlage, die weder in den Stammdaten noch in der Historie stehen (Abschnitt Plant).</summary>
public sealed class PlantOptions
{
    public const string SectionName = "Plant";

    public string HallId { get; set; } = PlantLayout.HallA;

    /// <summary>Chargenplätze je Ofen (OPC-Knoten Chargenplätze).</summary>
    [Range(1, 50)]
    public int OvenPlaces { get; set; } = 5;

    /// <summary>Topfzeit einer Mischungslieferung; „verarbeitbar bis“ = Lieferung + Topfzeit.</summary>
    [Range(typeof(TimeSpan), "00:10:00", "1.00:00:00")]
    public TimeSpan MixturePotLife { get; set; } = TimeSpan.FromHours(3);

    /// <summary>Anstieg des Füllstands (kg) zwischen zwei Minuten, ab dem eine Mischungslieferung erkannt wird.</summary>
    [Range(1, 10_000)]
    public double MixtureDeliveryJumpKg { get; set; } = 50;

    /// <summary>Sollbereich des Hallenklimas für Mischungen.</summary>
    public double ClimateMinCelsius { get; set; } = 18;

    public double ClimateMaxCelsius { get; set; } = 26;

    public double ClimateMinHumidity { get; set; } = 40;

    public double ClimateMaxHumidity { get; set; } = 60;

    /// <summary>Hallen, die nur Zähler liefern (Halle B und C), und die Station, unter der ihre Tageskennzahlen stehen.</summary>
    public Dictionary<string, string> CounterStations { get; } = new(StringComparer.Ordinal) { ["B"] = "HALLE-B", ["C"] = "HALLE-C" };
}
