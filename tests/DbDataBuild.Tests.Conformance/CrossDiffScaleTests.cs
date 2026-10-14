using System.Diagnostics;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// How `diff --against-connection` behaves on a table of a million rows (DESIGN.md 9.10): the time of the digest queries on both engines and what crosses the wire. Off unless `DDB_SCALE=1`: it fills two tables and
/// takes minutes. `DDB_SCALE_ROWS` sets the row count (default 1000000).
/// </summary>
public class CrossDiffScaleTests
{
    [SkippableFact]
    public async Task A_million_rows_are_compared_by_digests_and_the_few_that_differ_are_found()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DDB_SCALE") == "1", "set DDB_SCALE=1 to run");
        var rows = int.TryParse(Environment.GetEnvironmentVariable("DDB_SCALE_ROWS"), out var n) ? n : 1_000_000;
        var left = EngineEnv.Require("sqlserver");
        var right = EngineEnv.Require("postgres");
        await left.StartAsync(); await right.StartAsync();
        await using var _l = left; await using var _r = right;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-xdiff-scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models/big"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\n");
            File.WriteAllText(Path.Combine(dir, "models/big/t.yml"), "name: big.t\nkind: mapped\nconnections=: [sqlserver, postgres]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
            await left.ExecAsync("EXEC('CREATE SCHEMA big')");
            await left.ExecAsync("CREATE TABLE big.t (id BIGINT NOT NULL, a INT, price DECIMAL(18,2), code NVARCHAR(40), seen DATETIME2(6))");
            await left.ExecAsync($"INSERT INTO big.t SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 1000 AS INT), CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 9973 AS DECIMAL(18,2)) / 7, N'code-' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 5000 AS NVARCHAR(10)), DATEADD(SECOND, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), '2024-01-01') FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c");
            await right.ExecAsync("CREATE SCHEMA big");
            await right.ExecAsync("CREATE TABLE big.t (id BIGINT NOT NULL, a INT, price NUMERIC(12,2), code VARCHAR(40), seen TIMESTAMP(6))");
            await right.ExecAsync($"INSERT INTO big.t SELECT g, g % 1000, round((g % 9973)::numeric / 7, 2), 'code-' || (g % 5000), TIMESTAMP '2024-01-01' + g * INTERVAL '1 second' FROM generate_series(1, {rows}) g");
            // the two decimals above round differently only if the engines disagree; make five rows differ on purpose
            await right.ExecAsync("UPDATE big.t SET code = 'changed' WHERE id IN (7, 777, 77777, 700001, 999999)");
            await right.ExecAsync("DELETE FROM big.t WHERE id = 12345");
            await right.ExecAsync($"INSERT INTO big.t VALUES ({rows + 1}, 1, 1, 'extra', TIMESTAMP '2030-01-01')");

            Func<string, string?> env = v =>
                v == LoginSettings.VariableName("sqlserver", Login.Read) ? left.ConnectionString
                : v == LoginSettings.VariableName("postgres", Login.Read) ? right.ConnectionString : null;
            var o = new StringWriter(); var e = new StringWriter();
            var clock = Stopwatch.StartNew();
            var exit = CliApp.Run(["connection", "compare", "sqlserver", "big.t", "--project", dir, "--against-connection", "postgres", "--format", "json"], o, e, environment: env);
            clock.Stop();
            Assert.True(o.ToString().Length > 0, "no output: " + e);
            var data = JsonNode.Parse(o.ToString())!["data"]!;
            Console.WriteLine($"cross diff of {rows:N0} rows: {clock.Elapsed.TotalSeconds:0.0}s, exit {exit}, rows {data["rows"]}");
            Console.WriteLine(string.Join("\n", ((JsonArray)data["schema"]!["not_compared"]!).Select(x => x!.ToJsonString())));
            Assert.Equal(1, exit);
            Assert.Equal(1, (int)data["rows"]!["only_left"]!);                                    // 12345 is gone on the right
            Assert.Equal(1, (int)data["rows"]!["only_right"]!);
            Assert.Equal(5, (int)data["rows"]!["differing"]!);
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(10), $"took {clock.Elapsed}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
