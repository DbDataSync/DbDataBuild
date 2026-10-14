using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Refresh plans, step 4 (docs/research/refresh-plans.md, section 15): a copy is a load of a refresh, between the two real engines in both directions. The copy is a member of a plan, it reads its origin on the origin's read login,
/// stages the rows, loads them and drops the staging table, and what reads the copied table runs after it in the same run. An origin that cannot be reached fails the copy (`failed: origin`) and the failure rules apply.
/// </summary>
public partial class CopyConformanceTests
{
    private const string EventColumns = "columns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: day, type: DATE, nullable: false}\n  - {name: v, type: \"VARCHAR(20)\"}\n";

    private static async Task<Pair> RefreshSetUp(string origin, string destination, string copyKind = "{copy: {from: src.events, unique_key: [id], watermark: {column: day, lookback: 1 days}}}")
    {
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync();
        await to.StartAsync();
        var pair = new Pair(from, to, Path.Combine(Path.GetTempPath(), "ddb-copy-" + Guid.NewGuid().ToString("N")));
        pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\nrefresh:\n  plans:\n    hourly: {{ on_failure: continue }}\n    daily: {{ on_failure: continue }}\n" + Sensitive(destination));
        pair.Write("models/src/events.yml", $"name: src.events\nkind: mapped\nconnections=: [{origin}]\ngrain: [id]\n{EventColumns}");
        pair.Write("models/dst/events.yml", $"name: dst.events\nkind: {copyKind}\nrefresh: {{ hourly: default }}\n");
        pair.Write("models/dst/summary.yml", "name: dst.summary\nkind: full\ncolumns:\n  - {name: day, type: DATE, nullable: false}\n  - {name: n, type: BIGINT}\nrefresh: { hourly: default }\n");
        pair.Write("models/dst/summary.sql", "SELECT day, COUNT(*) AS n FROM dst.events GROUP BY day\n");
        await from.ExecAsync(origin == "postgres" ? "CREATE SCHEMA src" : "EXEC('CREATE SCHEMA src')");
        await from.ExecAsync($"CREATE TABLE src.events ({from.QuoteIdent("id")} BIGINT NOT NULL, {from.QuoteIdent("day")} DATE NOT NULL, {from.QuoteIdent("v")} {from.ColumnType("VARCHAR(20)")})");
        await from.ExecAsync("INSERT INTO src.events VALUES (1, '2026-03-01', 'a'), (2, '2026-03-02', 'b'), (3, '2026-03-03', 'c')");
        return pair;
    }

    private static async Task DeployAsync(Pair pair, string destination)
    {
        Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
        Ok(pair.Cli("project", "compile"), "compile");
        var plan = pair.Cli("connection", "deploy", destination, "--write-plan");
        Ok(plan, "plan");
        Ok(pair.Cli("connection", "deploy", "--apply-plan", Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value)), "deploy");
        await Task.CompletedTask;
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task A_copy_is_a_load_of_a_refresh_it_runs_before_what_reads_it_and_an_origin_that_cannot_be_reached_fails_it_and_skips_what_reads_it(string origin, string destination)
    {
        var pair = await RefreshSetUp(origin, destination);
        await using var fromEngine = pair.Origin;
        await using var toEngine = pair.Destination;
        try
        {
            await DeployAsync(pair, destination);
            var rows = async () => await toEngine.RowsAsync(destination == "postgres" ? "SELECT CAST(id AS VARCHAR(10)) || ':' || v FROM dst.events ORDER BY id" : "SELECT CAST(id AS VARCHAR(10)) + ':' + v FROM dst.events ORDER BY id");
            Assert.Equal(["1:a", "2:b", "3:c"], await rows());

            // the compiled plan: the copy is a member, ahead of the summary that reads it, and the origin's read is in the load plan
            var loads = File.ReadAllText(Path.Combine(pair.Dir, "rendered", destination, "loads.yml"));
            Assert.DoesNotContain("excluded:", loads);
            Assert.Contains($"connection: \"{origin}\"", loads);
            var plan = File.ReadAllText(Path.Combine(pair.Dir, "rendered", destination, "refresh", "hourly.yml"));
            Assert.True(plan.IndexOf("dst.events", StringComparison.Ordinal) is var at and >= 0 && plan.IndexOf("dst.summary", StringComparison.Ordinal) > at);

            // new rows at the origin, and a change in the newest day: the incremental read replaces what it re-reads
            await fromEngine.ExecAsync("INSERT INTO src.events VALUES (4, '2026-03-05', 'd')");
            await fromEngine.ExecAsync("UPDATE src.events SET v = 'c2' WHERE id = 3");
            var dry = pair.Cli("connection", "refresh", destination, "hourly", "--dry-run");
            Ok(dry, "dry run");
            Assert.Equal(["1:a", "2:b", "3:c"], await rows());                                                       // nothing was written
            var refresh = pair.Cli("connection", "refresh", destination, "hourly", "--format", "json");
            Ok(refresh, "refresh");
            var doc = System.Text.Json.Nodes.JsonNode.Parse(refresh.Out)!["data"]!;
            Assert.Equal("completed", (string)doc["outcome"]!);
            Assert.Equal(["dst.events", "dst.summary"], doc["results"]!.AsArray().Select(r => (string)r!["model"]!).ToList());
            Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await rows());
            Assert.Equal(["4"], await toEngine.RowsAsync("SELECT CAST(SUM(n) AS VARCHAR(10)) FROM dst.summary"));                        // the summary ran after the copy, on its rows
            Assert.Empty(await toEngine.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_name LIKE 'stg_dst%'"));        // the staging table is gone

            // the record: the transfer names its origin, belongs to the load's membership, and the load row is there
            Assert.Equal([$"from {origin}"], await toEngine.RowsAsync("SELECT DISTINCT load_name FROM dbdatabuild.run_log WHERE operation = 'transfer' AND membership = 'default' AND plan_id LIKE 'ref-%'"));
            Assert.Equal(["ok"], await toEngine.RowsAsync("SELECT DISTINCT status FROM dbdatabuild.run_log WHERE model = 'dst.events' AND membership = 'default' AND plan_id LIKE 'ref-%'"));
            Ok(pair.Cli("connection", "monitor", destination, "hourly"), "monitor");
            Ok(pair.Cli("connection", "status", destination), "status");

            // an origin without its read login: the copy fails with the reason, the summary is skipped, the table keeps what it had
            await fromEngine.ExecAsync("INSERT INTO src.events VALUES (5, '2026-03-06', 'e')");
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run(["connection", "refresh", destination, "hourly", "--project", pair.Dir, "--format", "json"], o, e,
                environment: v => v == LoginSettings.VariableName(origin, Login.Read) ? null : pair.Env(v));
            Assert.Equal(1, exit);
            var failed = System.Text.Json.Nodes.JsonNode.Parse(o.ToString())!["data"]!;
            Assert.Equal("failed", (string)failed["outcome"]!);
            var byModel = failed["results"]!.AsArray().ToDictionary(r => (string)r!["model"]!, r => r!);
            Assert.Equal("failed: origin", (string)byModel["dst.events"]["status"]!);
            Assert.Contains($"DBDATABUILD_{origin.ToUpperInvariant()}_READ", (string)byModel["dst.events"]["detail"]!);
            Assert.Equal("skipped: upstream failed", (string)byModel["dst.summary"]["status"]!);
            Assert.Equal(["1:a", "2:b", "3:c2", "4:d"], await rows());

            // an origin that is there again: the same plan, only what failed or was skipped
            var again = pair.Cli("connection", "refresh", destination, "hourly", "--only-failed");
            Ok(again, "only failed");
            Assert.Equal(["1:a", "2:b", "3:c2", "4:d", "5:e"], await rows());
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task A_source_resolver_of_a_copy_runs_in_a_refresh_at_the_origin_and_its_value_is_what_the_event_records(string origin, string destination)
    {
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync();
        await to.StartAsync();
        var pair = new Pair(from, to, Path.Combine(Path.GetTempPath(), "ddb-copy-" + Guid.NewGuid().ToString("N")));
        await using var fromEngine = from;
        await using var toEngine = to;
        try
        {
            pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\nrefresh:\n  plans:\n    daily: {{}}\n" + Sensitive(destination));
            pair.Write("models/src/events.yml", $"name: src.events\nkind: mapped\nconnections=: [{origin}]\ngrain: [id]\n{EventColumns}");
            pair.Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events}}\n" +
                "loads:\n  window:\n    strategy: delete_insert_by_range\n    column: day\n    params: {start: DATE, end: DATE}\n    inputs:\n" +
                "      start: {resolver: {source: \"SELECT min(day) FROM src.events WHERE day >= DATE '2026-03-01'\"}}\n" +
                "      end: {resolver: {expression: \"DATE '2026-03-04'\"}}\n" +
                "refresh: { daily: window }\n");
            await from.ExecAsync(origin == "postgres" ? "CREATE SCHEMA src" : "EXEC('CREATE SCHEMA src')");
            await from.ExecAsync($"CREATE TABLE src.events ({from.QuoteIdent("id")} BIGINT NOT NULL, {from.QuoteIdent("day")} DATE NOT NULL, {from.QuoteIdent("v")} {from.ColumnType("VARCHAR(20)")})");
            await from.ExecAsync("INSERT INTO src.events VALUES (1, '2026-03-01', 'a'), (2, '2026-03-02', 'b'), (3, '2026-03-03', 'c'), (9, '2026-03-09', 'late')");
            Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "compile");
            var plan = pair.Cli("connection", "deploy", destination, "--write-plan", "--op", "dst.events=window");
            Ok(plan, "plan");
            Ok(pair.Cli("connection", "deploy", "--apply-plan", Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value)), "deploy");
            var ids = async () => await toEngine.RowsAsync("SELECT CAST(id AS VARCHAR(10)) FROM dst.events ORDER BY id");
            Assert.Equal(["1", "2", "3"], await ids());

            // the origin moves: the first day in range is gone and another row arrives on the second; the refresh asks the origin again
            await fromEngine.ExecAsync("DELETE FROM src.events WHERE id = 1");
            await fromEngine.ExecAsync("INSERT INTO src.events VALUES (100, '2026-03-02', 'new')");
            var dry = pair.Cli("connection", "refresh", destination, "daily", "--dry-run");
            Ok(dry, "dry run");
            Assert.Contains("[resolver:source]", dry.Out);
            Assert.Contains("2026-03-02", dry.Out);
            var refresh = pair.Cli("connection", "refresh", destination, "daily");
            Ok(refresh, "refresh");
            Assert.Equal(["1", "100", "2", "3"], await ids());                                                                     // day 03-01 is outside the new window and stays; 03-02 was replaced
            Assert.Contains("resolver:source", string.Join("", await toEngine.RowsAsync("SELECT inputs FROM dbdatabuild.run_log WHERE model = 'dst.events' AND membership = 'window' AND plan_id LIKE 'ref-%'")));
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }
}
