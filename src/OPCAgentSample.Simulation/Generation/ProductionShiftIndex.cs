using OPCAgentSample.Domain;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>Sortierte Liste der Produktionsschichten ab Historienbeginn (wächst bei Bedarf); schnelle Suche je Zeitpunkt.</summary>
internal sealed class ProductionShiftIndex
{
    private static readonly TimeSpan s_chunk = TimeSpan.FromDays(28);

    private readonly ShiftCalendar _calendar;
    private readonly DateTimeOffset _startUtc;
    private readonly List<ProductionPeriod> _shifts = [];
    private readonly Lock _lock = new();
    private DateTimeOffset _coveredUntilUtc;

    public ProductionShiftIndex(ShiftCalendar calendar, DateTimeOffset startUtc)
    {
        _calendar = calendar;
        _startUtc = startUtc;
        _coveredUntilUtc = startUtc;
    }

    public IReadOnlyList<ProductionPeriod> Between(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        EnsureCovered(toUtc);
        lock (_lock)
        {
            return [.. _shifts.Where(s => s.EndUtc > fromUtc && s.StartUtc < toUtc)];
        }
    }

    /// <summary>Produktionsschicht, die den Zeitpunkt enthält, sonst <c>null</c>.</summary>
    public ProductionPeriod? At(DateTimeOffset instant)
    {
        EnsureCovered(instant + TimeSpan.FromDays(1));
        lock (_lock)
        {
            var index = IndexOfLastStartAtOrBefore(instant);
            return index >= 0 && _shifts[index].Contains(instant) ? _shifts[index] : null;
        }
    }

    /// <summary>Letzte Produktionsschicht, die vor oder zu dem Zeitpunkt begonnen hat.</summary>
    public ProductionPeriod? LastStartedAtOrBefore(DateTimeOffset instant)
    {
        EnsureCovered(instant + TimeSpan.FromDays(1));
        lock (_lock)
        {
            var index = IndexOfLastStartAtOrBefore(instant);
            return index >= 0 ? _shifts[index] : null;
        }
    }

    private int IndexOfLastStartAtOrBefore(DateTimeOffset instant)
    {
        int low = 0, high = _shifts.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_shifts[middle].StartUtc <= instant)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private void EnsureCovered(DateTimeOffset untilUtc)
    {
        lock (_lock)
        {
            while (_coveredUntilUtc < untilUtc)
            {
                var next = _coveredUntilUtc + s_chunk;
                foreach (var shift in _calendar.ProductionShiftsBetween(_coveredUntilUtc, next))
                {
                    if (shift.StartUtc >= _coveredUntilUtc && shift.StartUtc >= _startUtc) _shifts.Add(shift);
                }

                _coveredUntilUtc = next;
            }
        }
    }
}
