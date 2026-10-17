using OPCAgentSample.Domain.History;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Erzeugt die Prozesswerte je Takt einer Schicht (Takt, Presskraft, Presskraftaufbau, Füllgewicht, Dicke, Ausschuss).
/// Je Takt werden immer gleich viele Zufallszahlen gezogen; die Werte einer Schicht sind daher unabhängig vom
/// Ausgabefenster.
/// </summary>
internal static class CycleValueGenerator
{
    private const double ThicknessScrapOffsetMm = 0.08;
    private const double WeightScrapMarginGrams = 0.2;

    private static readonly (double Upper, ScrapReason Reason)[] s_reasonWeights =
    [
        (0.30, ScrapReason.Thickness),
        (0.55, ScrapReason.Fabric),
        (0.75, ScrapReason.Breakage),
        (0.90, ScrapReason.Weight),
        (1.00, ScrapReason.Label),
    ];

    public static IEnumerable<CycleRecord> Cycles(ScenarioDefinition scenario, StationPlan plan, ShiftRun run, int seed)
    {
        var model = scenario.Model;
        var random = DeterministicRandom.For(seed, plan.StationId + "/takt", run.Shift.StartUtc.UtcTicks);
        for (var i = 0; i < run.CycleTicks.Length; i++)
        {
            var instant = new DateTimeOffset(run.CycleTicks[i], TimeSpan.Zero);
            var assignment = plan.AssignmentAt(instant);
            var article = scenario.Article(assignment.ArticleId);
            var seconds = (instant - run.Shift.StartUtc).TotalSeconds;

            var cycleNoise = random.BoundedGaussian(model.CycleNoiseSigma, model.CycleNoiseLimit);
            var forceNoise = random.Gaussian(model.PressForceSigma);
            var buildUpNoise = random.Gaussian(model.BuildUpSigmaSeconds);
            var weightNoise = random.Gaussian(model.FillWeightSigmaGrams);
            var thicknessNoise = random.Gaussian(model.ThicknessSigmaMm);
            var reasonPick = random.NextDouble();

            ScrapReason? scrap = run.Scrap[i] ? run.FixedReasons[i] ?? PickReason(reasonPick) : null;
            var weightTarget = article.FillWeightTargetGrams + plan.FillWeightOffsetAt(instant);
            var weight = scrap == ScrapReason.Weight
                ? article.FillWeightTargetGrams + article.FillWeightToleranceGrams + WeightScrapMarginGrams + Math.Abs(weightNoise)
                : weightTarget + weightNoise;
            var thickness = article.ThicknessMm + thicknessNoise
                + (scrap == ScrapReason.Thickness ? (reasonPick < 0.5 ? -ThicknessScrapOffsetMm : ThicknessScrapOffsetMm) : 0);

            yield return new CycleRecord(
                plan.StationId,
                instant,
                assignment.OrderId,
                article.Id,
                plan.BatchAt(instant),
                Math.Round(run.Profile.MeasuredCycle(seconds) * (1 + cycleNoise), 3),
                article.TargetCycleSeconds,
                Math.Round(article.PressForceKn * (1 + forceNoise), 1),
                Math.Round(plan.BuildUpAt(instant, model.BuildUpNominalSeconds) + buildUpNoise, 3),
                Math.Round(weight, 2),
                Math.Round(thickness, 3),
                scrap);
        }
    }

    private static ScrapReason PickReason(double pick) => s_reasonWeights.First(w => pick < w.Upper).Reason;
}
