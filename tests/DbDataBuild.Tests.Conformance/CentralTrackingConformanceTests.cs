using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Tracking kept apart from the data: a SQL Server connection built by the tool, its records on a PostgreSQL one (central tracking), and the same project with tracking off. The records say which connection
/// they are about, the data connection holds none of them, and every command that works on the records finds them where they are.
/// </summary>
[Trait("Group", "apply")]
public partial class CentralTrackingConformanceTests
{
    private const string Orders = "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string Fct = "name: marts.fct_orders\nkind: full\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctSql = "SELECT order_id, amount FROM staging.orders\n";

    [GeneratedRegex(@"plan:\s+(\S+\.plan\.yml)")]
    private static partial Regex PlanPath();

    private sealed class Setup(Engine data, Engine tracking, string dir, string config)
    {
        public Engine Data => data;
        public Engine Tracking => tracking;
        public string Dir => dir;

        // sqlserver is the data connection, postgres keeps the records
        public string? Env(string v) => v is "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" ? data.ConnectionString : v is "DBDATABUILD_POSTGRES_READ" or "DBDATABUILD_POSTGRES_WRITE" ? tracking.ConnectionString : null;

        public (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }

        public void Write(string rel, string text)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public string PlanFile(string output) => Path.Combine(dir, PlanPath().Match(output).Groups[1].Value);
        public string Config => config;
    }

    private static async Task<Setup> Start(string tracking)
    {
        var data = EngineEnv.Require("sqlserver");
        var records = EngineEnv.Require("postgres");
        await data.StartAsync(); await records.StartAsync();
        var s = new Setup(data, records, Path.Combine(Path.GetTempPath(), "ddb-central-" + Guid.NewGuid().ToString("N")), tracking);
        s.Write("dbdatabuild.yml", $"defaults: {{connections: [sqlserver]}}\n{tracking}");
        s.Write("models/staging/orders.yml", Orders);
        s.Write("models/marts/fct_orders.yml", Fct);
        s.Write("models/marts/fct_orders.sql", FctSql);
        await data.ExecAsync("EXEC('CREATE SCHEMA staging')");
        await data.ExecAsync("CREATE TABLE staging.orders ([order_id] BIGINT NOT NULL, [amount] DECIMAL(14,2))");
        await data.ExecAsync("INSERT INTO staging.orders VALUES (1, 10.00), (2, 20.50)");
        return s;
    }

    private static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

    [SkippableFact]
    public async Task The_records_of_one_connection_live_on_another_and_every_command_finds_them_there()
    {
        var s = await Start("tracking: { connection: postgres }\n");
        await using var data = s.Data; await using var records = s.Tracking;
        try
        {
            Ok(s.Cli("project", "compile"), "render");
            // the tracking tables go to the tracking connection, and the data connection gets none
            Ok(s.Cli("connection", "init", "postgres", "--apply"), "init on the tracking connection");
            Assert.Contains("keeps no records of its own", s.Cli("connection", "init", "sqlserver").Err);
            Assert.Empty(await data.RowsAsync("SELECT 1 FROM sys.schemas WHERE name = 'dbdatabuild'"));

            var plan = s.Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(plan, "plan");
            Assert.DoesNotContain("Nothing is tracked", plan.Err);
            Ok(s.Cli("connection", "deploy", "--apply-plan", s.PlanFile(plan.Out)), "apply");
            Assert.Equal(2, int.Parse((await data.RowsAsync("SELECT COUNT(*) FROM marts.fct_orders")).Single()));
            Assert.Empty(await data.RowsAsync("SELECT 1 FROM sys.schemas WHERE name = 'dbdatabuild'"));                    // still none: the data connection never held a record

            // every record says which connection it is about
            Assert.Equal(["sqlserver"], (await records.RowsAsync("SELECT DISTINCT \"connection\" FROM dbdatabuild.migration_log")));
            Assert.Equal(["sqlserver"], (await records.RowsAsync("SELECT DISTINCT \"connection\" FROM dbdatabuild.run_log")));
            Assert.Equal(["sqlserver"], (await records.RowsAsync("SELECT DISTINCT \"connection\" FROM dbdatabuild.schema_version")));
            Assert.Contains("completed", await records.RowsAsync("SELECT status FROM dbdatabuild.migration_log"));

            // the baseline is read from the records on the other connection: nothing left to build, and an outside change is seen
            var again = s.Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(again, "second plan");
            Assert.DoesNotContain("create table", File.ReadAllText(s.PlanFile(again.Out)));
            await data.ExecAsync("ALTER TABLE marts.fct_orders ADD [sneaky] INT NULL");
            var drift = s.Cli("connection", "deploy", "sqlserver", "--write-plan");
            Assert.NotEqual(0, drift.Exit);
            Assert.Contains("DDB-430", drift.Err);

            // ack writes the acknowledgement to the records, and report reads them
            Ok(s.Cli("connection", "deploy", "sqlserver", "--ack", "drift:marts.fct_orders", "--reason", "known"), "ack");
            Assert.Equal(["sqlserver"], await records.RowsAsync("SELECT DISTINCT \"connection\" FROM dbdatabuild.block_log"));
            var report = s.Cli("connection", "monitor", "sqlserver");
            Ok(report, "report");
            Assert.Contains("completed", report.Out);
            Assert.Contains("records on postgres", report.Out);

            // a plan applied twice, or by another connection's name, finds its own records: plan ids are per plan, rows per connection
            var second = s.Cli("connection", "deploy", "--apply-plan", s.PlanFile(again.Out));
            Assert.True(second.Exit == 0 || second.Err.Contains("DDB"), second.Out + second.Err);
        }
        finally { try { Directory.Delete(s.Dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task With_no_tracking_the_tool_plans_from_declared_against_live_and_marks_every_change_to_an_existing_object_risky()
    {
        var s = await Start("tracking: none\n");
        await using var data = s.Data; await using var records = s.Tracking;
        try
        {
            Ok(s.Cli("project", "compile"), "render");
            var plan = s.Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(plan, "plan");
            Assert.DoesNotContain("DDB-232", plan.Err);                                                                // `none` is a choice: no warning
            Ok(s.Cli("connection", "deploy", "--apply-plan", s.PlanFile(plan.Out)), "apply");
            Assert.Equal(2, int.Parse((await data.RowsAsync("SELECT COUNT(*) FROM marts.fct_orders")).Single()));
            Assert.Empty(await data.RowsAsync("SELECT 1 FROM sys.schemas WHERE name = 'dbdatabuild'"));
            Assert.Empty(await records.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_schema = 'dbdatabuild'"));      // nothing was recorded anywhere

            // an object that exists is not adopted, drifted or blocked: it is judged against the declaration
            var again = s.Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(again, "second plan");
            Assert.DoesNotContain("create table", File.ReadAllText(s.PlanFile(again.Out)));

            // a change to it is risky, because nothing says it is safe
            s.Write("models/marts/fct_orders.yml", Fct.Replace("  - {name: amount, type: \"DECIMAL(14, 2)\"}\n", "  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: note, type: \"VARCHAR(20)\"}\n"));
            s.Write("models/marts/fct_orders.sql", "SELECT order_id, amount, CAST(NULL AS VARCHAR(20)) AS note FROM staging.orders\n");
            Ok(s.Cli("project", "compile"), "render after the change");
            s.Write("answers.yml", "answers:\n  - {id: Q-history-marts.fct_orders.note, choice: not_backfilled, note: \"no history\"}\n");
            var changed = s.Cli("connection", "deploy", "sqlserver", "--write-plan", "--answers", Path.Combine(s.Dir, "answers.yml"));
            if (changed.Exit == 0)
            {
                var text = File.ReadAllText(s.PlanFile(changed.Out));
                Assert.Contains("risk: risky", text);
                Assert.Contains("untracked: no record says what the tool built", text);
                var refused = s.Cli("connection", "deploy", "--apply-plan", s.PlanFile(changed.Out));
                Assert.NotEqual(0, refused.Exit);                                                                       // risky needs the person's allowance
                Assert.Contains("--allow-risky", refused.Err);
                Ok(s.Cli("connection", "deploy", "--apply-plan", s.PlanFile(changed.Out), "--allow-risky"), "apply with the allowance");
                Assert.Equal(["amount", "note", "order_id"], await data.RowsAsync("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'marts' AND TABLE_NAME = 'fct_orders' ORDER BY ORDINAL_POSITION"));
            }
            else Assert.Fail("the plan for a new column should be a plan, with questions answered or not needed:\n" + changed.Out + changed.Err);

            // the commands that work on the records say there are none
            var report = s.Cli("connection", "monitor", "sqlserver");
            Assert.NotEqual(0, report.Exit);
            Assert.Contains("is not tracked", report.Err);
        }
        finally { try { Directory.Delete(s.Dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_project_with_no_tracking_section_warns_on_plan_and_apply_and_records_nothing()
    {
        var s = await Start("");
        await using var data = s.Data; await using var records = s.Tracking;
        try
        {
            Ok(s.Cli("project", "compile"), "render");
            var plan = s.Cli("connection", "deploy", "sqlserver", "--write-plan");
            Ok(plan, "plan");
            Assert.Contains("DDB-232", plan.Err);
            var applied = s.Cli("connection", "deploy", "--apply-plan", s.PlanFile(plan.Out));
            Ok(applied, "apply");
            Assert.Contains("DDB-232", applied.Err);
            Assert.Equal(2, int.Parse((await data.RowsAsync("SELECT COUNT(*) FROM marts.fct_orders")).Single()));
            Assert.Empty(await data.RowsAsync("SELECT 1 FROM sys.schemas WHERE name = 'dbdatabuild'"));
        }
        finally { try { Directory.Delete(s.Dir, true); } catch (IOException) { } }
    }
}
