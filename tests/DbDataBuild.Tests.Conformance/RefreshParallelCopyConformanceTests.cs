using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Refresh plans, step 5 (docs/research/refresh-plans.md, section 16): `max_parallel` on the origin of fan-in copies. Three copies read three slow views of one PostgreSQL origin (each pauses a second and a half); run three at a time
/// they would have three sessions open at the origin at once, and with `max_parallel: 1` on the origin never more than one, whatever `--parallel` says.
/// </summary>
public partial class CopyConformanceTests
{
    [SkippableFact]
    public async Task Max_parallel_on_an_origin_keeps_the_copies_that_run_together_to_that_many_sessions_there()
    {
        const string origin = "postgres", destination = "sqlserver";
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync();
        await to.StartAsync();
        var pair = new Pair(from, to, Path.Combine(Path.GetTempPath(), "ddb-copy-" + Guid.NewGuid().ToString("N")));
        await using var fromEngine = from;
        await using var toEngine = to;
        try
        {
            pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\nrefresh:\n  plans:\n    fan: {{}}\n");
            await fromEngine.ExecAsync("CREATE SCHEMA src");
            foreach (var n in new[] { "1", "2", "3" })
            {
                // a view that takes a second and a half to answer
                await fromEngine.ExecAsync($"CREATE VIEW src.slow{n} AS SELECT CAST({n} AS BIGINT) AS id FROM pg_sleep(1.5)");
                pair.Write($"models/src/slow{n}.yml", $"name: src.slow{n}\nkind: mapped\nconnections=: [{origin}]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT}}\n");
                pair.Write($"models/dst/copy{n}.yml", $"name: dst.copy{n}\nkind: {{copy: {{from: src.slow{n}}}}}\nrefresh: {{ fan: default }}\n");
            }
            Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "compile");
            var plan = pair.Cli("connection", "deploy", destination, "--write-plan"); Ok(plan, "plan");
            Ok(pair.Cli("connection", "deploy", "--apply-plan", Path.Combine(pair.Dir, System.Text.RegularExpressions.Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value)), "deploy");

            SessionLimits? seen = null;
            RefreshCommand.LimitsObserver = l => seen = l;
            try
            {
                // no cap: three copies, three sessions at the origin at once
                var free = pair.Cli("connection", "refresh", destination, "fan", "--parallel", "3");
                Ok(free, "uncapped");
                Assert.Equal(3, seen!.MostInUse(origin));

                // a cap of 1 on the origin: the copies still run (the destination has no limit) and the origin never has more than one session of the refresh
                pair.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(pair.Dir, "dbdatabuild.yml")) + $"connections:\n  {origin}: {{ max_parallel: 1 }}\n");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var capped = pair.Cli("connection", "refresh", destination, "fan", "--parallel", "3");
                Ok(capped, "capped");
                Assert.Equal(1, seen!.MostInUse(origin));
                Assert.Equal(1, seen.CapOf(origin));
                Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(4.5), $"three reads of 1.5 s, one at a time, took {clock.Elapsed}");
                Assert.Contains("3 done", capped.Out);
                Assert.Equal(["1", "2", "3"], await toEngine.RowsAsync("SELECT CAST(id AS VARCHAR(10)) FROM (SELECT id FROM dst.copy1 UNION ALL SELECT id FROM dst.copy2 UNION ALL SELECT id FROM dst.copy3) x ORDER BY id"));
            }
            finally { RefreshCommand.LimitsObserver = null; }
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }
}
