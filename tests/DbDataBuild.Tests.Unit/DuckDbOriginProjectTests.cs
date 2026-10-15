using DbDataBuild.Apply;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Targets;
using DuckDB.NET.Data;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>A DuckDB file as the origin of a project's copies, mapped models, native queries and `source` resolvers, through the commands and the planner (no docker: the destination is only planned).</summary>
public class DuckDbOriginProjectTests : IDisposable
{
    private readonly string dir = NewProjectDir();
    private readonly string files = Path.Combine(Path.GetTempPath(), "ddb-duckdb-files-" + Guid.NewGuid().ToString("N")[..10]);

    public DuckDbOriginProjectTests() => Directory.CreateDirectory(files);

    public void Dispose() { foreach (var d in new[] { dir, files }) try { Directory.Delete(d, true); } catch (IOException) { } }

    private string Db => Path.Combine(files, "analytics.duckdb").Replace('\\', '/');

    private static void Exec(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private void MakeDb(Action<DuckDBConnection>? more = null)
    {
        using var c = new DuckDBConnection($"Data Source={Db}");
        c.Open();
        Exec(c, "CREATE SCHEMA src");
        Exec(c, "CREATE TABLE src.events (id BIGINT NOT NULL PRIMARY KEY, day DATE NOT NULL, label VARCHAR, amount DECIMAL(18,2))");
        Exec(c, "INSERT INTO src.events SELECT i, DATE '2026-02-27' + CAST(i AS INTEGER), 'e' || i, i * 1.5 FROM range(0, 8) t(i)");
        more?.Invoke(c);
    }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private const string EventsModel = "name: src.events\nkind: mapped\nconnections=: [analytics]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: day, type: DATE, nullable: false}\n  - {name: label, type: VARCHAR(20)}\n  - {name: amount, type: \"DECIMAL(18, 2)\"}\n";

    private Func<string, string?> Env => v => v == "DBDATABUILD_ANALYTICS_READ" ? Db : v == "DBDATABUILD_LAKE_READ" ? ":memory:" : null;

    private (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
        return (exit, o.ToString(), e.ToString());
    }

    private void Project(string copyBody = "kind: {copy: {from: src.events}}\n")
    {
        Write("dbdatabuild.yml", "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\nconnections:\n  analytics: { engine: readonly duckdb }\n  lake: { engine: readonly duckdb }\n");
        Write("models/src/events.yml", EventsModel);
        Write("models/dst/events.yml", "name: dst.events\n" + copyBody);
    }

    // ---- the commands ----

    [Fact]
    public void Inspect_connects_to_the_file_says_it_is_read_only_and_asks_for_no_write_login()
    {
        MakeDb(); Project();
        var r = Cli("connection", "inspect", "analytics");
        Assert.Contains("engine readonly duckdb", r.Out);
        Assert.Contains("read login    DBDATABUILD_ANALYTICS_READ: default user connects; DuckDB v", r.Out);
        Assert.Contains("write login   none: the connection is readonly", r.Out);
        Assert.DoesNotContain(Db, r.Out + r.Err);                                                  // the path is the login's value and is not printed
        var gone = Cli("connection", "inspect", "lake");                                          // `:memory:` answers too
        Assert.Contains("lake", gone.Out);
        Assert.Contains("connects", gone.Out);
    }

    [Fact]
    public void Project_import_exports_the_tables_of_the_file_as_mapped_models_and_sees_a_change()
    {
        MakeDb(); Project();
        var preview = Cli("project", "import", "analytics", "src.*", "--format", "json");
        Assert.Equal(0, preview.Exit);
        Assert.Contains("\"name\": \"src.events\"", preview.Out);
        Assert.Equal(0, Cli("project", "import", "analytics", "src.*", "--write").Exit);
        var yml = File.ReadAllText(Path.Combine(dir, "models", "src", "events.yml"));
        Assert.Contains("grain:", yml);
        Assert.Contains("name: id", yml);
        Assert.Contains("BIGINT", yml);
        Assert.Contains("DECIMAL(18, 2)", yml);
        Assert.Equal(0, Cli("project", "import", "analytics", "--check").Exit);                    // in sync
        using (var c = new DuckDBConnection($"Data Source={Db}")) { c.Open(); Exec(c, "ALTER TABLE src.events ADD COLUMN note VARCHAR"); }
        var changed = Cli("project", "import", "analytics", "--check");
        Assert.NotEqual(0, changed.Exit);
        Assert.Contains("note", changed.Out);
    }

    [Fact]
    public void Status_takes_a_duckdb_origin_and_names_it_by_what_it_is()
    {
        MakeDb(); Project();
        var status = Cli("connection", "status", "analytics");
        Assert.True(status.Exit is 0 or 1, status.Out + status.Err);
        Assert.DoesNotContain("DDB-455", status.Err);
        Assert.DoesNotContain("Exception", status.Out + status.Err);
        Assert.Contains("login: DuckDB file, read-only (DBDATABUILD_ANALYTICS_READ)", status.Out);
        Assert.Contains("connections: analytics (readonly duckdb)", status.Out);
    }

    [Fact]
    public void Compare_is_for_the_engines_the_tool_builds_on()
    {
        MakeDb(); Project();
        var r = Cli("connection", "compare", "analytics", "src.events", "--against", "src.events", "--key", "id");
        Assert.NotEqual(0, r.Exit);
        Assert.Contains("SQL Server and PostgreSQL", r.Err);
    }

    [Fact]
    public void A_model_cannot_be_built_on_a_duckdb_file_and_a_deploy_to_it_is_refused()
    {
        MakeDb(); Project();
        Write("models/marts/m.yml", "name: marts.m\nkind: view\nconnections: [analytics]\ncolumns:\n  - {name: n, type: INTEGER}\n");
        Write("models/marts/m.sql", "SELECT 1 AS n\n");
        Assert.Contains(ProjectValidator.Validate(dir).Diagnostics, d => d.Code == "DDB-455");
        Assert.Contains("DDB-455", Cli("connection", "deploy", "analytics", "--write-plan").Err);
    }

    // ---- planning the copy ----

    [Fact]
    public void A_copy_from_a_duckdb_file_reads_in_duckdbs_quoting_with_the_watermark_as_a_positional_value()
    {
        MakeDb(); Project("kind: {copy: {from: src.events, unique_key: [id], watermark: {column: day, lookback: 3 days}}}\n");
        Assert.False(ProjectValidator.Validate(dir).HasErrors, string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Select(DiagnosticFormatter.Format)));
        var ctx = ProjectContext.Load(dir);
        var copy = ctx.Project.BuiltModels.Single(s => s.Definition.Name == "dst.events").Definition;
        var origin = Assert.Single(ctx.OriginsOf(copy, "sqlserver"));
        Assert.Equal(("analytics", "duckdb"), (origin.Connection, origin.Engine));
        var ddl = TargetRegistry.Get("sqlserver").CreateDdl(ctx.Config);
        var (_, spec) = Planner.CopyTransfer(ctx.Config, copy, ddl, origin with { WatermarkValue = "2026-03-01" });
        Assert.Equal("SELECT \"id\", \"day\", \"label\", \"amount\" FROM \"src\".\"events\" WHERE \"day\" >= ?", spec.ReadText);
        Assert.Equal(["watermark"], spec.ParameterOrder);

        // the refresh keeps both reads and the order in the load plan
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var loads = File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "loads.yml"));
        Assert.Contains("read_incremental: \"SELECT \\\"id\\\", \\\"day\\\", \\\"label\\\", \\\"amount\\\" FROM \\\"src\\\".\\\"events\\\" WHERE \\\"day\\\" >= ?\"", loads);
        Assert.Contains("parameter_order:", loads);
        Assert.Contains("engine: \"duckdb\"", loads);
    }

    [Fact]
    public async Task The_structure_check_holds_a_mapped_duckdb_origin_to_its_declaration()
    {
        MakeDb(); Project();
        var ctx = ProjectContext.Load(dir);
        var copy = ctx.Project.BuiltModels.Single(s => s.Definition.Name == "dst.events").Definition;
        var origin = Assert.Single(ctx.OriginsOf(copy, "sqlserver"));
        var ok = await CopyOriginCheck.RunOneAsync(ctx, copy, origin, Env);
        Assert.DoesNotContain(ok.Findings, f => f.Severity == Severity.Error);
        using (var c = new DuckDBConnection($"Data Source={Db}")) { c.Open(); Exec(c, "ALTER TABLE src.events DROP COLUMN label"); }
        var drifted = await CopyOriginCheck.RunOneAsync(ctx, copy, origin, Env);
        var finding = Assert.Single(drifted.Findings, f => f.Code == "DDB-230");
        Assert.Contains("column `label` is gone", finding.Found);
    }

    [Fact]
    public async Task A_native_query_over_a_parquet_file_in_a_memory_origin_is_planned_checked_and_read()
    {
        MakeDb();
        var parquet = Path.Combine(files, "orders.parquet").Replace('\\', '/');
        await using (var w = new DuckDBConnection("Data Source=:memory:")) { w.Open(); Exec(w, $"COPY (SELECT i AS id, 'o' || i AS label FROM range(1, 7) t(i)) TO '{parquet}' (FORMAT PARQUET)"); }
        Write("dbdatabuild.yml", $"defaults: {{connections: [sqlserver]}}\nconnections:\n  lake: {{ engine: readonly duckdb }}\nparameters:\n  file: \"{parquet}\"\n  min_id: {{ type: BIGINT, value: \"3\" }}\n");
        Write("models/src/orders.yml", "name: src.orders\nkind:\n  native:\n    query: SELECT id, label FROM read_parquet(${project.file}) WHERE id >= ${project.min_id}\nconnections=: [lake]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: label, type: VARCHAR}\n");
        Write("models/dst/orders.yml", "name: dst.orders\nkind: {copy: {from: src.orders}}\n");
        Assert.False(ProjectValidator.Validate(dir).HasErrors, string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Select(DiagnosticFormatter.Format)));

        var ctx = ProjectContext.Load(dir);
        var copy = ctx.Project.BuiltModels.Single(s => s.Definition.Name == "dst.orders").Definition;
        var origin = Assert.Single(ctx.OriginsOf(copy, "sqlserver"));
        var ddl = TargetRegistry.Get("sqlserver").CreateDdl(ctx.Config);
        var (_, spec) = Planner.CopyTransfer(ctx.Config, copy, ddl, origin);
        Assert.Contains("read_parquet(?)", spec.ReadText);
        Assert.Contains(">= ?", spec.ReadText);
        Assert.DoesNotContain(parquet, spec.ReadText);                                             // a value is bound, never written into the text
        Assert.Equal(["p_native_src_orders__project_file", "p_native_src_orders__project_min_id"], spec.ParameterOrder);

        // the origin answers for the shape the native model declares (no rows are read to check)
        var (login, _) = LoginSettings.FromEnvironment(ctx.Config.Connections["lake"], Login.Read, Env);
        var native = ctx.Project.NativeModels.Single();
        Assert.Empty((await NativeShapeCheck.DifferencesAsync(ctx.Config, native, "lake", login!))!);
        var check = await CopyOriginCheck.RunOneAsync(ctx, copy, origin, Env);
        Assert.DoesNotContain(check.Findings, f => f.Severity != Severity.Note);

        // and the read the plan carries returns the rows, parameters in the order the plan recorded
        await using var read = await ReadSession.OpenAsync(login!);
        var byName = spec.Parameters!.ToDictionary(p => p.Name);
        await using var stream = await read.OpenStreamAsync(spec.ReadText, spec.ParameterOrder!.Select(n => ApplyEngine.ToGate(byName[n])).ToList());
        var ids = new List<object?>();
        await foreach (var row in stream.ReadAsync()) ids.Add(row[0]);
        Assert.Equal([3L, 4L, 5L, 6L], ids);
    }

    [Fact]
    public void A_source_resolver_of_a_copy_from_a_duckdb_file_is_rendered_in_duckdbs_own_text()
    {
        MakeDb();
        Project("kind: {copy: {from: src.events}}\nloads:\n  window:\n    strategy: delete_insert_by_range\n    column: day\n    params: {start: DATE, end: DATE}\n    inputs:\n" +
                "      start: {resolver: {source: \"SELECT min(day) FROM src.events WHERE day >= DATE '2026-03-01'\"}}\n      end: {resolver: {expression: \"DATE '2026-03-04'\"}}\n");
        var compiled = RenderFiles("--write", "--project", dir);
        Assert.True(compiled.Exit == 0, compiled.Out + compiled.Err);
        var loads = File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "loads.yml"));
        Assert.Contains("engine: \"duckdb\"", loads);
        Assert.Contains("min(day)", loads, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-- connection: analytics", loads);
        Assert.DoesNotContain("TOP ", loads);                                                      // not T-SQL: it runs on DuckDB as lowered
    }
}
