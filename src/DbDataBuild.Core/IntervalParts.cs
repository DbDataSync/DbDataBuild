using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataBuild.Core;

/// <summary>
/// A DuckDB interval is three numbers that are never mixed: months, days and microseconds (`INTERVAL '1 month 2 days 03:04:05'` is 1, 2 and 11045000000). Adding one to a date or timestamp adds the months
/// first (the day is cut back to the last day of a shorter month), then the days, then the microseconds; that order is what an engine without an interval type has to be given (nested DATEADD on SQL Server).
/// This reads the text forms whose meaning is certain (the names of the units in full, a time `[-]h:mm[:ss[.ffffff]]`, signs, repeated parts, fractions of an hour or less) and refuses everything else, so a
/// text it does not know stays a text for the matrix to report; each form is checked against DuckDB's own reading in <c>IntervalPartsTests</c>.
/// </summary>
public readonly partial record struct IntervalParts(long Months, long Days, long Microseconds)
{
    private const long MicrosPerSecond = 1_000_000, MicrosPerMinute = 60 * MicrosPerSecond, MicrosPerHour = 60 * MicrosPerMinute;

    private static readonly Dictionary<string, (long Months, long Days, long Micros)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["microsecond"] = (0, 0, 1), ["microseconds"] = (0, 0, 1),
        ["millisecond"] = (0, 0, 1_000), ["milliseconds"] = (0, 0, 1_000),
        ["second"] = (0, 0, MicrosPerSecond), ["seconds"] = (0, 0, MicrosPerSecond),
        ["minute"] = (0, 0, MicrosPerMinute), ["minutes"] = (0, 0, MicrosPerMinute),
        ["hour"] = (0, 0, MicrosPerHour), ["hours"] = (0, 0, MicrosPerHour),
        ["day"] = (0, 1, 0), ["days"] = (0, 1, 0),
        ["week"] = (0, 7, 0), ["weeks"] = (0, 7, 0),
        ["month"] = (1, 0, 0), ["months"] = (1, 0, 0),
        ["quarter"] = (3, 0, 0), ["quarters"] = (3, 0, 0),
        ["year"] = (12, 0, 0), ["years"] = (12, 0, 0),
        ["decade"] = (120, 0, 0), ["decades"] = (120, 0, 0),
        ["century"] = (1200, 0, 0), ["centuries"] = (1200, 0, 0),
        ["millennium"] = (12000, 0, 0), ["millennia"] = (12000, 0, 0),
    };

    // a part is a whole number with a unit; a unit up to an hour also takes a fraction (a fraction of a day or more is spread over the smaller units, which is not reproduced here)
    [GeneratedRegex(@"\G\s*(?<time>-?\d+:\d{2}(:\d{2}(\.\d{1,6})?)?)(?=\s|$)|\G\s*(?<number>-?\d+(\.\d{1,6})?)\s*(?<unit>[A-Za-z]+)(?=\s|\d|-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    public bool IsZero => Months == 0 && Days == 0 && Microseconds == 0;

    public IntervalParts Negated => new(-Months, -Days, -Microseconds);

    public static IntervalParts operator +(IntervalParts a, IntervalParts b) => new(a.Months + b.Months, a.Days + b.Days, a.Microseconds + b.Microseconds);

    /// <summary>True when the parts fit what DuckDB stores (32 bits for months and days, 64 for microseconds).</summary>
    public bool InRange => Months is >= int.MinValue and <= int.MaxValue && Days is >= int.MinValue and <= int.MaxValue;

    /// <summary>The parts of <c>n</c> of a unit (`3` and `weeks`), or null for a unit that is not known or a count that has a fraction where none is understood.</summary>
    public static IntervalParts? OfUnit(decimal n, string unit)
    {
        if (!Units.TryGetValue(unit, out var u)) return null;
        try
        {
            if (u.Micros == 0) return n != decimal.Truncate(n) ? null : Checked(new((long)n * u.Months, (long)n * u.Days, 0));
            var micros = n * u.Micros;
            return micros != decimal.Truncate(micros) ? null : Checked(new(0, 0, (long)micros));
        }
        catch (OverflowException) { return null; }
    }

    private static IntervalParts? Checked(IntervalParts p) => p.InRange ? p : null;

    /// <summary>Reads the text of an interval; null when any part of it is not a form this knows exactly.</summary>
    public static IntervalParts? TryParse(string text)
    {
        var total = default(IntervalParts);
        var at = 0;
        var any = false;
        var timeToken = false;            // DuckDB gives a signed `h:mm:ss` a say over the time units written before it (it subtracts them when the clock is negative): the two together are not read
        var timeUnit = false;
        var trimmed = text.TrimEnd();
        while (at < trimmed.Length)
        {
            var m = Token().Match(trimmed, at);
            if (!m.Success) return null;
            at = m.Index + m.Length;
            any = true;
            IntervalParts? part;
            if (m.Groups["time"].Success) { if (timeToken || timeUnit) return null; timeToken = true; /* DuckDB ignores whatever follows the clock: only a clock at the end is read */ if (m.Index + m.Length < trimmed.Length) return null; part = Time(m.Groups["time"].Value); }
            else if (!decimal.TryParse(m.Groups["number"].Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return null;
            else { part = OfUnit(n, m.Groups["unit"].Value); timeUnit |= part is { Microseconds: not 0 } || part is { IsZero: true } && Units.TryGetValue(m.Groups["unit"].Value, out var u) && u.Micros != 0; }
            if (part is not { } p || timeToken && timeUnit) return null;
            try { total = checked(total + p); } catch (OverflowException) { return null; }
        }
        return any && total.InRange ? total : null;
    }

    private static IntervalParts? Time(string text)
    {
        var negative = text.StartsWith('-');
        var fields = text.TrimStart('-').Split(':');
        if (!long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes > 59) return null;
        long micros = hours * MicrosPerHour + minutes * MicrosPerMinute;
        if (fields.Length == 3)
        {
            var parts = fields[2].Split('.');
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 59) return null;
            micros += seconds * MicrosPerSecond;
            if (parts.Length == 2) micros += long.Parse(parts[1].PadRight(6, '0'), CultureInfo.InvariantCulture);
        }
        return new(0, 0, negative ? -micros : micros);
    }

    /// <summary>The text DuckDB and PostgreSQL both read back as these parts: `1 months 2 days 3 microseconds`, a zero part left out (`0 days` for none at all).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        void Part(long n, string unit) { if (n != 0) sb.Append(sb.Length > 0 ? " " : "").Append(n.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(unit); }
        Part(Months, "months"); Part(Days, "days"); Part(Microseconds, "microseconds");
        return sb.Length == 0 ? "0 days" : sb.ToString();
    }
}
