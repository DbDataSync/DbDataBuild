using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Targets.DuckDb;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`test`: metadata rules (DuckDB SELECTs over the metadata views, DESIGN.md 9.8).</summary>
public class ProjectTestsTests
{
    private const string Orders = "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: note, type: VARCHAR}\nindexes:\n  - {name: IX_orders_amount, columns: [amount], include: [note]}\nforeign_keys:\n  - {name: FK_orders_self, columns: [order_id], references: {table: staging.orders, columns: [order_id]}}\n";
    private const string Fct = "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\ngrain: [order_id]\nconnections: [sqlserver]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: note2, type: VARCHAR}\nindexes:\n  - {name: ux_fct_orders_order_id, columns: [order_id], unique: true}\nhooks:\n  - {name: grant, event: post_create, script: hooks/grant.sql}\n";
    private const string Config = "defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n";

    private static string Project(params (string Path, string Text)[] rules)
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        Directory.CreateDirectory(Path.Combine(dir, "hooks"));
        File.WriteAllText(Path.Combine(dir, "hooks/grant.sql"), "GRANT SELECT ON marts.fct_orders TO reader;\n");
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), Config);
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), Orders);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), Fct);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id, o.amount, o.note || '!' AS note2 FROM staging.orders o\n");
        foreach (var (path, text) in rules)
        {
            var full = Path.Combine(dir, "tests/metadata", path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return dir;
    }

    private static (int Exit, JsonNode Doc, string Err) Test(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "tests", "run", "--project", dir, .. args, "--format", "json"], o, e, environment: _ => null);
        var doc = JsonNode.Parse(o.ToString())!;
        var schema = SchemaConformanceTests.LoadSchema("output");
        var result = schema.Evaluate(System.Text.Json.JsonSerializer.SerializeToNode(doc), new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        Assert.True(result.IsValid, string.Join("\n", result.Details.Where(x => x.Errors != null).SelectMany(x => x.Errors!.Select(kv => $"{x.InstanceLocation}: {kv.Key} {kv.Value}"))) + "\n" + o);
        Assert.Equal(exit, (int)doc["exit_code"]!);
        return (exit, doc, e.ToString());
    }

    private static JsonNode One(JsonNode doc, string name) => doc["data"]!["tests"]!.AsArray().Single(t => (string?)t!["name"] == name)!;

    // ---- the file format ----

    private static TestRule? Load(string text, List<Diagnostic> diags) => TestRuleLoader.Load(text, "tests/metadata/x.sql", diags);

    [Fact]
    public void A_rule_has_defaults_and_reads_its_settings_from_the_comments_at_the_top()
    {
        var diags = new List<Diagnostic>();
        var plain = Load("SELECT 1 WHERE false\n", diags)!;
        Assert.Empty(diags);
        Assert.Equal(("x", "error", null), (plain.Name, plain.Severity, plain.Description));
        Assert.Empty(plain.Tags);

        var rule = Load("-- Some prose about the rule.\n-- description: Output columns declare a length\n-- severity: warning\n\n-- tags: Naming, schema  critical-1\nSELECT 1\n".Replace("Naming", "naming"), diags)!;
        Assert.Empty(diags);
        Assert.Equal(("warning", "Output columns declare a length"), (rule.Severity, rule.Description));
        Assert.Equal(["naming", "schema", "critical-1"], rule.Tags);
    }

    [Theory]
    [InlineData("-- severty: warning\nSELECT 1", "DDB-104")]
    [InlineData("-- severity: loud\nSELECT 1", "DDB-106")]
    [InlineData("-- severity: error\n-- severity: warning\nSELECT 1", "DDB-102")]
    [InlineData("-- tags: Bad Tag!\nSELECT 1", "DDB-106")]
    [InlineData("-- description: only comments\n", "DDB-603")]
    public void A_damaged_header_is_a_diagnostic_with_a_line(string text, string code)
    {
        var diags = new List<Diagnostic>();
        Assert.Null(Load(text, diags));
        Assert.Equal(code, diags[0].Code);
        Assert.True(diags[0].Location.Line >= 1);
    }

    [Fact]
    public void Settings_are_only_read_from_the_header_and_names_follow_the_path()
    {
        var diags = new List<Diagnostic>();
        var rule = Load("SELECT 1 -- severity: loud\n-- severity: nonsense\n", diags)!;       // after the first line of SQL it is just a comment
        Assert.Empty(diags);
        Assert.Equal("error", rule.Severity);
        Assert.Equal("naming.no_max", TestRuleLoader.NameOf("tests/metadata/naming/no_max.sql"));
    }

    // ---- the views ----

    [Fact]
    public void The_views_flatten_the_documents_and_agree_with_the_published_ones()
    {
        var ctx = ProjectContext.Load(Project());
        using var db = MetadataDatabase.Open(MetadataPublisher.Collect(ctx, null, null).Select(d => new MetadataDocument(d.SubjectKind, d.Subject, d.Json, d.Hash)), ProductInfo.Version);
        List<string> Rows(string sql)
        {
            var run = db.Run(sql, 1000);
            Assert.True(run.Outcome == RuleOutcome.Ran, run.Message);
            static string Cell(object? v) => v switch { null => "∅", System.Collections.IEnumerable l and not string => "[" + string.Join(",", l.Cast<object?>().Select(Cell)) + "]", _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)! };
            return run.Rows.Select(r => string.Join("|", r.Values.Select(Cell))).ToList();
        }

        foreach (var view in MetadataDatabase.Names) Rows($"SELECT * FROM {view}");                                   // every view runs
        Assert.Equal(["connection", "model", "column_name", "logical_type", "nullable", "collation", "sqlserver_type", "postgres_type", "fabric_type", "inferred_nullability", "upstream", "model_kind"],
            db.Run("SELECT * FROM metadata_columns LIMIT 1", 1).Columns);                                              // the names of the view `init` creates in a target

        Assert.Equal(["project|project|∅", "model|staging.orders|mapped", "model|marts.fct_orders|incremental_by_unique_key"], Rows("SELECT subject_kind, subject, model_kind FROM metadata_current ORDER BY CASE subject_kind WHEN 'project' THEN 0 ELSE 1 END, subject DESC"));
        Assert.Equal(["marts.fct_orders|note2|VARCHAR|nvarchar(max)|incremental_by_unique_key"], Rows("SELECT model, column_name, logical_type, sqlserver_type, model_kind FROM metadata_columns WHERE column_name = 'note2'"));
        Assert.Equal(new[] { "staging.orders|order_id|BIGINT|False|mapped", "marts.fct_orders|order_id|BIGINT|False|incremental_by_unique_key" }.Order(), Rows("SELECT model, column_name, logical_type, nullable, model_kind FROM metadata_columns WHERE column_name = 'order_id'").Order());
        Assert.Equal(["marts.fct_orders|incremental_by_unique_key|[order_id]|[order_id]|[sqlserver]|3", "staging.orders|mapped|∅|[order_id]|[sqlserver]|3"], Rows("SELECT model, kind, unique_key, grain, connections, column_count FROM metadata_models ORDER BY model"));      // one view for the models of every kind
        Assert.Equal(["marts.fct_orders|staging.orders|mapped"], Rows("SELECT model, upstream, upstream_kind FROM metadata_upstream"));
        Assert.Equal(["staging.orders|IX_orders_amount|[amount]|False|[note]"], Rows("SELECT model, index_name, columns, is_unique, include FROM metadata_indexes WHERE model = 'staging.orders'"));
        Assert.Equal(["staging.orders|FK_orders_self|[order_id]|staging.orders|[order_id]"], Rows("SELECT model, foreign_key_name, columns, referenced_table, referenced_columns FROM metadata_foreign_keys"));
        Assert.Equal(["marts.fct_orders|note2|expression|staging.orders|note"], Rows("SELECT model, column_name, transform, upstream_table, upstream_column FROM metadata_lineage WHERE column_name = 'note2'"));
        Assert.Equal(["marts.fct_orders|ux_fct_orders_order_id|[order_id]|True"], Rows("SELECT model, index_name, columns, is_unique FROM metadata_indexes WHERE model = 'marts.fct_orders'"));
        Assert.Equal(["marts.fct_orders|note2|sqlserver|nvarchar(max)"], Rows("SELECT model, column_name, connection, native_type FROM metadata_native_types WHERE column_name = 'note2'"));
        Assert.Equal(["marts.fct_orders|sqlserver|default|delete_insert_by_key"], Rows("SELECT model, connection, operation, strategy FROM metadata_loads"));
        Assert.Equal(["marts.fct_orders|sqlserver|grant|post_create|hooks/grant.sql"], Rows("SELECT model, connection, hook_name, event, script FROM metadata_hooks"));
        Assert.Equal([1], Rows("SELECT count(*) FROM metadata_current WHERE model_kind = 'incremental_by_unique_key' AND document_hash IS NOT NULL").Select(int.Parse));
    }

    // ---- the command ----

    [Fact]
    public void Rules_pass_fail_warn_and_error_and_the_exit_code_follows_the_severity()
    {
        var dir = Project(
            ("passes.sql", "SELECT model FROM metadata_models WHERE len(grain) = 0\n"),
            ("naming/no_unlimited.sql", "-- description: Declare a length\n-- severity: warning\n-- tags: naming\nSELECT model, column_name FROM metadata_columns WHERE model_kind NOT IN ('mapped', 'native') AND logical_type = 'VARCHAR'\n"),
            ("grain_is_the_key.sql", "-- tags: critical\nSELECT model FROM metadata_models WHERE grain <> unique_key\n"));
        var (exit, doc, err) = Test(dir);
        Assert.Equal(0, exit);                                                                                // a warning does not fail the run
        Assert.Equal((3, 2, 0, 1, 0), ((int)doc["data"]!["counts"]!["total"]!, (int)doc["data"]!["counts"]!["passed"]!, (int)doc["data"]!["counts"]!["failed"]!, (int)doc["data"]!["counts"]!["warned"]!, (int)doc["data"]!["counts"]!["errored"]!));
        var warned = One(doc, "naming.no_unlimited");
        Assert.Equal(("warn", 1, "warning", "Declare a length"), ((string)warned["status"]!, (int)warned["violations"]!, (string)warned["severity"]!, (string)warned["description"]!));
        Assert.Equal(["model", "column_name"], warned["columns"]!.AsArray().Select(c => (string)c!));
        Assert.Equal("note2", (string?)warned["rows"]![0]!["column_name"]);
        Assert.Equal(["naming"], warned["tags"]!.AsArray().Select(t => (string)t!));
        var d = doc["diagnostics"]!.AsArray().Single()!;
        Assert.Equal(("DDB-601", "warning"), ((string)d["code"]!, (string)d["severity"]!));
        Assert.Equal(0, (int)Test(dir, "--limit", "0").Doc["data"]!["tests"]![1]!["rows"]!.AsArray().Count);   // --limit 0 shows no rows, still counts them

        Assert.Equal(1, Test(dir, "--strict").Exit);

        File.WriteAllText(Path.Combine(dir, "tests/metadata/grain_is_the_key.sql"), "-- tags: critical\nSELECT model FROM metadata_models WHERE len(unique_key) > 0\n");
        var (failExit, failDoc, _) = Test(dir);
        Assert.Equal(1, failExit);
        var failed = One(failDoc, "grain_is_the_key");
        Assert.Equal(("fail", "marts.fct_orders"), ((string)failed["status"]!, (string)failed["rows"]![0]!["model"]!));
        Assert.Contains(failDoc["diagnostics"]!.AsArray(), x => (string?)x!["code"] == "DDB-601" && (string?)x["severity"] == "error");
    }

    [Fact]
    public void Tests_can_be_chosen_by_name_path_or_tag_and_an_unknown_name_is_a_usage_error()
    {
        var dir = Project(("a.sql", "-- tags: critical\nSELECT 1 WHERE false\n"), ("b/c.sql", "-- tags: naming\nSELECT 1\n"), ("d.sql", "SELECT 1 WHERE false\n"));
        Assert.Equal(["b.c"], Test(dir, "b.c").Doc["data"]!["tests"]!.AsArray().Select(t => (string)t!["name"]!));
        Assert.Equal(["b.c"], Test(dir, "tests/metadata/b/c.sql").Doc["data"]!["tests"]!.AsArray().Select(t => (string)t!["name"]!));
        var tagged = Test(dir, "--tag", "critical");
        Assert.Equal(["a"], tagged.Doc["data"]!["tests"]!.AsArray().Select(t => (string)t!["name"]!));
        Assert.Equal(0, tagged.Exit);                                                                          // the failing rule is not in the group
        Assert.Equal(["a", "b.c"], Test(dir, "--tag", "critical", "naming").Doc["data"]!["tests"]!.AsArray().Select(t => (string)t!["name"]!));

        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["project", "tests", "run", "nope", "--project", dir], o, e, environment: _ => null));
        Assert.Contains("No test named `nope`", e.ToString());
    }

    [Fact]
    public void A_test_that_is_not_one_select_or_cannot_run_is_an_error_whatever_its_severity_and_never_reads_files()
    {
        var dir = Project(
            ("two.sql", "-- severity: warning\nSELECT 1; SELECT 2\n"),
            ("ddl.sql", "CREATE TABLE t(a INT)\n"),
            ("typo.sql", "-- severity: warning\nSELECT nope FROM metadata_models\n"),
            ("files.sql", "SELECT * FROM read_csv('/etc/hostname')\n"),
            ("good.sql", "SELECT 1 WHERE false\n"));
        var (exit, doc, _) = Test(dir);
        Assert.Equal(1, exit);
        Assert.Equal(("pass", "error", "error", "error", "error"), ((string)One(doc, "good")["status"]!, (string)One(doc, "two")["status"]!, (string)One(doc, "ddl")["status"]!, (string)One(doc, "typo")["status"]!, (string)One(doc, "files")["status"]!));
        var codes = doc["diagnostics"]!.AsArray().Select(d => (string)d!["code"]!).Order().ToArray();
        Assert.Equal(["DDB-602", "DDB-602", "DDB-603", "DDB-603"], codes);                                     // typo and files could not run; two and ddl are not a single query
    }

    [Fact]
    public void A_damaged_file_is_its_own_finding_and_does_not_stop_the_others()
    {
        var dir = Project(("bad.sql", "-- severity: loud\nSELECT 1\n"), ("ok.sql", "SELECT 1 WHERE false\n"));
        var (exit, doc, _) = Test(dir);
        Assert.Equal(1, exit);
        Assert.Equal(("error", "pass"), ((string)One(doc, "bad")["status"]!, (string)One(doc, "ok")["status"]!));
        Assert.Contains(doc["diagnostics"]!.AsArray(), d => (string?)d!["code"] == "DDB-106");
    }

    [Fact]
    public void A_project_without_tests_has_nothing_to_run_and_writes_nothing()
    {
        var dir = Project();
        var before = Snapshot(dir);
        var (exit, doc, _) = Test(dir);
        Assert.Equal(0, exit);
        Assert.Equal(0, (int)doc["data"]!["counts"]!["total"]!);
        Assert.Equal(before, Snapshot(dir));
    }
}
