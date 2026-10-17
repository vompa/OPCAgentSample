using OPCAgentSample.Domain;

namespace OPCAgentSample.Rollup;

/// <summary>
/// Die abgeschlossenen Schichten (und direkt gelieferte Tageswerte) einer Art von Summen im Speicher des Dienstes. Daraus entstehen
/// Tag, Woche, Monat, Quartal und Jahr als „Summe der Kinder“: Tag = Summe seiner Schichten (oder der direkt gelieferte Tageswert),
/// alle höheren Ebenen = Summe ihrer Tage. Die laufende Schicht wird beim Summieren zusätzlich übergeben.
/// </summary>
/// <typeparam name="TValue">Additive Größe (Zähler einer Station, Statistik einer Reihe).</typeparam>
internal sealed class PeriodSums<TValue>(ShiftCalendar calendar, Func<string, TValue> create, Action<TValue, TValue> merge)
    where TValue : class
{
    private readonly Dictionary<(string Owner, string ShiftKey), TValue> _shifts = [];
    private readonly Dictionary<(string Owner, string DayKey), TValue> _directDays = [];
    private readonly Dictionary<string, SortedSet<string>> _shiftKeysByDay = [];
    private readonly Dictionary<(RollupLevel Level, string Key), SortedSet<string>> _daysByParent = [];

    public TValue? Shift(string owner, string shiftKey) => _shifts.GetValueOrDefault((owner, shiftKey));

    public IEnumerable<(string Owner, string ShiftKey)> ShiftKeys => _shifts.Keys;

    public void SetShift(string owner, string shiftKey, TValue value)
    {
        _shifts[(owner, shiftKey)] = value;
        var dayKey = PeriodKeys.Day(PeriodKeys.DayOfShift(shiftKey));
        if (!_shiftKeysByDay.TryGetValue(dayKey, out var keys)) _shiftKeysByDay[dayKey] = keys = [];
        keys.Add(shiftKey);
        RegisterDay(dayKey);
    }

    public void SetDirectDay(string owner, string dayKey, TValue value)
    {
        _directDays[(owner, dayKey)] = value;
        RegisterDay(dayKey);
    }

    /// <summary>Meldet einen Tag an, damit er in den Summen seiner Eltern erscheint (auch wenn erst die laufende Schicht ihn trägt).</summary>
    public void RegisterDay(string dayKey)
    {
        var day = PeriodKeys.ParseDay(dayKey);
        foreach (var level in RollupLevels.AboveDay)
        {
            var key = (level, PeriodKeys.Of(level, day, calendar));
            if (!_daysByParent.TryGetValue(key, out var days)) _daysByParent[key] = days = [];
            days.Add(dayKey);
        }
    }

    /// <summary>Summe eines Tages; <paramref name="running"/> ersetzt die gleichnamige abgeschlossene Schicht (laufende Schicht).</summary>
    public TValue? Day(string owner, string dayKey, (string ShiftKey, TValue Value)? running = null)
    {
        TValue? sum = null;
        if (_directDays.TryGetValue((owner, dayKey), out var direct)) sum = Add(sum, direct, owner);

        if (_shiftKeysByDay.TryGetValue(dayKey, out var keys))
        {
            foreach (var shiftKey in keys)
            {
                if (running is { } r && r.ShiftKey == shiftKey) continue;
                if (_shifts.TryGetValue((owner, shiftKey), out var shift)) sum = Add(sum, shift, owner);
            }
        }

        if (running is { } extra && PeriodKeys.Day(PeriodKeys.DayOfShift(extra.ShiftKey)) == dayKey) sum = Add(sum, extra.Value, owner);
        return sum;
    }

    /// <summary>Summe einer Periode oberhalb des Tags aus ihren Tagen.</summary>
    public TValue? Parent(string owner, RollupLevel level, string key, (string ShiftKey, TValue Value)? running = null)
    {
        if (!_daysByParent.TryGetValue((level, key), out var days)) return null;

        TValue? sum = null;
        foreach (var dayKey in days)
        {
            if (Day(owner, dayKey, running) is { } day) sum = Add(sum, day, owner);
        }

        return sum;
    }

    private TValue Add(TValue? sum, TValue part, string owner)
    {
        sum ??= create(owner);
        merge(sum, part);
        return sum;
    }
}
