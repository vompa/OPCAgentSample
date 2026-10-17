using OPCAgentSample.Domain;
using OPCAgentSample.Simulation.Script;

namespace OPCAgentSample.Simulation.Scenario;

/// <summary>Setzt Stammdaten und Drehbuch der Halle A zum Szenario zusammen.</summary>
public static class HallAScenario
{
    /// <summary>Fester Standard-Seed der Demo; derselbe Seed ergibt bitgleiche Datensätze.</summary>
    public const int DefaultSeed = 4211;

    public static ScenarioDefinition Create(ShiftCalendar? calendar = null)
    {
        var shiftCalendar = calendar ?? new ShiftCalendar();
        var at = new LocalClock(shiftCalendar);
        return new ScenarioDefinition
        {
            Calendar = shiftCalendar,
            HistoryStartUtc = at.Oct(5, "00:00"),
            DemoTimeUtc = SimulationClock.DemoStart,
            Articles = HallAMasterData.Articles,
            PackagingArticles = HallAMasterData.PackagingArticles,
            MixtureBatches = HallAMasterData.MixtureBatches(at),
            Orders = HallAMasterData.Orders(at),
            Moulds = HallAMasterData.Moulds(at),
            ProductionStations = HallAMasterData.ProductionStations,
            Ovens = HallAMasterData.Ovens(at),
            Consumables = HallAMasterData.Consumables,
            ClimateRange = HallAMasterData.ClimateRange,
            Model = HallAMasterData.Model,
            DailyHistoryStart = HallAMasterData.DailyHistoryStart,
            ReliabilityTargets = HallAMasterData.ReliabilityTargets,
            UnplannedShares = HallAMasterData.UnplannedShares,
            Losses = HallAScript.Losses(at),
            Setups = HallAScript.Setups(at),
            OrderAssignments = HallAScript.OrderAssignments(at),
            BatchAssignments = HallAScript.BatchAssignments(at),
            OutputTargets = HallAScript.OutputTargets(at),
            ScrapWindows = HallAScript.ScrapWindows(at),
            FillWeightShifts = HallAScript.FillWeightShifts(at),
            BuildUpDrifts = HallAScript.BuildUpDrifts(at),
            MixtureDeliveries = HallAScript.MixtureDeliveries(at),
            Alarms = HallAScript.Alarms(at),
            BufferPlates = HallAScript.BufferPlates(at),
            BufferMeanWait = HallAScript.BufferMeanWait(at),
            OvenDelays = HallAScript.OvenDelays(at),
            PlanOverrides = [],
            QuietWindows = HallAScript.QuietWindows(at),
        };
    }
}
