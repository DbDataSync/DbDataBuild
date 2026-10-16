using System.Numerics;
using DbDataBuild.Core;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataBuild.Lowering;

public sealed record LoweredColumn(string Name, string DuckDbType);

/// <param name="Sql">One readable query in DuckDB's dialect: explicit columns, explicit casts, no macros, `*`, PIVOT or `GROUP BY ALL`.</param>
/// <param name="Columns">The output columns and the types DuckDB resolved for them.</param>
/// <param name="Rules">The type-pinning rules that changed the text, by name (for reports and tests).</param>
public sealed record LoweredQuery(string Sql, IReadOnlyList<LoweredColumn> Columns, IReadOnlyList<string> Rules)
{
    /// <summary>The tables the bound plan scans, by the name a query uses (`schema_name.table_name`): what the query reads once macros, `*` and the rest are expanded.</summary>
    public IReadOnlyList<string> Tables { get; init; } = [];
}

/// <summary>The query cannot be lowered. The message says what in the plan has no lowering yet; nothing is guessed (DESIGN.md: no silent fallbacks).</summary>
public sealed class LoweringException(string reason, string kind = "unsupported") : Exception(reason)
{
    /// <summary>`parser`, `binder` or `catalog` (or another DuckDB error type) when DuckDB itself rejected the query (its message is in the text), otherwise `unsupported`.</summary>
    public string Kind { get; } = kind;
}

/// <summary>
/// Turns DuckDB's bound, unoptimized logical plan into one readable SELECT in DuckDB's dialect (docs/research/duckdb-plan-lowering). Every plan operator becomes a
/// <c>Rel</c>, a SELECT block under construction; operators merge into their child's block when SQL allows it and otherwise wrap the child as a subquery. A column is
/// qualified only in a block with more than one source. Anything the lowerer does not know is a <see cref="LoweringException"/>, never a guess.
/// </summary>
public sealed class PlanLowerer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "order", "group", "select", "from", "where", "table", "user", "key", "index", "primary", "end", "by", "case", "when", "then", "else", "all", "as", "to", "limit", "offset",
        "having", "union", "join", "on", "and", "or", "not", "null", "true", "false", "in", "is", "like", "between", "exists", "distinct", "values", "year", "date", "time",
    };

    private static readonly Dictionary<string, string> LikeFunctions = new() { ["~~"] = "LIKE", ["~~*"] = "ILIKE", ["!~~"] = "NOT LIKE", ["!~~*"] = "NOT ILIKE" };
    private static readonly HashSet<string> Infix = ["+", "-", "*", "/", "%", "||", "//", "**"];
    private static readonly Dictionary<string, string> AggregateNames = new() { ["count_star"] = "count", ["sum_no_overflow"] = "sum" };
    private static readonly Dictionary<string, string> Comparisons = new()
    {
        ["COMPARE_EQUAL"] = "=", ["COMPARE_NOTEQUAL"] = "<>", ["COMPARE_LESSTHAN"] = "<", ["COMPARE_GREATERTHAN"] = ">", ["COMPARE_LESSTHANOREQUALTO"] = "<=",
        ["COMPARE_GREATERTHANOREQUALTO"] = ">=", ["COMPARE_NOT_DISTINCT_FROM"] = "IS NOT DISTINCT FROM", ["COMPARE_DISTINCT_FROM"] = "IS DISTINCT FROM",
    };
    private static readonly Dictionary<string, string> DatePartAliases = new(StringComparer.Ordinal)
    {
        ["era"] = "era", ["decade"] = "decade", ["century"] = "century", ["millennium"] = "millennium", ["isoyear"] = "isoyear", ["yearweek"] = "yearweek", ["julian"] = "julian", ["epoch"] = "epoch",
        ["microsecond"] = "microsecond", ["millisecond"] = "millisecond", ["weekofyear"] = "week", ["week"] = "week", ["weekday"] = "dow", ["dayofmonth"] = "day", ["dayofweek"] = "dow", ["dayofyear"] = "doy",
    };

    private static readonly HashSet<string> IntegerTypes = ["TINYINT", "SMALLINT", "INTEGER", "BIGINT", "HUGEINT", "UTINYINT", "USMALLINT", "UINTEGER", "UBIGINT"];

    private Func<string, IReadOnlyList<string>>? grainOf;
    private readonly Dictionary<string, string> aliasTables = new(StringComparer.Ordinal);
    private bool trimTrailing;
    private readonly Dictionary<string, Queue<string?>> aliasHints = new(StringComparer.Ordinal);
    private readonly HashSet<string> issuedAliases = new(StringComparer.Ordinal);

    private static string AliasKey(string? schema, string table) => (schema is null or "" or "main" ? table : $"{schema}.{table}").ToLowerInvariant();

    /// <summary>The alias of a scan of a table: the author's, when there is one for this scan and it is safe to use; otherwise the table's name, numbered when it is used again.</summary>
    private string AliasForTable(string baseName, string? schema)
    {
        if (aliasHints.TryGetValue(AliasKey(schema, baseName), out var queue) && queue.Count > 0 && queue.Dequeue() is { } hint)
        {
            var wanted = hint.ToLowerInvariant();
            if (Regex.IsMatch(wanted, "^[a-z_][a-z0-9_]*$") && !Reserved.Contains(wanted) && !reserved.Contains(wanted) && !issuedAliases.Contains(wanted))
            {
                uses[baseName] = uses.GetValueOrDefault(baseName) + 1;
                issuedAliases.Add(wanted);
                return wanted;
            }
        }
        string alias;
        if (!issuedAliases.Contains(baseName) && aliasHints.ContainsKey(AliasKey(schema, baseName))) { uses[baseName] = uses.GetValueOrDefault(baseName) + 1; alias = baseName; }      // an unaliased mention keeps the table's own name even after an aliased one
        else do alias = UniqueAlias(baseName, baseName); while (issuedAliases.Contains(alias));
        issuedAliases.Add(alias);
        return alias;
    }

    /// <summary>The text of an operand, trimmed when the query must ignore trailing spaces and the operand is a string.</summary>
    private string Trim(JsonElement e, string sql) => trimTrailing && TypeNameOf(e) == "VARCHAR" ? $"rtrim({sql})" : sql;

    /// <summary>A comparison of two operands; with trailing spaces ignored, both are trimmed when either is a string.</summary>
    private string Compare(JsonElement left, string leftSql, string op, JsonElement right, string rightSql) =>
        trimTrailing && (TypeNameOf(left) == "VARCHAR" || TypeNameOf(right) == "VARCHAR") ? $"(rtrim({leftSql}) {op} rtrim({rightSql}))" : $"({leftSql} {op} {rightSql})";
    private int aliasCounter;

    /// <summary>Every table and CTE name in the plan. A generated alias (`s1`, `series`, `orders_2`) never takes one of them: a table that is itself called `s1` would otherwise be captured by a derived table named `s1`.</summary>
    private readonly HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase);

    private void Reserve(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var pr in e.EnumerateObject())
            {
                if (pr.Name is "table" or "ctename" && pr.Value.ValueKind == JsonValueKind.String) reserved.Add(pr.Value.GetString()!);
                else Reserve(pr.Value);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Reserve(x);
    }

    /// <summary>`name`, then `name_2`, `name_3`, ... skipping names that are a table or CTE of the plan.</summary>
    private string UniqueAlias(string name, string usesKey)
    {
        uses[usesKey] = uses.GetValueOrDefault(usesKey) + 1;
        if (uses[usesKey] == 1) return name;
        string alias;
        do alias = $"{name}_{uses[usesKey]++}"; while (reserved.Contains(alias));
        uses[usesKey]--;     // the counter names the next use, so it ends on the one just taken
        return alias;
    }
    private readonly Dictionary<string, int> uses = [];
    private readonly Dictionary<int, (string Name, Rel Definition)> ctes = [];
    private readonly List<string> rules = [];

    /// <summary>
    /// A MARK join that is not a plain `IN` or `EXISTS`: `x op ANY/ALL (subquery)` and a row-value `(a, b) IN (subquery)`. Its value is three-valued (TRUE, FALSE, or NULL when no row matches and some comparison was
    /// unknown), and SQL Server has no boolean values, so it cannot be one expression. In a filter it can be written exactly with predicates only, by what the filter needs of it:
    /// <c>True</c> holds when the mark is TRUE (a row matches), <c>False</c> when it is FALSE (every row is definitely not a match, or there are none). A column holds a sentinel (`\u0003n\u0003`)
    /// until the filter that uses it picks one; a sentinel that reaches the output means the value was used as a value, and the query is refused.
    /// </summary>
    private readonly List<(string True, string False)> marks = [];
    private const string MarkUsedAsValue = "a subquery comparison (ANY, ALL or a row-value IN) used as a value rather than as a filter condition: its NULL result cannot be reproduced on SQL Server and PostgreSQL";

    /// <param name="outputNames">
    /// The names DuckDB gives the query's output columns (from `DESCRIBE`). The plan does not always carry them: when no projection sits at the top (a bare aggregate, for example)
    /// the author's aliases exist only in DuckDB's binder state, so they are applied to the lowered query by position.
    /// </param>
    /// <summary>Throws the <see cref="LoweringException"/> for a plan document that is DuckDB's own error (a parse or bind failure), so callers can classify it before doing anything else.</summary>
    public static void ThrowIfError(string planJson)
    {
        using var doc = JsonDocument.Parse(planJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new LoweringException(root.TryGetProperty("error_message", out var m) ? m.GetString()! : "unknown error", root.TryGetProperty("error_type", out var et) ? et.GetString()! : "binder");
    }

    /// <param name="grainOf">
    /// The columns that identify one row of a table (its declared grain), by the name the query uses (`staging.orders`). `DISTINCT ON` keeps one arbitrary row per key unless its
    /// ordering decides which, so it is lowered only when the ordering includes the grain of every table it reads; an unknown or empty grain means "cannot prove it".
    /// </param>
    /// <param name="authorAliases">
    /// The tables of the author's query in the order the plan reaches them, with the alias written for each (null: none). DuckDB's plan has no aliases, so a table would be named after itself (`orders`, `orders_2`); the n-th
    /// scan of a table takes the alias of the n-th mention of it, when the alias is a plain lowercase name that no table or earlier alias uses. An alias is a label: giving one to the wrong scan of a self-joined table is
    /// still the same query, so a plan that does not line up with the text costs readability, never correctness.
    /// </param>
    /// <param name="ignoreTrailingSpaces">
    /// Write the query as DuckDB must run it to answer as an engine that **ignores trailing spaces** in a string comparison does (the project's default profile; DuckDB cannot do it with any collation): string operands
    /// of a comparison, `IN`, `BETWEEN`, a join condition, a window partition, a `GROUP BY` key and a `DISTINCT` are wrapped in `rtrim()`. For DuckDB runs only (`sample`, `test`); never what an engine is given.
    /// Not covered: set operations without ALL, `count(DISTINCT x)`, and `LIKE` (which keeps trailing spaces on SQL Server too).
    /// </param>
    public static LoweredQuery Lower(string planJson, IReadOnlyList<string>? outputNames = null, Func<string, IReadOnlyList<string>>? grainOf = null, RewritePolicy? rewrites = null, bool ignoreTrailingSpaces = false, IReadOnlyList<(string? SchemaName, string Table, string? Alias)>? authorAliases = null)
    {
        using var doc = JsonDocument.Parse(PlanNormalizer.Normalize(planJson));
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new LoweringException(root.TryGetProperty("error_message", out var m) ? m.GetString()! : "unknown error", root.TryGetProperty("error_type", out var et) ? et.GetString()! : "binder");
        var plans = root.GetProperty("plans");
        if (plans.GetArrayLength() != 1) throw new LoweringException($"expected one plan, got {plans.GetArrayLength()}");
        var lowerer = new PlanLowerer { standardRowNulls = planJson.Contains("\"qname\"", StringComparison.Ordinal), grainOf = grainOf, rewrites = rewrites ?? RewritePolicy.Exact, trimTrailing = ignoreTrailingSpaces };
        foreach (var (schema, table, alias) in authorAliases ?? [])
        {
            var key = AliasKey(schema, table);
            if (!lowerer.aliasHints.TryGetValue(key, out var queue)) lowerer.aliasHints[key] = queue = new Queue<string?>();
            queue.Enqueue(alias);
        }
        lowerer.Reserve(plans);
        Rel rel;
        try { rel = lowerer.Node(plans[0]); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or IndexOutOfRangeException or InvalidCastException)
        {
            // a plan shape the lowerer did not expect is a query it cannot lower: refused with the reason's type (never the text of the data), not a crash
            throw new LoweringException($"a plan shape the lowerer does not handle ({ex.GetType().Name}); this is a tool limitation, please report it");
        }
        if (outputNames != null && rel.SetOp == null)
        {
            // DuckDB appends hidden columns (a DISTINCT ON key, an ORDER BY key) after the visible ones and drops them from the result: the first N are the query's columns
            if (outputNames.Count < rel.Sel.Count) rel.Sel = rel.Sel.Take(outputNames.Count).ToList();
            if (outputNames.Count != rel.Sel.Count) throw new LoweringException($"the plan has {rel.Sel.Count} output columns, but DuckDB describes {outputNames.Count}");
            rel.Sel = rel.Sel.Select((s, i) => s with { Alias = outputNames[i] }).ToList();
        }
        var sql = rel.Cte(lowerer.ctesInOrder, lowerer.hasRecursiveCte) + rel.Sql();
        if (sql.Contains('\u0003')) sql = lowerer.MarksAsValues(sql);
        var names = rel.SetOp != null ? rel.SetOpNames! : rel.Aliases();
        var columns = names.Select((n, i) => new LoweredColumn(n, rel.Sel.Count > i ? rel.Sel[i].Type ?? "UNKNOWN" : "UNKNOWN")).ToList();
        return new LoweredQuery(sql, columns, lowerer.rules.Distinct().Order(StringComparer.Ordinal).ToList()) { Tables = lowerer.aliasTables.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };
    }

    private bool standardRowNulls;
    private readonly List<(string Name, string Sql)> ctesInOrder = [];
    private bool hasRecursiveCte;
    private RewritePolicy rewrites = RewritePolicy.Exact;

    /// <summary>Whether a rewrite that keeps an engine equal to DuckDB is applied (the model or the project may have asked for the engine's own behavior).</summary>
    private bool On(string rewrite) => rewrites.Allows(rewrite);

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------

    private static string Ident(string n) => Regex.IsMatch(n, "^[a-z_][a-z0-9_]*$") && !Reserved.Contains(n) ? n : "\"" + n.Replace("\"", "\"\"") + "\"";

    private static string TypeName(JsonElement t)
    {
        var id = t.GetProperty("id").GetString()!;
        if (id == "DECIMAL" && t.TryGetProperty("type_info", out var ti))
            return $"DECIMAL({(ti.TryGetProperty("width", out var w) ? w.GetInt32() : 18)}, {(ti.TryGetProperty("scale", out var s) ? s.GetInt32() : 3)})";
        return id;
    }

    /// <summary>The text of a VARCHAR constant of the plan, or null when the expression is not one.</summary>
    private static string? StringConstant(JsonElement e) =>
        Str(e, "type") == "VALUE_CONSTANT" && e.GetProperty("value") is var v && !(v.TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True) && v.GetProperty("type").GetProperty("id").GetString() == "VARCHAR"
            ? (v.TryGetProperty("value", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : "") : null;

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    private static string? TypeId(JsonElement e) => e.TryGetProperty("return_type", out var t) ? t.GetProperty("id").GetString() : null;

    /// <summary>The column name behind a marker (`\0alias\u0001name\0`), or null when the text is not just a column.</summary>
    private static string? ColumnNameOf(string sql) => Regex.Match(sql, "^\0([^\u0001\0]*)\u0001([^\0]*)\0$") is { Success: true } m ? m.Groups[2].Value : null;

    /// <summary>The marker for a column of a source; <see cref="Rel.Render"/> turns it into `source.column` or a bare name.</summary>
    private static string Token(string alias, string name) => $"\0{alias}\u0001{name}\0";

    private static string Literal(JsonElement v)
    {
        if (v.TryGetProperty("is_null", out var isNull) && isNull.ValueKind == JsonValueKind.True) return "NULL";
        var type = v.GetProperty("type").GetProperty("id").GetString()!;
        // the plan is serialized with skip_empty, which drops the value of an empty string: '' is a VARCHAR constant with no `value`
        if (!v.TryGetProperty("value", out var x))
            return type == "VARCHAR" ? "''" : throw new LoweringException($"a literal of type {type} without a value");
        switch (type)
        {
            case "DECIMAL":
            {
                var scale = v.GetProperty("type").TryGetProperty("type_info", out var ti) && ti.TryGetProperty("scale", out var sc) ? sc.GetInt32() : 0;
                var n = IntegerValue(x);
                var digits = BigInteger.Abs(n).ToString().PadLeft(scale + 1, '0');
                return (n.Sign < 0 ? "-" : "") + (scale > 0 ? digits[..^scale] + "." + digits[^scale..] : digits);
            }
            case "INTERVAL" when x.ValueKind == JsonValueKind.Object && x.TryGetProperty("months", out var months) && x.TryGetProperty("days", out var days0) && x.TryGetProperty("micros", out var micros):
            {
                // DuckDB 2.0 folds INTERVAL 3 DAY into a constant (1.x kept to_days(3)); one unit is written back, which unit of equal length (12 MONTH or 1 YEAR) is not recoverable
                long mo = months.GetInt64(), da = days0.GetInt64(), mi = micros.GetInt64();
                if (mo != 0 && da == 0 && mi == 0) return mo % 12 == 0 ? $"INTERVAL {mo / 12} YEAR" : $"INTERVAL {mo} MONTH";
                if (mo == 0 && da != 0 && mi == 0) return $"INTERVAL {da} DAY";
                if (mo == 0 && da == 0 && mi != 0)
                {
                    if (mi % 3_600_000_000 == 0) return $"INTERVAL {mi / 3_600_000_000} HOUR";
                    if (mi % 60_000_000 == 0) return $"INTERVAL {mi / 60_000_000} MINUTE";
                    if (mi % 1_000_000 == 0) return $"INTERVAL {mi / 1_000_000} SECOND";
                }
                // several units at once, or a fraction of a second: the text form, which an engine without an interval type reads part by part (target rule interval-dateadd) and PostgreSQL reads as it is
                var parts = new IntervalParts(mo, da, mi);
                return $"CAST('{parts.ToText()}' AS INTERVAL)";
            }
            case "VARCHAR": return "'" + x.GetString()!.Replace("'", "''") + "'";
            case "BOOLEAN": return x.ValueKind == JsonValueKind.True || (x.ValueKind == JsonValueKind.String && x.GetString() is "t" or "true") ? "TRUE" : "FALSE";
            case "DATE":
                if (x.ValueKind == JsonValueKind.String) return $"DATE '{x.GetString()}'";
                // a DATE that DuckDB folded early (the default of a macro parameter, for one) is serialized as days since 1970-01-01. `CAST(19904 AS DATE)` is an error in DuckDB and PostgreSQL and 1954-... on SQL Server.
                var days = IntegerValue(x);
                if (days < -719162 || days > 2932896) throw new LoweringException($"a DATE literal outside the years 0001 to 9999 ({days} days from 1970-01-01)");
                return $"DATE '{DateOnly.FromDayNumber(719162 + (int)days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}'";
            case "DOUBLE" or "FLOAT":
            {
                var d = double.Parse(x.GetRawText(), System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                return d.Contains('.') || d.Contains('E') ? d : d + ".0";
            }
            default:
                if (IntegerTypes.Contains(type)) return IntegerValue(x).ToString();
                throw new LoweringException($"a literal of type {type}");
        }
    }

    /// <summary>
    /// A whole number of a plan: a JSON number, or, for what DuckDB stores in 128 bits (a HUGEINT, a DECIMAL wider than 18 digits), `{"upper": signed 64 bits, "lower": unsigned 64 bits}`, whose value is upper * 2^64 + lower.
    /// </summary>
    private static BigInteger IntegerValue(JsonElement x) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty("upper", out var upper) && x.TryGetProperty("lower", out var lower)
            ? (new BigInteger(upper.GetInt64()) << 64) + new BigInteger(lower.GetUInt64())
            : BigInteger.Parse(x.GetRawText());

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    // expressions

    private string Expr(JsonElement e, IReadOnlyList<string> outs)
    {
        var t = e.GetProperty("type").GetString()!;
        switch (t)
        {
            case "BOUND_REF": return outs[e.GetProperty("index").GetInt32()];
            case "VALUE_CONSTANT": return Literal(e.GetProperty("value"));
            case "OPERATOR_CAST": return Cast(e, outs);
            case "BOUND_FUNCTION": return Function(e, outs);
            case "BOUND_AGGREGATE": return Aggregate(e, outs);
            case "COMPARE_BETWEEN" or "COMPARE_NOT_BETWEEN":
            {
                // the binder keeps BETWEEN as one node when its input is not a plain column or constant (a subquery, for example)
                var input = Trim(e.GetProperty("input"), Expr(e.GetProperty("input"), outs));
                var lower = Trim(e.GetProperty("lower"), Expr(e.GetProperty("lower"), outs));
                var upper = Trim(e.GetProperty("upper"), Expr(e.GetProperty("upper"), outs));
                var both = e.GetProperty("lower_inclusive").ValueKind == JsonValueKind.True && e.GetProperty("upper_inclusive").ValueKind == JsonValueKind.True;
                if (!both) throw new LoweringException("a BETWEEN with an exclusive bound");
                return $"({input} {(t == "COMPARE_NOT_BETWEEN" ? "NOT " : "")}BETWEEN {lower} AND {upper})";
            }
            case "COMPARE_IN" or "COMPARE_NOT_IN":
            {
                var members = Arr(e, "children").ToList();
                var ch = members.Select(c => Trim(c, Expr(c, outs))).ToList();
                return $"({ch[0]} {(t == "COMPARE_NOT_IN" ? "NOT " : "")}IN ({string.Join(", ", ch.Skip(1))}))";
            }
            case "OPERATOR_COALESCE": return $"coalesce({string.Join(", ", Arr(e, "children").Select(c => Expr(c, outs)))})";
            case "OPERATOR_IS_NULL": return $"({Expr(Arr(e, "children").First(), outs)} IS NULL)";
            case "OPERATOR_IS_NOT_NULL": return $"({Expr(Arr(e, "children").First(), outs)} IS NOT NULL)";
            case "OPERATOR_NOT": return $"(NOT {Expr(Arr(e, "children").First(), outs)})";
            case "CONJUNCTION_AND": return "(" + string.Join(" AND ", Arr(e, "children").Select(c => Expr(c, outs))) + ")";
            case "CONJUNCTION_OR": return "(" + string.Join(" OR ", Arr(e, "children").Select(c => Expr(c, outs))) + ")";
            case "CASE_EXPR":
            {
                // a branch chosen by a condition that is constant (a macro's `CASE WHEN col IS NULL ...` over a column name or NULL given as an argument) is the query for that argument: only the
                // chosen branch is written, so nothing is left for the engine to decide
                if (IsAnyAllGuard(e)) throw new LoweringException("a subquery comparison (ANY or ALL) in the form DuckDB 2.0 plans it (a CASE over the count and the min or max of the subquery); only 1.x's form is lowered so far");
                if (FoldConstantCase(e, outs) is { } folded) return folded;
                var sb = new StringBuilder("CASE ");
                var checks = Arr(e, "case_checks").ToList();
                foreach (var c in checks) sb.Append($"WHEN {Expr(c.GetProperty("when_expr"), outs)} THEN {Expr(c.GetProperty("then_expr"), outs)} ");
                if (e.TryGetProperty("else_expr", out var el)) sb.Append($"ELSE {Expr(el, outs)} ");
                // DuckDB's guard for count(*) over a correlated subquery that matched no rows: `CASE WHEN count(*) IS NULL THEN 0 ELSE count(*) END`. A count is never NULL here.
                if (checks.Count == 1 && e.TryGetProperty("else_expr", out var guarded) && Expr(guarded, outs) is var inner && inner.StartsWith("count(", StringComparison.Ordinal) &&
                    Expr(checks[0].GetProperty("when_expr"), outs) == $"({inner} IS NULL)" && Expr(checks[0].GetProperty("then_expr"), outs) == "0")
                    return inner;
                return sb.Append("END").ToString();
            }
        }
        if (Comparisons.TryGetValue(t, out var op)) return Compare(e.GetProperty("left"), Expr(e.GetProperty("left"), outs), op, e.GetProperty("right"), Expr(e.GetProperty("right"), outs));
        if (t.StartsWith("WINDOW_", StringComparison.Ordinal)) return Window(e, outs);
        throw new LoweringException($"expression kind {t}");
    }

    /// <summary>
    /// `CASE WHEN count IS NULL THEN FALSE ELSE CASE WHEN count = 0 THEN FALSE ELSE ...`: how DuckDB 2.0 writes `x op ANY (S)` for an uncorrelated S, over a one-row aggregate of S (min or max, count(*), count(x)).
    /// A boolean CASE is not an expression SQL Server can use as a condition, so it is refused rather than written.
    /// </summary>
    private static bool IsAnyAllGuard(JsonElement e)
    {
        static bool FalseConstant(JsonElement x) => Str(x, "type") == "VALUE_CONSTANT" && x.GetProperty("value").TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.False;
        if (Arr(e, "case_checks").ToList() is not [{ } first] || Str(first.GetProperty("when_expr"), "type") != "OPERATOR_IS_NULL" || !FalseConstant(first.GetProperty("then_expr"))) return false;
        return e.TryGetProperty("else_expr", out var rest) && Str(rest, "type") == "CASE_EXPR" && Arr(rest, "case_checks").ToList() is [{ } second] && FalseConstant(second.GetProperty("then_expr")) &&
               Str(second.GetProperty("when_expr"), "type") == "COMPARE_EQUAL";
    }

    /// <summary>
    /// `CASE` whose conditions are known when the query is written: the branches that cannot be taken are dropped, and when the first one that can be is certain, it is the result. Null when a condition is
    /// not constant (the CASE is written as it is). A constant is a literal, or a cast of one, tested with IS [NOT] NULL, a boolean literal, or a NOT of one of those.
    /// </summary>
    private string? FoldConstantCase(JsonElement e, IReadOnlyList<string> outs)
    {
        var checks = Arr(e, "case_checks").ToList();
        var kept = new List<JsonElement>();
        foreach (var c in checks)
        {
            var truth = ConstantTruth(c.GetProperty("when_expr"));
            if (truth == null) kept.Add(c);
            else if (truth == true)
            {
                if (kept.Count == 0) return Expr(c.GetProperty("then_expr"), outs);
                // earlier conditions are not constant: this branch is the ELSE
                var sb = new StringBuilder("CASE ");
                foreach (var k in kept) sb.Append($"WHEN {Expr(k.GetProperty("when_expr"), outs)} THEN {Expr(k.GetProperty("then_expr"), outs)} ");
                return sb.Append($"ELSE {Expr(c.GetProperty("then_expr"), outs)} END").ToString();
            }
        }
        if (kept.Count == checks.Count) return null;                                   // nothing folded
        if (kept.Count == 0) return e.TryGetProperty("else_expr", out var only) ? Expr(only, outs) : null;
        var text = new StringBuilder("CASE ");
        foreach (var k in kept) text.Append($"WHEN {Expr(k.GetProperty("when_expr"), outs)} THEN {Expr(k.GetProperty("then_expr"), outs)} ");
        if (e.TryGetProperty("else_expr", out var el)) text.Append($"ELSE {Expr(el, outs)} ");
        return text.Append("END").ToString();
    }

    /// <summary>The expression a CASE with constant conditions comes to (the branch that is taken), or null when it does not come to one.</summary>
    private static JsonElement? ChosenBranch(JsonElement e)
    {
        foreach (var c in Arr(e, "case_checks"))
        {
            var truth = ConstantTruth(c.GetProperty("when_expr"));
            if (truth == null) return null;
            if (truth == true) return c.GetProperty("then_expr");
        }
        return e.TryGetProperty("else_expr", out var el) ? el : null;
    }

    private static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase) { "random", "uuid", "gen_random_uuid", "nextval", "currval", "setseed" };

    /// <summary>True when the value of the expression depends on no column: only literals, functions of those that are not volatile, and the items of the child that are themselves like that.</summary>
    private static bool IsColumnFree(JsonElement e, IReadOnlyList<Item> childItems)
    {
        switch (Str(e, "type"))
        {
            case "BOUND_REF": return e.GetProperty("index").GetInt32() is var i && i < childItems.Count && childItems[i].Const;
            case "VALUE_CONSTANT": return true;
            case "OPERATOR_CAST": return IsColumnFree(e.GetProperty("child"), childItems);
            case "CASE_EXPR": return ChosenBranch(e) is { } chosen && IsColumnFree(chosen, childItems);
            case "BOUND_FUNCTION": return !Volatile.Contains(Str(e, "name") ?? "") && Arr(e, "children").All(c => IsColumnFree(c, childItems));
            case "OPERATOR_COALESCE" or "OPERATOR_IS_NULL" or "OPERATOR_IS_NOT_NULL" or "OPERATOR_NOT" or "CONJUNCTION_AND" or "CONJUNCTION_OR": return Arr(e, "children").All(c => IsColumnFree(c, childItems));
            default: return false;
        }
    }

    /// <summary>true or false when the condition is a constant of the query, null when it depends on a row.</summary>
    private static bool? ConstantTruth(JsonElement e)
    {
        switch (Str(e, "type"))
        {
            case "OPERATOR_IS_NULL" or "OPERATOR_IS_NOT_NULL":
            {
                var child = Arr(e, "children").FirstOrDefault();
                while (child.ValueKind == JsonValueKind.Object && Str(child, "type") == "OPERATOR_CAST") child = child.GetProperty("child");
                if (child.ValueKind != JsonValueKind.Object || Str(child, "type") != "VALUE_CONSTANT") return null;
                var isNull = child.GetProperty("value").TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True;
                return Str(e, "type") == "OPERATOR_IS_NULL" ? isNull : !isNull;
            }
            case "OPERATOR_NOT": return Arr(e, "children").FirstOrDefault() is { ValueKind: JsonValueKind.Object } inner ? !ConstantTruth(inner) : null;
            case "VALUE_CONSTANT":
            {
                var v = e.GetProperty("value");
                if (v.TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True) return false;
                return v.TryGetProperty("type", out var ty) && ty.TryGetProperty("id", out var id) && id.GetString() == "BOOLEAN" && v.TryGetProperty("value", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? b.ValueKind == JsonValueKind.True : null;
            }
            default: return null;
        }
    }

    private string Cast(JsonElement e, IReadOnlyList<string> outs)
    {
        var child = e.GetProperty("child");
        var to = TypeId(e)!;
        // An enum has no counterpart on an engine and its check on the value (an error for a value outside the list) cannot be reproduced by a cast: refused, not left as the word ENUM for the engine to fail on.
        if (to == "ENUM" || TypeId(child) == "ENUM") throw new LoweringException("a cast to or from an enum type (a CREATE TYPE ... AS ENUM; an engine has no such type, so keep the value as text and check it in a test, or give the type an alias such as CREATE TYPE money AS DECIMAL(12, 2))");
        // constants the binder widened to the context type read better as plain literals; the engine types them by context
        if (child.GetProperty("type").GetString() == "VALUE_CONSTANT" && !(child.GetProperty("value").TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True))
        {
            var v = child.GetProperty("value");
            var lit = v.GetProperty("type").GetProperty("id").GetString()!;
            // (in a VALUES cell only the types whose bare literal the engines type the same way: SQL Server types 7 as INT, 2147483648 as NUMERIC, TRUE as 1 and 1.5 as NUMERIC)
            if (lit == to && (to is "VARCHAR" or "INTEGER" || (!exactCasts && to is "BIGINT" or "SMALLINT" or "BOOLEAN" or "DOUBLE"))) return Literal(v);
            if (!exactCasts && lit is "INTEGER" or "BIGINT" or "SMALLINT" && to is "INTEGER" or "BIGINT" or "SMALLINT" or "DECIMAL" or "DOUBLE" or "HUGEINT") return Literal(v);
            if (lit == "VARCHAR" && to == "BOOLEAN" && v.GetProperty("value").GetString() is "t" or "f" or "true" or "false") return exactCasts ? $"CAST({(v.GetProperty("value").GetString() is "t" or "true" ? "TRUE" : "FALSE")} AS BOOLEAN)" : v.GetProperty("value").GetString() is "t" or "true" ? "TRUE" : "FALSE";
            if (lit == "VARCHAR" && to is "DATE" or "TIMESTAMP" or "TIME") return $"{to} {Literal(v)}";
        }
        var fn = e.TryGetProperty("try_cast", out var tc) && tc.ValueKind == JsonValueKind.True ? "TRY_CAST" : "CAST";
        var inner = Expr(child, outs);
        // RULE try-cast-parse: parsing a string differs per engine ('' is NULL here, 0 on SQL Server, an error on PostgreSQL). The explicit VARCHAR cast marks the string source for the target step.
        if (On("try-cast-parse") && fn == "TRY_CAST" && TypeId(child) == "VARCHAR" && to is "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "DECIMAL" or "DOUBLE" or "FLOAT" or "DATE" or "TIMESTAMP")
        {
            inner = $"CAST({inner} AS VARCHAR)";
            rules.Add("try-cast-parse");
        }
        // RULE decimal-to-int: DuckDB rounds a DECIMAL to the nearest integer, half away from zero (2.5 is 3); SQL Server truncates (2). round() is the same on all three.
        if (On("decimal-to-int") && fn == "CAST" && TypeId(child) == "DECIMAL" && to is "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT")
        {
            inner = $"round({inner}, 0)";
            rules.Add("decimal-to-int");
        }
        // RULE double-to-int: DuckDB rounds a DOUBLE to the nearest integer, half to even (2.5 is 2, 3.5 is 4); SQL Server truncates, PostgreSQL does the same as DuckDB. The explicit DOUBLE cast marks it for the target step.
        else if (On("double-to-int") && fn == "CAST" && TypeId(child) is "DOUBLE" or "FLOAT" && to is "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT")
        {
            inner = $"CAST({inner} AS DOUBLE)";
            rules.Add("double-to-int");
        }
        // RULE double-to-decimal: DuckDB scales a DOUBLE by the decimal's scale and rounds half away from zero (819.025 is 819.03, 0.285 is 0.28 because 0.285 * 100 is 28.499...); SQL Server converts the exact binary
        // value (819.02) and PostgreSQL the shortest text (0.29). round(double, n) is what DuckDB does, and it is the shape the round-double target rule rewrites on every engine.
        else if (On("double-to-decimal") && fn == "CAST" && TypeId(child) is "DOUBLE" or "FLOAT" && to == "DECIMAL" && e.GetProperty("return_type").TryGetProperty("type_info", out var decimalInfo) && decimalInfo.TryGetProperty("scale", out var decimalScale) && decimalScale.GetInt32() is > 0 and <= 15)
        {
            inner = $"round(CAST({inner} AS DOUBLE), {decimalScale.GetInt32()})";
            rules.Add("double-to-decimal");
        }
        return $"{fn}({inner} AS {TypeName(e.GetProperty("return_type"))})";
    }

    private string Function(JsonElement e, IReadOnlyList<string> outs)
    {
        var name = Str(e, "name")!;
        var children = Arr(e, "children").ToList();
        if (name is "to_days" or "to_hours" or "to_minutes" or "to_seconds" or "to_months" or "to_years" or "to_weeks" or "to_quarters" or "to_decades" or "to_centuries" or "to_millennia" or "to_milliseconds" or "to_microseconds" && children.Count == 1)
        {
            // the binder turns INTERVAL 3 DAY into to_days(CAST(trunc(CAST(3 AS DOUBLE)) AS INTEGER)); fold the constant back
            var c = children[0];
            while (c.GetProperty("type").GetString() == "OPERATOR_CAST" || (c.GetProperty("type").GetString() == "BOUND_FUNCTION" && Str(c, "name") == "trunc"))
                c = c.GetProperty("type").GetString() == "OPERATOR_CAST" ? c.GetProperty("child") : Arr(c, "children").First();
            if (c.GetProperty("type").GetString() == "VALUE_CONSTANT")
            {
                // a decade, century or millennium is a count of years and a quarter three months (no engine has those units); the other units are written by name
                var count = Literal(c.GetProperty("value"));
                if (name is "to_decades" or "to_centuries" or "to_millennia" && long.TryParse(count, out var n))
                    return $"INTERVAL {checked(n * (name == "to_decades" ? 10 : name == "to_centuries" ? 100 : 1000))} YEAR";
                if (name == "to_quarters" && long.TryParse(count, out var quarters)) return $"INTERVAL {checked(quarters * 3)} MONTH";
                return $"INTERVAL {count} {name[3..^1].ToUpperInvariant()}";
            }
            throw new LoweringException("an interval built from an expression");
        }
        // the JSON arrows are the functions json_extract_string and json_extract written as operators, which the transpile does not read
        if (name == "->>") name = "json_extract_string";
        else if (name == "->") name = "json_extract";
        if (name is "list_value" or "struct_pack" or "map") throw new LoweringException($"the nested-type function {name}");
        // `current_timestamp` is bound as get_current_timestamp(), which no engine has (and polyglot does not translate); `now()` is the same value and is translated. transaction_timestamp() is `now()` in DuckDB too
        if (name is "get_current_timestamp" or "transaction_timestamp" && children.Count == 0) name = "now";
        // `localtime` is bound as current_localtime(), which polyglot writes as CURRENT_LOCALTIME() (no engine has it); it is the time part of `localtimestamp`, which is translated
        if (name == "current_localtime" && children.Count == 0) return "CAST(current_localtimestamp() AS TIME)";
        // DuckDB answers about its own session, version and transactions; no engine's answer is the same, so none is written
        if (SessionFunctions.Contains(name)) throw new LoweringException($"the session function {name}() (DuckDB answers about its own session, version or transaction; an engine's answer differs)");
        var a = children.Select(c => Expr(c, outs)).ToList();
        var returns = TypeId(e);
        // add(a, b), subtract, multiply and divide are the operators under another name (the plan keeps the name the query used); neither engine has a function of that name
        if (a.Count == 2) name = name switch { "add" => "+", "subtract" => "-", "multiply" => "*", "divide" => "//", _ => name };
        if (name == "age" && children.Any(c => TypeId(c) == "TIMESTAMP WITH TIME ZONE")) throw new LoweringException("age over TIMESTAMP WITH TIME ZONE values (the result depends on the session time zone of each engine; cast to TIMESTAMP in the zone you mean)");
        // strftime and format are written out as the pieces of their text (TextFormats): no engine's format function uses DuckDB's letters
        if (name == "strftime" && a.Count == 2)
        {
            var formatAt = StringConstant(children[1]) != null ? 1 : StringConstant(children[0]) != null ? 0 : -1;
            if (formatAt < 0) throw new LoweringException("strftime with a format that is not a literal (the specifiers are translated one by one, so the format has to be known)");
            var timeAt = 1 - formatAt;
            var timeType = TypeId(children[timeAt]);
            if (timeType is not ("DATE" or "TIMESTAMP")) throw new LoweringException($"strftime over {timeType} (only DATE and TIMESTAMP values are formatted exactly)");
            return TextFormats.Strftime(timeType == "DATE" ? $"CAST({a[timeAt]} AS TIMESTAMP)" : a[timeAt], StringConstant(children[formatAt])!);
        }
        if (name == "format" && a.Count >= 1 && StringConstant(children[0]) is { } formatTemplate)
        {
            // each argument is cast to text as the engine's own cast writes it; the types whose text is not DuckDB's (a double: 1e+10 for 10000000000.0; a timestamp: seven fraction digits; a decimal: trailing zeros) are refused
            var pieces = new List<string>();
            for (var i = 1; i < children.Count; i++)
            {
                var t = TypeId(children[i]);
                if (t is not ("VARCHAR" or "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "DATE"))
                    throw new LoweringException($"format with an argument of type {t} (its text differs between DuckDB and the engines; cast it to VARCHAR first, with the digits you want)");
                pieces.Add(t == "VARCHAR" ? a[i] : $"CAST({a[i]} AS VARCHAR)");
            }
            return TextFormats.Format(formatTemplate, pieces);
        }
        if (name == "format") throw new LoweringException("format with a template that is not a literal (the placeholders are translated one by one, so the template has to be known)");
        // position(needle IN haystack) is `position(haystack, needle)` in the plan, which reads the other way round as written; strpos(haystack, needle) says what it means
        if (name == "position" && a.Count == 2) name = "strpos";
        // SQL Server's SUBSTRING needs a length; DuckDB's two-argument form runs to the end of the string
        if (name is "substr" or "substring" && a.Count == 2) a.Add("2147483647");
        // the date parts that are functions in DuckDB are not on every engine; date_part is on all of them
        if (name is "year" or "month" or "day" or "hour" or "minute" or "second" or "quarter" && a.Count == 1) return $"date_part('{name}', {a[0]})";
        if (name is "dayofweek" or "isodow" or "dayofyear" && a.Count == 1) return $"date_part('{(name == "dayofweek" ? "dow" : name == "dayofyear" ? "doy" : name)}', {a[0]})";
        // the other parts are functions too (plan: julian(x), epoch(x); the query may say era(x), decade(x), weekofyear(x) ...) and a part may be written under another name: one spelling, which the target rules read
        if (DatePartAliases.TryGetValue(name, out var partName) && a.Count == 1) return $"date_part('{partName}', {a[0]})";
        if (name is "date_part" or "datepart" && a.Count == 2 && StringConstant(children[0]) is { } writtenPart) return $"date_part('{DatePartAliases.GetValueOrDefault(writtenPart.ToLowerInvariant(), writtenPart.ToLowerInvariant())}', {a[1]})";
        // RULE int-div: `//` on integers truncates toward zero. polyglot cannot read it, and the engines' own integer division is spelled `/`, which in DuckDB's dialect is float division
        if (name == "//" && a.Count == 2)
            return returns is "INTEGER" or "SMALLINT" or "TINYINT" ? $"CAST(trunc(CAST({a[0]} AS DOUBLE) / CAST(NULLIF({a[1]}, 0) AS DOUBLE)) AS {returns})" : throw new LoweringException($"integer division `//` over {returns}");
        // RULE date-plus-days: `date + 3` and `date - i` add and take away days in DuckDB and PostgreSQL; SQL Server does not take a whole number as an interval. The explicit casts mark the expression for the
        // target step (a date and a whole number side by side), which writes it as an interval of that many days on SQL Server.
        var argTypes = Arr(e, "arguments").Select(x => x.TryGetProperty("id", out var xid) ? xid.GetString() ?? "" : "").ToList();   // a constant has no return type of its own, but the function lists its argument types
        if (On("date-plus-days") && name is "+" or "-" && a.Count == 2 && argTypes is ["DATE", var daysType] && IntegerTypes.Contains(daysType) && returns == "DATE")
        {
            rules.Add("date-plus-days");
            return $"(CAST({a[0]} AS DATE) {name} CAST({a[1]} AS {daysType}))";
        }
        if (On("date-plus-days") && name == "+" && a.Count == 2 && argTypes is [var leadingDays, "DATE"] && IntegerTypes.Contains(leadingDays) && returns == "DATE")
        {
            rules.Add("date-plus-days");
            return $"(CAST({a[0]} AS {leadingDays}) + CAST({a[1]} AS DATE))";
        }
        // the days between two dates is `a - b` in DuckDB; SQL Server has no subtraction of dates
        if (name == "-" && a.Count == 2 && TypeId(children[0]) == "DATE" && TypeId(children[1]) == "DATE") return $"date_diff('day', {a[1]}, {a[0]})";
        // DuckDB gives NULL for x % 0 where both engines raise an error
        if (name == "%" && a.Count == 2 && !(children[1].GetProperty("type").GetString() == "VALUE_CONSTANT" && !IsZeroConstant(children[1].GetProperty("value")))) a[1] = $"NULLIF({a[1]}, 0)";
        // RULE round-double: DuckDB rounds the scaled double half away from zero; the engines do something else. The explicit DOUBLE cast marks it for the target step.
        // A FLOAT (32 bits) is rounded as its exact value in double arithmetic (DuckDB: round(CAST(1.005 AS FLOAT), 2) is 1, not 1.01) and the result is a FLOAT again: written with the casts, in a form the rule handles.
        var roundsFloat = false;
        if (On("round-double") && name == "round" && a.Count is 1 or 2 && TypeId(children[0]) is "DOUBLE" or "FLOAT" && (a.Count == 1 || children[1].GetProperty("type").GetString() == "VALUE_CONSTANT"))
        {
            roundsFloat = TypeId(children[0]) == "FLOAT";
            a[0] = roundsFloat ? $"CAST(CAST({a[0]} AS FLOAT) AS DOUBLE)" : $"CAST({a[0]} AS DOUBLE)";      // (the FLOAT cast is what tells the target step the operand is 32 bits wide)
            rules.Add("round-double");
        }
        // trunc(double, n) cuts toward zero at n digits; PostgreSQL has trunc(numeric, integer) only. The DOUBLE cast marks it for the target step (a FLOAT is cut as its exact value and is a FLOAT again).
        if (On("trunc-digits") && name == "trunc" && a.Count == 2 && TypeId(children[0]) is "DOUBLE" or "FLOAT" && children[1].GetProperty("type").GetString() == "VALUE_CONSTANT")
        {
            var floatResult = TypeId(children[0]) == "FLOAT";
            a[0] = floatResult ? $"CAST(CAST({a[0]} AS FLOAT) AS DOUBLE)" : $"CAST({a[0]} AS DOUBLE)";
            rules.Add("trunc-digits");
            if (floatResult) return $"CAST(trunc({a[0]}, {a[1]}) AS FLOAT)";
        }
        // round(x) is round(x, 0); SQL Server has no one-argument form
        if (name == "round" && a.Count == 1 && !roundsFloat && TypeId(children[0]) != "DOUBLE") a.Add("0");
        // power takes doubles in DuckDB; a whole-number constant the binder widened is written as a plain literal, which T-SQL types as an int (POWER(2, -1) is 0 there, POWER(2, 62) an overflow): spelled as a double
        if (name is "power" or "pow" or "^" or "**" && a.Count == 2)
            for (var i = 0; i < 2; i++)
                if (Regex.IsMatch(a[i], @"^\(?-?\d+\)?$")) a[i] = $"CAST({a[i]} AS DOUBLE)";
        // the operators that have no function-call spelling the parser reads: the plan names them by their symbols (`&`, `|`, `<<`, `>>`, `^`, `^@`, and `!__postfix` for the postfix factorial)
        if (name == "^@" && a.Count == 2) return $"starts_with({a[0]}, {a[1]})";
        if (name == "!__postfix" && a.Count == 1) return $"factorial({a[0]})";
        if (name == "^" && a.Count == 2) return $"({a[0]} ** {a[1]})";                            // `^` is the power operator in DuckDB (the exclusive or is xor())
        if (name is "&" or "|" && a.Count == 2) return $"({a[0]} {name} {a[1]})";
        if (name is "<<" or ">>" && a.Count == 2)
        {
            // The target step writes a shift as arithmetic, for which it needs the width of the value: the explicit cast says it (and changes nothing in DuckDB, where the value has that type already).
            // A TINYINT is signed here and unsigned on SQL Server, and the unsigned and 128-bit types have no counterpart: refused.
            var shifted = argTypes.Count > 0 ? argTypes[0] : TypeId(children[0]);
            if (shifted is not ("SMALLINT" or "INTEGER" or "BIGINT")) throw new LoweringException($"the shift operator {name} over {shifted ?? "an unknown type"} (only SMALLINT, INTEGER and BIGINT can be shifted exactly on every engine)");
            var shiftRule = name == "<<" ? "shift-left" : "shift-right";
            if (!On(shiftRule)) return $"({a[0]} {name} {a[1]})";                                  // the engine's own shift, as the project asked
            rules.Add(shiftRule);
            return $"(CAST({a[0]} AS {shifted}) {name} {a[1]})";
        }
        var anyDate = children.Any(c => TypeId(c) == "DATE");
        string text;
        if (LikeFunctions.TryGetValue(name, out var like) && a.Count == 2) return $"({a[0]} {like} {a[1]})";
        // FLOAT / FLOAT is a FLOAT in DuckDB, the correctly rounded 32-bit quotient, which the quotient of the two exact values in double arithmetic rounded to a FLOAT is (the engines would otherwise keep 64 bits)
        if (name == "/" && a.Count == 2 && returns == "FLOAT") return $"CAST((CAST({a[0]} AS DOUBLE) / CAST({a[1]} AS DOUBLE)) AS FLOAT)";
        if (roundsFloat) return $"CAST(round({string.Join(", ", a)}) AS FLOAT)";
        if (Infix.Contains(name) && a.Count == 2) text = $"({a[0]} {name} {a[1]})";
        else if (name == "-" && a.Count == 1) return $"(-{a[0]})";
        else text = $"{name}({string.Join(", ", a)})";
        // RULE date-to-timestamp: DuckDB widens a DATE to TIMESTAMP in date_trunc and in interval arithmetic; the engines keep a DATE. Say so in the query.
        if (On("date-to-timestamp") && returns == "TIMESTAMP" && anyDate && name is "date_trunc" or "datetrunc" or "+" or "-")
        {
            rules.Add("date-to-timestamp");
            return $"CAST({text} AS TIMESTAMP)";
        }
        return text;
    }

    /// <summary>Functions whose answer is a fact about the DuckDB session that planned the query (its schema, database, version, transaction, connection and query text), which no target can reproduce.</summary>
    private static readonly HashSet<string> SessionFunctions = new(StringComparer.Ordinal)
    {
        "version", "txid_current", "current_schema", "current_schemas", "current_database", "current_connection_id", "current_query", "current_query_id", "current_transaction_id", "current_setting", "getenv",
    };

    private static bool IsZeroConstant(JsonElement value) =>
        value.TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True || !value.TryGetProperty("value", out var x) || x.ValueKind == JsonValueKind.Number && x.GetRawText().Trim('0', '.', '-') == "";

    /// <summary>Aggregates whose value over no rows is NULL (a count is 0, and so is not on this list).</summary>
    private static readonly HashSet<string> NullOnEmpty = new(StringComparer.OrdinalIgnoreCase)
    {
        "min", "max", "sum", "sum_no_overflow", "avg", "mean", "first", "last", "any_value", "arbitrary", "string_agg", "group_concat", "listagg", "bool_and", "bool_or",
        "stddev", "stddev_samp", "stddev_pop", "var_samp", "var_pop", "variance", "mode",
    };

    private static readonly HashSet<string> NullPropagating = new(StringComparer.Ordinal) { "+", "-", "*", "/", "%", "//", "abs", "round", "floor", "ceil", "ceiling", "sqrt", "ln", "exp", "sign", "trunc" };

    /// <summary>True when the expression is NULL whenever a column it reads is NULL (arithmetic and casts of columns), so a row of NULLs stays a row of NULLs through it.</summary>
    private static bool NullWhenInputIsNull(JsonElement e) => Str(e, "type") switch
    {
        "BOUND_REF" => true,
        "OPERATOR_CAST" => NullWhenInputIsNull(e.GetProperty("child")),
        "BOUND_FUNCTION" => NullPropagating.Contains(Str(e, "name") ?? "") && Arr(e, "children").Any(NullWhenInputIsNull),
        _ => false,
    };

    private static string? SumWidening(string? type) => type switch { "TINYINT" or "SMALLINT" or "INTEGER" => "BIGINT", "BIGINT" => "DECIMAL(38, 0)", _ => null };

    private static bool IsQuantile(JsonElement e) =>
        Str(e, "type") == "BOUND_AGGREGATE" && Str(e, "name") is "median" or "quantile_cont" or "quantile_disc" && e.TryGetProperty("function_data", out var fd) && fd.ValueKind == JsonValueKind.Object && fd.TryGetProperty("quantiles", out _);

    /// <summary>
    /// The quantile of the rows of a group from their ranks (<paramref name="rankAt"/>, <paramref name="countAt"/> are the columns the ranking added): continuous is the interpolation DuckDB does, in double arithmetic,
    /// at position (n - 1) * q of the sorted non-NULL values; discrete is the value at position ceil(n * q). A group with no value gives NULL.
    /// </summary>
    private string Quantile(JsonElement e, IReadOnlyList<string> outs, int rankAt, int countAt)
    {
        var name = Str(e, "name")!;
        if (Str(e, "aggregate_type") == "DISTINCT" || e.TryGetProperty("filter", out _)) throw new LoweringException($"{name} with DISTINCT or FILTER");
        var data = e.GetProperty("function_data");
        var fractions = data.GetProperty("quantiles").EnumerateArray().ToList();
        if (fractions.Count != 1) throw new LoweringException($"{name} with a list of fractions (the result is a list)");
        if (data.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.True) throw new LoweringException($"{name} over a descending order");
        var q = FractionText(fractions[0]);
        var x = Expr(Arr(e, "children").Single(), outs);
        var rank = outs[rankAt];
        var n = $"CAST({outs[countAt]} AS DOUBLE)";
        rules.Add("quantile-ranked");
        if (name == "quantile_disc")
        {
            var position = $"CASE WHEN ceil({n} * {q}) < 1 THEN 1 ELSE CAST(ceil({n} * {q}) AS BIGINT) END";
            return $"max(CASE WHEN {rank} = {position} THEN {x} END)";
        }
        var returns = e.GetProperty("return_type");
        var kind = returns.GetProperty("id").GetString();
        if (kind is not ("DOUBLE" or "DECIMAL")) throw new LoweringException($"{name} over {kind} values");
        // DuckDB interpolates a DECIMAL on its stored whole numbers (the value times 10^scale) in double arithmetic and cuts the result to a whole number (1228.40888 is 1228.4088, not 1228.4089);
        // the same is done here, so the values agree to the last digit
        var scale = kind == "DECIMAL" && returns.TryGetProperty("type_info", out var info) && info.TryGetProperty("scale", out var s) ? s.GetInt32() : 0;
        var ten = $"1{new string('0', scale)}";                                  // 10^scale
        var power = scale == 0 ? "" : $" * {ten}";
        string Value() => kind == "DECIMAL" ? $"CAST(CAST({x}{power} AS BIGINT) AS DOUBLE)" : $"CAST({x} AS DOUBLE)";
        var at = $"(({n} - 1) * {q})";
        var lower = $"max(CASE WHEN {rank} = CAST(floor{at} AS BIGINT) + 1 THEN {Value()} END)";
        var upper = $"max(CASE WHEN {rank} = CAST(ceil{at} AS BIGINT) + 1 THEN {Value()} END)";
        var groupSize = $"CAST(max({outs[countAt]}) AS DOUBLE)";                 // outside an aggregate the group's row count has to be an aggregate itself
        var fraction = $"(({groupSize} - 1) * {q})";
        var weight = $"({fraction} - floor{fraction})";
        var interpolated = $"({lower} * (1.0 - {weight}) + {upper} * {weight})";      // DuckDB's own form: lo * (1 - d) + hi * d, which is not lo + (hi - lo) * d in the last bit
        if (kind == "DOUBLE") return interpolated;
        rules.Add("double-to-decimal");
        rules.Add("round-double");                                                // the explicit DOUBLE cast marks the round for the target step, as in the cast rule
        return $"CAST(round(CAST(CAST(trunc({interpolated}) AS DOUBLE) / {ten}.0 AS DOUBLE), {scale}) AS {TypeName(returns)})";
    }

    /// <summary>The fraction of a quantile as the plan holds it (a decimal constant stored as a whole number and a scale), written as a decimal literal.</summary>
    private static string FractionText(JsonElement constant)
    {
        var scale = constant.GetProperty("type").GetProperty("type_info").GetProperty("scale").GetInt32();
        var value = constant.GetProperty("value").GetInt64();
        var text = Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        return scale == 0 ? text : $"{text[..^scale]}.{text[^scale..]}";
    }

    private string Aggregate(JsonElement e, IReadOnlyList<string> outs)
    {
        var name = Str(e, "name")!;
        var children = Arr(e, "children").ToList();
        var a = children.Select(c => Expr(c, outs)).ToList();
        // RULE avg-double: the result type is DOUBLE; the engines' own AVG over integers or decimals is not
        if (On("avg-double") && name == "avg" && TypeId(e) == "DOUBLE" && children.Count > 0 && TypeId(children[0]) != "DOUBLE")
        {
            a[0] = $"CAST({a[0]} AS DOUBLE)";
            rules.Add("avg-double");
        }
        // RULE sum-widen: DuckDB sums integers into a HUGEINT; SQL Server's SUM of an INT is an INT and raises an overflow error past 2^31 (a BIGINT's past 2^63). Widen the argument.
        if (On("sum-widen") && name == "sum" && children.Count == 1 && SumWidening(TypeId(children[0])) is { } widened)
        {
            a[0] = $"CAST({a[0]} AS {widened})";
            rules.Add("sum-widen");
        }
        var shown = AggregateNames.GetValueOrDefault(name, name);
        // what the plan keeps outside the arguments: `string_agg`'s separator and any `ORDER BY` inside an aggregate. Anything else (the fraction of a quantile, the ordering of `first`) is not read here,
        // and a query that silently lost it would be a different query.
        var data = e.TryGetProperty("function_data", out var fd) && fd.ValueKind == JsonValueKind.Object && fd.EnumerateObject().Any() ? fd : (JsonElement?)null;
        var orders = e.TryGetProperty("order_bys", out var ob) && ob.TryGetProperty("orders", out var ord) && ord.GetArrayLength() > 0 ? ord.EnumerateArray().ToList() : null;
        string? suffix = null;
        if (name == "string_agg")
        {
            if (data is { } d && d.EnumerateObject().Any(x => x.Name != "separator")) throw new LoweringException("string_agg with options the lowerer does not read");
            var separator = data is { } sd && sd.TryGetProperty("separator", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString()! : ",";
            a.Add("'" + separator.Replace("'", "''") + "'");
            if (orders != null) suffix = " ORDER BY " + string.Join(", ", orders.Select(o => OrderItem(o, outs)));
        }
        else if (data != null || orders != null)
            throw new LoweringException($"the aggregate {name} with {(orders != null ? "an ORDER BY inside it" : "arguments the plan keeps outside its parameters (for example the fraction of a quantile)")}");
        var body = name == "count_star" ? "*" : (Str(e, "aggregate_type") == "DISTINCT" ? "DISTINCT " : "") + string.Join(", ", a) + suffix;
        var s = $"{shown}({body})";
        return e.TryGetProperty("filter", out var f) ? $"{s} FILTER (WHERE {Expr(f, outs)})" : s;
    }

    private static readonly Dictionary<string, string> FrameBounds = new()
    {
        ["UNBOUNDED_PRECEDING"] = "UNBOUNDED PRECEDING", ["CURRENT_ROW_ROWS"] = "CURRENT ROW", ["CURRENT_ROW_RANGE"] = "CURRENT ROW", ["UNBOUNDED_FOLLOWING"] = "UNBOUNDED FOLLOWING",
    };

    private string Window(JsonElement e, IReadOnlyList<string> outs)
    {
        var type = e.GetProperty("type").GetString()!;
        var name = type switch
        {
            "WINDOW_ROW_NUMBER" => "row_number", "WINDOW_RANK" => "rank", "WINDOW_RANK_DENSE" => "dense_rank", "WINDOW_LEAD" => "lead", "WINDOW_LAG" => "lag",
            "WINDOW_FIRST_VALUE" => "first_value", "WINDOW_NTH_VALUE" => "nth_value", "WINDOW_LAST_VALUE" => "last_value", "WINDOW_NTILE" => "ntile", "WINDOW_PERCENT_RANK" => "percent_rank", "WINDOW_CUME_DIST" => "cume_dist",
            _ => Str(e, "name"),
        } ?? throw new LoweringException($"the window function {type}");
        var windowChildren = Arr(e, "children").ToList();
        var args = windowChildren.Select(c => Expr(c, outs)).ToList();
        // count(*) OVER (...) is a `count` with no argument in the plan: written as it was, `count() OVER ()` reached both engines as a call they do not have
        if (type == "WINDOW_AGGREGATE" && (name == "count_star" || (name == "count" && windowChildren.Count == 0))) { name = "count"; args = ["*"]; }
        else if (type == "WINDOW_AGGREGATE" && AggregateNames.TryGetValue(name, out var windowName)) name = windowName;
        if (On("sum-widen") && type == "WINDOW_AGGREGATE" && name == "sum" && windowChildren.Count == 1 && SumWidening(TypeId(windowChildren[0])) is { } widened)
        {
            args[0] = $"CAST({args[0]} AS {widened})";
            rules.Add("sum-widen");
        }
        // RULE avg-double also applies to avg(x) OVER (...)
        if (On("avg-double") && type == "WINDOW_AGGREGATE" && name == "avg" && TypeId(e) == "DOUBLE" && windowChildren.Count > 0 && TypeId(windowChildren[0]) != "DOUBLE")
        {
            args[0] = $"CAST({args[0]} AS DOUBLE)";
            rules.Add("avg-double");
        }
        if (type is "WINDOW_LEAD" or "WINDOW_LAG" && (e.TryGetProperty("offset_expr", out _) || e.TryGetProperty("default_expr", out _)))
        {
            args.Add(e.TryGetProperty("offset_expr", out var off) ? Expr(off, outs) : "1");
            if (e.TryGetProperty("default_expr", out var def)) args.Add(Expr(def, outs));
        }
        var parts = new List<string>();
        var partitions = Arr(e, "partitions").ToList();
        if (partitions.Count > 0) parts.Add("PARTITION BY " + string.Join(", ", partitions.Select(p => Trim(p, Expr(p, outs)))));
        var orders = Arr(e, "orders").ToList();
        if (orders.Count > 0) parts.Add("ORDER BY " + string.Join(", ", orders.Select(o => OrderItem(o, outs))));
        var start = Str(e, "start");
        var end = Str(e, "end");
        // only RANGE ... CURRENT ROW is the default frame; ROWS ... CURRENT ROW is not (they differ when rows tie), so it is printed
        var isDefault = start is null or "UNBOUNDED_PRECEDING" && end is null or "CURRENT_ROW_RANGE" && !e.TryGetProperty("start_expr", out _) && !e.TryGetProperty("end_expr", out _);
        if (start != null && end != null && !isDefault)
        {
            string Bound(string kind, string field)
            {
                if (FrameBounds.TryGetValue(kind, out var fixedText)) return fixedText;
                if (kind.StartsWith("EXPR_PRECEDING", StringComparison.Ordinal)) return $"{Expr(e.GetProperty(field), outs)} PRECEDING";
                if (kind.StartsWith("EXPR_FOLLOWING", StringComparison.Ordinal)) return $"{Expr(e.GetProperty(field), outs)} FOLLOWING";
                throw new LoweringException($"the window frame bound {kind}");
            }
            var unit = start.EndsWith("ROWS", StringComparison.Ordinal) || end.EndsWith("ROWS", StringComparison.Ordinal) ? "ROWS" : "RANGE";
            parts.Add($"{unit} BETWEEN {Bound(start, "start_expr")} AND {Bound(end, "end_expr")}");
        }
        return $"{name}({string.Join(", ", args)}) OVER ({string.Join(" ", parts)})";
    }

    /// <summary>
    /// Null ordering is always written: DuckDB puts NULLs last, SQL Server first for ascending and PostgreSQL first for descending, so leaving it implicit would
    /// let the engines disagree about row order. A plain ascending key prints as `x NULLS LAST`.
    /// </summary>
    private string OrderItem(JsonElement o, IReadOnlyList<string> outs)
    {
        var desc = o.GetProperty("type").GetString() == "DESCENDING" ? " DESC" : "";
        var nulls = (Str(o, "null_order") ?? "NULLS_LAST").Replace('_', ' ');
        return $"{Expr(o.GetProperty("expression"), outs)}{desc} {nulls}";
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    // relations

    /// <param name="Outer">The column holds a value of the enclosing query (a correlated reference) or a pass-through of one.</param>
    /// <param name="Const">The value does not depend on any column (a literal, `current_date`, a macro's argument chosen by a constant condition): grouping by it groups nothing, and T-SQL refuses to.</param>
    private sealed record Item(string Sql, string? Alias, string? Type, bool Outer = false, bool Const = false);

    private sealed class Rel
    {
        public List<Item> Sel { get; set; } = [];
        public string Frm { get; set; } = "";
        public List<string> Sources { get; set; } = [];
        public List<string> Where { get; } = [];
        public List<string> Group { get; set; } = [];
        public List<string> Having { get; } = [];
        public List<string> Order { get; set; } = [];
        public long? Limit { get; set; }
        public long? Offset { get; set; }
        public bool Distinct { get; set; }
        public bool HasWindow { get; set; }
        public bool HasAgg { get; set; }
        public bool Plain { get; set; }
        public bool IsDelim { get; set; }
        /// <summary>An aggregate block whose every output is NULL when its input has no rows (a max, a sum, an expression of those; not a count): a join to it that finds no match and the aggregate over nothing agree.</summary>
        public bool EmptyIsNull { get; set; }
        /// <summary>A correlated `LIMIT k` that DuckDB wrote as `row_number() OVER (PARTITION BY correlated values ORDER BY ...) <= k`: the select item and the ordering to restore.</summary>
        public (int Index, List<string> Orders)? LimitWindow { get; set; }
        public bool SuppressAliases { get; set; }
        public string? SetOp { get; set; }
        public bool SetOpDistinct { get; set; }
        public IReadOnlyList<string>? SetOpNames { get; set; }

        public IReadOnlyList<string> Outs() => Sel.Select(s => s.Sql).ToList();
        public bool Mergeable() => Limit == null && Offset == null && !Distinct && SetOp == null;

        // a marker is `\0source\u0001column\0`, qualified only in a block with several sources; `\u0002...\u0002` is a reference to the enclosing query and is always qualified
        private bool qualifyAll;

        public string Render(string text)
        {
            var multi = Sources.Count > 1 || qualifyAll;
            return Regex.Replace(text, "([\0\u0002])([^\u0001\0\u0002]*)\u0001([^\0\u0002]*)[\0\u0002]", m => (multi || m.Groups[1].Value == "\u0002" ? Ident(m.Groups[2].Value) + "." : "") + Ident(m.Groups[3].Value));
        }

        private bool HasOuterReference() =>
            Sel.Any(i => i.Sql.Contains('\u0002')) || Frm.Contains('\u0002') || Where.Any(x => x.Contains('\u0002')) || Group.Any(x => x.Contains('\u0002')) ||
            Having.Any(x => x.Contains('\u0002')) || Order.Any(x => x.Contains('\u0002'));

        public Rel Clone() => new Rel()
        {
            Sel = [.. Sel], Frm = Frm, Sources = [.. Sources], Group = [.. Group], Order = [.. Order], Limit = Limit, Offset = Offset, Distinct = Distinct, HasWindow = HasWindow,
            HasAgg = HasAgg, Plain = Plain, IsDelim = IsDelim, EmptyIsNull = EmptyIsNull, LimitWindow = LimitWindow, SuppressAliases = SuppressAliases, SetOp = SetOp, SetOpDistinct = SetOpDistinct, SetOpNames = SetOpNames,
        }.CopyPredicates(this);

        private Rel CopyPredicates(Rel from) { Where.AddRange(from.Where); Having.AddRange(from.Having); return this; }

        private static readonly Regex Plainref = new("^[\0\u0002][^\u0001\0\u0002]*\u0001([^\0\u0002]*)[\0\u0002]$");

        public IReadOnlyList<string> Aliases()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            for (var i = 0; i < Sel.Count; i++)
            {
                var m = Plainref.Match(Sel[i].Sql);
                var n = Sel[i].Alias ?? (m.Success ? m.Groups[1].Value : $"col{i + 1}");
                var baseName = n; var k = 1;
                while (!used.Add(n)) n = $"{baseName}_{++k}";
                result.Add(n);
            }
            return result;
        }

        public string Sql()
        {
            if (SetOp != null) return SetOp;
            qualifyAll = HasOuterReference();
            var names = Aliases();
            var items = new List<string>();
            for (var i = 0; i < Sel.Count; i++)
            {
                var m = Plainref.Match(Sel[i].Sql);
                var needsAs = (!m.Success && !SuppressAliases) || (m.Success && m.Groups[1].Value != names[i]);
                items.Add(Render(Sel[i].Sql) + (needsAs ? $" AS {Ident(names[i])}" : ""));
            }
            var sb = new StringBuilder($"SELECT {(Distinct ? "DISTINCT " : "")}{string.Join(", ", items)}");
            if (Frm.Length > 0) sb.Append("\nFROM ").Append(Render(Frm));
            if (Where.Count > 0) sb.Append("\nWHERE ").Append(Render(string.Join(" AND ", Where)));
            if (Group.Count > 0) sb.Append("\nGROUP BY ").Append(Render(string.Join(", ", Group)));
            if (Having.Count > 0) sb.Append("\nHAVING ").Append(Render(string.Join(" AND ", Having)));
            if (Order.Count > 0) sb.Append("\nORDER BY ").Append(Render(string.Join(", ", Order)));
            if (Limit != null) sb.Append("\nLIMIT ").Append(Limit);
            if (Offset is > 0) sb.Append(" OFFSET ").Append(Offset);
            return sb.ToString();
        }

        public string Cte(List<(string Name, string Sql)> ctes, bool recursive = false) =>
            ctes.Count == 0 ? "" : (recursive ? "WITH RECURSIVE " : "WITH ") + string.Join(",\n", ctes.Select(c => $"{c.Name} AS (\n{Indent(c.Sql)}\n)")) + "\n";

        private static string Indent(string s) => string.Join('\n', s.Split('\n').Select(l => "  " + l));
    }

    private static string IndentText(string s) => string.Join('\n', s.Split('\n').Select(l => "  " + l));

    private string Fresh()
    {
        string alias;
        do alias = $"s{++aliasCounter}"; while (reserved.Contains(alias));
        return alias;
    }

    private Rel Wrap(Rel rel)
    {
        var a = Fresh();
        var names = rel.SetOp != null ? rel.SetOpNames! : rel.Aliases();
        var types = rel.Sel.Select(s => s.Type).ToList();
        return new Rel
        {
            Frm = $"(\n{IndentText(rel.Sql())}\n) AS {a}",
            Sources = [a],
            Sel = names.Select((n, i) => new Item(Token(a, n), n, i < types.Count ? types[i] : null, rel.Sel.Count > i && rel.Sel[i].Outer)).ToList(),
        };
    }

    /// <summary>
    /// UNPIVOT reaches the plan as an UNNEST of two lists built per row: the constant list of the column names and `unpivot_list(col1, col2, ...)`. The same rows are a lateral VALUES list with a row per
    /// column (the name as a literal and the column as the value) joined to one scan of the source, which SQL Server writes as CROSS APPLY and PostgreSQL as LATERAL; the NULL values DuckDB drops come out of the filter that follows the UNNEST.
    /// Any other UNNEST is not lowered: the engines have no equal for a list column.
    /// </summary>
    private Rel? TryUnpivot(JsonElement p, List<JsonElement> kids)
    {
        if (kids.Count != 1 || kids[0].GetProperty("type").GetString() != "LOGICAL_PROJECTION") return null;
        var projection = kids[0];
        var exprs = Arr(projection, "expressions").ToList();
        var namesAt = exprs.FindIndex(e => Str(e, "alias") == "unpivot_names" && Str(e, "type") == "VALUE_CONSTANT");
        var listAt = exprs.FindIndex(e => Str(e, "alias") == "unpivot_list" && Str(e, "name") == "unpivot_list");
        var unnests = Arr(p, "expressions").ToList();
        if (namesAt < 0 || listAt < 0 || unnests.Count != 2) return null;
        var namesValue = exprs[namesAt].GetProperty("value");
        if (!namesValue.TryGetProperty("value", out var listValue) || !listValue.TryGetProperty("children", out var nameItems)) return null;
        var names = nameItems.EnumerateArray().Select(n => n.TryGetProperty("value", out var v) ? v.GetString() : null).ToList();
        var values = Arr(exprs[listAt], "children").ToList();
        if (names.Count == 0 || names.Count != values.Count || names.Any(n => n == null)) return null;

        var source = Node(Arr(projection, "children").Single());
        var outs = source.Outs();
        var passthrough = exprs.Select((e, i) => (e, i)).Where(x => x.i != namesAt && x.i != listAt).ToList();
        var aliases = passthrough.Select((x, j) => Str(x.e, "alias") ?? $"c{j}").ToList();
        var nameColumn = UniqueName(aliases, "unpivot_name");
        var valueColumn = UniqueName(aliases, "unpivot_value");
        // one scan of the source: its columns and the unpivoted values as columns, then a lateral VALUES list with a row per unpivoted column (CROSS APPLY on SQL Server)
        var inner = source.Clone();
        var type = TypeNameOf(unnests[1]);
        var valueAliases = values.Select((_, k) => UniqueName(aliases, $"unpivot_v{k}")).ToList();
        inner.Sel = passthrough.Select((x, j) => new Item(Expr(x.e, outs), aliases[j], TypeNameOf(x.e)))
            .Concat(values.Select((v, k) => new Item(Expr(v, outs), valueAliases[k], type))).ToList();
        var a = Fresh();
        var b = Fresh();
        var rows = string.Join(", ", names.Select((n, k) => $"('{n!.Replace("'", "''")}', CAST({Token(a, valueAliases[k])} AS {type ?? "VARCHAR"}))"));
        var from = $"(\n{IndentText(inner.Sql())}\n) AS {a}\nCROSS JOIN LATERAL (VALUES {rows}) AS {b}({nameColumn}, {valueColumn})";
        var realNames = aliases.Append(nameColumn).Append(valueColumn).ToList();
        var sel = new List<Item>();
        var next = 0;
        for (var i = 0; i < exprs.Count; i++)
        {
            if (i == namesAt || i == listAt) { sel.Add(new Item("NULL", Str(exprs[i], "alias"), null)); continue; }       // the two lists are not columns of the result
            sel.Add(new Item(Token(a, realNames[next]), realNames[next], TypeNameOf(exprs[i])));
            next++;
        }
        sel.Add(new Item(Token(b, nameColumn), nameColumn, "VARCHAR"));
        sel.Add(new Item(Token(b, valueColumn), valueColumn, type));
        return new Rel { Frm = from, Sources = [a, b], Plain = true, Sel = sel };
    }

    /// <summary>
    /// While a cell of a VALUES list is lowered, a cast of a constant keeps its cast unless the constant already has the type: the list gets its column types from the cells, so a literal
    /// the binder widened to the column type (2 in a DECIMAL(11, 1) column) must not lose the widening that gives the column DuckDB's type on the engine.
    /// </summary>
    private bool exactCasts;

    /// <summary>The types a VALUES column can have and an engine give back exactly: the others have no equal (TINYINT is unsigned on SQL Server, an unsigned or 128-bit integer, an interval, a zone, a list or a struct have none).</summary>
    private static readonly HashSet<string> ValuesColumnTypes = ["BOOLEAN", "SMALLINT", "INTEGER", "BIGINT", "DECIMAL", "DOUBLE", "FLOAT", "VARCHAR", "DATE", "TIMESTAMP"];

    /// <summary>
    /// DuckDB's constant table (EXPRESSION_GET): `VALUES (...), (...)` inline or as `(VALUES ...) AS t(a, b)`. Every cell is already a cast to the column's type, so each cell is lowered like any expression and
    /// the list is written as the engines' table value constructor (SQL Server takes 1000 rows in one, which a target rule splits). The columns are named col0, col1, ... as DuckDB names them.
    /// </summary>
    private Rel ValuesList(JsonElement p)
    {
        // a lateral list: the child is DuckDB's DELIM_GET, the outer values the cells read (entry 180). Its columns come first in the cells' references; the list's last columns carry them back for the join.
        var children = Arr(p, "children").Where(k => Str(k, "type") != "LOGICAL_DUMMY_SCAN").ToList();     // (a list of constants has DuckDB's dummy scan under it)
        if (children.Any(k => Str(k, "type") != "LOGICAL_DELIM_GET") || children.Count > 1) throw new LoweringException("a VALUES list with a subquery in a cell, or a lateral list with another operator under it (only a list that reads the outer query's columns is lowered)");
        var delim = children.Count == 1 ? Node(children[0]) : null;
        var carried = delim?.Sel.Count ?? 0;
        var types = Arr(p, "expr_types").ToList();
        var authored = types.Count - carried;
        if (authored < 1) throw new LoweringException("a VALUES list without columns of its own");
        for (var j = 0; j < authored; j++)
        {
            var id = types[j].GetProperty("id").GetString()!;
            if (!ValuesColumnTypes.Contains(id)) throw new LoweringException($"a VALUES column of type {id} (the engines have no type that holds the same values; cast the cells to a type they have)");
        }
        var rows = p.GetProperty("expressions").EnumerateArray().Select(r => r.EnumerateArray().ToList()).ToList();
        if (rows.Count == 0 || types.Count == 0 || rows.Any(r => r.Count != types.Count)) throw new LoweringException("a VALUES list without rows or with rows of different lengths");
        var outs = delim?.Outs() ?? [];
        var lines = new List<string>();
        var saved = exactCasts;
        exactCasts = true;
        try
        {
            foreach (var row in rows)
            {
                if (row.Any(c => ContainsSubquery(c))) throw new LoweringException("a VALUES list with a subquery in a cell");
                if (delim == null && row.Any(c => ContainsReference(c))) throw new LoweringException("a VALUES list that reads a column of another table in a plan shape this does not read");
                // the last columns hand each outer value back (`a` as a column of the list) so that DuckDB can join the list to the outer rows; they are the outer values themselves, and are not written
                for (var j = 0; j < carried; j++)
                    if (Str(row[authored + j], "type") != "BOUND_REF" || row[authored + j].GetProperty("index").GetInt32() != j) throw new LoweringException("a lateral VALUES list in a plan shape this does not read (the outer values are not carried as the last columns)");
                lines.Add("(" + string.Join(", ", row.Take(authored).Select((c, j) => ValuesCell(c, TypeName(types[j]), outs))) + ")");
            }
        }
        finally { exactCasts = saved; }
        var alias = UniqueAlias("vals", "vals");
        var names = Enumerable.Range(0, authored).Select(j => $"col{j}").ToList();
        return new Rel
        {
            Frm = $"(VALUES\n{string.Join(",\n", lines.Select(l => "  " + l))}\n) AS {alias}({string.Join(", ", names)})",
            Sources = [alias],
            Sel = names.Select((n, j) => new Item(Token(alias, n), n, TypeName(types[j]))).Concat(delim?.Sel ?? []).ToList(),
        };
    }

    /// <summary>
    /// One cell, lowered like any expression and given the column's type: a constant that is not already a cast (and not one of the two whose bare literal has the type on every engine) is cast to it, and a boolean
    /// expression is written as a CASE with a cast to BOOLEAN, because SQL Server has no boolean values and its transpile only rewrites a condition that stands in a select list, not in a VALUES row.
    /// </summary>
    private string ValuesCell(JsonElement cell, string type, IReadOnlyList<string> outs)
    {
        var text = Expr(cell, outs);
        if (IsConstant(cell)) return Str(cell, "type") == "VALUE_CONSTANT" && (IsNullConstant(cell) || type is not ("VARCHAR" or "INTEGER" or "DECIMAL" or "DATE" or "TIMESTAMP")) ? $"CAST({text} AS {(type == "FLOAT" ? "REAL" : type)})" : text;
        if (type == "BOOLEAN") return $"CAST(CASE WHEN {text} THEN TRUE WHEN NOT {text} THEN FALSE END AS BOOLEAN)";
        // a BIGINT cell that is a calculation (length, year, a number of days, a count) is an INT on SQL Server and a NUMERIC on PostgreSQL for several of these functions; the column is BIGINT in DuckDB
        return type == "BIGINT" && Str(cell, "type") != "OPERATOR_CAST" ? $"CAST({text} AS BIGINT)" : text;
    }

    private static bool IsNullConstant(JsonElement e) => e.GetProperty("value").TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True;

    /// <summary>A constant or a cast of one.</summary>
    private static bool IsConstant(JsonElement e) => Str(e, "type") switch { "VALUE_CONSTANT" => true, "OPERATOR_CAST" => IsConstant(e.GetProperty("child")), _ => false };

    private static bool ContainsReference(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.Object => (Str(e, "type") is "BOUND_REF" or "BOUND_COLUMN_REF" or "BOUND_SUBQUERY") || e.EnumerateObject().Any(x => ContainsReference(x.Value)),
            JsonValueKind.Array => e.EnumerateArray().Any(ContainsReference),
            _ => false,
        };

    private static bool ContainsSubquery(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.Object => Str(e, "type") == "BOUND_SUBQUERY" || e.EnumerateObject().Any(x => ContainsSubquery(x.Value)),
            JsonValueKind.Array => e.EnumerateArray().Any(ContainsSubquery),
            _ => false,
        };

    private static string UniqueName(List<string> taken, string wanted)
    {
        var name = wanted;
        for (var i = 2; taken.Contains(name, StringComparer.OrdinalIgnoreCase); i++) name = wanted + i;
        return name;
    }

    /// <summary>
    /// `generate_series(a, b[, s])` and `range(...)` over integer constants become the engines' own GENERATE_SERIES (SQL Server 2022, PostgreSQL), with
    /// `range`'s exclusive end turned into an inclusive one. The column is cast to BIGINT because DuckDB's is, and SQL Server's would otherwise be INT.
    /// Date and timestamp series, UNNEST and any other table function are refused: the engines have no equal.
    /// </summary>
    private Rel Series(JsonElement p)
    {
        var name = Str(p, "name");
        if (name is not ("generate_series" or "range")) throw new LoweringException($"the table function {name ?? "(unnamed)"}");
        var args = new List<long>();
        foreach (var a in Arr(p, "parameters"))
        {
            var type = a.TryGetProperty("type", out var ty) && ty.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (type is null || !IntegerTypes.Contains(type) || type is "HUGEINT" or "UBIGINT" || (a.TryGetProperty("is_null", out var n) && n.ValueKind == JsonValueKind.True))
                throw new LoweringException($"{name} over {type ?? "a non-constant"} values (only integer series are lowered)");
            args.Add(a.GetProperty("value").GetInt64());
        }
        if (args.Count == 0) throw new LoweringException($"{name} with bounds that come from another table's columns (a series per row, which SQL Server writes as CROSS APPLY); use a series of constants and filter it");
        if (args.Count is > 3 || (name == "generate_series" && args.Count < 2)) throw new LoweringException($"{name} with {args.Count} argument(s)");
        var (start, stop, step) = args.Count switch { 1 => (0L, args[0], 1L), 2 => (args[0], args[1], 1L), _ => (args[0], args[1], args[2]) };
        if (step == 0) throw new LoweringException($"{name} with a step of zero");
        if (name == "range") stop += step > 0 ? -1 : 1;     // range excludes its end
        var column = p.GetProperty("names").EnumerateArray().First().GetString()!;
        var alias = UniqueAlias("series", "series");     // range is a keyword on SQL Server, so both functions share one alias
        var call = $"generate_series({start}, {stop}{(step == 1 ? "" : $", {step}")})";
        return new Rel
        {
            // the column is named `value`, which is what SQL Server calls it: it takes no column list after a function, so the renderer drops this one there
            Frm = $"{call} AS {alias}(value)",
            Sources = [alias],
            Sel = [new Item($"CAST({Token(alias, "value")} AS BIGINT)", column, "BIGINT")],
        };
    }

    /// <summary>The plan nodes being lowered, innermost last: a join asks its parent what happens to its rows (a filter that rejects NULL makes an inner join and a scalar subquery the same).</summary>
    private readonly Stack<JsonElement> nodeStack = new();

    private Rel Node(JsonElement p)
    {
        nodeStack.Push(p);
        try { return NodeCore(p); }
        finally { nodeStack.Pop(); }
    }

    private Rel NodeCore(JsonElement p)
    {
        var t = p.GetProperty("type").GetString()!;
        var kids = Arr(p, "children").ToList();
        switch (t)
        {
            case "LOGICAL_GET":
            {
                var fd = p.TryGetProperty("function_data", out var f) ? f : default;
                if (fd.ValueKind != JsonValueKind.Object || !fd.TryGetProperty("table", out var table)) return Series(p);
                var names = p.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToList();
                var types = p.GetProperty("returned_types").EnumerateArray().Select(TypeName).ToList();
                // an index past the columns is DuckDB's virtual row-id column, which a query that selects no column of the table (EXISTS (SELECT 1 FROM u)) still scans
                var idx = Arr(p, "column_indexes").Select(c => c.GetProperty("index").TryGetInt32(out var i) && i < names.Count ? i : -1).ToList();
                if (idx.Count == 0) idx = Enumerable.Range(0, names.Count).ToList();
                var baseName = table.GetString()!;
                var schema = Str(fd, "schema");
                var alias = AliasForTable(baseName, schema);
                aliasTables[alias] = schema is null or "main" ? baseName : $"{schema}.{baseName}";
                var rel = new Rel
                {
                    Frm = (schema is null or "main" ? baseName : $"{schema}.{baseName}") + (alias == baseName ? "" : $" AS {alias}"),
                    Sources = [alias],
                    Plain = true,
                    Sel = idx.Select(i => i < 0 ? new Item("NULL", null, "BIGINT") : new Item(Token(alias, names[i]), names[i], types[i])).ToList(),
                };
                return rel;
            }
            case "LOGICAL_DUMMY_SCAN": return new Rel();
            case "LOGICAL_EXPRESSION_GET": return ValuesList(p);
            case "LOGICAL_PROJECTION" when TryUncorrelatedScalar(p, kids) is { } constant: return constant;
            case "LOGICAL_PROJECTION" when TryCountPatch(p, kids) is { } counted: return counted;
            case "LOGICAL_PROJECTION":
            {
                var c = Node(kids[0]);
                if (!c.Mergeable()) c = Wrap(c);
                var outs = c.Outs();
                var childItems = c.Sel;
                c.Sel = Arr(p, "expressions").Select(e => new Item(Expr(e, outs), Str(e, "alias"), TypeNameOf(e), IsOuterRef(e, childItems), IsColumnFree(e, childItems))).ToList();
                c.Plain = false;
                c.EmptyIsNull = c.EmptyIsNull && Arr(p, "expressions").All(NullWhenInputIsNull);
                if (c.Sel.Any(s => s.Sql.Contains(" OVER (", StringComparison.Ordinal))) c.HasWindow = true;
                return c;
            }
            case "LOGICAL_FILTER":
            {
                var c = Node(kids[0]);
                if (c.LimitWindow is { } lw)
                {
                    var preds0 = Arr(p, "expressions").ToList();
                    var ok = preds0.Count == 1 && Str(preds0[0], "type") == "COMPARE_LESSTHANOREQUALTO" && preds0[0].GetProperty("left").GetProperty("type").GetString() == "BOUND_REF" &&
                             preds0[0].GetProperty("left").GetProperty("index").GetInt32() == lw.Index && preds0[0].GetProperty("right").GetProperty("type").GetString() == "VALUE_CONSTANT";
                    if (!ok) throw new LoweringException("a correlated LIMIT with an offset or another shape of row filter");
                    c.Order = lw.Orders;
                    c.Limit = preds0[0].GetProperty("right").GetProperty("value").GetProperty("value").GetInt64();
                    c.LimitWindow = null;
                    return c;
                }
                if (c.HasWindow || !c.Mergeable()) c = Wrap(c);
                var preds = Arr(p, "expressions").Select(e => Predicate(e, c.Outs())).ToList();
                if (c.HasAgg && c.Having.Count == 0 && !c.Plain) c.Having.AddRange(preds); else c.Where.AddRange(preds);
                return c;
            }
            case "LOGICAL_AGGREGATE_AND_GROUP_BY":
            {
                var c = Node(kids[0]);
                if (!c.Mergeable() || c.HasWindow || c.HasAgg || c.Group.Count > 0 || c.Having.Count > 0) c = Wrap(c);
                var aggregateExprs = Arr(p, "expressions").ToList();
                // median, quantile_cont and quantile_disc have no aggregate form on SQL Server (only an analytic PERCENTILE_CONT with an OVER clause): the rows are ranked inside each group first, and the
                // quantile is read off the ranks with conditional aggregates, which every engine has
                var quantileColumns = new Dictionary<int, (int Rank, int Count)>();
                if (aggregateExprs.Any(IsQuantile))
                {
                    var before = c.Outs();
                    var partition = Arr(p, "groups").Select(g => Expr(g, before)).ToList();
                    var by = partition.Count > 0 ? "PARTITION BY " + string.Join(", ", partition) : "";
                    var items = new List<Item>(c.Sel);
                    var ranked = new Dictionary<string, (int Rank, int Count)>(StringComparer.Ordinal);       // several quantiles of one column share one ranking
                    for (var i = 0; i < aggregateExprs.Count; i++)
                    {
                        if (!IsQuantile(aggregateExprs[i])) continue;
                        var x = Expr(Arr(aggregateExprs[i], "children").Single(), before);
                        if (ranked.TryGetValue(x, out var shared)) { quantileColumns[i] = shared; continue; }
                        quantileColumns[i] = ranked[x] = (items.Count, items.Count + 1);
                        items.Add(new Item($"row_number() OVER ({by} ORDER BY {x} NULLS LAST)", null, "BIGINT"));
                        items.Add(new Item($"count({x}) OVER ({by})", null, "BIGINT"));
                    }
                    c.Sel = items;
                    c.HasWindow = true;
                    c = Wrap(c);
                }
                // SQL Server refuses an aggregate (and a GROUP BY key) that contains a subquery: the subquery is computed in a derived table first and the aggregate reads its column
                if (aggregateExprs.Any(x => !IsQuantile(x) && HoldsSubquery(Expr(x, c.Outs()))) || Arr(p, "groups").Any(g => HoldsSubquery(Expr(g, c.Outs())))) c = Wrap(c);
                var outs = c.Outs();
                var childItems = c.Sel;
                var groups = Arr(p, "groups").Select(g => (Sql: Trim(g, Expr(g, outs)), Type: TypeNameOf(g), Outer: IsOuterRef(g, childItems), Const: IsColumnFree(g, childItems))).ToList();
                var aggs = aggregateExprs.Select((a, i) => (Sql: quantileColumns.TryGetValue(i, out var cols) ? Quantile(a, outs, cols.Rank, cols.Count) : Expr(a, outs), Type: TypeNameOf(a), Outer: false, Const: false)).ToList();
                if (groups.Count == 0 && aggs.Count == 0) throw new LoweringException("an empty aggregate");
                // a group on a value of the enclosing query is constant for each outer row (DuckDB added it when it decorrelated the subquery), so it is not a GROUP BY any more
                if (groups.Count > 0 && groups.All(g => g.Outer) && aggs.Count == 0) throw new LoweringException("an aggregate that only groups by correlated values");
                // a key that no column determines (the date a macro gave a live table) groups nothing and is not a GROUP BY: T-SQL refuses it. When it was the only key, the aggregate without GROUP BY
                // always returns a row, so `HAVING count(*) > 0` keeps what GROUP BY did: no row for no input
                var droppedConstant = groups.Any(g => !g.Outer && g.Const);
                c.Group = groups.Where(g => !g.Outer && !g.Const).Select(g => g.Sql).ToList();
                if (droppedConstant && c.Group.Count == 0 && groups.All(g => g.Outer || g.Const) && aggs.Count > 0 && !c.Having.Any(h => h == "count(*) > 0")) c.Having.Add("count(*) > 0");
                c.Sel = groups.Concat(aggs).Select(x => new Item(x.Sql, null, x.Type, x.Outer, x.Const)).ToList();
                c.HasAgg = true;
                c.EmptyIsNull = aggregateExprs.All(a => IsQuantile(a) || NullOnEmpty.Contains(Str(a, "name") ?? ""));
                c.Plain = false;
                return c;
            }
            case "LOGICAL_ORDER_BY":
            {
                var c = Node(kids[0]);
                if (c.Limit != null) c = Wrap(c);
                c.Order = Arr(p, "orders").Select(o => OrderItem(o, c.Outs())).ToList();
                return c;
            }
            case "LOGICAL_LIMIT":
            {
                var c = Node(kids[0]);
                if (c.Limit != null || c.Offset != null) c = Wrap(c);
                long? Val(string k)
                {
                    if (!p.TryGetProperty(k, out var v) || Str(v, "type") == "UNSET") return null;
                    if (Str(v, "type") == "CONSTANT_VALUE" && v.TryGetProperty("constant_percentage", out var pc) && pc.GetDouble() == -1) return v.GetProperty("constant_integer").GetInt64();
                    throw new LoweringException("a limit that is not a plain constant (a percentage or an expression)");
                }
                c.Limit = Val("limit_val");
                c.Offset = Val("offset_val");
                return c;
            }
            case "LOGICAL_DISTINCT":
            {
                var c = Node(kids[0]);
                if (Str(p, "distinct_type") == "DISTINCT_ON") return DistinctOn(p, c);
                if (Str(p, "distinct_type") != "DISTINCT") throw new LoweringException($"the DISTINCT form {Str(p, "distinct_type")}");
                if (c.SetOp != null && c.SetOpDistinct) return c; // a set operation without ALL is already distinct
                if (!c.Mergeable() || c.HasWindow) c = Wrap(c);
                c.Distinct = true;
                // two values that differ only in trailing spaces are one row, and the row shows the trimmed one (SQL Server shows either; a comparison of results trims both)
                if (trimTrailing) c.Sel = c.Sel.Select(i => i.Type == "VARCHAR" && (i.Alias ?? ColumnNameOf(i.Sql)) is { } name ? i with { Sql = $"rtrim({i.Sql})", Alias = name } : i).ToList();
                return c;
            }
            case "LOGICAL_DELIM_GET":
            {
                if (delimFrames.Count == 0) throw new LoweringException("a correlated reference outside a subquery");
                return new Rel { IsDelim = true, Sel = delimFrames.Peek().Select(i => i with { Outer = true }).ToList() };
            }
            case "LOGICAL_DELIM_JOIN": return DelimJoin(p, kids);
            case "LOGICAL_COMPARISON_JOIN" when Str(p, "join_type") == "MARK": return MarkJoin(p, kids);
            case "LOGICAL_COMPARISON_JOIN" or "LOGICAL_CROSS_PRODUCT":
            {
                var l = Node(kids[0]);
                if (t == "LOGICAL_COMPARISON_JOIN" && TryKeyedLimit(p, kids, l) is { } limited) return limited;
                var r = Node(kids[1]);
                if (l.IsDelim || r.IsDelim) return JoinWithDelim(p, t, l, r);
                if (t == "LOGICAL_COMPARISON_JOIN" && TryKeyedJoin(p, l, r) is { } keyed) return keyed;
                if (t == "LOGICAL_CROSS_PRODUCT" && (IsConstantRow(l) || IsConstantRow(r)))
                {
                    // a subquery that returns one value: it is an expression of the other side, not a table
                    var host = IsConstantRow(l) ? r : l;
                    host.Sel = l.Sel.Concat(r.Sel).ToList();
                    host.Plain = false;
                    return host;
                }
                // an aggregate without groups returns exactly one row: next to another relation it is a scalar subquery, and reads like the one the author wrote
                if (t == "LOGICAL_CROSS_PRODUCT" && (IsSingleRow(r) || IsSingleRow(l)) && !(IsSingleRow(r) && IsSingleRow(l)))
                {
                    var hostIsLeft = IsSingleRow(r);
                    var (host, single) = hostIsLeft ? (l, r) : (r, l);
                    if (!host.Mergeable() || host.HasWindow) host = Wrap(host);
                    var scalars = single.Sel.Select(i => new Item($"(\n{SubqueryWith(single, i.Sql)}\n)", null, i.Type)).ToList();
                    host.Sel = (hostIsLeft ? host.Sel.Concat(scalars) : scalars.Concat(host.Sel)).ToList();
                    host.Plain = false;
                    return host;
                }
                // The binder moves a one-sided predicate of an ON clause (`ON a.k = b.k AND b.f IS NOT NULL`) into a filter on that side's input, so an input can carry predicates of its own. They are not
                // dropped: an input that is more than a table with filters becomes a derived table, and the filters of a plain input are read as WHERE (an inner join, or the side a left or right join keeps)
                // or as part of ON (the side a left or right join fills in with NULLs, where a WHERE would turn the join into an inner one).
                var joinKind = Str(p, "join_type");
                static bool Flat(Rel x) => x.Plain && x.Mergeable() && !x.HasWindow && x.Having.Count == 0 && x.Group.Count == 0;
                var full = joinKind is "OUTER" or "FULL";
                if (!Flat(l) || (full && l.Where.Count > 0)) l = Wrap(l);
                if (!Flat(r) || (full && r.Where.Count > 0)) r = Wrap(r);
                var rel = new Rel { Sources = l.Sources.Concat(r.Sources).ToList(), Sel = LeftColumns(p, l.Sel).Concat(RightColumns(p, r.Sel)).ToList() };
                var filled = new List<string>();      // predicates of the side the join fills in with NULLs
                if (t == "LOGICAL_CROSS_PRODUCT" || joinKind == "INNER") { rel.Where.AddRange(l.Where); rel.Where.AddRange(r.Where); }
                else if (joinKind == "LEFT") { rel.Where.AddRange(l.Where); filled.AddRange(r.Where); }
                else if (joinKind == "RIGHT") { rel.Where.AddRange(r.Where); filled.AddRange(l.Where); }
                if (t == "LOGICAL_CROSS_PRODUCT") { rel.Frm = $"{l.Frm}\nCROSS JOIN {r.Frm}"; return rel; }
                var joinType = joinKind switch { "INNER" => "JOIN", "LEFT" => "LEFT JOIN", "RIGHT" => "RIGHT JOIN", "OUTER" or "FULL" => "FULL JOIN", var other => throw new LoweringException($"the join type {other}") };
                if (p.TryGetProperty("expression", out _)) throw new LoweringException("a join with an extra expression");
                var lo = l.Outs(); var ro = r.Outs();
                // 2.0 also lists, among the conditions, a predicate that is not a comparison of the two sides (the OR of `ON a.k = b.k AND (b.to IS NULL OR a.d <= b.to)`): it has only a `left`, over the columns of both sides
                var both = lo.Concat(ro).ToList();
                var conditions = Arr(p, "conditions").Where(c => c.TryGetProperty("right", out _)).Select(c => Compare(c.GetProperty("left"), Expr(c.GetProperty("left"), lo), Comparisons[Str(c, "comparison")!], c.GetProperty("right"), Expr(c.GetProperty("right"), ro))).ToList();
                var predicates = Arr(p, "conditions").Where(c => !c.TryGetProperty("right", out _)).Select(c => Expr(c.GetProperty("left"), both)).ToList();
                if (joinKind == "INNER") { rel.Where.AddRange(predicates); if (conditions.Count == 0 && filled.Count == 0) { rel.Frm = $"{l.Frm}\nCROSS JOIN {r.Frm}"; return rel; } }
                else conditions.AddRange(predicates);
                rel.Frm = $"{l.Frm}\n{joinType} {r.Frm}\n  ON {string.Join(" AND ", conditions.Concat(filled))}";
                return rel;
            }
            case "LOGICAL_WINDOW":
            {
                var c = Node(kids[0]);
                if (!c.Mergeable()) c = Wrap(c);
                var outs = c.Outs();
                var childItems = c.Sel;
                var first = c.Sel.Count;
                var items = new List<Item>();
                foreach (var e in Arr(p, "expressions"))
                {
                    var partitions = Arr(e, "partitions").ToList();
                    var correlated = partitions.Count(x => IsOuterRef(x, childItems));
                    if (correlated > 0)
                    {
                        // `LIMIT k` inside a correlated subquery: DuckDB numbers the rows per correlated value. Undo that when it is only that.
                        if (correlated != partitions.Count || e.GetProperty("type").GetString() != "WINDOW_ROW_NUMBER") throw new LoweringException("a window function partitioned by a correlated value");
                        c.LimitWindow = (first + items.Count, Arr(e, "orders").Select(o => OrderItem(o, outs)).ToList());
                        items.Add(new Item("NULL", null, TypeNameOf(e)));
                    }
                    else items.Add(new Item(Window(e, outs), null, TypeNameOf(e)));
                }
                c.Sel = c.Sel.Concat(items).ToList();
                if (items.Any(i => i.Sql != "NULL")) c.HasWindow = true;
                return c;
            }
            case "LOGICAL_MATERIALIZED_CTE":
            {
                var definition = Node(kids[0]);
                ctes[p.GetProperty("table_index").GetInt32()] = (Str(p, "ctename")!, definition);
                ctesInOrder.Add((Str(p, "ctename")!, definition.Sql()));   // before the main query, which may define more CTEs that read this one
                return Node(kids[1]);
            }
            case "LOGICAL_UNNEST" when TryUnpivot(p, kids) is { } unpivoted: return unpivoted;
            case "LOGICAL_RECURSIVE_CTE":
            {
                // WITH RECURSIVE: the anchor query, then the recursive query that reads the CTE through a CTE_REF to this operator's own table index. The engines want both parts to have the same column types
                // (SQL Server refuses a text column that is VARCHAR(81) in one part and VARCHAR(165) in the other), so a text or decimal column is cast to its type in both parts.
                var name = Str(p, "ctename")!;
                var anchor = Node(kids[0]);
                var names = anchor.SetOp != null ? anchor.SetOpNames! : anchor.Aliases();
                ctes[p.GetProperty("table_index").GetInt32()] = (name, anchor);
                var recursive = Node(kids[1]);
                if (anchor.SetOp != null || recursive.SetOp != null) throw new LoweringException("a recursive query whose parts are set operations");
                static Rel Typed(Rel r)
                {
                    r.Sel = r.Sel.Select(i => i.Type is { } t && (t == "VARCHAR" || t.StartsWith("DECIMAL", StringComparison.Ordinal)) ? i with { Sql = $"CAST({i.Sql} AS {t})" } : i).ToList();
                    return r;
                }
                var all = p.TryGetProperty("union_all", out var ua) && ua.ValueKind == JsonValueKind.True;
                hasRecursiveCte = true;
                return new Rel
                {
                    SetOp = $"{Typed(anchor).Sql()}\nUNION{(all ? " ALL" : "")}\n{Typed(recursive).Sql()}",
                    SetOpDistinct = !all,
                    SetOpNames = names,
                    Sel = names.Select((n, i) => new Item(Token("u", n), n, i < anchor.Sel.Count ? anchor.Sel[i].Type : null)).ToList(),
                };
            }
            case "LOGICAL_CTE_REF":
            {
                var (name, definition) = ctes[p.GetProperty("cte_index").GetInt32()];
                var key = "cte:" + name;
                var alias = UniqueAlias(name, key);
                var names = definition.SetOp != null ? definition.SetOpNames! : definition.Aliases();
                return new Rel
                {
                    Frm = name + (alias == name ? "" : $" AS {alias}"),
                    Sources = [alias],
                    Plain = true,
                    Sel = names.Select((n, i) => new Item(Token(alias, n), n, i < definition.Sel.Count ? definition.Sel[i].Type : null)).ToList(),
                };
            }
            case "LOGICAL_UNION" or "LOGICAL_INTERSECT" or "LOGICAL_EXCEPT":
            {
                var a = Node(kids[0]);
                var op = t switch { "LOGICAL_UNION" => "UNION", "LOGICAL_INTERSECT" => "INTERSECT", _ => "EXCEPT" };
                var all = p.TryGetProperty("setop_all", out var sa) && sa.ValueKind == JsonValueKind.True;
                var names = a.SetOp != null ? a.SetOpNames! : a.Aliases();
                // DuckDB flattens `A UNION B UNION C` into one operator with three children; every child after the first is joined in order
                var text = a.Sql();
                foreach (var kid in kids.Skip(1)) text = $"{text}\n{op}{(all ? " ALL" : "")}\n{Node(kid).Sql()}";
                return new Rel
                {
                    SetOp = text,
                    SetOpDistinct = !all,
                    SetOpNames = names,
                    Sel = names.Select((n, i) => new Item(Token("u", n), n, i < a.Sel.Count ? a.Sel[i].Type : null)).ToList(),
                };
            }
        }
        throw new LoweringException($"the plan operator {t[8..]}");
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------
    // subqueries
    //
    // DuckDB's binder has already decorrelated every subquery: an EXISTS is a DELIM_JOIN of type MARK, a scalar subquery a SINGLE join, a LATERAL an INNER or LEFT one, and the
    // right side reads the outer query's values through DELIM_GET. Here that is undone. Each DELIM_GET column is a reference to the outer expression that feeds it, the
    // joins back to the duplicate-eliminated values and the GROUP BYs on them are dropped (they are constant per outer row), and the right side is printed as a nested
    // query that mentions the outer columns, which is what the author wrote.

    private readonly Stack<List<Item>> delimFrames = new();

    private static bool IsOuterRef(JsonElement e, List<Item> childItems) =>
        e.GetProperty("type").GetString() == "BOUND_REF" && e.GetProperty("index").GetInt32() is var i && i < childItems.Count && childItems[i].Outer;

    /// <summary>A reference to a column of an enclosing block: printed qualified wherever it appears.</summary>
    private static string Requalify(string text) => text.Replace('\0', '\u0002');
    private static string Unqualify(string text) => text.Replace('\u0002', '\0');

    /// <summary>A block that is an aggregate with no grouping and nothing after it: it returns exactly one row.</summary>
    private static bool IsSingleRow(Rel r) => r.HasAgg && r.Group.Count == 0 && r.Having.Count == 0 && !r.HasWindow && r.SetOp == null && !r.Distinct && r.Limit == null && r.Offset == null && r.Sel.All(i => !i.Outer);

    private static string Nested(Rel r)
    {
        var sql = r.Sql();
        return string.Join('\n', sql.Split('\n').Select(l => "  " + l));
    }

    private static string SubqueryWith(Rel r, string selectSql)
    {
        var copy = r.Clone();
        copy.Sel = [new Item(selectSql, null, null)];
        copy.SuppressAliases = true;                      // a subquery's column has no name worth writing
        return Nested(copy);
    }

    /// <summary>
    /// Points the references of <paramref name="right"/> to the enclosing block's columns (written as the text of the column, marked as an outer reference) at the columns of that block's wrapped form:
    /// `from[i]` is what the left block's column i was, `to[i]` what it is after the block became a derived table. Found by a lateral join over a filtered derived table, whose lateral side printed `t.a`
    /// where the table in scope was `s1`.
    /// </summary>
    private static void RebaseOuterReferences(Rel right, IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < from.Count && i < to.Count; i++)
            if (from[i].Contains('\0') && Requalify(from[i]) is var key && key != Requalify(to[i])) map.TryAdd(key, Requalify(to[i]));
        if (map.Count == 0) return;
        var pattern = new Regex(string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)));
        string Rebase(string text) => text.Contains('\u0002') ? pattern.Replace(text, m => map[m.Value]) : text;
        right.Sel = right.Sel.Select(i => i with { Sql = Rebase(i.Sql) }).ToList();
        right.Frm = Rebase(right.Frm);
        for (var i = 0; i < right.Where.Count; i++) right.Where[i] = Rebase(right.Where[i]);
        right.Group = right.Group.Select(Rebase).ToList();
        for (var i = 0; i < right.Having.Count; i++) right.Having[i] = Rebase(right.Having[i]);
        right.Order = right.Order.Select(Rebase).ToList();
    }

    private Rel DelimJoin(JsonElement p, List<JsonElement> kids)
    {
        var left = Node(kids[0]);
        // the subquery becomes part of the select list or WHERE of the outer block, so that block must be able to hold it: not a DISTINCT, LIMIT, set operation or window
        if (!left.Mergeable() || left.HasWindow) left = Wrap(left);
        var lo = left.Outs();
        var duplicate = Arr(p, "duplicate_eliminated_columns").Select(e => new Item(Requalify(Expr(e, lo)), null, TypeNameOf(e), Outer: true)).ToList();
        if (left.HasAgg && duplicate.Any(d => !Regex.IsMatch(d.Sql, "^\u0002[^\u0001\0\u0002]*\u0001[^\0\u0002]*\u0002$")))
        {
            left = Wrap(left);         // correlated on an aggregate: it has to be a column of something
            lo = left.Outs();
            duplicate = Arr(p, "duplicate_eliminated_columns").Select(e => new Item(Requalify(Expr(e, lo)), null, TypeNameOf(e), Outer: true)).ToList();
        }
        delimFrames.Push(duplicate);
        Rel right;
        try { right = Node(kids[1]); }
        finally { delimFrames.Pop(); }
        return FinishDelimJoin(p, left, lo, right);
    }

    /// <summary>
    /// The join of the outer block with its subquery, once both are lowered and the correlation lives in the subquery as predicates on outer columns (items marked Outer carry the outer value): the subquery
    /// becomes an EXISTS, an IN, scalar subqueries in the select list, or a LATERAL join.
    /// </summary>
    private Rel FinishDelimJoin(JsonElement p, Rel left, IReadOnlyList<string> lo, Rel right, string? joinTypeOverride = null)
    {
        RequireComparisons(p);
        var joinType = joinTypeOverride ?? Str(p, "join_type")!;
        left = MapLeft(p, left);
        // DuckDB 2.0 hands a scalar subquery that is an ungrouped aggregate over as a LEFT join (1.x: SINGLE), with any expression over it above the join; an aggregate without groups returns one row, so
        // the two are the same. (A LEFT JOIN LATERAL written by the author over such an aggregate reads as scalar subqueries too: the same rows.)
        if (joinType == "LEFT" && right.HasAgg && right.Group.Count == 0 && right.Having.Count == 0 && !right.HasWindow && right.SetOp == null && !right.Distinct && right.Limit == null && right.Offset == null &&
            !Arr(p, "conditions").Any(c => Str(c, "comparison") != "COMPARE_NOT_DISTINCT_FROM"))
            joinType = "SINGLE";
        if (right.SetOp != null) throw new LoweringException("a correlated subquery over a set operation (UNION, INTERSECT, EXCEPT)");
        var ro = right.Outs();
        // conditions that tie a duplicate-eliminated column to its carried copy on the right are plumbing; any other condition is the comparison of an IN
        var real = new List<JsonElement>();
        foreach (var c in Arr(p, "conditions"))
        {
            var r = c.GetProperty("right");
            var carried = r.GetProperty("type").GetString() == "BOUND_REF" && right.Sel[r.GetProperty("index").GetInt32()].Outer;
            if (!carried) real.Add(c);
        }

        switch (joinType)
        {
            case "MARK":
            {
                Item mark;
                if (real.Count == 0) mark = new Item($"EXISTS (\n{SubqueryWith(right, "1")}\n)", null, "BOOLEAN");
                else if (real.Count == 1 && Str(real[0], "comparison") == "COMPARE_EQUAL")
                    mark = new Item($"({Expr(real[0].GetProperty("left"), lo)} IN (\n{SubqueryWith(right, Expr(real[0].GetProperty("right"), ro))}\n))", null, "BOOLEAN");
                else mark = ComparisonMark(real, lo, right);
                left.Sel = left.Sel.Append(mark).ToList();
                left.Plain = false;
                return left;
            }
            case "SINGLE":
            {
                var items = new List<Item>();
                for (var i = 0; i < right.Sel.Count; i++)
                {
                    if (!Projected(p, i)) continue;
                    var item = right.Sel[i];
                    if (item.Outer)
                    {
                        // the carried copy of an outer value is that outer value
                        var link = Arr(p, "conditions").FirstOrDefault(c => c.GetProperty("right").GetProperty("type").GetString() == "BOUND_REF" && c.GetProperty("right").GetProperty("index").GetInt32() == i);
                        items.Add(new Item(link.ValueKind == JsonValueKind.Object ? Expr(link.GetProperty("left"), lo) : Unqualify(item.Sql), null, item.Type, Outer: false));
                    }
                    else items.Add(new Item($"(\n{SubqueryWith(right, item.Sql)}\n)", null, item.Type));
                }
                left.Sel = left.Sel.Concat(items).ToList();
                left.Plain = false;
                return left;
            }
            case "INNER" or "LEFT":
            {
                if (real.Count > 0) throw new LoweringException("a lateral join with a comparison");
                if (!left.Plain && (joinType == "LEFT" || !left.Mergeable() || left.HasAgg || left.HasWindow || left.Where.Count > 0))
                {
                    // the right side was lowered against the left block's own columns; wrapping the left makes them columns of the new derived table
                    var before = left.Outs();
                    left = Wrap(left);
                    RebaseOuterReferences(right, before, left.Outs());
                }
                var alias = Fresh();
                var rightNames = right.Aliases();
                var lateral = Nested(right);
                var rel = new Rel
                {
                    Sources = left.Sources.Append(alias).ToList(),
                    Frm = $"{left.Frm}\n{(joinType == "LEFT" ? "LEFT JOIN" : "CROSS JOIN")} LATERAL (\n{lateral}\n) AS {alias}{(joinType == "LEFT" ? " ON TRUE" : "")}",
                    Sel = left.Sel.Concat(rightNames.Select((n, i) => (n, i)).Where(x => Projected(p, x.i)).Select(x => new Item(Token(alias, x.n), x.n, right.Sel[x.i].Type))).ToList(),
                };
                rel.Where.AddRange(left.Where);
                return rel;
            }
        }
        throw new LoweringException($"a subquery of join type {joinType}");
    }

    /// <summary>
    /// DuckDB 2.0 removes the join with the duplicate-eliminated outer values whenever it can: the correlated equality `u.a = t.a` of a subquery is not a filter inside it any more but a join condition
    /// `t.a IS NOT DISTINCT FROM g.a` against a subquery that is keyed by `a` (grouped by it, or filtered to `a IS NOT NULL`, which is what makes the null-safe equality an equality). This is the inverse:
    /// each keyed column of the subquery goes back to being a predicate on the outer value, put first in the WHERE as authors write the correlation, and the key is no longer a GROUP BY column. The
    /// column that carried the key now carries the outer value (marked Outer), as in the 1.x plan.
    /// Null when the subquery is not keyed that way (the caller then keeps the join, which is exact too, or refuses).
    /// </summary>
    /// <param name="keys">Per key: the index of the subquery column and the text of the outer value (already marked as a reference to the enclosing block).</param>
    /// <param name="nullSafeFallback">A key without the `IS NOT NULL` filter is written `IS NOT DISTINCT FROM` instead of being refused (an EXISTS or IN, where only the rows matter).</param>
    private static Rel? PushCorrelation(Rel right, IReadOnlyList<(int Index, string Outer)> keys, bool nullSafeFallback)
    {
        if (right.SetOp != null || right.Limit != null || right.Offset != null || right.HasWindow || right.IsDelim) return null;
        var copy = right.Clone();
        var unplaced = new List<string>();
        foreach (var (index, outer) in keys)
        {
            if (index >= copy.Sel.Count || copy.Sel[index].Outer) return null;
            var key = copy.Sel[index].Sql;
            // DuckDB put the `IS NOT NULL` where the author's equality stood among the other conditions: the predicate takes its place
            var at = copy.Where.IndexOf($"({key} IS NOT NULL)");
            var notNull = at >= 0;
            if (copy.HasAgg)
            {
                // the key must be one of the groups; a group the author wrote on the same column stays (it is another entry of the list)
                var group = copy.Group.IndexOf(key);
                if (!notNull || group < 0) return null;
                copy.Group.RemoveAt(group);
            }
            else if (!notNull && !nullSafeFallback) return null;
            var predicate = $"({key} {(notNull ? "=" : "IS NOT DISTINCT FROM")} {outer})";
            if (notNull) copy.Where[at] = predicate; else unplaced.Add(predicate);
            copy.Sel[index] = copy.Sel[index] with { Sql = outer, Outer = true };
        }
        copy.Where.InsertRange(0, unplaced);
        return copy;
    }

    /// <summary>The outer block of a keyed join as a subquery can sit in: not a DISTINCT, LIMIT or window, and a column of something when it is an aggregate; and the keys it supplies.</summary>
    private (Rel Left, IReadOnlyList<string> Outs, List<(int Index, string Outer)> Keys)? PrepareKeyedLeft(JsonElement p, Rel l)
    {
        var left = l;
        if (!left.Mergeable() || left.HasWindow) left = Wrap(left);
        var lo = left.Outs();
        var keys = KeysOf(p, lo);
        if (keys == null) return null;
        if (left.HasAgg && keys.Any(k => !Regex.IsMatch(k.Outer, "^\u0002[^\u0001\0\u0002]*\u0001[^\0\u0002]*\u0002$")))
        {
            left = Wrap(left);
            lo = left.Outs();
            keys = KeysOf(p, lo)!;
        }
        return (left, lo, keys);
    }

    /// <summary>
    /// The scalar subquery with `ORDER BY ... LIMIT k` (a SINGLE join): DuckDB numbers the rows of each key with row_number() and keeps those up to k, which the author wrote as a LIMIT inside the subquery.
    /// Recognized before the subquery is lowered, because the window and the filter over it are one thing here: the partition is the correlated key, so the pushed-in predicate replaces it.
    /// </summary>
    private Rel? TryKeyedLimit(JsonElement p, List<JsonElement> kids, Rel l)
    {
        if (Str(p, "join_type") != "SINGLE" || l.IsDelim || p.TryGetProperty("expression", out _)) return null;
        var filter = kids[1];
        if (Str(filter, "type") != "LOGICAL_FILTER" || Arr(filter, "expressions").ToList() is not [{ } cond] || Str(cond, "type") != "COMPARE_LESSTHANOREQUALTO") return null;
        if (Arr(filter, "children").ToList() is not [{ } window] || Str(window, "type") != "LOGICAL_WINDOW" || Arr(window, "expressions").ToList() is not [{ } numbering] || Str(numbering, "type") != "WINDOW_ROW_NUMBER") return null;
        if (Arr(window, "children").ToList() is not [{ } source]) return null;
        var counter = cond.GetProperty("left"); var bound = cond.GetProperty("right");
        if (Str(counter, "type") != "BOUND_REF" || Str(bound, "type") != "VALUE_CONSTANT" || !bound.GetProperty("value").TryGetProperty("value", out var limitValue) || !limitValue.TryGetInt64(out var limit)) return null;
        var partitions = Arr(numbering, "partitions").ToList();
        if (partitions.Count == 0 || partitions.Any(x => Str(x, "type") != "BOUND_REF")) return null;
        if (PrepareKeyedLeft(p, l) is not var (left, lo, keys)) return null;
        if (!keys.Select(k => k.Index).Order().SequenceEqual(partitions.Select(x => x.GetProperty("index").GetInt32()).Distinct().Order())) return null;
        if (keys.Any(k => Projected(p, k.Index))) return null;
        var rows = Node(source);
        if (!rows.Mergeable()) rows = Wrap(rows);
        if (rows.Sel.Count != counter.GetProperty("index").GetInt32()) return null;
        var orders = Arr(numbering, "orders").Select(o => OrderItem(o, rows.Outs())).ToList();
        if (PushCorrelation(rows, keys, nullSafeFallback: false) is not { } pushed) return null;
        pushed.Order = orders;
        pushed.Limit = limit;
        pushed.Sel = pushed.Sel.Append(new Item("NULL", null, "BIGINT")).ToList();        // the row number is only ever compared with the limit
        return FinishDelimJoin(p, left, lo, pushed, "SINGLE");
    }

    /// <summary>The keys of a join's NOT DISTINCT conditions: the subquery column each is against, and the outer value it carries (null when a condition is not of that form).</summary>
    private List<(int Index, string Outer)>? KeysOf(JsonElement p, IReadOnlyList<string> lo)
    {
        var keys = new List<(int, string)>();
        foreach (var c in Arr(p, "conditions"))
        {
            if (!c.TryGetProperty("right", out var r)) return null;
            if (Str(c, "comparison") != "COMPARE_NOT_DISTINCT_FROM" || Str(r, "type") != "BOUND_REF") return null;
            keys.Add((r.GetProperty("index").GetInt32(), Requalify(Expr(c.GetProperty("left"), lo))));
        }
        return keys.Count == 0 ? null : keys;
    }

    /// <summary>
    /// A SINGLE, LEFT or INNER join of a block to a keyed subquery (see <see cref="PushCorrelation"/>) is the correlated subquery the author wrote:
    /// <list type="bullet">
    /// <item>a subquery that is an aggregate (SINGLE or LEFT, or INNER when the filter above rejects the rows that found nothing) is a scalar subquery in the select list, as it was in 1.x (where it was a SINGLE
    /// delim join). The aggregate must give NULL over no rows (a count does not, and DuckDB guards it differently: <see cref="TryCountPatch"/>), because the join gives NULL for an outer value with no match;</item>
    /// <item>a SINGLE join of a plain subquery is a scalar subquery (it errors on more than one row, as the engines do);</item>
    /// <item>a LEFT or INNER join of a plain subquery is a LATERAL join.</item>
    /// </list>
    /// Anything else is not changed: a join to a derived table has the same rows.
    /// </summary>
    private Rel? TryKeyedJoin(JsonElement p, Rel l, Rel r)
    {
        var joinKind = Str(p, "join_type");
        if (joinKind is not ("SINGLE" or "LEFT" or "INNER") || p.TryGetProperty("expression", out _) || l.IsDelim || r.IsDelim) return null;
        if (PrepareKeyedLeft(p, l) is not var (left, lo, keys)) return null;
        // scalar subqueries do not carry the key, so a key the query reads again means the join stays
        var scalar = joinKind == "SINGLE" || r.HasAgg;
        if (scalar && keys.Any(k => Projected(p, k.Index))) return null;
        if (r.HasAgg)
        {
            if (!r.EmptyIsNull) return null;
            if (joinKind == "INNER" && !RejectsNullRowsOf(p, left.Sel.Count)) return null;
        }
        if (PushCorrelation(r, keys, nullSafeFallback: false) is not { } pushed) return null;
        var type = scalar ? "SINGLE" : joinKind;
        return FinishDelimJoin(p, left, lo, pushed, type);
    }

    /// <summary>
    /// True when the filter that reads this INNER join's output rejects every row whose subquery columns (from <paramref name="firstRightColumn"/> on) are NULL: a comparison that is NULL when one of its operands
    /// is. Then the rows the inner join drops (an outer value with no match) are rows the filter drops anyway, and the join is the same as a scalar subquery that gives NULL for them.
    /// </summary>
    private bool RejectsNullRowsOf(JsonElement join, int firstRightColumn)
    {
        if (nodeStack.Count < 2) return false;
        var parent = nodeStack.ElementAt(1);        // the join itself is on top
        if (Str(parent, "type") != "LOGICAL_FILTER") return false;

        bool Rejects(JsonElement e)
        {
            switch (Str(e, "type"))
            {
                case "CONJUNCTION_AND": return Arr(e, "children").Any(Rejects);
                case "COMPARE_EQUAL" or "COMPARE_NOTEQUAL" or "COMPARE_LESSTHAN" or "COMPARE_GREATERTHAN" or "COMPARE_LESSTHANOREQUALTO" or "COMPARE_GREATERTHANOREQUALTO":
                    return e.TryGetProperty("left", out var a) && e.TryGetProperty("right", out var b) && (StrictRight(a, firstRightColumn) || StrictRight(b, firstRightColumn));
                default: return false;
            }
        }
        return Arr(parent, "expressions").Any(Rejects);
    }

    /// <summary>An operand that is NULL when a column of the subquery is NULL.</summary>
    private static bool StrictRight(JsonElement e, int firstRightColumn) => Str(e, "type") switch
    {
        "BOUND_REF" => e.GetProperty("index").GetInt32() >= firstRightColumn,
        "OPERATOR_CAST" => StrictRight(e.GetProperty("child"), firstRightColumn),
        "BOUND_FUNCTION" => NullPropagating.Contains(Str(e, "name") ?? "") && Arr(e, "children").Any(c => StrictRight(c, firstRightColumn)),
        _ => false,
    };

    /// <summary>
    /// DuckDB 2.0 guards a correlated `count` against the COUNT bug: an outer key with no rows must give 0, not NULL. Its plan is the grouped count LEFT-joined to the
    /// distinct outer keys, LEFT-joined again to a count over an empty result, and a CASE that picks the empty count when the key found nothing:
    /// <code>PROJECTION [CASE WHEN marker IS NULL THEN empty_count ELSE count END, key]  over  LEFT(LEFT(DELIM_GET, AGG(count, count GROUP BY key)), AGG(count OVER EMPTY))</code>
    /// A scalar subquery `(SELECT count(*) ...)` already yields 0 over no rows, so the whole shape is just the grouped aggregate: this returns its count and the carried key,
    /// which is what the 1.x plan had.
    /// </summary>
    /// <summary>
    /// DuckDB 2.0 lists the columns of the right side that a join outputs in `right_projection_map` (it drops the carried copy of a correlated key, for example). Absent, as in
    /// 1.x, every column is output.
    /// </summary>
    /// <summary>2.0 also lists, per join, the columns of the left side to output (and may repeat one).</summary>
    private static IReadOnlyList<Item> LeftColumns(JsonElement p, IReadOnlyList<Item> left) =>
        p.TryGetProperty("left_projection_map", out var map) && map.ValueKind == JsonValueKind.Array && map.GetArrayLength() > 0
            ? map.EnumerateArray().Select(i => left[i.GetInt32()]).ToList() : left;

    private static Rel MapLeft(JsonElement p, Rel left)
    {
        if (!p.TryGetProperty("left_projection_map", out var map) || map.ValueKind != JsonValueKind.Array || map.GetArrayLength() == 0) return left;
        var copy = left.Clone();
        copy.Sel = LeftColumns(p, left.Sel).ToList();
        return copy;
    }

    private static IReadOnlyList<Item> RightColumns(JsonElement p, IReadOnlyList<Item> right) =>
        p.TryGetProperty("right_projection_map", out var map) && map.ValueKind == JsonValueKind.Array && map.GetArrayLength() > 0
            ? map.EnumerateArray().Select(i => right[i.GetInt32()]).ToList() : right;

    private static bool Projected(JsonElement p, int rightIndex) =>
        !(p.TryGetProperty("right_projection_map", out var map) && map.ValueKind == JsonValueKind.Array && map.GetArrayLength() > 0) || map.EnumerateArray().Any(i => i.GetInt32() == rightIndex);

    private Rel? TryCountPatch(JsonElement p, List<JsonElement> kids)
    {
        if (kids.Count != 1 || Str(kids[0], "type") != "LOGICAL_COMPARISON_JOIN" || Str(kids[0], "join_type") != "LEFT" || Arr(kids[0], "conditions").Any()) return null;
        var outer = Arr(kids[0], "children").ToList();
        if (outer.Count != 2 || Str(outer[1], "type") != "LOGICAL_AGGREGATE_AND_GROUP_BY" || Arr(outer[1], "groups").Any()) return null;
        var emptyKids = Arr(outer[1], "children").ToList();
        if (emptyKids.Count != 1 || Str(emptyKids[0], "type") != "LOGICAL_EMPTY_RESULT") return null;
        var inner = outer[0];
        if (Str(inner, "type") != "LOGICAL_COMPARISON_JOIN" || Str(inner, "join_type") != "LEFT" || Arr(inner, "conditions").Any(c => Str(c, "comparison") != "COMPARE_NOT_DISTINCT_FROM")) return null;
        var inside = Arr(inner, "children").ToList();
        if (inside.Count != 2 || Str(inside[0], "type") != "LOGICAL_DELIM_GET" || Str(inside[1], "type") != "LOGICAL_AGGREGATE_AND_GROUP_BY") return null;
        if (!Arr(outer[1], "expressions").All(e => Str(e, "name") is "count_star" or "count")) throw new LoweringException("a correlated aggregate that is not a count, over a COUNT-bug guard");

        var delim = Node(inside[0]);
        var agg = Node(inside[1]);
        // DuckDB 2.0 keys the grouped count by the outer value (NOT DISTINCT FROM join, a group by and `IS NOT NULL`) rather than filtering by it
        var countKeys = new List<(int Index, string Outer)>();
        foreach (var c in Arr(inner, "conditions"))
        {
            var le = c.GetProperty("left"); var re = c.GetProperty("right");
            if (Str(re, "type") == "BOUND_REF" && re.GetProperty("index").GetInt32() is var ri && ri < agg.Sel.Count && !agg.Sel[ri].Outer && IsOuterRef(le, delim.Sel))
                countKeys.Add((ri, delim.Sel[le.GetProperty("index").GetInt32()].Sql));
        }
        if (countKeys.Count > 0)
        {
            if (PushCorrelation(agg, countKeys, nullSafeFallback: false) is not { } keyedCount) return null;
            agg = keyedCount;
        }
        var items = delim.Sel.Concat(agg.Sel).Append(new Item("0", null, "BIGINT")).ToList();
        var outs = items.Select(i => i.Sql).ToList();
        var result = new List<Item>();
        foreach (var e in Arr(p, "expressions"))
        {
            if (Str(e, "type") == "CASE_EXPR" && Arr(e, "case_checks").ToList() is [{ } check] && IsGuardCase(e, check, delim.Sel.Count, items.Count - 1, out var count))
                result.Add(items[count] with { Outer = false });
            else
                result.Add(new Item(Expr(e, outs), Str(e, "alias"), TypeNameOf(e), IsOuterRef(e, items)));
        }
        var rel = agg.Clone();
        rel.Sel = result;
        rel.Plain = false;
        return rel;
    }

    /// <summary>`CASE WHEN #marker IS NULL THEN #empty ELSE #count END`: the marker is a column of the grouped side, the empty count is the last column.</summary>
    private static bool IsGuardCase(JsonElement e, JsonElement check, int firstAggColumn, int emptyColumn, out int count)
    {
        count = -1;
        var when = check.GetProperty("when_expr");
        var then = check.GetProperty("then_expr");
        var other = e.GetProperty("else_expr");
        if (Str(when, "type") != "OPERATOR_IS_NULL" || Arr(when, "children").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } m || Str(m, "type") != "BOUND_REF") return false;
        if (Str(then, "type") != "BOUND_REF" || then.GetProperty("index").GetInt32() != emptyColumn || Str(other, "type") != "BOUND_REF") return false;
        count = other.GetProperty("index").GetInt32();
        return m.GetProperty("index").GetInt32() >= firstAggColumn && count >= firstAggColumn;
    }

    /// <summary>
    /// A MARK join that is not wrapped in a delim join: how DuckDB 2.0 hands over EXISTS and IN. Its decorrelation has already happened, so the correlated predicates are join
    /// conditions between the outer (left) and the subquery (right) side. An EQUAL condition is the comparison of an IN; the other conditions are the correlation and go back into
    /// the subquery's WHERE as predicates on the outer columns. The null-safe equality (NOT DISTINCT FROM) DuckDB uses for a correlated `=` is written as `=` again when the
    /// subquery side already filters that column to not NULL, which is exactly what makes the two the same.
    /// </summary>
    private static void RequireComparisons(JsonElement p)
    {
        if (p.TryGetProperty("conditions", out var cs) && cs.ValueKind == JsonValueKind.Array && cs.EnumerateArray().Any(c => !c.TryGetProperty("right", out _)))
            throw new LoweringException("a correlated join condition that is not a comparison of its two sides");
    }

    private Rel MarkJoin(JsonElement p, List<JsonElement> kids)
    {
        RequireComparisons(p);
        var left = Node(kids[0]);
        if (!left.Mergeable() || left.HasWindow) left = Wrap(left);
        var right = Node(kids[1]);
        if (right.SetOp != null) throw new LoweringException("a correlated subquery over a set operation (UNION, INTERSECT, EXCEPT)");
        var lo = left.Outs();
        var ro = right.Outs();

        var comparisons = new List<JsonElement>();       // the comparison(s) of an IN, ANY or ALL: an outer expression against a column of the subquery
        var correlated = new List<string>();
        // a correlated `=` against a column of the subquery goes back inside it (also when the subquery is grouped by that column)
        var keys = new List<(int Index, string Outer)>();
        var keyed = new HashSet<string>();
        foreach (var c in Arr(p, "conditions"))
            if (Str(c, "comparison") == "COMPARE_NOT_DISTINCT_FROM" && Str(c.GetProperty("right"), "type") == "BOUND_REF" && c.GetProperty("right").GetProperty("index").GetInt32() is var keyIndex && keyIndex < right.Sel.Count && !right.Sel[keyIndex].Outer)
                keys.Add((keyIndex, Requalify(Expr(c.GetProperty("left"), lo))));
        if (keys.Count > 0 && PushCorrelation(right, keys, nullSafeFallback: true) is { } pushed)
        {
            right = pushed;
            ro = right.Outs();
            foreach (var k in keys) keyed.Add(k.Index + "|" + k.Outer);
        }
        foreach (var c in Arr(p, "conditions"))
        {
            var op = Str(c, "comparison")!;
            if (op != "COMPARE_NOT_DISTINCT_FROM") { comparisons.Add(c); continue; }
            if (Str(c.GetProperty("right"), "type") == "BOUND_REF" && keyed.Contains(c.GetProperty("right").GetProperty("index").GetInt32() + "|" + Requalify(Expr(c.GetProperty("left"), lo)))) continue;
            // the null-safe equality DuckDB uses for a correlated `=`: a predicate of the subquery on an outer column
            if (right.HasAgg || right.HasWindow || !right.Mergeable()) throw new LoweringException("a correlated predicate over an aggregate");
            var l = Expr(c.GetProperty("left"), lo);
            var r = Expr(c.GetProperty("right"), ro);
            var text = "IS NOT DISTINCT FROM";
            if (right.Where.Remove($"({r} IS NOT NULL)")) text = "=";
            correlated.Add($"({r} {text} {Requalify(l)})");
        }
        if (correlated.Count > 0)
        {
            right = right.Clone();
            right.Where.AddRange(correlated);
        }

        Item mark;
        if (comparisons.Count == 0) mark = new Item($"EXISTS (\n{SubqueryWith(right, "1")}\n)", null, "BOOLEAN");
        else if (comparisons.Count == 1 && Str(comparisons[0], "comparison") == "COMPARE_EQUAL")
            mark = new Item($"({Expr(comparisons[0].GetProperty("left"), lo)} IN (\n{SubqueryWith(right, Expr(comparisons[0].GetProperty("right"), right.Outs()))}\n))", null, "BOOLEAN");
        else mark = ComparisonMark(comparisons, lo, right);
        left = MapLeft(p, left);
        left.Sel = left.Sel.Append(mark).ToList();
        left.Plain = false;
        return left;
    }

    /// <summary>
    /// The mark of `x op ANY (S)`, `x op ALL (S)` (DuckDB writes it as `NOT (x op' ANY (S))`) and `(a, b) IN (S)`: a row of S matches when every comparison is TRUE, and the mark is TRUE if some row matches, FALSE
    /// if no row matches or could (a comparison on a NULL operand), NULL otherwise. Written with predicates only: TRUE as `EXISTS (S WHERE p)`, and FALSE as `NOT EXISTS (S WHERE p OR an operand IS NULL)`.
    /// For one comparison that is exactly SQL's three-valued logic. For a row of several, DuckDB lets a NULL in any component make the row unknown even when another component is definitely different (where SQL's
    /// AND would say FALSE), so `(1, 2) NOT IN ((NULL, 3))` is not true in DuckDB; the lowering follows DuckDB, and the differential tests would show it if a DuckDB release changed that. A subquery with its own
    /// aggregate, window, DISTINCT or LIMIT is a derived table first.
    /// </summary>
    private Item ComparisonMark(List<JsonElement> conditions, IReadOnlyList<string> lo, Rel right)
    {
        if (right.HasAgg || right.HasWindow || !right.Mergeable()) right = Wrap(right);
        var ro = right.Outs();
        var parts = new List<string>();
        var nulls = new List<string>();
        var perPart = new List<string>();
        foreach (var c in conditions)
        {
            var op = Str(c, "comparison")!;
            if (op is not ("COMPARE_EQUAL" or "COMPARE_NOTEQUAL" or "COMPARE_LESSTHAN" or "COMPARE_GREATERTHAN" or "COMPARE_LESSTHANOREQUALTO" or "COMPARE_GREATERTHANOREQUALTO"))
                throw new LoweringException("a subquery comparison other than IN, ANY and ALL");
            // written the way a subquery is usually written, its own column first: `b.z < a.x`
            var flipped = op switch { "COMPARE_LESSTHAN" => ">", "COMPARE_GREATERTHAN" => "<", "COMPARE_LESSTHANOREQUALTO" => ">=", "COMPARE_GREATERTHANOREQUALTO" => "<=", _ => Comparisons[op] };
            var (inner, outer) = (Expr(c.GetProperty("right"), ro), Requalify(Expr(c.GetProperty("left"), lo)));
            parts.Add($"({inner} {flipped} {outer})");
            perPart.Add($"(({inner} {flipped} {outer}) OR {inner} IS NULL OR {outer} IS NULL)");
            nulls.Add($"{inner} IS NULL");
            nulls.Add($"{outer} IS NULL");
        }
        var predicate = parts.Count == 1 ? parts[0] : "(" + string.Join(" AND ", parts) + ")";
        var yes = right.Clone();
        yes.Where.Add(predicate);
        var maybe = right.Clone();
        // DuckDB 1.x makes a row unknown when any component is NULL; 2.0 follows the standard: a component that is definitely different makes the row definitely not a match
        maybe.Where.Add(standardRowNulls && parts.Count > 1 ? "(" + string.Join(" AND ", perPart) + ")" : $"({predicate} OR {string.Join(" OR ", nulls.Distinct())})");
        marks.Add(($"EXISTS (\n{SubqueryWith(yes, "1")}\n)", $"(NOT EXISTS (\n{SubqueryWith(maybe, "1")}\n))"));
        return new Item($"\u0003{marks.Count - 1}\u0003", null, "BOOLEAN");
    }

    /// <summary>
    /// A comparison mark that was not consumed by a filter is a value: TRUE when a row matches, FALSE when none can, NULL otherwise, written as a `CASE` over the two predicates (one `EXISTS` for each), which
    /// the target rules then lower like any boolean value. A mark that is still in the text after that was carried out of the query level its predicates belong to, and is refused.
    /// </summary>
    private string MarksAsValues(string sql)
    {
        return Regex.Replace(sql, "\u0003([0-9]+)\u0003", m =>
        {
            var mark = marks[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
            return $"(CASE WHEN {mark.True} THEN TRUE WHEN {mark.False} THEN FALSE ELSE NULL END)";
        });
    }

    private static bool HoldsSubquery(string sql) => Regex.IsMatch(sql, @"\(\s*(WITH|SELECT)\b", RegexOptions.IgnoreCase);

    private static bool ReferencesMark(JsonElement e, IReadOnlyList<string> outs)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (Str(e, "type") == "BOUND_REF" && e.TryGetProperty("index", out var ix) && ix.GetInt32() is var i && i < outs.Count && outs[i].Contains('\u0003')) return true;
            return e.EnumerateObject().Any(pr => ReferencesMark(pr.Value, outs));
        }
        return e.ValueKind == JsonValueKind.Array && e.EnumerateArray().Any(x => ReferencesMark(x, outs));
    }

    /// <summary>A filter condition. One that does not use a comparison mark is an ordinary expression.</summary>
    private string Predicate(JsonElement e, IReadOnlyList<string> outs) => ReferencesMark(e, outs) ? Truth(e, outs, wantTrue: true) : Expr(e, outs);

    /// <summary>
    /// A predicate that is TRUE exactly when <paramref name="e"/> is TRUE (or, with <paramref name="wantTrue"/> false, exactly when it is FALSE), for an expression whose comparison marks sit directly under
    /// AND, OR and NOT. A mark anywhere else is a value.
    /// </summary>
    private string Truth(JsonElement e, IReadOnlyList<string> outs, bool wantTrue)
    {
        if (!ReferencesMark(e, outs)) { var plain = Expr(e, outs); return wantTrue ? plain : $"(NOT {plain})"; }
        switch (Str(e, "type"))
        {
            case "BOUND_REF":
            {
                var text = outs[e.GetProperty("index").GetInt32()];
                if (!Regex.IsMatch(text, "^\u0003[0-9]+\u0003$")) return AsValueCondition(e, outs, wantTrue);
                var mark = marks[int.Parse(text.Trim('\u0003'), System.Globalization.CultureInfo.InvariantCulture)];
                return wantTrue ? mark.True : mark.False;
            }
            case "OPERATOR_NOT": return Truth(Arr(e, "children").First(), outs, !wantTrue);
            case "CONJUNCTION_AND" or "CONJUNCTION_OR":
            {
                var and = (Str(e, "type") == "CONJUNCTION_AND") == wantTrue;      // TRUE of an AND, and FALSE of an OR, need every part
                return "(" + string.Join(and ? " AND " : " OR ", Arr(e, "children").Select(c => Truth(c, outs, wantTrue))) + ")";
            }
            default: return AsValueCondition(e, outs, wantTrue);
        }
    }

    /// <summary>A condition that uses a mark as a value (`(a > ANY (S)) IS NULL`, a `CASE`): the marks become their values, and the condition holds when the expression is TRUE (or FALSE).</summary>
    private string AsValueCondition(JsonElement e, IReadOnlyList<string> outs, bool wantTrue)
    {
        var text = MarksAsValues(Expr(e, outs));
        return wantTrue ? $"({text})" : $"(NOT ({text}))";
    }

    /// <summary>A join where one side is the duplicate-eliminated outer values: those are not a table here, they are the outer query, so the join disappears.</summary>
    private Rel JoinWithDelim(JsonElement p, string type, Rel l, Rel r)
    {
        RequireComparisons(p);
        var other = l.IsDelim ? r : l;
        if (l.IsDelim && r.IsDelim) throw new LoweringException("a join of two sets of correlated values");
        if (type == "LOGICAL_COMPARISON_JOIN")
        {
            var jt = Str(p, "join_type");
            if (jt == "SEMI" && r.IsDelim && !l.IsDelim && !l.HasAgg && l.Mergeable() && p.TryGetProperty("conditions", out _) && Arr(p, "conditions").All(c => Str(c, "comparison") == "COMPARE_EQUAL"))
            {
                // DuckDB 2.0 keeps the rows of a subquery whose key is among the outer values (a SEMI join against them) before grouping by the key. For each outer row that is the equality with its value,
                // which the keyed join above puts back; here the key is only known to be a value, not NULL (an `IS NOT NULL` that the keyed join turns into the equality).
                var semi = l.Clone();
                var lOuts = l.Outs();
                foreach (var c in Arr(p, "conditions")) semi.Where.Add($"({Expr(c.GetProperty("left"), lOuts)} IS NOT NULL)");
                return semi;
            }
            if (jt is not ("INNER" or "LEFT") || (jt == "LEFT" && !l.IsDelim)) throw new LoweringException($"the correlated join type {jt}");
            var lo = l.Outs();
            // DuckDB 2.0 joins the outer values to a subquery that is keyed by them (`delim.a IS NOT DISTINCT FROM g.a` over a group by `a`) instead of filtering inside it: the key goes back inside
            var pushed = new HashSet<int>();
            if (l.IsDelim && jt is "INNER" or "LEFT")
            {
                var keys = new List<(int Index, string Outer)>();
                var all = Arr(p, "conditions").ToList();
                for (var i = 0; i < all.Count; i++)
                {
                    var le = all[i].GetProperty("left"); var re = all[i].GetProperty("right");
                    if (Str(all[i], "comparison") == "COMPARE_NOT_DISTINCT_FROM" && IsOuterRef(le, l.Sel) && Str(re, "type") == "BOUND_REF" && !IsOuterRef(re, r.Sel))
                    {
                        keys.Add((re.GetProperty("index").GetInt32(), l.Sel[le.GetProperty("index").GetInt32()].Sql));
                        pushed.Add(i);
                    }
                }
                if (keys.Count > 0)
                {
                    if (PushCorrelation(r, keys, nullSafeFallback: false) is { } keyed) { r = keyed; other = r; }
                    else pushed.Clear();
                }
            }
            var ro = r.Outs();
            var position = -1;
            foreach (var c in Arr(p, "conditions"))
            {
                position++;
                if (pushed.Contains(position)) continue;
                var le = c.GetProperty("left"); var re = c.GetProperty("right");
                var bothOuter = IsOuterRef(le, l.Sel) && IsOuterRef(re, r.Sel);
                if (bothOuter) continue;      // the join back to the outer value: true by construction
                if (other.HasAgg) throw new LoweringException("a correlated predicate over an aggregate");
                other.Where.Add(Compare(le, Expr(le, lo), Comparisons[Str(c, "comparison")!], re, Expr(re, ro)));
            }
            if (p.TryGetProperty("expression", out _)) throw new LoweringException("a correlated join with an extra expression");
        }
        var sel = LeftColumns(p, l.Sel).Concat(RightColumns(p, r.Sel)).ToList();
        other.Sel = sel;
        other.Plain = false;
        return other;
    }

    /// <summary>
    /// `DISTINCT ON (k) ... ORDER BY k, o` is `row_number() OVER (PARTITION BY k ORDER BY o) = 1` over the same rows. DuckDB picks an arbitrary row when the ordering leaves ties,
    /// and so would a window, so the result would differ between engines: it is lowered only when the ordering provably decides the row.
    /// </summary>
    private Rel DistinctOn(JsonElement p, Rel c)
    {
        if (!p.TryGetProperty("order_by", out var orderBy) || !orderBy.TryGetProperty("orders", out var ordersElement)) throw new LoweringException("DISTINCT ON without an ORDER BY (the row kept would be arbitrary)");
        if (c.SetOp != null) throw new LoweringException("DISTINCT ON over a set operation");
        if (!c.Mergeable() || c.HasWindow || c.HasAgg) throw new LoweringException("DISTINCT ON over a query that groups, limits or has windows of its own");
        var outs = c.Outs();
        var partitions = Arr(p, "distinct_targets").Select(e => Expr(e, outs)).ToList();
        var orders = ordersElement.EnumerateArray().ToList();
        var orderKeys = orders.Select(o => Expr(o.GetProperty("expression"), outs)).ToList();
        var remaining = orders.Where((o, i) => !partitions.Contains(orderKeys[i])).ToList();
        if (remaining.Count == 0) throw new LoweringException("DISTINCT ON whose ORDER BY only names the ON columns (the row kept would be arbitrary)");

        // the ordering must identify one row of every table the query reads
        var named = partitions.Concat(orderKeys).Select(t => Regex.Match(t, "^\0([^\u0001\0]*)\u0001([^\0]*)\0$")).Where(m => m.Success).Select(m => (Alias: m.Groups[1].Value, Column: m.Groups[2].Value)).ToList();
        foreach (var source in c.Sources)
        {
            var table = aliasTables.GetValueOrDefault(source);
            var grain = table == null || grainOf == null ? [] : grainOf(table);
            if (grain.Count == 0)
                throw new LoweringException($"DISTINCT ON over {(table == null ? "a derived table" : $"`{table}`, which has no declared grain")}: its ORDER BY cannot be shown to decide the row (declare a grain, or order by a unique key)");
            var missing = grain.Where(g => !named.Any(n => n.Alias == source && string.Equals(n.Column, g, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
                throw new LoweringException($"DISTINCT ON keeps an arbitrary row among ties: add the grain column(s) {string.Join(", ", missing.Select(m => $"`{m}`"))} of `{table}` to its ORDER BY");
        }

        var rowNumber = $"row_number() OVER (PARTITION BY {string.Join(", ", partitions)} ORDER BY {string.Join(", ", remaining.Select(o => OrderItem(o, outs)))})";
        c.Sel = c.Sel.Append(new Item(rowNumber, "rn", "BIGINT")).ToList();
        c.HasWindow = true;
        c.Plain = false;
        var wrapped = Wrap(c);
        wrapped.Where.Add($"({wrapped.Sel[^1].Sql} = 1)");
        return wrapped;
    }

    private static bool IsConstantRow(Rel r) => r.Frm.Length == 0 && r.Sources.Count == 0 && !r.IsDelim && r.SetOp == null && r.Where.Count == 0 && r.Group.Count == 0;

    /// <summary>
    /// An uncorrelated scalar subquery or EXISTS is a one-row relation crossed with the outer query. DuckDB writes the single-row guarantee of a scalar subquery as
    /// `CASE WHEN count(*) > 1 THEN error(...) ELSE first(x) END` over the subquery, and EXISTS as `count(*) = 1` over `LIMIT 1`. Both are turned back into the subquery.
    /// </summary>
    private Rel? TryUncorrelatedScalar(JsonElement p, List<JsonElement> kids)
    {
        if (kids.Count != 1 || Str(kids[0], "type") != "LOGICAL_AGGREGATE_AND_GROUP_BY" || kids[0].TryGetProperty("groups", out var gs) && gs.GetArrayLength() > 0) return null;
        var agg = kids[0];
        var aggs = Arr(agg, "expressions").ToList();
        var aggKids = Arr(agg, "children").ToList();
        if (aggKids.Count != 1) return null;
        var projections = Arr(p, "expressions").ToList();

        // EXISTS (SELECT ...): count(*) over a LIMIT 1, compared with 1
        if (aggs.Count == 1 && Str(aggs[0], "name") == "count_star" && Str(aggKids[0], "type") == "LOGICAL_LIMIT" && projections.Count == 1 &&
            Str(projections[0], "type") == "COMPARE_EQUAL" && projections[0].GetProperty("left").GetProperty("type").GetString() == "BOUND_REF" &&
            projections[0].GetProperty("right").GetProperty("type").GetString() == "VALUE_CONSTANT")
        {
            var want = projections[0].GetProperty("right").GetProperty("value").GetProperty("value").GetInt64();
            if (want is not (0 or 1)) return null;
            var limit = aggKids[0];
            if (!(Str(limit.GetProperty("limit_val"), "type") == "CONSTANT_VALUE" && limit.GetProperty("limit_val").GetProperty("constant_integer").GetInt64() == 1) || (limit.TryGetProperty("offset_val", out var off) && Str(off, "type") != "UNSET")) return null;
            var inner = Node(Arr(limit, "children").First());
            var text = $"EXISTS (\n{SubqueryWith(inner, "1")}\n)";
            return new Rel { Sel = [new Item(want == 1 ? text : $"(NOT {text})", Str(projections[0], "alias"), "BOOLEAN")] };
        }

        // (SELECT x FROM ...): first(x) guarded by count(*) > 1
        if (aggs.Count >= 1 && aggs.Count <= 2 && aggs.Any(a => Str(a, "name") == "first") && projections.Count >= 1 && projections.All(e => Str(e, "type") == "CASE_EXPR"))
        {
            var guarded = projections.All(e => Arr(e, "case_checks").Count() == 1 && Str(Arr(e, "case_checks").First().GetProperty("then_expr"), "name") == "error" && e.GetProperty("else_expr").GetProperty("type").GetString() == "BOUND_REF");
            if (!guarded) return null;
            var columns = new List<(JsonElement Expression, int Column)>();
            foreach (var e in projections)
            {
                var firstAgg = aggs[e.GetProperty("else_expr").GetProperty("index").GetInt32()];
                if (Str(firstAgg, "name") != "first") return null;
                columns.Add((e, Arr(firstAgg, "children").First().GetProperty("index").GetInt32()));
            }
            var inner = Node(aggKids[0]);          // only now that the shape is certain: lowering a node has side effects (aliases are numbered)
            var items = columns.Select(x => new Item($"(\n{SubqueryWith(inner, inner.Sel[x.Column].Sql)}\n)", Str(x.Expression, "alias"), TypeNameOf(x.Expression))).ToList();
            return new Rel { Sel = items };
        }
        return null;
    }

    private static string? TypeNameOf(JsonElement e) =>
        e.TryGetProperty("return_type", out var t) ? TypeName(t)
        : e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("type", out var vt) && vt.ValueKind == JsonValueKind.Object ? TypeName(vt)   // a constant carries its type on the value
        : null;
}
