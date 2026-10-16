using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Sql.Ast;

namespace DbDataBuild.Sql.Matrix;

/// <summary>
/// Named AST predicates for constructs that are clause-level or conditional rather than a single node tag.
/// A matrix row refers to one with <c>detect: detector:&lt;name&gt;</c>. Each is evaluated against every node.
/// </summary>
public static class Detectors
{
    public static readonly IReadOnlyDictionary<string, Func<AstNode, bool>> All = new Dictionary<string, Func<AstNode, bool>>
    {
        ["cast_varchar_length"] = n => n.Type is "cast" or "try_cast" && n.TryGet("to", out var to) &&
            to.ValueKind == JsonValueKind.Object && to.TryGetProperty("data_type", out var dt) && dt.GetString() is "var_char" or "char" &&
            to.TryGetProperty("length", out var len) && len.ValueKind == JsonValueKind.Number,
        ["unicode_literal"] = n => n.Type == "literal" && n.GetString("literal_type") == "string" && (n.GetString("value") ?? "").Any(c => c > 127),
        ["string_equality"] = n => n.Type switch
        {
            "eq" or "neq" => IsStringLiteral(n, "left") || IsStringLiteral(n, "right"),
            "in" => n.TryGet("expressions", out var xs) && xs.ValueKind == JsonValueKind.Array && xs.EnumerateArray().Any(IsStringLiteral),
            _ => false,
        },
        ["qualify"] = n => Select(n) && NonNull(n, "qualify"),
        ["group_by_ordinal"] = n => Select(n) && n.TryGet("group_by", out var g) && g.ValueKind == JsonValueKind.Object &&
            g.TryGetProperty("expressions", out var xs) && xs.ValueKind == JsonValueKind.Array && xs.EnumerateArray().Any(IsNumberLiteral),
        ["group_by_all"] = n => Select(n) && n.TryGet("group_by", out var g) && g.ValueKind == JsonValueKind.Object &&
            g.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True,
        ["order_nulls"] = n => (Select(n) && NonNull(n, "order_by")) ||
            (n.Type == "window_function" && n.TryGet("over", out var over) && over.ValueKind == JsonValueKind.Object &&
             over.TryGetProperty("order_by", out var ob) && ob.ValueKind == JsonValueKind.Array && ob.GetArrayLength() > 0),
        ["join_using"] = n => Select(n) && Joins(n).Any(j => j.TryGetProperty("using", out var u) && u.ValueKind == JsonValueKind.Array && u.GetArrayLength() > 0),
        ["join_natural"] = n => Select(n) && Joins(n).Any(j => j.TryGetProperty("kind", out var k) && k.GetString() == "Natural"),
        ["distinct_on"] = n => Select(n) && NonNull(n, "distinct_on"),
        // a product with a DECIMAL(38, s) operand: what DuckDB writes into the plan when a decimal product would be wider than 38 digits (it casts the operands to the widest type and multiplies exactly)
        ["wide_decimal_product"] = n => n.Type == "mul" && (WideDecimalCast(n, "left") || WideDecimalCast(n, "right")),
        ["recursive_cte"] = n => Select(n) && n.TryGet("with", out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True,
        ["lateral_subquery"] = n => n.Type == "subquery" && n.TryGet("lateral", out var l) && l.ValueKind == JsonValueKind.True,
        ["sample"] = n => Select(n) && NonNull(n, "sample"),
        // a unit of date_part, date_trunc or date_diff that the tool does not translate (DateUnits), or one that is not a literal; the unit is named in the message
        ["date_unit_unsupported"] = n => n.Type == "function" && n.GetString("name")?.ToUpperInvariant() is "DATE_PART" or "DATEPART" or "DATE_TRUNC" or "DATETRUNC" or "DATE_DIFF" or "DATEDIFF" &&
            n.TryGet("args", out var args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 &&
            (LiteralValue(args[0]) is not { } unit || !(n.GetString("name")!.ToUpperInvariant() switch { "DATE_PART" or "DATEPART" => DateUnits.DatePart, "DATE_TRUNC" or "DATETRUNC" => DateUnits.Truncation, _ => DateUnits.Difference }).Contains(unit)),
        // log(x): base 10 in DuckDB, the natural logarithm in T-SQL (a target rule writes LOG10; this reports it when the rule is turned off)
        ["log_single_argument"] = n => n.Type == "log" && !NonNull(n, "base"),
        // an interval that cannot become DATEADD calls on SQL Server: text the interval reader does not know exactly (a unit written short, a fraction of a day), a cast of anything but a text literal, or a product or quotient
        // of an interval (the target rule interval-dateadd takes what it can read before the matrix is asked)
        ["interval_unreadable"] = n => (n.Type == "cast" && n.TryGet("to", out var to) && to.ValueKind == JsonValueKind.Object && to.TryGetProperty("data_type", out var dt) && dt.GetString() == "interval" &&
            !(n.TryGet("this", out var text) && LiteralType(text) == "string" && LiteralValue(text) is { } s && IntervalParts.TryParse(s) != null)) ||
            (n.Type is "mul" or "div" && (IsInterval(n, "left") || IsInterval(n, "right"))),
        // substr(s, -2): DuckDB counts from the end
        ["substring_negative_start"] = n => n.Type == "substring" && n.TryGet("start", out var start) && (start.ToString().Contains("\"neg\"", StringComparison.Ordinal) || LiteralValue(start)?.StartsWith('-') == true),
    };

    private static bool IsInterval(AstNode n, string side) =>
        n.TryGet(side, out var s) && s.ValueKind == JsonValueKind.Object && (s.TryGetProperty("interval", out _) ||
        s.TryGetProperty("cast", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object && to.TryGetProperty("data_type", out var dt) && dt.GetString() == "interval");

    private static bool WideDecimalCast(AstNode n, string side) =>
        n.TryGet(side, out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("cast", out var c) && c.ValueKind == JsonValueKind.Object &&
        c.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object && to.TryGetProperty("data_type", out var dt) && dt.GetString() == "decimal" &&
        to.TryGetProperty("precision", out var precision) && precision.ValueKind == JsonValueKind.Number && precision.GetInt32() >= 37 &&
        to.TryGetProperty("scale", out var scale) && scale.ValueKind == JsonValueKind.Number && scale.GetInt32() > 0;      // (DECIMAL(38, 0) times DECIMAL(38, 0) is exact: the shift rules use it)

    private static string? LiteralValue(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("literal", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.ToLowerInvariant() : null;

    /// <summary>For clause-level detectors: the select field whose position best locates the construct.</summary>
    public static readonly IReadOnlyDictionary<string, string> LocationField = new Dictionary<string, string>
    {
        ["qualify"] = "qualify", ["group_by_ordinal"] = "group_by", ["group_by_all"] = "group_by", ["order_nulls"] = "order_by",
        ["join_using"] = "joins", ["join_natural"] = "joins", ["distinct_on"] = "distinct_on", ["sample"] = "sample",
    };

    public static bool Select(AstNode n) => n.Type == "select";

    public static bool NonNull(AstNode n, string field) =>
        n.TryGet(field, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) &&
        !(v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);

    public static IEnumerable<JsonElement> Joins(AstNode n) =>
        n.TryGet("joins", out var j) && j.ValueKind == JsonValueKind.Array ? j.EnumerateArray() : [];

    private static bool IsStringLiteral(AstNode n, string field) => n.TryGet(field, out var v) && IsStringLiteral(v);

    private static bool IsStringLiteral(JsonElement e) => LiteralType(e) == "string";

    private static bool IsNumberLiteral(JsonElement e) => LiteralType(e) == "number";

    private static string? LiteralType(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("literal", out var l) && l.ValueKind == JsonValueKind.Object &&
        l.TryGetProperty("literal_type", out var t) ? t.GetString() : null;
}
