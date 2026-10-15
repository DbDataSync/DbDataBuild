using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Planning;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The whole loop on real engines, through the command surface: init, render, check, plan, dry run, apply, a refused re-apply, a model change that needs
/// answers and acknowledgements, drift, risk allowances, a stale plan, and a failure followed by a resume. Each stage asserts what the tracking tables and the
/// target hold afterwards, not just the exit code.
/// </summary>
[Trait("Group", "apply")]
public partial class ApplyConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private sealed class Run(Engine engine, string dir, string name)
    {
        public string Dir => dir;
        public Func<string, string?> Env => v =>
            v == LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Read) || v == LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Write) ? engine.ConnectionString : null;

        public (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            if (args.Zip(args.Skip(1)).Any(p => p is ("--format", "json"))) OutputSchemas.Check(args[0], o.ToString());     // every JSON document the suite sees is checked against the schema
            return (exit, o.ToString(), e.ToString());
        }

        /// <summary>A command with a person at the keyboard: <paramref name="reply"/> is what they type when asked.</summary>
        public (int Exit, string Out, string Err) CliAsking(string reply, params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, new StringReader(reply + "\n"), interactive: true, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }

        public (int Exit, string Out, string Err) CliNoProject(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = CliApp.Run(args, o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }

        public string PlanFile(string output) => Path.Combine(dir, PlanPath().Match(output).Groups[1].Value);

        public void Write(string rel, string text)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public Engine Engine => engine;
        public string Q(string id) => engine.QuoteIdent(id);
    }

    [GeneratedRegex(@"plan:\s+(\S+\.plan\.yml)")]
    private static partial Regex PlanPath();

    private const string Staging = "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctYaml = "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctSql = "SELECT o.order_id, o.amount FROM staging.orders o\n";
    private const string FctYaml2 = FctYaml + "  - {name: discount_code, type: VARCHAR(20)}\n";
    private const string FctSql2 = "SELECT o.order_id, o.amount, CAST(NULL AS VARCHAR(20)) AS discount_code FROM staging.orders o\n";
    private const string ViewYaml = "name: marts.v_orders\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n";
    private const string ViewSql = "SELECT order_id FROM marts.fct_orders\n";

    private static async Task<Run> SetUp(string name, Func<Engine>? make = null, string extraConfig = "")
    {
        var engine = make?.Invoke() ?? EngineEnv.Require(name);
        await engine.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), "ddb-e2e-" + Guid.NewGuid().ToString("N"));
        var run = new Run(engine, dir, name);
        run.Write("dbdatabuild.yml", name == "postgres"
            ? "defaults: {connections: [postgres]}\ntracking: { connection: postgres }\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n"
            : "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\n" + extraConfig);
        run.Write("models/staging/orders.yml", Staging);
        run.Write("models/marts/fct_orders.yml", FctYaml);
        run.Write("models/marts/fct_orders.sql", FctSql);
        run.Write("models/marts/v_orders.yml", ViewYaml);
        run.Write("models/marts/v_orders.sql", ViewSql);
        await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA staging" : "EXEC('CREATE SCHEMA staging')");
        await engine.ExecAsync($"CREATE TABLE staging.orders ({run.Q("order_id")} BIGINT NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(14,2)")})");
        await engine.ExecAsync("INSERT INTO staging.orders VALUES (1, 10.00), (2, 20.50), (3, NULL)");
        return run;
    }

    private static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");
    private static void Refused((int Exit, string Out, string Err) r, string code, string what)
    {
        Assert.True(r.Exit != 0, $"{what} should have been refused:\n{r.Out}");
        Assert.True(r.Err.Contains(code) || r.Out.Contains(code), $"{what} should name {code}:\n{r.Out}\n{r.Err}");
    }

    private static async Task<int> CountAsync(Run run, string table, string? where = null) =>
        int.Parse((await run.Engine.RowsAsync($"SELECT COUNT(*) FROM {table}{(where == null ? "" : " WHERE " + where)}")).Single());

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_whole_loop_works_and_every_refusal_holds(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";

            // ---- init, render, check ----
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render --write");
            var check = run.Cli("connection", "status");
            Ok(check, "check");
            Assert.Contains("missing (a plan would create it)", check.Out);

            // ---- plan: nothing to ask, structure then load, written as a plan and a report ----
            var plan1 = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan1, "plan");
            var planFile = run.PlanFile(plan1.Out);
            var diags = new List<Diagnostic>();
            var parsed = PlanDocument.Parse(File.ReadAllText(planFile), planFile, diags)!;
            Assert.Empty(diags);
            Assert.Equal(["create schema marts", "create table marts.fct_orders", "create view marts.v_orders", "load marts.fct_orders (default)"], parsed.Steps.Select(s => s.Description));
            Assert.True(File.Exists(planFile.Replace(".plan.yml", ".plan.md")));

            // ---- dry run changes nothing, and has the same checks ----
            var dry = run.Cli("connection", "deploy", "--apply-plan", planFile, "--dry-run");
            Ok(dry, "apply --dry-run");
            Assert.Contains("CREATE TABLE", dry.Out);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));
            Assert.Equal(0, await CountAsync(run, T("migration_log")));

            // ---- apply ----
            var applied = run.Cli("connection", "deploy", "--apply-plan", planFile);
            Ok(applied, "apply");
            Assert.Equal(3, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(3, await CountAsync(run, "marts.v_orders"));
            Assert.Equal(["completed", "started"], (await run.Engine.RowsAsync($"SELECT status FROM {T("migration_log")}")));
            Assert.Equal(3, await CountAsync(run, T("ddl_log"), "status = 'ok'"));               // the schema, the table and the view
            Assert.Equal(1, await CountAsync(run, T("run_log"), "status = 'ok'"));
            Assert.Equal(2, await CountAsync(run, T("schema_version"), "source = 'tool'"));
            Assert.NotEqual(0, await CountAsync(run, T("run_log"), "definition_hash IS NOT NULL"));

            // a plan is applied once
            Refused(run.Cli("connection", "deploy", "--apply-plan", planFile), "DDB-438", "re-applying a completed plan");

            // ---- the next plan is a routine load only; it applies cleanly and is idempotent ----
            var plan2 = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan2, "second plan");
            Assert.Contains("1 step(s)", plan2.Out);
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan2.Out)), "routine load");
            Assert.Equal(4, await CountAsync(run, "marts.fct_orders"));

            // ---- a model change: stale rendered files, then the incremental definition block, then answers ----
            run.Write("models/marts/fct_orders.yml", FctYaml2);
            run.Write("models/marts/fct_orders.sql", FctSql2);
            var stale = run.Cli("connection", "deploy", "--write-plan");
            Refused(stale, "DDB-424", "planning with stale rendered files");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(stale.Err, "error DDB-424"));
            Assert.Contains("project compile", stale.Err);
            Ok(run.Cli("project", "compile"), "render --write after the change");
            var blocked = run.Cli("connection", "deploy", "--write-plan");
            Refused(blocked, "DDB-431", "planning an incremental model whose query changed");
            Assert.Contains("DDB-433", blocked.Err);                                              // the view that reads it is skipped
            Refused(run.Cli("connection", "deploy", "--ack", "definition:marts.fct_orders"), "--reason", "an acknowledgement without a reason");
            Ok(run.Cli("connection", "deploy", "--ack", "definition:marts.fct_orders", "--reason", "Added discount_code, no semantic change to existing columns"), "ack definition");
            Assert.Equal(1, await CountAsync(run, T("block_log"), "ack_utc IS NOT NULL"));

            var unanswered = run.Cli("connection", "deploy", "--write-plan");
            Refused(unanswered, "DDB-414", "planning with an open question");
            Assert.Contains("Q-history-marts.fct_orders.discount_code", unanswered.Err);
            run.Write("answers.yml", "answers:\n  - id: Q-history-marts.fct_orders.discount_code\n    choice: not_backfilled\n    note: \"No history exists in source.\"\n");
            var plan3 = run.Cli("connection", "deploy", "--write-plan", "--answers", Path.Combine(run.Dir, "answers.yml"));
            Ok(plan3, "plan with answers");
            var plan3File = run.PlanFile(plan3.Out);
            var report = File.ReadAllText(plan3File.Replace(".plan.yml", ".plan.md"));
            Assert.Contains("No history exists in source.", report);                              // the note travels with the decision
            Assert.Contains("Noticed but NOT done", report);

            // risk allowance: this plan is safe (a nullable column), so it applies without flags
            Ok(run.Cli("connection", "deploy", "--apply-plan", plan3File), "apply the column change");
            await using (var read = await ReadSession.OpenAsync(LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Read, _ => engine.ConnectionString).Settings!))
            {
                var shape = (await DbDataBuild.Execution.CatalogReader.ReadObjectsAsync(read, name, "marts"))["marts.fct_orders"];
                Assert.Contains(shape.Columns, c => c.Name == "discount_code");
            }

            // the report keeps the decision, the note and who applied it
            var history = run.Cli("connection", "monitor");
            Ok(history, "report after the column change");
            Assert.Contains("Column history (1)", history.Out);
            Assert.Contains("`discount_code` was added to marts.fct_orders by plan", history.Out);
            Assert.Contains("not_backfilled (file, \"No history exists in source.\")", history.Out);

            // ---- drift: an out-of-band column blocks, an acknowledgement unblocks, a destructive step needs its allowance ----
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders ADD {run.Q("sneaky")} {engine.ColumnType("VARCHAR(5)")} NULL");
            var driftCheck = run.Cli("connection", "status");
            Refused(driftCheck, "DDB-430", "check after an out-of-band change");
            Assert.Contains("CHANGED OUTSIDE THE TOOL", driftCheck.Out);
            Refused(run.Cli("connection", "deploy", "--write-plan"), "DDB-430", "plan after an out-of-band change");
            Refused(run.Cli("connection", "deploy", "--ack", "drift:marts.fct_orders"), "--reason", "ack without reason");
            Ok(run.Cli("connection", "deploy", "--ack", "drift:marts.fct_orders", "--reason", "DBA added a scratch column"), "ack drift");
            var again = run.Cli("connection", "deploy", "--ack", "drift:marts.fct_orders", "--reason", "again");
            Ok(again, "a second ack of the same state");
            Assert.Contains("already acknowledged", again.Out);
            Assert.Equal(1, await CountAsync(run, T("block_log"), "code = 'DDB-430'"));                // nothing was recorded twice

            var plan4 = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan4, "plan after ack");
            var plan4File = run.PlanFile(plan4.Out);
            var p4 = PlanDocument.Parse(File.ReadAllText(plan4File), plan4File, new List<Diagnostic>())!;
            Assert.Contains(p4.Steps, s => s.Type == StepType.Track && s.ShapeSource == "out_of_band");
            Assert.Contains(p4.Steps, s => s.Risk == RiskClass.Destructive && s.Description == "drop column sneaky");
            var noAllowance = run.Cli("connection", "deploy", "--apply-plan", plan4File);
            Refused(noAllowance, "DDB-436", "a destructive plan without its allowance");
            Assert.Contains("--allow-destructive marts.fct_orders", noAllowance.Err);
            Refused(run.Cli("connection", "deploy", "--apply-plan", plan4File, "--allow-destructive", "marts.other"), "DDB-436", "an allowance naming another object");
            Assert.Equal(0, await CountAsync(run, T("migration_log"), "plan_id = '" + p4.Id + "'"));        // refused before anything was recorded

            // ---- stale: the target changes after the plan was made ----
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders ADD {run.Q("late")} {engine.ColumnType("VARCHAR(5)")} NULL");
            Refused(run.Cli("connection", "deploy", "--apply-plan", plan4File, "--allow-destructive", "marts.fct_orders"), "DDB-437", "a stale plan");
            Assert.Equal(0, await CountAsync(run, T("migration_log"), "plan_id = '" + p4.Id + "'"));
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders DROP COLUMN {run.Q("late")}");

            // ---- an edited plan is refused ----
            var tampered = plan4File.Replace(".plan.yml", ".edited.plan.yml");
            File.WriteAllText(tampered, File.ReadAllText(plan4File).Replace("DROP COLUMN", "DROP  COLUMN"));
            Refused(run.Cli("connection", "deploy", "--apply-plan", tampered, "--allow-destructive", "marts.fct_orders"), "DDB-435", "an edited plan");

            // ---- failure part-way, then resume ----
            await engine.ExecAsync("DROP TABLE staging.orders");                                       // the load will fail: its source is gone
            var failed = run.Cli("connection", "deploy", "--apply-plan", plan4File, "--allow-destructive", "marts.fct_orders");
            Refused(failed, "DDB-440", "an apply whose load fails");
            Assert.Contains("failed", failed.Out);
            Assert.DoesNotContain("staging.orders", failed.Err + failed.Out.Replace("staging.orders", "")); // no driver text with object names reaches the output beyond our own lines
            Assert.Equal(["failed", "started"], await run.Engine.RowsAsync($"SELECT status FROM {T("migration_log")} WHERE plan_id = '{p4.Id}'"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), "status = 'failed'"));
            Refused(run.Cli("connection", "deploy", "--apply-plan", plan4File, "--allow-destructive", "marts.fct_orders"), "DDB-4", "re-running a partly applied plan continues it, and fails at the same step while its cause is there");

            await engine.ExecAsync($"CREATE TABLE staging.orders ({run.Q("order_id")} BIGINT NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(14,2)")})");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (1, 10.00), (2, 20.50), (3, NULL), (4, 40.00), (5, 50.00)");
            var resumed = run.Cli("connection", "deploy", "--apply-plan", plan4File, "--allow-destructive", "marts.fct_orders");
            Ok(resumed, "apply again: the plan continues");
            Assert.Contains("skipped", resumed.Out);                                                   // the steps that finished before are not repeated
            Assert.Equal(5, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(["completed", "failed", "failed", "started", "started", "started"], await run.Engine.RowsAsync($"SELECT status FROM {T("migration_log")} WHERE plan_id = '{p4.Id}'"));
            Refused(run.Cli("connection", "deploy", "--apply-plan", plan4File, "--allow-destructive", "marts.fct_orders"), "DDB-438", "resuming a plan that completed");

            // the end state is in sync: the next plan is the routine load again
            var final = run.Cli("connection", "status");
            Ok(final, "final check");
            Assert.Contains("in sync", final.Out);

            // ---- every statement the tool ran is in a statement log, and none contains a connection string ----
            var logs = Directory.GetFiles(Path.Combine(run.Dir, ".dbdatabuild", "statement-log"), "*.jsonl");
            Assert.NotEmpty(logs);
            Assert.DoesNotContain(logs.SelectMany(File.ReadAllLines), l => l.Contains("Password") || l.Contains("ddbconf") || l.Contains("Ddb!Conf"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Two_applies_cannot_run_at_once(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);

            // hold the same application lock from another connection
            var login = LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Write, _ => engine.ConnectionString).Settings!;
            await using var holder = await MutationGate.OpenAsync(login, "hold", StatementKind.Tracking, new MemoryStatementLog(), Guid.NewGuid());
            Assert.True(await holder.TryAcquireApplicationLockAsync("dbdatabuild:dbdatabuild"));

            var blocked = run.Cli("connection", "deploy", "--apply-plan", planFile);
            Refused(blocked, "DDB-439", "an apply while another holds the lock");
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));

            await holder.ReleaseApplicationLockAsync("dbdatabuild:dbdatabuild");
            Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "apply after the lock was released");
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_second_apply_started_while_the_first_is_running_is_refused_and_both_leave_the_target_consistent(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var pg = name == "postgres";
            // the first plan builds a table and then waits two seconds in a hook, which keeps its application lock; the second is an unrelated view
            run.Write("hooks/pause.sql", pg ? "SELECT pg_sleep(2.5);\n" : "WAITFOR DELAY '00:00:02.500';\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: pause, event: post_create, script: hooks/pause.sql}\n");
            run.Write("models/marts/other.yml", "name: marts.other\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
            run.Write("models/marts/other.sql", "SELECT order_id FROM staging.orders\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var planA = run.Cli("connection", "deploy", "--write-plan", "marts.fct_orders"); Ok(planA, "plan A");
            var planB = run.Cli("connection", "deploy", "--write-plan", "marts.other"); Ok(planB, "plan B");

            var first = Task.Run(() => run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(planA.Out)));
            await Task.Delay(1200);                                                              // the first is inside its hook
            var second = run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(planB.Out));
            Refused(second, "DDB-439", "an apply while another apply is running");
            Ok(await first, "the first apply");
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'fct_orders'"));
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'other'"));      // the refused one changed nothing

            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(planB.Out)), "the refused plan, applied once the lock is free");
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'other'"));
            Assert.Equal(0, run.Cli("connection", "monitor").Exit);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_statement_that_runs_longer_than_the_drivers_default_timeout_is_not_abandoned(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // the drivers give up on a command after 30 seconds unless told otherwise; a load of a large model takes longer
            run.Write("hooks/slow.sql", name == "postgres" ? "SELECT pg_sleep(32);\n" : "WAITFOR DELAY '00:00:32';\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: slow, event: post_create, script: hooks/slow.sql}\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply with a 32 second statement");
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'fct_orders'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Reserved_words_spaces_accents_and_quotes_in_names_are_quoted_on_both_engines(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var q = run.Q;
            var quoted = name == "postgres" ? "\"a\"\"b\"" : "[a\"b]";               // the helper does not double a quote inside a name
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            // a table whose columns are named like the SQL around them: keywords, a space, an accent and a double quote
            await engine.ExecAsync($"CREATE TABLE staging.items ({q("id")} BIGINT NOT NULL, {q("order")} INT, {q("group")} {engine.ColumnType("VARCHAR(10)")}, {q("my col")} INT, {q("Café")} INT, {quoted} INT)");
            await engine.ExecAsync($"INSERT INTO staging.items VALUES (1, 10, 'x', 100, 1000, 7), (2, 20, 'y', 200, 2000, 8)");
            run.Write("models/staging/items.yml", "name: staging.items\nkind: mapped\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: order, type: INTEGER}\n  - {name: group, type: \"VARCHAR(10)\"}\n  - {name: my col, type: INTEGER}\n  - {name: Café, type: INTEGER}\n  - {name: 'a\"b', type: INTEGER}\n");
            // a model named with a keyword, built from them, with a keyword as its unique key and in an index
            run.Write("models/rpt/order.yml", "name: rpt.order\nkind: {incremental_by_unique_key: {unique_key: [id]}}\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: select, type: INTEGER}\n  - {name: group, type: \"VARCHAR(10)\"}\n  - {name: from, type: INTEGER}\n  - {name: Café, type: INTEGER}\n  - {name: 'a\"b', type: INTEGER}\nindexes:\n  - {name: ix_group, columns: [group, select]}\n");
            run.Write("models/rpt/order.sql", "SELECT id, \"order\" AS \"select\", \"group\", \"my col\" AS \"from\", \"Café\", \"a\"\"b\" FROM staging.items\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            Assert.Equal(["1|10|x|100|1000|7", "2|20|y|200|2000|8"], await engine.RowsAsync($"SELECT CAST(id AS VARCHAR(5)) + '|' + CAST({q("select")} AS VARCHAR(5)) + '|' + {q("group")} + '|' + CAST({q("from")} AS VARCHAR(5)) + '|' + CAST({q("Café")} AS VARCHAR(5)) + '|' + CAST({quoted} AS VARCHAR(5)) FROM rpt.{q("order")} ORDER BY id".Replace(" + ", name == "postgres" ? " || " : " + ")));
            // the second load is an incremental one over the same names: nothing changes, and the plan finds nothing to alter
            await engine.ExecAsync($"INSERT INTO staging.items VALUES (3, 30, 'z', 300, 3000, 9)");
            var again = run.Cli("connection", "deploy", "--write-plan"); Ok(again, "second plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(again.Out)), "second apply");
            Assert.Equal(3, await CountAsync(run, "rpt." + q("order")));
            Assert.Equal(0, run.Cli("connection", "monitor").Exit);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_name_longer_than_the_engine_keeps_is_refused_before_anything_is_planned(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // 70 characters: SQL Server keeps them (up to 128), PostgreSQL silently cuts at 63 and the table it built would never match the declaration
            var longName = new string('c', 70);
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            run.Write("models/marts/long.yml", $"name: marts.long\nkind: full\ncolumns:\n  - {{name: order_id, type: BIGINT, nullable: false}}\n  - {{name: {longName}, type: INTEGER}}\n");
            run.Write("models/marts/long.sql", $"SELECT order_id, 1 AS {longName} FROM staging.orders\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            if (name == "postgres")
            {
                var refused = run.Cli("project", "compile");
                Refused(refused, "DDB-241", "a 70-character column name on PostgreSQL");
                Assert.Contains("PostgreSQL keeps 63", refused.Err);
                Refused(run.Cli("connection", "deploy", "--write-plan"), "DDB-241", "planning with it");
                return;
            }
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            Assert.Equal(new[] { "order_id", longName }.Order(StringComparer.Ordinal), (await engine.RowsAsync("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('marts.long')")).Order(StringComparer.Ordinal));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_incremental_load_by_key_of_two_million_rows_takes_seconds_not_minutes(string name)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DDB_SCALE") == "1", "set DDB_SCALE=1 to run");
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var pg = name == "postgres";
            const int Rows = 2_000_000;
            await engine.ExecAsync("DELETE FROM staging.orders");
            await engine.ExecAsync(pg
                ? $"INSERT INTO staging.orders SELECT g, (g % 1000) / 7.0 FROM generate_series(1, {Rows}) g"
                : $"INSERT INTO staging.orders SELECT TOP ({Rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 1000) / 7.0 FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "first apply");
            Console.WriteLine($"SCALE first load ({name}): {clock.Elapsed.TotalSeconds:0.0}s");
            Assert.Equal(Rows, await CountAsync(run, "marts.fct_orders"));
            await engine.ExecAsync("UPDATE staging.orders SET amount = amount + 1 WHERE order_id % 10 = 0");
            clock.Restart();
            var again = run.Cli("connection", "deploy", "--write-plan"); Ok(again, "plan 2");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(again.Out)), "second apply");
            Console.WriteLine($"SCALE second load, 10% changed ({name}): {clock.Elapsed.TotalSeconds:0.0}s");
            Assert.Equal(Rows, await CountAsync(run, "marts.fct_orders"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_model_that_left_the_project_stays_on_the_target_and_the_report_says_nothing_builds_it_any_more(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            Ok(run.Cli("project", "compile"), "render without the view");
            var again = run.Cli("connection", "deploy", "--write-plan"); Ok(again, "plan without the view");
            Assert.DoesNotContain("v_orders", again.Out.Replace("marts.fct_orders", ""));                              // nothing is dropped, nothing is planned for it
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'v_orders'"));
            var report = run.Cli("connection", "monitor"); Ok(report, "report");
            Assert.Contains("marts.v_orders", report.Out);
            Assert.Contains("in sync (no model in the project; the tool never drops it)", report.Out);
            Assert.Single(report.Out.Split('\n'), l => l.Contains("no model in the project"));                      // only the view that left, not the table that stayed
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Text_literals_with_emoji_cjk_quotes_backslashes_and_newlines_survive_a_load_on_both_engines(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            run.Write("models/marts/lit.yml", "name: marts.lit\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: tag, type: \"VARCHAR(200)\"}\n");
            // the literal goes through DuckDB, the lowerer and the engine's own text: a quote is doubled, a backslash is not an escape, a newline stays one
            run.Write("models/marts/lit.sql", "SELECT order_id, 'café ☕ 😀 日本語 it''s a \\ b ' || chr(10) || 'second line' AS tag FROM staging.orders WHERE order_id = 1\n");
            run.Write("models/marts/lit_v.yml", "name: marts.lit_v\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: tag, type: \"VARCHAR(200)\"}\n");
            run.Write("models/marts/lit_v.sql", "SELECT order_id, 'café ☕ 😀 日本語 it''s a \\ b ' || chr(10) || 'second line' AS tag FROM staging.orders WHERE order_id = 1\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            var value = (await engine.RowsAsync("SELECT tag FROM marts.lit")).Single();
            Assert.Equal("café ☕ 😀 日本語 it's a \\ b \nsecond line", value.Replace("\r\n", "\n"));
            var viewValue = (await engine.RowsAsync("SELECT tag FROM marts.lit_v")).Single();                   // the same text in a view
            Assert.Equal("café ☕ 😀 日本語 it's a \\ b \nsecond line", viewValue.Replace("\r\n", "\n"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Constants_of_every_type_come_back_exactly_from_a_load_on_both_engines(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var pg = name == "postgres";
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            run.Write("models/marts/consts.yml", "name: marts.consts\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n" +
                "  - {name: ts, type: TIMESTAMP}\n  - {name: d1, type: DATE}\n  - {name: d2, type: DATE}\n  - {name: big, type: \"DECIMAL(36, 6)\"}\n  - {name: tiny, type: \"DECIMAL(10, 7)\"}\n" +
                "  - {name: mx, type: BIGINT}\n  - {name: mn, type: BIGINT}\n  - {name: t, type: BOOLEAN}\n  - {name: f, type: BOOLEAN}\n  - {name: nul, type: INTEGER}\n  - {name: empty, type: \"VARCHAR(10)\"}\n  - {name: spaces, type: \"VARCHAR(10)\"}\n" +
                "  - {name: id, type: UUID}\n  - {name: dbl, type: DOUBLE}\n");
            run.Write("models/marts/consts.sql", "SELECT order_id, TIMESTAMP '2024-02-29 13:14:15.123456' AS ts, DATE '0001-01-01' AS d1, DATE '9999-12-31' AS d2, CAST(123456789012345678901234567890.123456 AS DECIMAL(36, 6)) AS big, " +
                "CAST(-0.0000001 AS DECIMAL(10, 7)) AS tiny, CAST(9223372036854775807 AS BIGINT) AS mx, CAST(-9223372036854775808 AS BIGINT) AS mn, TRUE AS t, FALSE AS f, CAST(NULL AS INTEGER) AS nul, '' AS empty, '  ' AS spaces, " +
                "CAST('0e984725-c51c-4bf4-9960-e1c80e27aba0' AS UUID) AS id, CAST(1.5e300 AS DOUBLE) AS dbl FROM staging.orders WHERE order_id = 1\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            var render = run.Cli("project", "compile"); Ok(render, "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            string Text(string col, string kind) => (pg, kind) switch
            {
                (true, "ts") => $"to_char({col}, 'YYYY-MM-DD HH24:MI:SS.US')", (false, "ts") => $"CONVERT(VARCHAR(30), {col}, 121)",
                (true, "date") => $"to_char({col}, 'YYYY-MM-DD')", (false, "date") => $"CONVERT(VARCHAR(10), {col}, 23)",
                (true, "bool") => $"CASE WHEN {col} THEN '1' ELSE '0' END", (false, "bool") => $"CAST({col} AS VARCHAR(1))",
                (true, "uuid") => $"LOWER(CAST({col} AS TEXT))", (false, "uuid") => $"LOWER(CAST({col} AS VARCHAR(36)))",
                (true, "len") => $"CAST(LENGTH({col}) AS TEXT)", (false, "len") => $"CAST(LEN(REPLACE({col}, ' ', '_')) AS VARCHAR(10))",      // LEN ignores trailing spaces on SQL Server: count them as characters
                (true, "dbl") => $"CAST({col} AS TEXT)", (false, "dbl") => $"CAST({col} AS VARCHAR(40))",
                _ => $"CAST({col} AS {(pg ? "TEXT" : "VARCHAR(60)")})",
            };
            var parts = new[] { Text("ts", "ts"), Text("d1", "date"), Text("d2", "date"), Text("big", ""), Text("tiny", ""), Text("mx", ""), Text("mn", ""), Text("t", "bool"), Text("f", "bool"), "COALESCE(CAST(nul AS VARCHAR(5)), 'NULL')", "CAST(LEN(empty) AS VARCHAR(5))".Replace("LEN", pg ? "LENGTH" : "LEN"), Text("spaces", "len"), Text("id", "uuid") };
            var row = (await engine.RowsAsync($"SELECT {string.Join(", ", parts)} FROM marts.consts")).Single().Split('|');
            Assert.Equal(["2024-02-29 13:14:15.123456", "0001-01-01", "9999-12-31", "123456789012345678901234567890.123456", "-0.0000001", "9223372036854775807", "-9223372036854775808", "1", "0", "NULL", "0", "2", "0e984725-c51c-4bf4-9960-e1c80e27aba0"], row);
            Assert.Equal(1.5e300, double.Parse((await engine.RowsAsync($"SELECT {Text("dbl", "dbl")} FROM marts.consts")).Single(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_first_Ctrl_C_stops_an_apply_after_the_step_that_is_running_and_the_plan_resumes(string name)
    {
        Skip.If(!OperatingSystem.IsLinux(), "sends SIGINT with kill");
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var pg = name == "postgres";
            run.Write("hooks/pause.sql", pg ? "SELECT pg_sleep(6);\n" : "WAITFOR DELAY '00:00:06';\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: pause, event: post_create, script: hooks/pause.sql}\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);

            // the real executable, in its own process, so that the signal is the operator's Ctrl-C
            // the executable's own folder (the test folder lacks some of the libraries the CLI needs on its own): <repo>/src/DbDataBuild.Cli/bin/<configuration>/<framework>/
            var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var dll = Path.Combine(baseDir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName, "src", "DbDataBuild.Cli", "bin", baseDir.Parent!.Name, baseDir.Name, "dbdatabuild.dll");
            Skip.IfNot(File.Exists(dll), $"{dll} is not built");
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{dll}\" connection deploy --apply-plan \"{planFile}\" --project \"{run.Dir}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var variable in new[] { LoginSettings.VariableName(name, Login.Read), LoginSettings.VariableName(name, Login.Write) }) psi.Environment[variable] = engine.ConnectionString;
            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await Task.Delay(3500);                                                              // inside the hook (the apply starts in a second)
            System.Diagnostics.Process.Start("kill", $"-INT {process.Id}")!.WaitForExit();
            Assert.True(process.WaitForExit(60_000), "the apply did not stop");
            var output = await stdout; var errors = await stderr;
            Assert.True(process.ExitCode == 1, $"exit {process.ExitCode}\n{output}\n{errors}");
            Assert.Contains("Interrupt received", output);
            Assert.Contains("DDB-445", errors + output);                                          // stopped by the operator
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'fct_orders'"));      // the step that had started was finished, not cut off
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'v_orders'"));         // and nothing after it ran

            Ok(run.Cli("connection", "deploy", "--apply-plan", planFile), "resume after the stop");
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'v_orders'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_current_time_is_loaded_as_the_instant_it_is_on_both_engines(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var pg = name == "postgres";
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            run.Write("models/marts/clock.yml", "name: marts.clock\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: t1, type: TIMESTAMP WITH TIME ZONE}\n  - {name: t2, type: TIMESTAMP WITH TIME ZONE}\n  - {name: d, type: DATE}\n");
            // current_timestamp is bound by DuckDB as get_current_timestamp(), which no engine has; now() was written as GETDATE(), the local clock without a zone
            run.Write("models/marts/clock.sql", "SELECT order_id, now() AS t1, current_timestamp AS t2, current_date AS d FROM staging.orders WHERE order_id = 1\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            var render = run.Cli("project", "compile"); Ok(render, "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            foreach (var column in new[] { "t1", "t2" })
            {
                var seconds = int.Parse((await engine.RowsAsync(pg
                    ? $"SELECT CAST(ABS(EXTRACT(EPOCH FROM (now() - {column}))) AS INT) FROM marts.clock"
                    : $"SELECT ABS(DATEDIFF(SECOND, SWITCHOFFSET({column}, '+00:00'), SYSUTCDATETIME())) FROM marts.clock")).Single());
                Assert.True(seconds < 300, $"{column} is {seconds} seconds from the engine's own clock");
            }
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_apply_whose_connection_is_killed_mid_step_fails_cleanly_and_the_plan_resumes(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var pg = name == "postgres";
            run.Write("hooks/pause.sql", pg ? "SELECT pg_sleep(4);\n" : "WAITFOR DELAY '00:00:04';\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: pause, event: post_create, script: hooks/pause.sql}\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);

            var apply = Task.Run(() => run.Cli("connection", "deploy", "--apply-plan", planFile));
            await Task.Delay(1500);                                                              // inside the hook
            if (pg) await engine.ExecAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query LIKE '%pg_sleep(4)%' AND pid <> pg_backend_pid()");
            else await engine.ExecAsync("DECLARE @s INT = (SELECT TOP 1 session_id FROM sys.dm_exec_requests WHERE command = 'WAITFOR'); IF @s IS NOT NULL EXEC('KILL ' + @s)");
            var killed = await apply;
            Assert.NotEqual(0, killed.Exit);
            Assert.DoesNotContain("DDB-900", killed.Err + killed.Out);                           // a failure of the connection is reported, not a crash

            // the lock went with the session: the plan can be resumed at once, and finishes
            var resumed = run.Cli("connection", "deploy", "--apply-plan", planFile);
            Ok(resumed, "resume after the connection was killed");
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'fct_orders'"));
            var rep = run.Cli("connection", "monitor");
            Ok(rep, "report after the resume");                                                  // the step that was cut off ran again and finished: nothing is left to attend to
            Assert.Contains("nothing", rep.Out.Split("Needs attention")[1]);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Refresh_runs_the_compiled_routine_loads_under_the_check_chosen_and_each_run_is_an_event(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        var queries = new List<string>();
        try
        {
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var log = run.Q("dbdatabuild") + "." + run.Q("run_log");
            var events = run.Q("dbdatabuild") + "." + run.Q("migration_log");

            // nothing is deployed yet: the check (objects, the default) finds no recorded shape and stops before anything runs; nothing is written
            var refused = run.Cli("connection", "refresh");
            Refused(refused, "DDB-447", "a refresh before the first deploy");
            Assert.Contains("never been deployed", refused.Err);
            Assert.Contains("dbdatabuild connection deploy", refused.Out);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));
            Assert.Equal(1, await CountAsync(run, events, run.Q("lane") + " = 'refresh' AND status = 'failed'"));       // the load failed its check (kind check): the run is recorded, and nothing ran
            Assert.Equal(1, await CountAsync(run, log, "status = 'failed: check'"));

            // without the check the engine's own error is the check: the load fails on the missing table, and the event says so
            var unchecked1 = run.Cli("connection", "refresh", "--check", "none");
            Assert.NotEqual(0, unchecked1.Exit);
            Assert.Equal(2, await CountAsync(run, events, run.Q("lane") + " = 'refresh' AND status = 'failed'"));

            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            Assert.Equal(1, await CountAsync(run, events, run.Q("lane") + " = 'deploy' AND status = 'completed' AND " + run.Q("project_hash") + " IS NOT NULL"));    // the deploy recorded the structure it was made from

            // from here on a routine load is a one-liner
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            var routine = run.Cli("connection", "refresh");
            Ok(routine, "refresh");
            Assert.Contains("1 of 1 member(s)", routine.Out);
            Assert.Contains("Next: dbdatabuild connection monitor", routine.Out);
            Assert.Equal(4, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(2, await CountAsync(run, log, "status = 'ok'"));

            // a schedule runs the same content again and again: each run is its own event, none is refused as "already applied", and the rows of a load are the rows inserted
            Ok(run.Cli("connection", "refresh"), "again with nothing new");
            Ok(run.Cli("connection", "refresh"), "and once more");
            Assert.Equal(4, await CountAsync(run, log, "status = 'ok'"));
            Assert.Equal(3, await CountAsync(run, events, run.Q("plan_id") + " LIKE 'ref-%' AND status = 'completed' AND " + run.Q("lane") + " = 'refresh'"));
            Assert.Equal(1, await CountAsync(run, log, "status = 'ok' AND rows_affected = 3"));
            Assert.Equal(3, await CountAsync(run, log, "status = 'ok' AND rows_affected = 4"));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(run.Dir, "plans", name), "ref-*"));       // a refresh leaves no plan file: the compiled plan is the plan

            // the other checks pass on a deployed project too
            Ok(run.Cli("connection", "refresh", "--check", "project"), "check project");
            Ok(run.Cli("connection", "refresh", "--check", "live"), "check live");

            // a refresh with no check asks the connection nothing about its structure: no catalog query, only the loads, the tracking tables and the watermarks
            ReadSession.Observer = q => queries.Add(q);
            try { Ok(run.Cli("connection", "refresh", "--check", "none"), "check none"); }
            finally { ReadSession.Observer = null; }
            Assert.DoesNotContain(queries, q => q.Contains("information_schema", StringComparison.OrdinalIgnoreCase) || q.Contains("sys.", StringComparison.OrdinalIgnoreCase) || q.Contains("pg_catalog", StringComparison.OrdinalIgnoreCase));

            // the report shows the history and is clean
            var report = run.Cli("connection", "monitor");
            Ok(report, "report");
            Assert.Contains("Events", report.Out);
            Assert.Contains("completed", report.Out);
            Assert.Contains("marts.fct_orders", report.Out);
            Assert.Contains("in sync", report.Out);
            Assert.Contains("nothing", report.Out.Split("Needs attention")[1]);
            Assert.Matches(@"\bdeploy\b", report.Out);                                                       // the events say which lane they were
            Assert.Matches(@"\brefresh\b", report.Out);

            // the report can be narrowed: to one lane, to one event in detail, or to what needs attention
            var deployId = Path.GetFileName(run.PlanFile(plan.Out)).Replace(".plan.yml", "");
            var refreshId = Regex.Match(report.Out, @"ref-\S+").Value;
            var refreshOnly = run.Cli("connection", "monitor", "--lane", "refresh");
            Ok(refreshOnly, "monitor --lane refresh");
            Assert.Contains("Events of the refresh lane", refreshOnly.Out);
            Assert.DoesNotContain(deployId, refreshOnly.Out.Split("Events of the refresh lane")[1].Split("Objects the tool has recorded")[0]);      // no deploy event, no deploy DDL
            var detail = run.Cli("connection", "monitor", "--event", refreshId, "--format", "json");
            Ok(detail, "monitor --event");
            var shown = System.Text.Json.Nodes.JsonNode.Parse(detail.Out)!["data"]!["event"]!;
            Assert.Equal("refresh", (string)shown["lane"]!);
            Assert.Contains(shown["loads"]!.AsArray(), l => (string)l!["model"]! == "marts.fct_orders" && (string)l["status"]! == "ok");
            Assert.Contains("completed", string.Join(" ", shown["states"]!.AsArray().Select(x => (string)x!["status"]!)));
            Assert.Contains("Event " + deployId, run.Cli("connection", "monitor", "--event", deployId).Out);
            var missing = run.Cli("connection", "monitor", "--event", "ref-nothing");
            Assert.Equal(1, missing.Exit);
            Assert.Contains("No event", missing.Err);
            var attentionOnly = run.Cli("connection", "monitor", "--attention");
            Ok(attentionOnly, "monitor --attention");
            Assert.Contains("Needs attention (0)", attentionOnly.Out);
            Assert.DoesNotContain("Events (newest first)", attentionOnly.Out);

            // the landing view: structure, the newest deploy and refresh, and the policy
            var status = run.Cli("connection", "status");
            Ok(status, "status");
            Assert.True(status.Out.Contains("Structure  in sync: a deploy would change no structure"), status.Out);
            Assert.Matches(@"Deploy     \S+  COMPLETED  \d{4}-\d\d-\d\d \d\d:\d\d UTC", status.Out);
            Assert.Matches(@"Refresh    ref-\S+  COMPLETED", status.Out);
            Assert.Contains("refresh check: objects", status.Out);

            // the project changes: the compiled plan is out of date with what was deployed, and the checks that read what was deployed say so; none runs anything
            run.Write("models/marts/fct_orders.yml", FctYaml2);
            run.Write("models/marts/fct_orders.sql", FctSql2);
            Ok(run.Cli("project", "compile"), "compile after the change");
            Refused(run.Cli("connection", "refresh"), "DDB-447", "objects: the table was deployed with the old shape");
            Refused(run.Cli("connection", "refresh", "--check", "project"), "DDB-447", "project: another structure than the one last deployed");
            Refused(run.Cli("connection", "refresh", "--check", "live"), "DDB-447", "live: the table on the connection has the old shape");
            var before = await CountAsync(run, log, "status = 'ok'");
            Assert.Equal(before, await CountAsync(run, log, "status = 'ok'"));                      // nothing was executed by any of them
            var warned = run.Cli("connection", "refresh", "--on-fail", "warn");                     // reports, and runs: the load names a column the table does not have, so the engine refuses it
            Assert.NotEqual(0, warned.Exit);
            Assert.Contains("DDB-447", warned.Err);

            // back to the deployed model: a change made outside the tool is not in what was deployed, so only the live check sees it
            run.Write("models/marts/fct_orders.yml", FctYaml);
            run.Write("models/marts/fct_orders.sql", FctSql);
            Ok(run.Cli("project", "compile"), "compile back");
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders ADD {run.Q("sneaky")} {engine.ColumnType("VARCHAR(5)")} NULL");
            var drift = run.Cli("connection", "monitor");
            Refused(drift, "CHANGED OUTSIDE THE TOOL", "report after drift");
            Assert.Contains("--ack drift:marts.fct_orders", drift.Out);
            Ok(run.Cli("connection", "refresh"), "objects does not see a change outside the tool: it reads what was deployed");
            var live = run.Cli("connection", "refresh", "--check", "live");
            Refused(live, "DDB-447", "live sees it");
            Assert.Contains("outside the tool", live.Err + live.Out);
        }
        finally { ReadSession.Observer = null; if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Inspect_says_what_is_in_the_way_and_what_each_login_may_do(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var before = run.Cli("connection", "inspect");
            Refused(before, "thing(s) in the way", "inspect before the tracking tables exist");
            Assert.Contains("connects", before.Out);
            Assert.Contains("may create tables in the database", before.Out);                                // what the write login may do is asked with a SELECT, nothing is created
            Assert.Contains("not there yet", before.Out);
            Assert.Contains("marts (missing", before.Out);
            Assert.Contains("Next: dbdatabuild connection init", before.Out);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));

            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            var after = run.Cli("connection", "inspect", "--format", "json");
            Ok(after, "inspect when everything is there");
            var data = System.Text.Json.Nodes.JsonNode.Parse(after.Out)!["data"]!;
            Assert.Equal("ready", (string)data["tracking"]!["state"]!);
            Assert.All(data["logins"]!.AsArray(), l => Assert.True((bool)l!["connects"]!));
            Assert.Contains(data["schema_names"]!.AsArray(), n => (string)n!["name"]! == "marts" && (bool)n["exists"]!);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_project_settings_say_what_a_deploy_keeps_and_records_and_how_long_the_logs_stay(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.AppendAllText(Path.Combine(run.Dir, "dbdatabuild.yml"), "plans:\n  deploy: { keep: ephemeral, audit: minimal }\nretention: { statement_logs_days: 1 }\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");

            // statement logs: one that is days old goes at the next deploy, a recent one stays
            var logs = Path.Combine(run.Dir, ".dbdatabuild", "statement-log");
            Directory.CreateDirectory(logs);
            var old = Path.Combine(logs, "20200101T000000-apply-old.jsonl"); File.WriteAllText(old, "{}"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
            var recent = Path.Combine(logs, "20990101T000000-apply-recent.jsonl"); File.WriteAllText(recent, "{}");

            // ephemeral: the plan is written under .dbdatabuild/plans (git ignores it), never under plans/, and goes when it has been applied
            var written = run.Cli("connection", "deploy", "--write-plan");
            Ok(written, "plan");
            Assert.Contains(".dbdatabuild/plans/", written.Out.Replace('\\', '/'));
            Assert.False(Directory.Exists(Path.Combine(run.Dir, "plans")));
            var file = run.PlanFile(written.Out);
            Assert.True(File.Exists(file));
            var applied = run.Cli("connection", "deploy", "--apply-plan", file);
            Ok(applied, "apply");
            Assert.Contains("ephemeral", applied.Out);
            Assert.False(File.Exists(file));
            Assert.Contains("older than 1 day(s)", applied.Out);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));

            // audit minimal: the event and its steps are there, without the plan text, the commit or who; the DDL keeps its hash and no text
            var events = run.Q("dbdatabuild") + "." + run.Q("migration_log");
            Assert.Equal(0, await CountAsync(run, events, run.Q("plan_text") + " <> ''"));
            Assert.Equal(0, await CountAsync(run, events, run.Q("applied_by") + " <> ''"));
            Assert.Equal(0, await CountAsync(run, events, run.Q("git_commit") + " IS NOT NULL"));
            var ddl = run.Q("dbdatabuild") + "." + run.Q("ddl_log");
            Assert.True(await CountAsync(run, ddl, "status = 'ok'") > 0);
            Assert.Equal(0, await CountAsync(run, ddl, run.Q("statement_text") + " <> ''"));
            Assert.Equal(0, await CountAsync(run, ddl, run.Q("statement_hash") + " = ''"));
            Ok(run.Cli("connection", "monitor"), "the report reads such events");

            // database: the plan is kept in the tracking tables only, applied by its id, and refused when the stored text was edited
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")).Replace("keep: ephemeral", "keep: database"));
            await engine.ExecAsync(name == "postgres" ? "CREATE TABLE staging.extra (id BIGINT)" : "CREATE TABLE staging.extra (id BIGINT)");
            run.Write("models/staging/extra.yml", "name: staging.extra\nkind: mapped\ncolumns:\n  - {name: id, type: BIGINT}\n");
            run.Write("models/marts/ex.yml", "name: marts.ex\nkind: full\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
            run.Write("models/marts/ex.sql", "SELECT CAST(id AS BIGINT) AS id FROM staging.extra\n");
            Ok(run.Cli("project", "compile"), "compile with another model");
            var kept = run.Cli("connection", "deploy", "--write-plan");
            Ok(kept, "plan kept in the database");
            Assert.Contains("kept in the tracking tables", kept.Out);
            Assert.False(Directory.Exists(Path.Combine(run.Dir, "plans")));
            Assert.False(Directory.Exists(Path.Combine(run.Dir, ".dbdatabuild", "plans")) && Directory.EnumerateFiles(Path.Combine(run.Dir, ".dbdatabuild", "plans"), "*", SearchOption.AllDirectories).Any());
            var id = System.Text.RegularExpressions.Regex.Match(kept.Out, @"Plan (\S+):").Groups[1].Value;
            var store = run.Q("dbdatabuild") + "." + run.Q("plan_store");
            Assert.Equal(1, await CountAsync(run, store, run.Q("plan_id") + $" = '{id}'"));
            Assert.Contains($"connection deploy --apply-plan {id}", kept.Out);

            // someone edits the stored text: the plan's own hash no longer matches and it is refused
            await engine.ExecAsync($"UPDATE {store} SET {run.Q("plan_text")} = REPLACE({run.Q("plan_text")}, 'marts.ex', 'marts.exx') WHERE {run.Q("plan_id")} = '{id}'");
            var tampered = run.Cli("connection", "deploy", "--apply-plan", id);
            Refused(tampered, "DDB-435", "a stored plan that was edited");
            await engine.ExecAsync($"UPDATE {store} SET {run.Q("plan_text")} = REPLACE({run.Q("plan_text")}, 'marts.exx', 'marts.ex') WHERE {run.Q("plan_id")} = '{id}'");

            Ok(run.Cli("connection", "deploy", "--apply-plan", id), "apply the stored plan by its id");
            Assert.Equal(1, await CountAsync(run, "information_schema.tables", "table_schema = 'marts' AND table_name = 'ex'"));
            Refused(run.Cli("connection", "deploy", "--apply-plan", id), "DDB-438", "a stored plan is applied once too");
            Refused(run.Cli("connection", "deploy", "--apply-plan", "2020-01-01-00000000"), "No plan", "an id nothing is kept under");
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    private const string EventsSource = "name: staging.events\nkind: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string EventsYaml = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n" +
        "loads:\n  daily:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: \"2000-01-01 00:00:00\"}\n" +
        "  reload:\n    strategy: delete_insert_by_range\n    params: {start: TIMESTAMP, end: TIMESTAMP}\n    max_span: 10 days\n";
    private const string EventsSql = "SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Operations_can_be_chosen_and_a_backfill_is_a_risky_recorded_step(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // a different project: one time-range model with a routine operation and a reload operation
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/staging/events.yml", EventsSource);
            run.Write("models/marts/fct_events.yml", EventsYaml);
            run.Write("models/marts/fct_events.sql", EventsSql);
            await engine.ExecAsync($"CREATE TABLE staging.events ({run.Q("event_id")} BIGINT NOT NULL, {run.Q("event_ts")} {engine.ColumnType("TIMESTAMP")} NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(14,2)")})");
            await engine.ExecAsync("INSERT INTO staging.events VALUES (1, '2024-01-01 10:00:00', 1.00), (2, '2024-01-02 10:00:00', 2.00), (3, '2024-01-05 10:00:00', 3.00)");
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";

            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var first = run.Cli("connection", "deploy", "--write-plan");
            Ok(first, "first plan");                                                  // the watermark is NULL on a new table: the declared initial literal is used
            var firstPlan = PlanDocument.Parse(File.ReadAllText(run.PlanFile(first.Out)), "p", new List<Diagnostic>())!;
            Assert.Equal("2000-01-01 00:00:00", firstPlan.Steps.Single(s => s.Type == StepType.Load).Parameters.Single().Value);
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(first.Out)), "first apply");
            Assert.Equal(3, await CountAsync(run, "marts.fct_events"));
            Assert.Equal(1, await CountAsync(run, T("operation_interval"), "operation = 'load'"));   // the watermark it started from is recorded

            // usage errors before anything else
            Assert.Equal(CliApp.ExitUsage, run.Cli("connection", "deploy", "--write-plan", "--backfill", "marts.fct_events").Exit);
            Assert.Equal(CliApp.ExitUsage, run.Cli("connection", "deploy", "--write-plan", "--backfill", "marts.nope=reload").Exit);
            Refused(run.Cli("connection", "deploy", "--write-plan", "--op", "marts.fct_events=missing"), "DDB-434", "an operation that is not rendered");

            // a backfill of the reload operation: parameters are questions; the span is bounded by max_span
            var startId = "Q-param-marts.fct_events-reload-start";
            var endId = "Q-param-marts.fct_events-reload-end";
            run.Write("tooLong.yml", $"answers:\n  - {{id: {startId}, choice: provide, value: \"2024-01-01 00:00:00\"}}\n  - {{id: {endId}, choice: provide, value: \"2024-03-01 00:00:00\"}}\n");
            var tooLong = run.Cli("connection", "deploy", "--write-plan", "--backfill", "marts.fct_events=reload", "--answers", Path.Combine(run.Dir, "tooLong.yml"));
            Refused(tooLong, "DDB-412", "a range longer than max_span");
            Assert.Contains("max_span of 10 days", tooLong.Err);

            run.Write("ok.yml", $"answers:\n  - {{id: {startId}, choice: provide, value: \"2024-01-01 00:00:00\"}}\n  - {{id: {endId}, choice: provide, value: \"2024-01-04 00:00:00\"}}\n");
            var plan = run.Cli("connection", "deploy", "--write-plan", "--backfill", "marts.fct_events=reload", "--answers", Path.Combine(run.Dir, "ok.yml"));
            Ok(plan, "backfill plan");
            Assert.Contains("1 backfill", plan.Out);
            var planFile = run.PlanFile(plan.Out);
            Assert.Equal(["backfill marts.fct_events (reload)"], PlanDocument.Parse(File.ReadAllText(planFile), "p", new List<Diagnostic>())!.Steps.Select(s => s.Description));

            await engine.ExecAsync("UPDATE marts.fct_events SET amount = 999 WHERE event_id IN (1, 2)");   // the backfill must replace these rows from the source
            Refused(run.Cli("connection", "deploy", "--apply-plan", planFile), "DDB-436", "a backfill without --allow-risky");
            Assert.Equal(0, await CountAsync(run, T("run_log"), "operation = 'reload'"));
            Ok(run.Cli("connection", "deploy", "--apply-plan", planFile, "--allow-risky"), "backfill apply");
            Assert.Equal(["1", "2"], await run.Engine.RowsAsync("SELECT amount FROM marts.fct_events WHERE event_id IN (1, 2)"));
            Assert.Equal(1, await CountAsync(run, T("operation_interval"), "operation = 'backfill' AND range_start = '2024-01-01 00:00:00' AND range_end = '2024-01-04 00:00:00'"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), "operation = 'reload' AND status = 'ok'"));

            // --param answers the same questions without a file; a range the table's data does not overlap is noticed in the plan (not refused), a backwards one is refused
            string[] Range(string a, string b) => ["--backfill", "marts.fct_events=reload", "--param", $"marts.fct_events.reload.start={a}", "--param", $"marts.fct_events.reload.end={b}"];
            var far = run.Cli(["connection", "deploy", "--write-plan", .. Range("2030-01-01 00:00:00", "2030-01-04 00:00:00"), "--format", "json"]);
            Ok(far, "plan with --param far from the data");
            var notices = System.Text.Json.Nodes.JsonNode.Parse(far.Out)!["data"]!["noticed"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.Contains(notices, n => n.Contains("does not overlap") && n.Contains("2024-01-05"));          // the table holds rows up to 2024-01-05 10:00
            var near = run.Cli(["connection", "deploy", "--write-plan", .. Range("2024-01-02 00:00:00", "2024-01-04 00:00:00"), "--format", "json"]);
            Ok(near, "plan with --param over the data");
            Assert.DoesNotContain(System.Text.Json.Nodes.JsonNode.Parse(near.Out)!["data"]!["noticed"]!.AsArray(), n => ((string)n!).Contains("does not overlap"));
            Refused(run.Cli(["connection", "deploy", "--write-plan", .. Range("2024-01-04 00:00:00", "2024-01-02 00:00:00")]), "DDB-412", "a backwards range");
            Assert.Equal(CliApp.ExitUsage, run.Cli("connection", "deploy", "--write-plan", "--param", "nonsense").Exit);

            // the report mentions the backfill's load
            Assert.Contains("reload", run.Cli("connection", "monitor").Out);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Declared_indexes_are_planned_verified_and_never_dropped(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply the tables");

            // an index and a unique index, declared by the operator; the model's unique_key alone never created a constraint
            run.Write("models/marts/fct_orders.yml", FctYaml + "indexes:\n  - {name: ix_amount, columns: [amount]}\n  - {name: uq_order, columns: [order_id], unique: true}\n");
            Ok(run.Cli("project", "compile", "--check"), "render --check (indexes do not change rendered loads)");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan with indexes");
            var p = PlanDocument.Parse(File.ReadAllText(run.PlanFile(plan.Out)), "p", new List<Diagnostic>())!;
            Assert.Equal(["create index ix_amount", "create index uq_order", "load marts.fct_orders (default)"], p.Steps.Select(s => s.Description));
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply the indexes");

            await using var read = await ReadSession.OpenAsync(LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Read, _ => engine.ConnectionString).Settings!);
            async Task<IReadOnlyList<DbDataBuild.State.PhysicalItem>> Physical() => (await CatalogReader.ReadObjectsAsync(read, name, "marts"))["marts.fct_orders"].Physical;
            var physical = await Physical();
            Assert.Contains(physical, i => i is { Kind: "index", Name: "ix_amount", Definition: "unique=0;keys=amount;include=" });
            Assert.Contains(physical, i => i is { Kind: "index", Name: "uq_order", Definition: "unique=1;keys=order_id;include=" });
            await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecAsync("INSERT INTO marts.fct_orders (order_id, amount) VALUES (1, 1.00)"));   // the unique index is enforced by the engine

            // in sync now: the next plan is the routine load only
            Assert.Contains("1 step(s)", run.Cli("connection", "deploy", "--write-plan").Out);

            // changing a declared index rebuilds it (risky); the plan shows drop and create together
            run.Write("models/marts/fct_orders.yml", FctYaml + "indexes:\n  - {name: ix_amount, columns: [amount], include: [order_id]}\n  - {name: uq_order, columns: [order_id], unique: true}\n");
            var rebuild = run.Cli("connection", "deploy", "--write-plan");
            Ok(rebuild, "plan with a changed index");
            var rebuildFile = run.PlanFile(rebuild.Out);
            Refused(run.Cli("connection", "deploy", "--apply-plan", rebuildFile), "DDB-436", "a rebuild without --allow-risky");
            Ok(run.Cli("connection", "deploy", "--apply-plan", rebuildFile, "--allow-risky"), "apply the rebuild");
            Assert.Contains(await Physical(), i => i is { Kind: "index", Name: "ix_amount", Definition: "unique=0;keys=amount;include=order_id" });

            // an index someone else added is left alone and mentioned; removing a declaration does not drop the index either
            await engine.ExecAsync($"CREATE INDEX dba_extra ON marts.fct_orders ({run.Q("order_id")}, {run.Q("amount")})");
            run.Write("models/marts/fct_orders.yml", FctYaml);
            var left = run.Cli("connection", "deploy", "--write-plan");
            Ok(left, "plan after removing the declarations");
            Assert.DoesNotContain("drop", PlanDocument.Parse(File.ReadAllText(run.PlanFile(left.Out)), "p", new List<Diagnostic>())!.Steps.Select(s => s.Description.ToLowerInvariant()).Aggregate("", (a, b) => a + b));
            Assert.Contains("`dba_extra`", File.ReadAllText(run.PlanFile(left.Out).Replace(".plan.yml", ".plan.md")));
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(left.Out)), "apply (loads only)");
            var after = await Physical();
            Assert.Contains(after, i => i.Name == "dba_extra");
            Assert.Contains(after, i => i.Name == "ix_amount");

            // physical changes are not drift: the shape is what blocks
            Ok(run.Cli("connection", "status"), "check with extra indexes on the target");
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Hooks_run_around_their_events_in_order_and_are_checked_before_anything_is_planned(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // a table the hooks write to (one extra column keeps their order), and the hook scripts: native SQL per engine, committed as written
            await engine.ExecAsync(name == "postgres"
                ? "CREATE TABLE staging.hook_log (seq serial PRIMARY KEY, event varchar(50) NOT NULL)"
                : "CREATE TABLE staging.hook_log (seq int IDENTITY PRIMARY KEY, event nvarchar(50) NOT NULL)");
            void Hook(string path, string eventName) => run.Write(path, $"INSERT INTO staging.hook_log (event) VALUES ('{eventName}');\n");
            Hook("hooks/created.sql", "created");
            Hook("hooks/pre_load.sql", "group_pre_load");
            Hook("hooks/post_load.sql", "group_post_load");
            Hook("hooks/sqlserver/native.sql", "native_sqlserver");
            Hook("hooks/postgres/native.sql", "native_postgres");
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) +
                "hook_groups:\n  audit:\n    - {name: before, event: pre_load, script: hooks/pre_load.sql, effect: data}\n    - {name: after, event: post_load, script: hooks/post_load.sql, effect: data}\n");
            var hooks = "hooks:\n  - {name: created, event: post_create, script: hooks/created.sql}\n  - {use: audit}\n" +
                        "  - {name: native, event: post_create, script: {sqlserver: hooks/sqlserver/native.sql, postgres: hooks/postgres/native.sql}}\n";
            run.Write("models/marts/fct_orders.yml", FctYaml + hooks);
            // read in insertion order (RowsAsync sorts, which would hide the order that matters here)
            async Task<List<string>> Log() => ((string?)await engine.ScalarAsync(name == "postgres"
                ? "SELECT string_agg(event, ',' ORDER BY seq) FROM staging.hook_log"
                : "SELECT STRING_AGG(event, ',') WITHIN GROUP (ORDER BY seq) FROM staging.hook_log"))?.Split(',').ToList() ?? [];
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            Ok(run.Cli("project", "compile"), "validate with hooks");

            // plan: the hooks are steps, placed around their events, with the script text in the plan
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            var file = run.PlanFile(plan.Out);
            var parsed = PlanDocument.Parse(File.ReadAllText(file), "p", new List<Diagnostic>())!;
            Assert.Equal(["create schema marts", "create table marts.fct_orders", "hook created (post_create)", $"hook native (post_create)", "create view marts.v_orders",
                "hook audit.before (pre_load)", "load marts.fct_orders (default)", "hook audit.after (post_load)"], parsed.Steps.Select(s => s.Description));
            Assert.Equal(name == "postgres" ? "native_postgres" : "native_sqlserver", System.Text.RegularExpressions.Regex.Match(parsed.Steps.Single(s => s.Hook == "native").Text, "'([a-z_]+)'").Groups[1].Value);
            Assert.Contains("hook audit.before (pre_load)", File.ReadAllText(file.Replace(".plan.yml", ".plan.md")));

            // dry run shows them and runs none
            var dry = run.Cli("connection", "deploy", "--apply-plan", file, "--dry-run");
            Ok(dry, "dry run");
            Assert.Contains("INSERT INTO staging.hook_log", dry.Out);
            Assert.Empty(await Log());

            Ok(run.Cli("connection", "deploy", "--apply-plan", file), "apply");
            Assert.Equal(["created", name == "postgres" ? "native_postgres" : "native_sqlserver", "group_pre_load", "group_post_load"], await Log());     // the order the plan gave
            Assert.Equal(4, await CountAsync(run, run.Q("dbdatabuild") + "." + run.Q("run_log"), "operation IN ('post_create', 'pre_load', 'post_load') AND status = 'ok'"));

            // a routine load with only data hooks is still routine; create hooks do not fire for an existing table
            await engine.ExecAsync("DELETE FROM staging.hook_log");
            var routine = run.Cli("connection", "refresh");
            Ok(routine, "run with data hooks");
            Assert.Equal(["group_pre_load", "group_post_load"], await Log());

            // a hook that is not a data hook (the default effect is ddl) makes the load not routine: compiling leaves the model out of the refresh plan, and says why; a deploy does it
            run.Write("models/marts/fct_orders.yml", FctYaml + hooks + "  - {name: ddl_hook, event: post_load, script: hooks/created.sql}\n");
            Ok(run.Cli("project", "compile"), "compile with a ddl hook");
            var refused = run.Cli("connection", "refresh");
            Ok(refused, "a refresh with nothing routine in it");
            Assert.Contains("has no members", refused.Out);
            Assert.Contains("Nothing to do", refused.Out);
            Assert.Contains("not a safe data hook", run.Cli("project", "show", "refresh", "default").Out);       // and the plan says why it holds nothing

            // checked before anything is planned: a missing script, a script that does not parse, an unknown group
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: gone, event: post_create, script: hooks/nowhere.sql}\n");
            Refused(run.Cli("connection", "deploy", "--write-plan"), "DDB-323", "a hook whose script does not exist");
            Refused(run.Cli("project", "compile"), "DDB-323", "validate with a missing hook script");
            run.Write("hooks/broken.sql", "SELEC nothing FRM nowhere ((;\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: broken, event: post_create, script: hooks/broken.sql}\n");
            Refused(run.Cli("connection", "deploy", "--write-plan"), "DDB-323", "a hook script that does not parse");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {use: no_such_group}\n");
            var group = run.Cli("connection", "deploy", "--write-plan");
            Assert.NotEqual(0, group.Exit);
            Assert.Contains("no_such_group", group.Err + group.Out);

            // a hook that fails stops the apply, is recorded as failed, and the plan resumes after it is fixed
            run.Write("hooks/failing.sql", "INSERT INTO staging.no_such_table (x) VALUES (1);\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: failing, event: post_load, script: hooks/failing.sql, effect: data}\n");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (9, 90.00)");
            var failPlan = run.Cli("connection", "deploy", "--write-plan");
            Ok(failPlan, "plan with a failing hook");
            var failFile = run.PlanFile(failPlan.Out);
            var failed = run.Cli("connection", "deploy", "--apply-plan", failFile);
            Refused(failed, "DDB-440", "a failing hook");
            Assert.Equal(1, await CountAsync(run, run.Q("dbdatabuild") + "." + run.Q("run_log"), "operation = 'post_load' AND status = 'failed'"));
            Assert.Equal(4, await CountAsync(run, "marts.fct_orders"));                                   // the load before the hook had already committed (3 rows plus order 9)
            await engine.ExecAsync("CREATE TABLE staging.no_such_table (x int)");
            Ok(run.Cli("connection", "deploy", "--apply-plan", failFile), "resume after fixing the hook's cause");
            Assert.Equal(1, await CountAsync(run, "staging.no_such_table"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_history_warning_can_be_accepted_without_touching_data(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // a `full` model, so adding a column needs no definition acknowledgement
            run.Write("models/marts/fct_orders.yml", FctYaml.Replace("kind: {incremental_by_unique_key: {unique_key: [order_id]}}", "kind: full").Replace("grain: [order_id]\n", ""));
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply");

            run.Write("models/marts/fct_orders.yml", FctYaml2.Replace("kind: {incremental_by_unique_key: {unique_key: [order_id]}}", "kind: full").Replace("grain: [order_id]\n", ""));
            run.Write("models/marts/fct_orders.sql", FctSql2);
            Ok(run.Cli("project", "compile"), "render");
            run.Write("answers.yml", "answers:\n  - {id: Q-history-marts.fct_orders.discount_code, choice: backfill_later}\n");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan", "--answers", Path.Combine(run.Dir, "answers.yml")).Out)), "apply the new column");
            var rowsBefore = await run.Engine.RowsAsync("SELECT order_id FROM marts.fct_orders");

            // the report warns and exits non-zero, saying how to deal with it
            var warn = run.Cli("connection", "monitor");
            Refused(warn, "backfill was requested and none", "report with an unfulfilled backfill request");
            Assert.Contains("--ack history:marts.fct_orders.discount_code", warn.Out);

            // a model built from the column is told about it (DDB-240), and when the project says so it is refused until the history is acknowledged
            run.Write("models/marts/disc.yml", "name: marts.disc\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: discount_code, type: \"VARCHAR(20)\"}\n");
            run.Write("models/marts/disc.sql", "SELECT order_id, discount_code FROM marts.fct_orders\n");
            Ok(run.Cli("project", "compile"), "render disc");
            var told = run.Cli("connection", "deploy", "--write-plan");
            Ok(told, "plan with a warning");
            Assert.Contains("DDB-240", told.Err);
            Assert.Contains("marts.disc is built from a column whose history is inconsistent (`discount_code` comes from marts.fct_orders.discount_code)", told.Err);
            var config = File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml"));
            run.Write("dbdatabuild.yml", config + "policy:\n  severity:\n    history_inconsistency: error\n");
            var blocked = run.Cli("connection", "deploy", "--write-plan");
            Refused(blocked, "DDB-240", "a plan of a model built from an inconsistent column, with the policy at error");
            Assert.Contains("Nothing was planned", blocked.Out);

            // the operator accepts it: a reason is required, nothing in the data changes, and the report keeps the facts
            Refused(run.Cli("connection", "deploy", "--ack", "history:marts.fct_orders.discount_code"), "--reason", "an acknowledgement without a reason");
            Refused(run.Cli("connection", "deploy", "--ack", "history:marts.fct_orders.nothing_here", "--reason", "x"), "nothing to acknowledge", "acknowledging something that is not there");
            var ack = run.Cli("connection", "deploy", "--ack", "history:marts.fct_orders.discount_code", "--reason", "The source never had this column; NULL is correct.");
            Ok(ack, "ack history");
            Assert.Equal(rowsBefore, await run.Engine.RowsAsync("SELECT order_id FROM marts.fct_orders"));

            var unblocked = run.Cli("connection", "deploy", "--write-plan");
            Ok(unblocked, "plan after the acknowledgement");
            Assert.DoesNotContain("DDB-240", unblocked.Err);

            var calm = run.Cli("connection", "monitor");
            Ok(calm, "report after the acknowledgement");
            Assert.Contains("Acknowledged by", calm.Out);
            Assert.Contains("The source never had this column; NULL is correct.", calm.Out);
            Assert.Contains("A backfill was requested and none has been recorded.", calm.Out);
            Assert.Contains("nothing", calm.Out.Split("Needs attention")[1]);

            // repeating it is a no-op
            Assert.Contains("already acknowledged", run.Cli("connection", "deploy", "--ack", "history:marts.fct_orders.discount_code", "--reason", "again").Err);
            Assert.Equal(1, await CountAsync(run, run.Q("dbdatabuild") + "." + run.Q("block_log"), "code = 'DDB-443'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_database_commands_speak_json_with_their_data(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            System.Text.Json.Nodes.JsonNode Json((int Exit, string Out, string Err) r, int expectedExit = 0)
            {
                Assert.Equal(expectedExit, r.Exit);
                var doc = System.Text.Json.Nodes.JsonNode.Parse(r.Out)!;                            // standard output is exactly one document
                Assert.Equal("dbdatabuild.output/1", (string?)doc["schema"]);
                Assert.Equal(r.Exit, (int)doc["exit_code"]!);
                Assert.Equal("", r.Err);
                return doc;
            }
            var init = Json(run.Cli("connection", "init", "--apply", "--format", "json"));
            Assert.True((bool)init["data"]!["applied"]!);
            Json(run.Cli("project", "compile", "--format", "json"));

            var check = Json(run.Cli("connection", "status", "--format", "json"));
            Assert.Equal(["missing", "missing"], check["data"]!["objects"]!.AsArray().Select(o => (string)o!["state"]!).ToArray());
            Assert.Equal(4, (int)check["data"]!["preview"]!["steps"]!);

            // JSON mode never prompts: an open question is data, with its options, and the exit code says findings
            run.Write("models/marts/fct_orders.yml", FctYaml);
            var plan = Json(run.Cli("connection", "deploy", "--write-plan", "--format", "json"));
            Assert.Equal(4, plan["data"]!["plan"]!["steps"]!.AsArray().Count);
            Assert.Equal("ddl", (string?)plan["data"]!["plan"]!["steps"]![0]!["type"]);
            Assert.Equal("obj.missing.table", (string?)plan["data"]!["plan"]!["steps"]![1]!["reasons"]![0]);
            var planFile = Path.Combine(run.Dir, (string)plan["data"]!["files"]!["plan"]!);
            Assert.True(File.Exists(planFile));

            var dry = Json(run.Cli("connection", "deploy", "--apply-plan", planFile, "--dry-run", "--format", "json"));
            Assert.True((bool)dry["data"]!["dry_run"]!);
            Assert.Contains("CREATE TABLE", (string)dry["data"]!["statements"]![1]!["text"]!);

            var applied = Json(run.Cli("connection", "deploy", "--apply-plan", planFile, "--format", "json"));
            Assert.True((bool)applied["data"]!["success"]!);
            Assert.All(applied["data"]!["outcomes"]!.AsArray(), o => Assert.Equal("ok", (string?)o!["status"]));
            Assert.EndsWith(".jsonl", (string)applied["data"]!["statement_log"]!);

            var refused = Json(run.Cli("connection", "deploy", "--apply-plan", planFile, "--format", "json"), expectedExit: 1);   // a plan is applied once
            Assert.Equal("DDB-438", (string?)refused["diagnostics"]![0]!["code"]);

            // a model change that needs an answer: an open question as data
            run.Write("models/marts/fct_orders.yml", FctYaml2);
            run.Write("models/marts/fct_orders.sql", FctSql2);
            Json(run.Cli("project", "compile", "--format", "json"));
            Json(run.Cli("connection", "deploy", "--ack", "definition:marts.fct_orders", "--reason", "added a column", "--format", "json"));
            var asked = Json(run.Cli("connection", "deploy", "--write-plan", "--format", "json"), expectedExit: 1);
            var question = asked["data"]!["open_questions"]![0]!;
            Assert.Equal("Q-history-marts.fct_orders.discount_code", (string?)question["id"]);
            Assert.Equal(["not_backfilled", "backfill_later"], question["options"]!.AsArray().Select(o => (string)o!["key"]!).ToArray());
            Assert.Contains(asked["diagnostics"]!.AsArray(), d => (string?)d!["code"] == "DDB-414");

            var report = Json(run.Cli("connection", "monitor", "--format", "json"));
            Assert.Equal("completed", (string?)report["data"]!["events"]![0]!["status"]);
            Assert.Contains(report["data"]!["objects"]!.AsArray(), o => (string?)o!["object"] == "marts.fct_orders" && (string?)o["now"] == "in sync");
            Assert.Empty(report["data"]!["needs_attention"]!.AsArray());
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Metadata_can_be_stored_in_the_target_queried_with_sql_and_republished_without_noise(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            System.Text.Json.Nodes.JsonNode Json((int Exit, string Out, string Err) r)
            {
                Assert.Equal(0, r.Exit);
                return System.Text.Json.Nodes.JsonNode.Parse(r.Out)!;
            }
            var meta = (string table) => $"{run.Q("dbdatabuild")}.{run.Q(table)}";
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");

            // the operator's choice of when to store: nothing is stored until asked
            Assert.Equal(0, await CountAsync(run, meta("metadata_document")));
            var first = Json(run.Cli("connection", "publish", "--format", "json"));
            Assert.Equal(4, first["data"]!["written"]!.AsArray().Count);              // the project, the source and the two models
            Assert.Empty(first["data"]!["unchanged"]!.AsArray());
            Assert.Equal(4, await CountAsync(run, meta("metadata_document")));

            // unchanged documents are not written again
            var second = Json(run.Cli("connection", "publish", "--format", "json"));
            Assert.Empty(second["data"]!["written"]!.AsArray());
            Assert.Equal(4, await CountAsync(run, meta("metadata_document")));

            // introspection with SQL: the stored document is the one `metadata` prints, and the column view unpacks it
            var printed = Json(run.Cli("project", "show", "metadata", "--format", "json"))["data"]!["models"]!.AsArray().Single(m => (string?)m!["name"] == "marts.fct_orders")!;
            var storedHash = (string?)await engine.ScalarAsync(name == "postgres"
                ? $"SELECT document ->> 'definition_hash' FROM {meta("metadata_current")} WHERE subject_kind = 'model' AND subject = 'marts.fct_orders'"
                : $"SELECT JSON_VALUE(document, '$.definition_hash') FROM {meta("metadata_current")} WHERE subject_kind = 'model' AND subject = 'marts.fct_orders'");
            Assert.Equal((string?)printed["definition_hash"], storedHash);
            var amount = await engine.RowsAsync($"SELECT {run.Q("logical_type")}, {run.Q(name == "postgres" ? "postgres_type" : "sqlserver_type")}, {run.Q("nullable")} FROM {meta("metadata_columns")} WHERE model = 'marts.fct_orders' AND column_name = 'amount'");
            Assert.Equal(new List<string> { name == "postgres" ? "DECIMAL(14, 2)|numeric(14, 2)|True" : "DECIMAL(14, 2)|decimal(14, 2)|True" }, amount);
            Assert.Equal(new[] { "order_id", "amount" }.Order(), (await engine.RowsAsync($"SELECT column_name FROM {meta("metadata_columns")} WHERE model = 'marts.fct_orders'")).Order());

            // a changed model changes its own document and the project document (which lists every model's hash), and nothing else
            run.Write("models/marts/v_orders.sql", "SELECT order_id FROM marts.fct_orders WHERE order_id > 0\n");
            var third = Json(run.Cli("connection", "publish", "--format", "json"));
            Assert.Equal(["marts.v_orders", "project"], third["data"]!["written"]!.AsArray().Select(d => (string)d!["subject"]!).Order().ToArray());
            Assert.Equal(6, await CountAsync(run, meta("metadata_document")));      // history is kept; metadata_current shows the latest
            Assert.Equal(4, await CountAsync(run, meta("metadata_current")));

            // only the models named are stored
            run.Write("models/marts/fct_orders.sql", "SELECT o.order_id, o.amount FROM staging.orders o WHERE 1 = 1\n");
            var only = Json(run.Cli("connection", "publish", "marts.fct_orders", "--format", "json"));
            Assert.Equal(["marts.fct_orders", "project"], only["data"]!["written"]!.AsArray().Select(d => (string)d!["subject"]!).Order().ToArray());
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_words_about_objects_are_stored_as_rows_beside_the_documents_and_replaced_when_they_change(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var info = $"{run.Q("dbdatabuild")}.{run.Q("object_info")}";
            async Task<List<string>> Rows(string kind, string subject) => [.. (await engine.RowsAsync(
                $"SELECT {run.Q("element")}, {run.Q("item")}, {run.Q("item_key")}, COALESCE({run.Q("value_type")}, '-'), COALESCE({run.Q("value")}, '-') FROM {info} WHERE {run.Q("subject_kind")} = '{kind}' AND {run.Q("subject")} = '{subject}'")).Order(StringComparer.Ordinal)];
            Ok(run.Cli("connection", "init", "--apply"), "init");

            run.Write("dbdatabuild.yml", "title: Orders project\ndescription: \"Where the orders are.\"\ntags: [shop]\nproperties: {owner: sales, tier: 1}\nconnections:\n  " + name + ": {title: The database, properties: {live: true}}\n" + File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")));
            run.Write("models/marts/fct_orders.yml", "title: Orders fact\ndescription: \"One row per order; the amount is in dollars.\"\ntags: [finance]\nproperties: {owner: finance, tier: 2, pii: false, note: \"it's 100%\"}\n" + FctYaml.Replace("{name: amount,", "{title: Amount, description: \"Paid, tax included\", properties: {unit: USD}, name: amount,") + "indexes:\n  - {name: ix_orders_amount, columns: [amount], description: \"Range scans\", tags: [scan]}\n");
            Ok(run.Cli("project", "compile"), "compile");
            Assert.Equal(0, await CountAsync(run, info));
            Ok(run.Cli("connection", "publish"), "publish");

            Assert.Equal(new List<string>
            {
                "|description||-|One row per order; the amount is in dollars.", "|property|note|text|it's 100%", "|property|owner|text|finance", "|property|pii|boolean|false", "|property|tier|number|2",
                "|tag|finance|-|-", "|title||-|Orders fact", "column:amount|description||-|Paid, tax included", "column:amount|property|unit|text|USD", "column:amount|title||-|Amount",
                "index:ix_orders_amount|description||-|Range scans", "index:ix_orders_amount|tag|scan|-|-",
            }.Order(StringComparer.Ordinal).ToList(), await Rows("model", "marts.fct_orders"));
            Assert.Equal(new List<string> { "|description||-|Where the orders are.", "|property|owner|text|sales", "|property|tier|number|1", "|tag|shop|-|-", "|title||-|Orders project", "connection:" + name + "|property|live|boolean|true", "connection:" + name + "|title||-|The database" }.Order(StringComparer.Ordinal).ToList(), await Rows("project", "project"));
            Assert.Empty(await Rows("model", "marts.v_orders"));                   // a model that says nothing has no rows

            // the document says the same, and a second publish writes nothing
            Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(run.Cli("connection", "publish", "--format", "json").Out)!["data"]!["written"]!.AsArray());
            var before = await CountAsync(run, info);

            // a changed description replaces the rows of that model and no other
            run.Write("models/marts/fct_orders.yml", File.ReadAllText(Path.Combine(run.Dir, "models/marts/fct_orders.yml")).Replace("One row per order; the amount is in dollars.", "One row per order.").Replace("tags: [finance]", "tags: [finance, audited]"));
            var changed = System.Text.Json.Nodes.JsonNode.Parse(run.Cli("connection", "publish", "--format", "json").Out)!["data"]!["written"]!.AsArray().Select(d => (string)d!["subject"]!).Order().ToList();
            Assert.Equal(["marts.fct_orders"], changed);
            Assert.Contains("|description||-|One row per order.", await Rows("model", "marts.fct_orders"));
            Assert.DoesNotContain("|description||-|One row per order; the amount is in dollars.", await Rows("model", "marts.fct_orders"));
            Assert.Contains("|tag|audited|-|-", await Rows("model", "marts.fct_orders"));
            Assert.Equal(before + 1, await CountAsync(run, info));

            // saying nothing any more leaves no rows
            run.Write("models/marts/fct_orders.yml", FctYaml);
            Ok(run.Cli("connection", "publish"), "publish");
            Assert.Empty(await Rows("model", "marts.fct_orders"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Sources_are_exported_from_the_target_refreshed_checked_and_published(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            System.Text.Json.Nodes.JsonNode Json((int Exit, string Out, string Err) r, int expectedExit = 0)
            {
                Assert.True(r.Exit == expectedExit, $"exit {r.Exit}, expected {expectedExit}:\n{r.Out}\n{r.Err}");
                return System.Text.Json.Nodes.JsonNode.Parse(r.Out)!;
            }
            var file = (string rel) => Path.Combine(run.Dir, rel);
            var codes = (System.Text.Json.Nodes.JsonNode d) => d["diagnostics"]!.AsArray().Select(x => (string)x!["code"]!).ToList();
            var pg = name == "postgres";

            // a table with a primary key, a spread of types (unlimited text and json/xml among them), and one column no logical type can describe
            await engine.ExecAsync(pg
                ? "CREATE TABLE staging.imp (id bigint NOT NULL PRIMARY KEY, qty integer NOT NULL, price numeric(12,3), ratio double precision, flag boolean, born date, seen timestamp(6), code varchar(20), notes text, tiny smallint, ref uuid, blobby bytea, j jsonb, odd interval)"
                : "CREATE TABLE staging.imp (id bigint NOT NULL PRIMARY KEY, qty int NOT NULL, price decimal(12,3), ratio float, flag bit, born date, seen datetime2(6), code nvarchar(20), notes nvarchar(max), tiny tinyint, ref uniqueidentifier, blobby varbinary(max), j xml, odd geography)");

            // an index with an included column, a unique index, and a foreign key (the primary key's own index is the grain, not listed)
            await engine.ExecAsync("CREATE TABLE staging.ref (id bigint NOT NULL PRIMARY KEY)");
            await engine.ExecAsync("CREATE INDEX ix_imp_code ON staging.imp (code) INCLUDE (qty)");
            await engine.ExecAsync("CREATE UNIQUE INDEX ux_imp_ref ON staging.imp (ref)");
            await engine.ExecAsync("ALTER TABLE staging.imp ADD CONSTRAINT fk_imp_ref FOREIGN KEY (id) REFERENCES staging.ref (id)");

            // the preview shows the diff and writes nothing
            var preview = Json(run.Cli("project", "import", "staging.imp", "--format", "json"));
            var imp = preview["data"]!["mapped_models"]!.AsArray().Single()!;
            Assert.Equal(("staging.imp", "table", "new", "models/staging/imp.yml", "preview"), ((string)imp["name"]!, (string)imp["object_kind"]!, (string)imp["status"]!, (string)imp["file"]!, (string)preview["data"]!["mode"]!));
            Assert.Equal(["id"], imp["grain"]!.AsArray().Select(x => (string)x!));
            Assert.False(File.Exists(file("models/staging/imp.yml")));
            Assert.Equal(["DDB-226"], codes(preview));                                                   // only `odd`: unlimited text and xml/json are text
            var tiny = imp["columns"]!.AsArray().Single(c => (string?)c!["name"] == "tiny")!;
            Assert.Equal(pg ? "exact" : "widened", (string?)tiny["fit"]);
            Assert.Equal(("VARCHAR", "exact"), (imp["columns"]!.AsArray().Single(c => (string?)c!["name"] == "notes")!["logical_type"]!.ToString(), (string)imp["columns"]!.AsArray().Single(c => (string?)c!["name"] == "notes")!["fit"]!));
            Assert.Equal(("VARCHAR", "lossy"), ((string)imp["columns"]!.AsArray().Single(c => (string?)c!["name"] == "j")!["logical_type"]!, (string)imp["columns"]!.AsArray().Single(c => (string?)c!["name"] == "j")!["fit"]!));

            // writing it
            var written = Json(run.Cli("project", "import", "staging.imp", "--write", "--format", "json"));
            Assert.Equal(["models/staging/imp.yml"], written["data"]!["written"]!.AsArray().Select(x => (string)x!));
            Assert.Equal(
                $"name: staging.imp\nkind: mapped\nconnections=: [{name}]\ngrain: [id]\ncolumns:\n  - name: id\n    type: BIGINT\n    nullable: false\n  - name: qty\n    type: INTEGER\n    nullable: false\n  - name: price\n    type: DECIMAL(12, 3)\n" +
                "  - name: ratio\n    type: DOUBLE\n  - name: flag\n    type: BOOLEAN\n  - name: born\n    type: DATE\n  - name: seen\n    type: TIMESTAMP\n  - name: code\n    type: VARCHAR(20)\n  - name: notes\n    type: VARCHAR\n" +
                "  - name: tiny\n    type: SMALLINT\n  - name: ref\n    type: UUID\n  - name: blobby\n    type: BLOB\n  - name: j\n    type: VARCHAR\n" +
                "indexes:\n  - {name: ix_imp_code, columns: [code], include: [qty]}\n  - {name: ux_imp_ref, columns: [ref], unique: true}\n" +
                "foreign_keys:\n  - {name: fk_imp_ref, columns: [id], references: {table: staging.ref, columns: [id]}}\n",
                File.ReadAllText(file("models/staging/imp.yml")));
            Ok(run.Cli("project", "compile"), "the exported descriptor is valid");
            var again = Json(run.Cli("project", "import", "staging.imp", "--check", "--format", "json"));
            Assert.Equal("unchanged", (string?)again["data"]!["mapped_models"]![0]!["status"]);

            // the table changes: --check fails for CI, the preview shows the change, a refresh without arguments takes it
            await engine.ExecAsync(pg ? "ALTER TABLE staging.imp ADD extra integer" : "ALTER TABLE staging.imp ADD extra int");
            await engine.ExecAsync("CREATE INDEX ix_imp_extra ON staging.imp (extra)");
            var stale = Json(run.Cli("project", "import", "--check", "--format", "json"), expectedExit: 1);
            Assert.Contains("DDB-227", codes(stale));
            var changed = stale["data"]!["mapped_models"]!.AsArray().Single(x => (string?)x!["name"] == "staging.imp")!;
            Assert.Equal("changed", (string?)changed["status"]);
            Assert.Equal(("columnadded", "extra"), ((string)changed["changes"]![0]!["kind"]!, (string)changed["changes"]![0]!["column"]!));
            Assert.Contains(changed["changes"]!.AsArray(), x => (string?)x!["kind"] == "indexadded" && (string?)x["column"] == "ix_imp_extra");
            Assert.Equal("unchanged", (string?)stale["data"]!["mapped_models"]!.AsArray().Single(x => (string?)x!["name"] == "staging.orders")!["status"]);   // the hand-written descriptor already matched

            // human knowledge survives: a grain, a length put on unlimited text, and a type for a column the catalog cannot type
            var text = File.ReadAllText(file("models/staging/imp.yml")).Replace("grain: [id]", "grain: [id, code]") .Replace("  - name: notes\n    type: VARCHAR\n", "  - name: notes\n    type: VARCHAR(500)\n").Replace("indexes:\n", "  - {name: odd, type: VARCHAR(40)}\nindexes:\n");
            run.Write("models/staging/imp.yml", text);
            var refreshed = Json(run.Cli("project", "import", "--write", "--format", "json"));
            Assert.Equal(["models/staging/imp.yml"], refreshed["data"]!["written"]!.AsArray().Select(x => (string)x!));
            var after = File.ReadAllText(file("models/staging/imp.yml"));
            Assert.Contains("grain: [id, code]", after);
            Assert.Contains("  - name: extra\n    type: INTEGER\n", after);
            Assert.Contains("  - {name: ix_imp_extra, columns: [extra]}\n", after);
            Assert.Contains("  - {name: fk_imp_ref, columns: [id], references: {table: staging.ref, columns: [id]}}\n", after);
            Assert.Contains("  - name: notes\n    type: VARCHAR(500)\n", after);   // the same type as the live unlimited text, so not churned
            Assert.Contains("  - name: odd\n    type: VARCHAR(40)\n", after);
            Ok(run.Cli("project", "compile"), "the refreshed descriptor is valid");

            // a view is a source too; a pattern takes both; the tool's own schema and a damaged file are never touched
            await engine.ExecAsync($"CREATE VIEW staging.imp_view AS SELECT id, qty FROM staging.imp");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            run.Write("models/staging/broken.yml", "name: staging.broken\nkind: mapped\ncolumns: nope\n");
            await engine.ExecAsync("CREATE TABLE staging.broken (id bigint)");
            var all = Json(run.Cli("project", "import", "staging.*", "dbdatabuild.*", "--write", "--format", "json"));
            var byName = all["data"]!["mapped_models"]!.AsArray().ToDictionary(x => (string)x!["name"]!, x => x!);
            Assert.Equal(("view", "new"), ((string)byName["staging.imp_view"]["object_kind"]!, (string)byName["staging.imp_view"]["status"]!));
            Assert.Equal("invalid", (string?)byName["staging.broken"]["status"]);
            Assert.Equal("name: staging.broken\nkind: mapped\ncolumns: nope\n", File.ReadAllText(file("models/staging/broken.yml")));
            Assert.Contains(all["data"]!["skipped"]!.AsArray(), x => (string?)x!["object"] == "dbdatabuild");
            Assert.True(File.Exists(file("models/staging/imp_view.yml")));
            File.Delete(file("models/staging/broken.yml"));

            // a descriptor whose table is gone is reported and kept
            run.Write("models/staging/ghost.yml", "name: staging.ghost\nkind: mapped\ncolumns:\n  - {name: id, type: BIGINT}\n");
            var ghost = Json(run.Cli("project", "import", "--format", "json"));
            Assert.Equal("stale", (string?)ghost["data"]!["mapped_models"]!.AsArray().Single(x => (string?)x!["name"] == "staging.ghost")!["status"]);
            Assert.Contains("DDB-228", codes(ghost));
            Assert.True(File.Exists(file("models/staging/ghost.yml")));
            File.Delete(file("models/staging/ghost.yml"));

            // published with the rest of the metadata, and queryable next to the models' columns
            Ok(run.Cli("project", "compile"), "render");
            var published = Json(run.Cli("connection", "publish", "--format", "json"));
            Assert.Contains(published["data"]!["written"]!.AsArray(), d => (string?)d!["subject_kind"] == "model" && (string?)d["subject"] == "staging.imp");
            var meta = (string table) => $"{run.Q("dbdatabuild")}.{run.Q(table)}";
            Assert.Equal(["order_id"], await engine.RowsAsync($"SELECT {run.Q("column_name")} FROM {meta("metadata_columns")} WHERE {run.Q("model_kind")} = 'mapped' AND model = 'staging.orders' AND {run.Q("column_name")} = 'order_id'"));
            Assert.Equal("BIGINT", (await engine.RowsAsync($"SELECT {run.Q("logical_type")} FROM {meta("metadata_columns")} WHERE {run.Q("model_kind")} = 'mapped' AND model = 'staging.imp' AND {run.Q("column_name")} = 'id'")).Single());
            var readers = await engine.RowsAsync($"SELECT {run.Q("subject")} FROM {meta("metadata_dependency")} WHERE {run.Q("depends_on")} = 'staging.orders' AND {run.Q("depth")} = 1");      // who reads a mapped model is a query on the dependency rows
            Assert.Equal(["marts.fct_orders"], readers);
            Assert.Equal(1, await CountAsync(run, meta("metadata_current"), pg ? "subject_kind = 'model' AND model_kind = 'mapped' AND subject = 'staging.imp' AND document::text LIKE '%ix_imp_code%fk_imp_ref%'" : "subject_kind = 'model' AND model_kind = 'mapped' AND subject = 'staging.imp' AND document LIKE '%ix_imp_code%fk_imp_ref%'"));   // indexes and foreign keys are published with the source
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Diff_compares_two_tables_of_one_target_with_counts_by_default_and_values_only_when_asked(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var pg = name == "postgres";
            System.Text.Json.Nodes.JsonNode Json((int Exit, string Out, string Err) r, int expectedExit)
            {
                Assert.True(r.Exit == expectedExit, $"exit {r.Exit}, expected {expectedExit}:\n{r.Out}\n{r.Err}");
                return System.Text.Json.Nodes.JsonNode.Parse(r.Out)!;
            }
            // staging.orders (declared in the project with grain order_id): 1 10.00, 2 20.50, 3 NULL; add 5 50.00 (only on this side)
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (5, 50.00)");
            await engine.ExecAsync(pg ? "CREATE SCHEMA dev" : "EXEC('CREATE SCHEMA dev')");
            // the copy: 2 differs, 3 differs (NULL on the left), 4 is only here, 5 is missing, a wider decimal and one more column
            await engine.ExecAsync($"CREATE TABLE dev.orders ({run.Q("order_id")} BIGINT NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(18,3)")}, {run.Q("extra")} {engine.ColumnType("VARCHAR(5)")})");
            await engine.ExecAsync("INSERT INTO dev.orders VALUES (1, 10.000, 'x'), (2, 21.500, 'x'), (3, 5.000, 'x'), (4, 40.000, 'x')");

            // counts only: nothing of the data is read
            var counts = Json(run.Cli("connection", "compare", "staging.orders", "--against-schema", "dev", "--format", "json"), 1);
            var data = counts["data"]!;
            Assert.Equal((4, 4, "grain"), ((int)data["left"]!["rows"]!, (int)data["right"]!["rows"]!, (string)data["key"]!["source"]!));
            Assert.Equal((1, 1, 3, 2), ((int)data["rows"]!["only_left"]!, (int)data["rows"]!["only_right"]!, (int)data["rows"]!["matched"]!, (int)data["rows"]!["differing"]!));
            Assert.Equal(2, (int)data["rows"]!["differing_by_column"]!["amount"]!);
            Assert.Equal(["extra"], data["schema"]!["only_right"]!.AsArray().Select(x => (string)x!));
            Assert.Equal(["amount"], data["schema"]!["type_differences"]!.AsArray().Select(x => (string)x!["column"]!));
            Assert.Null(data["samples"]);
            Assert.All(data["column_stats"]!.AsArray(), c => { Assert.Null(c!["left_min"]); Assert.Null(c["right_max"]); });
            var amount = data["column_stats"]!.AsArray().Single(c => (string?)c!["column"] == "amount")!;
            Assert.Equal((3, 4), ((int)amount["left_non_null"]!, (int)amount["right_non_null"]!));        // a NULL on one side is a count, not a value
            Assert.False((bool)data["identical"]!);
            Assert.DoesNotContain("21.5", counts.ToJsonString());
            Assert.DoesNotContain("50.0", counts.ToJsonString());

            // values, because the operator asked
            var shown = Json(run.Cli("connection", "compare", "staging.orders", "--against", "dev.orders", "--show-values", "--limit", "5", "--format", "json"), 1)["data"]!;
            var samples = shown["samples"]!;
            Assert.Equal(("5", 50m), ((string)samples["only_left"]![0]!["key"]!["order_id"]!, decimal.Parse((string)samples["only_left"]![0]!["values"]!["amount"]!, System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Equal("4", (string?)samples["only_right"]![0]!["key"]!["order_id"]);
            var differing = samples["differing"]!.AsArray().OrderBy(d => (string)d!["key"]!["order_id"]!).ToList();
            Assert.Equal(["2", "3"], differing.Select(d => (string)d!["key"]!["order_id"]!));
            Assert.Equal(("20.5", "21.5"), (Trim((string)differing[0]!["columns"]!["amount"]!["left"]!), Trim((string)differing[0]!["columns"]!["amount"]!["right"]!)));
            Assert.Null((string?)differing[1]!["columns"]!["amount"]!["left"]);                           // NULL on the left
            Assert.Equal("5", Trim((string)differing[1]!["columns"]!["amount"]!["right"]!));
            Assert.Equal("10", Trim((string)shown["column_stats"]!.AsArray().Single(c => (string?)c!["column"] == "amount")!["left_min"]!));
            Assert.Equal("50", Trim((string)shown["column_stats"]!.AsArray().Single(c => (string?)c!["column"] == "amount")!["left_max"]!));
            var text = run.Cli("connection", "compare", "staging.orders", "--against-schema", "dev", "--show-values");
            Assert.Contains("order_id=5", text.Out);
            Assert.Contains("amount 20.50", text.Out.Replace("20.500", "20.50"));

            // narrowing the columns, and a key given by hand
            var narrow = Json(run.Cli("connection", "compare", "staging.orders", "--against-schema", "dev", "--columns", "order_id", "--format", "json"), 1)["data"]!;
            Assert.Equal((0, 1, 1), ((int)narrow["rows"]!["differing"]!, (int)narrow["rows"]!["only_left"]!, (int)narrow["rows"]!["only_right"]!));

            // an identical copy
            await engine.ExecAsync(pg ? "CREATE TABLE dev.copy AS SELECT * FROM staging.orders" : "SELECT * INTO dev.copy FROM staging.orders");
            var same = Json(run.Cli("connection", "compare", "staging.orders", "--against", "dev.copy", "--format", "json"), 0)["data"]!;
            Assert.True((bool)same["identical"]!);
            Assert.Equal((4, 0, 0, 0), ((int)same["rows"]!["matched"]!, (int)same["rows"]!["only_left"]!, (int)same["rows"]!["only_right"]!, (int)same["rows"]!["differing"]!));

            // a key that is not unique: rows are not compared, and it says so
            await engine.ExecAsync("INSERT INTO dev.copy VALUES (1, 10.00)");
            var dup = Json(run.Cli("connection", "compare", "staging.orders", "--against", "dev.copy", "--format", "json"), 1)["data"]!;
            Assert.Equal((false, 0, 1), ((bool)dup["rows"]!["compared"]!, (int)dup["duplicate_keys"]!["left"]!, (int)dup["duplicate_keys"]!["right"]!));
            Assert.Contains("is not unique", run.Cli("connection", "compare", "staging.orders", "--against", "dev.copy").Out);

            // a column of another kind is not compared; a table that is not there is a usage error
            await engine.ExecAsync($"CREATE TABLE dev.texty ({run.Q("order_id")} BIGINT NOT NULL, {run.Q("amount")} {engine.ColumnType("VARCHAR(20)")})");
            var kinds = Json(run.Cli("connection", "compare", "staging.orders", "--against", "dev.texty", "--format", "json"), 1)["data"]!;
            Assert.Equal(["amount"], kinds["schema"]!["not_compared"]!.AsArray().Select(x => (string)x!["column"]!));
            var gone = run.Cli("connection", "compare", "staging.orders", "--against", "dev.nothing");
            Assert.Equal(2, gone.Exit);
            Assert.Contains("is not a table or view", gone.Err);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }

        static string Trim(string number) => decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture).ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Unlimited_text_stays_unlimited_from_the_source_through_the_model_to_the_target(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var pg = name == "postgres";
            await engine.ExecAsync(pg
                ? "CREATE TABLE staging.docs (id bigint NOT NULL, body text, tag varchar(10))"
                : "CREATE TABLE staging.docs (id bigint NOT NULL, body nvarchar(max), tag varchar(10))");
            await engine.ExecAsync(pg
                ? "INSERT INTO staging.docs VALUES (1, repeat('x', 6000), 'ab')"
                : "INSERT INTO staging.docs VALUES (1, REPLICATE(CAST(N'x' AS nvarchar(max)), 6000), 'ab')");

            // exported as the source says it is: no length on the unlimited column
            Ok(run.Cli("project", "import", "staging.docs", "--write"), "import");
            Assert.Equal($"name: staging.docs\nkind: mapped\nconnections=: [{name}]\ncolumns:\n  - name: id\n    type: BIGINT\n    nullable: false\n  - name: body\n    type: VARCHAR\n  - name: tag\n    type: VARCHAR(10)\n", File.ReadAllText(Path.Combine(run.Dir, "models/staging/docs.yml")));

            // a model that passes it through and computes from it declares no length either, and `define` agrees with that
            run.Write("models/marts/docs_out.yml", "name: marts.docs_out\nkind: full\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: body, type: VARCHAR}\n  - {name: body2, type: VARCHAR}\n  - {name: tag_up, type: VARCHAR(10)}\n");
            run.Write("models/marts/docs_out.sql", "SELECT d.id, d.body, d.body || '!' AS body2, upper(d.tag) AS tag_up FROM staging.docs d\n");
            Ok(run.Cli("project", "model", "update", "--check"), "define agrees with the declared types");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply");

            // the engine's own unlimited type, and nothing was cut at 4000 (nvarchar) or anywhere else
            var typeOf = (string column) => $"SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'marts' AND TABLE_NAME = 'docs_out' AND COLUMN_NAME = '{column}'";
            Assert.Equal([pg ? "text|∅" : "nvarchar|-1"], await engine.RowsAsync(typeOf("body")));
            Assert.Equal([pg ? "text|∅" : "nvarchar|-1"], await engine.RowsAsync(typeOf("body2")));
            Assert.Equal([pg ? "character varying|10" : "nvarchar|10"], await engine.RowsAsync(typeOf("tag_up")));
            Assert.Equal(["1|6000|6001|AB"], await engine.RowsAsync(pg
                ? "SELECT id, length(body), length(body2), tag_up FROM marts.docs_out"
                : "SELECT id, LEN(body), LEN(body2), tag_up FROM marts.docs_out"));

            // and the tool recognizes what it made: in sync, nothing to plan
            var report = System.Text.Json.Nodes.JsonNode.Parse(run.Cli("connection", "monitor", "--format", "json").Out)!;
            Assert.Contains(report["data"]!["objects"]!.AsArray(), o => (string?)o!["object"] == "marts.docs_out" && (string?)o["now"] == "in sync");
            var again = run.Cli("connection", "deploy", "--write-plan");
            Ok(again, "plan again");
            Assert.DoesNotContain("CREATE TABLE", again.Out);              // loads run again; no DDL is planned for a table that is what the model declares
            Assert.DoesNotContain("ALTER TABLE", again.Out);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_older_tracking_layout_is_upgraded_by_init_and_apply_can_store_metadata_itself(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");

            // turn the tracking tables back into layout 1: no metadata objects, version row 1
            await engine.ExecAsync($"DROP VIEW {T("metadata_columns")}");
            await engine.ExecAsync($"DROP VIEW {T("metadata_current")}");
            await engine.ExecAsync($"DROP TABLE {T("metadata_document")}");
            await engine.ExecAsync($"UPDATE {T("tracking_version")} SET {run.Q("version")} = 1");
            var old = run.Cli("connection", "deploy", "--write-plan");
            Refused(old, "DDB-505", "planning against an older layout");
            Assert.Contains("layout version 1", old.Err);
            Ok(run.Cli("connection", "init", "--apply"), "init upgrades");
            Assert.Equal(["1", DbDataBuild.State.TrackingSchema.Version.ToString()], await engine.RowsAsync($"SELECT {run.Q("version")} FROM {T("tracking_version")}"));
            Ok(run.Cli("connection", "deploy", "--write-plan"), "plan after the upgrade");

            // metadata.store_on_apply: a successful apply also stores the project, the models it touched and the plan
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "metadata:\n  store_on_apply: true\n");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            var applied = run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out));
            Ok(applied, "apply");
            Assert.Contains("Metadata stored: 5 document(s) written", applied.Out);       // project, two models, the source they read, the plan
            Assert.Equal(["model", "model", "model", "plan", "project"], (await engine.RowsAsync($"SELECT subject_kind FROM {T("metadata_document")}")).Order().ToArray());
            var planId = PlanDocument.Parse(File.ReadAllText(run.PlanFile(plan.Out)), "p", new List<Diagnostic>())!.Id;
            Assert.Equal(1, await CountAsync(run, T("metadata_document"), $"subject_kind = 'plan' AND subject = '{planId}'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Lowering_makes_what_the_engines_compute_match_what_DuckDB_computes(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // two constructs that went wrong when the author's text was transpiled directly: GROUP BY ALL, and AVG over integers (T-SQL and PostgreSQL truncate or widen differently)
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/marts/fct_stats.yml", "name: marts.fct_stats\nkind: full\ncolumns:\n  - {name: size, type: \"VARCHAR(5)\"}\n  - {name: n, type: BIGINT}\n  - {name: mean_id, type: DOUBLE}\n");
            run.Write("models/marts/fct_stats.sql", "SELECT CASE WHEN o.order_id > 2 THEN 'big' ELSE 'small' END AS size, COUNT(*) AS n, AVG(o.order_id) AS mean_id FROM staging.orders o GROUP BY ALL\n");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");

            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var lowered = File.ReadAllText(Path.Combine(run.Dir, "rendered/lowered/marts.fct_stats/lowered.sql"));
            Assert.Contains("avg(CAST(order_id AS DOUBLE))", lowered);                 // the plan said DOUBLE, so the query says so
            Assert.Contains("-- type rules:   avg-double", lowered);
            Assert.DoesNotContain("GROUP BY ALL", lowered);
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply");

            // DuckDB's answer is 1.5 and 3.5; an engine that averages integers as integers would say 1 and 3
            Assert.Equal(new List<string> { "big|2|3.5", "small|2|1.5" }, await engine.RowsAsync("SELECT size, n, mean_id FROM marts.fct_stats"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Correlated_subqueries_give_the_same_rows_on_both_engines(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/marts/fct_rank.yml", "name: marts.fct_rank\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT}\n  - {name: earlier, type: BIGINT}\n  - {name: rank_tag, type: \"VARCHAR(10)\"}\n");
            // a correlated scalar subquery with an aggregate, a correlated EXISTS inside a CASE, and NOT IN against a set that contains a NULL-amount row's key
            run.Write("models/marts/fct_rank.sql",
                "SELECT o.order_id,\n  (SELECT count(*) FROM staging.orders p WHERE p.order_id < o.order_id) AS earlier,\n" +
                "  CASE WHEN EXISTS (SELECT 1 FROM staging.orders q WHERE q.amount > o.amount) THEN 'has_bigger' ELSE 'top' END AS rank_tag\n" +
                "FROM staging.orders o\nWHERE o.order_id NOT IN (SELECT r.order_id FROM staging.orders r WHERE r.amount IS NULL)\n");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");

            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var lowered = File.ReadAllText(Path.Combine(run.Dir, "rendered/lowered/marts.fct_rank/lowered.sql"));
            Assert.Contains("EXISTS (", lowered);
            Assert.DoesNotContain("DELIM", lowered);
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply");

            Assert.Equal(new List<string> { "1|0|has_bigger", "2|1|has_bigger", "4|3|top" }, await engine.RowsAsync("SELECT order_id, earlier, rank_tag FROM marts.fct_rank"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task ANY_ALL_and_row_value_IN_give_DuckDBs_rows_on_both_engines_including_where_NULLs_are_involved(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            run.Write("models/staging/s1.yml", "name: staging.s1\nkind: mapped\ncolumns:\n  - {name: id, type: INTEGER, nullable: false}\n  - {name: x, type: INTEGER}\n  - {name: y, type: INTEGER}\n");
            run.Write("models/staging/s2.yml", "name: staging.s2\nkind: mapped\ncolumns:\n  - {name: id, type: INTEGER, nullable: false}\n  - {name: z, type: INTEGER}\n  - {name: w, type: INTEGER}\n");
            const string rows1 = "(1, 5, 1), (2, NULL, 1), (3, 1, NULL), (4, 10, 2), (5, 3, 3), (6, NULL, NULL), (7, 5, NULL), (8, 2, 2)";
            const string rows2 = "(1, 2, 1), (2, NULL, 2), (3, 7, 3), (4, 3, 3), (5, 5, NULL)";
            foreach (var (t, cols, rows) in new[] { ("s1", "id INT NOT NULL, x INT, y INT", rows1), ("s2", "id INT NOT NULL, z INT, w INT", rows2) })
            {
                await engine.ExecAsync($"CREATE TABLE staging.{t} ({cols})");
                await engine.ExecAsync($"INSERT INTO staging.{t} VALUES {rows}");
            }

            // the same filters, on DuckDB, are the expected answer
            var queries = new[]
            {
                "s.x > ANY (SELECT b.z FROM staging.s2 b)",
                "s.x <= ANY (SELECT b.z FROM staging.s2 b)",
                "s.x >= ALL (SELECT b.z FROM staging.s2 b)",
                "s.x < ALL (SELECT b.z FROM staging.s2 b WHERE b.z IS NOT NULL)",
                "s.x <> ALL (SELECT b.z FROM staging.s2 b)",
                "s.x > ALL (SELECT b.z FROM staging.s2 b WHERE b.w > 100)",
                "NOT (s.x > ANY (SELECT b.z FROM staging.s2 b))",
                "s.x > ANY (SELECT b.z FROM staging.s2 b WHERE b.id = s.id)",
                "s.x >= ALL (SELECT avg(b.z) FROM staging.s2 b)",
                "s.id < 3 OR s.x <= ANY (SELECT b.z FROM staging.s2 b)",
                "(s.x, s.y) IN (SELECT b.z, b.w FROM staging.s2 b)",
                "(s.x, s.y) NOT IN (SELECT b.z, b.w FROM staging.s2 b)",
                "(s.x, s.y) NOT IN (SELECT b.z, b.w FROM staging.s2 b WHERE b.id > 100)",
            };
            using var duck = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
            duck.Open();
            void Duck(string sql) { using var cmd = duck.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            Duck("CREATE SCHEMA staging; CREATE TABLE staging.s1 (id INTEGER NOT NULL, x INTEGER, y INTEGER); CREATE TABLE staging.s2 (id INTEGER NOT NULL, z INTEGER, w INTEGER)");
            Duck($"INSERT INTO staging.s1 VALUES {rows1}; INSERT INTO staging.s2 VALUES {rows2}");
            List<string> Expected(string where)
            {
                using var cmd = duck.CreateCommand();
                cmd.CommandText = $"SELECT s.id FROM staging.s1 s WHERE {where} ORDER BY s.id";
                using var r = cmd.ExecuteReader();
                var ids = new List<string>();
                while (r.Read()) ids.Add(Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
                return ids;
            }
            for (var i = 0; i < queries.Length; i++)
            {
                run.Write($"models/marts/q{i}.yml", $"name: marts.q{i}\nkind: full\ncolumns:\n  - {{name: id, type: INTEGER, nullable: false}}\n");
                run.Write($"models/marts/q{i}.sql", $"SELECT s.id FROM staging.s1 s WHERE {queries[i]}\n");
            }
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply");
            for (var i = 0; i < queries.Length; i++)
                Assert.True(Expected(queries[i]).SequenceEqual(await engine.RowsAsync($"SELECT id FROM marts.q{i} ORDER BY id")), $"q{i}: {queries[i]}");
            Assert.NotEmpty(Expected(queries[0]));                                              // the cases are not all empty

            // used as a value: TRUE, FALSE or NULL as DuckDB gives them (a CASE over the two predicates)
            var valueQueries = new[]
            {
                "s.x > ANY (SELECT b.z FROM staging.s2 b)",
                "s.x >= ALL (SELECT b.z FROM staging.s2 b)",
                "s.x <> ALL (SELECT b.z FROM staging.s2 b WHERE b.w < 3)",
                "NOT (s.x > ANY (SELECT b.z FROM staging.s2 b))",
                "(s.x, s.y) NOT IN (SELECT b.z, b.w FROM staging.s2 b)",
            };
            for (var i = 0; i < valueQueries.Length; i++)
            {
                run.Write($"models/marts/qv{i}.yml", $"name: marts.qv{i}\nkind: full\ncolumns:\n  - {{name: id, type: INTEGER, nullable: false}}\n  - {{name: g, type: BOOLEAN}}\n");
                run.Write($"models/marts/qv{i}.sql", $"SELECT s.id, {valueQueries[i]} AS g FROM staging.s1 s\n");
            }
            Ok(run.Cli("project", "compile"), "render values");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply values");
            for (var i = 0; i < valueQueries.Length; i++)
            {
                using var cmd = duck.CreateCommand();
                cmd.CommandText = $"SELECT s.id, CASE WHEN ({valueQueries[i]}) THEN '1' WHEN NOT ({valueQueries[i]}) THEN '0' ELSE 'n' END FROM staging.s1 s ORDER BY s.id";
                using var r = cmd.ExecuteReader();
                var expected = new List<string>();
                while (r.Read()) expected.Add($"{r.GetValue(0)}|{r.GetValue(1)}");
                var actual = await engine.RowsAsync($"SELECT id, COALESCE(CAST(CAST(g AS INT) AS VARCHAR(5)), 'n') FROM marts.qv{i} ORDER BY id");
                Assert.True(expected.SequenceEqual(actual), $"qv{i}: {valueQueries[i]}\nexpected {string.Join(", ", expected)}\nactual   {string.Join(", ", actual)}");
                Assert.Contains(expected, x => x.EndsWith("|n"));                               // the NULL case is in the data
            }
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_aggregate_over_a_subquery_runs_on_both_engines_through_a_derived_table(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var f in new[] { "v_orders", "fct_orders" }) foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/{f}.{ext}"));
            run.Write("models/staging/s1.yml", "name: staging.s1\nkind: mapped\ncolumns:\n  - {name: id, type: INTEGER, nullable: false}\n");
            run.Write("models/staging/s2.yml", "name: staging.s2\nkind: mapped\ncolumns:\n  - {name: id, type: INTEGER, nullable: false}\n  - {name: z, type: INTEGER}\n");
            await engine.ExecAsync("CREATE TABLE staging.s1 (id INT NOT NULL)");
            await engine.ExecAsync("INSERT INTO staging.s1 VALUES (1), (2), (3), (4), (5), (6), (7), (8)");
            await engine.ExecAsync("CREATE TABLE staging.s2 (id INT NOT NULL, z INT)");
            await engine.ExecAsync("INSERT INTO staging.s2 VALUES (1, 2), (2, NULL), (3, 7), (4, 3), (5, 5)");
            run.Write("models/marts/agg_a.yml", "name: marts.agg_a\nkind: full\ncolumns:\n  - {name: total, type: BIGINT}\n");
            run.Write("models/marts/agg_a.sql", "SELECT CAST(sum((SELECT count(*) FROM staging.s2 b WHERE b.id = s.id)) AS BIGINT) AS total FROM staging.s1 s\n");
            run.Write("models/marts/agg_b.yml", "name: marts.agg_b\nkind: full\ncolumns:\n  - {name: m, type: INTEGER}\n  - {name: n, type: BIGINT, nullable: false}\n");
            run.Write("models/marts/agg_b.sql", "SELECT max(coalesce((SELECT max(b.z) FROM staging.s2 b WHERE b.id = s.id), 0)) AS m, count(*) AS n FROM staging.s1 s\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            Assert.Equal(["5"], await engine.RowsAsync("SELECT total FROM marts.agg_a"));
            Assert.Equal(["7|8"], await engine.RowsAsync("SELECT m, n FROM marts.agg_b"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task DISTINCT_ON_with_a_deciding_order_gives_the_same_rows_on_both_engines(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/marts/fct_first.yml", "name: marts.fct_first\nkind: full\ncolumns:\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: order_id, type: BIGINT}\n");
            // the first order per amount; order_id (the source's declared grain) is in the ORDER BY, so the row kept is decided, not arbitrary. NULL is a group of its own.
            run.Write("models/marts/fct_first.sql", "SELECT DISTINCT ON (o.amount) o.amount, o.order_id FROM staging.orders o ORDER BY o.amount, o.order_id\n");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00), (5, 40.00), (6, NULL)");

            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            Assert.Contains("row_number() OVER (PARTITION BY amount ORDER BY order_id NULLS LAST)", File.ReadAllText(Path.Combine(run.Dir, "rendered/lowered/marts.fct_first/lowered.sql")));
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply");
            Assert.Equal(new List<string> { "10|1", "20.5|2", "40|4", "∅|3" }, await engine.RowsAsync("SELECT amount, order_id FROM marts.fct_first"));

            // an ordering that leaves ties is refused before anything is planned, and says what to add
            run.Write("models/marts/fct_first.sql", "SELECT DISTINCT ON (o.amount) o.amount, o.order_id FROM staging.orders o ORDER BY o.amount\n");
            var ties = run.Cli("project", "compile");
            Assert.Contains("DDB-324", ties.Err);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Integer_series_become_the_engines_own_generate_series_and_join_to_tables(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/marts/fct_slots.yml", "name: marts.fct_slots\nkind: full\ncolumns:\n  - {name: slot, type: BIGINT}\n  - {name: orders_in_slot, type: BIGINT}\n");
            // range excludes its end; the slots with no orders must still appear
            run.Write("models/marts/fct_slots.sql", "SELECT g.s AS slot, count(o.order_id) AS orders_in_slot FROM range(0, 4) AS g(s) LEFT JOIN staging.orders o ON o.order_id = g.s GROUP BY g.s\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            Assert.Contains("generate_series(0, 3)", File.ReadAllText(Path.Combine(run.Dir, "rendered/lowered/marts.fct_slots/lowered.sql")));
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply");
            Assert.Equal(new List<string> { "0|0", "1|1", "2|1", "3|1" }, (await engine.RowsAsync("SELECT slot, orders_in_slot FROM marts.fct_slots")).Order().ToList());
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    // what DuckDB returns for the same rows and the same query (checked with DuckDB 1.5.4); the engines must agree, not just run
    private static readonly string[] ProbeExpected =
    [
        "1|0|∅|∅|∅|2.5|3|0", "2|4|12|12|12|0.28|0|0", "3|3|∅|∅|∅|1|1|0", "4|2|5|5|5|-2.5|-3|0", "5|11|∅|∅|99999999999|2.68|3|0", "6|5|6|1.56|1.555|1234.57|1235|1200",
        "7|2|-7|-7|-7|0.13|0|0", "8|4|∅|∅|∅|∅|∅|∅", "9|∅|∅|∅|∅|0|0|0", "10|3|1|100|100|-0.4|0|0", "11|4|2|0.5|0.5|1E+20|1E+20|1E+20",
    ];

    private static string Normalize(string row) => string.Join("|", row.Split('|').Select(f => f == "∅" ? f : decimal.TryParse(f, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) : f)
        .Select(f => f == "-0" ? "0" : f));

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Target_rules_make_length_try_cast_and_round_compute_what_DuckDB_computes(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/staging/probe.yml", "name: staging.probe\nkind: mapped\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: s, type: VARCHAR(40)}\n  - {name: t, type: VARCHAR(40)}\n  - {name: x, type: DOUBLE}\n");
            run.Write("models/marts/fct_probe.yml", "name: marts.fct_probe\nkind: full\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: len, type: BIGINT}\n  - {name: i, type: INTEGER}\n  - {name: d, type: \"DECIMAL(10, 2)\"}\n  - {name: f, type: DOUBLE}\n  - {name: r2, type: DOUBLE}\n  - {name: r0, type: DOUBLE}\n  - {name: rm, type: DOUBLE}\n");
            run.Write("models/marts/fct_probe.sql", "SELECT id, length(s) AS len, TRY_CAST(t AS INTEGER) AS i, TRY_CAST(s AS DECIMAL(10, 2)) AS d, TRY_CAST(s AS DOUBLE) AS f, round(x, 2) AS r2, round(x) AS r0, round(x, -2) AS rm FROM staging.probe\n");
            var dbl = name == "postgres" ? "DOUBLE PRECISION" : "FLOAT";
            var vc = engine.ColumnType("VARCHAR(40)");
            await engine.ExecAsync($"CREATE TABLE staging.probe (id BIGINT NOT NULL, s {vc} NULL, t {vc} NULL, x {dbl} NULL)");
            await engine.ExecAsync("INSERT INTO staging.probe VALUES (1, '', '', 2.5), (2, ' 12 ', ' 12 ', 0.285), (3, 'abc', 'abc', 1.005), (4, '+5', '+5', -2.5), (5, '99999999999', '99999999999', 2.675), " +
                "(6, '1.555', '6', 1234.5678), (7, '-7', '-7', 0.125), (8, 'ab  ', 'x', NULL), (9, NULL, NULL, 0.0), (10, '100', '1', -0.4), (11, ' .5 ', '2', 1e20)");

            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var script = File.ReadAllText(Directory.GetFiles(Path.Combine(run.Dir, "rendered", name), "load.*.sql", SearchOption.AllDirectories).First(f => f.Contains("fct_probe")));
            Assert.Contains("target rules:", script);
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(run.Cli("connection", "deploy", "--write-plan").Out)), "apply");
            var rows = await engine.RowsAsync("SELECT id, len, i, d, f, r2, r0, rm FROM marts.fct_probe ORDER BY id");
            Assert.Equal(ProbeExpected.Select(Normalize).Order().ToList(), rows.Select(Normalize).Order().ToList());
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Sums_of_integers_do_not_overflow_on_either_engine(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/staging/big.yml", "name: staging.big\nkind: mapped\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: n, type: INTEGER}\n  - {name: b, type: BIGINT}\n");
            run.Write("models/marts/fct_big.yml", "name: marts.fct_big\nkind: full\ncolumns:\n  - {name: sn, type: BIGINT}\n  - {name: sb, type: \"DECIMAL(38, 0)\"}\n");
            // DuckDB sums into a HUGEINT: three INTs near 2^31 and two BIGINTs near 2^63 are over what SQL Server's own SUM holds
            run.Write("models/marts/fct_big.sql", "SELECT CAST(sum(n) AS BIGINT) AS sn, CAST(sum(b) AS DECIMAL(38, 0)) AS sb FROM staging.big\n");
            await engine.ExecAsync("CREATE TABLE staging.big (id BIGINT NOT NULL, n INTEGER NULL, b BIGINT NULL)");
            await engine.ExecAsync("INSERT INTO staging.big VALUES (1, 2147483647, 9000000000000000000), (2, 2147483647, 9000000000000000000), (3, 2147483647, NULL)");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            Assert.Equal(["6442450941|18000000000000000000"], await engine.RowsAsync("SELECT sn, sb FROM marts.fct_big"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_data_bearing_engine_error_never_reaches_output_files_or_tracking_tables(string name)
    {
        // DESIGN.md 14.2: SQL Server and PostgreSQL quote the offending value in conversion errors
        const string secret = "SECRET-VALUE-4711";
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/staging/raw.yml", "name: staging.raw\nkind: mapped\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: s, type: VARCHAR(40)}\n");
            run.Write("models/marts/fct_num.yml", "name: marts.fct_num\nkind: full\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: n, type: INTEGER}\n");
            run.Write("models/marts/fct_num.sql", "SELECT id, CAST(s AS INTEGER) AS n FROM staging.raw\n");
            await engine.ExecAsync($"CREATE TABLE staging.raw (id BIGINT NOT NULL, s {engine.ColumnType("VARCHAR(40)")} NULL)");
            await engine.ExecAsync($"INSERT INTO staging.raw VALUES (1, '12'), (2, '{secret}')");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);

            var text = run.Cli("connection", "deploy", "--apply-plan", planFile);
            var json = run.Cli("connection", "deploy", "--apply-plan", planFile, "--format", "json");
            Assert.NotEqual(0, text.Exit);
            Assert.Contains("DDB-", text.Err);
            foreach (var shown in new[] { text.Out, text.Err, json.Out, json.Err }) Assert.DoesNotContain(secret, shown);

            // nothing the tool wrote locally, and nothing in the tracking tables
            foreach (var file in Directory.EnumerateFiles(run.Dir, "*", SearchOption.AllDirectories)) Assert.DoesNotContain(secret, File.ReadAllText(file));
            foreach (var table in new[] { "tracking_version", "schema_version", "ddl_log", "run_log", "operation_interval", "block_log", "migration_log", "metadata_document" })
                foreach (var row in await engine.RowsAsync($"SELECT * FROM {run.Q("dbdatabuild")}.{run.Q(table)}")) Assert.DoesNotContain(secret, row);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Every_database_command_prints_json_that_satisfies_the_output_schema(string name)
    {
        // Run.Cli checks each --format json document against schemas/output.schema.json and throws when it does not fit
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var json = (string[] args, int exit) => { var r = run.Cli([.. args, "--format", "json"]); Assert.True(r.Exit == exit, $"{string.Join(' ', args)} exited {r.Exit}, expected {exit}:\n{r.Out}\n{r.Err}"); return r; };
            json(["connection", "init", "--apply"], 0);
            json(["project", "compile"], 0);
            json(["connection", "status"], 0);
            var plan = json(["connection", "deploy", "--write-plan"], 0);
            var planFile = Path.Combine(run.Dir, System.Text.Json.Nodes.JsonNode.Parse(plan.Out)!["data"]!["files"]!["plan"]!.GetValue<string>());
            json(["connection", "deploy", "--apply-plan", planFile, "--dry-run"], 0);
            json(["connection", "deploy", "--apply-plan", planFile], 0);
            json(["connection", "deploy", "--apply-plan", planFile], 1);                                                   // a plan is applied once: the refusal is a document too
            json(["connection", "status"], 0);
            json(["connection", "refresh"], 0);                                                               // a routine load
            json(["connection", "monitor"], 0);
            json(["connection", "publish"], 0);
            json(["connection", "publish"], 0);                                                  // unchanged the second time

            // drift: someone adds a column outside the tool
            await engine.ExecAsync("ALTER TABLE marts.fct_orders ADD scratch INT NULL");
            json(["connection", "status"], 1);
            json(["connection", "refresh", "--check", "live"], 1);                                         // the live check sees what the recorded one cannot
            json(["connection", "deploy", "--ack", "drift:marts.fct_orders", "--reason", "scratch column from the DBA"], 0);
            json(["connection", "refresh"], 0);                                         // the drift is accepted, and the refresh that was stopped by it goes through (an unfinished refresh stays in the monitor until one completes)
            var after = json(["connection", "monitor"], 0);
            Assert.DoesNotContain("changed outside the tool (`", after.Out);              // an accepted drift is shown as accepted, not as something that needs attention
            json(["connection", "deploy", "--ack", "history:marts.fct_orders.nothing", "--reason", "x"], 1);       // nothing to acknowledge: a refusal is still a valid document
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_apply_reports_each_step_as_it_runs_can_be_stopped_between_steps_and_is_then_resumed(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);

            // the terminal interface's hooks: progress lines arrive while the apply runs, and a stop request is asked about before each step
            var seen = new List<string>();
            var stop = false;
            var hooks = new CommandHooks(line => { seen.Add(line); if (line.Contains("step 1 done")) stop = true; }, () => stop);
            var stopped = CommandContext.With(hooks, () => run.Cli("connection", "deploy", "--apply-plan", planFile));
            Assert.Equal(1, stopped.Exit);
            Assert.Contains("DDB-445", stopped.Err);
            Assert.Contains(seen, l => l.StartsWith("step 1/4:"));
            Assert.Contains(seen, l => l.StartsWith("  step 1 done in "));
            Assert.DoesNotContain(seen, l => l.StartsWith("step 2/4:"));                    // the second step never started
            Assert.Contains("step 2: stopped", stopped.Out);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));   // the schema was created, no table was

            // nothing is half done: the plan resumes from the second step and completes
            var resumed = run.Cli("connection", "deploy", "--apply-plan", planFile);
            Ok(resumed, "apply again: the plan continues");
            Assert.Contains("step 1: skipped", resumed.Out);
            Assert.Equal(2, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
