using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Mcp;

/// <summary>Ein Fertigungsauftrag mit gerechnetem Fortschritt, Tempo, voraussichtlichem Ende und Bewertung.</summary>
public sealed record OrderView(
    ProductionOrder Order,
    Article? Article,
    long Produced,
    double RatePerHour,
    DateTimeOffset? ExpectedEndUtc,
    double HoursNeeded,
    double HoursAvailable,
    string Rating,
    string Reason)
{
    public double Progress => Order.TargetQuantity > 0 ? (double)Produced / Order.TargetQuantity : 0;

    public long Remaining => Math.Max(0, Order.TargetQuantity - Produced);

    public string ArticleLabel => Article is null ? Order.ArticleId : $"Ø {Article.DiameterMm} {Article.Grit}";
}

/// <summary>
/// Auftragsfortschritt aus den Auftragsdaten und den Takten der Station: gefertigte Menge, aktuelles Tempo, voraussichtliches
/// Ende und Bewertung. Aufträge einer Station werden mit ihrem eigenen Tempo bewertet; Aufträge ohne Station (Halle B/C)
/// teilen sich die Kapazität der Halle: Reicht die verfügbare Produktionszeit bis zum Termin nicht für alle zusammen, sind sie
/// gemeinsam gefährdet.
/// </summary>
public sealed class OrderService(
    MasterDataStore masterData, PlantQueries queries, ShiftCalendar calendar)
{
    private static readonly TimeSpan s_rateWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Aufträge der angegebenen Stationen und – ohne Station – der angegebenen Hallen (B/C). Mit <paramref name="orderId"/> nur
    /// dieser Auftrag, auch wenn er abgeschlossen ist; liegt er außerhalb der Sicht, fehlt er im Ergebnis.
    /// </summary>
    public async Task<IReadOnlyList<OrderView>> GetAsync(
        DateTimeOffset now, IReadOnlyCollection<string> stationIds, IReadOnlyCollection<string> counterHalls, string? orderId, CancellationToken ct)
    {
        var data = await masterData.GetAsync(ct);
        var articles = data.Articles;
        var orders = data.Orders
            .Where(o => orderId is null ? o.State != OrderState.Completed : o.Id == orderId)
            .OrderBy(o => o.DueUtc).ThenBy(o => o.Id, StringComparer.Ordinal)
            .Where(o => o.StationId is not null ? stationIds.Contains(o.StationId) : counterHalls.Contains(o.HallId))
            .ToList();

        var views = new List<OrderView>();
        var shared = new List<(ProductionOrder Order, long Produced, double Rate)>();
        foreach (var order in orders)
        {
            var article = articles.GetValueOrDefault(order.ArticleId);
            if (order.StationId is { } station)
            {
                views.Add(await StationOrderAsync(order, article, station, now, ct));
            }
            else
            {
                shared.Add((order, order.ProducedQuantity, HallRate(order, now)));
            }
        }

        views.AddRange(SharedCapacityOrders(shared, articles, now));
        return [.. views.OrderBy(v => v.Order.DueUtc).ThenBy(v => v.Order.Id, StringComparer.Ordinal)];
    }

    /// <summary>Verfügbare Produktionszeit (Stunden) zwischen zwei Zeitpunkten laut Schichtkalender.</summary>
    public double ProductionHours(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0;
        return calendar.ProductionShiftsBetween(from, to)
            .Sum(s => new TimeWindow(s.StartUtc, s.EndUtc).Intersect(from, to).Duration.TotalHours);
    }

    /// <summary>Zeitpunkt, an dem die angegebenen Produktionsstunden ab <paramref name="from"/> verbraucht sind (nur Schichten laut Kalender).</summary>
    public DateTimeOffset? AddProductionHours(DateTimeOffset from, double hours)
    {
        var remaining = TimeSpan.FromHours(hours);
        foreach (var shift in calendar.ProductionShiftsBetween(from, from.AddDays(60)))
        {
            var start = shift.StartUtc > from ? shift.StartUtc : from;
            var available = shift.EndUtc - start;
            if (available <= TimeSpan.Zero) continue;
            if (remaining <= available) return start + remaining;

            remaining -= available;
        }

        return null;
    }

    private async Task<OrderView> StationOrderAsync(
        ProductionOrder order, Article? article, string station, DateTimeOffset now, CancellationToken ct)
    {
        long produced = order.ProducedQuantity;
        var rate = 0.0;
        if (order.State == OrderState.Running && order.StartedAtUtc is not null)
        {
            produced = Math.Max(produced, await queries.OrderCyclesAsync(order.Id));
            rate = await queries.CyclesAsync(station, now - s_rateWindow, now) / s_rateWindow.TotalHours;
        }

        var remaining = Math.Max(0, order.TargetQuantity - produced);
        var needed = rate > 0 ? remaining / rate : 0;
        var available = ProductionHours(now, order.DueUtc);
        DateTimeOffset? end = rate > 0 ? AddProductionHours(now, needed) : null;
        var (rating, reason) = order.State == OrderState.Planned
            ? (Rating.Ok, "geplant, noch nicht gestartet")
            : Judge(remaining, rate, needed, available, order);
        return new OrderView(order, article, produced, rate, end, needed, available, rating, reason);
    }

    private static (string Rating, string Reason) Judge(long remaining, double rate, double needed, double available, ProductionOrder order)
    {
        if (remaining == 0) return (Rating.Ok, "Menge erreicht");
        if (rate <= 0) return (Rating.Warn, "Station fertigt gerade nicht");
        if (needed > available) return (Rating.Crit, $"Restmenge braucht {needed:0} h, bis zum Termin stehen {available:0} h zur Verfügung");
        if (order.Remark?.Contains("Rückstand", StringComparison.Ordinal) == true)
        {
            return (Rating.Warn, $"Rückstand, aber aufholbar: Restmenge braucht {needed:0} h von {available:0} h bis zum Termin");
        }

        return needed > 0.8 * available
            ? (Rating.Warn, $"knapp: Restmenge braucht {needed:0} h von {available:0} h bis zum Termin")
            : (Rating.Ok, $"im Plan: Restmenge braucht {needed:0} h von {available:0} h bis zum Termin");
    }

    /// <summary>Tempo eines Hallenauftrags ohne Station: bisher gefertigt je Produktionsstunde seit dem Start.</summary>
    private double HallRate(ProductionOrder order, DateTimeOffset now)
    {
        var hours = order.StartedAtUtc is { } start ? ProductionHours(start, now) : 0;
        return hours > 0 ? order.ProducedQuantity / hours : 0;
    }

    private IEnumerable<OrderView> SharedCapacityOrders(
        List<(ProductionOrder Order, long Produced, double Rate)> shared, IReadOnlyDictionary<string, Article> articles, DateTimeOffset now)
    {
        foreach (var hall in shared.GroupBy(s => s.Order.HallId))
        {
            var items = hall.Where(s => s.Order.State == OrderState.Running).ToList();
            var due = items.Count > 0 ? items.Max(s => s.Order.DueUtc) : now;
            var available = ProductionHours(now, due);
            var needed = items.Sum(s => s.Rate > 0 ? Math.Max(0, s.Order.TargetQuantity - s.Produced) / s.Rate : 0);
            var shortage = needed > available && items.Count > 1;
            var sequence = items.OrderByDescending(s => s.Order.TargetQuantity > 0 ? (double)s.Produced / s.Order.TargetQuantity : 0).ToList();

            foreach (var (order, produced, rate) in hall)
            {
                var article = articles.GetValueOrDefault(order.ArticleId);
                var remaining = Math.Max(0, order.TargetQuantity - produced);
                var own = rate > 0 ? remaining / rate : 0;
                DateTimeOffset? end = rate > 0 ? AddProductionHours(now, own) : null;
                var (rating, reason) = order.State == OrderState.Planned
                    ? (Rating.Ok, "geplant, noch nicht gestartet")
                    : shortage
                        ? (Rating.Crit,
                            $"gemeinsame Kapazität Halle {order.HallId}: alle laufenden Aufträge brauchen bei bisherigem Tempo {needed:0} h, "
                            + $"bis zum Termin stehen {available:0} h zur Verfügung; sie reicht nur für {ReachableCount(sequence, available)} von {items.Count}")
                        : Judge(remaining, rate, own, available, order);
                yield return new OrderView(order, article, produced, rate, end, own, available, rating, reason);
            }
        }
    }

    /// <summary>Wie viele Aufträge nacheinander (der am weitesten fortgeschrittene zuerst) in die verfügbare Zeit passen.</summary>
    private static int ReachableCount(List<(ProductionOrder Order, long Produced, double Rate)> sequence, double available)
    {
        var used = 0.0;
        var count = 0;
        foreach (var (order, produced, rate) in sequence)
        {
            used += rate > 0 ? Math.Max(0, order.TargetQuantity - produced) / rate : 0;
            if (used > available) break;
            count++;
        }

        return count;
    }
}
