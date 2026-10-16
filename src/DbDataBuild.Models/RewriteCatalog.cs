using DbDataBuild.Core;

namespace DbDataBuild.Models;

/// <summary>Where a rewrite is applied: while the DuckDB plan is lowered (the same for every target), or when the lowered query is made ready for one target.</summary>
public enum RewriteLayer { Lowering, Target, Both }

/// <param name="Name">The name used in `rewrites:` and printed in rendered headers.</param>
/// <param name="RequiredOn">Targets that cannot run the query without it. On every other target the rewrite only keeps the answer equal to DuckDB's, so it can be turned off there.</param>
/// <param name="Targets">Targets the rewrite does something on at all.</param>
/// <param name="Exact">What the rewrite makes the engine do.</param>
/// <param name="Native">What the engine does without it.</param>
public sealed record RewriteRule(string Name, RewriteLayer Layer, IReadOnlyList<string> Targets, IReadOnlyList<string> RequiredOn, string Exact, string Native);

/// <summary>
/// Every rewrite the tool applies to make an engine agree with DuckDB (DESIGN.md 7.6.2), as data. A project can ask for the engines' own behavior instead of DuckDB's (`rewrites:` in
/// dbdatabuild.yml, or in a model): the rendered query is shorter and the engine's plan simpler, at the price of the difference written in <see cref="RewriteRule.Native"/>, which the matrix
/// linter then reports again as a note. A rewrite an engine cannot do without is never turned off.
/// </summary>
public static class RewriteCatalog
{
    private static readonly string[] SqlServer = [TargetNames.SqlServer, TargetNames.Fabric];
    private static readonly string[] Postgres = [TargetNames.Postgres];
    private static readonly string[] Both = [TargetNames.SqlServer, TargetNames.Fabric, TargetNames.Postgres];
    private static readonly string[] None = [];
    private static readonly string[] Spark = ["spark"];
    private static readonly string[] Oracle = ["oracle"];
    private static readonly string[] SqlServer17 = [TargetNames.SqlServer];      // SQL Server 2025 and later: version 17 in `targets.sqlserver.version`
    private static readonly string[] BigQuery = ["bigquery"];

    public static readonly IReadOnlyList<RewriteRule> All =
    [
        new("length-trailing-spaces", RewriteLayer.Target, SqlServer, None, "length() counts trailing spaces: LEN(s + 'x') - 1", "LEN() ignores trailing spaces"),
        new("round-double", RewriteLayer.Both, Both, Postgres, "round(double, n) scales, rounds half away from zero and unscales", "SQL Server rounds the decimal text of the double (2.675 is 2.67); PostgreSQL has no round(double precision, integer) and the query is refused there"),
        new("try-cast-parse", RewriteLayer.Both, Both, None, "TRY_CAST of text to a number or date gives NULL for blank text and for text that is not a number", "SQL Server gives 0 or 1900-01-01 for blank text; PostgreSQL has no TRY_CAST and raises on bad data"),
        new("double-to-int", RewriteLayer.Both, [..SqlServer, ..Spark], None, "CAST(double AS integer) rounds half to even", "SQL Server cuts the fraction (2.9 is 2)"),
        new("decimal-to-int", RewriteLayer.Lowering, Both, None, "CAST(decimal AS integer) rounds half away from zero", "SQL Server cuts the fraction (2.5 is 2)"),
        new("double-to-decimal", RewriteLayer.Lowering, Both, None, "CAST(double AS DECIMAL(p, s)) scales by 10^s and rounds half away from zero (0.285 is 0.28)", "SQL Server converts the exact binary value (819.025 is 819.02), PostgreSQL the shortest text (0.285 is 0.29)"),
        new("avg-double", RewriteLayer.Lowering, Both, None, "avg of integers or decimals is a DOUBLE", "the engine's own result type (SQL Server: integer average truncated, decimal average a decimal)"),
        new("sum-widen", RewriteLayer.Lowering, Both, None, "sum of integers is widened so it cannot overflow (DuckDB sums into HUGEINT)", "SQL Server's SUM of an INT raises an overflow error past 2^31"),
        new("date-to-timestamp", RewriteLayer.Lowering, Both, None, "date_trunc and date arithmetic with an interval give a TIMESTAMP, as in DuckDB", "the engine keeps a DATE"),
        new("weekday-datefirst", RewriteLayer.Target, [..SqlServer, ..Spark], SqlServer, "date_part('dow' | 'isodow') counted from a fixed Sunday or Monday", "DATEPART(weekday) depends on the session's @@DATEFIRST, and a rendered script that reads session state is refused (DDB-319)"),
        new("date-diff-boundaries", RewriteLayer.Target, [..Postgres, ..Spark], None, "date_diff('year' | 'month' | 'quarter') counts boundaries crossed", "PostgreSQL's AGE() counts whole elapsed periods (2024-01-31 to 2024-03-01 is 1 month)"),
        new("date-diff-weeks", RewriteLayer.Target, [..Both, ..Spark, ..BigQuery], None, "date_diff('week') is the days between over seven, toward zero", "SQL Server counts week boundaries from @@DATEFIRST; PostgreSQL's cast rounds"),
        new("date-plus-days", RewriteLayer.Both, SqlServer, SqlServer, "date + n and date - n add and take away days", "SQL Server refuses `date + int` (operand type clash)"),
        new("concat-plus", RewriteLayer.Target, SqlServer, SqlServer, "every || is written as + before the transpile", "polyglot leaves || inside strpos(), position() and starts_with(), a syntax error on SQL Server"),
        new("pad-to-length", RewriteLayer.Target, SqlServer, SqlServer, "lpad and rpad with a literal length and pad are written out", "SQL Server has no LPAD or RPAD (before 2025)"),
        new("split-part", RewriteLayer.Target, [..Postgres, ..Spark], [..Postgres, ..Spark], "split_part is PostgreSQL's own", "the plan's array_extract(string_split(...)) does not exist on PostgreSQL"),
        new("json-extract-string", RewriteLayer.Target, Postgres, Postgres, "json_extract_string(x, '$.a.b[1]') with a simple literal path is the chain json_extract_path(json_extract_path(x::json, 'a'), 'b') ... json_extract_path_text(..., 1)", "polyglot writes `x ->> '$.a.b'`, which PostgreSQL rejects for text and which takes a key, not a path"),
        new("json-array-length", RewriteLayer.Target, Postgres, Postgres, "json_array_length(x) casts the text to json", "PostgreSQL's json_array_length takes json, not text"),
        new("regexp-full-match", RewriteLayer.Target, [..Postgres, ..Spark, ..SqlServer17], [..Postgres, ..Spark], "regexp_full_match(s, p) matches the whole text: the pattern is anchored", "polyglot writes the unanchored `~`, so a partial match counts"),
        new("regexp-extract", RewriteLayer.Target, [..Postgres, ..SqlServer17], [..Postgres, ..SqlServer17], "regexp_extract(s, p, n) is the nth group of regexp_match (REGEXP_SUBSTR with the group on SQL Server 2025), or '' when nothing matches", "PostgreSQL has no regexp_extract"),
        new("division-by-zero", RewriteLayer.Target, Spark, None, "a / b is Infinity, -Infinity or NaN when b is 0, as DuckDB gives", "Spark 4 (ANSI mode) raises DIVIDE_BY_ZERO"),
        new("concat-skips-null", RewriteLayer.Target, Spark, Spark, "concat(a, b) treats NULL as empty text: written as concat_ws('', a, b)", "Spark's concat gives NULL when any argument is NULL"),
        new("regexp-replace-flags", RewriteLayer.Target, Spark, Spark, "regexp_replace with the 'g' flag drops it (Spark replaces every match) and writes group references as $1", "Spark reads the fourth argument as a start position, and \\1 as a literal"),
        new("substring-bounds", RewriteLayer.Target, Spark, None, "substr with a literal start below 1 or a negative length, and left with a negative count, give what DuckDB gives", "Spark counts a start of 0 as 1 and gives '' for a negative length or count"),
        new("week-of-year", RewriteLayer.Target, Spark, Spark, "week(d) is EXTRACT(WEEK FROM d), the ISO week", "Spark has no function named week"),
        new("mod-function", RewriteLayer.Target, [..Oracle, ..BigQuery], [..Oracle, ..BigQuery], "a % b is written MOD(a, b)", "the transpile leaves `%`, which Oracle and BigQuery do not have"),
        new("varchar-length", RewriteLayer.Target, Oracle, Oracle, "CAST(x AS VARCHAR) is VARCHAR2(4000)", "a VARCHAR without a length is a CLOB on Oracle, which most functions refuse"),
        new("left-right-substr", RewriteLayer.Target, Oracle, Oracle, "left(s, n) and right(s, n) with a positive literal n are SUBSTR", "Oracle has no LEFT or RIGHT"),
        new("date-diff-argument-order", RewriteLayer.Target, BigQuery, BigQuery, "date_diff('day' | 'month' | 'quarter' | 'year', a, b) is DATE_DIFF(b, a, PART)", "the transpile writes DuckDB's argument order with the part as a column name"),
        new("regexp-replace-first", RewriteLayer.Target, SqlServer17, None, "regexp_replace replaces the first match, or every match with the 'g' flag: the occurrence is written out (1, or 0)", "SQL Server's REGEXP_REPLACE replaces every match"),
        new("now-with-zone", RewriteLayer.Target, SqlServer, None, "now() and current_timestamp are SYSDATETIMEOFFSET(), a point in time", "GETDATE() is the server's local clock without a zone: stored in a column with a zone it is read as UTC, wrong by the server's offset"),
        new("log-base-ten", RewriteLayer.Target, SqlServer, None, "log(x) with one argument is the base-10 logarithm: LOG10(x)", "T-SQL's LOG(x) is the natural logarithm (log(2.5) is 0.916 where DuckDB gives 0.398)"),
        new("log-with-base", RewriteLayer.Target, Postgres, Postgres, "log(b, x) is ln(x) / ln(b) in double arithmetic, which is how DuckDB computes it", "PostgreSQL's log(b, x) takes numeric: with a double precision argument the call fails (function log(integer, double precision) does not exist)"),
        new("string-agg-array", RewriteLayer.Target, Postgres, Postgres, "string_agg is array_to_string(array_agg(... ORDER BY ...), sep)", "polyglot writes LISTAGG, which PostgreSQL does not have"),
    ];

    public static RewriteRule? Find(string name) => All.FirstOrDefault(r => r.Name == name);

    public static IReadOnlyList<string> Names => All.Select(r => r.Name).ToList();

    private static bool RequiredFor(RewriteRule rule, IReadOnlyList<string> targets) => rule.RequiredOn.Any(t => targets.Contains(t));

    /// <summary>
    /// The rewrites that are off for a model with the given targets. Project settings first, then the model's own on top: `fidelity: native` turns off every rewrite the targets can do without,
    /// `disable` and `enable` change single ones. Naming a rewrite the targets need in `disable` is an error (DDB-229).
    /// </summary>
    /// <summary>The policy of one model: the project's settings, then the model's own, for the targets the model is built for. Errors (DDB-229) go to <paramref name="diags"/> when given.</summary>
    public static RewritePolicy For(ProjectConfig config, ModelDefinition def, List<Diagnostic>? diags = null) =>
        Resolve(config.Rewrites, def.Rewrites, (def.Targets ?? config.DefaultConnections).Select(c => config.EngineOf(c) ?? c).Distinct().ToList(), def.Name, diags);      // rewrites are per engine; a model names connections

    public static RewritePolicy Resolve(RewriteSettings? project, RewriteSettings? model, IReadOnlyList<string> targets, string modelName, List<Diagnostic>? diags)
    {
        var off = new HashSet<string>(StringComparer.Ordinal);
        void Apply(RewriteSettings? s, string where)
        {
            if (s == null) return;
            if (s.Fidelity != null) { off.Clear(); if (s.Fidelity == RewriteSettings.Native) foreach (var r in All.Where(r => !RequiredFor(r, targets) && r.Targets.Any(targets.Contains))) off.Add(r.Name); }
            foreach (var name in s.Enable) off.Remove(name);
            foreach (var name in s.Disable)
            {
                var rule = Find(name);
                if (rule == null || !rule.Targets.Any(targets.Contains)) continue;      // not a rewrite (reported where it was read), or one these targets never get
                if (RequiredFor(rule, targets))
                {
                    diags?.Add(new Diagnostic(DiagnosticCatalog.RewriteNotOptional, new(where, s.Line, 0),
                        $"`{name}` cannot be turned off for {modelName}: it is required on {string.Join(", ", rule.RequiredOn.Where(targets.Contains))} ({rule.Native})."));
                    continue;
                }
                off.Add(name);
            }
        }
        Apply(project, ProductInfo.ConfigFile);
        Apply(model, modelName);
        return off.Count == 0 ? RewritePolicy.Exact : new RewritePolicy(off);
    }
}

/// <summary>The `rewrites:` section of dbdatabuild.yml or of a model definition.</summary>
/// <param name="Fidelity">`exact` or `native`; null when the section does not say (the level below applies).</param>
public sealed record RewriteSettings(string? Fidelity, IReadOnlyList<string> Disable, IReadOnlyList<string> Enable, int Line = 0)
{
    public const string Exact = "exact";
    public const string Native = "native";
}
