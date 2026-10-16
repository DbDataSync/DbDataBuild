using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// DuckDB's FLOAT (also FLOAT4 and REAL) is 32 bits; DOUBLE (FLOAT8, DOUBLE PRECISION) is 64. Through the tool's own pipeline (lowering, target rules, transpile) on both engines against DuckDB: the values
/// (a FLOAT widened to DOUBLE shows the 32-bit rounding: 0.1 is 0.100000001490116 and 16777217 is 16777216) and the column type from `sys.dm_exec_describe_first_result_set` and `pg_typeof`
/// (OPEN-ITEMS W-10(a), entry 179).
/// </summary>
public class FloatTypeConformance
{
    private static readonly (string Id, string Sql)[] Cases =
    [
        ("cast_column", "SELECT id, CAST(d AS FLOAT) AS f FROM probe"),
        ("cast_integer_column", "SELECT id, CAST(i AS FLOAT) AS f FROM probe"),
        ("cast_decimal_column", "SELECT id, CAST(n AS FLOAT) AS f FROM probe"),
        ("cast_text_column", "SELECT CAST(s AS FLOAT) AS f FROM (VALUES ('1.5'), ('0.1'), ('16777217')) AS v(s)"),
        ("double_colon", "SELECT id, d::FLOAT AS f FROM probe"),
        ("float4", "SELECT id, d::FLOAT4 AS f FROM probe"),
        ("real", "SELECT id, d::REAL AS f FROM probe"),
        ("float_n", "SELECT id, CAST(d AS FLOAT(24)) AS f FROM probe"),
        ("float_n_wide", "SELECT id, CAST(d AS FLOAT(53)) AS f FROM probe"),
        ("try_cast", "SELECT id, TRY_CAST(d AS FLOAT) AS f FROM probe"),
        ("try_cast_text", "SELECT TRY_CAST(s AS FLOAT) AS f FROM (VALUES ('1.5'), ('0.1'), ('x'), (NULL)) AS v(s)"),
        ("literal", "SELECT CAST(0.1 AS FLOAT) AS f, CAST(16777217 AS FLOAT) AS g"),
        ("to_double_0_1", "SELECT CAST(CAST(0.1 AS FLOAT) AS DOUBLE) AS v"),
        ("to_double_16777217", "SELECT CAST(CAST(16777217 AS FLOAT) AS DOUBLE) AS v"),
        ("to_double_column", "SELECT id, CAST(CAST(d AS FLOAT) AS DOUBLE) AS v FROM probe"),
        ("to_double_big_integer", "SELECT CAST(CAST(i AS FLOAT) AS DOUBLE) AS v FROM probe"),
        ("cast_float_to_integer", "SELECT id, CAST(CAST(d AS FLOAT) AS INTEGER) AS v FROM probe WHERE id <> 8"),
        ("float_in_cte", "WITH c AS (SELECT id, CAST(d AS FLOAT) AS f FROM probe) SELECT id, f FROM c"),
        ("float_in_subquery", "SELECT id, f FROM (SELECT id, CAST(d AS FLOAT) AS f FROM probe) AS q WHERE f > 0"),
        ("float_in_union", "SELECT CAST(d AS FLOAT) AS f FROM probe UNION ALL SELECT CAST(0.1 AS FLOAT)"),
        ("float_aggregate_min", "SELECT min(CAST(d AS FLOAT)) AS lo, max(CAST(d AS FLOAT)) AS hi FROM probe"),
        ("float_coalesce", "SELECT id, coalesce(CAST(d AS FLOAT), CAST(0.1 AS FLOAT)) AS f FROM probe"),
        ("float_case", "SELECT id, CASE WHEN id > 3 THEN CAST(d AS FLOAT) ELSE CAST(0.1 AS FLOAT) END AS f FROM probe"),
        ("float_plus_float", "SELECT id, CAST(d AS FLOAT) + CAST(0.1 AS FLOAT) AS f FROM probe"),
        ("float_times_integer", "SELECT id, CAST(d AS FLOAT) * 3 AS f FROM probe"),
        ("float_divided", "SELECT id, CAST(d AS FLOAT) / 3 AS f FROM probe"),
        ("float_rounded", "SELECT id, round(CAST(d AS FLOAT), 2) AS f FROM probe"),
        ("float_negated", "SELECT id, -CAST(d AS FLOAT) AS f FROM probe"),
        ("float_sum", "SELECT sum(CAST(d AS FLOAT)) AS f FROM probe"),
        ("float_avg", "SELECT avg(CAST(d AS FLOAT)) AS f FROM probe"),
        ("float_nullif", "SELECT id, nullif(CAST(d AS FLOAT), CAST(0.5 AS FLOAT)) AS f FROM probe"),
        ("float_sqrt", "SELECT id, sqrt(abs(CAST(d AS FLOAT))) AS f FROM probe"),
        ("float_compare", "SELECT id FROM probe WHERE CAST(d AS FLOAT) = CAST(0.5 AS FLOAT)"),
        ("double_stays_double", "SELECT id, CAST(i AS DOUBLE) AS f FROM probe"),
        ("float8_stays_double", "SELECT id, i::FLOAT8 AS f FROM probe"),
        ("double_precision_stays_double", "SELECT id, CAST(i AS DOUBLE PRECISION) AS f FROM probe"),
        ("both_widths", "SELECT CAST(d AS FLOAT) AS f, CAST(d AS DOUBLE) AS g FROM probe"),
    ];

    /// <summary>A column definition written in DuckDB's dialect goes through the same step (the transpile alone writes FLOAT, which is 8 bytes on SQL Server, and REAL(24) or FLOAT8).</summary>
    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task Column_definitions_get_32_bits_for_FLOAT_and_64_for_DOUBLE(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        const string ddl = "CREATE TABLE float_width_cols (a FLOAT, b REAL, c FLOAT4, d FLOAT(24), e DOUBLE, f FLOAT8, g FLOAT(53), h FLOAT(25))";
        var rendered = DbDataBuild.Targets.Rules.TargetRules.Finish(DbDataBuild.Sql.Polyglot.TranspileOne(DbDataBuild.Targets.Rules.TargetRules.Apply(ddl, name).Sql, DbDataBuild.Sql.Dialects.Canonical, DbDataBuild.Sql.Dialects.ForTarget(name)).Sql!, name);
        await engine.QueryAsync("DROP TABLE IF EXISTS float_width_cols");
        await engine.QueryAsync(rendered);
        try
        {
            string actual;
            if (name == "sqlserver")
            {
                var described = await engine.QueryAsync("SELECT system_type_name FROM sys.dm_exec_describe_first_result_set(N'SELECT * FROM float_width_cols', NULL, 0) ORDER BY column_ordinal");
                actual = string.Join(",", described.Rows.Select(r => ((string)r[0]!).ToLowerInvariant()));
                Assert.Equal("real,real,real,real,float,float,float,float", actual);
            }
            else
            {
                var described = await engine.QueryAsync("SELECT string_agg(format_type(atttypid, atttypmod), ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'float_width_cols'::regclass AND attnum > 0");
                actual = (string)described.Rows[0][0]!;
                Assert.Equal("real,real,real,real,double precision,double precision,double precision,double precision", actual);
            }
        }
        finally { await engine.QueryAsync("DROP TABLE IF EXISTS float_width_cols"); }
    }

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task FLOAT_is_32_bits_and_DOUBLE_stays_64(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        await probe.CreateTableAsync();
        var problems = new List<string>();
        foreach (var (id, sql) in Cases)
        {
            var expected = probe.OnDuckDb(sql);
            var (rendered, refused) = probe.Render(sql);
            if (rendered == null) { problems.Add($"{id}: refused ({refused})"); continue; }
            var actual = await probe.OnEngineAsync(rendered);
            if (expected.Error != null || actual.Error != null) { problems.Add($"{id}: duckdb [{expected.Error}] engine [{actual.Error}]  {rendered.Replace("\n", " ")}"); continue; }
            if (actual.Rows != expected.Rows) problems.Add($"{id}: rows differ, duckdb [{expected.Rows}] engine [{actual.Rows}]  {rendered.Replace("\n", " ")}");
            var typeProblem = await ValuesListConformance.TypesDifferAsync(engine, probe, sql, rendered);
            if (typeProblem != null) problems.Add($"{id}: {typeProblem}  {rendered.Replace("\n", " ")}");
        }
        Assert.True(problems.Count == 0, name + ":\n" + string.Join("\n", problems));
    }
}
