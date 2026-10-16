using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace DbDataBuild.Targets.Rules;

/// <summary>
/// The rules for single functions and operators whose engines disagree with DuckDB (docs/research/function-matrix.md): bit operators, shifts, the factorial. They sit apart from the rules of
/// <see cref="TargetRules"/> only for size; <see cref="TargetRules.Apply"/> calls <see cref="Rewrite"/> for every node.
/// </summary>
public static partial class TargetRules
{
    public const string BitOperators = "bit-operators";
    public const string ShiftLeft = "shift-left";
    public const string ShiftRight = "shift-right";
    public const string FactorialTable = "factorial-table";
    public const string TruncDigits = "trunc-digits";

    /// <summary>Marker for an integer division: polyglot reads `/` in DuckDB's dialect as division of doubles and writes casts around it, so a rule that needs the engine's own whole-number division writes this call, and <see cref="Finish"/> turns it into `((a) / (b))`.</summary>
    private const string IntegerDivisionMarker = "ddb_idiv";

    private const string ExclusiveOrMarker = "ddb_xor";

    private static bool IsBigLiteral(JsonNode n) => WholeLiteral(n) is { } v && (v > int.MaxValue || v < int.MinValue);

    private static JsonNode BigLiteralAsBigint(JsonNode n) => IsBigLiteral(n) ? Template("CAST(__v AS BIGINT)", ("__v", n)) : n.DeepClone();

    // the table of functions written out from others (FunctionTemplates), by the name the call has: a function's name, or the node polyglot reads it as
    private static readonly Dictionary<string, List<FunctionTemplate>> TemplatesByName = FunctionTemplates.All
        .GroupBy(t => (t.Node ?? t.Function).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList());

    private static bool IsDoubleCast(JsonNode? n) => (n?["cast"]?["to"]?["data_type"]?.GetValue<string>()) is "double";

    private static bool IsIntervalValued(JsonNode? n) => n is not null &&
        (n["function"]?["name"]?.GetValue<string>()?.ToLowerInvariant() is "age" || n["interval"] is not null || (n["cast"]?["to"]?["data_type"]?.GetValue<string>()) is "interval" || IsIntervalValued(n["paren"]?["this"]));

    private static JsonNode AsDouble(JsonNode n) => n["cast"]?["to"]?["data_type"]?.GetValue<string>() == "double" ? n : Template("CAST(__v AS DOUBLE)", ("__v", n));

    private static JsonNode? FromTemplate(string kind, JsonObject body, string target, HashSet<string> rules, SortedSet<string> fired)
    {
        string name; List<JsonNode?> args;
        if (kind == "function" && body["name"]?.GetValue<string>() is { } called && body["args"] is JsonArray callArgs) { name = called.ToLowerInvariant(); args = callArgs.ToList(); }
        else if (TemplatesByName.TryGetValue(kind, out var nodeTemplates) && nodeTemplates[0].Fields is { } fields) { name = kind; args = fields.Select(f => body[f]).ToList(); }
        else return null;
        if (!TemplatesByName.TryGetValue(name, out var candidates) || args.Any(a => a is null)) return null;
        foreach (var t in candidates)
        {
            if (!t.Targets.Contains(target) || !rules.Contains(t.Rule) || t.Arity != args.Count) continue;
            if (t.Unit != null && args.Count > 1 && IsIntervalValued(args[1])) continue;                  // the part of an age or of an interval, not of a date (PostgreSQL's own EXTRACT reads it)
            if (t.Unit != null && !string.Equals(args[0]?["literal"]?["value"]?.GetValue<string>(), t.Unit, StringComparison.OrdinalIgnoreCase)) continue;
            fired.Add(t.Rule);
            return Template(t.Template, args.Select((a, i) => ($"__{i}", t.DoubleArguments ? AsDouble(a!) : a!)).ToArray());
        }
        return null;
    }

    private static JsonNode? FunctionRewrite(string kind, JsonObject body, string target, HashSet<string> rules, SortedSet<string> fired)
    {
        if (AgeRewrite(kind, body, rules, fired) is { } aged) return aged;
        if (StatisticsRewrite(kind, body, rules, fired) is { } statistic) return statistic;
        if (FromTemplate(kind, body, target, rules, fired) is { } templated) return templated;
        switch (kind)
        {
            case "function" when rules.Contains(BitOperators) && string.Equals(body["name"]?.GetValue<string>(), "xor", StringComparison.OrdinalIgnoreCase) && body["args"] is JsonArray { Count: 2 } xorArgs:
                // DuckDB's xor(a, b) is the bitwise exclusive or of two integers; T-SQL's operator is ^ (polyglot writes XOR(a, b) there, which does not exist). PostgreSQL's # is written by the transpile.
                // (a `^` in the text would be read back as DuckDB's power operator: the marker is turned into the operator after the transpile.)
                fired.Add(BitOperators);
                return new JsonObject { ["function"] = new JsonObject { ["name"] = ExclusiveOrMarker, ["args"] = new JsonArray(BigLiteralAsBigint(xorArgs[0]!), BigLiteralAsBigint(xorArgs[1]!)), ["distinct"] = false, ["use_bracket_syntax"] = false, ["no_parens"] = false } };
            case "bitwise_and" or "bitwise_or" when rules.Contains(BitOperators) && body["left"] is { } bitLeft && body["right"] is { } bitRight && (IsBigLiteral(bitLeft) || IsBigLiteral(bitRight)):
                // T-SQL types a whole-number literal beyond 2147483647 as a numeric, which & and | refuse next to a bigint ("the data types bigint and numeric are incompatible")
                fired.Add(BitOperators);
                body["left"] = BigLiteralAsBigint(bitLeft);
                body["right"] = BigLiteralAsBigint(bitRight);
                return body.Parent;                                          // changed in place
            case "bitwise_left_shift" when rules.Contains(ShiftLeft) && IsMarkedShift(body, out var leftValue, out var leftType, out var leftCount):
                fired.Add(ShiftLeft);
                return LeftShift(leftValue, leftType, leftCount);
            case "bitwise_right_shift" when rules.Contains(ShiftRight) && IsMarkedShift(body, out var rightValue, out var rightType, out var rightCount):
                fired.Add(ShiftRight);
                return RightShift(rightValue, rightType, rightCount, target);
            case "function" when rules.Contains(TruncDigits) && string.Equals(body["name"]?.GetValue<string>(), "trunc", StringComparison.OrdinalIgnoreCase) && body["args"] is JsonArray { Count: 2 } truncArgs
                && IsDoubleCast(truncArgs[0]) && WholeLiteral(truncArgs[1]) is { } truncDigits && truncDigits is >= 0 and <= 15:
                // trunc(double, n) is the double cut toward zero at n digits: trunc(x * 10^n) / 10^n. PostgreSQL has trunc(numeric, integer) only.
                fired.Add(TruncDigits);
                var scale = BigInteger.Pow(10, (int)truncDigits).ToString(CultureInfo.InvariantCulture) + ".0";
                return Template($"trunc(__x * {scale}) / {scale}", ("__x", truncArgs[0]!));
            case "function" when rules.Contains(FactorialTable) && string.Equals(body["name"]?.GetValue<string>(), "factorial", StringComparison.OrdinalIgnoreCase) && body["args"] is JsonArray { Count: 1 } factorialArgs:
                fired.Add(FactorialTable);
                return Factorial(factorialArgs[0]!);
        }
        return null;
    }

    // ---- shifts -------------------------------------------------------------------------------------------------------------------------------------------
    // DuckDB: `a << n` is a times 2^n and raises when a is negative, when n is outside 0 to width - 1, and when the result does not fit the type; `a >> n` shifts right keeping the sign (the floor of a / 2^n) and
    // is 0 for a count outside 0 to width - 1. T-SQL has no shift operators (LEFT_SHIFT and RIGHT_SHIFT exist from SQL Server 2022 but wrap silently, and RIGHT_SHIFT fills with zeros: RIGHT_SHIFT(-8, 1) is
    // 2147483644), and PostgreSQL's << wraps silently. The lowerer marks the width with a cast of the value to its type.

    private static readonly Dictionary<string, (int Width, string Name)> ShiftTypes = new() { ["small_int"] = (16, "SMALLINT"), ["int"] = (32, "INTEGER"), ["big_int"] = (64, "BIGINT") };

    private static bool IsMarkedShift(JsonObject shift, out JsonNode value, out (int Width, string Name) type, out JsonNode count)
    {
        value = count = null!; type = default;
        if (shift["left"] is not JsonObject { Count: 1 } l || l["cast"] is not JsonObject cast || cast["to"]?["data_type"]?.GetValue<string>() is not { } dataType || !ShiftTypes.TryGetValue(dataType, out type)) return false;
        if (cast["this"] is not { } inner || shift["right"] is not { } n) return false;
        value = inner; count = n;
        return true;
    }

    private static JsonNode LeftShift(JsonNode value, (int Width, string Name) type, JsonNode count)
    {
        // the product is formed in DECIMAL(38, 0), which holds 2^63 times 2^63, and cast back to the type: a result that does not fit raises on the engine as it does in DuckDB
        if (WholeLiteral(count) is { } n && n >= 0 && n < type.Width)
            return Template($"CAST(CAST(__a AS DECIMAL(38, 0)) * {BigInteger.Pow(2, (int)n)} AS {type.Name})", ("__a", value));
        return Template($"CAST(CAST(__a AS DECIMAL(38, 0)) * POWER(CAST(2 AS DECIMAL(38, 0)), __n) AS {type.Name})", ("__a", value), ("__n", count));
    }

    private static JsonNode RightShift(JsonNode value, (int Width, string Name) type, JsonNode count, string target)
    {
        var width = type.Width;
        if (target == "postgres")
        {
            // PostgreSQL's >> keeps the sign; only the counts that DuckDB answers with 0 need a guard
            if (WholeLiteral(count) is { } literal) return literal >= 0 && literal < width ? Template($"CAST(__a AS {type.Name}) >> {literal}", ("__a", value)) : Template("__a * 0", ("__a", value));
            return Template($"CASE WHEN __n < 0 OR __n >= {width} THEN __a * 0 ELSE CAST(__a AS {type.Name}) >> CAST(__n AS INTEGER) END", ("__a", value), ("__n", count));
        }
        // T-SQL: whole-number division truncates toward zero, so a negative value is shifted as its complement (~a is -a - 1, never negative, and never overflows for the smallest value):
        // a >> n is ~((~a) >> n) for a < 0. A shift of 63 places (BIGINT) is the sign alone, because 2^63 does not fit a BIGINT.
        string Shifted(string power) => $"CASE WHEN __a >= 0 THEN {IntegerDivisionMarker}(CAST(__a AS BIGINT), {power}) ELSE ~{IntegerDivisionMarker}(~CAST(__a AS BIGINT), {power}) END";
        if (WholeLiteral(count) is { } fixedCount)
        {
            if (fixedCount < 0 || fixedCount >= width) return Template("__a * 0", ("__a", value));
            if (fixedCount == 0) return Template($"CAST(__a AS {type.Name})", ("__a", value));
            var sign = Template("CASE WHEN __a < 0 THEN -1 ELSE 0 END", ("__a", value));
            if (fixedCount == 63) return sign;
            return Template($"CAST({Shifted($"CAST({BigInteger.Pow(2, (int)fixedCount)} AS BIGINT)")} AS {type.Name})", ("__a", value));
        }
        var guarded = "POWER(CAST(2 AS BIGINT), CASE WHEN __n BETWEEN 0 AND 62 THEN __n ELSE 0 END)";
        var text = $"CASE WHEN __n < 0 OR __n >= {width} THEN __a * 0 {(width == 64 ? "WHEN __n = 63 THEN CASE WHEN __a < 0 THEN -1 ELSE 0 END " : "")}ELSE CAST({Shifted(guarded)} AS {type.Name}) END";
        return Template(text, ("__a", value), ("__n", count));
    }

    private static long? WholeLiteral(JsonNode? n)
    {
        var negative = n?["neg"]?["this"] is { };
        var literal = negative ? n!["neg"]!["this"] : n;
        if (literal?["literal"] is not JsonObject lit || (lit["literal_type"]?.GetValue<string>()) is not "number" || !long.TryParse(lit["value"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
        return negative ? -v : v;
    }

    // ---- factorial ----------------------------------------------------------------------------------------------------------------------------------------
    // DuckDB's factorial (and the postfix `!`) gives a HUGEINT for 0 to 33 and raises for a negative number and for 34 and beyond (the result would not fit 128 bits' worth of the engine's DECIMAL(38, 0)
    // either: 33! has 37 digits). The values are written out; any other number runs into a cast that overflows, so the engine raises as DuckDB does.

    private static JsonNode Factorial(JsonNode n)
    {
        var sb = new StringBuilder("CASE __n");
        var f = BigInteger.One;
        for (var i = 0; i <= 33; i++)
        {
            if (i > 1) f *= i;
            sb.Append(CultureInfo.InvariantCulture, $" WHEN {i} THEN CAST('{f}' AS DECIMAL(38, 0))");
        }
        sb.Append(" ELSE CAST(CAST(__n AS DOUBLE) * 1e38 AS DECIMAL(38, 0)) END");
        return Template(sb.ToString(), ("__n", n));
    }

    // ---- markers that stand for an operator -----------------------------------------------------------------------------------------------------------------

    /// <summary>PostgreSQL's EXTRACT(EPOCH FROM x): polyglot reads DuckDB's epoch(x) and julian(x) and writes the functions of those names, which PostgreSQL does not have.</summary>
    private static string ExpandPostgresMarkers(string text) =>
        ExpandMarker(ExpandMarker(ExpandMarker(ExpandMarker(text, "ddb_numeric", a => $"CAST({a[0]} AS NUMERIC)"), "ddb_pg_epoch_exact", a => $"EXTRACT(EPOCH FROM {a[0]})"), "ddb_pg_epoch", a => $"EXTRACT(EPOCH FROM {a[0]})"), "ddb_pg_julian", a => $"EXTRACT(JULIAN FROM {a[0]})");

    private static string ExpandOperatorMarkers(string text)
    {
        text = ExpandOperatorMarker(ExpandOperatorMarker(text, IntegerDivisionMarker, "/"), ExclusiveOrMarker, "^");
        // the epoch of a timestamp needs the argument twice (the whole seconds and the microseconds); SQL Server's DATEDIFF would overflow past 2038, so DATEDIFF_BIG
        return ExpandMarker(text, "ddb_epoch", a => $"(DATEDIFF_BIG(SECOND, CAST('1970-01-01' AS DATETIME2), {a[0]}) + DATEPART(MICROSECOND, {a[0]}) / 1000000.0)");
    }

    private static string ExpandOperatorMarker(string text, string marker, string op) => ExpandMarker(text, marker, a => $"(({a[0]}) {op} ({a[1]}))");

    /// <summary>Replaces `marker(a, b, ...)` by what <paramref name="write"/> makes of the arguments: they are found by counting parentheses, so nested calls and string literals are safe. Innermost calls first.</summary>
    private static string ExpandMarker(string text, string marker, Func<string[], string> write)
    {
        while (true)
        {
            var at = text.LastIndexOf(marker + "(", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return text;
            var open = at + marker.Length;
            int depth = 0, close = -1;
            var splits = new List<int>();
            var inString = false;
            for (var i = open; i < text.Length && close < 0; i++)
            {
                var c = text[i];
                if (inString) { if (c == '\'') inString = false; continue; }
                switch (c)
                {
                    case '\'': inString = true; break;
                    case '(': depth++; break;
                    case ')': depth--; if (depth == 0) close = i; break;
                    case ',' when depth == 1: splits.Add(i); break;
                }
            }
            if (close < 0) return text;                                      // not what this wrote: left as it is, and the engine says so
            var arguments = new List<string>();
            var start = open + 1;
            foreach (var split in splits) { arguments.Add(text[start..split].Trim()); start = split + 1; }
            arguments.Add(text[start..close].Trim());
            text = text[..at] + write([.. arguments]) + text[(close + 1)..];
        }
    }
}
