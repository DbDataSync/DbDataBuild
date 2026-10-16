using System.Globalization;
using DbDataBuild.Lowering;
using DbDataBuild.Models;
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

    // ---- bit operators, shifts, power and factorial ----

    private static string Lowered(string sql)
    {
        using var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE t (i INTEGER, j INTEGER, b BIGINT, sm SMALLINT, ti TINYINT, h HUGEINT, x DOUBLE, s VARCHAR)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = QueryDescriber.PlanStatement(sql);
        return PlanLowerer.Lower((string)cmd.ExecuteScalar()!).Sql;
    }

    private static string Transpiled(string lowered, string target)
    {
        var r = TargetRules.Apply(lowered, target);
        return TargetRules.Finish(Polyglot.TranspileOne(r.Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!, target);
    }

    [Theory]
    [InlineData("SELECT i & j AS v FROM t", "(i & j)")]
    [InlineData("SELECT i | j AS v FROM t", "(i | j)")]
    [InlineData("SELECT i ^ j AS v FROM t", "(CAST(i AS DOUBLE) ** CAST(j AS DOUBLE))")]
    [InlineData("SELECT x ** 2 AS v FROM t", "** CAST(2 AS DOUBLE)")]
    [InlineData("SELECT 2 ^ -1 AS v FROM t", "(CAST(2 AS DOUBLE) ** CAST(-1 AS DOUBLE))")]
    [InlineData("SELECT power(2, i) AS v FROM t", "power(CAST(2 AS DOUBLE),")]
    [InlineData("SELECT i! AS v FROM t", "factorial(i)")]
    [InlineData("SELECT s ^@ 'a' AS v FROM t", "starts_with(s, 'a')")]
    [InlineData("SELECT i << 3 AS v FROM t", "(CAST(i AS INTEGER) << 3)")]
    [InlineData("SELECT b >> j AS v FROM t", "(CAST(b AS BIGINT) >> CAST(j AS BIGINT))")]
    [InlineData("SELECT sm << 2 AS v FROM t", "(CAST(sm AS SMALLINT) << 2)")]
    public void The_lowerer_writes_the_operators_the_parser_reads(string sql, string expected)
    {
        var lowered = Lowered(sql);
        Assert.Contains(expected, lowered);
        Assert.True(Polyglot.Parse(lowered, Dialects.Canonical).Ok, lowered);
    }

    [Theory]
    [InlineData("SELECT ti << 1 AS v FROM t", "TINYINT")]
    [InlineData("SELECT h >> 1 AS v FROM t", "HUGEINT")]
    public void A_shift_over_a_type_that_cannot_be_shifted_exactly_everywhere_is_refused_with_the_type_named(string sql, string type)
    {
        var ex = Assert.Throws<LoweringException>(() => Lowered(sql));
        Assert.Contains(type, ex.Message);
    }

    [Fact]
    public void Shifts_are_written_as_exact_arithmetic_on_each_engine()
    {
        const string left = "SELECT CAST(i AS INTEGER) << CAST(j AS INTEGER) AS v FROM t";
        var tsql = Transpiled(left, "sqlserver");
        Assert.Contains("CAST(CAST(i AS NUMERIC(38, 0)) * POWER(CAST(2 AS NUMERIC(38, 0)), CAST(j AS INTEGER)) AS INTEGER)", tsql);
        Assert.Equal(TargetRules.Apply(left, "sqlserver").Rules, [TargetRules.ShiftLeft]);
        Assert.Contains("* 8 AS INTEGER)", Transpiled("SELECT CAST(i AS INTEGER) << 3 AS v FROM t", "sqlserver"));
        Assert.DoesNotContain("<<", tsql);
        Assert.DoesNotContain("<<", Transpiled(left, "postgres"));                                           // PostgreSQL's << wraps silently
        var right = Transpiled("SELECT CAST(i AS INTEGER) >> CAST(j AS INTEGER) AS v FROM t", "sqlserver");
        Assert.DoesNotContain(">>", right);
        Assert.DoesNotContain("DDB_IDIV", right, StringComparison.OrdinalIgnoreCase);                          // the marker is gone: a whole-number division
        Assert.Contains("~((~CAST(i AS BIGINT)) / (", right.Replace("\n", "").Replace("  ", " "));
        Assert.Contains("CAST(i AS INT) >> CAST(", Transpiled("SELECT CAST(i AS INTEGER) >> CAST(j AS INTEGER) AS v FROM t", "postgres"));
        Assert.Contains("CASE WHEN b < 0 THEN -1 ELSE 0 END", Transpiled("SELECT CAST(b AS BIGINT) >> 63 AS v FROM t", "sqlserver"));      // 2^63 does not fit a BIGINT
        Assert.DoesNotContain("POWER(CAST(2 AS BIGINT)", Transpiled("SELECT CAST(b AS BIGINT) >> 3 AS v FROM t", "sqlserver"));              // a literal count needs no POWER
    }

    [Fact]
    public void The_exclusive_or_is_the_caret_on_SQL_Server_the_hash_on_PostgreSQL_and_a_big_literal_is_a_bigint()
    {
        Assert.Contains("((i) ^ (j))", Transpiled("SELECT xor(i, j) AS v FROM t", "sqlserver").Replace("\n", ""));
        Assert.Contains("i # j", Transpiled("SELECT xor(i, j) AS v FROM t", "postgres"));
        Assert.Contains("CAST(4294967295 AS BIGINT)", Transpiled("SELECT (b & 4294967295) AS v FROM t", "sqlserver"));
        Assert.Contains("(b & 4294967295)", Transpiled("SELECT (b & 4294967295) AS v FROM t", "postgres"));
        Assert.Empty(TargetRules.Apply("SELECT (i & 255) AS v FROM t", "sqlserver").Rules);                         // an INT-sized literal is fine
    }

    [Fact]
    public void The_factorial_table_covers_0_to_33_and_overflows_beyond()
    {
        var sql = Transpiled("SELECT factorial(i) AS v FROM t", "sqlserver");
        Assert.Contains("WHEN 33 THEN CAST('8683317618811886495518194401280000000' AS NUMERIC(38, 0))", sql);
        Assert.DoesNotContain("WHEN 34", sql);
        Assert.Contains("ELSE CAST(CAST(i AS FLOAT) * 1E38 AS NUMERIC(38, 0))", sql.Replace("1e38", "1E38"));
        Assert.Equal([TargetRules.FactorialTable], TargetRules.Apply("SELECT factorial(i) AS v FROM t", "postgres").Rules);
    }

    private static DuckDBConnection Numbers()
    {
        var c = PlanningConnection.Open(lockDown: false);
        // the rule writes ddb_idiv for the engine's whole-number division; DuckDB's // is that (truncating toward zero), and these values are far inside what a double holds exactly
        Run(c, "CREATE MACRO ddb_idiv(a, b) AS CAST(trunc(CAST(a AS DOUBLE) / CAST(b AS DOUBLE)) AS BIGINT)");
        Run(c, "CREATE TABLE n (i INTEGER, j INTEGER, b BIGINT)");
        var rows = new List<string>();
        foreach (var i in new[] { -2147483648L, -65536L, -8L, -7L, -1L, 0L, 1L, 7L, 8L, 1000L, 65535L, 2147483647L })
            foreach (var j in new[] { -1, 0, 1, 2, 3, 15, 16, 30, 31, 32, 33, 62, 63, 64 })
                rows.Add($"({i}, {j}, {(i * 1_000_003L) % 4_000_000_000_000L})");
        Run(c, "INSERT INTO n VALUES " + string.Join(", ", rows));
        return c;
    }

    [Theory]
    [InlineData("SELECT i, j, CAST(i AS INTEGER) >> CAST(j AS INTEGER) AS v FROM n")]
    [InlineData("SELECT i, j, CAST(i AS INTEGER) >> 3 AS v FROM n")]
    [InlineData("SELECT i, j, CAST(i AS INTEGER) >> 31 AS v FROM n")]
    [InlineData("SELECT i, j, CAST(i AS INTEGER) >> 32 AS v FROM n")]
    [InlineData("SELECT i, j, CAST(i AS INTEGER) >> -1 AS v FROM n")]
    [InlineData("SELECT i, j, CAST(b AS BIGINT) >> CAST(j AS INTEGER) AS v FROM n")]
    [InlineData("SELECT i, j, CAST(b AS BIGINT) >> 17 AS v FROM n")]
    [InlineData("SELECT i, j, CAST(b AS BIGINT) >> 63 AS v FROM n")]
    [InlineData("SELECT i, j, CAST(i AS BIGINT) >> CAST(j AS INTEGER) AS v FROM n")]
    public void A_right_shift_written_as_arithmetic_gives_DuckDBs_value_on_the_edges(string sql)
    {
        using var c = Numbers();
        foreach (var target in new[] { "sqlserver", "postgres" })
        {
            var r = TargetRules.Apply(sql, target);
            Assert.Equal([TargetRules.ShiftRight], r.Rules);
            Assert.Equal(Rows(c, sql), Rows(c, r.Sql));
        }
    }

    [Theory]
    [InlineData("SELECT i, j, CAST(abs(i % 65536) AS INTEGER) << CAST(j AS INTEGER) AS v FROM n WHERE j BETWEEN 0 AND 14")]
    [InlineData("SELECT i, j, CAST(abs(i % 65536) AS INTEGER) << 3 AS v FROM n")]
    [InlineData("SELECT i, j, CAST(abs(i % 1000) AS BIGINT) << CAST(j AS INTEGER) AS v FROM n WHERE j BETWEEN 0 AND 40")]
    [InlineData("SELECT i, j, CAST(abs(i % 100) AS SMALLINT) << 2 AS v FROM n")]
    public void A_left_shift_written_as_a_product_gives_DuckDBs_value_where_DuckDB_has_one(string sql)
    {
        using var c = Numbers();
        foreach (var target in new[] { "sqlserver", "postgres" })
        {
            var r = TargetRules.Apply(sql, target);
            Assert.Equal([TargetRules.ShiftLeft], r.Rules);
            Assert.Equal(Rows(c, sql), Rows(c, r.Sql));
        }
    }

    [Theory]
    [InlineData("SELECT i, CAST(factorial(abs(i % 1000) % 34) AS VARCHAR) AS v FROM n")]
    [InlineData("SELECT i, CAST(CAST(abs(j) AS INTEGER)! AS VARCHAR) AS v FROM n WHERE j < 34")]
    public void The_factorial_table_gives_DuckDBs_digits(string sql)
    {
        using var c = Numbers();
        foreach (var target in new[] { "sqlserver", "postgres" })
        {
            var r = TargetRules.Apply(sql.Replace("CAST(abs(j) AS INTEGER)!", "factorial(CAST(abs(j) AS INTEGER))"), target);
            Assert.Contains(TargetRules.FactorialTable, r.Rules);
            Assert.Equal(Rows(c, sql.Replace("CAST(abs(j) AS INTEGER)!", "factorial(CAST(abs(j) AS INTEGER))")), Rows(c, r.Sql));
        }
    }

    [Theory]
    [InlineData("SELECT factorial(abs(i % 1000) + 34) AS v FROM n")]
    [InlineData("SELECT factorial(-abs(i % 1000) - 1) AS v FROM n")]
    public void Where_the_factorial_raises_in_DuckDB_the_rewritten_text_raises_too(string sql)
    {
        using var c = Numbers();
        Assert.ThrowsAny<DuckDBException>(() => Rows(c, sql));
        Assert.ThrowsAny<DuckDBException>(() => Rows(c, TargetRules.Apply(sql, "sqlserver").Sql));
    }

    [Fact]
    public void Where_DuckDB_raises_for_a_shift_the_rewritten_text_is_not_asked_to_match()
    {
        // documented in the rule: a negative value, a negative count and a count beyond the width raise in DuckDB; the engine returns the product (or raises on overflow, which is DuckDB's other case)
        using var c = Numbers();
        Assert.ThrowsAny<DuckDBException>(() => Rows(c, "SELECT CAST(-1 AS INTEGER) << CAST(j AS INTEGER) FROM n"));
        Assert.ThrowsAny<DuckDBException>(() => Rows(c, "SELECT CAST(1 AS INTEGER) << CAST(j AS INTEGER) FROM n"));
    }

    [Theory]
    [InlineData("SELECT i & j AS v FROM t")]
    [InlineData("SELECT i | j AS v FROM t")]
    [InlineData("SELECT ~i AS v FROM t")]
    [InlineData("SELECT xor(i, j) AS v FROM t")]
    [InlineData("SELECT i ^ j AS v FROM t")]
    [InlineData("SELECT x ** 2 AS v FROM t")]
    [InlineData("SELECT power(x, 2) AS v FROM t")]
    [InlineData("SELECT i << 3 AS v FROM t")]
    [InlineData("SELECT i >> j AS v FROM t")]
    [InlineData("SELECT b << j AS v FROM t")]
    [InlineData("SELECT i! AS v FROM t")]
    [InlineData("SELECT s ^@ 'a' AS v FROM t")]
    public void The_operators_are_covered_by_the_matrix_after_their_rules_have_run(string sql)
    {
        var lowered = Lowered(sql);
        var linter = new DbDataBuild.Sql.Matrix.MatrixLinter(DbDataBuild.Sql.Matrix.MatrixLoader.LoadEmbedded([]));
        foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
        {
            var diags = linter.Lint(TargetRules.Apply(lowered, target).Sql, "m.sql", [target]).Where(d => d.Severity != DbDataBuild.Core.Severity.Note && d.Code != "DDB-304").ToList();
            Assert.True(diags.Count == 0, $"{sql} on {target}: {string.Join(" | ", diags.Select(d => d.Code + " " + d.Found))}");
        }
    }

    // ---- functions written out from others (FunctionTemplates): one theory over the table ----

    private static DuckDBConnection ProbeTable()
    {
        var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE probe (id INTEGER NOT NULL, i INTEGER, j INTEGER, d DOUBLE, n DECIMAL(10, 2), s VARCHAR, dt DATE, ts TIMESTAMP)");
        Run(c, @"INSERT INTO probe VALUES (1, 7, 2, 2.5, 2.50, 'abc', '2024-01-15', '2024-01-15 10:30:45'), (2, -7, 2, -2.5, -2.50, 'ABC', '2024-02-29', '2024-02-29 23:59:59'), (3, 7, -2, 3.5, 3.50, ' abc ', '2024-03-31', '2024-03-31 00:00:00'),
            (4, 0, 0, 0.5, 0.00, '', '2023-12-31', '2023-12-31 12:00:00'), (5, NULL, 3, NULL, NULL, NULL, NULL, NULL), (6, 5, 0, 1.005, 1.25, 'xa', '2024-06-30', '2024-06-30 08:15:30'),
            (7, 2147483647, 1, -0.5, -0.50, 'héllo', '2024-01-31', '2024-01-31 18:45:00'), (8, 10, 3, 10000000000, 12345.67, 'a,b,c', '2024-02-01', '2024-02-01 00:00:01')");
        Run(c, "CREATE MACRO ddb_atn2(y, x) AS atan2(y, x)");                     // the marker the rule writes for ATN2
        Run(c, "CREATE MACRO ddb_isoweek(x) AS date_part('week', x)");              // DATEPART(ISO_WEEK, x)
        Run(c, "CREATE MACRO ddb_datepart_millisecond(x) AS date_part('millisecond', x) % 1000");        // T-SQL's DATEPART counts within the second
        Run(c, "CREATE MACRO ddb_datepart_microsecond(x) AS date_part('microsecond', x) % 1000000");
        Run(c, "CREATE MACRO ddb_idiv(a, b) AS CAST(trunc(CAST(a AS DOUBLE) / CAST(b AS DOUBLE)) AS BIGINT)");
        Run(c, "CREATE MACRO ddb_numeric(x) AS CAST(x AS DECIMAL(38, 4))");
        Run(c, "CREATE MACRO ddb_pg_epoch(x) AS epoch(x)");
        Run(c, "CREATE MACRO ddb_pg_epoch_exact(x) AS CAST(epoch_us(x) AS DECIMAL(24, 0)) / 1000000");
        Run(c, "CREATE MACRO ddb_pg_julian(x) AS julian(x)");
        Run(c, "CREATE MACRO ddb_epoch(x) AS epoch(x)");                             // DATEDIFF_BIG seconds and the microseconds
        foreach (var unit in new[] { "hour", "minute", "second", "millisecond", "microsecond" }) Run(c, $"CREATE MACRO ddb_datediff_big_{unit}(a, b) AS date_diff('{unit}', a, b)");
        return c;
    }

    public static IEnumerable<object[]> Templates() => Enumerable.Range(0, FunctionTemplates.All.Count).Select(i => new object[] { i });

    private static bool SameWithin(string expected, string actual)
    {
        if (expected == actual) return true;
        var (a, b) = (expected.Split("; "), actual.Split("; "));
        if (a.Length != b.Length) return false;
        for (var r = 0; r < a.Length; r++)
        {
            var (x, y) = (a[r].Split('|'), b[r].Split('|'));
            if (x.Length != y.Length) return false;
            for (var k = 0; k < x.Length; k++)
                if (x[k] != y[k] && !(double.TryParse(x[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.TryParse(y[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var q) && Math.Abs(p - q) <= 1e-11 * Math.Max(Math.Abs(p), Math.Abs(q)) + 1e-12)) return false;
        }
        return true;
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void A_function_written_out_from_others_fires_on_its_targets_only_and_gives_DuckDBs_value(int index)
    {
        var t = FunctionTemplates.All[index];
        using var c = ProbeTable();
        foreach (var probe in t.Probes)
        {
            var sql = $"SELECT id, {probe} AS v FROM probe";
            var expected = Rows(c, sql);
            foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
            {
                var r = TargetRules.Apply(sql, target);
                if (!t.Targets.Contains(target)) { if (t.Unit == null) Assert.DoesNotContain(t.Rule, r.Rules); continue; }
                Assert.Contains(t.Rule, r.Rules);
                if (t.Unit == null) Assert.DoesNotMatch($@"\b{t.Function}\s*\(", r.Sql.ToLowerInvariant().Replace("starts_with", "").Replace("ends_with(", "(").Replace("ddb_atn2", ""));       // the call is gone (the name is not written again)
                Assert.True(SameWithin(expected, Rows(c, r.Sql)), $"{probe} on {target}: DuckDB [{expected}], rewritten [{Rows(c, r.Sql)}]");
                var text = TargetRules.Finish(Polyglot.TranspileOne(r.Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!, target);
                Assert.DoesNotContain("DDB_", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Every_function_the_table_writes_out_has_a_catalog_entry_that_says_where_it_is_needed()
    {
        foreach (var rule in FunctionTemplates.RuleNames)
        {
            var entry = DbDataBuild.Models.RewriteCatalog.Find(rule);
            Assert.NotNull(entry);
            var targets = FunctionTemplates.All.Where(t => t.Rule == rule).SelectMany(t => t.Targets).Distinct().Order().ToList();
            Assert.Equal(targets, entry!.RequiredOn.Order());                     // the engine lacks the function: without the rule the call fails
        }
    }

    [Fact]
    public void The_atan2_marker_becomes_ATN2_and_the_hyperbolics_leave_PostgreSQL_alone()
    {
        Assert.Contains("ATN2(", TargetRules.Finish(Polyglot.TranspileOne(TargetRules.Apply("SELECT atan2(d, 1.0) AS v FROM probe", "sqlserver").Sql, Dialects.Canonical, Dialects.ForTarget("sqlserver")).Sql!, "sqlserver"));
        Assert.Empty(TargetRules.Apply("SELECT cosh(d) AS v FROM probe", "postgres").Rules);
        Assert.Empty(TargetRules.Apply("SELECT atan2(d, 1.0) AS v FROM probe", "postgres").Rules);
        Assert.Empty(TargetRules.Apply("SELECT cbrt(d) AS v FROM probe", "postgres").Rules);
    }

    [Fact]
    public void A_native_fidelity_project_keeps_the_rules_an_engine_needs()
    {
        var policy = RewriteCatalog_Native("sqlserver");
        Assert.Contains(TargetRules.HyperbolicFunctionsRule, TargetRules.Apply("SELECT cosh(d) AS v FROM probe", "sqlserver", policy).Rules);     // SQL Server has no COSH: the rule is not optional
        Assert.Contains("log-base-ten", policy.Disabled);                                                                                              // the one that only keeps the answer equal is
    }

    private static DbDataBuild.Core.RewritePolicy RewriteCatalog_Native(string target) =>
        DbDataBuild.Models.RewriteCatalog.Resolve(new DbDataBuild.Models.RewriteSettings("native", [], [], 1), null, [target], "m", null);

    // ---- FLOAT (32 bits) ----

    private static string LoweredFloat(string sql)
    {
        using var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE f (id INTEGER, x FLOAT, y FLOAT, d DOUBLE)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = QueryDescriber.PlanStatement(sql);
        return PlanLowerer.Lower((string)cmd.ExecuteScalar()!).Sql;
    }

    [Theory]
    [InlineData("SELECT x / y AS v FROM f", "CAST((CAST(x AS DOUBLE) / CAST(y AS DOUBLE)) AS FLOAT)")]
    [InlineData("SELECT round(x, 2) AS v FROM f", "CAST(round(CAST(CAST(x AS FLOAT) AS DOUBLE), 2) AS FLOAT)")]
    [InlineData("SELECT round(x) AS v FROM f", "CAST(round(CAST(CAST(x AS FLOAT) AS DOUBLE)) AS FLOAT)")]
    [InlineData("SELECT trunc(x, 1) AS v FROM f", "CAST(trunc(CAST(CAST(x AS FLOAT) AS DOUBLE), 1) AS FLOAT)")]
    [InlineData("SELECT trunc(d, 1) AS v FROM f", "trunc(CAST(d AS DOUBLE), 1)")]
    public void A_FLOAT_is_computed_in_double_and_cut_back_to_32_bits_as_the_lowered_text_says(string sql, string expected)
    {
        Assert.Contains(expected, LoweredFloat(sql));
    }

    [Fact]
    public void A_FLOAT_cast_is_REAL_on_both_engines_and_a_rounded_FLOAT_is_widened_first()
    {
        var round = LoweredFloat("SELECT round(x, 2) AS v FROM f");
        Assert.Contains("CAST(x AS REAL)", Transpiled("SELECT CAST(x AS FLOAT) AS v FROM f", "sqlserver"));
        Assert.Contains("CAST(x AS REAL)", Transpiled("SELECT CAST(x AS FLOAT) AS v FROM f", "postgres"));
        Assert.Contains("CAST(CAST(x AS REAL) AS FLOAT) * 100.0", Transpiled(round, "sqlserver").Replace("  ", " "));         // not REAL * 100.0, which is 100.5 for 1.005 and rounds to 1.01
    }

    [Theory]
    [InlineData("SELECT id, trunc(CAST(d AS DOUBLE), 1) AS v FROM t2")]
    [InlineData("SELECT id, trunc(CAST(d AS DOUBLE), 0) AS v FROM t2")]
    [InlineData("SELECT id, trunc(CAST(d AS DOUBLE), 3) AS v FROM t2")]
    public void Trunc_with_digits_is_the_scaled_cut_on_PostgreSQL_and_gives_DuckDBs_value(string sql)
    {
        using var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE t2 (id INTEGER, d DOUBLE)");
        Run(c, "INSERT INTO t2 VALUES (1, 2.5), (2, -2.5), (3, 2.9999999999999996), (4, 1.15), (5, 0.285), (6, NULL), (7, 123456.789), (8, -0.0001), (9, 1e15)");
        var r = TargetRules.Apply(sql, "postgres");
        Assert.Equal([TargetRules.TruncDigits], r.Rules);
        Assert.Equal(Rows(c, sql), Rows(c, r.Sql));
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);                                  // ROUND(x, n, 1) is the engine's own
    }

    // ---- strftime and format ----

    private static string LoweredText(string sql)
    {
        using var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE g (ts TIMESTAMP, dt DATE, s VARCHAR, i INTEGER, d DOUBLE, tz TIMESTAMPTZ)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = QueryDescriber.PlanStatement(sql);
        return PlanLowerer.Lower((string)cmd.ExecuteScalar()!).Sql;
    }

    [Fact]
    public void Strftime_is_written_out_as_pieces_of_text()
    {
        var sql = LoweredText("SELECT strftime(ts, '%Y-%m-%d %A') AS v FROM g");
        Assert.Contains("right('000' || CAST(date_part('year', ts) AS VARCHAR), 4)", sql);
        Assert.Contains("WHEN 1 THEN 'Monday'", sql);
        Assert.StartsWith("SELECT CASE WHEN ts IS NULL THEN NULL", sql);
        Assert.Contains("CAST(dt AS TIMESTAMP)", LoweredText("SELECT strftime(dt, '%H') AS v FROM g"));
    }

    [Theory]
    [InlineData("SELECT strftime(ts, '%U') AS v FROM g", "%U")]
    [InlineData("SELECT strftime(ts, '%Y %V') AS v FROM g", "%V")]
    [InlineData("SELECT strftime(tz, '%Y') AS v FROM g", "TIMESTAMP WITH TIME ZONE")]
    [InlineData("SELECT format('{:05d}', i) AS v FROM g", "{:05d}")]
    [InlineData("SELECT format('{}', d) AS v FROM g", "DOUBLE")]
    public void What_has_no_exact_text_is_refused_with_the_specifier_or_type_named(string sql, string named)
    {
        var ex = Assert.Throws<LoweringException>(() => LoweredText(sql));
        Assert.Contains(named, ex.Message);
    }

    [Fact]
    public void Format_with_placeholders_is_a_concatenation_of_text()
    {
        var sql = LoweredText("SELECT format('{}|{}!', s, i) AS v FROM g");
        Assert.Contains("(s || '|' || CAST(CAST(i AS BIGINT) AS VARCHAR) || '!')", sql);
    }

    // ---- age() ----

    public static readonly string[] AgeStamps =
    [
        "1900-02-28 00:00:00", "1900-03-01 10:30:45.5", "1999-12-31 23:59:59.999999", "2000-02-28 08:15:30", "2000-02-29 00:00:00.000001", "2000-03-01 12:00:00", "2023-01-31 00:00:00", "2023-02-28 23:59:59.999999",
        "2023-03-31 10:30:45.5", "2023-04-30 08:15:30", "2023-12-31 12:00:00", "2024-01-30 00:00:00", "2024-01-31 08:15:30", "2024-02-01 00:00:00.000001", "2024-02-28 23:59:59.999999", "2024-02-29 00:00:00", "2024-02-29 10:30:45.5",
        "2024-03-01 00:00:00", "2024-03-01 12:00:00", "2024-03-30 08:15:30", "2024-03-31 00:00:00", "2024-03-31 23:59:59.999999", "2024-04-30 10:30:45.5", "2024-05-31 12:00:00", "2024-12-31 00:00:00", "2025-01-31 08:15:30",
        "2025-02-28 00:00:00.000001", "2025-03-01 10:30:45.5", "2025-03-31 23:59:59.999999", "2100-02-28 12:00:00", "2100-03-01 00:00:00",
    ];

    private static DuckDBConnection AgePairs()
    {
        var c = ProbeTable();
        Run(c, "CREATE TABLE ages (a TIMESTAMP, b TIMESTAMP)");
        Run(c, "INSERT INTO ages SELECT x.s, y.s FROM (SELECT CAST(unnest AS TIMESTAMP) AS s FROM unnest([" + string.Join(", ", AgeStamps.Select(t => $"'{t}'")) + "])) x CROSS JOIN (SELECT CAST(unnest AS TIMESTAMP) AS s FROM unnest([" + string.Join(", ", AgeStamps.Select(t => $"'{t}'")) + "])) y");
        return c;
    }

    public static IEnumerable<object[]> AgeShapes() => new[]
    {
        "date_part('year', age(a, b))", "date_part('month', age(a, b))", "date_part('day', age(a, b))", "date_part('hour', age(a, b))", "date_part('minute', age(a, b))", "date_part('second', age(a, b))",
        "normalized_interval(age(a, b)) > normalized_interval(CAST('1 year' AS INTERVAL))", "normalized_interval(age(a, b)) >= normalized_interval(INTERVAL 30 DAY)",
        "normalized_interval(age(a, b)) = normalized_interval(INTERVAL 1 MONTH)", "normalized_interval(age(a, b)) < normalized_interval(INTERVAL 0 DAY)",
        "normalized_interval(age(a, b)) < normalized_interval(age(b, CAST('2024-01-01' AS TIMESTAMP)))", "normalized_interval(age(a, b)) <> normalized_interval(CAST('1 month 2 days 03:04:05' AS INTERVAL))",
    }.Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(AgeShapes))]
    public void Age_written_out_in_T_SQL_terms_gives_DuckDBs_value_on_every_pair(string expression)
    {
        using var c = AgePairs();
        var sql = $"SELECT a, b, {expression} AS v FROM ages";
        var r = TargetRules.Apply(sql, "sqlserver");
        Assert.Contains(TargetRules.AgeParts, r.Rules);
        Assert.DoesNotContain("age(", r.Sql.ToLowerInvariant().Replace("last_day", ""));
        Assert.DoesNotContain("normalized_interval", r.Sql);
        Assert.Equal(Rows(c, sql), Rows(c, r.Sql));
    }

    [Fact]
    public void Age_on_PostgreSQL_loses_the_wrapper_and_the_fraction_of_the_second()
    {
        using var c = AgePairs();
        var wrapped = "SELECT a, b, normalized_interval(age(a, b)) > normalized_interval(CAST('1 year' AS INTERVAL)) AS v FROM ages";
        var r = TargetRules.Apply(wrapped, "postgres");
        Assert.Equal([TargetRules.NormalizedIntervalStrip], r.Rules);
        Assert.DoesNotContain("normalized_interval", r.Sql);
        Assert.Equal(Rows(c, wrapped), Rows(c, r.Sql));
        var second = TargetRules.Apply("SELECT date_part('second', age(a, b)) AS v FROM ages", "postgres");
        Assert.Equal([TargetRules.AgeSecond], second.Rules);
        Assert.Contains("trunc(", second.Sql.ToLowerInvariant());
    }

    [Theory]
    [InlineData("SELECT age(a, b) AS v FROM ages")]                                                // a value
    [InlineData("SELECT date_part('epoch', age(a, b)) AS v FROM ages")]                            // other parts
    [InlineData("SELECT date_part('millisecond', age(a, b)) AS v FROM ages")]
    [InlineData("SELECT a + age(a, b) AS v FROM ages")]                                            // arithmetic
    [InlineData("SELECT date_part('year', age(a, (SELECT max(b) FROM ages))) AS v FROM ages")]     // an operand with a subquery would be repeated
    [InlineData("SELECT date_part('year', age(random() * a, b)) AS v FROM ages")]
    public void The_shapes_age_cannot_be_exact_in_are_left_for_the_matrix_to_refuse_on_SQL_Server(string sql)
    {
        var r = TargetRules.Apply(sql, "sqlserver");
        var linter = new DbDataBuild.Sql.Matrix.MatrixLinter(DbDataBuild.Sql.Matrix.MatrixLoader.LoadEmbedded([]));
        Assert.Contains(linter.Lint(r.Sql, "m.sql", ["sqlserver"]), d => d.Code == "DDB-301" && d.Found.Contains("fn.age"));
        Assert.DoesNotContain(linter.Lint(TargetRules.Apply(sql, "postgres").Sql, "m.sql", ["postgres"]), d => d.Code == "DDB-301");
    }

    [Theory]
    [InlineData("covar_pop")]
    [InlineData("covar_samp")]
    [InlineData("regr_sxy")]
    public void Covariance_on_PostgreSQL_is_the_exact_sums_and_gives_DuckDBs_value(string function)
    {
        using var c = ProbeTable();
        foreach (var sql in new[]
        {
            $"SELECT {function}(a.d, b.d) AS v FROM probe a CROSS JOIN probe b",                                       // the outlier of 1e10 against independent columns: 0
            $"SELECT {function}(i, j) AS v FROM probe",
            $"SELECT {function}(d, i) FILTER (WHERE id > 2) AS v FROM probe",
            $"SELECT {function}(d, d * 2) AS v FROM probe WHERE id < 4",
            $"SELECT {function}(d, i) AS v FROM probe WHERE id = 1",                                                  // one row: 0 or NULL
            $"SELECT {function}(d, i) AS v FROM probe WHERE id = 5",                                                  // no pair: NULL
        })
        {
            var r = TargetRules.Apply(sql, "postgres");
            Assert.Equal([TargetRules.CovarianceExact], r.Rules);
            Assert.True(SameWithin(Rows(c, sql), Rows(c, r.Sql)), $"{sql}: DuckDB [{Rows(c, sql)}], rewritten [{Rows(c, r.Sql)}]");
        }
        Assert.Empty(TargetRules.Apply($"SELECT {function}(d, i) OVER () AS v FROM probe", "postgres").Rules);          // a window is left alone
        Assert.Empty(TargetRules.Apply($"SELECT {function}(d, i) AS v FROM probe", "sqlserver").Rules);
    }
}
