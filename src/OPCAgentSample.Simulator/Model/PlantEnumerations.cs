using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Simulator.Model;

/// <summary>Enum-Datentypen des Informationsmodells mit ihren Anzeigetexten (Index = Zahlenwert).</summary>
internal static class PlantEnumerations
{
    public const string OperatingStateName = "Betriebszustand";
    public const string ScrapGradeName = "Ausschussgrund";

    /// <summary>Texte in der Reihenfolge von <see cref="OperatingState"/> (Spezifikation Abschnitt 4).</summary>
    public static IReadOnlyList<string> OperatingStates { get; } = [.. Enum.GetValues<OperatingState>().Select(OperatingStateText)];

    /// <summary>0 = Gut, danach die Werte von <see cref="ScrapReason"/> plus eins.</summary>
    public static IReadOnlyList<string> ScrapGrades { get; } =
        ["Gut", .. Enum.GetValues<ScrapReason>().Select(ScrapReasonText)];

    private static string OperatingStateText(OperatingState state) => state switch
    {
        OperatingState.Running => "Läuft",
        OperatingState.Idle => "Leerlauf",
        OperatingState.Setup => "Rüsten",
        OperatingState.Fault => "Störung",
        OperatingState.PlannedMaintenance => "WartungGeplant",
        OperatingState.WaitingForMaterial => "WartetMaterial",
        OperatingState.WaitingForUpstream => "WartetVorstufe",
        OperatingState.RunningWithoutOrder => "LäuftOhneAuftrag",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static string ScrapReasonText(ScrapReason reason) => reason switch
    {
        ScrapReason.Weight => "Gewicht",
        ScrapReason.Thickness => "Dicke",
        ScrapReason.Fabric => "Gewebe",
        ScrapReason.Label => "Etikett",
        ScrapReason.Breakage => "Bruch",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
