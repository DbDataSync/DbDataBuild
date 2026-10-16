using System.Globalization;
using DbDataBuild.Lowering;
using DbDataBuild.Sql;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rules;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// DuckDB's FLOAT (FLOAT4, REAL) is 32 bits and its DOUBLE (FLOAT8, DOUBLE PRECISION) is 64; FLOAT(n) is the first for n up to 24 and the second for 25 to 53. The rule `float-width` writes the first as REAL
/// and the second as DOUBLE whatever the position (OPEN-ITEMS W-10(a), entry 179). Tested three ways: the text each target gets (here), the rule's DuckDB-dialect output run on DuckDB against the original for
/// values and column types (here), and the engines' values and column types against DuckDB's (`FloatTypeConformance`).
/// </summary>
public class FloatWidthTests
{
    private static string Render(string sql, string target) =>
        TargetRules.Finish(TargetRules.Transpile(TargetRules.Apply(sql, target).Sql, Dialects.Canonical, Dialects.ForTarget(target)).Sql!, target);

    [Theory]
    [InlineData("SELECT CAST(x AS FLOAT) AS v FROM t")]
    [InlineData("SELECT TRY_CAST(x AS FLOAT) AS v FROM t")]
    [InlineData("SELECT x::FLOAT AS v FROM t")]
    [InlineData("SELECT x::FLOAT4 AS v FROM t")]
    [InlineData("SELECT x::REAL AS v FROM t")]
    [InlineData("SELECT CAST(x AS FLOAT(1)) AS v FROM t")]
    [InlineData("SELECT CAST(x AS FLOAT(24)) AS v FROM t")]
    [InlineData("SELECT * FROM (VALUES (CAST(1.5 AS FLOAT))) AS v(a)")]
    [InlineData("CREATE TABLE t (a FLOAT)")]
    [InlineData("CREATE TABLE t (a FLOAT4)")]
    [InlineData("CREATE TABLE t (a REAL)")]
    [InlineData("CREATE TABLE t (a FLOAT(24))")]
    public void A_32_bit_type_is_REAL_on_every_target(string sql)
    {
        foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
        {
            var text = Render(sql, target);
            Assert.Contains("REAL", text);
            Assert.DoesNotMatch(@"\bFLOAT\b|FLOAT[48]|REAL\(", text);
        }
    }

    [Theory]
    [InlineData("SELECT CAST(x AS DOUBLE) AS v FROM t", "FLOAT", "DOUBLE PRECISION")]
    [InlineData("SELECT x::FLOAT8 AS v FROM t", "FLOAT", "DOUBLE PRECISION")]
    [InlineData("SELECT x::DOUBLE PRECISION AS v FROM t", "FLOAT", "DOUBLE PRECISION")]
    [InlineData("SELECT CAST(x AS FLOAT(25)) AS v FROM t", "FLOAT", "DOUBLE PRECISION")]
    [InlineData("SELECT CAST(x AS FLOAT(53)) AS v FROM t", "FLOAT", "DOUBLE PRECISION")]
    [InlineData("CREATE TABLE t (a FLOAT8, b FLOAT(30))", "FLOAT", "DOUBLE PRECISION")]
    public void A_64_bit_type_stays_64_bits(string sql, string sqlServer, string postgres)
    {
        var tsql = Render(sql, "sqlserver");
        Assert.Contains(sqlServer, tsql);
        Assert.DoesNotContain("REAL", tsql);
        Assert.DoesNotContain("FLOAT8", tsql);
        Assert.Contains(postgres, Render(sql, "postgres"));
        Assert.DoesNotContain("REAL", Render(sql, "postgres"));
        Assert.DoesNotContain("REAL", Render(sql, "fabric"));
    }

    [Fact]
    public void A_mixed_column_definition_gets_each_width()
    {
        Assert.Equal("CREATE TABLE t (a REAL, b REAL, c REAL, d REAL, e FLOAT, f FLOAT, g FLOAT)", Render("CREATE TABLE t (a FLOAT, b REAL, c FLOAT4, d FLOAT(24), e DOUBLE, f FLOAT8, g FLOAT(53))", "sqlserver"));
        Assert.Equal("CREATE TABLE t (a REAL, b REAL, c REAL, d REAL, e DOUBLE PRECISION, f DOUBLE PRECISION, g DOUBLE PRECISION)", Render("CREATE TABLE t (a FLOAT, b REAL, c FLOAT4, d FLOAT(24), e DOUBLE, f FLOAT8, g FLOAT(53))", "postgres"));
    }

    [Fact]
    public void The_rule_is_named_for_the_targets_that_have_it_and_a_query_without_a_FLOAT_does_not_fire_it()
    {
        foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
        {
            Assert.Contains(TargetRules.FloatWidth, TargetRules.Apply("SELECT CAST(x AS FLOAT) AS v FROM t", target).Rules);
            Assert.Contains(TargetRules.FloatWidth, TargetRules.Apply("SELECT x::FLOAT8 AS v FROM t", target).Rules);
            Assert.DoesNotContain(TargetRules.FloatWidth, TargetRules.Apply("SELECT CAST(x AS DOUBLE) AS v FROM t", target).Rules);
            Assert.DoesNotContain(TargetRules.FloatWidth, TargetRules.Apply("SELECT CAST(x AS REAL) AS v FROM t", target).Rules);
        }
        Assert.DoesNotContain(TargetRules.FloatWidth, TargetRules.Apply("SELECT CAST(x AS FLOAT) AS v FROM t", "oracle").Rules);
    }

    [Fact]
    public void A_precision_DuckDB_refuses_is_left_alone_and_DuckDB_is_the_one_that_says_no()
    {
        // FLOAT(0) and FLOAT(54) are DuckDB parse errors (checked below), so no model reaches the target step with one
        Assert.Empty(TargetRules.Apply("SELECT CAST(x AS FLOAT(54)) AS v FROM t", "sqlserver").Rules);
        using var c = PlanningConnection.Open(lockDown: false);
        foreach (var bits in new[] { 0, 54 })
        {
            var ex = Assert.Throws<DuckDBException>(() => Run(c, $"SELECT CAST(1 AS FLOAT({bits}))"));
            Assert.Contains("precision for type float", ex.Message);
        }
    }

    // ---- the differential: the rule's output, in DuckDB's dialect, run on DuckDB against the original ----

    private static (List<string> Types, List<string> Rows) Run(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var types = Enumerable.Range(0, r.FieldCount).Select(i => r.GetDataTypeName(i)).ToList();
        var rows = new List<string>();
        while (r.Read()) rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
        rows.Sort(StringComparer.Ordinal);
        return (types, rows);
    }

    private static DuckDBConnection Open()
    {
        var c = PlanningConnection.Open(lockDown: false);
        Run(c, "CREATE TABLE t (id INTEGER, d DOUBLE, i INTEGER, s VARCHAR)");
        Run(c, "INSERT INTO t VALUES (1, 0.1, 16777217, '0.1'), (2, 1.005, 2147483647, '16777217'), (3, -2.5, 0, '1e10'), (4, NULL, NULL, NULL), (5, 10000000000, -7, '3.4e39')");
        return c;
    }

    public static IEnumerable<object[]> Queries() => new[]
    {
        "SELECT id, CAST(d AS FLOAT) AS v FROM t",
        "SELECT id, CAST(i AS FLOAT) AS v FROM t",
        "SELECT id, d::FLOAT4 AS v FROM t",
        "SELECT id, d::REAL AS v FROM t",
        "SELECT id, CAST(d AS FLOAT(24)) AS v FROM t",
        "SELECT id, CAST(i AS FLOAT(25)) AS v FROM t",
        "SELECT id, CAST(i AS FLOAT(53)) AS v FROM t",
        "SELECT id, i::FLOAT8 AS v FROM t",
        "SELECT id, i::DOUBLE PRECISION AS v FROM t",
        "SELECT CAST(0.1 AS FLOAT) AS v, CAST(16777217 AS FLOAT) AS w",
        "SELECT CAST(CAST(0.1 AS FLOAT) AS DOUBLE) AS v",
        "SELECT CAST(CAST(16777217 AS FLOAT) AS DOUBLE) AS v",
        "SELECT CAST(CAST(0.1 AS FLOAT(24)) AS DOUBLE) AS v, CAST(CAST(0.1 AS FLOAT(25)) AS DOUBLE) AS w",
        "SELECT id, CAST(CAST(d AS FLOAT) AS DOUBLE) AS v FROM t",
        "SELECT id, TRY_CAST(s AS FLOAT) AS v FROM t",
        "SELECT CAST(0.1 AS FLOAT) AS f, CAST(0.1 AS DOUBLE) AS g",
    }.Select(q => new object[] { q });

    [Theory]
    [MemberData(nameof(Queries))]
    public void The_rules_output_gives_DuckDB_the_same_values_and_column_types(string sql)
    {
        using var c = Open();
        var expected = Run(c, sql);
        foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
        {
            var actual = Run(c, TargetRules.Apply(sql, target).Sql);
            Assert.Equal(expected.Types, actual.Types);
            Assert.Equal(expected.Rows, actual.Rows);
        }
    }

    [Theory]
    [InlineData("CREATE TABLE w1 (a FLOAT, b REAL, c FLOAT4, d FLOAT(24), e DOUBLE, f FLOAT8, g FLOAT(53), h FLOAT(25))")]
    public void The_rules_output_creates_the_same_column_types_on_DuckDB(string sql)
    {
        using var c = Open();
        Run(c, sql);
        var expected = Run(c, "SELECT typeof(a), typeof(b), typeof(c), typeof(d), typeof(e), typeof(f), typeof(g), typeof(h) FROM (SELECT * FROM w1 UNION ALL BY NAME SELECT NULL) LIMIT 1");
        Run(c, "DROP TABLE w1");
        Run(c, TargetRules.Apply(sql, "sqlserver").Sql);
        Assert.Equal(expected.Rows, Run(c, "SELECT typeof(a), typeof(b), typeof(c), typeof(d), typeof(e), typeof(f), typeof(g), typeof(h) FROM (SELECT * FROM w1 UNION ALL BY NAME SELECT NULL) LIMIT 1").Rows);
        Assert.Equal("FLOAT|FLOAT|FLOAT|FLOAT|DOUBLE|DOUBLE|DOUBLE|DOUBLE", expected.Rows[0]);
    }

    [Fact]
    public void The_lowered_form_of_a_FLOAT_cast_and_of_a_wide_FLOAT_goes_through_the_rule_to_the_same_types()
    {
        using var c = Open();
        foreach (var sql in new[] { "SELECT id, CAST(d AS FLOAT) AS v FROM t", "SELECT id, CAST(d AS FLOAT(30)) AS v FROM t", "SELECT * FROM (VALUES (CAST(1.1 AS FLOAT)), (CAST(2 AS FLOAT))) AS v(a)" })
        {
            string plan;
            using (var cmd = c.CreateCommand()) { cmd.CommandText = QueryDescriber.PlanStatement(sql); plan = (string)cmd.ExecuteScalar()!; }
            var names = new List<string>();
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "DESCRIBE " + sql; using var r = cmd.ExecuteReader(); while (r.Read()) names.Add(r.GetString(0)); }
            var lowered = PlanLowerer.Lower(plan, names).Sql;
            var expected = Run(c, sql);
            foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
            {
                var actual = Run(c, TargetRules.Apply(lowered, target).Sql);
                Assert.Equal(expected.Types, actual.Types);
                Assert.Equal(expected.Rows, actual.Rows);
                var text = Render(lowered, target);
                if (sql.Contains("FLOAT(30)")) Assert.DoesNotContain("REAL", text);
                else Assert.DoesNotMatch(@"\bFLOAT\b", text);
            }
        }
    }
}
