using System.Text.RegularExpressions;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>A `source` resolver on a copy (docs/research/refresh-plans.md, section 8): it runs at the origin, lowered for the origin's engine, on the origin's read login; the value goes into the plan and is bound to the load.</summary>
public partial class CopyConformanceTests
{
    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task A_source_resolver_runs_at_the_origin_in_its_own_dialect_and_its_value_bounds_the_range_the_copy_loads(string origin, string destination)
    {
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync();
        await to.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), "ddb-copy-" + Guid.NewGuid().ToString("N"));
        var pair = new Pair(from, to, dir);
        await using var fromEngine = from;
        await using var toEngine = to;
        try
        {
            pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\n" + Sensitive(destination));
            pair.Write("models/src/events.yml", $"name: src.events\nkind: mapped\nconnections=: [{origin}]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT, nullable: false}}\n  - {{name: day, type: DATE, nullable: false}}\n");
            pair.Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events}}\n" +
                "loads:\n  window:\n    strategy: delete_insert_by_range\n    column: day\n    params: {start: DATE, end: DATE}\n    inputs:\n" +
                "      start: {resolver: {source: \"SELECT min(day) FROM src.events WHERE day >= DATE '2026-03-01'\"}}\n" +
                "      end: {resolver: {expression: \"DATE '2026-03-04'\"}}\n");
            await from.ExecAsync(origin == "postgres" ? "CREATE SCHEMA src" : "EXEC('CREATE SCHEMA src')");
            await from.ExecAsync($"CREATE TABLE src.events ({from.QuoteIdent("id")} BIGINT NOT NULL, {from.QuoteIdent("day")} DATE NOT NULL)");
            await from.ExecAsync("INSERT INTO src.events VALUES " + string.Join(", ", Enumerable.Range(0, 8).Select(i => $"({i + 1}, '{new DateTime(2026, 2, 27).AddDays(i):yyyy-MM-dd}')")));      // 02-27 .. 03-06

            Ok(pair.Cli("connection", "init", destination, "--apply"), "init");
            Ok(pair.Cli("project", "compile"), "compile");
            // the text for each engine of the origins is in the load plan, beside the name of the input (no file of its own)
            Assert.False(File.Exists(Path.Combine(dir, $"rendered/{destination}/dst.events/input.window.start.{origin}.sql")));
            Assert.Contains($"engine: \"{origin}\"", File.ReadAllText(Path.Combine(dir, $"rendered/{destination}/loads.yml")));
            var plan = pair.Cli("connection", "deploy", destination, "--write-plan", "--op", "dst.events=window");
            Ok(plan, "plan");
            var planFile = Path.Combine(dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            var text = File.ReadAllText(planFile);
            Assert.Contains("planned_source: \"resolver:source\"", text);
            Assert.Contains("planned_value: \"2026-03-01\"", text);                                                                  // the origin's own answer, shown before anything is applied
            Assert.Contains($"origin: \"{origin}\"", text);
            Assert.Contains("start", File.ReadAllText(planFile.Replace(".plan.yml", ".plan.md")));

            Ok(pair.Cli("connection", "deploy", "--apply-plan", planFile), "apply");
            Assert.Equal(["2026-03-01", "2026-03-02", "2026-03-03"], await toEngine.RowsAsync(origin == "postgres" ? "SELECT CAST(day AS VARCHAR(10)) FROM dst.events ORDER BY day" : "SELECT CAST(day AS VARCHAR(10)) FROM dst.events ORDER BY day"));
            Assert.Empty(await toEngine.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_name LIKE 'stg_dst%'"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
