using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Native models (a query the engine runs: a table function, an engine-native select) on the real engines: inlined into a table and a view, bound parameters, a local copy, and a copy to another engine.</summary>
[Trait("Group", "apply")]
public partial class NativeConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private static string NumsText(string engine) => engine == "postgres"
        ? "SELECT CAST(x AS integer) AS n FROM unnest(string_to_array(${project.list}, ',')) AS x"
        : "SELECT CAST(value AS int) AS n FROM STRING_SPLIT(${project.list}, ',')";

    private static string FixedText(string engine) => "SELECT 1 AS n UNION ALL SELECT 2";

    private static string Config(string name) =>
        $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name} }}\nparameters:\n  list: \"1,2,3,4\"\n" +
        (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "");

    private const string Cols = "grain: [n]\ncolumns:\n  - {name: n, type: INTEGER, nullable: false}\n";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_native_select_is_inlined_with_bound_parameters_into_a_table_and_a_view_and_landed_by_a_local_copy(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-native-" + Guid.NewGuid().ToString("N"));
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
        try
        {
            Write("dbdatabuild.yml", Config(name));
            Write("models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    query: |\n      " + NumsText(name) + "\n" + Cols);             // the text inline in the YAML
            Write("models/src/fixed.yml", "name: src.fixed\nkind: native\n" + Cols);                                                       // the text in a file beside it
            Write("models/src/fixed.native.sql", FixedText(name) + "\n");
            Write("models/marts/big.yml", "name: marts.big\nkind: full\n" + Cols);
            Write("models/marts/big.sql", "SELECT n FROM src.nums WHERE n > 1\n");
            Write("models/marts/v.yml", "name: marts.v\nkind: view\n" + Cols);
            Write("models/marts/v.sql", "SELECT n FROM src.fixed\n");
            Write("models/marts/snapshot.yml", "name: marts.snapshot\nkind:\n  copy:\n    from: src.nums\n");                           // a local copy: the native select, landed on its own connection

            Ok(Cli("connection", "init", name, "--apply"), "init");
            Ok(Cli("project", "compile"), "render");
            var script = File.ReadAllText(Path.Combine(dir, "rendered", name, "marts.big", "load.default.sql"));
            Assert.Contains("@p_native_src_nums__project_list", script);                                         // the native's own placeholder, bound at run time
            Assert.DoesNotContain("1,2,3,4", script);
            var plan = Cli("connection", "deploy", name, "--write-plan");
            Ok(plan, "plan");
            Assert.DoesNotContain("type: transfer", File.ReadAllText(PlanOf(plan.Out)));                         // a local copy moves nothing across connections
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(plan.Out)), "apply");
            Assert.Equal(["2", "3", "4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.big"));
            Assert.Equal(["1", "2"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.v"));      // a view over a native select: its text is in the view
            Assert.Equal(["1", "2", "3", "4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.snapshot"));
            Assert.Equal(4, int.Parse((await engine.RowsAsync("SELECT COUNT(*) FROM marts.snapshot")).Single()));

            // a different value is a new plan, the same rendered files
            Write("dbdatabuild.yml", Config(name).Replace("1,2,3,4", "5,6"));
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
            Assert.Equal(["5", "6"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.big"));
            Assert.Equal(["5", "6"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.snapshot"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_native_select_on_one_engine_can_be_copied_to_another()
    {
        var origin = EngineEnv.Require("postgres");
        var destination = EngineEnv.Require("sqlserver");
        await origin.StartAsync(); await destination.StartAsync();
        await using var _o = origin; await using var _d = destination;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-native-copy-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == "DBDATABUILD_POSTGRES_READ" ? origin.ConnectionString : v is "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" ? destination.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        try
        {
            Write("dbdatabuild.yml", "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\nparameters:\n  list: \"7,8,9\"\n");
            Write("models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    query: " + NumsText("postgres").Replace("${project.list}", "${project.list}") + "\nconnections=: [postgres]\n" + Cols);
            Write("models/dst/nums.yml", "name: dst.nums\nkind:\n  copy:\n    from: src.nums\n");
            Ok(Cli("connection", "init", "sqlserver", "--apply"), "init");
            Ok(Cli("project", "compile"), "render");
            var plan = Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(plan, "plan");
            var file = Path.Combine(dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            var text = File.ReadAllText(file);
            Assert.Contains("type: transfer", text);
            Assert.Contains("p_native_src_nums__project_list", text);                                            // the native's parameter travels in the transfer step, bound on the origin
            Assert.DoesNotContain("7,8,9", text.Replace("value: \"7,8,9\"", ""));                                // never in the read text
            Ok(Cli("connection", "deploy", "--apply-plan", file), "apply");
            Assert.Equal(["7", "8", "9"], await destination.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM dst.nums"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static (string Create, string Call) ProcedureFor(string engine) => engine == "postgres"
        ? ("CREATE FUNCTION public.usp_nums(list text) RETURNS TABLE(n integer) LANGUAGE plpgsql AS $$ BEGIN RETURN QUERY SELECT CAST(x AS integer) FROM unnest(string_to_array(list, ',')) AS x; END $$",
           "SELECT * FROM public.usp_nums(${project.list})")
        : ("CREATE PROCEDURE dbo.usp_nums @list varchar(100) AS BEGIN INSERT INTO dbo.side_effect VALUES (1); SELECT CAST(value AS int) AS n FROM STRING_SPLIT(@list, ',') END",
           "EXEC dbo.usp_nums @list = ${project.list}");

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_native_command_is_run_with_bound_parameters_copied_locally_and_leaves_nothing_behind(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-command-" + Guid.NewGuid().ToString("N"));
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
        var schema = name == "postgres" ? "public" : "dbo";
        var (create, call) = ProcedureFor(name);
        try
        {
            await engine.ExecAsync($"DROP TABLE IF EXISTS {schema}.side_effect");
            await engine.ExecAsync($"CREATE TABLE {schema}.side_effect (v int)");
            await engine.ExecAsync(name == "postgres" ? "DROP FUNCTION IF EXISTS public.usp_nums(text)" : "DROP PROCEDURE IF EXISTS dbo.usp_nums");
            await engine.ExecAsync(create);

            Write("dbdatabuild.yml", Config(name) + $"connections:\n  {name}: {{ allow_native_commands: true }}\n");
            Write("models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    access: command\n    query: " + call + "\n" + Cols);
            Write("models/marts/snapshot.yml", "name: marts.snapshot\nkind:\n  copy:\n    from: src.nums\n");                      // on the command's own connection: still a transfer
            Write("models/marts/reader.yml", "name: marts.reader\nkind: full\n" + Cols);
            Write("models/marts/reader.sql", "SELECT n FROM src.nums\n");

            var broken = Cli("project", "compile");
            Assert.NotEqual(0, broken.Exit);
            Assert.Contains("a command can only be run, never read inside a query", broken.Err);                                       // nothing may read it
            File.Delete(Path.Combine(dir, "models", "marts", "reader.yml")); File.Delete(Path.Combine(dir, "models", "marts", "reader.sql"));

            Ok(Cli("connection", "init", name, "--apply"), "init");
            Ok(Cli("project", "compile"), "render");
            var plan = Cli("connection", "deploy", name, "--write-plan");
            Ok(plan, "plan");
            var file = PlanOf(plan.Out);
            var text = File.ReadAllText(file);
            Assert.Contains("type: transfer", text);
            Assert.Contains("command: true", text);
            Assert.Equal("0", (await engine.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM {schema}.side_effect")).Single());          // planning never calls it

            var dry = Cli("connection", "deploy", "--apply-plan", file, "--dry-run", "--allow-risky");
            Ok(dry, "dry run");
            Assert.Equal("0", (await engine.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM {schema}.side_effect")).Single());

            var refused = Cli("connection", "deploy", "--apply-plan", file);
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("--allow-risky", refused.Err);                                                                           // running a procedure is a risky step
            Ok(Cli("connection", "deploy", "--apply-plan", file, "--allow-risky"), "apply");
            Assert.Equal(["1", "2", "3", "4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.snapshot"));
            Assert.Equal("0", (await engine.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM {schema}.side_effect")).Single());          // the call ran in a transaction that was rolled back
            if (name == "postgres")
            {
                // the read login's session is read-only: a function that writes is stopped by the engine
                await engine.ExecAsync("CREATE OR REPLACE FUNCTION public.usp_nums(list text) RETURNS TABLE(n integer) LANGUAGE plpgsql AS $$ BEGIN INSERT INTO public.side_effect VALUES (1); RETURN QUERY SELECT 1; END $$");
                var writing = Cli("connection", "deploy", name, "--write-plan");
                Ok(writing, "plan");
                Assert.NotEqual(0, Cli("connection", "deploy", "--apply-plan", PlanOf(writing.Out), "--allow-risky").Exit);
                Assert.Equal("0", (await engine.RowsAsync("SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM public.side_effect")).Single());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableTheory]
    [InlineData("postgres", "sqlserver")]
    [InlineData("sqlserver", "postgres")]
    public async Task A_native_command_on_one_engine_is_run_there_and_its_rows_are_copied_to_the_other(string originName, string destinationName)
    {
        var origin = EngineEnv.Require(originName);
        var destination = EngineEnv.Require(destinationName);
        await origin.StartAsync(); await destination.StartAsync();
        await using var _o = origin; await using var _d = destination;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-command-copy-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(originName, Login.Read) ? origin.ConnectionString
            : v == LoginSettings.VariableName(destinationName, Login.Read) || v == LoginSettings.VariableName(destinationName, Login.Write) ? destination.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        var schema = originName == "postgres" ? "public" : "dbo";
        var (create, call) = ProcedureFor(originName);
        try
        {
            await origin.ExecAsync($"DROP TABLE IF EXISTS {schema}.side_effect");
            await origin.ExecAsync($"CREATE TABLE {schema}.side_effect (v int)");
            await origin.ExecAsync(originName == "postgres" ? "DROP FUNCTION IF EXISTS public.usp_nums(text)" : "DROP PROCEDURE IF EXISTS dbo.usp_nums");
            await origin.ExecAsync(create);

            Write("dbdatabuild.yml", $"defaults: {{connections: [{destinationName}]}}\ntracking: {{ connection: {destinationName} }}\nconnections:\n  {originName}: {{ allow_native_commands: true }}\nparameters:\n  list: \"7,8,9\"\n" +
                (destinationName == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : ""));
            Write("models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    access: command\n    query: " + call + $"\nconnections=: [{originName}]\n" + Cols);
            Write("models/dst/nums.yml", "name: dst.nums\nkind:\n  copy:\n    from: src.nums\n");
            Ok(Cli("connection", "init", destinationName, "--apply"), "init");
            Ok(Cli("project", "compile"), "render");
            var plan = Cli("connection", "deploy", destinationName, "--write-plan");
            Ok(plan, "plan");
            var file = Path.Combine(dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            var text = File.ReadAllText(file);
            Assert.Contains("type: transfer", text);
            Assert.Contains("command: true", text);
            Assert.Equal("0", (await origin.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM {schema}.side_effect")).Single());          // planning never calls it
            Assert.NotEqual(0, Cli("connection", "deploy", "--apply-plan", file).Exit);                                                                                        // running a procedure is a risky step
            Ok(Cli("connection", "deploy", "--apply-plan", file, "--allow-risky"), "apply");
            Assert.Equal(["7", "8", "9"], await destination.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM dst.nums ORDER BY n"));
            Assert.Equal("0", (await origin.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM {schema}.side_effect")).Single());          // rolled back on the origin
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_native_command_is_refused_on_a_connection_that_does_not_allow_it()
    {
        var engine = EngineEnv.Require("sqlserver");
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-command-no-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v is "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" ? engine.ConnectionString : null;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models", "src"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), Config("sqlserver"));
            File.WriteAllText(Path.Combine(dir, "models", "src", "nums.yml"), "name: src.nums\nkind:\n  native:\n    access: command\n    query: EXEC dbo.usp_nums @list = 'a'\n" + Cols);
            var o = new StringWriter(); var e = new StringWriter();
            Assert.NotEqual(0, CliApp.Run(["project", "compile", "--project", dir], o, e, environment: Env));
            Assert.Contains("allow_native_commands", e.ToString() + o.ToString());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_native_select_that_returns_other_than_it_declares_stops_the_plan_without_running_the_text(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-native-drift-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        try
        {
            Write("dbdatabuild.yml", Config(name));
            Write("models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    query: |\n      " + NumsText(name) + "\n" + Cols);
            Write("models/marts/big.yml", "name: marts.big\nkind: full\n" + Cols);
            Write("models/marts/big.sql", "SELECT n FROM src.nums\n");
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal(0, Cli("project", "compile").Exit);
            var fine = Cli("connection", "deploy", name, "--write-plan");
            Assert.True(fine.Exit == 0, fine.Out + fine.Err);                                                     // the declaration agrees with the engine

            // the text now returns a string and a column of another name
            Write("models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    query: SELECT 'x' AS n, 1 AS extra, 2 AS gone\n" + Cols.Replace("columns:\n", "columns:\n  - {name: gone, type: INTEGER, nullable: false}\n").Replace("grain: [n]", "grain: [n]"));
            var drift = Cli("connection", "deploy", name, "--write-plan");
            Assert.NotEqual(0, drift.Exit);
            Assert.Contains("returns something other than it declares", drift.Err + drift.Out);
            Assert.Contains("column `n` is returned as another type", drift.Err + drift.Out);

            Write("models/src/nums.yml", "name: src.nums\nkind:\n  native:\n    query: SELECT 1 AS m\n" + Cols);
            var missing = Cli("connection", "deploy", name, "--write-plan");
            Assert.NotEqual(0, missing.Exit);
            Assert.Contains("column `n` is not returned", missing.Err + missing.Out);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_routine_changed_in_the_database_is_reported_by_the_next_plan_and_recorded_by_the_next_apply(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-native-def-" + Guid.NewGuid().ToString("N"));
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
        var schema = name == "postgres" ? "public" : "dbo";
        string Function(string filter) => name == "postgres"
            ? $"CREATE OR REPLACE FUNCTION public.fn_tracked(cutoff integer) RETURNS TABLE(n integer) LANGUAGE sql AS $$ SELECT x FROM generate_series(1, 5) AS x WHERE x {filter} cutoff $$"
            : $"CREATE OR ALTER FUNCTION dbo.fn_tracked(@cutoff int) RETURNS TABLE AS RETURN SELECT CAST(value AS int) AS n FROM STRING_SPLIT('1,2,3,4,5', ',') WHERE CAST(value AS int) {filter} @cutoff";
        try
        {
            await engine.ExecAsync(Function(">"));
            var routine = $"{schema}.fn_tracked";
            Write("dbdatabuild.yml", Config(name));
            Write("models/src/tracked.yml", $"name: src.tracked\ntrack_definition: [{routine}, {schema}.fn_missing]\nkind:\n  native:\n    query: SELECT n FROM {routine}(2)\n" + Cols);
            Write("models/marts/snapshot.yml", "name: marts.snapshot\nkind:\n  copy:\n    from: src.tracked\n");

            Ok(Cli("connection", "init", name, "--apply"), "init");
            Ok(Cli("project", "compile"), "render");
            var first = Cli("connection", "deploy", name, "--write-plan");
            Ok(first, "first plan");
            Assert.DoesNotContain("DDB-234", first.Err + first.Out);                                              // nothing recorded yet, nothing to compare with
            Assert.Contains("fn_missing", first.Err + first.Out);                                                 // a routine the engine has no definition for is not checked (DDB-235)
            Assert.Contains("DDB-235", first.Err + first.Out);
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(first.Out)), "first apply");
            Assert.Equal(["3", "4", "5"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.snapshot"));

            var same = Cli("connection", "deploy", name, "--write-plan");
            Ok(same, "plan after apply");
            Assert.DoesNotContain("DDB-234", same.Err + same.Out);                                                // the record matches

            await engine.ExecAsync(Function(">="));                                                               // someone changes the function; no file changes
            var changed = Cli("connection", "deploy", name, "--write-plan");
            Ok(changed, "plan after a change");                                                                   // a warning, not a stop
            Assert.Contains("DDB-234", changed.Err + changed.Out);
            Assert.Contains($"the definition of `{routine}` changed since the last apply", changed.Err + changed.Out);

            Write("dbdatabuild.yml", Config(name) + "policy:\n  severity:\n    native_definition_changed: error\n");
            var refused = Cli("connection", "deploy", name, "--write-plan");
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("DDB-234", refused.Err + refused.Out);
            Write("dbdatabuild.yml", Config(name));

            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(changed.Out)), "apply after the change");
            Assert.Equal(["2", "3", "4", "5"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.snapshot"));
            var settled = Cli("connection", "deploy", name, "--write-plan");
            Ok(settled, "plan after the second apply");
            Assert.DoesNotContain("DDB-234", settled.Err + settled.Out);                                          // the new definition is the record now
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_read_login_without_VIEW_DEFINITION_cannot_check_a_routine_and_says_so_instead_of_reporting_a_change()
    {
        var engine = EngineEnv.Require("sqlserver");
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-native-viewdef-" + Guid.NewGuid().ToString("N"));
        var lowpriv = Regex.Replace(Regex.Replace(engine.ConnectionString, "(?i)(User Id|UID)=[^;]*", "$1=ddb_lowpriv"), "(?i)(Password|PWD)=[^;]*", "$1=Low_Priv_1234!");
        var useLow = false;
        string? Env(string v) => v == LoginSettings.VariableName("sqlserver", Login.Read) ? (useLow ? lowpriv : engine.ConnectionString) : v == LoginSettings.VariableName("sqlserver", Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        try
        {
            await engine.ExecAsync("CREATE OR ALTER FUNCTION dbo.fn_tracked(@cutoff int) RETURNS TABLE AS RETURN SELECT CAST(value AS int) AS n FROM STRING_SPLIT('1,2,3,4,5', ',') WHERE CAST(value AS int) > @cutoff");
            Write("dbdatabuild.yml", Config("sqlserver"));
            Write("models/src/tracked.yml", "name: src.tracked\ntrack_definition: [dbo.fn_tracked]\nkind:\n  native:\n    query: SELECT n FROM dbo.fn_tracked(2)\n" + Cols);
            Write("models/marts/snapshot.yml", "name: marts.snapshot\nkind:\n  copy:\n    from: src.tracked\n");
            Assert.Equal(0, Cli("connection", "init", "sqlserver", "--apply").Exit);
            Assert.Equal(0, Cli("project", "compile").Exit);
            var first = Cli("connection", "deploy", "sqlserver", "--write-plan");
            Assert.True(first.Exit == 0, first.Out + first.Err);
            Assert.True(Cli("connection", "deploy", "--apply-plan", PlanOf(first.Out)).Exit == 0);                                              // the definition is recorded (read by sa)

            // a login that can read the data and the tracking tables, but may not see definitions
            await engine.ExecAsync("IF SUSER_ID('ddb_lowpriv') IS NULL CREATE LOGIN ddb_lowpriv WITH PASSWORD = 'Low_Priv_1234!', CHECK_POLICY = OFF");
            await engine.ExecAsync("IF USER_ID('ddb_lowpriv') IS NULL CREATE USER ddb_lowpriv FOR LOGIN ddb_lowpriv");
            foreach (var schema in new[] { "dbo", "marts", "dbdatabuild" }) await engine.ExecAsync($"GRANT SELECT, EXECUTE ON SCHEMA::{schema} TO ddb_lowpriv");
            useLow = true;
            var plan = Cli("connection", "deploy", "sqlserver", "--write-plan");
            Assert.DoesNotContain("DDB-234", plan.Err + plan.Out);                                               // not "changed": it could not be read
            Assert.Contains("DDB-235", plan.Err + plan.Out);                                                     // not checked, and said so
        }
        finally
        {
            try { await engine.ExecAsync("IF USER_ID('ddb_lowpriv') IS NOT NULL DROP USER ddb_lowpriv"); await engine.ExecAsync("IF SUSER_ID('ddb_lowpriv') IS NOT NULL DROP LOGIN ddb_lowpriv"); } catch (Exception) { /* the container is thrown away */ }
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
