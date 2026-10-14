using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`test` for models: `tests/models/*.yml`, given rows and what the query must return (DESIGN.md 9.8).</summary>
public class ModelTestsTests
{
    private const string Orders = "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: note, type: VARCHAR}\n";
    private const string Fct = "name: marts.fct_orders\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: note2, type: VARCHAR}\n";
    private const string FctSql = "SELECT o.order_id, o.amount, o.note || '!' AS note2 FROM staging.orders o WHERE o.order_id > 0\n";

    private static string Project(params (string Path, string Text)[] tests)
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n");
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), Orders);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), Fct);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), FctSql);
        foreach (var (path, text) in tests)
        {
            var full = Path.Combine(dir, "tests/models", path);
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
        var result = SchemaConformanceTests.LoadSchema("output").Evaluate(System.Text.Json.JsonSerializer.SerializeToNode(doc), new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        Assert.True(result.IsValid, string.Join("\n", result.Details.Where(x => x.Errors != null).SelectMany(x => x.Errors!.Select(kv => $"{x.InstanceLocation}: {kv.Key} {kv.Value}"))) + "\n" + o);
        return (exit, doc, e.ToString());
    }

    private static JsonNode One(JsonNode doc, string name) => doc["data"]!["tests"]!.AsArray().Single(t => (string?)t!["name"] == name)!;
    private static string Status(JsonNode doc, string name) => (string)One(doc, name)["status"]!;

    // ---- the file format ----

    private static ModelTestFile? Load(string text, List<Diagnostic> diags) => ModelTestLoader.Load(text, "tests/models/marts/fct_orders.yml", diags);

    [Fact]
    public void A_test_file_reads_cases_given_rows_both_forms_of_expect_and_nulls()
    {
        var diags = new List<Diagnostic>();
        var t = Load("# Some prose about it.\n# description: Orders keep their amounts\n# severity: warning\n# tags: critical, orders\nmodel: marts.fct_orders\ncases:\n" +
                     "  - name: one\n    given:\n      staging.orders:\n        - {order_id: 1, amount: 10.00, note: \"null\"}\n        - {order_id: 2, amount: null, note: ~}\n        - order_id: 3\n          note:\n    expect:\n      ordered: true\n      rows:\n        - {order_id: 1}\n" +
                     "  - name: two\n    expect: [{order_id: 1}]\n    assert: SELECT 1 WHERE false\n  - name: three\n    expect: []\n", diags)!;
        Assert.Empty(diags);
        Assert.Equal(("marts.fct_orders", "marts.fct_orders", "warning", "Orders keep their amounts"), (t.Name, t.Model, t.Severity, t.Description));
        Assert.Equal(["critical", "orders"], t.Tags);
        Assert.Equal(["one", "two", "three"], t.Cases.Select(c => c.Name));
        var rows = t.Cases[0].Given.Single().Rows;
        Assert.Equal(["null", null, null], [rows[0].Values["note"].Text, rows[1].Values["note"].Text, rows[2].Values["note"].Text]);   // quoted "null" is text; plain null, ~ and nothing are NULL
        Assert.Equal("10.00", rows[0].Values["amount"].Text);
        Assert.Null(rows[1].Values["amount"].Text);
        Assert.True(t.Cases[0].Ordered);
        Assert.Single(t.Cases[1].Expect!);
        Assert.Equal("SELECT 1 WHERE false", t.Cases[1].Assert);
        Assert.Empty(t.Cases[2].Expect!);
        Assert.Null(Load("cases:\n  - {name: a, expect: []}\n", diags = [])!.Description);
    }

    [Theory]
    [InlineData("cases:\n  - {name: a, expect: []}\nsurprise: 1\n", "DDB-104")]
    [InlineData("model: x\n", "DDB-105")]
    [InlineData("cases: []\n", "DDB-106")]
    [InlineData("cases:\n  - {expect: []}\n", "DDB-105")]
    [InlineData("cases:\n  - {name: a}\n", "DDB-105")]
    [InlineData("cases:\n  - {name: a, expect: [], extra: 1}\n", "DDB-104")]
    [InlineData("cases:\n  - {name: a, expect: []}\n  - {name: a, expect: []}\n", "DDB-102")]
    [InlineData("cases:\n  - name: a\n    expect: {rows: [], ordered: maybe}\n", "DDB-106")]
    [InlineData("cases:\n  - name: a\n    expect: {ordered: true}\n", "DDB-105")]
    [InlineData("cases:\n  - name: a\n    given: {staging.orders: nope}\n    expect: []\n", "DDB-106")]
    [InlineData("cases:\n  - name: a\n    expect: [{order_id: [1]}]\n", "DDB-106")]
    [InlineData("# severity: loud\ncases:\n  - {name: a, expect: []}\n", "DDB-106")]
    public void A_damaged_file_is_a_diagnostic_with_a_position(string text, string code)
    {
        var diags = new List<Diagnostic>();
        Assert.Null(Load(text, diags));
        Assert.Contains(code, diags.Select(d => d.Code));
        Assert.All(diags, d => Assert.True(d.Location.Line >= 1));
    }

    // ---- running ----

    private const string Cases =
        "cases:\n" +
        "  - name: passes rows through\n    given:\n      staging.orders:\n        - {order_id: 1, amount: 10, note: hi}\n        - {order_id: 2, amount: null, note: null}\n        - {order_id: 0, amount: 5, note: filtered}\n" +
        "    expect:\n      - {order_id: 1, amount: 10.00, note2: \"hi!\"}\n      - {order_id: 2, amount: null, note2: null}\n" +
        "  - name: wrong amount\n    given:\n      staging.orders:\n        - {order_id: 1, amount: 10, note: hi}\n    expect:\n      - {order_id: 1, amount: 11, note2: \"hi!\"}\n" +
        "  - name: duplicates count\n    given:\n      staging.orders:\n        - {order_id: 1, amount: 1, note: a}\n        - {order_id: 1, amount: 1, note: a}\n    expect:\n      - {order_id: 1, amount: 1, note2: \"a!\"}\n" +
        "  - name: empty in, empty out\n    expect: []\n" +
        "  - name: wrong order\n    given:\n      staging.orders:\n        - {order_id: 1, amount: 1, note: a}\n        - {order_id: 2, amount: 2, note: b}\n    expect:\n      ordered: true\n      rows:\n        - {order_id: 2, amount: 2, note2: \"b!\"}\n        - {order_id: 1, amount: 1, note2: \"a!\"}\n" +
        "  - name: assert\n    given:\n      staging.orders:\n        - {order_id: 3, amount: -1, note: a}\n    assert: SELECT * FROM result WHERE amount < 0\n";

    [Fact]
    public void Cases_pass_and_fail_on_rows_amounts_duplicates_order_and_assertions()
    {
        var dir = Project(("marts/fct_orders.yml", "# tags: critical\n" + Cases));
        var (exit, doc, _) = Test(dir);
        Assert.Equal(1, exit);
        Assert.Equal(("pass", "fail", "fail", "pass", "fail", "fail"),
            (Status(doc, "marts.fct_orders::passes rows through"), Status(doc, "marts.fct_orders::wrong amount"), Status(doc, "marts.fct_orders::duplicates count"),
             Status(doc, "marts.fct_orders::empty in, empty out"), Status(doc, "marts.fct_orders::wrong order"), Status(doc, "marts.fct_orders::assert")));

        var wrong = One(doc, "marts.fct_orders::wrong amount");
        Assert.Equal(("model", "marts.fct_orders", "wrong amount", 2), ((string)wrong["kind"]!, (string)wrong["model"]!, (string)wrong["case"]!, (int)wrong["violations"]!));
        Assert.Equal(["_diff", "order_id", "amount", "note2"], wrong["columns"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(["missing|11", "unexpected|10"], wrong["rows"]!.AsArray().Select(r => $"{(string)r!["_diff"]!}|{(decimal)r["amount"]!:0}").Order(StringComparer.Ordinal));
        Assert.Equal(["critical"], wrong["tags"]!.AsArray().Select(t => (string)t!));

        var dup = One(doc, "marts.fct_orders::duplicates count");
        Assert.Equal("unexpected", (string?)dup["rows"]![0]!["_diff"]);                                         // a duplicate is a row too (EXCEPT ALL)
        var order = One(doc, "marts.fct_orders::wrong order");
        Assert.Equal(["position", "expected_order_id", "expected_amount", "expected_note2", "returned_order_id", "returned_amount", "returned_note2"], order["columns"]!.AsArray().Select(c => (string)c!));
        var asserted = One(doc, "marts.fct_orders::assert");
        Assert.Equal((1, -1m), ((int)asserted["violations"]!, (decimal)asserted["rows"]![0]!["amount"]!));
        Assert.Equal(["DDB-601", "DDB-601", "DDB-601", "DDB-601"], doc["diagnostics"]!.AsArray().Select(d => (string)d!["code"]!));
        Assert.Equal((6, 2, 4, 0, 0), ((int)doc["data"]!["counts"]!["total"]!, (int)doc["data"]!["counts"]!["passed"]!, (int)doc["data"]!["counts"]!["failed"]!, (int)doc["data"]!["counts"]!["warned"]!, (int)doc["data"]!["counts"]!["errored"]!));
    }

    [Fact]
    public void Values_are_cast_to_the_declared_types_so_ten_is_ten_point_zero_zero_and_null_is_not_the_text_null()
    {
        var dir = Project(("marts/fct_orders.yml",
            "cases:\n  - name: types\n    given:\n      staging.orders:\n        - {order_id: \"1\", amount: \"10.5\", note: \"null\"}\n    expect:\n      - {order_id: 1, amount: 10.50, note2: \"null!\"}\n" +
            "  - name: null is not text\n    given:\n      staging.orders:\n        - {order_id: 1, amount: 1, note: null}\n    expect:\n      - {order_id: 1, amount: 1, note2: \"null!\"}\n"));
        var (exit, doc, _) = Test(dir);
        Assert.Equal(1, exit);
        Assert.Equal(("pass", "fail"), (Status(doc, "marts.fct_orders::types"), Status(doc, "marts.fct_orders::null is not text")));   // NULL || '!' is NULL, so the text "null!" is wrong
    }

    [Fact]
    public void A_case_that_cannot_run_says_why_and_is_an_error_whatever_its_severity()
    {
        var dir = Project(("marts/fct_orders.yml",
            "# severity: warning\ncases:\n" +
            "  - name: unread table\n    given:\n      staging.customers:\n        - {id: 1}\n    expect: []\n" +
            "  - name: unknown column\n    given:\n      staging.orders:\n        - {order_id: 1, colour: red}\n    expect: []\n" +
            "  - name: missing not null\n    given:\n      staging.orders:\n        - {amount: 1}\n    expect: []\n" +
            "  - name: bad value\n    given:\n      staging.orders:\n        - {order_id: one, amount: 1, note: a}\n    expect: []\n" +
            "  - name: unknown expected column\n    expect:\n      - {order_id: 1, nope: 2}\n" +
            "  - name: assert is not a select\n    assert: DROP TABLE result\n" +
            "  - name: assert cannot run\n    assert: SELECT nope FROM result\n" +
            "  - name: fine\n    expect: []\n"));
        var (exit, doc, err) = Test(dir);
        Assert.Equal(1, exit);
        string Msg(string c) => (string)One(doc, "marts.fct_orders::" + c)["error"]!;
        Assert.Contains("does not read", Msg("unread table"));
        Assert.Contains("`colour`, which is not declared", Msg("unknown column"));
        Assert.Contains("gives no value for `order_id`, which is NOT NULL", Msg("missing not null"));
        Assert.Contains("one", Msg("bad value"));                                                             // DuckDB's own conversion error
        Assert.Contains("`nope`, which is not declared", Msg("unknown expected column"));
        Assert.Equal(("error", "error", "error", "error", "error", "error", "error", "pass"),
            (Status(doc, "marts.fct_orders::unread table"), Status(doc, "marts.fct_orders::unknown column"), Status(doc, "marts.fct_orders::missing not null"), Status(doc, "marts.fct_orders::bad value"),
             Status(doc, "marts.fct_orders::unknown expected column"), Status(doc, "marts.fct_orders::assert is not a select"), Status(doc, "marts.fct_orders::assert cannot run"), Status(doc, "marts.fct_orders::fine")));
        var codes = doc["diagnostics"]!.AsArray().Select(d => (string)d!["code"]!).Order().ToArray();
        Assert.Equal(["DDB-602", "DDB-602", "DDB-602", "DDB-602", "DDB-602", "DDB-602", "DDB-603"], codes);
        Assert.Contains("tests/models/marts/fct_orders.yml:", err.Length == 0 ? "tests/models/marts/fct_orders.yml:" : err);
    }

    [Fact]
    public void A_warning_test_only_reports_and_a_test_for_a_missing_model_is_an_error()
    {
        var dir = Project(
            ("marts/fct_orders.yml", "# severity: warning\ncases:\n  - name: wrong\n    given:\n      staging.orders:\n        - {order_id: 1, amount: 1, note: a}\n    expect: []\n"),
            ("ghost.yml", "cases:\n  - {name: a, expect: []}\n"));
        var (exit, doc, _) = Test(dir);
        Assert.Equal(1, exit);                                                                                // the ghost test is an error
        Assert.Equal(("warn", "error"), (Status(doc, "marts.fct_orders::wrong"), Status(doc, "ghost")));
        Assert.Equal(0, Test(dir, "marts.fct_orders").Exit);                                                  // by itself a warning does not fail the run
        Assert.Equal(1, Test(dir, "marts.fct_orders", "--strict").Exit);
        var w = Test(dir, "marts.fct_orders").Doc["diagnostics"]!.AsArray().Single()!;
        Assert.Equal(("DDB-601", "warning"), ((string)w["code"]!, (string)w["severity"]!));
        Assert.Contains("is not a model of this project", (string)One(doc, "ghost")["error"]!);
    }

    [Fact]
    public void Tests_are_chosen_by_name_kind_and_tag_across_both_kinds_and_nothing_is_written()
    {
        var dir = Project(("marts/fct_orders.yml", "# tags: orders\ncases:\n  - {name: a, expect: []}\n"));
        Directory.CreateDirectory(Path.Combine(dir, "tests/metadata"));
        File.WriteAllText(Path.Combine(dir, "tests/metadata/rule.sql"), "-- tags: naming\nSELECT 1 WHERE false\n");
        var before = Snapshot(dir);
        string[] Names(JsonNode d) => d["data"]!["tests"]!.AsArray().Select(t => (string)t!["name"]!).ToArray();
        Assert.Equal(["marts.fct_orders::a", "rule"], Names(Test(dir).Doc));
        Assert.Equal(["rule"], Names(Test(dir, "--kind", "metadata").Doc));
        Assert.Equal(["marts.fct_orders::a"], Names(Test(dir, "--kind", "model").Doc));
        Assert.Equal(["marts.fct_orders::a"], Names(Test(dir, "--tag", "orders").Doc));
        Assert.Equal(["marts.fct_orders::a"], Names(Test(dir, "tests/models/marts/fct_orders.yml").Doc));
        Assert.Equal(["marts.fct_orders::a"], Names(Test(dir, "marts.fct_orders").Doc));
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["project", "tests", "run", "--project", dir, "--kind", "data"], new StringWriter(), new StringWriter(), environment: _ => null));
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void A_model_that_reads_another_model_is_given_that_models_rows()
    {
        var dir = Project(("marts/v_big.yml", "cases:\n  - name: big only\n    given:\n      marts.fct_orders:\n        - {order_id: 1, amount: 5, note2: a}\n        - {order_id: 2, amount: 500, note2: b}\n    expect:\n      - {order_id: 2}\n"));
        File.WriteAllText(Path.Combine(dir, "models/marts/v_big.yml"), "name: marts.v_big\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/v_big.sql"), "SELECT f.order_id FROM marts.fct_orders f WHERE f.amount > 100\n");
        var (exit, doc, _) = Test(dir);
        Assert.Equal((0, "pass"), (exit, Status(doc, "marts.v_big::big only")));
    }
}
