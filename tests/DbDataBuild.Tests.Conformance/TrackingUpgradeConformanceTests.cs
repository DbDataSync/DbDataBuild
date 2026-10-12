using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DbDataBuild.State;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Tracking tables of the older layout (no `connection` column in any record) brought to the current one in place, on the real engines.</summary>
[Trait("Group", "apply")]
public class TrackingUpgradeConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Tables_of_layout_three_get_the_connection_column_and_keep_their_rows(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-upgrade-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        var schema = "dbdatabuild_up_" + Guid.NewGuid().ToString("N")[..6];
        var ddl = TrackingDdl.For(name);
        string Q(string id) => ddl.Quote(id);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name}, schema: {schema} }}\n" + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : ""));

            // the layout as it was: this layout, less the column, with the keys as they were, the version recorded as 3
            Ok(Cli("connection", "init", name, "--apply"), "init");
            foreach (var view in new[] { "metadata_columns", "metadata_current" }) await engine.ExecAsync($"DROP VIEW {Q(schema)}.{Q(view)}");
            foreach (var t in TrackingSchema.Tables.Where(t => t.Columns.Any(c => c.Name == "connection")))
            {
                var old = string.Join(", ", t.PrimaryKey.Where(k => k != "connection").Select(Q));
                await engine.ExecAsync($"ALTER TABLE {Q(schema)}.{Q(t.Name)} DROP CONSTRAINT {Q("pk_" + t.Name)}");
                await engine.ExecAsync($"ALTER TABLE {Q(schema)}.{Q(t.Name)} DROP COLUMN {Q("connection")}");
                await engine.ExecAsync($"ALTER TABLE {Q(schema)}.{Q(t.Name)} ADD CONSTRAINT {Q("pk_" + t.Name)} PRIMARY KEY ({old})");
            }
            await engine.ExecAsync($"UPDATE {Q(schema)}.{Q("tracking_version")} SET {Q("version")} = 3");
            await engine.ExecAsync($"INSERT INTO {Q(schema)}.{Q("migration_log")} ({Q("plan_id")}, {Q("plan_hash")}, {Q("plan_text")}, {Q("applied_by")}, {Q("applied_utc")}, {Q("status")}) VALUES ('old-plan', '{new string('a', 64)}', 'text', 'someone', '2026-01-01 00:00:00', 'completed')");

            // without the upgrade the tool says what to do
            var refused = Cli("connection", "deploy", name, "--write-plan");
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("init --upgrade", refused.Err + refused.Out);

            // the script is printed for review first
            var review = Cli("connection", "init", name, "--upgrade");
            Ok(review, "review");
            Assert.Contains("upgrade-01", review.Out);
            Assert.Contains("This is an upgrade", review.Out);

            Ok(Cli("connection", "init", name, "--upgrade", "--apply"), "upgrade");
            Assert.Equal([name], await engine.RowsAsync($"SELECT {Q("connection")} FROM {Q(schema)}.{Q("migration_log")}"));        // the rows it had belong to the connection that was initialized
            Assert.Equal(["6"], await engine.RowsAsync($"SELECT CAST(MAX({Q("version")}) AS VARCHAR(10)) FROM {Q(schema)}.{Q("tracking_version")}"));
            // the key includes the connection now: another connection can have the same plan id
            await engine.ExecAsync($"INSERT INTO {Q(schema)}.{Q("migration_log")} ({Q("connection")}, {Q("plan_id")}, {Q("plan_hash")}, {Q("plan_text")}, {Q("applied_by")}, {Q("applied_utc")}, {Q("status")}) VALUES ('other', 'old-plan', '{new string('b', 64)}', 'text', 'someone', '2026-01-01 00:00:00', 'completed')");
            Assert.Equal(2, int.Parse((await engine.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM {Q(schema)}.{Q("migration_log")}")).Single()));
            var report = Cli("connection", "monitor", name);
            Assert.Contains("old-plan", report.Out);                                                                          // the tool reads the upgraded tables
            Assert.DoesNotContain("DDB-505", report.Err + report.Out);
            Ok(Cli("connection", "init", name, "--upgrade", "--apply"), "a second upgrade changes nothing");              // safe to repeat
            Assert.Equal(2, int.Parse((await engine.RowsAsync($"SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM {Q(schema)}.{Q("migration_log")}")).Single()));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { await engine.ExecAsync(name == "postgres" ? $"DROP SCHEMA IF EXISTS {Q(schema)} CASCADE" : $"DECLARE @s nvarchar(max) = N''; SELECT @s += N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.views WHERE schema_id = SCHEMA_ID('{schema}'); SELECT @s += N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.tables WHERE schema_id = SCHEMA_ID('{schema}'); EXEC(@s); DROP SCHEMA {Q(schema)}"); } catch (Exception) { }
        }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_store_of_layout_four_is_not_claimed_by_init_and_is_brought_to_six_by_the_upgrade(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-upgrade5-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        var schema = "dbdatabuild_up5_" + Guid.NewGuid().ToString("N")[..6];
        var ddl = TrackingDdl.For(name);
        string Q(string id) => ddl.Quote(id);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name}, schema: {schema} }}\n" + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : ""));
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);

            // the layout as it was: this layout without the two columns of layout five, recorded as four (the table of layout six is left: a store at four is not claimed whatever else it holds)
            foreach (var (table, column) in TrackingSchema.AddedInLayout5) await engine.ExecAsync($"ALTER TABLE {Q(schema)}.{Q(table)} DROP COLUMN {Q(column)}");
            await engine.ExecAsync($"DELETE FROM {Q(schema)}.{Q("tracking_version")} WHERE {Q("version")} = 6");
            await engine.ExecAsync($"INSERT INTO {Q(schema)}.{Q("tracking_version")} ({Q("version")}, {Q("tool_version")}, {Q("applied_utc")}) VALUES (4, 'old', {(name == "postgres" ? "now()" : "SYSUTCDATETIME()")})");

            // init never alters, so it does not claim the store: the version stays four and the tool says what to do
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal(["4"], await engine.RowsAsync($"SELECT CAST(MAX({Q("version")}) AS VARCHAR(10)) FROM {Q(schema)}.{Q("tracking_version")}"));
            var refused = Cli("connection", "monitor", name);
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("init --upgrade", refused.Err + refused.Out);

            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);
            Assert.Equal(["6"], await engine.RowsAsync($"SELECT CAST(MAX({Q("version")}) AS VARCHAR(10)) FROM {Q(schema)}.{Q("tracking_version")}"));
            Assert.Equal(0, Cli("connection", "monitor", name).Exit);
            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);          // safe to repeat
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { await engine.ExecAsync(name == "postgres" ? $"DROP SCHEMA IF EXISTS {Q(schema)} CASCADE" : $"DECLARE @s nvarchar(max) = N''; SELECT @s += N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.views WHERE SCHEMA_NAME(schema_id) = N'{schema}'; EXEC(@s); SELECT @s = N''; SELECT @s += N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.tables WHERE SCHEMA_NAME(schema_id) = N'{schema}'; EXEC(@s); DROP SCHEMA [{schema}];"); } catch (Exception) { }
        }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_store_of_layout_five_lacks_only_the_info_table_which_init_creates_and_the_upgrade_too(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-upgrade6-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        var schema = "dbdatabuild_up6_" + Guid.NewGuid().ToString("N")[..6];
        var ddl = TrackingDdl.For(name);
        string Q(string id) => ddl.Quote(id);
        async Task<string> Version() => (await engine.RowsAsync($"SELECT CAST(MAX({Q("version")}) AS VARCHAR(10)) FROM {Q(schema)}.{Q("tracking_version")}")).Single();
        async Task BackToFive()
        {
            await engine.ExecAsync($"DROP TABLE IF EXISTS {Q(schema)}.{Q(TrackingSchema.AddedInLayout6)}");
            await engine.ExecAsync($"DELETE FROM {Q(schema)}.{Q("tracking_version")} WHERE {Q("version")} IN (5, 6)");
            await engine.ExecAsync($"INSERT INTO {Q(schema)}.{Q("tracking_version")} ({Q("version")}, {Q("tool_version")}, {Q("applied_utc")}) VALUES (5, 'old', {(name == "postgres" ? "now()" : "SYSUTCDATETIME()")})");
        }
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name}, schema: {schema} }}\n" + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : ""));
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal("6", await Version());

            // the layout as it was: this layout without the info table, recorded as five
            await BackToFive();
            var refused = Cli("connection", "monitor", name);
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("DDB-505", refused.Err + refused.Out);
            Assert.Contains("object_info", refused.Err + refused.Out);                                  // it says what is missing and what to run

            // init alters nothing and creates the table, so it brings the store to six
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal("6", await Version());
            Assert.Empty(await engine.RowsAsync($"SELECT {Q("connection")} FROM {Q(schema)}.{Q(TrackingSchema.AddedInLayout6)}"));
            Assert.Equal(0, Cli("connection", "monitor", name).Exit);

            // and so does the upgrade, which is also safe to repeat
            await BackToFive();
            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);
            Assert.Equal("6", await Version());
            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);
            Assert.Equal(0, Cli("connection", "monitor", name).Exit);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { await engine.ExecAsync(name == "postgres" ? $"DROP SCHEMA IF EXISTS {Q(schema)} CASCADE" : $"DECLARE @s nvarchar(max) = N''; SELECT @s += N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.views WHERE SCHEMA_NAME(schema_id) = N'{schema}'; EXEC(@s); SELECT @s = N''; SELECT @s += N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.tables WHERE SCHEMA_NAME(schema_id) = N'{schema}'; EXEC(@s); DROP SCHEMA [{schema}];"); } catch (Exception) { }
        }
    }
}
