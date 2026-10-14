using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Parameters as values in a model's query, on the real engines: bound by the driver with their declared types, the rendered files untouched by a value change.</summary>
[Trait("Group", "apply")]
public partial class ParametersConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private const string Query = "SELECT id, region FROM staging.t WHERE region = ${project.region} AND d >= ${project.cutoff} AND ts >= ${project.since} AND n < ${project.small} AND id < ${project.limit} AND k > ${project.tiny} AND note <> ${model.note}\n";

    private static string Config(string name, string region, string limit) =>
        $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name} }}\nparameters:\n  region: {region}\n  cutoff: {{ type: DATE, value: \"2024-01-03\" }}\n  since: {{ type: TIMESTAMP, value: \"2024-01-04 00:00:00\" }}\n  limit: {{ type: BIGINT, value: {limit} }}\n  small: {{ type: INTEGER, value: 7 }}\n  tiny: {{ type: SMALLINT, value: 0 }}\n"
        + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "");

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Typed_parameters_are_bound_per_run_and_a_value_change_changes_the_rows_not_the_files(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-params-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        var q = engine.QuoteIdent;
        try
        {
            await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA staging" : "EXEC('CREATE SCHEMA staging')");
            await engine.ExecAsync($"CREATE TABLE staging.t ({q("id")} BIGINT NOT NULL, {q("region")} {engine.ColumnType("VARCHAR(20)")}, {q("d")} DATE, {q("ts")} {engine.ColumnType("TIMESTAMP")}, {q("n")} INTEGER, {q("k")} SMALLINT, {q("note")} {engine.ColumnType("VARCHAR(20)")})");
            await engine.ExecAsync("INSERT INTO staging.t VALUES " +
                "(1, 'eu', '2024-01-03', '2024-01-04 00:00:00', 6, 1, 'x'), " +           // every condition holds
                "(2, 'us', '2024-01-03', '2024-01-04 00:00:00', 6, 1, 'x'), " +           // wrong region
                "(3, 'eu', '2024-01-02', '2024-01-04 00:00:00', 6, 1, 'x'), " +           // date too early
                "(4, 'eu', '2024-01-03', '2024-01-03 23:59:59', 6, 1, 'x'), " +           // timestamp too early
                "(5, 'eu', '2024-01-03', '2024-01-04 00:00:00', 7, 1, 'x'), " +           // int not below the bound
                "(6, 'eu', '2024-01-03', '2024-01-04 00:00:00', 6, 0, 'x'), " +           // smallint not above the bound
                "(7, 'eu', '2024-01-03', '2024-01-04 00:00:00', 6, 1, 'skip'), " +        // the model's own parameter
                "(9, 'eu', '2024-02-01', '2024-02-01 00:00:00', 6, 1, 'x')");
            Write("dbdatabuild.yml", Config(name, "eu", "8"));
            Write("models/staging/t.yml", "name: staging.t\nkind: mapped\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: region, type: \"VARCHAR(20)\"}\n  - {name: d, type: DATE}\n  - {name: ts, type: TIMESTAMP}\n  - {name: n, type: INTEGER}\n  - {name: k, type: SMALLINT}\n  - {name: note, type: \"VARCHAR(20)\"}\n");
            Write("models/marts/m.yml", "name: marts.m\nkind: full\nparameters:\n  note: skip\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: region, type: \"VARCHAR(20)\"}\n");
            Write("models/marts/m.sql", Query);

            Ok(Cli("connection", "init", name, "--apply"), "init");
            Ok(Cli("project", "compile"), "render");
            var plan = Cli("connection", "deploy", name, "--write-plan");
            Ok(plan, "plan");
            var text = File.ReadAllText(PlanOf(plan.Out));
            Assert.Contains("source: \"parameter\"", text);
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(plan.Out)), "apply");
            Assert.Equal(["1"], await engine.RowsAsync("SELECT CAST(id AS VARCHAR(10)) FROM marts.m"));            // only row 1 meets every bound: each value was bound with its own type

            // a new value is a new plan with new rows, and the rendered files did not change
            Write("dbdatabuild.yml", Config(name, "eu", "20"));
            var changed = Cli("project", "compile", "--check");
            Assert.NotEqual(0, changed.Exit);                                                         // only the load plans, which record the values, and the plan that names them differ: the values are bound at run time, not rendered into a script
            var stale = System.Text.RegularExpressions.Regex.Matches(changed.Err, @"error DDB-424\s+(\S+)").Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(2, stale.Count);                                                             // the load plan that records the values, and the plan file that names it by hash
            Assert.Contains(stale, s => s.EndsWith("/loads.yml", StringComparison.Ordinal));
            Assert.Contains(stale, s => s.EndsWith("/refresh/default.yml", StringComparison.Ordinal));
            Ok(Cli("project", "compile"), "compile after the value changed");
            var again = Cli("connection", "deploy", name, "--write-plan");
            Ok(again, "second plan");
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(again.Out)), "second apply");
            Assert.Equal(["1", "9"], await engine.RowsAsync("SELECT CAST(id AS VARCHAR(10)) FROM marts.m"));
            var parameters = await engine.RowsAsync($"SELECT {q("parameters")} FROM {q("dbdatabuild")}.{q("run_log")} WHERE {q("parameters")} IS NOT NULL");
            Assert.Contains(parameters, p => p.Contains("p_project_limit") && p.Contains("20"));                      // the values of a run are in the run log
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
