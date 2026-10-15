using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using Json.Schema;
using static DbDataBuild.Tests.Unit.PolyglotBindingTests;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`--format json`: one document on standard output, the same exit codes as text mode, structured diagnostics, and a data payload per command.</summary>
public class JsonOutputTests
{
    private static readonly JsonSchema Schema = SchemaConformanceTests.LoadSchema("output");

    private const string Orders = "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctYaml = "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\ngrain: [order_id]\nconnections: [sqlserver]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\nindexes:\n  - {name: ix_amount, columns: [amount]}\n";

    private static string Project()
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), Orders);
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), FctYaml);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id, o.amount FROM staging.orders o\n");
        return dir;
    }

    private static (int Exit, JsonNode Doc, string Raw, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--format", "json"], o, e, environment: _ => null);
        var raw = o.ToString();
        var doc = JsonNode.Parse(raw)!;                                    // exactly one JSON document, nothing else on standard output
        var result = Schema.Evaluate(JsonSerializer.SerializeToNode(doc), new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, $"{args[0]} output does not satisfy output.schema.json:\n{raw}");
        Assert.Equal(exit, doc["exit_code"]!.GetValue<int>());
        Assert.Equal(exit == 0, doc["ok"]!.GetValue<bool>());
        Assert.Equal(CommandSpecs.All.Select(c => c.Name).Where(n => (string.Join(' ', args) + " ").StartsWith(n + " ")).OrderByDescending(n => n.Length).FirstOrDefault() ?? args[0], doc["command"]!.GetValue<string>());
        return (exit, doc, raw, e.ToString());
    }

    [Fact]
    public void Agent_kit_with_mcp_reports_the_config_file_and_satisfies_the_schema()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-kitjson-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var (_, doc, _, _) = Run("project", "agent-kit", "--project", dir, "--mcp");
            Assert.False(doc["data"]!["mcp"]!["up_to_date"]!.GetValue<bool>());
            var (_, written, _, _) = Run("project", "agent-kit", "--project", dir, "--mcp", "--write");
            Assert.True(written["data"]!["mcp"]!["wrote"]!.GetValue<bool>());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_command_says_what_comes_next_as_commands_in_the_text_and_in_the_document()
    {
        var dir = Project();
        var (exit, doc, _, _) = Run("project", "compile", "--project", dir);
        Assert.Equal(0, exit);
        var next = doc["next"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("dbdatabuild project tests run", next);
        Assert.Contains("Next:", string.Join("\n", doc["messages"]!.AsArray().Select(m => m!.GetValue<string>())));
        // a check changes nothing and has nothing to suggest
        Assert.Null(Run("project", "compile", "--check", "--project", dir).Doc["next"]);
    }

    [Fact]
    public void Every_header_says_what_the_command_reads_and_writes_and_its_lane()
    {
        var dir = Project();
        var o = new StringWriter();
        CliApp.Run(["project", "show", "loads", "--project", dir], o, new StringWriter());
        Assert.Contains("reads/writes: P→", o.ToString().Split('\n')[0]);
        var d = new StringWriter();
        CliApp.Run(["connection", "deploy", "--write-plan", "--project", dir], d, new StringWriter(), environment: _ => null);
        Assert.Contains("reads/writes: C→P  |  lane: deploy", d.ToString().Split('\n')[0]);
    }

    [Fact]
    public void Validate_reports_counts_and_the_full_metadata_of_every_model()
    {
        var dir = Project();
        var (exit, doc, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(0, exit);
        Assert.Equal("", err);                                              // diagnostics are in the document, not on standard error
        var data = doc["data"]!;
        Assert.Equal(2, data["counts"]!["models"]!.GetValue<int>());                                                           // the built model and the mapped one it reads
        Assert.Equal((1, 1, 0), (data["counts"]!["by_kind"]!["incremental_by_unique_key"]!.GetValue<int>(), data["counts"]!["by_kind"]!["mapped"]!.GetValue<int>(), data["counts"]!["by_kind"]!["view"]!.GetValue<int>()));
        Assert.Null(data["sources"]);                                                                                           // one array, whatever the kind
        Assert.Equal(["marts.fct_orders", "staging.orders"], data["models"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()));
        var model = data["models"]![0]!;
        Assert.Equal("dbdatabuild.model/3", model["schema"]!.GetValue<string>());
        Assert.Equal("marts.fct_orders", model["name"]!.GetValue<string>());
        Assert.Equal("incremental_by_unique_key", model["kind"]!.GetValue<string>());                                        // the kind is a plain string, its settings are beside it
        Assert.Equal(["order_id"], model["kind_settings"]!["unique_key"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Matches("^[0-9a-f]{64}$", model["definition_hash"]!.GetValue<string>());
        var amount = model["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "amount")!;
        Assert.Equal("DECIMAL(14, 2)", amount["logical_type"]!.GetValue<string>());
        Assert.Equal("decimal(14, 2)", amount["native"]!["sqlserver"]!["type"]!.GetValue<string>());
        Assert.Null(amount["native"]!["postgres"]);                         // only the model's own targets are described
        Assert.Equal("staging.orders", model["upstream"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("mapped", model["upstream"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("ix_amount", model["indexes"]![0]!["name"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", model["expected_shape_hash"]!["sqlserver"]!.GetValue<string>());
        Assert.Equal("delete_insert_by_key", model["loads"]![0]!["strategy"]!.GetValue<string>());
        Assert.NotNull(data["project"]!["matrix_version"]);
    }

    [Fact]
    public void Findings_are_structured_diagnostics_with_the_same_exit_code_as_text_mode()
    {
        var dir = Project();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id, [1, 2] AS l FROM staging.orders o\n");
        var (exit, doc, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(1, exit);
        Assert.Equal("", err);
        var d = doc["diagnostics"]!.AsArray().First(x => x!["severity"]!.GetValue<string>() == "error")!;
        Assert.Matches("^DDB-\\d{3}$", d["code"]!.GetValue<string>());
        Assert.Equal("models/marts/fct_orders.sql", d["location"]!["file"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(d["fix"]!.GetValue<string>()));
        Assert.Contains(doc["messages"]!.AsArray(), m => m!.GetValue<string>().StartsWith("FAILED"));
    }

    [Fact]
    public void Matrix_explain_loads_render_init_and_metadata_each_carry_their_data()
    {
        var dir = Project();
        var matrix = Run("help", "matrix");
        Assert.True(matrix.Doc["data"]!["constructs"]!.AsArray().Count > 10);
        Assert.NotNull(matrix.Doc["data"]!["constructs"]![0]!["engines"]!["sqlserver"]!["status"]);

        var explain = Run("help", "code", "DDB-430");
        Assert.Equal("DDB-430", explain.Doc["data"]!["code"]!["code"]!.GetValue<string>());
        Assert.Equal(2, Run("help", "code", "DDB-999").Exit);                    // usage errors are JSON too

        var loads = Run("project", "show", "loads", "--project", dir);
        Assert.Equal("delete_insert_by_key", loads.Doc["data"]!["operations"]![0]!["strategy"]!.GetValue<string>());

        var render = Run("project", "compile", "--project", dir, "--content");
        Assert.Equal(0, render.Exit);
        Assert.Equal(["rendered/lowered/marts.fct_orders/lowered.sql", "rendered/sqlserver/marts.fct_orders/load.default.sql", "rendered/sqlserver/marts.fct_orders/manifest.yml", "rendered/sqlserver/loads.yml", "rendered/sqlserver/refresh/default.yml"], render.Doc["data"]!["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        var write = Run("project", "compile", "--project", dir);
        Assert.Equal(0, write.Exit);
        Assert.NotEmpty(write.Doc["data"]!["wrote"]!.AsArray());
        var check = Run("project", "compile", "--project", dir, "--check");
        Assert.Empty(check.Doc["data"]!["out_of_date"]!.AsArray());
        File.AppendAllText(Path.Combine(dir, "rendered/sqlserver/marts.fct_orders/load.default.sql"), "-- edited\n");
        var stale = Run("project", "compile", "--project", dir, "--check");
        Assert.Equal(1, stale.Exit);
        Assert.Contains("DDB-424", stale.Doc["diagnostics"]!.AsArray().Select(x => x!["code"]!.GetValue<string>()));

        var init = Run("connection", "init", "--project", dir);
        Assert.False(init.Doc["data"]!["applied"]!.GetValue<bool>());
        Assert.Equal(16, init.Doc["data"]!["statements"]!.AsArray().Count);        // schema, twelve tables, two views, the version row
        Assert.Contains("CREATE TABLE", init.Doc["data"]!["statements"]![1]!["text"]!.GetValue<string>());

        var meta = Run("project", "show", "metadata", "--project", dir);
        Assert.Equal("marts.fct_orders", meta.Doc["data"]!["models"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_logins_and_usage_errors_still_produce_one_valid_document()
    {
        var dir = Project();
        var plan = Run("connection", "deploy", "--write-plan", "--project", dir);
        Assert.Equal(1, plan.Exit);
        Assert.Equal("DDB-501", plan.Doc["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("password", plan.Raw, StringComparison.OrdinalIgnoreCase);
        var ack = Run("connection", "deploy", "--ack", "banana:x", "--reason", "r", "--project", dir);
        Assert.Equal(2, ack.Exit);
        Assert.Contains(ack.Doc["errors"]!.AsArray(), m => m!.GetValue<string>().Contains("Unknown acknowledgement"));
    }

    [Fact]
    public void The_default_format_is_unchanged_text()
    {
        var o = new StringWriter();
        Assert.Equal(0, CliApp.Run(["help", "matrix"], o, new StringWriter()));
        Assert.StartsWith("dbdatabuild help matrix", o.ToString());
        var bad = new StringWriter();
        Assert.NotEqual(0, CliApp.Run(["help", "matrix", "--format", "yaml"], new StringWriter(), bad));
    }

    [Fact]
    public void An_internal_failure_in_json_mode_is_a_document_not_a_stack_trace()
    {
        var o = new StringWriter();
        var exit = CliApp.Guarded(["connection", "status", "--format", "json"], new StringWriter(), () => throw new InvalidOperationException("boom: secret-row-value"), o);
        Assert.Equal(CliApp.ExitInternal, exit);
        var doc = JsonNode.Parse(o.ToString())!;
        Assert.Equal("DDB-900", doc["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("secret-row-value", o.ToString());
        Assert.DoesNotContain("   at ", o.ToString());
        Assert.True(Schema.Evaluate(JsonSerializer.SerializeToNode(doc)).IsValid);
    }
    [Fact]
    public void Every_command_has_a_data_schema_and_the_schema_rejects_drift()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "schemas", "output.schema.json"));
        var declared = JsonNode.Parse(text)!["allOf"]!.AsArray().Select(x => (string)x!["if"]!["properties"]!["command"]!["const"]!).Order().ToList();
        Assert.Equal(CommandSpecs.All.Where(c => c.Name is not ("ui terminal" or "ui mcp" or "ui web")).Select(c => c.Name).Order(), declared);       // a new command without a data schema fails here (`tui` is interactive and has no JSON form)

        var dir = Project();
        var (_, doc, _, _) = Run("project", "show", "loads", "--project", dir);
        bool Valid(JsonNode n) => Schema.Evaluate(JsonSerializer.SerializeToNode(n), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
        Assert.True(Valid(doc));
        var renamed = JsonNode.Parse(doc.ToJsonString())!;
        renamed["data"]!["operations"]![0]!["strategy"] = 5;                            // a retyped key
        Assert.False(Valid(renamed));
        var extra = JsonNode.Parse(doc.ToJsonString())!;
        extra["data"]!["operations"]![0]!["surprise"] = "x";                            // an unannounced key
        Assert.False(Valid(extra));
        var unknownKey = JsonNode.Parse(doc.ToJsonString())!;
        unknownKey["data"]!["rows"] = new JsonArray();
        Assert.False(Valid(unknownKey));
    }

    [Fact]
    public void Model_and_project_documents_satisfy_the_standalone_metadata_schema_and_carry_the_index_advice()
    {
        var metadata = SchemaConformanceTests.LoadSchema("metadata");
        var dir = Project();
        var (_, doc, _, _) = Run("project", "show", "metadata", "--project", dir);
        bool Valid(JsonNode? n) => metadata.Evaluate(JsonSerializer.SerializeToNode(n), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
        Assert.True(Valid(doc["data"]!["project"]));
        var model = doc["data"]!["models"]![0]!;
        Assert.True(Valid(model));
        var advice = model["index_advice"]!.AsArray().Single()!;
        Assert.Equal(("DDB-223", "warning", "merge_key", "ux_fct_orders_order_id"), ((string)advice["code"]!, (string)advice["severity"]!, (string)advice["reason"]!, (string)advice["suggested"]!));
        Assert.False((bool)advice["silenced"]!);
        var broken = JsonNode.Parse(model.ToJsonString())!;
        broken["schema"] = "dbdatabuild.model/1";
        Assert.False(Valid(broken));
    }

    [Fact]
    public void Define_reports_what_it_checked_wrote_and_still_needs_in_its_data()
    {
        var dir = Project();
        var (exit, doc, _, _) = Run("project", "model", "update", "--check", "--project", dir);
        Assert.Equal("check", (string?)doc["data"]!["mode"]);
        Assert.Equal(1, (int)doc["data"]!["definitions"]!);
        Assert.Equal(exit == 0 ? 0 : 1, (int)doc["data"]!["differences"]! > 0 ? 1 : 0);

        File.Delete(Path.Combine(dir, "models/marts/fct_orders.yml"));
        var (_, asked, _, _) = Run("project", "model", "update", "--write", "--project", dir, "--answers", WriteAnswers(dir, ""));
        Assert.True(asked["data"]!["models"] != null, asked.ToJsonString());
        var model = asked["data"]!["models"]![0]!;
        Assert.Equal("incomplete", (string?)model["status"]);
        Assert.Contains(model["open_questions"]!.AsArray(), q => ((string)q!["id"]!).EndsWith("-kind"));
        Assert.Empty(asked["data"]!["written"]!.AsArray());
    }

    private static string WriteAnswers(string dir, string body)
    {
        var path = Path.Combine(dir, "answers.yml");
        File.WriteAllText(path, "answers: []\n" + body);
        return path;
    }
    [Fact]
    public void Sample_reports_its_tables_rows_and_errors_as_data()
    {
        var dir = Project();
        var (exit, doc, _, _) = Run("project", "sample", "--project", dir, "--rows", "12", "--limit", "3", "--declared");
        Assert.Equal(0, exit);
        var tables = doc["data"]!["tables"]!.AsArray();
        var model = tables.Single(t => (string?)t!["kind"] == "incremental_by_unique_key")!;
        Assert.Equal("marts.fct_orders", (string?)model["name"]);
        Assert.Equal(12, (int)model["row_count"]!);
        Assert.Equal(3, model["rows"]!.AsArray().Count);
        Assert.Equal(2, model["rows"]![0]!.AsArray().Count);
        Assert.Equal(12, (int)tables.Single(t => (string?)t!["kind"] == "mapped")!["row_count"]!);
    }
    [Fact]
    public void Agent_kit_lists_and_installs_its_files_as_data()
    {
        var dir = Project();
        var (_, listed, _, _) = Run("project", "agent-kit", "--project", dir);
        Assert.Equal(".claude/skills/dbdatabuild", (string?)listed["data"]!["directory"]);
        Assert.Contains(listed["data"]!["files"]!.AsArray(), f => ((string)f!["path"]!).EndsWith("/SKILL.md"));
        Assert.Empty(listed["data"]!["wrote"]!.AsArray());
        var (_, written, _, _) = Run("project", "agent-kit", "--project", dir, "--write");
        Assert.Equal(written["data"]!["files"]!.AsArray().Count, written["data"]!["wrote"]!.AsArray().Count);
    }
}
