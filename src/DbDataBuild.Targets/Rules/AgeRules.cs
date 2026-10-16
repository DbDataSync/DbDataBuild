using System.Text.Json.Nodes;
using DbDataBuild.Core;

namespace DbDataBuild.Targets.Rules;

/// <summary>
/// DuckDB's age(a, b) (docs/research/age-function.md): the difference as months, days and microseconds with borrowing (the days of the earlier timestamp's month). PostgreSQL's AGE is that function exactly (5449
/// of 5449 pairs). SQL Server has no AGE and no interval type, so the shapes that end in a number or a boolean are written out in T-SQL (proven equal on the same 5449 pairs, and again by the conformance test
/// AgeRuleConformance): `date_part(unit, age(a, b))` for year, month, day, hour, minute and second, and `normalized_interval(age(...))` (DuckDB's wrapper for comparing and ordering intervals: a month is 30 days),
/// which becomes the number of microseconds with the sign. Anything else (an age as a value, in arithmetic, sub-second parts, epoch) stays and the matrix refuses it (row fn.age, DDB-301).
/// </summary>
public static partial class TargetRules
{
    public const string AgeParts = "age-parts";
    public const string NormalizedIntervalStrip = "normalized-interval";
    public const string AgeSecond = "age-second";

    private static JsonNode? AgeRewrite(string kind, JsonObject body, HashSet<string> rules, SortedSet<string> fired)
    {
        if (kind != "function" || body["name"]?.GetValue<string>()?.ToLowerInvariant() is not { } name || body["args"] is not JsonArray args) return null;
        if (name == "normalized_interval" && args.Count == 1)
        {
            if (rules.Contains(NormalizedIntervalStrip)) { fired.Add(NormalizedIntervalStrip); return args[0]!.DeepClone(); }
            if (!rules.Contains(AgeParts)) return null;
            if (AgeOperands(args[0]) is { } ageOperands) { fired.Add(AgeParts); return Template($"({AgeKey})", AgeHoles(ageOperands)); }
            if (IntervalOf(args[0]) is { } constant)
            {
                try { fired.Add(AgeParts); return Template(checked((constant.Months * 30 + constant.Days) * 86_400_000_000L + constant.Microseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                catch (OverflowException) { fired.Remove(AgeParts); return null; }
            }
            return null;
        }
        if (name == "date_part" && args.Count == 2 && (args[0]?["literal"]?["value"]?.GetValue<string>())?.ToLowerInvariant() is { } unit && AgeOperands(args[1]) is { } operands)
        {
            if (rules.Contains(AgeSecond) && unit == "second") { fired.Add(AgeSecond); return Template("CAST(trunc(date_part('second', __age)) AS BIGINT)", ("__age", args[1]!)); }
            if (!rules.Contains(AgeParts) || !AgeUnits.TryGetValue(unit, out var expression)) return null;
            fired.Add(AgeParts);
            return Template($"({expression})", AgeHoles(operands));
        }
        return null;
    }

    private const string Earlier = "CASE WHEN __a < __b THEN __a ELSE __b END";
    private const string Later = "CASE WHEN __a < __b THEN __b ELSE __a END";
    private const string TimeBorrow = $"CASE WHEN CAST({Later} AS TIME) < CAST({Earlier} AS TIME) THEN 1 ELSE 0 END";
    private const string MonthBorrow = $"CASE WHEN date_part('day', {Later}) < date_part('day', {Earlier}) OR (date_part('day', {Later}) = date_part('day', {Earlier}) AND CAST({Later} AS TIME) < CAST({Earlier} AS TIME)) THEN 1 ELSE 0 END";
    private const string Months = $"(date_diff('month', {Earlier}, {Later}) - ({MonthBorrow}))";
    private const string Days = $"(date_part('day', {Later}) - date_part('day', {Earlier}) - ({TimeBorrow}) + ({MonthBorrow}) * date_part('day', last_day({Earlier})))";
    private const string Micros = $"(ddb_datediff_big_microsecond(CAST({Earlier} AS TIME), CAST({Later} AS TIME)) + ({TimeBorrow}) * CAST(86400000000 AS BIGINT))";
    private const string Sign = "CASE WHEN __a < __b THEN -1 ELSE 1 END";

    // the number of microseconds with the sign, months counting 30 days: what DuckDB's normalized_interval orders and compares
    private const string AgeKey = $"({Sign}) * (({Months} * 30 + {Days}) * CAST(86400000000 AS BIGINT) + {Micros})";

    private static readonly Dictionary<string, string> AgeUnits = new(StringComparer.Ordinal)
    {
        ["year"] = $"({Sign}) * ddb_idiv({Months}, 12)",
        ["month"] = $"({Sign}) * ({Months} % 12)",
        ["day"] = $"({Sign}) * {Days}",
        ["hour"] = $"({Sign}) * ddb_idiv({Micros}, CAST(3600000000 AS BIGINT))",
        ["minute"] = $"({Sign}) * (ddb_idiv({Micros}, 60000000) % 60)",
        ["second"] = $"({Sign}) * (ddb_idiv({Micros}, 1000000) % 60)",
    };

    private static (string, JsonNode)[] AgeHoles((JsonNode A, JsonNode B) o) => [("__a", o.A), ("__b", o.B)];

    /// <summary>The two operands of age(a, b), or of age(b) (against today's date); null when the node is not an age call or an operand cannot be repeated (it holds a subquery or a volatile call).</summary>
    private static (JsonNode A, JsonNode B)? AgeOperands(JsonNode? n)
    {
        if (n?["function"] is not JsonObject f || (f["name"]?.GetValue<string>())?.ToLowerInvariant() != "age" || f["args"] is not JsonArray args) return null;
        JsonNode a, b;
        if (args.Count == 2) { a = args[0]!; b = args[1]!; }
        else if (args.Count == 1) { a = Template("CAST(current_date AS TIMESTAMP)"); b = args[0]!; }
        else return null;
        return IsRepeatable(a) && IsRepeatable(b) ? (a, b) : null;
    }

    private static readonly HashSet<string> RepeatableCalls = ["current_date", "now", "current_timestamp", "localtimestamp", "make_date", "date_trunc", "today"];

    /// <summary>An operand the age expression repeats many times: a column, a literal, a cast, a sum or difference of those, or a stable call. A subquery or a random value would be evaluated again each time.</summary>
    private static bool IsRepeatable(JsonNode? n)
    {
        if (n is not JsonObject { Count: 1 } o) return n is null;
        var (kind, body) = (o.First().Key, o.First().Value as JsonObject);
        if (body == null) return kind is "current_date" or "current_timestamp";
        switch (kind)
        {
            case "column" or "literal" or "interval": return true;
            case "paren" or "neg": return IsRepeatable(body["this"]);
            case "cast" or "try_cast": return IsRepeatable(body["this"]);
            case "add" or "sub": return IsRepeatable(body["left"]) && IsRepeatable(body["right"]);
            case "function": return (body["name"]?.GetValue<string>())?.ToLowerInvariant() is { } fn && RepeatableCalls.Contains(fn) && (body["args"] as JsonArray ?? []).All(a => IsRepeatable(a));
            default: return false;
        }
    }
}
