using System.Text;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// VALUES lists through the tool's own pipeline (lowering from DuckDB's EXPRESSION_GET, the target rule values-chunks, transpile) on both engines against DuckDB: the rows, the column names and the column types
/// (SQL Server's `sys.dm_exec_describe_first_result_set`, PostgreSQL's `pg_typeof` and `scale`), over NULLs, mixed integer and decimal cells, text with quotes and non-BMP characters, dates, timestamps, booleans,
/// expressions in cells, one row, 1000 / 1001 / many rows, and a list as a join side, in a CTE, in a subquery and in IN (OPEN-ITEMS W-3, entry 178).
/// </summary>
public class ValuesListConformance
{
    private static string Rows(int count) =>
        string.Join(", ", Enumerable.Range(1, count).Select(i => $"({i}, 's{i}', {(i % 7 == 0 ? "NULL" : $"{i}.25")}, {(i % 3 == 0 ? "NULL" : $"DATE '2024-01-{1 + i % 28:00}'")})"));

    /// <summary>Rows with a date plus an interval of months, a date plus a number of days, and a timestamp plus an interval of hours (OPEN-ITEMS W-10(b), entry 180).</summary>
    internal static string IntervalRows(int count) =>
        string.Join(", ", Enumerable.Range(1, count).Select(i => $"(DATE '2024-01-31' + INTERVAL {i % 5 + 1} MONTH, {i}, DATE '2024-03-01' + {i % 9}, TIMESTAMP '2024-01-31 10:00:00' + INTERVAL '{i % 3} hours')"));

    private static readonly (string Id, string Sql)[] Cases =
    [
        ("nulls", "SELECT * FROM (VALUES (1, 'a'), (2, NULL), (NULL, 'c')) AS v(id, s)"),
        ("inline_values", "VALUES (1, 'a'), (2, 'b')"),
        ("no_column_list", "SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v"),
        ("one_row", "SELECT * FROM (VALUES (1)) AS v(id)"),
        ("one_row_many_columns", "SELECT * FROM (VALUES (1, 'a', 2.5, DATE '2024-02-29', TIMESTAMP '2024-02-29 23:59:59.123456', true)) AS v(a, b, c, d, e, f)"),
        ("int_and_decimal", "SELECT * FROM (VALUES (1, 2.5), (2, 3), (NULL, 1 + 1)) AS v(a, b)"),
        ("decimal_scales", "SELECT * FROM (VALUES (1.5), (2.25), (NULL), (-100.125)) AS v(a)"),
        ("wide_decimal", "SELECT * FROM (VALUES (12345678901234567890.123456789), (1)) AS v(a)"),
        ("widening_to_bigint", "SELECT * FROM (VALUES (1), (2147483648), (-9000000000)) AS v(a)"),
        ("smallint_and_float", "SELECT * FROM (VALUES (CAST(7 AS SMALLINT), CAST(1.5 AS FLOAT)), (CAST(-3 AS SMALLINT), CAST(2 AS FLOAT))) AS v(a, b)"),
        ("doubles", "SELECT * FROM (VALUES (1.5e0), (2), (-1e10), (NULL)) AS v(a)"),
        ("text_edge", "SELECT * FROM (VALUES ('héllo 😀', ''), ('a''b', ' padded '), (NULL, 'x')) AS v(a, b)"),
        ("long_text", $"SELECT * FROM (VALUES ('{new string('x', 200)}', 1), (NULL, 2)) AS v(a, b)"),
        ("dates_timestamps_booleans", "SELECT * FROM (VALUES (DATE '2024-02-29', TIMESTAMP '2024-02-29 23:59:59.123456', true), (NULL, NULL, NULL), (DATE '1999-12-31', TIMESTAMP '1999-12-31 00:00:00', false)) AS v(d, ts, b)"),
        ("expressions_in_cells", "SELECT * FROM (VALUES (1 + 2, abs(-3), upper('a') || 'b', CASE WHEN 1 = 1 THEN 10 ELSE 20 END, DATE '2024-01-31' + INTERVAL 1 MONTH)) AS v(a, b, c, d, e)"),
        ("boolean_expressions", "SELECT * FROM (VALUES (1 = 1, 2 > 3), (NULL, 1 < 2), (1 IS NULL, NULL IS NULL)) AS v(a, b)"),
        ("boolean_mixed", "SELECT * FROM (VALUES (true), (1 = 1), (NULL)) AS v(a)"),
        ("float_cells", "SELECT * FROM (VALUES (CAST(1.1 AS FLOAT)), (CAST(2 AS FLOAT)), (NULL)) AS v(a)"),
        ("date_plus_interval", "SELECT * FROM (VALUES (DATE '2024-01-31' + INTERVAL 1 MONTH, 1), (DATE '2024-03-31' + INTERVAL 1 MONTH, 2)) AS v(d, n)"),
        ("date_plus_days", "SELECT * FROM (VALUES (DATE '2024-01-31' + 3), (DATE '2024-02-28' + 2)) AS v(d)"),
        ("interval_unit_cells", "SELECT * FROM (VALUES (DATE '2024-01-31' + INTERVAL 1 YEAR, 1), (DATE '2024-01-31' + INTERVAL 1 QUARTER, 2), (DATE '2024-01-31' + INTERVAL 1 WEEK, 3), (DATE '2024-02-29' + INTERVAL 12 MONTH, 4), (CAST(NULL AS DATE) + INTERVAL 1 MONTH, 5)) AS v(d, n)"),
        ("interval_subtract_cells", "SELECT * FROM (VALUES (DATE '2024-01-31' - INTERVAL 1 MONTH, 1), (DATE '2024-03-31' - INTERVAL '2 days', 2), (DATE '2024-03-31' - 4, 3)) AS v(d, n)"),
        ("interval_text_cells", "SELECT * FROM (VALUES (TIMESTAMP '2024-01-31 10:00:00' + INTERVAL '1 day 2 hours', 1), (TIMESTAMP '2024-03-31 00:00:00' + INTERVAL '3 hours', 2), (DATE '2024-01-31' + INTERVAL '1 month 2 days 03:04:05', 3)) AS v(d, n)"),
        ("interval_leading_and_nested_cells", "SELECT * FROM (VALUES (INTERVAL 1 DAY + DATE '2024-01-31', 1), ((DATE '2024-01-31' + INTERVAL 1 MONTH) + INTERVAL 2 DAY, 2), (3 + DATE '2024-01-31', 3)) AS v(d, n)"),
        ("interval_times_number_cells", "SELECT * FROM (VALUES (DATE '2024-01-31' + INTERVAL 1 DAY * 3, 1), (DATE '2024-01-31' + 2 * INTERVAL 1 WEEK, 2), (TIMESTAMP '2024-01-31 00:00:00' + INTERVAL 90 MINUTE * 2, 3)) AS v(d, n)"),
        ("interval_cells_in_join", "SELECT p.id, v.d FROM probe p JOIN (VALUES (1, DATE '2024-01-31' + INTERVAL 1 MONTH), (2, DATE '2024-03-31' + 3)) AS v(k, d) ON p.id = v.k"),
        ("function_cells", "SELECT * FROM (VALUES (ceil(1.2), 'a' || 'b', CAST(length('a ') AS BIGINT), 7 // 2, 1 / 2.0, round(CAST(2.675 AS DOUBLE), 2), last_day(DATE '2024-01-15'), date_diff('week', DATE '2024-01-01', DATE '2024-02-01'), CAST(DATE '2024-03-01' - DATE '2024-01-01' AS BIGINT), CAST(year(DATE '2024-05-05') AS BIGINT))) AS v(a, b, c, d, e, f, g, h, i, j)"),
        ("function_cells_many_rows", $"SELECT * FROM (VALUES {string.Join(", ", Enumerable.Range(1, 1200).Select(i => $"({i}, upper('a') || 'b{i}', CAST(length('x{i} ') AS BIGINT), ceil({i}.2), CAST(DATE '2024-03-01' - DATE '2024-01-{1 + i % 28:00}' AS BIGINT))"))}) AS v(a, b, c, d, e)"),
        ("interval_cells_1001", $"SELECT * FROM (VALUES {IntervalRows(1001)}) AS v(d, n, e, t)"),
        ("interval_cells_5000", $"SELECT * FROM (VALUES {IntervalRows(5000)}) AS v(d, n, e, t)"),
        ("lateral_cross", "SELECT p.id, v.x, v.y FROM probe p, LATERAL (VALUES (p.i, 1), (p.j + 1, 2)) AS v(x, y)"),
        ("lateral_two_outer_columns", "SELECT p.id, v.x, v.y FROM probe p CROSS JOIN LATERAL (VALUES (p.i, p.j), (p.j, p.i), (NULL, 0)) AS v(x, y)"),
        ("lateral_left_join", "SELECT p.id, v.x, v.y FROM probe p LEFT JOIN LATERAL (VALUES (p.i, p.j), (p.j, p.i)) AS v(x, y) ON TRUE"),
        ("lateral_nulls_filtered", "SELECT p.id, v.x FROM probe p, LATERAL (VALUES (p.i), (NULL), (p.j + 1)) AS v(x) WHERE v.x IS NOT NULL"),
        ("lateral_one_row", "SELECT p.id, v.x FROM probe p, LATERAL (VALUES (p.i)) AS v(x)"),
        ("lateral_text", "SELECT p.id, v.x, v.n FROM probe p, LATERAL (VALUES (p.s || '!', 1), (p.s, 2)) AS v(x, n)"),
        ("lateral_decimal_mix", "SELECT p.id, v.x FROM probe p, LATERAL (VALUES (p.n), (2.5), (p.i)) AS v(x)"),
        ("lateral_double", "SELECT p.id, v.x FROM probe p, LATERAL (VALUES (p.d), (p.d * 2), (1.5e0)) AS v(x)"),
        ("lateral_dates", "SELECT p.id, v.x FROM probe p, LATERAL (VALUES (p.dt + INTERVAL 1 MONTH), (p.dt + 3), (p.dt - INTERVAL '2 days')) AS v(x)"),
        ("lateral_timestamps", "SELECT p.id, v.x FROM probe p, LATERAL (VALUES (p.ts + INTERVAL '1 day 2 hours'), (p.ts)) AS v(x)"),
        ("lateral_chained", "SELECT p.id, v.x, w.y FROM probe p, LATERAL (VALUES (p.j), (p.id)) AS v(x), LATERAL (VALUES (v.x + 1), (v.x * 2)) AS w(y)"),
        ("lateral_aggregate", "SELECT p.id, CAST(sum(v.x) AS BIGINT) AS s, count(*) AS n, count(v.x) AS nx FROM probe p, LATERAL (VALUES (p.i), (p.id), (7)) AS v(x) GROUP BY p.id"),
        ("lateral_exists", "SELECT p.id FROM probe p WHERE EXISTS (SELECT 1 FROM (VALUES (p.i), (p.id)) AS v(x) WHERE v.x = 2)"),
        ("lateral_scalar_subquery", "SELECT p.id, (SELECT max(x) FROM (VALUES (p.i), (p.id)) AS v(x)) AS m FROM probe p"),
        ("lateral_cte", "WITH c AS (SELECT p.id, v.x FROM probe p, LATERAL (VALUES (p.i), (p.id)) AS v(x)) SELECT * FROM c WHERE x > 1"),
        ("lateral_empty_outer", "SELECT q.id, v.x FROM (SELECT * FROM probe WHERE id < 0) q, LATERAL (VALUES (q.i), (q.i + 1)) AS v(x)"),
        ("lateral_empty_outer_left_join", "SELECT q.id, v.x FROM (SELECT * FROM probe WHERE id < 0) q LEFT JOIN LATERAL (VALUES (q.i), (q.i + 1)) AS v(x) ON TRUE"),
        ("lateral_empty_outer_count", "SELECT count(*) AS n FROM (SELECT * FROM probe WHERE id < 0) q, LATERAL (VALUES (q.i), (q.i + 1)) AS v(x)"),
        ("lateral_1100_rows", $"SELECT p.id, v.x FROM probe p, LATERAL (VALUES {string.Join(", ", Enumerable.Range(1, 1100).Select(k => $"(p.j + {k})"))}) AS v(x)"),
        ("lateral_over_filtered_derived_outer", "SELECT q.id, v.x FROM (SELECT * FROM probe WHERE id > 2) q, LATERAL (VALUES (q.j), (q.id + 1)) AS v(x)"),
        ("lateral_left_join_over_filtered_derived_outer", "SELECT q.id, v.x FROM (SELECT * FROM probe WHERE id > 2) q LEFT JOIN LATERAL (VALUES (q.j), (q.id + 1)) AS v(x) ON TRUE"),
        ("lateral_subquery_over_filtered_derived_outer", "SELECT q.id, x.m FROM (SELECT * FROM probe WHERE id > 2) q, LATERAL (SELECT max(u.id) AS m FROM probe u WHERE u.id < q.id) x"),
        ("lateral_ordered_limit", "SELECT p.id, v.x FROM probe p, LATERAL (VALUES (p.j), (p.id) ORDER BY 1 LIMIT 1) AS v(x)"),
        ("lateral_with_join_to_other_list", "SELECT p.id, v.x, w.label FROM probe p, LATERAL (VALUES (p.id), (p.id + 1)) AS v(x) JOIN (VALUES (1, 'one'), (2, 'two'), (9, 'nine')) AS w(k, label) ON w.k = v.x"),
        ("join_side", "SELECT p.id, v.label FROM probe p JOIN (VALUES (1, 'one'), (2, 'two'), (7, 'seven'), (99, 'none')) AS v(k, label) ON p.id = v.k"),
        ("left_join_side", "SELECT p.id, v.label FROM probe p LEFT JOIN (VALUES (1, 'one'), (2, 'two')) AS v(k, label) ON p.id = v.k"),
        ("two_lists_joined", "SELECT v.id, w.n FROM (VALUES (1, 'x'), (2, 'y')) AS v(id, s) JOIN (VALUES (1, 10), (2, 20)) AS w(k, n) ON v.id = w.k"),
        ("cte", "WITH c AS (SELECT * FROM (VALUES (1, 'x'), (2, 'y'), (3, 'z')) AS v(id, s)) SELECT * FROM c WHERE id > 1"),
        ("in_subquery", "SELECT id FROM probe WHERE id IN (SELECT x FROM (VALUES (1), (3), (5)) AS v(x))"),
        ("not_in_subquery", "SELECT id FROM probe WHERE id NOT IN (SELECT x FROM (VALUES (1), (3), (5)) AS v(x))"),
        ("exists_subquery", "SELECT id FROM probe WHERE EXISTS (SELECT 1 FROM (VALUES (1), (3), (5)) AS v(x) WHERE v.x = probe.id)"),
        ("scalar_subquery", "SELECT id, (SELECT max(x) FROM (VALUES (1), (2), (3)) AS v(x)) AS m FROM probe"),
        ("group_by", "SELECT x, count(*) AS n FROM (VALUES (1), (1), (2), (NULL), (NULL)) AS v(x) GROUP BY x"),
        ("order_limit", "SELECT x FROM (VALUES (3), (1), (2), (5), (4)) AS v(x) ORDER BY x DESC LIMIT 2"),
        ("union_all", "SELECT x FROM (VALUES (1), (2)) AS v(x) UNION ALL SELECT id FROM probe"),
        ("distinct", "SELECT DISTINCT x FROM (VALUES (1), (1), (NULL), (NULL)) AS v(x)"),
        ("filter", "SELECT x FROM (VALUES (1), (2), (3)) AS v(x) WHERE x > 1"),
        ("rows_999", $"SELECT * FROM (VALUES {Rows(999)}) AS v(id, s, d, dt)"),
        ("rows_1000", $"SELECT * FROM (VALUES {Rows(1000)}) AS v(id, s, d, dt)"),
        ("rows_1001", $"SELECT * FROM (VALUES {Rows(1001)}) AS v(id, s, d, dt)"),
        ("rows_2500", $"SELECT * FROM (VALUES {Rows(2500)}) AS v(id, s, d, dt)"),
        ("rows_5000", $"SELECT * FROM (VALUES {Rows(5000)}) AS v(id, s, d, dt)"),
        ("many_join_side", $"SELECT p.id, v.s, v.d FROM probe p JOIN (VALUES {Rows(2200)}) AS v(id, s, d, dt) ON p.id = v.id"),
        ("many_cte", $"WITH c AS (SELECT * FROM (VALUES {Rows(1500)}) AS v(id, s, d, dt)) SELECT count(*) AS n, sum(d) AS total, count(dt) AS dates FROM c"),
        ("many_in_subquery", $"SELECT id FROM probe WHERE id IN (SELECT id FROM (VALUES {Rows(1500)}) AS v(id, s, d, dt) WHERE id % 2 = 0)"),
        ("many_aggregate", $"SELECT count(*) AS n, count(d) AS nd, min(s) AS lo, max(id) AS hi FROM (VALUES {Rows(3000)}) AS v(id, s, d, dt)"),
    ];

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task VALUES_lists_give_DuckDBs_rows_names_and_types(string name)
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
            if (expected.Error != null || actual.Error != null) { problems.Add($"{id}: duckdb [{expected.Error}] engine [{actual.Error}]"); continue; }
            if (actual.Rows != expected.Rows) problems.Add($"{id}: rows differ, first duckdb [{First(expected.Rows)}] engine [{First(actual.Rows)}]  {Short(expected.Rows)} vs {Short(actual.Rows)}");
            var typeProblem = await TypesDifferAsync(engine, probe, sql, rendered);
            if (typeProblem != null) problems.Add($"{id}: {typeProblem}");
        }
        Assert.True(problems.Count == 0, name + ":\n" + string.Join("\n", problems));
    }

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task A_list_the_engines_cannot_hold_exactly_is_refused_before_the_engine_is_asked(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        foreach (var (sql, named) in new[]
        {
            ("SELECT * FROM (VALUES (CAST(1 AS TINYINT)), (2)) AS v(a)", "TINYINT"), ("SELECT * FROM (VALUES (CAST(1 AS UBIGINT))) AS v(a)", "UBIGINT"), ("SELECT * FROM (VALUES (INTERVAL 3 DAY)) AS v(a)", "INTERVAL"),
            ("SELECT * FROM (VALUES ([1, 2])) AS v(a)", "LIST"), ("SELECT * FROM (VALUES (TIME '10:00:00')) AS v(a)", "TIME"), ("SELECT * FROM probe, LATERAL (VALUES (probe.id), ((SELECT count(*) FROM probe p2))) AS v(x)", "subquery"),
        })
        {
            var (rendered, refused) = probe.Render(sql);
            Assert.True(rendered == null && refused != null && refused.Contains(named), $"{sql}: expected a refusal naming {named}, got {refused ?? rendered}");
        }
    }

    private static string First(string rows) => rows.Split("; ")[0];
    private static string Short(string rows) => rows.Length > 120 ? $"{rows.Length} chars" : rows;

    /// <summary>The engine's type of each column against DuckDB's, or null when they agree (the width of a text column is not compared: DuckDB's is unbounded).</summary>
    internal static async Task<string?> TypesDifferAsync(IProbeEngine engine, EngineProbe probe, string source, string rendered)
    {
        var columns = probe.DuckColumns(source);
        var problems = new StringBuilder();
        if (engine.Name == "sqlserver")
        {
            var described = await engine.QueryAsync($"SELECT name, system_type_name FROM sys.dm_exec_describe_first_result_set(N'{rendered.Replace("'", "''")}', NULL, 0) ORDER BY column_ordinal");
            if (described.Rows.Count != columns.Count) return $"the engine has {described.Rows.Count} columns, DuckDB {columns.Count}";
            for (var i = 0; i < columns.Count; i++)
            {
                var (engineName, engineType) = ((string)described.Rows[i][0]!, ((string)described.Rows[i][1]!).ToLowerInvariant());
                if (engineName != columns[i].Name) problems.Append($" column {i} is named {engineName} (DuckDB {columns[i].Name});");
                if (!SqlServerType(columns[i].Type, engineType)) problems.Append($" {columns[i].Name}: {engineType} for DuckDB's {columns[i].Type};");
            }
        }
        else
        {
            var select = string.Join(", ", columns.Select(c => $"pg_typeof(q.\"{c.Name}\")::text" + (c.Type.StartsWith("DECIMAL", StringComparison.Ordinal) ? $", scale(q.\"{c.Name}\")" : "")));
            var described = await engine.QueryAsync($"SELECT {select} FROM ({rendered}) AS q LIMIT 1");
            // a query with no rows has no row to ask the scale of: its types are read from the null row of an outer join, and the scale is not compared
            var noRows = described.Rows.Count == 0;
            if (noRows) described = await engine.QueryAsync($"SELECT {select} FROM (VALUES (1)) AS ddb_one LEFT JOIN ({rendered}) AS q ON false");
            var row = described.Rows[0];
            var at = 0;
            foreach (var c in columns)
            {
                var engineType = (string)row[at++]!;
                if (!PostgresType(c.Type, engineType)) problems.Append($" {c.Name}: {engineType} for DuckDB's {c.Type};");
                if (!c.Type.StartsWith("DECIMAL", StringComparison.Ordinal)) continue;
                var scaleValue = row[at++];
                if (!noRows && Convert.ToInt32(scaleValue) is var scale && c.Type != $"DECIMAL({c.Type.Split(',')[0].Split('(')[1]},{scale})") problems.Append($" {c.Name}: scale {scale} for DuckDB's {c.Type};");
            }
        }
        return problems.Length == 0 ? null : problems.ToString().Trim();
    }

    private static bool SqlServerType(string duck, string engine) => duck switch
    {
        "INTEGER" => engine == "int",
        "BIGINT" => engine == "bigint",
        "SMALLINT" => engine == "smallint",
        "BOOLEAN" => engine == "bit",
        "DOUBLE" => engine == "float",
        "DATE" => engine == "date",
        "TIMESTAMP" => engine.StartsWith("datetime2", StringComparison.Ordinal),
        "VARCHAR" => engine.StartsWith("varchar", StringComparison.Ordinal) || engine.StartsWith("nvarchar", StringComparison.Ordinal),
        "FLOAT" => engine == "real",
        _ when duck.StartsWith("DECIMAL(", StringComparison.Ordinal) => engine.Replace(" ", "").Replace("numeric", "decimal") == duck.ToLowerInvariant().Replace(" ", ""),
        _ => false,
    };

    private static bool PostgresType(string duck, string engine) => duck switch
    {
        "INTEGER" => engine == "integer",
        "BIGINT" => engine == "bigint",
        "SMALLINT" => engine == "smallint",
        "BOOLEAN" => engine == "boolean",
        "DOUBLE" => engine == "double precision",
        "FLOAT" => engine == "real",
        "DATE" => engine == "date",
        "TIMESTAMP" => engine == "timestamp without time zone",
        "VARCHAR" => engine is "text" or "character varying",
        _ when duck.StartsWith("DECIMAL(", StringComparison.Ordinal) => engine == "numeric",
        _ => false,
    };
}
