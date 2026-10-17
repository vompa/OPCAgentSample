using OPCAgentSample.Domain;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Ingestion;

/// <summary>Stammdaten für die Verdichtung: Kostensätze, Füllgewichte der Artikel und eingebaute Formen.</summary>
public sealed class MasterDataRollupContext(MasterDataStore masterData, ShiftCalendar calendar) : IRollupContextSource
{
    public async Task<RollupContext> GetAsync(CancellationToken cancellationToken)
    {
        var data = await masterData.GetAsync(cancellationToken);
        return new RollupContext(
            calendar,
            data.CostTable(),
            data.Articles.Values.ToDictionary(a => a.Id, a => new FillSpec((double)a.FillWeightTargetGrams, (double)a.FillWeightToleranceGrams)),
            [.. data.Moulds.Select(m => new MouldMount(m.Id, m.StationId, m.Nests, m.MountedAtUtc))]);
    }
}
