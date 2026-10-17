using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Rollup;

/// <summary>Soll-Füllgewicht und Toleranz eines Artikels (Grundlage des Histogramms „Füllgewicht in Toleranzen“).</summary>
public sealed record FillSpec(double TargetGrams, double ToleranceGrams);

/// <summary>Eingebaute Pressform: Ab dem Einbau zählen die Takte des Rundtischs als Hübe (Takte / Formnester).</summary>
public sealed record MouldMount(string MouldId, string StationId, int Nests, DateTimeOffset MountedUtc);

/// <summary>
/// Alles aus den Stammdaten, was die Verdichtung braucht: Schichtkalender, Kostensätze (Ausschusskosten im Zähler), Füllgewichte der
/// Artikel und eingebaute Formen. Die Stammdaten kennen den Server; die Bibliothek bekommt sie nur über diesen Vertrag.
/// </summary>
public sealed record RollupContext(
    ShiftCalendar Calendar,
    CostRateTable Costs,
    IReadOnlyDictionary<string, FillSpec> Fill,
    IReadOnlyList<MouldMount> Moulds,
    string HallId = PlantLayout.HallA);

/// <summary>Quelle des <see cref="RollupContext"/>; wird beim ersten Fenster und nach einem Neuaufbau gelesen.</summary>
public interface IRollupContextSource
{
    Task<RollupContext> GetAsync(CancellationToken cancellationToken);
}
