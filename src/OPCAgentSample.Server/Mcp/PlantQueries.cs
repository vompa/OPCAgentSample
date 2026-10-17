using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Mcp;

/// <summary>Zusammenfassung der Takte einer Station in einem Fenster (Mittelwerte, Ausschuss nach Grund).</summary>
public sealed record CycleAggregate(
    string StationId, long Count, long Scrap, double MeanCycleSeconds, double MeanBuildUpSeconds, double MeanFillWeightGrams,
    IReadOnlyDictionary<string, long> ScrapByReason)
{
    public double ScrapRate => Count > 0 ? (double)Scrap / Count : 0;
}

/// <summary>Verdichteter Abschnitt einer Zeitreihe.</summary>
public sealed record SeriesBucket(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Mean, double Min, double Max, long Count);

/// <summary>Ein Minutenwert mit Zeitpunkt.</summary>
public sealed record MinutePoint(DateTimeOffset At, double Value);

/// <summary>Rohe Stammdaten einer Pressform mit Hubzählung aus den Takten.</summary>
public sealed record MouldReading(string MouldId, string StationId, int Strokes, int StrokeLimit, double StrokesPerMinute, int AverageStrokesPerDay);

/// <summary>Namen der Reihen der Taktwerte je Variable (feste Liste, keine Eingabe des Modells im Schlüssel).</summary>
public static class CycleColumns
{
    public static string? Of(string normalizedVariable) => normalizedVariable switch
    {
        "taktzeit" or "takt" or "zykluszeit" => SeriesNames.CycleTime,
        "presskraft" => SeriesNames.PressForce,
        "presskraftaufbau" or "presskraftaufbauzeit" or "aufbauzeit" or "kraftaufbau" => SeriesNames.BuildUp,
        "fuellgewicht" or "gewicht" => SeriesNames.FillWeight,
        "dicke" or "scheibendicke" => SeriesNames.Thickness,
        _ => null,
    };
}

/// <summary>
/// Lesezugriffe auf die verdichteten Daten in Redis, die die Tools über die Kennzahlenblöcke hinaus brauchen (Prozesswerte, Ereignisse,
/// Alarme, Rückverfolgung). Es gibt nur lesende Abfragen; die Kennzahlen selbst rechnet die Kennzahlen-Bibliothek. Fenster, die nicht
/// auf Schicht- oder Tagesgrenzen liegen, sind 15 Minuten genau (bis zwei Tage zurück bei den Rundtischen eine Minute).
/// </summary>
public sealed class PlantQueries(RollupReader reader, MasterDataStore masterData, ShiftCalendar calendar)
{
    private static readonly TimeSpan s_strokeRateWindow = TimeSpan.FromMinutes(60);

    /// <summary>Verlustereignisse, die das Fenster berühren (neuester Stand je Id).</summary>
    public Task<IReadOnlyList<LossEvent>> LossEventsAsync(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string>? stations) =>
        reader.ReadLossesAsync(from, to, stations);

    /// <summary>Alarme, die im Fenster aktiv waren (aktiv seit vor dem Ende und nicht vor dem Beginn beendet).</summary>
    public Task<IReadOnlyList<AlarmRecord>> AlarmsAsync(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string>? stations) =>
        reader.ReadAlarmsAsync(from, to, stations);

    /// <summary>
    /// Takte je Station im Fenster: Anzahl, Ausschuss und Mittelwerte der Prozesswerte aus der Statistik; der Ausschuss nach Grund
    /// stammt aus den Schichten, die im Fenster beginnen und enden (die laufende Schicht zählt, wenn das Fenster bis zum Datenstand reicht).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, CycleAggregate>> CycleAggregatesAsync(
        DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> stations)
    {
        var latest = await reader.ReadOverallWatermarkAsync() ?? to;
        var shifts = calendar.ProductionShiftsBetween(from, to).Where(s => s.StartUtc >= from && (s.EndUtc <= to || to >= latest)).ToList();
        var reasons = await reader.ReadCountersAsync(stations.SelectMany(station => shifts.Select(s => (station, RollupLevel.Shift, s.Key))));
        var result = new Dictionary<string, CycleAggregate>();
        foreach (var station in stations)
        {
            var cycleTime = await reader.ReadRangeAsync(station, SeriesNames.CycleTime, from, to, calendar);
            if (cycleTime.Count == 0) continue;

            var buildUp = await reader.ReadRangeAsync(station, SeriesNames.BuildUp, from, to, calendar);
            var fill = await reader.ReadRangeAsync(station, SeriesNames.FillWeight, from, to, calendar);
            var scrap = await reader.ReadRangeAsync(station, SeriesNames.Scrap, from, to, calendar);
            var byReason = new Dictionary<string, long>();
            foreach (var row in reasons.Where(r => r.Station == station))
            {
                foreach (var (reason, count) in row.Totals.ScrapByReason) byReason[reason] = byReason.GetValueOrDefault(reason) + count;
            }

            result[station] = new CycleAggregate(
                station, cycleTime.Count, (long)Math.Round(scrap.Sum), cycleTime.Mean, buildUp.Mean, fill.Mean, byReason);
        }

        return result;
    }

    /// <summary>Verdichteter Verlauf eines Taktwerts (Reihe aus <see cref="CycleColumns"/>) in gleich langen Abschnitten.</summary>
    public Task<IReadOnlyList<SeriesBucket>> CycleBucketsAsync(
        string stationId, string series, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset? origin = null) =>
        BucketsAsync(stationId, series, from, to, bucket, origin ?? from);

    /// <summary>Verdichteter Verlauf eines Minutenwerts.</summary>
    public Task<IReadOnlyList<SeriesBucket>> MinuteBucketsAsync(
        string stationId, string variable, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset? origin = null) =>
        BucketsAsync(stationId, variable, from, to, bucket, origin ?? from);

    private async Task<IReadOnlyList<SeriesBucket>> BucketsAsync(
        string stationId, string series, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset origin)
    {
        var latest = await reader.ReadOverallWatermarkAsync() ?? to;
        var buckets = await reader.ReadBucketsAsync(stationId, series, from, to, bucket, origin, latest, calendar);
        return [.. buckets.Where(b => b.Stat.Count > 0).Select(b => new SeriesBucket(b.StartUtc, b.EndUtc, b.Stat.Mean, b.Stat.Min, b.Stat.Max, b.Stat.Count))];
    }

    /// <summary>Minutenwerte mehrerer Stationen und Variablen im Fenster (die Minutenreihen halten vier Stunden), nach Zeit sortiert.</summary>
    public async Task<IReadOnlyDictionary<(string Station, string Variable), List<MinutePoint>>> MinuteSeriesAsync(
        IReadOnlyCollection<string> stations, IReadOnlyCollection<string> variables, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new Dictionary<(string, string), List<MinutePoint>>();
        foreach (var station in stations)
        {
            foreach (var variable in variables)
            {
                var minutes = await reader.ReadMinutesAsync(station, variable, from, to);
                if (minutes.Count > 0) result[(station, variable)] = [.. minutes.Select(m => new MinutePoint(m.At, m.Value))];
            }
        }

        return result;
    }

    /// <summary>Körnung (z. B. A60) der zuletzt verarbeiteten Mischungscharge je Rundtisch.</summary>
    public async Task<IReadOnlyDictionary<string, (string Batch, string Grit)>> LatestBatchesAsync(
        IReadOnlyCollection<string> stations, DateTimeOffset until)
    {
        var data = await masterData.GetAsync();
        var result = new Dictionary<string, (string, string)>();
        foreach (var station in stations)
        {
            var cycles = await reader.ReadRecentCyclesAsync(station, until, count: 1);
            if (cycles.Count == 0) continue;

            var batch = cycles[^1].MixtureBatchId;
            if (data.GritOfBatch(batch) is { } grit) result[station] = (batch, grit);
        }

        return result;
    }

    /// <summary>Hübe der aktuellen Form je Rundtisch (Takte seit dem Einbau / Formnester) und das Hubtempo der letzten Stunde.</summary>
    public async Task<IReadOnlyList<MouldReading>> MouldsAsync(DateTimeOffset until)
    {
        var data = await masterData.GetAsync();
        var current = data.Moulds
            .Where(m => m.MountedAtUtc <= until)
            .GroupBy(m => m.StationId)
            .Select(g => g.OrderByDescending(m => m.MountedAtUtc).First())
            .OrderBy(m => m.StationId, StringComparer.Ordinal);
        var result = new List<MouldReading>();
        foreach (var mould in current)
        {
            var total = await reader.ReadMouldCyclesAsync(mould.Id);
            var recent = await CyclesAsync(mould.StationId, until - s_strokeRateWindow, until);
            result.Add(new MouldReading(
                mould.Id, mould.StationId, mould.Nests > 0 ? (int)(total / mould.Nests) : 0, mould.StrokeLimit,
                mould.Nests > 0 ? recent / s_strokeRateWindow.TotalMinutes / mould.Nests : 0, mould.AverageStrokesPerDay));
        }

        return result;
    }

    /// <summary>Gefertigte Takte je Auftrag seit dessen Start (Takte mit der Auftragsnummer, gezählt mit dem Aufnahmefenster).</summary>
    public Task<long> OrderCyclesAsync(string orderId) => reader.ReadOrderCyclesAsync(orderId);

    /// <summary>Takte einer Station im Fenster (aktuelles Tempo für Restlaufzeiten).</summary>
    public async Task<long> CyclesAsync(string stationId, DateTimeOffset from, DateTimeOffset to)
    {
        var info = SeriesCatalog.For(stationId, SeriesNames.CycleTime);
        var slots = await reader.ReadSlotsAsync(info, fine: true, from, to, calendar);
        return slots.Sum(s => s.Stat.Count);
    }

    /// <summary>Zeitpunkt (15 Minuten genau, bei Rundtischen bis zwei Tage zurück eine Minute) und Wert des Höchststands einer Reihe im Fenster.</summary>
    public async Task<(DateTimeOffset At, double Value)?> PeakAsync(string stationId, string variable, DateTimeOffset from, DateTimeOffset to)
    {
        var info = SeriesCatalog.For(stationId, variable);
        var slots = await reader.ReadSlotsAsync(info, fine: true, from, to, calendar);
        var peak = slots.Where(s => s.Stat.Count > 0).OrderByDescending(s => s.Stat.Max).ThenBy(s => s.StartUtc).FirstOrDefault();
        return peak is null ? null : (peak.StartUtc, peak.Stat.Max);
    }

    /// <summary>Belegte Platzminuten je Ofen im Fenster (Summe der Minutenwerte „BelegtePlaetze“ und Anzahl der Minuten).</summary>
    public async Task<IReadOnlyDictionary<string, (double Occupied, long Minutes)>> OvenOccupancyAsync(
        IReadOnlyCollection<string> ovens, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new Dictionary<string, (double, long)>();
        foreach (var oven in ovens)
        {
            var stat = await reader.ReadRangeAsync(oven, MinuteVariables.OccupiedPlaces, from, to, calendar);
            if (stat.Count > 0) result[oven] = (stat.Sum, stat.Count);
        }

        return result;
    }

    /// <summary>Betriebszustand einer Station zu einem Zeitpunkt (letzter Zustandswechsel davor).</summary>
    public async Task<OperatingState?> StateAtAsync(string stationId, DateTimeOffset at) => (await reader.ReadStateAtAsync(stationId, at))?.State;

    /// <summary>Ofenchargen im Fenster (Entladezeit), neueste zuerst; mit <paramref name="chargeId"/> nur diese Charge.</summary>
    public async Task<IReadOnlyList<OvenChargeRow>> OvenChargesAsync(DateTimeOffset from, DateTimeOffset to, string? ovenId, string? chargeId)
    {
        var charges = chargeId is not null
            ? await reader.ReadChargesAsync([chargeId])
            : [.. (await reader.ReadChargesUnloadedSinceAsync(from)).Where(c => c.LoadedUtc < to && (ovenId is null || c.StationId == ovenId))];
        return [.. charges.Select(c => new OvenChargeRow(
            c.ChargeId, c.StationId, c.Place, c.DiameterMm, c.Plates, c.LoadedUtc, c.UnloadedUtc, c.RotationTestPassed, c.BreakTestPassed))];
    }

    // ---- Rückverfolgung -------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PressLotRow>> PressLotsAsync(string? batchId, IReadOnlyCollection<string>? lotIds)
    {
        var lots = new Dictionary<string, PressLot>();
        if (batchId is not null)
        {
            foreach (var lot in await reader.ReadLotsOfBatchAsync(batchId)) lots[lot.LotId] = lot;
        }

        if (lotIds is not null)
        {
            foreach (var lot in await reader.ReadLotsAsync(lotIds)) lots[lot.LotId] = lot;
        }

        return [.. lots.Values
            .OrderBy(l => l.StartUtc).ThenBy(l => l.LotId, StringComparer.Ordinal)
            .Select(l => new PressLotRow(l.LotId, l.StationId, l.MixtureBatchId, l.ArticleId, l.ShiftKey, l.StartUtc, l.EndUtc, l.GoodPieces))];
    }

    /// <summary>Verknüpfungen Ofencharge ↔ Pressenlos (beide Richtungen, je nach gesetztem Filter).</summary>
    public Task<IReadOnlyList<(string ChargeId, string LotId)>> ChargeLotsAsync(
        IReadOnlyCollection<string>? lotIds, IReadOnlyCollection<string>? chargeIds) =>
        reader.ReadChargeLotsAsync(lotIds, chargeIds);

    public async Task<IReadOnlyList<PalletRow>> PalletsAsync(IReadOnlyCollection<string>? chargeIds, IReadOnlyCollection<string>? palletIds)
    {
        var pallets = new Dictionary<string, PalletRecord>();
        if (chargeIds is not null)
        {
            foreach (var pallet in await reader.ReadPalletsOfChargesAsync(chargeIds)) pallets[pallet.PalletId] = pallet;
        }

        if (palletIds is not null)
        {
            foreach (var pallet in await reader.ReadPalletsAsync(palletIds)) pallets[pallet.PalletId] = pallet;
        }

        return [.. pallets.Values
            .OrderBy(p => p.TimestampUtc).ThenBy(p => p.PalletId, StringComparer.Ordinal)
            .Select(p => new PalletRow(p.PalletId, p.StationId, p.ArticleId, p.Cartons, p.PiecesPerCarton, p.ShippedUtc, p.TimestampUtc, p.OvenChargeIds))];
    }

    public Task<bool> ExistsAsync(TraceKind kind, string id) => reader.TraceExistsAsync(kind, id);

    /// <summary>Gibt es die Mischungscharge in den Stammdaten?</summary>
    public async Task<bool> BatchExistsAsync(string batchId) => (await masterData.GetAsync()).MixtureBatches.ContainsKey(batchId);
}

public sealed record OvenChargeRow(
    string ChargeId, string OvenId, int Place, int DiameterMm, int Plates, DateTimeOffset LoadedUtc, DateTimeOffset UnloadedUtc,
    bool RotationTestPassed, bool BreakTestPassed);

public sealed record PressLotRow(
    string LotId, string StationId, string BatchId, string ArticleId, string ShiftKey, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int GoodPieces);

public sealed record PalletRow(
    string PalletId, string StationId, string ArticleId, int Cartons, int PiecesPerCarton, DateTimeOffset? ShippedUtc,
    DateTimeOffset CreatedUtc, IReadOnlyList<string> ChargeIds);
