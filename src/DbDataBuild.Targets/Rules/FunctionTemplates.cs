namespace DbDataBuild.Targets.Rules;

/// <summary>
/// A function an engine lacks, written out from functions it has (docs/research/function-matrix.md). The table is the rule: <see cref="TargetRules"/> replaces a call of <see cref="Function"/> on one of the
/// <see cref="Targets"/> by <see cref="Template"/>, a DuckDB-dialect expression in which `__0`, `__1` stand for the arguments. Every row is run three ways (CLAUDE.md): the text each target gets and a differential
/// run of the rewritten query against the original on DuckDB (FunctionRulesTests), and the engines' rows against DuckDB's (FunctionRuleProbes, whose cases come from <see cref="Probes"/>).
/// </summary>
/// <param name="Rule">The rewrite's name in `rewrites:` and in rendered headers (a rule groups related functions).</param>
/// <param name="Function">The DuckDB name, lower case.</param>
/// <param name="Targets">The engines that get the template (an engine that has the function is not listed).</param>
/// <param name="Template">The DuckDB-dialect expression. It is written after the other rules have run: a function it calls is not rewritten again, so it names only what the engine has.</param>
/// <param name="Probes">Calls to compare with DuckDB on the engines and on DuckDB itself, over the probe table (`d` is a DOUBLE, `i` an INTEGER, `s` text, `dt` a DATE, `ts` a TIMESTAMP) or with literals.</param>
/// <param name="Node">For a function that polyglot reads as a node of its own (`atan2`, `ends_with`, `last_day`): the node's name, and in <see cref="Fields"/> the names of its arguments in order.</param>
/// <param name="DoubleArguments">The arguments are made DOUBLE first (a whole-number constant stays an int in T-SQL, and POWER(64, 1.0 / 3.0) is an int).</param>
/// <param name="Unit">For date_part, date_trunc and date_diff: the unit (the first argument, a literal) this row is for; the holes then count the unit as `__0`, so the date is `__1` (and `__2` for the second date).</param>
/// <param name="Tolerance">What the rewrite does not reproduce to the last bit (for the matrix note and the rewrite catalog); null when it is exact.</param>
public sealed record FunctionTemplate(string Rule, string Function, IReadOnlyList<string> Targets, string Template, IReadOnlyList<string> Probes, string? Node = null, IReadOnlyList<string>? Fields = null, string? Tolerance = null, bool DoubleArguments = false, string? Unit = null)
{
    public int Arity => Fields?.Count ?? System.Text.RegularExpressions.Regex.Matches(Template, "__(\\d)").Select(m => m.Groups[1].Value[0] - '0').DefaultIfEmpty(-1).Max() + 1;
}

public static class FunctionTemplates
{
    public const string HyperbolicFunctions = "hyperbolic-functions";
    public const string CubeRoot = "cube-root";
    public const string ArcTangent2 = "atan2-atn2";
    public const string LogBaseTwo = "log2-ratio";
    public const string DayAndMonthNames = "day-month-names";
    public const string EndsWith = "ends-with";
    public const string StartsWithAlias = "prefix-alias";
    public const string FloatClassification = "float-classification";
    public const string LastDayOfMonth = "last-day";
    public const string GcdAndLcm = "gcd-lcm-names";
    public const string DatePartUnits = "date-part-units";
    public const string DateTruncUnits = "date-trunc-units";
    public const string DateDiffUnits = "date-diff-units";

    private static readonly string[] SqlServer = ["sqlserver", "fabric"];
    private static readonly string[] Postgres = ["postgres"];
    private static readonly string[] Both = ["sqlserver", "fabric", "postgres"];

    private const string SmallArgument = "abs(__0) < 0.001";
    private const string NearlyExact = "agrees to about 13 significant digits (an expression of EXP and LOG, where DuckDB calls the C library); outside the range of EXP (an argument beyond about 709) the engine raises an overflow where DuckDB gives Infinity";

    /// <summary>The weekday number from a known Sunday (1900-01-07), which does not depend on @@DATEFIRST or lc_time, as the rule weekday-datefirst does it.</summary>
    private const string Weekday = "((date_diff('day', DATE '1900-01-07', CAST(__0 AS DATE)) % 7) + 7) % 7";

    // ---- the units of date_part, date_trunc and date_diff (the date is `__1`, a second date `__2`; the unit itself, `__0`, is the literal that selects the row) ----
    private const string IsoDow = "(((date_diff('day', DATE '1900-01-01', CAST(__1 AS DATE)) % 7) + 7) % 7 + 1)";
    private const string Thursday = "CAST(CAST(__1 AS DATE) + INTERVAL (4 - " + IsoDow + ") DAY AS DATE)";                       // the Thursday of the date's ISO week: its year is the ISO year
    private const string AsTimestamp = "CAST(__1 AS TIMESTAMP)";                                                                 // a DATE takes no hour in DATEPART or EXTRACT: a timestamp does (DuckDB gives 0)

    private static FunctionTemplate Part(string unit, string[] targets, string template, params string[] probes) =>
        new(DatePartUnits, "date_part", targets, template, probes.Select(p => $"date_part('{unit}', {p})").ToArray(), Unit: unit);

    private static FunctionTemplate Trunc(string unit, string[] targets, string template, params string[] probes) =>
        new(DateTruncUnits, "date_trunc", targets, template, probes.Select(p => $"date_trunc('{unit}', {p})").ToArray(), Unit: unit);

    private static FunctionTemplate Diff(string unit, string[] targets, string template, params string[] pairs) =>
        new(DateDiffUnits, "date_diff", targets, template, pairs.Select(p => $"date_diff('{unit}', {p})").ToArray(), Unit: unit);

    private static readonly string[] Dates = ["dt", "ts", "DATE '2021-01-03'", "DATE '2020-12-31'", "DATE '2024-12-30'", "TIMESTAMP '2000-12-31 23:59:59.999999'", "DATE '2001-01-01'", "DATE '1999-12-31'"];
    private static readonly string[] Instants = ["ts", "dt", "TIMESTAMP '2024-01-15 10:30:45.123456'", "TIMESTAMP '1969-12-31 23:59:59.5'", "TIMESTAMP '2100-06-30 01:02:03.000001'", "DATE '2024-02-29'"];

    private static string Names(string selector, int first, params string[] names) => $"CASE {selector} " + string.Join(" ", names.Select((n, i) => $"WHEN {i + first} THEN '{n}'")) + " END";

    public static readonly IReadOnlyList<FunctionTemplate> All =
    [
        // ---- hyperbolic functions (SQL Server has none; PostgreSQL has them all) ----
        new(HyperbolicFunctions, "cosh", SqlServer, "(exp(__0) + exp(-__0)) / 2.0", ["cosh(CASE WHEN abs(d) < 600 THEN d ELSE 1.5 END)", "cosh(CAST(i % 50 AS DOUBLE) / 10)", "cosh(CAST(0 AS DOUBLE))", "cosh(CAST(-3.5 AS DOUBLE))", "cosh(CAST(700 AS DOUBLE))"], Tolerance: NearlyExact, DoubleArguments: true),
        new(HyperbolicFunctions, "sinh", SqlServer, $"CASE WHEN {SmallArgument} THEN __0 + __0 * __0 * __0 / 6.0 + __0 * __0 * __0 * __0 * __0 / 120.0 ELSE (exp(__0) - exp(-__0)) / 2.0 END",
            ["sinh(CASE WHEN abs(d) < 600 THEN d ELSE 1.5 END)", "sinh(CAST(i % 50 AS DOUBLE) / 10)", "sinh(CAST(0.0001 AS DOUBLE))", "sinh(CAST(-0.0005 AS DOUBLE))", "sinh(CAST(0.001 AS DOUBLE))", "sinh(CAST(-700 AS DOUBLE))"], Tolerance: NearlyExact, DoubleArguments: true),
        new(HyperbolicFunctions, "tanh", SqlServer,
            $"CASE WHEN abs(__0) > 20 THEN sign(__0) WHEN {SmallArgument} THEN __0 - __0 * __0 * __0 / 3.0 + 2.0 * __0 * __0 * __0 * __0 * __0 / 15.0 ELSE (exp(2.0 * __0) - 1.0) / (exp(2.0 * __0) + 1.0) END",
            ["tanh(d)", "tanh(CAST(i % 50 AS DOUBLE) / 10)", "tanh(CAST(0.0001 AS DOUBLE))", "tanh(CAST(-20.5 AS DOUBLE))", "tanh(CAST(1000 AS DOUBLE))", "tanh(CAST(-1000 AS DOUBLE))", "tanh(CAST(19.9 AS DOUBLE))"], Tolerance: "agrees to about 13 significant digits", DoubleArguments: true),
        new(HyperbolicFunctions, "asinh", SqlServer,
            $"CASE WHEN {SmallArgument} THEN __0 - __0 * __0 * __0 / 6.0 + 3.0 * __0 * __0 * __0 * __0 * __0 / 40.0 WHEN abs(__0) > 1e8 THEN sign(__0) * (ln(abs(__0)) + 0.6931471805599453) ELSE sign(__0) * ln(abs(__0) + sqrt(__0 * __0 + 1.0)) END",
            ["asinh(d)", "asinh(CAST(i AS DOUBLE))", "asinh(CAST(0.0001 AS DOUBLE))", "asinh(CAST(-3.5 AS DOUBLE))", "asinh(CAST(1e10 AS DOUBLE))", "asinh(CAST(-1e200 AS DOUBLE))"], Tolerance: "agrees to about 12 significant digits", DoubleArguments: true),
        new(HyperbolicFunctions, "acosh", SqlServer, "CASE WHEN __0 >= 1e8 THEN ln(__0) + 0.6931471805599453 ELSE ln(__0 + sqrt((__0 - 1.0) * (__0 + 1.0))) END",
            ["acosh(abs(d) + 1)", "acosh(CAST(abs(i) AS DOUBLE) + 1)", "acosh(CAST(1 AS DOUBLE))", "acosh(CAST(1.0000001 AS DOUBLE))", "acosh(CAST(1e10 AS DOUBLE))", "acosh(CAST(1e300 AS DOUBLE))"], Tolerance: "agrees to about 11 significant digits near 1; below 1 the engine raises where DuckDB gives NaN", DoubleArguments: true),
        new(HyperbolicFunctions, "atanh", SqlServer, $"CASE WHEN {SmallArgument} THEN __0 + __0 * __0 * __0 / 3.0 + __0 * __0 * __0 * __0 * __0 / 5.0 ELSE 0.5 * ln((1.0 + __0) / (1.0 - __0)) END",
            ["atanh(CASE WHEN abs(d) < 20 THEN d / 20 ELSE 0.5 END)", "atanh(CAST(0.5 AS DOUBLE))", "atanh(CAST(0.0001 AS DOUBLE))", "atanh(CAST(-0.999 AS DOUBLE))", "atanh(CAST(0 AS DOUBLE))"], Tolerance: "agrees to about 12 significant digits; at 1 and beyond DuckDB gives Infinity and NaN, the engine NULL or an error", DoubleArguments: true),

        // ---- cube root: SQL Server has no CBRT; one Newton step after POWER makes perfect cubes come out whole ----
        new(CubeRoot, "cbrt", SqlServer,
            "CASE WHEN __0 = 0 THEN 0.0 ELSE sign(__0) * (power(abs(__0), 1.0 / 3.0) - (power(abs(__0), 1.0 / 3.0) * power(abs(__0), 1.0 / 3.0) * power(abs(__0), 1.0 / 3.0) - abs(__0)) / (3.0 * power(abs(__0), 1.0 / 3.0) * power(abs(__0), 1.0 / 3.0))) END",
            ["cbrt(d)", "cbrt(CAST(i AS DOUBLE))", "cbrt(CAST(27 AS DOUBLE))", "cbrt(CAST(-64 AS DOUBLE))", "cbrt(CAST(0.001 AS DOUBLE))", "cbrt(CAST(1e300 AS DOUBLE))", "cbrt(CAST(1e-300 AS DOUBLE))", "cbrt(CAST(0 AS DOUBLE))"],
            Tolerance: "agrees to the last digit or two (libm's cbrt is not correctly rounded either)", DoubleArguments: true),

        // ---- atan2 is ATN2 on SQL Server (a marker; Finish writes the name) ----
        new(ArcTangent2, "atan2", SqlServer, "ddb_atn2(__0, __1)", ["atan2(d, CAST(i AS DOUBLE))", "atan2(CAST(1 AS DOUBLE), CAST(-1 AS DOUBLE))", "atan2(CAST(0 AS DOUBLE), CAST(-1 AS DOUBLE))", "atan2(CAST(-2 AS DOUBLE), CAST(0.5 AS DOUBLE))"], Node: "atan2", Fields: ["this", "expression"], DoubleArguments: true),

        // ---- log2 (neither engine has it) ----
        new(LogBaseTwo, "log2", Both, "ln(__0) / ln(2.0)", ["log2(abs(d) + 1)", "log2(CAST(abs(i) AS DOUBLE) + 1)", "log2(CAST(8 AS DOUBLE))", "log2(CAST(0.25 AS DOUBLE))", "log2(CAST(1 AS DOUBLE))"], Tolerance: "agrees to the last digit or two (ln(8) / ln(2) is not exactly 3 in every case)", DoubleArguments: true),

        // ---- day and month names, in English whatever the session language (DATENAME and to_char follow it) ----
        new(DayAndMonthNames, "dayname", Both, Names(Weekday, 0, "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"), ["dayname(dt)", "dayname(ts)", "dayname(DATE '2024-02-29')", "dayname(DATE '1900-01-06')", "dayname(DATE '0001-01-01')"]),
        new(DayAndMonthNames, "monthname", Both, Names("date_part('month', __0)", 1, "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"), ["monthname(dt)", "monthname(ts)", "monthname(DATE '2024-12-31')"]),

        // ---- text ----
        new(EndsWith, "ends_with", Both, "right(__0, length(__1 || 'x') - 1) = __1", ["ends_with(s, 'c')", "ends_with(s, '')", "ends_with(s, s)", "ends_with(s, 'bc')"], Node: "ends_with", Fields: ["this", "expression"]),
        new(EndsWith, "suffix", Both, "right(__0, length(__1 || 'x') - 1) = __1", ["suffix(s, 'c')", "suffix(s, 'é')"]),
        new(StartsWithAlias, "prefix", Both, "starts_with(__0, __1)", ["prefix(s, 'a')", "prefix(s, '')"]),

        // ---- date parts (DuckDB's definitions: millisecond and microsecond count from the start of the minute, week and isoyear are ISO, epoch is seconds with a fraction) ----
        Part("week", SqlServer, "ddb_isoweek(CAST(__1 AS DATE))", Dates),
        Part("isoyear", SqlServer, $"date_part('year', {Thursday})", Dates),
        Part("yearweek", SqlServer, $"date_part('year', {Thursday}) * 100 + ddb_isoweek(CAST(__1 AS DATE))", Dates),
        Part("yearweek", Postgres, "date_part('isoyear', __1) * 100 + date_part('week', __1)", Dates),
        Part("decade", SqlServer, "CAST(floor(date_part('year', __1) / 10.0) AS BIGINT)", Dates),
        Part("century", SqlServer, "CAST(floor((date_part('year', __1) - 1) / 100.0) AS BIGINT) + 1", Dates),
        Part("millennium", SqlServer, "CAST(floor((date_part('year', __1) - 1) / 1000.0) AS BIGINT) + 1", Dates),
        Part("era", Both, "CASE WHEN __1 IS NULL THEN NULL ELSE 1 END", "dt", "ts"),
        Part("hour", Both, $"date_part('hour', {AsTimestamp})", Instants),
        Part("minute", Both, $"date_part('minute', {AsTimestamp})", Instants),
        Part("second", SqlServer, $"date_part('second', {AsTimestamp})", Instants),
        Part("second", Postgres, $"CAST(trunc(date_part('second', {AsTimestamp})) AS BIGINT)", Instants),
        Part("millisecond", SqlServer, $"date_part('second', {AsTimestamp}) * 1000 + ddb_datepart_millisecond({AsTimestamp})", Instants),
        Part("millisecond", Postgres, $"CAST(floor(date_part('millisecond', {AsTimestamp})) AS BIGINT)", Instants),
        Part("microsecond", SqlServer, $"date_part('second', {AsTimestamp}) * 1000000 + ddb_datepart_microsecond({AsTimestamp})", Instants),
        Part("microsecond", Postgres, $"date_part('microsecond', {AsTimestamp})", Instants),
        Part("epoch", SqlServer, $"ddb_epoch({AsTimestamp})", Instants),
        Part("epoch", Postgres, $"CAST(ddb_pg_epoch({AsTimestamp}) AS DOUBLE)", Instants),
        Part("julian", SqlServer, $"date_diff('day', DATE '1970-01-01', CAST(__1 AS DATE)) + 2440588 + (((date_part('hour', {AsTimestamp}) * 60 + date_part('minute', {AsTimestamp})) * 60 + date_part('second', {AsTimestamp})) * 1000000.0 + ddb_datepart_microsecond({AsTimestamp})) / 86400000000.0", Instants),
        Part("julian", Postgres, $"CAST(ddb_pg_julian({AsTimestamp}) AS DOUBLE)", Instants),

        // ---- date_trunc ----
        Trunc("week", SqlServer, "CAST(CAST(CAST(__1 AS DATE) + INTERVAL (1 - " + IsoDow + ") DAY AS DATE) AS TIMESTAMP)", Dates),
        Trunc("decade", SqlServer, "CAST(make_date(CAST(floor(date_part('year', __1) / 10.0) AS INTEGER) * 10, 1, 1) AS TIMESTAMP)", Dates),
        Trunc("century", Both, "CAST(make_date(CAST(floor(date_part('year', __1) / 100.0) AS INTEGER) * 100, 1, 1) AS TIMESTAMP)", Dates),
        Trunc("millennium", Both, "CAST(make_date(CAST(floor(date_part('year', __1) / 1000.0) AS INTEGER) * 1000, 1, 1) AS TIMESTAMP)", Dates),
        Trunc("hour", SqlServer, "date_trunc('hour', " + AsTimestamp + ")", Instants),
        Trunc("minute", SqlServer, "date_trunc('minute', " + AsTimestamp + ")", Instants),
        Trunc("second", SqlServer, "date_trunc('second', " + AsTimestamp + ")", Instants),
        Trunc("millisecond", SqlServer, "date_trunc('millisecond', " + AsTimestamp + ")", Instants),
        Trunc("microsecond", SqlServer, "date_trunc('microsecond', " + AsTimestamp + ")", Instants),

        // ---- date_diff (the boundaries crossed, as DuckDB counts them) ----
        Diff("decade", Both, "CAST(floor(date_part('year', __2) / 10.0) - floor(date_part('year', __1) / 10.0) AS BIGINT)", "DATE '2000-01-01', DATE '2024-06-01'", "dt, DATE '2031-03-17'", "DATE '2009-12-31', DATE '2010-01-01'", "DATE '2024-06-01', DATE '2000-01-01'", "ts, TIMESTAMP '2031-03-17 12:34:56.789'"),
        Diff("century", Both, "CAST(floor(date_part('year', __2) / 100.0) - floor(date_part('year', __1) / 100.0) AS BIGINT)", "DATE '1900-01-01', DATE '2024-06-01'", "DATE '1999-12-31', DATE '2000-01-01'", "DATE '2000-01-01', DATE '1999-12-31'", "dt, DATE '2231-03-17'"),
        Diff("millennium", Both, "CAST(floor(date_part('year', __2) / 1000.0) - floor(date_part('year', __1) / 1000.0) AS BIGINT)", "DATE '1000-01-01', DATE '2024-06-01'", "DATE '1999-12-31', DATE '2000-01-01'", "dt, DATE '3031-03-17'"),
        Diff("hour", SqlServer, "ddb_datediff_big_hour(CAST(__1 AS TIMESTAMP), CAST(__2 AS TIMESTAMP))", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts", "dt, DATE '2031-03-17'"),
        Diff("minute", SqlServer, "ddb_datediff_big_minute(CAST(__1 AS TIMESTAMP), CAST(__2 AS TIMESTAMP))", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts"),
        Diff("second", SqlServer, "ddb_datediff_big_second(CAST(__1 AS TIMESTAMP), CAST(__2 AS TIMESTAMP))", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts"),
        Diff("millisecond", SqlServer, "ddb_datediff_big_millisecond(CAST(__1 AS TIMESTAMP), CAST(__2 AS TIMESTAMP))", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts"),
        Diff("microsecond", SqlServer, "ddb_datediff_big_microsecond(CAST(__1 AS TIMESTAMP), CAST(__2 AS TIMESTAMP))", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts"),

        // PostgreSQL rounds a difference of times where DuckDB counts the boundaries crossed (1999-12-31 23:59:59.999999 to 2000-01-01 is one second in DuckDB, 0.000001 rounded up is also one, but a whole second before is two): the epochs cut to the unit
        Diff("hour", Postgres, "CAST(floor(ddb_pg_epoch_exact(CAST(__2 AS TIMESTAMP)) / 3600.0) - floor(ddb_pg_epoch_exact(CAST(__1 AS TIMESTAMP)) / 3600.0) AS BIGINT)", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts", "TIMESTAMP '1999-12-31 23:59:59.999999', TIMESTAMP '2000-01-01 00:00:00'", "dt, DATE '2031-03-17'"),
        Diff("minute", Postgres, "CAST(floor(ddb_pg_epoch_exact(CAST(__2 AS TIMESTAMP)) / 60.0) - floor(ddb_pg_epoch_exact(CAST(__1 AS TIMESTAMP)) / 60.0) AS BIGINT)", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts", "TIMESTAMP '1999-12-31 23:59:59.999999', TIMESTAMP '2000-01-01 00:00:00'", "dt, DATE '2031-03-17'"),
        Diff("second", Postgres, "CAST(floor(ddb_pg_epoch_exact(CAST(__2 AS TIMESTAMP)) * 1) - floor(ddb_pg_epoch_exact(CAST(__1 AS TIMESTAMP)) * 1) AS BIGINT)", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts", "TIMESTAMP '1999-12-31 23:59:59.999999', TIMESTAMP '2000-01-01 00:00:00'", "dt, DATE '2031-03-17'"),
        Diff("millisecond", Postgres, "CAST(floor(ddb_pg_epoch_exact(CAST(__2 AS TIMESTAMP)) * 1000) - floor(ddb_pg_epoch_exact(CAST(__1 AS TIMESTAMP)) * 1000) AS BIGINT)", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts", "TIMESTAMP '1999-12-31 23:59:59.999999', TIMESTAMP '2000-01-01 00:00:00'", "dt, DATE '2031-03-17'"),
        Diff("microsecond", Postgres, "CAST(floor(ddb_pg_epoch_exact(CAST(__2 AS TIMESTAMP)) * 1000000) - floor(ddb_pg_epoch_exact(CAST(__1 AS TIMESTAMP)) * 1000000) AS BIGINT)", "ts, TIMESTAMP '2031-03-17 12:34:56.789'", "TIMESTAMP '2031-03-17 12:34:56.789', ts", "TIMESTAMP '1999-12-31 23:59:59.999999', TIMESTAMP '2000-01-01 00:00:00'", "dt, DATE '2031-03-17'"),

        // ---- PostgreSQL only ----
        new(FloatClassification, "isnan", Postgres, "(__0 = CAST('NaN' AS DOUBLE))", ["isnan(d)", "isnan(CAST('NaN' AS DOUBLE))"]),
        new(FloatClassification, "isinf", Postgres, "(__0 = CAST('Infinity' AS DOUBLE) OR __0 = CAST('-Infinity' AS DOUBLE))", ["isinf(d)", "isinf(CAST('Infinity' AS DOUBLE))", "isinf(CAST('-Infinity' AS DOUBLE))"]),
        new(LastDayOfMonth, "last_day", Postgres, "CAST(date_trunc('month', __0) + INTERVAL 1 MONTH - INTERVAL 1 DAY AS DATE)", ["last_day(dt)", "last_day(ts)", "last_day(DATE '2024-02-10')", "last_day(DATE '2023-02-10')", "last_day(DATE '2024-12-31')"], Node: "last_day", Fields: ["this"]),
        new(GcdAndLcm, "greatest_common_divisor", Postgres, "gcd(__0, __1)", ["greatest_common_divisor(i, j)", "greatest_common_divisor(abs(i) % 100, 12)"]),
        new(GcdAndLcm, "least_common_multiple", Postgres, "lcm(__0, __1)", ["least_common_multiple(abs(i) % 100, abs(j) + 1)"]),
    ];

    /// <summary>The names of the rules that have a template for <paramref name="target"/>.</summary>
    public static IEnumerable<string> RulesFor(string target) => All.Where(t => t.Targets.Contains(target)).Select(t => t.Rule).Distinct();

    public static IEnumerable<string> RuleNames => All.Select(t => t.Rule).Distinct();
}
