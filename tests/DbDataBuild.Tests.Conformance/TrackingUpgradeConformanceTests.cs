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
            Assert.Equal([TrackingSchema.Version.ToString()], await engine.RowsAsync($"SELECT CAST(MAX({Q("version")}) AS VARCHAR(10)) FROM {Q(schema)}.{Q("tracking_version")}"));
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
    public async Task A_store_of_layout_four_is_not_claimed_by_init_and_is_brought_to_seven_by_the_upgrade(string name)
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
            await engine.ExecAsync($"DELETE FROM {Q(schema)}.{Q("tracking_version")} WHERE {Q("version")} = 7");
            await engine.ExecAsync($"INSERT INTO {Q(schema)}.{Q("tracking_version")} ({Q("version")}, {Q("tool_version")}, {Q("applied_utc")}) VALUES (4, 'old', {(name == "postgres" ? "now()" : "SYSUTCDATETIME()")})");

            // init never alters, so it does not claim the store: the version stays four and the tool says what to do
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal(["4"], await engine.RowsAsync($"SELECT CAST(MAX({Q("version")}) AS VARCHAR(10)) FROM {Q(schema)}.{Q("tracking_version")}"));
            var refused = Cli("connection", "monitor", name);
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("init --upgrade", refused.Err + refused.Out);

            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);
            Assert.Equal([TrackingSchema.Version.ToString()], await engine.RowsAsync($"SELECT CAST(MAX({Q("version")}) AS VARCHAR(10)) FROM {Q(schema)}.{Q("tracking_version")}"));
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
    public async Task A_store_of_layout_five_lacks_the_info_table_and_the_refresh_columns_which_the_upgrade_adds(string name)
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
            await engine.ExecAsync($"DROP TABLE IF EXISTS {Q(schema)}.{Q(TrackingSchema.TableAddedInLayout7)}");
            foreach (var (table, column) in TrackingSchema.AddedInLayout7) await engine.ExecAsync($"ALTER TABLE {Q(schema)}.{Q(table)} DROP COLUMN {Q(column)}");
            await engine.ExecAsync($"DELETE FROM {Q(schema)}.{Q("tracking_version")} WHERE {Q("version")} IN (5, 6, 7)");
            await engine.ExecAsync($"INSERT INTO {Q(schema)}.{Q("tracking_version")} ({Q("version")}, {Q("tool_version")}, {Q("applied_utc")}) VALUES (5, 'old', {(name == "postgres" ? "now()" : "SYSUTCDATETIME()")})");
        }
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name}, schema: {schema} }}\n" + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : ""));
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal("7", await Version());

            // the layout as it was: this layout without the info table and the refresh plan columns, recorded as five
            await BackToFive();
            var refused = Cli("connection", "monitor", name);
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("DDB-505", refused.Err + refused.Out);
            Assert.Contains("object_info", refused.Err + refused.Out);                                  // it says what is missing and what to run

            // init alters nothing: it creates the tables that are missing and does not claim a store whose columns are missing
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal("5", await Version());
            Assert.Empty(await engine.RowsAsync($"SELECT {Q("connection")} FROM {Q(schema)}.{Q(TrackingSchema.AddedInLayout6)}"));
            Assert.NotEqual(0, Cli("connection", "monitor", name).Exit);

            // the upgrade adds them, and is safe to repeat
            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);
            Assert.Equal("7", await Version());
            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);
            Assert.Equal(0, Cli("connection", "monitor", name).Exit);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { await engine.ExecAsync(name == "postgres" ? $"DROP SCHEMA IF EXISTS {Q(schema)} CASCADE" : $"DECLARE @s nvarchar(max) = N''; SELECT @s += N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.views WHERE SCHEMA_NAME(schema_id) = N'{schema}'; EXEC(@s); SELECT @s = N''; SELECT @s += N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.tables WHERE SCHEMA_NAME(schema_id) = N'{schema}'; EXEC(@s); DROP SCHEMA [{schema}];"); } catch (Exception) { }
        }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_store_of_layout_six_gets_the_refresh_plan_columns_and_table_from_the_upgrade_and_keeps_its_rows(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-upgrade7-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        var schema = "dbdatabuild_up7_" + Guid.NewGuid().ToString("N")[..6];
        var ddl = TrackingDdl.For(name);
        string Q(string id) => ddl.Quote(id);
        async Task<string> Version() => (await engine.RowsAsync($"SELECT CAST(MAX({Q("version")}) AS VARCHAR(10)) FROM {Q(schema)}.{Q("tracking_version")}")).Single();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name}, schema: {schema} }}\n" + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : ""));
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal("7", await Version());
            await engine.ExecAsync($"INSERT INTO {Q(schema)}.{Q("migration_log")} ({Q("connection")}, {Q("plan_id")}, {Q("plan_hash")}, {Q("plan_text")}, {Q("applied_by")}, {Q("applied_utc")}, {Q("status")}) VALUES ('{name}', 'ref-old', '{new string('a', 64)}', 'text', 'me', {(name == "postgres" ? "now()" : "SYSUTCDATETIME()")}, 'completed')");

            // the layout as it was
            await engine.ExecAsync($"DROP TABLE {Q(schema)}.{Q(TrackingSchema.TableAddedInLayout7)}");
            foreach (var (table, column) in TrackingSchema.AddedInLayout7) await engine.ExecAsync($"ALTER TABLE {Q(schema)}.{Q(table)} DROP COLUMN {Q(column)}");
            await engine.ExecAsync($"UPDATE {Q(schema)}.{Q("tracking_version")} SET {Q("version")} = 6");
            var refused = Cli("connection", "monitor", name);
            Assert.NotEqual(0, refused.Exit);
            Assert.Contains("DDB-505", refused.Err + refused.Out);
            Assert.Contains("layout version 6", refused.Err + refused.Out);

            // init creates the table and alters nothing, so the store is still six; the upgrade adds the columns
            Assert.Equal(0, Cli("connection", "init", name, "--apply").Exit);
            Assert.Equal("6", await Version());
            var review = Cli("connection", "init", name, "--upgrade");
            Assert.Contains("layout 7", review.Out);
            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);
            Assert.Equal("7", await Version());
            Assert.Equal(["ref-old"], await engine.RowsAsync($"SELECT {Q("plan_id")} FROM {Q(schema)}.{Q("migration_log")}"));
            foreach (var (table, column) in TrackingSchema.AddedInLayout7) Assert.Empty(await engine.RowsAsync($"SELECT {Q(column)} FROM {Q(schema)}.{Q(table)} WHERE {Q(column)} IS NOT NULL"));
            Assert.Equal(0, Cli("connection", "monitor", name).Exit);
            Assert.Equal(0, Cli("connection", "init", name, "--upgrade", "--apply").Exit);          // safe to repeat
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            try { await engine.ExecAsync(name == "postgres" ? $"DROP SCHEMA IF EXISTS {Q(schema)} CASCADE" : $"DECLARE @s nvarchar(max) = N''; SELECT @s += N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.views WHERE schema_id = SCHEMA_ID('{schema}'); EXEC(@s); SELECT @s = N''; SELECT @s += N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.tables WHERE schema_id = SCHEMA_ID('{schema}'); EXEC(@s); DROP SCHEMA [{schema}];"); } catch (Exception) { }
        }
    }
}
