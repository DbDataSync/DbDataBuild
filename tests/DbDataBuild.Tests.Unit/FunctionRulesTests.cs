using System.Globalization;
using DbDataBuild.Sql;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rules;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The function rules of entry 177 onward (docs/research/function-matrix.md), each tested the way CLAUDE.md asks: the text a target gets (here), the rewritten query run on DuckDB against the original (the
/// differential, here: a rule writes DuckDB-dialect text, so DuckDB can run both), and the engines' rows against DuckDB's (`FunctionRuleProbes` in the conformance suite).
/// </summary>
public class FunctionRulesTests
{
    /// <summary>Edge rows: negatives, zero, NULL, a half, large values.</summary>
    private static DuckDBConnection Edge()
    {
        var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE t (id INTEGER, x DOUBLE, i INTEGER, b BIGINT)");
        Run(c, "INSERT INTO t VALUES (1, 0.5, 0, 0), (2, 2.5, 7, 1), (3, 100.0, -7, -1), (4, 1e-3, 2147483647, 9223372036854775807), (5, NULL, NULL, NULL), (6, 12345.678, -2147483648, -9223372036854775808)");
        return c;
    }

    private static void Run(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    internal static string Rows(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i) switch
            {
                double d => Math.Round(d, 9).ToString("0.#########", CultureInfo.InvariantCulture),
                var v => Convert.ToString(v, CultureInfo.InvariantCulture)!,
            })));
        rows.Sort(StringComparer.Ordinal);
        return string.Join("; ", rows);
    }

    /// <summary>The rule fires on <paramref name="target"/> and what it writes gives DuckDB's rows.</summary>
    private static void Differential(string rule, string target, string sql)
    {
        using var c = Edge();
        var rewritten = TargetRules.Apply(sql, target);
        Assert.Contains(rule, rewritten.Rules);
        Assert.Equal(Rows(c, sql), Rows(c, rewritten.Sql));
    }

    [Theory]
    [InlineData("SELECT id, log(x) AS v FROM t WHERE x > 0", "LOG10(x)")]
    [InlineData("SELECT id, log(x + 1) AS v FROM t WHERE x > 0", "LOG10(x + 1)")]
    public void Log_with_one_argument_is_the_base_ten_logarithm_on_SQL_Server_and_Fabric(string sql, string expected)
    {
        foreach (var target in new[] { "sqlserver", "fabric" })
        {
            Assert.Equal([TargetRules.LogIsBaseTen], TargetRules.Apply(sql, target).Rules);
            Assert.Contains(expected, Polyglot.TranspileOne(TargetRules.Apply(sql, target).Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!);
        }
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);                       // PostgreSQL's log(x) is base 10 already
    }

    [Fact]
    public void Log_with_a_base_is_left_alone_because_the_transpile_already_swaps_the_arguments()
    {
        const string sql = "SELECT log(2, x) AS v FROM t";
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);
        Assert.Contains("LOG(x, 2)", Polyglot.TranspileOne(sql, Dialects.Canonical, Dialects.ForTarget("sqlserver")).Sql!);
    }

    [Fact]
    public void Log_rewritten_for_SQL_Server_gives_DuckDBs_values()
    {
        Differential(TargetRules.LogIsBaseTen, "sqlserver", "SELECT id, log(x) AS v FROM t WHERE x > 0");
        Differential(TargetRules.LogIsBaseTen, "sqlserver", "SELECT id, log(CAST(x AS DOUBLE) + 1) AS v FROM t");
    }

    [Fact]
    public void Log_with_a_base_is_the_ratio_of_natural_logarithms_on_PostgreSQL_as_DuckDB_computes_it()
    {
        const string sql = "SELECT id, log(2, x) AS v FROM t WHERE x > 0";
        var r = TargetRules.Apply(sql, "postgres");
        Assert.Equal([TargetRules.LogWithBase], r.Rules);
        Assert.Contains("LN(CAST(x AS DOUBLE PRECISION)) AS DOUBLE PRECISION) / NULLIF(LN(CAST(2 AS DOUBLE PRECISION)), 0)", Polyglot.TranspileOne(r.Sql, Dialects.Canonical, Dialects.ForTarget("postgres")).Sql!);
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);
        Differential(TargetRules.LogWithBase, "postgres", sql);
        using var c = Edge();                                                      // the same to the last digit but at most one bit (the engines compute the quotient themselves)
        Assert.Equal("5", Rows(c, "SELECT count(*) FROM t WHERE x > 0 AND abs(log(2, x) - ln(CAST(x AS DOUBLE)) / ln(CAST(2 AS DOUBLE))) <= abs(log(2, x)) * 4e-16"));
    }

    // ---- intervals of several units on SQL Server ----

    private static DuckDBConnection Dates()
    {
        var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE d (id INTEGER, ts TIMESTAMP, dt DATE)");
        Run(c, @"INSERT INTO d VALUES (1, '2024-01-31 23:59:59.999999', '2024-01-31'), (2, '2024-02-29 12:00:00', '2024-02-29'), (3, '2023-03-31 00:00:00', '2023-03-31'), (4, '2024-03-31 10:00:00', '2024-03-31'),
            (5, '2000-02-29 00:00:00', '2000-02-29'), (6, '1900-01-31 06:30:00', '1900-01-31'), (7, '2024-12-31 23:00:00', '2024-12-31'), (8, NULL, NULL), (9, '2023-01-01 00:00:00', '2023-01-01'), (10, '2024-05-31 00:00:01', '2024-05-31')");
        return c;
    }

    public static IEnumerable<object[]> IntervalExpressions() => new[]
    {
        "ts + INTERVAL '1 month 2 days 03:04:05'", "ts - INTERVAL '1 month 2 days 03:04:05'", "dt + INTERVAL '1 month 2 days 03:04:05'", "dt - INTERVAL '1 month 2 days 03:04:05'",
        "ts + INTERVAL '1 year 2 months'", "ts + INTERVAL '-1 month -2 days'", "ts + INTERVAL '1 month -2 days'", "ts - INTERVAL '-1 month 2 days'", "ts + INTERVAL '1 month -03:04:05'",
        "ts + INTERVAL '90 minutes'", "dt + INTERVAL 3 HOUR", "ts + INTERVAL '1.5 seconds'", "ts + INTERVAL '0.000001 seconds'", "ts - INTERVAL '1 day 0.000123 seconds'",
        "dt + INTERVAL 1 WEEK", "ts - INTERVAL 2 WEEK", "dt + INTERVAL 1 QUARTER", "dt + INTERVAL 1 DECADE", "ts + INTERVAL 500 MILLISECOND", "ts + INTERVAL 7 MICROSECOND",
        "INTERVAL 1 DAY + dt", "INTERVAL '1 month 2 days' + ts", "ts + (INTERVAL 1 MONTH + INTERVAL 2 DAY)", "ts + (INTERVAL '1 month 2 days' - INTERVAL 1 DAY)",
        "ts + INTERVAL 1 MONTH + INTERVAL 1 DAY", "ts + (-INTERVAL '1 month 2 days')", "ts + INTERVAL '0 days'", "ts + INTERVAL '1 month 1 day'", "ts - INTERVAL '1 month 1 day'",
        "ts + INTERVAL '12 months 1 day'", "ts - INTERVAL '13 months 100000 hours'", "ts + INTERVAL '2 weeks 3 days 4 hours'",
    }.Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(IntervalExpressions))]
    public void An_interval_of_several_units_becomes_one_DATEADD_per_part_in_DuckDBs_order_and_gives_DuckDBs_value(string expression)
    {
        var sql = $"SELECT id, {expression} AS v FROM d";
        using var c = Dates();
        var rewritten = TargetRules.Apply(sql, "sqlserver");
        foreach (var target in new[] { "sqlserver", "fabric" })
        {
            var r = TargetRules.Apply(sql, target);
            if (r.Rules.Count == 0) continue;                       // a single date-part unit on the right is already DATEADD
            Assert.Equal([TargetRules.IntervalAsDateAdd], r.Rules);
            var text = TargetRules.Finish(Polyglot.TranspileOne(r.Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!, target);
            Assert.DoesNotContain("INTERVAL", text, StringComparison.OrdinalIgnoreCase);
            if (!expression.Contains("'0 days'")) Assert.Contains("DATEADD(", text);        // (an interval of nothing is the date itself)
        }
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);                                   // PostgreSQL has the interval type
        Assert.Equal(Rows(c, sql), Rows(c, rewritten.Sql));                                       // the differential: the nested form is the same value on DuckDB
    }

    [Fact]
    public void The_months_come_first_then_the_days_then_the_time()
    {
        var r = TargetRules.Apply("SELECT ts + INTERVAL '1 month 2 days 03:04:05' AS v FROM d", "sqlserver");
        var sql = TargetRules.Finish(Polyglot.TranspileOne(r.Sql, Dialects.Canonical, Dialects.ForTarget("sqlserver")).Sql!, "sqlserver");
        Assert.Contains("DATEADD(MONTH, 1,", sql);
        Assert.True(sql.IndexOf("DATEADD(MONTH", StringComparison.Ordinal) > sql.IndexOf("DATEADD(DAY", StringComparison.Ordinal), sql);      // the months are the innermost call, so they are applied first
        Assert.True(sql.IndexOf("DATEADD(DAY", StringComparison.Ordinal) > sql.IndexOf("DATEADD(HOUR", StringComparison.Ordinal), sql);        // then the days, then the time (the outermost)
    }

    [Fact]
    public void A_date_becomes_a_timestamp_before_a_time_unit_is_added_only_where_the_lowerer_says_a_date_is_involved()
    {
        string Render(string sql) => TargetRules.Finish(Polyglot.TranspileOne(TargetRules.Apply(sql, "sqlserver").Sql, Dialects.Canonical, Dialects.ForTarget("sqlserver")).Sql!, "sqlserver");
        Assert.Contains("DATEADD(HOUR, 3, CAST(dt AS DATETIME2))", Render("SELECT CAST((dt + INTERVAL 3 HOUR) AS TIMESTAMP) AS v FROM d"));
        Assert.Contains("DATEADD(HOUR, -1, SYSDATETIMEOFFSET())", Render("SELECT now() - INTERVAL 1 HOUR AS v FROM d"));          // a timestamp with a zone keeps its offset
        Assert.DoesNotContain("CAST(ts AS DATETIME2)", Render("SELECT ts + INTERVAL 3 HOUR AS v FROM d"));
    }

    [Theory]
    [InlineData("SELECT dt + INTERVAL 1 MONTH AS v FROM d")]                                    // one date-part unit on the right: the transpile's DATEADD is exact
    [InlineData("SELECT dt - INTERVAL 3 DAY AS v FROM d")]
    [InlineData("SELECT dt + INTERVAL 1 YEAR AS v FROM d")]
    public void A_single_date_part_unit_is_left_to_the_transpile(string sql)
    {
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);
    }

    [Theory]
    [InlineData("SELECT dt + CAST('1 d' AS INTERVAL) AS v FROM d")]                               // a short unit
    [InlineData("SELECT dt + CAST('1.5 days' AS INTERVAL) AS v FROM d")]                           // a fraction of a day
    [InlineData("SELECT dt + CAST('5' AS INTERVAL) AS v FROM d")]                                  // no unit
    public void An_interval_text_the_reader_does_not_know_is_not_rewritten_and_the_matrix_refuses_it(string sql)
    {
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);
        var linter = new DbDataBuild.Sql.Matrix.MatrixLinter(DbDataBuild.Sql.Matrix.MatrixLoader.LoadEmbedded([]));
        Assert.Contains(linter.Lint(TargetRules.Apply(sql, "sqlserver").Sql, "m.sql", ["sqlserver"]), d => d.Code == "DDB-301" && d.Found.Contains("op.interval_text"));
        Assert.DoesNotContain(linter.Lint(TargetRules.Apply(sql, "postgres").Sql, "m.sql", ["postgres"]), d => d.Code == "DDB-301");
    }

    [Fact]
    public void An_interval_the_rule_rewrote_is_not_reported_by_the_matrix()
    {
        var sql = TargetRules.Apply("SELECT dt + INTERVAL '1 month 2 days' AS v FROM d", "sqlserver").Sql;
        var diags = new DbDataBuild.Sql.Matrix.MatrixLinter(DbDataBuild.Sql.Matrix.MatrixLoader.LoadEmbedded([])).Lint(sql, "m.sql", ["sqlserver"]);
        Assert.DoesNotContain(diags, d => d.Code == "DDB-301");
    }
}
