using System.Text;
using Xunit;

namespace DbDataBuild.Tests.Conformance.FunctionMatrix;

/// <summary>
/// Measures DuckDB's `age` against PostgreSQL's native `age` and against a T-SQL formulation, on a table of edge-case pairs (month ends, leap days, both directions, times of day),
/// for each shape a model uses `age` in (docs/research/age-function.md). Skipped unless DDB_AGE_PROBE_OUT names a file; scripts/age-probe.sh sets it. Asserts nothing: the report is the result.
/// </summary>
public class AgeProbe
{
    private static readonly string[] Dates =
    [
        "1900-02-28", "1900-03-01", "1999-12-31", "2000-02-28", "2000-02-29", "2000-03-01", "2021-02-28", "2022-02-28", "2023-01-31", "2023-02-28", "2023-03-01", "2023-03-31", "2023-04-30", "2023-05-31",
        "2023-06-15", "2023-07-31", "2023-08-31", "2023-12-31", "2024-01-01", "2024-01-30", "2024-01-31", "2024-02-01", "2024-02-28", "2024-02-29", "2024-03-01", "2024-03-30", "2024-03-31",
        "2024-04-30", "2024-05-31", "2024-06-30", "2024-07-31", "2024-08-31", "2024-09-30", "2024-10-31", "2024-11-30", "2024-12-31", "2025-01-01", "2025-01-31", "2025-02-28", "2025-03-01",
        "2025-03-31", "2100-02-28", "2100-03-01",
    ];
    private static readonly string[] StampDates = ["2023-01-31", "2023-02-28", "2024-01-31", "2024-02-29", "2024-03-01", "2024-03-31", "2024-12-31", "2025-02-28", "2025-03-01", "2025-03-31"];
    private static readonly string[] Times = ["00:00:00", "00:00:00.000001", "08:15:30", "10:30:45.5", "12:00:00", "23:59:59.999999"];

    private sealed record Shape(string Name, string Duck, string Postgres, string SqlServer);

    // `c` has, per pair, the sign (-1 when a is before b), the whole months, the days and the microseconds of the difference taken from the earlier to the later timestamp, as DuckDB and PostgreSQL do
    private const string SqlServerCte = @"WITH x AS (SELECT id, CASE WHEN a < b THEN a ELSE b END AS e, CASE WHEN a < b THEN b ELSE a END AS l, CASE WHEN a < b THEN -1 ELSE 1 END AS sgn FROM age_pairs),
y AS (SELECT *, CASE WHEN CAST(l AS time(6)) < CAST(e AS time(6)) THEN 1 ELSE 0 END AS tb FROM x),
z AS (SELECT *, CASE WHEN DAY(l) < DAY(e) OR (DAY(l) = DAY(e) AND tb = 1) THEN 1 ELSE 0 END AS bw FROM y),
c AS (SELECT id, sgn, DATEDIFF(MONTH, e, l) - bw AS mo, DAY(l) - DAY(e) - tb + bw * DAY(EOMONTH(e)) AS dd,
  DATEDIFF_BIG(MICROSECOND, CAST(e AS time(6)), CAST(l AS time(6))) + tb * CAST(86400000000 AS bigint) AS us FROM z) ";

    private static readonly Shape[] Shapes =
    [
        new("extract(year FROM age(a, b))", "extract(year FROM age(a, b))", "extract(year FROM age(a, b))", "sgn * (mo / 12)"),
        new("extract(month FROM age(a, b))", "extract(month FROM age(a, b))", "extract(month FROM age(a, b))", "sgn * (mo % 12)"),
        new("extract(day FROM age(a, b))", "extract(day FROM age(a, b))", "extract(day FROM age(a, b))", "sgn * dd"),
        new("extract(hour FROM age(a, b))", "extract(hour FROM age(a, b))", "extract(hour FROM age(a, b))", "sgn * (us / 3600000000)"),
        new("extract(minute FROM age(a, b))", "extract(minute FROM age(a, b))", "extract(minute FROM age(a, b))", "sgn * ((us / 60000000) % 60)"),
        new("extract(second FROM age(a, b))", "extract(second FROM age(a, b))", "extract(second FROM age(a, b))", "sgn * ((us / 1000000) % 60)"),
        new("year(age(a, b))", "year(age(a, b))", "extract(year FROM age(a, b))", "sgn * (mo / 12)"),
        new("date_part('month', age(a, b))", "date_part('month', age(a, b))", "date_part('month', age(a, b))", "sgn * (mo % 12)"),
        new("age(a, b) > INTERVAL '1 year'", "age(a, b) > INTERVAL '1 year'", "age(a, b) > INTERVAL '1 year'", "CASE WHEN sgn * ((mo * 30 + dd) * CAST(86400000000 AS bigint) + us) > 12 * 30 * CAST(86400000000 AS bigint) THEN 1 ELSE 0 END"),
        new("age(a, b) >= INTERVAL '30 days'", "age(a, b) >= INTERVAL '30 days'", "age(a, b) >= INTERVAL '30 days'", "CASE WHEN sgn * ((mo * 30 + dd) * CAST(86400000000 AS bigint) + us) >= 30 * CAST(86400000000 AS bigint) THEN 1 ELSE 0 END"),
        new("age(a, b) = INTERVAL '1 month'", "age(a, b) = INTERVAL '1 month'", "age(a, b) = INTERVAL '1 month'", "CASE WHEN sgn * ((mo * 30 + dd) * CAST(86400000000 AS bigint) + us) = 30 * CAST(86400000000 AS bigint) THEN 1 ELSE 0 END"),
        new("age(a, b) < INTERVAL '0 days' (negative direction)", "age(a, b) < INTERVAL '0 days'", "age(a, b) < INTERVAL '0 days'", "CASE WHEN sgn * ((mo * 30 + dd) * CAST(86400000000 AS bigint) + us) < 0 THEN 1 ELSE 0 END"),
    ];

    private static string Bool(string s) => s switch { "true" => "1", "false" => "0", _ => s };

    [SkippableFact]
    public async Task Age_in_every_shape_on_DuckDB_PostgreSQL_and_a_TSQL_formulation()
    {
        var path = Environment.GetEnvironmentVariable("DDB_AGE_PROBE_OUT");
        Skip.If(string.IsNullOrEmpty(path), "Set DDB_AGE_PROBE_OUT to a file (scripts/age-probe.sh does, with the engines running).");
        Skip.If(EngineEnv.Get(EngineEnv.SqlServer) == null || EngineEnv.Get(EngineEnv.Postgres) == null, "Set DBDATABUILD_TEST_MSSQL and DBDATABUILD_TEST_PG.");
        await using var x = new Execution();
        await x.StartAsync();

        var pairs = new List<(int Id, string Kind, string A, string B)>();
        foreach (var a in Dates) foreach (var b in Dates) pairs.Add((pairs.Count + 1, "date", a + " 00:00:00", b + " 00:00:00"));
        var stamps = StampDates.SelectMany(d => Times.Select(t => $"{d} {t}")).ToList();
        foreach (var a in stamps) foreach (var b in stamps) pairs.Add((pairs.Count + 1, "timestamp", a, b));

        // the same table on all three
        void Duck(string sql) { using var cmd = x.Duck.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        Duck("CREATE TABLE age_pairs (id INTEGER, a TIMESTAMP, b TIMESTAMP)");
        await x.SqlServer.ExecAsync("CREATE TABLE age_pairs (id INT, a DATETIME2(6), b DATETIME2(6))");
        await x.Postgres.ExecAsync("CREATE TABLE age_pairs (id INT, a TIMESTAMP(6), b TIMESTAMP(6))");
        foreach (var chunk in pairs.Chunk(500))
        {
            var values = string.Join(", ", chunk.Select(p => $"({p.Id}, '{p.A}', '{p.B}')"));
            Duck("INSERT INTO age_pairs VALUES " + values);
            await x.SqlServer.ExecAsync("INSERT INTO age_pairs VALUES " + values);
            await x.Postgres.ExecAsync("INSERT INTO age_pairs VALUES " + values);
        }
        var kind = pairs.ToDictionary(p => p.Id.ToString(), p => p.Kind);

        var sb = new StringBuilder();
        sb.Append($"pairs: {pairs.Count(p => p.Kind == "date")} date pairs ({Dates.Length} dates, every ordered pair), {pairs.Count(p => p.Kind == "timestamp")} timestamp pairs ({stamps.Count} timestamps)\n\n");

        // the interval itself: DuckDB against PostgreSQL's native age
        await Compare(sb, x, "age(a, b) as an interval (months, days, microseconds)", "SELECT id, age(a, b) AS v FROM age_pairs", "SELECT id, age(a, b) AS v FROM age_pairs",
            $"{SqlServerCte} SELECT id, CONCAT(sgn * mo, 'm ', sgn * dd, 'd ', sgn * us, 'us') AS v FROM c", kind);
        foreach (var s in Shapes)
            await Compare(sb, x, s.Name, $"SELECT id, {s.Duck} AS v FROM age_pairs", $"SELECT id, {s.Postgres} AS v FROM age_pairs", $"{SqlServerCte} SELECT id, {s.SqlServer} AS v FROM c", kind);

        // two ages compared with each other (and so ordered): 40 pairs against 40 pairs
        var ids = string.Join(", ", Enumerable.Range(0, 40).Select(i => 1 + i * 47 % 1936));
        var duckCmp = $"SELECT p.id * 10000 + q.id AS id, age(p.a, p.b) < age(q.a, q.b) AS v FROM age_pairs p, age_pairs q WHERE p.id IN ({ids}) AND q.id IN ({ids})";
        var pgCmp = duckCmp;
        var msCmp = $"{SqlServerCte}, k AS (SELECT id, sgn * ((mo * 30 + dd) * CAST(86400000000 AS bigint) + us) AS n FROM c) SELECT p.id * 10000 + q.id AS id, CASE WHEN p.n < q.n THEN 1 ELSE 0 END AS v FROM k p, k q WHERE p.id IN ({ids}) AND q.id IN ({ids})";
        await Compare(sb, x, "age(p) < age(q) (comparing, ordering two ages)", duckCmp, pgCmp, msCmp, new Dictionary<string, string>());

        // age(ts) with one argument
        var one = x.OnDuck("SELECT age(TIMESTAMP '2020-05-17 10:00:00') = age(CAST(current_date AS TIMESTAMP), TIMESTAMP '2020-05-17 10:00:00') AS v");
        sb.Append($"age(ts) = age(current_date, ts) on DuckDB: {(one.Ok ? one.Rows!.Rows[0][^1] : one.Error)}\n");
        File.WriteAllText(path!, sb.ToString(), new UTF8Encoding(false));
    }

    private static async Task Compare(StringBuilder sb, Execution x, string name, string duckSql, string pgSql, string msSql, Dictionary<string, string> kind)
    {
        var d = x.OnDuck(duckSql);
        var p = await x.OnEngineAsync("postgres", pgSql);
        var m = await x.OnEngineAsync("sqlserver", msSql);
        sb.Append($"## {name}\n  duckdb:     {Describe(d)}\n  postgres:   {Describe(p)}\n  sql server: {Describe(m)}\n");
        if (!d.Ok) return;
        var reference = d.Rows!.Rows.ToDictionary(r => r[0], r => Bool(r[^1]));
        foreach (var (label, a) in new[] { ("postgres native", p), ("t-sql formulation", m) })
        {
            if (!a.Ok) { sb.Append($"  {label}: not run ({a.Error})\n"); continue; }
            var theirs = a.Rows!.Rows.ToDictionary(r => r[0], r => Bool(r[^1]));
            var bad = reference.Where(kv => !theirs.TryGetValue(kv.Key, out var v) || !Execution.SameCell(kv.Value, v)).ToList();
            var byKind = bad.GroupBy(kv => kind.GetValueOrDefault(kv.Key, "pair")).Select(g => $"{g.Key} {g.Count()}");
            sb.Append($"  {label}: {reference.Count - bad.Count} of {reference.Count} rows equal DuckDB's, {bad.Count} differ{(bad.Count > 0 ? " (" + string.Join(", ", byKind) + ")" : "")}\n");
            foreach (var kv in bad.Take(4)) sb.Append($"    id {kv.Key}: DuckDB {kv.Value}, {label} {(theirs.TryGetValue(kv.Key, out var v) ? v : "missing")}\n");
        }
        sb.Append('\n');
    }

    private static string Describe(Answer a) => a.Ok ? $"{a.Rows!.Rows.Count} rows, type {a.Rows.ValueType}" : "ERROR " + a.Error;
}
