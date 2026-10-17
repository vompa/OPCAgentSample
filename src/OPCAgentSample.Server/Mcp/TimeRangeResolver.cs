using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Server.Mcp;

public enum RangeKind
{
    Shift,
    Day,
    Week,
    Custom,
}

/// <summary>
/// Aufgelöster Zeitraum eines Tools. <see cref="Periods"/> sind die logischen Bereiche (Schicht, Tag, Woche) des
/// Schichtkalenders, aus denen die Kennzahlenblöcke gelesen werden; bei freien Fenstern (z. B. letzte 24 h) ist die Liste leer.
/// </summary>
public sealed record ResolvedRange(
    string Label, DateTimeOffset StartUtc, DateTimeOffset EndUtc, RangeKind Kind, IReadOnlyList<ProductionPeriod> Periods)
{
    public TimeWindow Window => new(StartUtc, EndUtc);

    /// <summary>Der Zeitraum endet nicht vor dem Datenstand: Er enthält laufende Daten.</summary>
    public bool IsOpen(DateTimeOffset now) => EndUtc > now;

    public DateTimeOffset EffectiveEnd(DateTimeOffset now) => EndUtc < now ? EndUtc : now;
}

/// <summary>
/// Löst Zeitangaben der Tools in logische Bereiche des <see cref="ShiftCalendar"/> auf: „aktuelle Schicht“, „vorherige
/// Schicht“, „heute“, „gestern“, „diese Woche“, „Vorwoche“, „KW 42“, „KW 41–42“, „24 h“, „90 Tage“, „seit 06:00“,
/// „06:00–jetzt“, „Jul–Okt“ sowie von/bis (Datum oder Zeitpunkt in Ortszeit).
/// </summary>
public sealed partial class TimeRangeResolver(ShiftCalendar calendar)
{
    private static readonly string[] s_months =
        ["jan", "feb", "mae", "apr", "mai", "jun", "jul", "aug", "sep", "okt", "nov", "dez"];

    public const string AcceptedFormats =
        "aktuelle Schicht, vorherige Schicht, heute, gestern, diese Woche, Vorwoche, KW 42, KW 41-42, 24 h, 48 h, 90 Tage, "
        + "seit 06:00, 06:00-jetzt, Jul-Okt oder von/bis (2026-10-19)";

    /// <param name="text">Angabe des Modells; leer = <paramref name="defaultText"/>.</param>
    /// <param name="forward">Fenster wie „48 h“ und „7 Tage“ liegen in der Zukunft (Wartungshorizont) statt in der Vergangenheit.</param>
    public ResolvedRange Resolve(string? text, DateTimeOffset now, string? from = null, string? to = null, string defaultText = "heute", bool forward = false)
    {
        if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to)) return FromTo(from, to, now);

        var normalized = Normalize(string.IsNullOrWhiteSpace(text) ? defaultText : text);
        return TrySince(normalized, now)
            ?? TryRolling(normalized, now, forward)
            ?? TryShift(normalized, now)
            ?? TryDay(normalized, now)
            ?? TryWeek(normalized, now)
            ?? TryMonths(normalized, now)
            ?? throw new McpException($"Unbekannter Zeitraum '{text}'. Gültig: {AcceptedFormats}.");
    }

    /// <summary>Schichten für <c>get_shift_summary</c>: aktuell, vorherige, gestern/heute (alle Schichten), Früh/Spät/Nacht oder Schicht-Schlüssel.</summary>
    public IReadOnlyList<ProductionPeriod> ResolveShifts(string? text, DateTimeOffset now, bool all)
    {
        var normalized = Normalize(string.IsNullOrWhiteSpace(text) ? "aktuell" : text);
        var currentDay = calendar.ProductionDayOf(now);
        var current = calendar.ShiftPeriodOf(now);

        var key = ShiftKeyPattern().Match(normalized);
        if (key.Success)
        {
            var day = DateOnly.ParseExact(key.Groups["day"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return [calendar.ShiftPeriod(day, ShiftOfCode(key.Groups["code"].Value))];
        }

        if (normalized is "aktuell" or "aktuelle" or "aktuelle schicht" or "schicht" or "laufende schicht" or "diese schicht" or "jetzt")
        {
            return all ? ShiftsOfDay(currentDay, now) : [current];
        }

        if (normalized is "vorherige" or "vorherige schicht" or "letzte schicht" or "vorschicht" or "vorherig")
        {
            return [PreviousShift(current)];
        }

        if (normalized is "heute" or "tag")
        {
            return ShiftsOfDay(currentDay, now);
        }

        if (normalized is "gestern")
        {
            return ShiftsOfDay(PreviousProductionDay(currentDay), now);
        }

        var kind = normalized.Replace("schicht", string.Empty, StringComparison.Ordinal).Trim();
        if (kind is "frueh" or "spaet" or "nacht")
        {
            var shift = kind switch { "frueh" => Shift.Early, "spaet" => Shift.Late, _ => Shift.Night };
            for (var day = currentDay; day >= currentDay.AddDays(-7); day = day.AddDays(-1))
            {
                if (!calendar.IsProductionDay(day)) continue;
                var period = calendar.ShiftPeriod(day, shift);
                if (period.StartUtc <= now) return [period];
            }
        }

        throw new McpException($"Unbekannte Schicht '{text}'. Gültig: aktuelle, vorherige, heute, gestern, Früh, Spät, Nacht oder ein Schlüssel wie 2026-10-14N.");
    }

    /// <summary>Wochentag-Schlüssel der Woche (ISO) zu einer Wochennummer des Jahres des Datenstands.</summary>
    public ProductionPeriod WeekOfYear(int week, DateTimeOffset now)
    {
        var year = ISOWeek.GetYear(calendar.ToLocal(now).DateTime);
        if (week is < 1 or > 53) throw new McpException($"Ungültige Kalenderwoche {week}.");
        var monday = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
        return calendar.WeekPeriod(DateOnly.FromDateTime(monday));
    }

    /// <summary>Zeitraum zu einem logischen Bereich des Schichtkalenders (Schicht, Tag, Woche).</summary>
    public ResolvedRange ForPeriod(ProductionPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        var (kind, label) = period.Kind switch
        {
            PeriodKind.Week => (RangeKind.Week, WeekLabel(period)),
            PeriodKind.Day => (RangeKind.Day, Fmt.Day(calendar.ProductionDayOf(period.StartUtc))),
            _ => (RangeKind.Shift, ShiftLabel(period)),
        };
        return new ResolvedRange(label, period.StartUtc, period.EndUtc, kind, [period]);
    }

    /// <summary>Die Produktionstage eines Zeitraums bis zum Datenstand als eigene Zeiträume (Montag bis Freitag, keine Feiertage).</summary>
    public IReadOnlyList<ResolvedRange> DaysOf(ResolvedRange range, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(range);
        var first = calendar.ProductionDayOf(range.StartUtc);
        var last = calendar.ProductionDayOf(range.EffectiveEnd(now).AddMinutes(-1));
        var days = new List<ResolvedRange>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (calendar.IsProductionDay(day)) days.Add(ForPeriod(calendar.DayPeriod(day)));
        }

        return days;
    }

    public ProductionPeriod ShiftOf(DateTimeOffset at) => calendar.ShiftPeriodOf(at);

    public DateTimeOffset FloorToHour(DateTimeOffset at) => calendar.HourOf(at).StartUtc;

    public ProductionPeriod PreviousShift(ProductionPeriod current) =>
        calendar.ProductionShiftsBetween(current.StartUtc.AddDays(-5), current.StartUtc)
            .LastOrDefault(s => s.StartUtc < current.StartUtc)
        ?? calendar.ShiftPeriodOf(current.StartUtc.AddHours(-1));

    public DateOnly PreviousProductionDay(DateOnly day)
    {
        var previous = day.AddDays(-1);
        while (!calendar.IsProductionDay(previous)) previous = previous.AddDays(-1);
        return previous;
    }

    public DateOnly NextProductionDay(DateOnly day)
    {
        var next = day.AddDays(1);
        while (!calendar.IsProductionDay(next)) next = next.AddDays(1);
        return next;
    }

    /// <summary>Beschriftung einer Schicht, z. B. <c>Nacht Mi 14.10.</c>.</summary>
    public string ShiftLabel(ProductionPeriod shift)
    {
        var day = calendar.ProductionDayOf(shift.StartUtc);
        return $"{ShiftCalendar.ShiftLabel(calendar.ShiftOf(shift.StartUtc))} {Fmt.Day(day)}";
    }

    public static string WeekLabel(ProductionPeriod week)
    {
        var parts = week.Key.Split("-W");
        return $"KW {int.Parse(parts[1], CultureInfo.InvariantCulture)}";
    }

    private List<ProductionPeriod> ShiftsOfDay(DateOnly day, DateTimeOffset now) =>
        [.. Enum.GetValues<Shift>().Select(s => calendar.ShiftPeriod(day, s)).Where(p => p.StartUtc < now)];

    private ResolvedRange? TrySince(string normalized, DateTimeOffset now)
    {
        var match = SincePattern().Match(normalized);
        if (!match.Success) return null;

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var local = calendar.ToLocal(now);
        var start = new DateTime(local.Year, local.Month, local.Day, Math.Min(hour, 23), Math.Min(minute, 59), 0, DateTimeKind.Unspecified);
        var startUtc = calendar.ToUtc(start);
        if (startUtc > now) startUtc = calendar.ToUtc(start.AddDays(-1));
        var shift = calendar.ShiftPeriodOf(startUtc);
        var label = $"seit {hour:00}:{minute:00}";
        // Beginnt das Fenster zum Schichtbeginn der laufenden Schicht, ist es genau diese Schicht (Kennzahlenblock).
        return startUtc == calendar.ShiftPeriodOf(now).StartUtc
            ? new ResolvedRange(label + " (aktuelle Schicht)", shift.StartUtc, shift.EndUtc, RangeKind.Shift, [shift])
            : new ResolvedRange(label, startUtc, now, RangeKind.Custom, []);
    }

    private static ResolvedRange? TryRolling(string normalized, DateTimeOffset now, bool forward)
    {
        var match = RollingPattern().Match(normalized);
        if (!match.Success) return null;

        var amount = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value;
        var span = unit.StartsWith('t') || unit.StartsWith('d') ? TimeSpan.FromDays(amount) : TimeSpan.FromHours(amount);
        var label = unit.StartsWith('t') || unit.StartsWith('d') ? $"{amount} Tage" : $"{amount} h";
        return forward
            ? new ResolvedRange($"nächste {label}", now, now + span, RangeKind.Custom, [])
            : new ResolvedRange($"letzte {label}", now - span, now, RangeKind.Custom, []);
    }

    private ResolvedRange? TryShift(string normalized, DateTimeOffset now)
    {
        var current = calendar.ShiftPeriodOf(now);
        if (normalized is "aktuell" or "aktuelle" or "aktuelle schicht" or "schicht" or "laufende schicht" or "diese schicht")
        {
            return new ResolvedRange($"aktuelle Schicht ({ShiftLabel(current)})", current.StartUtc, current.EndUtc, RangeKind.Shift, [current]);
        }

        if (normalized is "vorherige" or "vorherige schicht" or "letzte schicht" or "vorherig")
        {
            var previous = PreviousShift(current);
            return new ResolvedRange($"vorherige Schicht ({ShiftLabel(previous)})", previous.StartUtc, previous.EndUtc, RangeKind.Shift, [previous]);
        }

        return null;
    }

    private ResolvedRange? TryDay(string normalized, DateTimeOffset now)
    {
        var today = calendar.ProductionDayOf(now);
        switch (normalized)
        {
            case "heute" or "tag" or "produktionstag" or "aktueller tag" or "tagesverlauf":
                return DayRange($"heute ({Fmt.Day(today)})", [today]);
            case "gestern":
                var yesterday = PreviousProductionDay(today);
                return DayRange($"gestern ({Fmt.Day(yesterday)})", [yesterday]);
            case "morgen":
                var tomorrow = NextProductionDay(today);
                return DayRange($"morgen ({Fmt.Day(tomorrow)})", [tomorrow]);
            case "uebermorgen":
                return DayRange("übermorgen", [NextProductionDay(NextProductionDay(today))]);
            case "heute-morgen" or "heute und morgen" or "heute+morgen" or "heute/morgen":
                var next = NextProductionDay(today);
                return DayRange($"heute und morgen ({Fmt.Day(today)} bis {Fmt.Day(next)})", [today, next]);
            default:
                break;
        }

        var iso = IsoDatePattern().Match(normalized);
        if (!iso.Success) return null;

        var day = DateOnly.ParseExact(iso.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return DayRange(Fmt.Day(day), [day]);
    }

    private ResolvedRange DayRange(string label, IReadOnlyList<DateOnly> days)
    {
        var periods = days.Select(calendar.DayPeriod).ToList();
        return new ResolvedRange(label, periods[0].StartUtc, periods[^1].EndUtc, periods.Count == 1 ? RangeKind.Day : RangeKind.Custom, periods);
    }

    private ResolvedRange? TryWeek(string normalized, DateTimeOffset now)
    {
        var thisWeek = calendar.WeekPeriodOf(now);
        switch (normalized)
        {
            case "diese woche" or "woche" or "aktuelle woche" or "kw" or "wochenverlauf":
                return WeekRange(thisWeek);
            case "vorwoche" or "letzte woche" or "vorherige woche":
                return WeekRange(calendar.WeekPeriod(calendar.ProductionDayOf(thisWeek.StartUtc.AddDays(-3))));
            case "naechste woche" or "naechste kw" or "kommende woche" or "naechste":
                return WeekRange(calendar.WeekPeriod(calendar.ProductionDayOf(thisWeek.EndUtc.AddHours(1))));
            default:
                break;
        }

        var span = WeekSpanPattern().Match(normalized);
        if (span.Success)
        {
            var first = WeekOfYear(int.Parse(span.Groups["a"].Value, CultureInfo.InvariantCulture), now);
            var last = WeekOfYear(int.Parse(span.Groups["b"].Value, CultureInfo.InvariantCulture), now);
            var weeks = new List<ProductionPeriod>();
            for (var week = first; week.StartUtc <= last.StartUtc; week = calendar.WeekPeriod(calendar.ProductionDayOf(week.EndUtc.AddHours(1))))
            {
                weeks.Add(week);
            }

            return new ResolvedRange($"{WeekLabel(first)} bis {WeekLabel(last)}", first.StartUtc, last.EndUtc, RangeKind.Custom, weeks);
        }

        var single = WeekPattern().Match(normalized);
        if (single.Success)
        {
            return WeekRange(WeekOfYear(int.Parse(single.Groups["n"].Value, CultureInfo.InvariantCulture), now));
        }

        var key = WeekKeyPattern().Match(normalized);
        if (key.Success)
        {
            var week = int.Parse(key.Groups["n"].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(key.Groups["y"].Value, CultureInfo.InvariantCulture);
            return WeekRange(calendar.WeekPeriod(DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday))));
        }

        return null;
    }

    private ResolvedRange WeekRange(ProductionPeriod week) =>
        new($"{WeekLabel(week)} ({Fmt.Day(calendar.ProductionDayOf(week.StartUtc))} bis {Fmt.Day(calendar.ProductionDayOf(week.EndUtc.AddDays(-3)))})",
            week.StartUtc, week.EndUtc, RangeKind.Week, [week]);

    private ResolvedRange? TryMonths(string normalized, DateTimeOffset now)
    {
        var match = MonthSpanPattern().Match(normalized);
        if (!match.Success) return null;

        var first = MonthIndex(match.Groups["a"].Value);
        var last = match.Groups["b"].Success ? MonthIndex(match.Groups["b"].Value) : first;
        if (first < 0 || last < 0) return null;

        var local = calendar.ToLocal(now);
        var year = local.Year;
        var start = new DateOnly(year, first + 1, 1);
        var end = new DateOnly(year, last + 1, 1).AddMonths(1);
        var startUtc = calendar.ToUtc(start.ToDateTime(TimeOnly.MinValue).AddHours(6));
        var endUtc = calendar.ToUtc(end.ToDateTime(TimeOnly.MinValue).AddHours(6));
        var label = first == last ? $"{MonthName(first)} {year}" : $"{MonthName(first)} bis {MonthName(last)} {year}";
        return new ResolvedRange(label, startUtc, endUtc, RangeKind.Custom, []);
    }

    private ResolvedRange FromTo(string? from, string? to, DateTimeOffset now)
    {
        var start = ParseBoundary(from, isEnd: false) ?? calendar.DayPeriodOf(now).StartUtc;
        var end = ParseBoundary(to, isEnd: true) ?? (start.AddDays(1) > now ? start.AddDays(1) : now);
        if (end <= start) throw new McpException("'bis' muss nach 'von' liegen.");

        var dateOnly = IsDateOnly(from) && (IsDateOnly(to) || string.IsNullOrWhiteSpace(to));
        var periods = dateOnly
            ? EachDay(calendar.ProductionDayOf(start), calendar.ProductionDayOf(end.AddMinutes(-1))).Select(calendar.DayPeriod).ToList()
            : [];
        var label = $"{Fmt.Stamp(start)} bis {Fmt.Stamp(end)}";
        return new ResolvedRange(label, start, end, RangeKind.Custom, periods);
    }

    private static IEnumerable<DateOnly> EachDay(DateOnly first, DateOnly last)
    {
        for (var day = first; day <= last; day = day.AddDays(1)) yield return day;
    }

    private static bool IsDateOnly(string? text) => !string.IsNullOrWhiteSpace(text) && !text.Contains(':', StringComparison.Ordinal);

    private DateTimeOffset? ParseBoundary(string? text, bool isEnd)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var start = calendar.DayPeriod(day);
            return isEnd ? start.EndUtc : start.StartUtc;
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var offset)
            && (trimmed.EndsWith('Z') || trimmed.Contains('+', StringComparison.Ordinal) || trimmed.LastIndexOf('-') > 9))
        {
            return offset.ToUniversalTime();
        }

        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return calendar.ToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
        }

        throw new McpException($"Ungültige Zeitangabe '{text}'. Erwartet wird ein Datum wie 2026-10-19 oder ein Zeitpunkt wie 2026-10-19T08:00 (Ortszeit).");
    }

    private static int MonthIndex(string name)
    {
        var prefix = name.Length >= 3 ? name[..3] : name;
        return Array.IndexOf(s_months, prefix);
    }

    private static string MonthName(int index) => CultureInfo.GetCultureInfo("de-AT").DateTimeFormat.GetMonthName(index + 1);

    private static Shift ShiftOfCode(string code) => code.ToUpperInvariant() switch
    {
        "F" => Shift.Early,
        "S" => Shift.Late,
        _ => Shift.Night,
    };

    /// <summary>Kleinbuchstaben, Umlaute ersetzt, Bindestriche vereinheitlicht („KW 41–42“ → „kw 41-42“).</summary>
    private static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace('–', '-').Replace('—', '-').Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ö", "oe", StringComparison.Ordinal).Replace("ü", "ue", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?:seit\s+(?<h>\d{1,2}):(?<m>\d{2})|(?<h>\d{1,2}):(?<m>\d{2})\s*-\s*jetzt)$")]
    private static partial Regex SincePattern();

    [GeneratedRegex(@"^(?:letzte[nr]?\s+|naechste[nr]?\s+)?(?<n>\d{1,3})\s*(?<unit>h|std|stunden|tage|tagen|tag|d)$")]
    private static partial Regex RollingPattern();

    [GeneratedRegex(@"^kw\s*(?<a>\d{1,2})\s*-\s*(?:kw\s*)?(?<b>\d{1,2})$")]
    private static partial Regex WeekSpanPattern();

    [GeneratedRegex(@"^kw\s*(?<n>\d{1,2})$")]
    private static partial Regex WeekPattern();

    [GeneratedRegex(@"^(?<y>\d{4})-w(?<n>\d{2})$")]
    private static partial Regex WeekKeyPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDatePattern();

    [GeneratedRegex(@"^(?<day>\d{4}-\d{2}-\d{2})(?<code>[fsn])$", RegexOptions.IgnoreCase)]
    private static partial Regex ShiftKeyPattern();

    [GeneratedRegex(@"^(?<a>[a-z]{3,10})(?:\s*-\s*(?<b>[a-z]{3,10}))?$")]
    private static partial Regex MonthSpanPattern();
}
