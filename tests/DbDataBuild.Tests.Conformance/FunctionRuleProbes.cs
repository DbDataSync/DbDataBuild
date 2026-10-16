using System.Globalization;
using System.Text;
using DbDataBuild.Targets.Rules;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The real-engine leg of the function rules (docs/research/function-matrix.md, entry 177 onward): each case is a DuckDB query over the probe table (and literal edge values) that the tool lowers, rewrites for the
/// engine and sends; the rows must be DuckDB's. A case in <see cref="Engines"/> only runs on the engines it names. A case that DuckDB itself rejects (a domain error) must be rejected by the engine too: the
/// engines' messages differ, the fact that nothing wrong is returned does not.
/// </summary>
public class FunctionRuleProbes
{
    /// <param name="Id">Names the case in a failure.</param>
    /// <param name="Sql">A DuckDB query over the probe table.</param>
    /// <param name="Engines">The engines the case runs on; null for both.</param>
    private sealed record Case(string Id, string Sql, string[]? Engines = null);

    private static Case V(string id, string expr, string? from = null, string[]? engines = null) => new(id, $"SELECT id, {expr} AS v FROM {from ?? "probe"}", engines);
    private static Case S(string id, string expr, string[]? engines = null) => new(id, $"SELECT {expr} AS v", engines);

    private static readonly string[] SqlServerOnly = ["sqlserver"];
    private static readonly string[] PostgresOnly = ["postgres"];

    private static Case[] Cases => [.. Explicit, .. FromTable];

    /// <summary>One case per probe of every function written out from others (FunctionTemplates), on the engines that get the template.</summary>
    private static Case[] FromTable => FunctionTemplates.All
        .SelectMany(t => t.Probes.Select(p => V($"{t.Rule}:{p}", p, engines: t.Targets.Where(x => x != "fabric").ToArray())))
        .ToArray();

    private static readonly Case[] Explicit =
    [
        // log(x) is base 10; log(b, x) has a base
        V("log_x", "log(abs(d) + 1)"), V("log_two_args", "log(2, abs(d) + 1)"), V("log_ten_args", "log(10, abs(d) + 1)"), V("log_int_col", "log(abs(i) + 1)"), V("log_decimal", "log(abs(n) + 1)"),
        S("log_one", "log(1.0)"), S("log_hundred", "log(CAST(100 AS DOUBLE))"), S("log_small", "log(CAST(0.001 AS DOUBLE))"), S("log_null", "log(CAST(NULL AS DOUBLE))"),
        S("log_zero_errors", "log(CAST(0 AS DOUBLE))"), S("log_negative_errors", "log(CAST(-1 AS DOUBLE))"),
        // an interval of several units added to or taken from a date or timestamp: months first (cut back to the end of a shorter month), then days, then the time
        V("iv_multi_ts", "ts + INTERVAL '1 month 2 days 03:04:05'"), V("iv_multi_ts_sub", "ts - INTERVAL '1 month 2 days 03:04:05'"), V("iv_multi_dt", "dt + INTERVAL '1 month 2 days 03:04:05'"),
        V("iv_multi_dt_sub", "dt - INTERVAL '1 month 2 days 03:04:05'"), V("iv_year_months", "dt + INTERVAL '1 year 2 months'"), V("iv_negative", "ts + INTERVAL '-1 month -2 days'"), V("iv_mixed_signs", "ts + INTERVAL '1 month -2 days'"),
        V("iv_negative_time", "ts + INTERVAL '1 month -03:04:05'"), V("iv_minutes", "ts + INTERVAL '90 minutes'"), V("iv_date_hours", "dt + INTERVAL 3 HOUR"), V("iv_date_minutes", "dt - INTERVAL 90 MINUTE"),
        V("iv_fraction", "ts + INTERVAL '1.5 seconds'"), V("iv_micro", "ts + INTERVAL '0.000001 seconds'"), V("iv_micros_multi", "ts + INTERVAL '1 day 0.000123 seconds'"), V("iv_week", "dt + INTERVAL 1 WEEK"), V("iv_week_sub", "ts - INTERVAL 2 WEEK"),
        V("iv_quarter", "dt + INTERVAL 1 QUARTER"), V("iv_decade", "dt + INTERVAL 1 DECADE"), V("iv_century", "dt + INTERVAL 1 CENTURY"), V("iv_millisecond", "ts + INTERVAL 500 MILLISECOND"), V("iv_microsecond", "ts + INTERVAL 7 MICROSECOND"),
        V("iv_years_text", "ts + INTERVAL '2 years 1 day'"), V("iv_interval_first", "INTERVAL 1 DAY + dt"), V("iv_interval_first_multi", "INTERVAL '1 month 2 days' + ts"), V("iv_sum", "ts + (INTERVAL 1 MONTH + INTERVAL 2 DAY)"),
        V("iv_difference", "ts + (INTERVAL '1 month 2 days' - INTERVAL 1 DAY)"), V("iv_chain", "ts + INTERVAL 1 MONTH + INTERVAL 1 DAY"), V("iv_neg_interval", "ts + (-INTERVAL '1 month 2 days')"), V("iv_zero", "ts + INTERVAL '0 days'"),
        V("iv_month_end_add", "ts + INTERVAL '1 month 1 day'"), V("iv_month_end_sub", "ts - INTERVAL '1 month 1 day'"), V("iv_in_where", "id", from: "probe WHERE ts + INTERVAL '1 month 2 days' > TIMESTAMP '2024-03-01 00:00:00'"),
        V("iv_grouped", "date_trunc('day', ts + INTERVAL '1 day 12:00:00')"),
        S("iv_leap_day_year", "TIMESTAMP '2024-02-29 12:00:00' + INTERVAL '1 year'"), S("iv_leap_day_months", "TIMESTAMP '2024-02-29 12:00:00' + INTERVAL '12 months 1 day'"),
        S("iv_jan31_plus", "TIMESTAMP '2023-01-31 00:00:00' + INTERVAL '1 month 1 day'"), S("iv_mar31_minus", "TIMESTAMP '2024-03-31 00:00:00' - INTERVAL '1 month 1 day'"),
        S("iv_micro_edge", "TIMESTAMP '2024-01-31 23:59:59.999999' + INTERVAL '1 month 0.000001 seconds'"), S("iv_micro_edge_sub", "TIMESTAMP '2024-03-01 00:00:00' - INTERVAL '1 day 0.000001 seconds'"),
        S("iv_century_boundary", "TIMESTAMP '1999-12-31 23:59:59' + INTERVAL '1 month 1 second'"), S("iv_many_hours", "TIMESTAMP '2024-01-01 00:00:00' + INTERVAL '1 month 100000 hours'"),
        // bit operators, shifts, power and factorial (the plan names them by their symbols; polyglot did not read them back)
        V("band", "i & j"), V("bor", "i | j"), V("bnot", "~i"), V("xor_fn", "xor(i, j)"), V("band_big", "CAST(i AS BIGINT) & 4294967295"), V("bor_big", "CAST(i AS BIGINT) | 4294967296"), V("xor_big", "xor(CAST(i AS BIGINT), 1099511627776)"),
        V("band_neg_lit", "i & -2"), V("bnot_big", "~CAST(i AS BIGINT)"), V("bit_precedence", "i & j | 8"), V("xor_nested", "xor(i & 3, j | 4)"),
        V("shl_col", "(abs(i) % 1000) << (abs(j) % 8)"), V("shl_lit", "(abs(i) % 1000) << 3"), V("shl_zero", "(abs(i) % 1000) << 0"), V("shl_big", "(CAST(abs(i) AS BIGINT) % 1000) << (abs(j) % 40)"), V("shl_big_lit", "CAST(abs(i) % 1000 AS BIGINT) << 50"),
        V("shl_small", "CAST(abs(i) % 100 AS SMALLINT) << 2"), V("shl_30", "CAST(abs(i) % 2 AS INTEGER) << 30"), V("shl_nullable_count", "(abs(i) % 1000) << (abs(j) + id - id)"),
        S("shl_one_30", "CAST(1 AS INTEGER) << 30"), S("shl_big_62", "CAST(1 AS BIGINT) << 62"), V("shl_overflow_errors", "CAST(abs(i) % 2 + 1 AS INTEGER) << 31"), V("shl_big_overflow_errors", "CAST(abs(i) % 2 + 1 AS BIGINT) << 63"),
        V("shl_width_errors", "CAST(abs(i) % 2 + 1 AS INTEGER) << 32"), V("shl_max_errors", "CAST(2147483647 + (i - i) AS INTEGER) << 1"), V("shl_small_overflow_errors", "CAST(abs(i) % 2 + 20000 AS SMALLINT) << 1"),
        V("shl_just_fits", "CAST(abs(i) % 2 + 1 AS INTEGER) << 30 >> 29"), V("shl_big_just_fits", "CAST(abs(i) % 2 AS BIGINT) << 62"),
        V("shr_col", "i >> j"), V("shr_abs", "i >> abs(j)"), V("shr_lit", "i >> 2"), V("shr_lit_one", "i >> 1"), V("shr_lit_31", "i >> 31"), V("shr_lit_32", "i >> 32"), V("shr_negative_lit", "i >> -1"), V("shr_zero", "i >> 0"),
        V("shr_big", "CAST(i AS BIGINT) * 1000000000 >> abs(j)"), V("shr_big_neg", "CAST(i AS BIGINT) * -1000000000 >> abs(j)"), V("shr_big_lit62", "CAST(i AS BIGINT) * 1000000000 * 1000000000 >> 62"),
        V("shr_big_lit63", "CAST(i AS BIGINT) * 1000000000 * -1000000000 >> 63"), V("shr_big_lit64", "CAST(i AS BIGINT) * 1000000000 >> 64"), V("shr_big_col_wide", "CAST(i AS BIGINT) * 1000000000 * 1000000000 >> (abs(j) * 31)"),
        V("shr_small", "CAST(i % 1000 AS SMALLINT) >> abs(j)"), V("shr_small_16", "CAST(i % 1000 AS SMALLINT) >> 16"), V("shr_expr_count", "i >> (j + 1)"),
        S("shr_int_min", "CAST(-2147483647 - 1 AS INTEGER) >> 1"), S("shr_int_min_31", "CAST(-2147483647 - 1 AS INTEGER) >> 31"), S("shr_int_minus_one", "CAST(-1 AS INTEGER) >> 5"), S("shr_big_min", "CAST(-9223372036854775807 - 1 AS BIGINT) >> 1"),
        S("shr_big_min_62", "CAST(-9223372036854775807 - 1 AS BIGINT) >> 62"), S("shr_big_min_63", "CAST(-9223372036854775807 - 1 AS BIGINT) >> 63"), S("shr_big_max_63", "CAST(9223372036854775807 AS BIGINT) >> 63"), S("shr_big_max_62", "CAST(9223372036854775807 AS BIGINT) >> 62"),
        S("shr_odd_negative", "CAST(-7 AS INTEGER) >> 1"), S("shr_minus_eight", "CAST(-8 AS INTEGER) >> 1"),
        V("pow_op", "abs(i) ^ 2"), V("pow_dstar", "d ** 2"), V("pow_frac", "abs(d) ^ 0.5"), V("pow_neg", "2 ^ -1"), V("pow_int_int", "i ^ j"), V("pow_decimal", "n ^ 2"), S("pow_big", "2 ^ 62"), S("pow_zero_zero", "0 ^ 0"), V("pow_fn", "power(abs(i), 3)"),
        V("pow_assoc", "2 ^ 3 ^ 2"), V("pow_neg_base", "(-2) ^ 3"), V("pow_sci", "d ^ 1"),
        V("fact_postfix", "CAST((abs(i) % 34)! AS VARCHAR)"), V("fact_fn", "CAST(factorial(abs(i) % 34) AS VARCHAR)"), V("fact_33", "CAST(factorial(33 + (i - i)) AS VARCHAR)"), V("fact_zero_one", "CAST(factorial(abs(j) % 2) AS VARCHAR)"),
        V("fact_34_errors", "CAST(factorial(abs(i) + 34) AS VARCHAR)", engines: null), V("fact_negative_errors", "CAST(factorial(-abs(i) - 1) AS VARCHAR)"), V("fact_arith", "factorial(5 + (i - i)) / 120"),
        // FLOAT is 32 bits: the cast is REAL on both engines; division and rounding are done in double arithmetic and cut back to 32 bits as DuckDB does
        V("float_cast", "CAST(d AS FLOAT)"), V("float_div", "CAST(d AS FLOAT) / CAST(j + 1 AS FLOAT)"), V("float_div_const", "CAST(d AS FLOAT) / 3"), V("float_div_chain", "CAST(d AS FLOAT) * CAST(i % 1000 AS FLOAT) / CAST(7 AS FLOAT)"),
        V("float_round1", "round(CAST(d AS FLOAT), 1)"), V("float_round2", "round(CAST(d AS FLOAT), 2)"), V("float_round0", "round(CAST(d AS FLOAT))"), V("float_round_005", "round(CAST(1.005 AS FLOAT) + d * 0, 2)"),
        V("float_round_neg", "round(CAST(d AS FLOAT), -1)"), V("float_trunc1", "trunc(CAST(d AS FLOAT), 1)"), V("float_trunc0", "trunc(CAST(d AS FLOAT))"), V("float_widened", "CAST(CAST(d AS FLOAT) / CAST(3 AS FLOAT) AS DOUBLE)"),
        V("float_sum", "sum(CAST(d AS FLOAT)) OVER ()"), V("float_avg", "avg(CAST(d AS FLOAT)) OVER ()"), V("float_literal_widened", "CAST(CAST(1.4 AS FLOAT) AS DOUBLE) + d * 0"),
        V("trunc_double1", "trunc(d, 1)"), V("trunc_double2", "trunc(d, 2)"), V("trunc_double0", "trunc(d, 0)"), V("trunc_edge", "trunc(CAST(2.9999999999999996 AS DOUBLE) + d * 0, 1)"), V("trunc_neg", "trunc(-d, 1)"), V("trunc_decimal", "trunc(n, 1)"),
        // strftime and format are written out as pieces of text
        V("sf_ymd", "strftime(ts, '%Y-%m-%d')"), V("sf_hms", "strftime(ts, '%H:%M:%S')"), V("sf_names", "strftime(ts, '%A %B %-d, %Y')"), V("sf_short_names", "strftime(ts, '%a %b %d')"), V("sf_doy", "strftime(ts, '%j|%-j')"),
        V("sf_twelve", "strftime(ts, '%I:%M %p|%-I')"), V("sf_year2", "strftime(ts, '%y-%m')"), V("sf_unpadded", "strftime(ts, '%-d/%-m %-H:%-M:%-S')"), V("sf_micro", "strftime(ts + INTERVAL 123456 MICROSECOND, '%H:%M:%S.%f')"),
        V("sf_dow", "strftime(ts, '%w %u')"), V("sf_percent", "strftime(ts, '100%% %Y')"), V("sf_date", "strftime(dt, '%Y/%m/%d %H:%M')"), V("sf_literal", "strftime(ts, 'week of it')"), V("sf_arg_order", "strftime('%d.%m.%Y', ts)"),
        V("sf_quote", "strftime(ts, 'it''s %Y')"), S("sf_midnight_noon", "strftime(TIMESTAMP '2024-03-04 00:02:03', '%I %p') || strftime(TIMESTAMP '2024-03-04 12:02:03', '%I %p')"), S("sf_year_small", "strftime(TIMESTAMP '0099-03-04 01:02:03', '%Y|%y')"),
        V("fmt_plain", "format('{}|{}', s, i)"), V("fmt_text", "format('x{}y', i)"), V("fmt_date", "format('on {}', dt)"), V("fmt_braces", "format('{{{}}}', i)"), V("fmt_none", "format('constant')"), V("fmt_bigint", "format('{}', CAST(i AS BIGINT) * 1000000000)"),
        // PostgreSQL's covariance accumulates unstably next to an outlier (-334 for 0); the sums are exact now. (SQL Server has none of the three: matrix row fn.missing.statistics.sqlserver)
        new("cov_pop_outlier", "SELECT covar_pop(a.d, b.d) AS v FROM probe a CROSS JOIN probe b", PostgresOnly), new("cov_samp_outlier", "SELECT covar_samp(a.d, b.d) AS v FROM probe a CROSS JOIN probe b", PostgresOnly),
        new("regr_sxy_outlier", "SELECT regr_sxy(a.d, b.d) AS v FROM probe a CROSS JOIN probe b", PostgresOnly), new("cov_pop_ints", "SELECT covar_pop(i, j) AS v FROM probe", PostgresOnly),
        new("cov_samp_filter", "SELECT covar_samp(i, j) FILTER (WHERE id > 2) AS v FROM probe", PostgresOnly), new("regr_sxy_small", "SELECT regr_sxy(d, d * 2) AS v FROM probe WHERE id < 4", PostgresOnly),
        new("cov_pop_one_row", "SELECT covar_pop(d, i) AS v FROM probe WHERE id = 1", PostgresOnly), new("cov_samp_one_row", "SELECT covar_samp(d, i) AS v FROM probe WHERE id = 1", PostgresOnly),
        V("starts_op", "s ^@ 'a'"), V("starts_op_col", "s ^@ s"), V("starts_op_empty", "s ^@ ''"),
    ];

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task The_engine_gives_DuckDBs_rows_for_every_case(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        await probe.CreateTableAsync();

        var problems = new List<string>();
        foreach (var c in Cases.Where(c => c.Engines == null || c.Engines.Contains(name)))
        {
            var expected = probe.OnDuckDb(c.Sql);
            var (rendered, refused) = probe.Render(c.Sql);
            if (rendered == null)
            {
                if (expected.Error != null && refused != null && refused.StartsWith("duckdb:", StringComparison.Ordinal)) continue;       // DuckDB itself rejects the query before there is a plan
                problems.Add($"{c.Id}: refused ({refused})");
                continue;
            }
            var actual = await probe.OnEngineAsync(rendered);
            if (expected.Error != null)
            {
                if (actual.Error == null) problems.Add($"{c.Id}: DuckDB raises ({expected.Error}) and the engine returns [{actual.Rows}]  {rendered.Replace("\n", " ")}");
            }
            else if (actual.Error != null) problems.Add($"{c.Id}: engine error {actual.Error}  {rendered.Replace("\n", " ")}");
            else if (!SameRows(expected.Rows, actual.Rows)) problems.Add($"{c.Id}: duckdb [{expected.Rows}] engine [{actual.Rows}]  {rendered.Replace("\n", " ")}");
        }
        Assert.True(problems.Count == 0, name + ":\n" + string.Join("\n", problems));
    }

    /// <summary>Queries the tool must refuse at compile (the lowering or the target step says so before an engine is asked): the id, the SQL and the text the message holds.</summary>
    private static readonly (string Id, string Sql, string Contains)[] Refusals =
    [
        ("sf_week_number", "SELECT strftime(ts, '%U') AS v FROM probe", "%U"), ("sf_iso_year", "SELECT strftime(ts, '%G-%V') AS v FROM probe", "%G"), ("sf_zone", "SELECT strftime(ts, '%Z') AS v FROM probe", "%Z"),
        ("sf_locale", "SELECT strftime(ts, '%c') AS v FROM probe", "%c"),
        ("fmt_width", "SELECT format('{:05d}', i) AS v FROM probe", "{:05d}"), ("fmt_double", "SELECT format('{}', d) AS v FROM probe", "DOUBLE"), ("fmt_not_literal", "SELECT format(s, i) AS v FROM probe", "not a literal"),
    ];

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task What_cannot_be_exact_is_refused_with_a_reason(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        var problems = new StringBuilder();
        foreach (var (id, sql, contains) in Refusals)
        {
            var (rendered, refused) = probe.Render(sql);
            if (rendered != null || refused == null || !refused.Contains(contains, StringComparison.OrdinalIgnoreCase)) problems.AppendLine($"{id}: expected a refusal naming '{contains}', got {refused ?? rendered}");
        }
        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>The rows are equal; a number may differ by a relative 1e-11 (the functions that are written out from others do not reproduce the last bits of the C library's).</summary>
    private static bool SameRows(string expected, string actual)
    {
        if (expected == actual) return true;
        var (a, b) = (expected.Split("; "), actual.Split("; "));
        if (a.Length != b.Length) return false;
        for (var r = 0; r < a.Length; r++)
        {
            var (x, y) = (a[r].Split('|'), b[r].Split('|'));
            if (x.Length != y.Length) return false;
            for (var c = 0; c < x.Length; c++)
            {
                if (x[c] == y[c]) continue;
                if (!double.TryParse(x[c], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || !double.TryParse(y[c], NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) return false;
                if (Math.Abs(p - q) > 1e-11 * Math.Max(Math.Abs(p), Math.Abs(q)) + 1e-12) return false;
            }
        }
        return true;
    }
}
