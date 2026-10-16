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
        new("float-width", RewriteLayer.Target, Both, Both, "FLOAT, FLOAT4, REAL and FLOAT(n) up to 24 are written REAL (32 bits), FLOAT8 and FLOAT(n) from 25 are DOUBLE (64 bits), in a cast, a column definition and a VALUES cell, as in DuckDB", "the transpile writes FLOAT as T-SQL's FLOAT, which is 8 bytes, and FLOAT(n), FLOAT4 and FLOAT8 as written (REAL(24) and FLOAT8 are not SQL Server types)"),
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
        new("interval-dateadd", RewriteLayer.Target, SqlServer, SqlServer, "date + INTERVAL of several units (or an interval written as text, in front of the date, or a sum of intervals) is one DATEADD per part: the months, then the days, then the time, as DuckDB adds them", "the transpile reads '1 month 2 days 03:04:05' as one unit and drops the rest (2024-01-15 + that is 2024-02-15 where DuckDB gives 2024-02-17 03:04:05); SQL Server has no interval type"),
        new("values-chunks", RewriteLayer.Target, SqlServer, SqlServer, "a VALUES list of more than 1000 rows is UNION ALL of lists of at most 1000 rows", "SQL Server refuses more than 1000 row value expressions in one VALUES constructor (error 10738)"),
        new("bit-operators", RewriteLayer.Target, SqlServer, SqlServer, "xor(a, b) is a ^ b, and a whole-number literal beyond the INT range next to & or | is a BIGINT", "the transpile writes XOR(a, b), which T-SQL does not have (PostgreSQL's # is written without a rule); T-SQL reads 4294967295 as a numeric, which & and | refuse next to a bigint"),
        new("shift-left", RewriteLayer.Both, Both, SqlServer, "a << n is a times 2^n in exact arithmetic, cast back to the type: a result that does not fit raises, as in DuckDB", "T-SQL has no << (SQL Server 2022's LEFT_SHIFT wraps silently: 1 << 31 is -2147483648); PostgreSQL's << wraps silently too"),
        new("shift-right", RewriteLayer.Both, Both, SqlServer, "a >> n keeps the sign (the floor of a / 2^n), and is 0 for a count of 0 below or the width and above, as in DuckDB", "T-SQL has no >> (RIGHT_SHIFT fills with zeros: RIGHT_SHIFT(-8, 1) is 2147483644); PostgreSQL's >> agrees for counts inside the width and takes the count modulo it outside"),
        new("factorial-table", RewriteLayer.Target, Both, Both, "factorial(n) and n! are the exact value for 0 to 33 and an overflow error otherwise, as in DuckDB (a HUGEINT, written as DECIMAL(38, 0))", "SQL Server has no factorial; PostgreSQL's factorial gives a numeric for 34 and beyond where DuckDB raises"),
        new("hyperbolic-functions", RewriteLayer.Target, SqlServer, SqlServer, "cosh, sinh, tanh, acosh, asinh and atanh are written with EXP, LN and SQRT (a series near 0 and a limit for large arguments keep the digits); they agree with DuckDB to about 12 significant digits", "SQL Server has none of them ('COSH' is not a recognized built-in function name); PostgreSQL has them all"),
        new("cube-root", RewriteLayer.Target, SqlServer, SqlServer, "cbrt(x) is the signed cube root: POWER(|x|, 1/3) with one Newton step, so a perfect cube comes out whole", "SQL Server has no CBRT; PostgreSQL has it"),
        new("atan2-atn2", RewriteLayer.Target, SqlServer, SqlServer, "atan2(y, x) is ATN2(y, x)", "SQL Server calls it ATN2 (ATAN2 is not a recognized built-in function name)"),
        new("log2-ratio", RewriteLayer.Target, Both, Both, "log2(x) is ln(x) / ln(2)", "neither engine has log2"),
        new("day-month-names", RewriteLayer.Target, Both, Both, "dayname(d) and monthname(d) are the English names from the weekday and month numbers (the weekday counted from a fixed Sunday, as in weekday-datefirst)", "SQL Server has DATENAME and PostgreSQL to_char, which follow the session's language"),
        new("ends-with", RewriteLayer.Target, Both, Both, "ends_with(s, p) and suffix(s, p) are right(s, length(p)) = p", "neither engine has ends_with"),
        new("prefix-alias", RewriteLayer.Target, Both, Both, "prefix(s, p) is starts_with(s, p)", "neither engine has prefix"),
        new("float-classification", RewriteLayer.Target, Postgres, Postgres, "isnan(x) and isinf(x) compare with NaN and the infinities", "PostgreSQL has no isnan or isinf"),
        new("last-day", RewriteLayer.Target, Postgres, Postgres, "last_day(d) is the month's first day plus one month minus one day, as a DATE", "PostgreSQL has no last_day (SQL Server's EOMONTH is written by the transpile)"),
        new("gcd-lcm-names", RewriteLayer.Target, Postgres, Postgres, "greatest_common_divisor and least_common_multiple are PostgreSQL's gcd and lcm", "PostgreSQL knows them only by the short names"),
        new("trunc-digits", RewriteLayer.Both, Postgres, Postgres, "trunc(double, n) is trunc(x * 10^n) / 10^n in double arithmetic, as DuckDB does it (a FLOAT is cut as its exact value and is a FLOAT again)", "PostgreSQL has trunc(numeric, integer) only: trunc(double precision, integer) does not exist"),
        new("date-part-units", RewriteLayer.Target, Both, Both, "date_part(unit, x) with DuckDB's meaning for week and isoyear (ISO), yearweek, decade, century, millennium, era, julian, epoch (seconds with a fraction), millisecond and microsecond (counted from the start of the minute), second (whole), and a DATE read as a timestamp for the time units", "SQL Server's week is the US week and it has no isoyear, decade, century, era or epoch; DATEPART refuses an hour of a DATE; PostgreSQL has no yearweek, era-less julian forms or epoch(x) through the transpile and gives a fractional second"),
        new("date-trunc-units", RewriteLayer.Target, Both, Both, "date_trunc for week (Monday), decade, century and millennium (the year rounded down: 2024 is 2000-01-01), and the time units of a DATE", "SQL Server's DATETRUNC starts the week on Sunday and has no decade, century or millennium; PostgreSQL's century and millennium start in year 1"),
        new("date-diff-units", RewriteLayer.Target, Both, Both, "date_diff for decade, century and millennium (the difference of the year divided and rounded down), and the time units through DATEDIFF_BIG", "SQL Server's DATEDIFF overflows an int for milliseconds over 24 days and has no decade, century or millennium; PostgreSQL's AGE() gives an interval where DuckDB gives a number"),
        new("age-parts", RewriteLayer.Target, SqlServer, SqlServer, "date_part(unit, age(a, b)) for year, month, day, hour, minute and second, and normalized_interval(age(...)) (the number of microseconds with the sign, a month counting 30 days) are written out in T-SQL with DuckDB's borrowing rule; age as a value, in arithmetic or with other parts is refused (DDB-301)", "SQL Server has no AGE ('AGE' is not a recognized built-in function name) and no interval type"),
        new("normalized-interval", RewriteLayer.Target, Postgres, Postgres, "normalized_interval(x) is x: PostgreSQL's own interval comparison is the same ordering (a month is 30 days)", "PostgreSQL has no normalized_interval (function normalized_interval(interval) does not exist)"),
        new("age-second", RewriteLayer.Target, Postgres, None, "date_part('second', age(a, b)) is the whole second: trunc of the extract", "PostgreSQL's extract of the second of an age is fractional (-45.5) where DuckDB gives the whole second (-45)"),
        new("covariance-exact", RewriteLayer.Target, Postgres, None, "covar_pop, covar_samp and regr_sxy are sum(xy) - sum(x) * sum(y) / n in NUMERIC, exact to the digits of the inputs", "PostgreSQL accumulates in double precision with an unstable update: next to a 1e10 outlier two independent columns have a covariance of -334 where DuckDB gives 0"),
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
