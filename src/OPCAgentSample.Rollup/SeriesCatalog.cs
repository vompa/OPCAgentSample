using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Rollup;

/// <summary>Namen der Reihen, die nicht aus Minutenwerten stammen (Takte der Rundtische, Kartons der Verpackung).</summary>
public static class SeriesNames
{
    public const string CycleTime = "Taktzeit";

    /// <summary>Abweichung der Taktzeit vom Soll-Takt des Artikels in Prozent (Histogramm an den Toleranzgrenzen).</summary>
    public const string CycleDeviation = "Taktabweichung";
    public const string PressForce = "Presskraft";
    public const string BuildUp = "Presskraftaufbau";
    public const string FillWeight = "Fuellgewicht";
    public const string Thickness = "Dicke";

    /// <summary>1 je Ausschusstakt, sonst 0: Summe = Ausschuss, Anzahl = Takte (Ausschussquote für freie Fenster).</summary>
    public const string Scrap = "Ausschuss";

    public const string Cartons = "Kartons";

    /// <summary>Temperatur Zone 2 der Öfen nur in der Haltephase (ab 120 °C); kältere Werte (Bereitschaft, kalt) zählen hier nicht.</summary>
    public const string OvenHold = "Haltephase";

    /// <summary>Ab dieser Temperatur zählt ein Minutenwert der Zone 2 zur Haltephase.</summary>
    public const double OvenHoldFromCelsius = 120;
}

/// <summary>Toleranzbänder der Prozesswerte (Maschinen-Report); die Klassengrenzen liegen auf den Toleranzgrenzen.</summary>
public static class ProcessBands
{
    /// <summary>Taktzeit gegen Soll-Takt des Artikels in Prozent, Toleranz ±5 %.</summary>
    public static readonly HistogramSpec CycleDeviationPercent = new([-15, -5, -2.5, 0, 2.5, 5, 15], 2, 5);

    /// <summary>Füllgewicht als Abweichung vom Soll in Einheiten der Artikeltoleranz (±1 = Toleranzgrenze).</summary>
    public static readonly HistogramSpec FillWeightInTolerances = new([-2, -1, -2.0 / 3, -1.0 / 3, 0, 1.0 / 3, 2.0 / 3, 1, 2], 2, 7);

    /// <summary>Presskraftaufbau in Sekunden, Band 0,6 bis 1,0 s.</summary>
    public static readonly HistogramSpec BuildUpSeconds = new([0.4, 0.6, 0.7, 0.8, 0.9, 1.0, 1.2], 2, 5);

    /// <summary>Ofentemperatur Zone 2 in der Haltephase (ab 120 °C), Band 175 bis 185 °C.</summary>
    public static readonly HistogramSpec OvenZone2 = new([120, 165, 175, 180, 185, 195], 3, 4);

    /// <summary>Kartons je Minute im Verhältnis zur geplanten Rate, Band ab 90 %.</summary>
    public static readonly HistogramSpec PackagingRate = new([0.5, 0.9, 1.1], 2, 3);
}

/// <summary>Eine Reihe von Prozesswerten: Station und Variable, dazu das Histogramm (falls es eine Toleranz gibt).</summary>
public sealed record SeriesInfo(string Station, string Variable, HistogramSpec? Spec, bool HasFine)
{
    public int Bins => Spec?.Bins ?? 0;
}

/// <summary>
/// Verzeichnis der Reihen. Das 1-Minuten-Raster führen nur die Rundtische; alle übrigen Reihen beginnen bei 15 Minuten. Zähler
/// seit Schichtbeginn (Stück, Energie) sind keine Reihen.
/// </summary>
public static class SeriesCatalog
{
    public static SeriesInfo For(string station, string variable)
    {
        ArgumentException.ThrowIfNullOrEmpty(station);
        ArgumentException.ThrowIfNullOrEmpty(variable);
        var hasFine = PlantLayout.IsKnown(station) && PlantLayout.Get(station).Type == StationType.RotaryTable;
        var spec = variable switch
        {
            SeriesNames.CycleDeviation => ProcessBands.CycleDeviationPercent,
            SeriesNames.BuildUp => ProcessBands.BuildUpSeconds,
            SeriesNames.FillWeight => ProcessBands.FillWeightInTolerances,
            SeriesNames.Cartons => ProcessBands.PackagingRate,
            SeriesNames.OvenHold => ProcessBands.OvenZone2,
            _ => null,
        };
        return new SeriesInfo(station, variable, spec, hasFine);
    }

    /// <summary>Minutenwerte, die keine Reihe bilden (Zähler seit Schichtbeginn).</summary>
    public static bool IsCounter(string variable) =>
        variable is MinuteVariables.GoodCount or MinuteVariables.ScrapCount or MinuteVariables.EnergyKwh;
}

/// <summary>Kennung einer Reihe in den Summen im Speicher des Dienstes: <c>Station|Variable</c>.</summary>
internal static class StatOwner
{
    public static string Of(SeriesInfo info) => $"{info.Station}|{info.Variable}";

    public static SeriesInfo Info(string owner)
    {
        var parts = owner.Split('|', 2);
        return SeriesCatalog.For(parts[0], parts[1]);
    }
}
