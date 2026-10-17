using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Kpi;

/// <summary>
/// Ist/Soll: Ist = gefertigte Stück (Gut + Ausschuss), Soll = Planmenge der Schicht zeitanteilig bis zum Datenstand.
/// Hochrechnung bis Schichtende = Ist × Schichtdauer / verstrichene Schichtzeit (lineare Fortschreibung des bisherigen
/// Tempos).
/// </summary>
public sealed record PlanFulfillmentResult(long Actual, decimal Planned)
{
    public double Ratio => Planned > 0 ? (double)(Actual / Planned) : 0;
}

public static class PlanFulfillment
{
    public static PlanFulfillmentResult From(ProductionFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        return new PlanFulfillmentResult(figures.TotalPieces, figures.PlannedQuantity);
    }

    /// <summary>Hochrechnung auf das Schichtende.</summary>
    public static double ProjectToShiftEnd(long actual, TimeSpan elapsed, TimeSpan shiftDuration) =>
        elapsed > TimeSpan.Zero ? actual * shiftDuration.TotalSeconds / elapsed.TotalSeconds : 0;

    /// <summary>
    /// Rückstand eines Auftrags = geplanter Fortschritt (linear zwischen Planstart und Planende) − gefertigte Menge.
    /// </summary>
    public static double OrderBacklog(
        int orderQuantity, int produced, DateTimeOffset plannedStartUtc, DateTimeOffset plannedEndUtc, DateTimeOffset nowUtc)
    {
        var total = (plannedEndUtc - plannedStartUtc).TotalSeconds;
        var elapsed = Math.Clamp((nowUtc - plannedStartUtc).TotalSeconds, 0, total);
        var plannedProgress = total > 0 ? orderQuantity * elapsed / total : orderQuantity;
        return plannedProgress - produced;
    }

    /// <summary>
    /// Stückverlust durch Taktverlust: Stück, die bei Soll-Takt statt Ist-Takt in der Laufzeit mehr entstanden wären.
    /// </summary>
    public static double SpeedLossPieces(TimeSpan runTime, double actualCycleSeconds, double targetCycleSeconds) =>
        actualCycleSeconds <= 0 || targetCycleSeconds <= 0
            ? 0
            : runTime.TotalSeconds * ((1 / targetCycleSeconds) - (1 / actualCycleSeconds));
}
