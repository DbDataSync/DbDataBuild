using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DuckDB.NET.Data;
using Json.Schema;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The online part of `project update` and `project model update`: the metadata of every mapped model (and of the tables copies read) is compared with its table and written as `project import` would, on a DuckDB file
/// (no docker). Both commands run the same code; a mapped model is never ignored; a connection that cannot be read is said and skipped.
/// </summary>
public class ProjectUpdateMappedModelsTests : IDisposable
{
    private static readonly JsonSchema Schema = SchemaConformanceTests.LoadSchema("output");
    private readonly string dir = NewProjectDir();
    private readonly string files = Path.Combine(Path.GetTempPath(), "ddb-update-files-" + Guid.NewGuid().ToString("N")[..10]);

    public ProjectUpdateMappedModelsTests() => Directory.CreateDirectory(files);
    public void Dispose() { foreach (var d in new[] { dir, files }) try { Directory.Delete(d, true); } catch (IOException) { } }

    private string Db => Path.Combine(files, "analytics.duckdb").Replace('\\', '/');
    private static void Exec(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private void MakeDb(string extraColumn = ", amount DECIMAL(18,2)")
    {
        using var c = new DuckDBConnection($"Data Source={Db}");
        c.Open();
        Exec(c, "CREATE SCHEMA src");
        Exec(c, $"CREATE TABLE src.events (id BIGINT NOT NULL PRIMARY KEY, label VARCHAR{extraColumn})");
        Exec(c, "CREATE INDEX ix_events_label ON src.events (label)");
    }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private const string StaleEvents = "name: src.events\nkind: mapped\nconnections=: [analytics]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: label, type: VARCHAR}\n";

    private void Project(string events = StaleEvents)
    {
        Write("dbdatabuild.yml", "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\nconnections:\n  analytics: { engine: readonly duckdb }\n  lost: { engine: readonly duckdb }\n");
        Write("models/src/events.yml", events);
        Write("models/dst/events.yml", "name: dst.events\nkind: {copy: {from: src.events}}\n");
        Write("models/src/elsewhere.yml", "name: src.elsewhere\nkind: mapped\nconnections=: [lost]\ncolumns:\n  - {name: id, type: BIGINT}\n");
        Write("models/src/computed.yml", "name: src.computed\nkind: {native: {access: select, query: \"SELECT 1 AS n\"}}\nconnections=: [analytics]\ncolumns:\n  - {name: n, type: INTEGER}\n");
    }

    private Func<string, string?> Env => v => v == "DBDATABUILD_ANALYTICS_READ" ? Db : null;      // `lost` has no read login

    private (int Exit, JsonNode Doc, string Text) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--project", dir, "--format", "json"], o, e, environment: Env);
        var doc = JsonNode.Parse(o.ToString())!;
        Assert.True(Schema.Evaluate(JsonSerializer.SerializeToNode(doc), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, $"does not satisfy output.schema.json:\n{o}");
        return (exit, doc, string.Join('\n', doc["messages"]!.AsArray().Select(m => (string)m!)) + "\n" + string.Join('\n', doc["errors"]!.AsArray().Select(m => (string)m!)));
    }

    private static JsonNode Source(JsonNode doc, string name) => doc["data"]!["mapped_models"]!.AsArray().Single(s => (string)s!["name"]! == name)!;

    [Fact]
    public void Update_brings_the_table_into_the_mapped_model_columns_keys_and_indexes_and_a_second_run_is_quiet()
    {
        MakeDb(); Project();
        var (exit, doc, text) = Cli("project", "update");
        Assert.Equal(0, exit);
        Assert.Equal("changed", (string)Source(doc, "src.events")["status"]!);
        Assert.Equal("analytics", (string)Source(doc, "src.events")["connection"]!);
        Assert.Contains("amount", text);
        var written = File.ReadAllText(Path.Combine(dir, "models", "src", "events.yml"));
        Assert.Contains("amount", written);
        Assert.Contains("ix_events_label", written);                                              // the index import writes
        Assert.Contains("models/src/events.yml", doc["data"]!["written"]!.AsArray().Select(x => (string)x!));
        var again = Cli("project", "update");
        Assert.Equal(0, again.Exit);
        Assert.Equal("unchanged", (string)Source(again.Doc, "src.events")["status"]!);
        Assert.Empty(again.Doc["data"]!["written"]!.AsArray());
    }

    [Fact]
    public void A_mapped_model_is_never_ignored_one_that_cannot_be_read_is_said_and_the_rest_still_updates()
    {
        MakeDb(); Project();
        var (exit, doc, text) = Cli("project", "update");
        Assert.Equal(0, exit);                                                                      // a skipped connection is a note, not a failure
        var skipped = Source(doc, "src.elsewhere");
        Assert.Equal("skipped", (string)skipped["status"]!);
        Assert.Contains("DBDATABUILD_LOST_READ", (string)skipped["reason"]!);
        Assert.Contains("DBDATABUILD_LOST_READ", text);
        Assert.Equal("skipped", (string)Source(doc, "src.computed")["status"]!);                    // a native model has no table to read, and says so
        Assert.Contains(doc["data"]!["connections"]!.AsArray(), c => (string)c!["connection"]! == "lost" && (string)c["status"]! == "skipped");
        Assert.Contains("amount", File.ReadAllText(Path.Combine(dir, "models", "src", "events.yml")));
    }

    [Fact]
    public void Check_shows_the_difference_per_model_writes_nothing_and_exits_one()
    {
        MakeDb(); Project();
        var before = Snapshot(dir);
        var (exit, doc, text) = Cli("project", "update", "--check");
        Assert.Equal(1, exit);
        Assert.Equal(1, (int)doc["data"]!["differences"]!);
        Assert.Contains("+++ b/models/src/events.yml", text);
        Assert.Contains(doc["diagnostics"]!.AsArray(), d => (string)d!["code"]! == "DDB-461");
        Assert.Equal(before, Snapshot(dir));
        Cli("project", "update");
        Assert.Equal(0, Cli("project", "update", "--check").Exit);
    }

    [Fact]
    public void Offline_reads_no_connection()
    {
        MakeDb(); Project();
        var before = Snapshot(dir);
        var (exit, doc, text) = Cli("project", "update", "--offline");
        Assert.Equal(0, exit);
        Assert.False((bool)doc["data"]!["online"]!);
        Assert.Null(doc["data"]!["mapped_models"]);
        Assert.Contains("Offline", text);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void A_selector_names_the_models_and_a_copy_brings_the_table_it_reads()
    {
        MakeDb(); Project();
        var (exit, doc, _) = Cli("project", "update", "dst.events");
        Assert.Equal(0, exit);
        Assert.Equal("changed", (string)Source(doc, "src.events")["status"]!);                      // the copy's origin
        Assert.DoesNotContain(doc["data"]!["mapped_models"]!.AsArray(), s => (string)s!["name"]! == "src.elsewhere");
        var (bad, _, _) = Cli("project", "update", "nothing.like.this");
        Assert.Equal(2, bad);
    }

    [Fact]
    public void Model_update_has_the_same_online_part_and_options()
    {
        MakeDb(); Project();
        var before = Snapshot(dir);
        var (exit, doc, _) = Cli("project", "model", "update", "--check");
        Assert.Equal(1, exit);
        Assert.Contains(doc["diagnostics"]!.AsArray(), d => (string)d!["code"]! == "DDB-461");
        Assert.Equal("changed", (string)Source(doc, "src.events")["status"]!);
        Assert.Equal(before, Snapshot(dir));
        Assert.Equal(0, Cli("project", "model", "update", "--check", "--offline").Exit);           // offline: the definitions are in sync, nothing else is looked at
        var (writeExit, written, _) = Cli("project", "model", "update", "--write");
        Assert.Equal(0, writeExit);
        Assert.Contains("amount", File.ReadAllText(Path.Combine(dir, "models", "src", "events.yml")));
        Assert.Contains("models/src/events.yml", written["data"]!["written"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(0, Cli("project", "model", "update", "--check").Exit);
    }

    [Fact]
    public void Model_update_paths_limit_the_mapped_models_too()
    {
        MakeDb(); Project();
        var (exit, doc, _) = Cli("project", "model", "update", "--check", "models/src/events.yml");
        Assert.Equal(1, exit);
        Assert.DoesNotContain(doc["data"]!["mapped_models"]!.AsArray(), s => (string)s!["name"]! == "src.elsewhere");
    }
}
