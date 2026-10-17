using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>Ofencharge mit Platz, Durchmesser und Belegungszeit.</summary>
public sealed record OvenChargeInfo(string ChargeId, string OvenId, int Place, int DiameterMm, int Plates, DateTimeOffset LoadedUtc, DateTimeOffset UnloadedUtc);

/// <summary>
/// Aushärteofen: periodische Beladung während der Produktionszeit (Taktung aus den Stammdaten), Aushärteprofil
/// Aufheizen/Halten/Abkühlen, Verzögerungen aus dem Drehbuch. Liefert Belegung, Zonentemperaturen und Chargen.
/// </summary>
internal sealed class OvenModel
{
    private const double StandbyCelsius = 60;
    private const double ColdCelsius = 25;
    private const double ZoneSigma = 0.4;
    private const double DeviationCelsius = 6;
    private static readonly TimeSpan s_horizon = TimeSpan.FromDays(56);
    private static readonly TimeSpan s_lotLookback = TimeSpan.FromMinutes(60);

    private readonly ScenarioDefinition _scenario;
    private readonly ProductionShiftIndex _shifts;
    private readonly int _seed;
    private readonly IReadOnlyList<PlannedLoss> _losses;
    private readonly Func<int, DateTimeOffset, DateTimeOffset, IReadOnlyList<string>> _pressLots;

    public OvenModel(
        ScenarioDefinition scenario,
        OvenSpec spec,
        ProductionShiftIndex shifts,
        int seed,
        Func<int, DateTimeOffset, DateTimeOffset, IReadOnlyList<string>> pressLots)
    {
        _scenario = scenario;
        Spec = spec;
        _shifts = shifts;
        _seed = seed;
        _pressLots = pressLots;
        _losses = [.. scenario.Losses.Where(l => l.StationId == spec.StationId).Select(PlannedLoss.From)];
        Charges = BuildCharges();
    }

    public OvenSpec Spec { get; }

    public IReadOnlyList<OvenChargeInfo> Charges { get; }

    public int OccupiedAt(DateTimeOffset instant) => Charges.Count(c => c.LoadedUtc <= instant && instant < c.UnloadedUtc);

    public IEnumerable<OvenChargeInfo> ActiveAt(DateTimeOffset instant) =>
        Charges.Where(c => c.LoadedUtc <= instant && instant < c.UnloadedUtc);

    public IReadOnlyList<string> ChargesUnloadedBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        [.. Charges.Where(c => c.UnloadedUtc >= fromUtc && c.UnloadedUtc < toUtc).Select(c => c.ChargeId)];

    public List<HistoryRecord> Generate(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        var records = new List<HistoryRecord>();
        var window = new TimeWindow(fromUtc, untilUtc);

        // Öfen sind eingeplant (Schichtzeit für Störungskennzahlen), haben aber keine Stückplanung.
        foreach (var shift in _shifts.Between(fromUtc, untilUtc).Where(s => window.Contains(s.StartUtc)))
        {
            records.Add(new ShiftPlanRecord(Spec.StationId, shift.StartUtc, shift.Key, shift.StartUtc, shift.EndUtc, 0));
        }

        foreach (var charge in Charges.Where(c => window.Contains(c.UnloadedUtc)))
        {
            var lots = _pressLots(charge.DiameterMm, charge.LoadedUtc - s_lotLookback, charge.LoadedUtc);
            records.Add(new OvenCharge(Spec.StationId, charge.UnloadedUtc, charge.ChargeId, charge.Place, charge.DiameterMm,
                charge.Plates, charge.LoadedUtc, charge.UnloadedUtc, lots, RotationTestPassed: true, BreakTestPassed: true));
        }

        foreach (var loss in _losses)
        {
            ProductionStationModel.AddVersions(loss, window, records);
            if (loss.Category == LossCategory.Breakdown) AlarmVersions.AddFaultAlarm(loss, window, records);
        }

        foreach (var alarm in _scenario.Alarms.Where(a => a.StationId == Spec.StationId))
        {
            AlarmVersions.AddScripted(alarm, window, records);
        }

        AddStateChanges(window, records);
        AddMinuteValues(window, records);
        return records;
    }

    public OperatingState StateAt(DateTimeOffset instant)
    {
        if (_losses.Any(l => l.IsStandstill && instant >= l.StartUtc && instant < l.EndUtc)) return OperatingState.Fault;
        return OccupiedAt(instant) > 0 ? OperatingState.Running : OperatingState.Idle;
    }

    private void AddStateChanges(TimeWindow window, List<HistoryRecord> records)
    {
        var instants = Charges.SelectMany(c => new[] { c.LoadedUtc, c.UnloadedUtc })
            .Concat(_losses.SelectMany(l => new[] { l.StartUtc, l.EndUtc }))
            .Append(_scenario.HistoryStartUtc)
            .Where(window.Contains)
            .Distinct()
            .Order();
        foreach (var instant in instants)
        {
            var state = StateAt(instant);
            if (instant != _scenario.HistoryStartUtc && state == StateAt(instant - TimeSpan.FromTicks(1))) continue;
            records.Add(new StateChange(Spec.StationId, instant, state, null));
        }
    }

    private void AddMinuteValues(TimeWindow window, List<HistoryRecord> records)
    {
        var minute = TimeSpan.FromMinutes(1);
        for (var t = ProductionStationModel.MinuteCeiling(window.StartUtc); t < window.EndUtc; t += minute)
        {
            var occupied = OccupiedAt(t);
            var noise = MinuteNoise.For(_seed, Spec.StationId, t);
            var deviation = _losses.Any(l => l.Category == LossCategory.Breakdown && t >= l.StartUtc && t < l.EndUtc) ? DeviationCelsius : 0;
            var baseline = occupied > 0 ? Spec.HoldTemperatureCelsius : _shifts.At(t) is null ? ColdCelsius : StandbyCelsius;
            records.Add(new MinuteValue(Spec.StationId, t, MinuteVariables.OccupiedPlaces, occupied));
            records.Add(new MinuteValue(Spec.StationId, t, MinuteVariables.Zone1Temperature, Math.Round(baseline + noise.Next(ZoneSigma), 1)));
            records.Add(new MinuteValue(Spec.StationId, t, MinuteVariables.Zone2Temperature, Math.Round(baseline - deviation + noise.Next(ZoneSigma), 1)));
            records.Add(new MinuteValue(Spec.StationId, t, MinuteVariables.Zone3Temperature, Math.Round(baseline + noise.Next(ZoneSigma), 1)));
        }
    }

    private List<OvenChargeInfo> BuildCharges()
    {
        var charges = new List<OvenChargeInfo>();
        var start = _scenario.HistoryStartUtc;
        var firstIndex = (long)Math.Floor((start - Spec.LoadAnchorUtc) / Spec.LoadInterval);
        var lastIndex = (long)Math.Ceiling((start + s_horizon - Spec.LoadAnchorUtc) / Spec.LoadInterval);
        for (var k = firstIndex; k <= lastIndex; k++)
        {
            var loaded = Spec.LoadAnchorUtc + (Spec.LoadInterval * k);
            if (loaded < start || _shifts.At(loaded) is null) continue;
            var unloaded = loaded + Spec.CureTime;
            foreach (var delay in _scenario.OvenDelays.Where(d => d.StationId == Spec.StationId && d.AtUtc >= loaded && d.AtUtc < unloaded))
            {
                unloaded += delay.Delay;
            }

            var place = (int)(((k % Spec.Places) + Spec.Places) % Spec.Places) + 1;
            var local = _scenario.Calendar.ToLocal(loaded).ToString("yyMMdd-HHmm", CultureInfo.InvariantCulture);
            charges.Add(new OvenChargeInfo($"{Spec.StationId}-C{local}", Spec.StationId, place, Spec.DiameterMm,
                Spec.PlatesPerPlace, loaded, unloaded));
        }

        return charges;
    }
}
