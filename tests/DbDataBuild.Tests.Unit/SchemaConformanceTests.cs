using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Core;
using DbDataBuild.Models;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using static DbDataBuild.Tests.Unit.PolyglotBindingTests;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The published JSON Schemas (schemas/) and the C# loaders must agree. One corpus runs through both: structural problems
/// must fail in the schema and the loader, and semantic-only problems (name against path, grain against unique_key, column
/// references) must pass the schema and fail the loader. The schema sees the YAML the way an editor does (typed scalars).
/// </summary>
public class SchemaConformanceTests
{
    internal static JsonSchema LoadSchema(string name)
    {
        // output.schema.json refers to metadata.schema.json by its $id, so that one is loaded (and registered) first
        var schema = JsonSchema.FromFile(Path.Combine(RepoRoot(), "schemas", name + ".schema.json"));
        if (name is "metadata" or "declared-model") SchemaRegistry.Global.Register(schema);
        if (name == "output") LoadSchema("metadata");
        if (name == "model") LoadSchema("declared-model");     // a model file that says `kind: mapped` or `kind: native` is handed to the declared-model schema
        return schema;
    }

    /// <summary>YAML as a YAML-aware editor reads it: unquoted true/false/numbers are typed.</summary>
    internal static JsonNode? EditorView(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, Convert(c.Value)))),
        YamlSequenceNode q => new JsonArray(q.Children.Select(Convert).ToArray()),
        YamlScalarNode { Style: ScalarStyle.Plain } s => Typed(s.Value ?? ""),
        YamlScalarNode s => JsonValue.Create(s.Value ?? ""),
        _ => null,
    };

    /// <summary>YAML 1.2 core schema typing of a plain scalar, as language servers apply it.</summary>
    private static JsonNode? Typed(string v) => v switch
    {
        "" or "~" or "null" or "Null" or "NULL" => null,
        "true" or "True" or "TRUE" => JsonValue.Create(true),
        "false" or "False" or "FALSE" => JsonValue.Create(false),
        _ when long.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
        _ when double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && v.Any(char.IsDigit) && !v.Contains(' ') => JsonValue.Create(d),
        _ => JsonValue.Create(v),
    };

    internal static bool SchemaAccepts(JsonSchema schema, string yaml) =>
        schema.Evaluate(EditorView(yaml), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    public sealed record Fixture(string Name, string Yaml, bool SchemaValid, string[] Codes, string ExpectedName = "marts.fct_orders")
    {
        public override string ToString() => Name;
    }

    private const string Cols = "columns:\n  - name: a\n    type: INT\n";

    private static Fixture Ok(string name, string yaml) => new(name, yaml, true, []);
    private static Fixture Bad(string name, string yaml, params string[] codes) => new(name, yaml, false, codes);
    private static Fixture Semantic(string name, string yaml, params string[] codes) => new(name, yaml, true, codes);

    public static readonly Fixture[] ModelFixtures =
    [
        Ok("design example", TestSupport.ValidModel),
        Ok("view minimal", "name: marts.fct_orders\nkind: view\n" + Cols),
        Ok("full, nullable unquoted and quoted, collation",
            "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: a, type: VARCHAR(20), nullable: false, collation: default}\n  - {name: b, type: INT, nullable: \"true\"}\n"),
        Ok("time range with lookback", "name: marts.fct_orders\nkind: {incremental_by_time_range: {time_column: d, lookback: 3 days}}\ngrain: [d]\ncolumns:\n  - {name: d, type: DATE}\n"),
        Ok("composite key", "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [a, b]}}\ngrain: [b, a]\ncolumns:\n  - {name: a, type: INT}\n  - {name: b, type: INT}\n"),
        Ok("all three targets", "name: marts.fct_orders\nkind: full\nconnections: [sqlserver, fabric, postgres]\n" + Cols),
        Ok("renames", "name: marts.fct_orders\nkind: full\n" + Cols + "renames:\n  - from: old\n    to: a\n"),
        Ok("loads mapping", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  daily:\n    default: true\n    strategy: full_replace\n"),

        Bad("unknown top key", "name: marts.fct_orders\nkind: full\n" + Cols + "surprise: 1\n", "DDB-104"),
        Bad("missing name", "kind: full\n" + Cols, "DDB-105"),
        Semantic("missing kind", "name: marts.fct_orders\n" + Cols, "DDB-105"),
        Bad("missing columns", "name: marts.fct_orders\nkind: full\n", "DDB-105"),
        Bad("unknown kind", "name: marts.fct_orders\nkind: nope\n" + Cols, "DDB-106"),
        Bad("kind as a list", "name: marts.fct_orders\nkind: [full]\n" + Cols, "DDB-248"),
        Bad("two kinds", "name: marts.fct_orders\nkind: {full: {}, view: {}}\n" + Cols, "DDB-248"),
        Bad("an empty kind", "name: marts.fct_orders\nkind: {}\n" + Cols, "DDB-248"),
        Bad("settings that are not a mapping", "name: marts.fct_orders\nkind: {full: nope}\n" + Cols, "DDB-248"),
        Ok("kind with an empty mapping of settings", "name: marts.fct_orders\nkind: {full: {}}\n" + Cols),
        Bad("view with unique_key", "name: marts.fct_orders\nkind: {view: {unique_key: [a]}}\n" + Cols, "DDB-104"),
        Bad("unique_key missing", "name: marts.fct_orders\nkind: incremental_by_unique_key\ngrain: [a]\n" + Cols, "DDB-214"),
        Bad("unique_key empty", "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: []}}\ngrain: [a]\n" + Cols, "DDB-214"),
        Bad("time_column missing", "name: marts.fct_orders\nkind: incremental_by_time_range\ngrain: [a]\n" + Cols, "DDB-215"),
        Bad("grain missing on incremental", "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [a]}}\n" + Cols, "DDB-216"),
        Bad("grain empty on incremental", "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [a]}}\ngrain: []\n" + Cols, "DDB-216"),
        Bad("grain not a list", "name: marts.fct_orders\nkind: full\ngrain: a\n" + Cols, "DDB-106"),
        Bad("invalid connection name", "name: marts.fct_orders\nkind: full\nconnections: [\"9 bad\"]\n" + Cols, "DDB-106"),
        Bad("empty targets", "name: marts.fct_orders\nkind: full\nconnections: []\n" + Cols, "DDB-106"),
        Bad("duplicate targets", "name: marts.fct_orders\nkind: full\nconnections: [sqlserver, sqlserver]\n" + Cols, "DDB-106"),
        Bad("empty columns", "name: marts.fct_orders\nkind: full\ncolumns: []\n", "DDB-106"),
        Bad("columns not a list", "name: marts.fct_orders\nkind: full\ncolumns: a\n", "DDB-106"),
        Bad("column without type", "name: marts.fct_orders\nkind: full\ncolumns:\n  - name: a\n", "DDB-105"),
        Bad("column unknown key", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: a, type: INT, default: 0}\n", "DDB-104"),
        Bad("column empty name", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: \"\", type: INT}\n", "DDB-106"),
        Bad("nullable maybe", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: a, type: INT, nullable: maybe}\n", "DDB-106"),
        Bad("rename missing to", "name: marts.fct_orders\nkind: full\n" + Cols + "renames:\n  - from: old\n", "DDB-105"),
        Bad("rename unknown key", "name: marts.fct_orders\nkind: full\n" + Cols + "renames:\n  - {from: old, to: a, why: x}\n", "DDB-104"),
        Bad("loads as a scalar", "name: marts.fct_orders\nkind: full\n" + Cols + "loads: 5\n", "DDB-106"),

        // load operations (DESIGN.md 6.6)
        Ok("loads: every strategy", "name: marts.fct_orders\nkind: {incremental_by_time_range: {time_column: d, lookback: 3 days}}\ngrain: [d]\ncolumns:\n  - {name: d, type: DATE}\n  - {name: id, type: BIGINT}\n" +
            "loads:\n  daily:\n    default: true\n    strategy: watermark_append\n    watermark: {column: d, resolver: target_max, lookback: 3 days, on_null: initial, initial: \"2020-01-01\", overridable: true}\n" +
            "  reload_period:\n    strategy: delete_insert_by_range\n    params: {start: DATE, end: DATE}\n    max_span: 400 days\n" +
            "  by_key:\n    strategy: merge_by_key\n    key: [id]\n    connections: [sqlserver]\n  everything:\n    strategy: full_replace\n  replace_keys:\n    strategy: delete_insert_by_key\n    key: [id, d]\n"),
        Ok("loads: key defaults from the kind", "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [a]}}\ngrain: [a]\n" + Cols + "loads:\n  m:\n    strategy: merge_by_key\n"),
        Ok("loads: range column defaults from the kind", "name: marts.fct_orders\nkind: {incremental_by_time_range: {time_column: d}}\ngrain: [d]\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  r:\n    strategy: delete_insert_by_range\n"),
        Bad("loads: unknown strategy", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  x:\n    strategy: upsert_magic\n", "DDB-106"),
        Bad("loads: strategy missing", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  x:\n    default: true\n", "DDB-105"),
        Bad("loads: operation name not snake_case", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  Daily-Load:\n    strategy: full_replace\n", "DDB-106"),
        Bad("loads: unknown key", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  x:\n    strategy: full_replace\n    speed: fast\n", "DDB-104"),
        Bad("loads: key on full_replace", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  x:\n    strategy: full_replace\n    key: [a]\n", "DDB-106"),
        Bad("loads: watermark missing", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  x:\n    strategy: watermark_append\n", "DDB-105"),
        Bad("loads: watermark without resolver", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  x:\n    strategy: watermark_append\n    watermark: {column: d}\n", "DDB-105"),
        Bad("loads: unknown resolver", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  x:\n    strategy: watermark_append\n    watermark: {column: d, resolver: magic}\n", "DDB-106"),
        Bad("loads: on_null initial needs initial", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  x:\n    strategy: watermark_append\n    watermark: {column: d, resolver: target_max, on_null: initial}\n", "DDB-105"),
        Bad("loads: initial without on_null initial", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  x:\n    strategy: watermark_append\n    watermark: {column: d, resolver: target_max, initial: \"2020-01-01\"}\n", "DDB-106"),
        Bad("loads: lookback not a duration", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  x:\n    strategy: watermark_append\n    watermark: {column: d, resolver: target_max, lookback: three days}\n", "DDB-106"),
        Bad("loads: params without end", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  x:\n    strategy: delete_insert_by_range\n    column: d\n    params: {start: DATE}\n", "DDB-105"),
        Bad("loads: max_span not a duration", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  x:\n    strategy: delete_insert_by_range\n    column: d\n    max_span: forever\n", "DDB-106"),
        Bad("loads: targets invalid", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  x:\n    strategy: full_replace\n    connections: [\"9 bad\"]\n", "DDB-106"),
        Bad("loads: on a view", "name: marts.fct_orders\nkind: view\n" + Cols + "loads:\n  x:\n    strategy: full_replace\n", "DDB-106"),
        Bad("kind lookback not a duration", "name: marts.fct_orders\nkind: {incremental_by_time_range: {time_column: d, lookback: soon}}\ngrain: [d]\ncolumns:\n  - {name: d, type: DATE}\n", "DDB-106"),

        // indexes (declared by the operator; never implied by unique_key)
        Ok("indexes: unique, covering, per target", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: a, type: INT}\n  - {name: b, type: INT}\n  - {name: c, type: INT}\nindexes:\n  - {name: uq_a, columns: [a], unique: true}\n  - {name: ix_b, columns: [b, a], include: [c], connections: [sqlserver]}\n"),
        Bad("indexes: columns missing", "name: marts.fct_orders\nkind: full\n" + Cols + "indexes:\n  - {name: ix}\n", "DDB-105"),
        Bad("indexes: unknown key", "name: marts.fct_orders\nkind: full\n" + Cols + "indexes:\n  - {name: ix, columns: [a], clustered: true}\n", "DDB-104"),
        Bad("indexes: bad name", "name: marts.fct_orders\nkind: full\n" + Cols + "indexes:\n  - {name: \"my index\", columns: [a]}\n", "DDB-106"),
        Bad("indexes: unique maybe", "name: marts.fct_orders\nkind: full\n" + Cols + "indexes:\n  - {name: ix, columns: [a], unique: maybe}\n", "DDB-106"),
        Bad("indexes: invalid target", "name: marts.fct_orders\nkind: full\n" + Cols + "indexes:\n  - {name: ix, columns: [a], connections: [\"9 bad\"]}\n", "DDB-106"),
        Semantic("indexes: column not declared", "name: marts.fct_orders\nkind: full\n" + Cols + "indexes:\n  - {name: ix, columns: [ghost]}\n", "DDB-217"),
        Semantic("indexes: duplicate name", "name: marts.fct_orders\nkind: full\n" + Cols + "indexes:\n  - {name: ix, columns: [a]}\n  - {name: IX, columns: [a]}\n", "DDB-102"),
        Semantic("indexes: key column also included", "name: marts.fct_orders\nkind: full\n" + Cols + "indexes:\n  - {name: ix, columns: [a], include: [a]}\n", "DDB-106"),

        // hooks: ordered, named, per-event, per-target; groups are referenced with `use`
        Ok("hooks: every form", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: grant, event: post_create, script: hooks/grant.sql}\n" +
            "  - name: stats\n    event: post_load\n    script: {sqlserver: hooks/sqlserver/stats.sql, postgres: hooks/postgres/stats.sql}\n    effect: data\n" +
            "  - {name: only_pg, event: pre_alter, script: hooks/lock.sql, connections: [postgres], risk: risky}\n  - {use: standard}\n"),
        Ok("lint_ignore: both codes", "name: marts.fct_orders\nkind: full\n" + Cols + "lint_ignore: [DDB-223, DDB-224]\n"),
        Bad("lint_ignore: an unknown code", "name: marts.fct_orders\nkind: full\n" + Cols + "lint_ignore: [DDB-999]\n", "DDB-106"),
        Bad("hooks: not a list", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks: nope\n", "DDB-106"),
        Bad("hooks: script missing", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load}\n", "DDB-105"),
        Bad("hooks: event missing", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, script: hooks/x.sql}\n", "DDB-105"),
        Bad("hooks: unknown event", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: after_lunch, script: hooks/x.sql}\n", "DDB-106"),
        Bad("hooks: reserved event", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: pre_drop, script: hooks/x.sql}\n", "DDB-106"),
        Bad("hooks: unknown key", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load, script: hooks/x.sql, retries: 3}\n", "DDB-104"),
        Bad("hooks: script escapes the project", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load, script: ../outside.sql}\n", "DDB-106"),
        Bad("hooks: absolute script path", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load, script: /etc/x.sql}\n", "DDB-106"),
        Bad("hooks: script is not .sql", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load, script: hooks/x.sh}\n", "DDB-106"),
        Bad("hooks: bad effect", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load, script: hooks/x.sql, effect: everything}\n", "DDB-106"),
        Bad("hooks: bad risk", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load, script: hooks/x.sql, risk: yolo}\n", "DDB-106"),
        Bad("hooks: use with other keys", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {use: standard, name: x}\n", "DDB-106"),
        Semantic("hooks: duplicate name", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load, script: hooks/x.sql}\n  - {name: x, event: pre_load, script: hooks/y.sql}\n", "DDB-102"),
        Semantic("hooks: targets and per-target script together", "name: marts.fct_orders\nkind: full\n" + Cols + "hooks:\n  - {name: x, event: post_load, script: {postgres: hooks/x.sql}, connections: [postgres]}\n", "DDB-106"),

        // Semantic rules: the schema cannot express them, so it accepts and the loader rejects.
        Semantic("name does not match path", "name: marts.other\nkind: full\n" + Cols, "DDB-107"),
        Semantic("grain differs from unique_key", TestSupport.ValidModel.Replace("grain: [order_id]", "grain: [customer_id]"), "DDB-216"),
        Semantic("grain names an undeclared column", TestSupport.ValidModel.Replace("grain: [order_id]", "grain: [order_id, ghost]"), "DDB-217"),
        Semantic("duplicate column names", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: a, type: INT}\n  - {name: A, type: INT}\n", "DDB-102"),
        Semantic("loads: key strategy without any key", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  m:\n    strategy: merge_by_key\n", "DDB-105"),
        Semantic("loads: key column undeclared", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  m:\n    strategy: merge_by_key\n    key: [ghost]\n", "DDB-217"),
        Semantic("loads: range strategy without a column", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  r:\n    strategy: delete_insert_by_range\n", "DDB-105"),
        Semantic("loads: range column of the wrong type", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: s, type: VARCHAR(5)}\nloads:\n  r:\n    strategy: delete_insert_by_range\n    column: s\n", "DDB-106"),
        Semantic("loads: param type differs from the range column", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  r:\n    strategy: delete_insert_by_range\n    column: d\n    params: {start: TIMESTAMP, end: TIMESTAMP}\n", "DDB-106"),
        Semantic("loads: watermark column undeclared", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  w:\n    strategy: watermark_append\n    watermark: {column: ghost, resolver: target_max}\n", "DDB-217"),
        Semantic("loads: watermark column of the wrong type", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: s, type: VARCHAR(5)}\nloads:\n  w:\n    strategy: watermark_append\n    watermark: {column: s, resolver: target_max}\n", "DDB-106"),
        Semantic("loads: lookback unit does not fit a DATE", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  w:\n    strategy: watermark_append\n    watermark: {column: d, resolver: target_max, lookback: 3 hours}\n", "DDB-106"),
        Semantic("loads: initial is not a valid literal for the column", "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: d, type: DATE}\nloads:\n  w:\n    strategy: watermark_append\n    watermark: {column: d, resolver: target_max, on_null: initial, initial: yesterday}\n", "DDB-106"),
        Semantic("loads: two defaults for one target", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  a:\n    default: true\n    strategy: full_replace\n  b:\n    default: true\n    strategy: delete_insert_by_key\n    key: [a]\n", "DDB-106"),
        Semantic("loads: defaults on different targets are fine only when targets differ", "name: marts.fct_orders\nkind: full\n" + Cols + "loads:\n  a:\n    default: true\n    strategy: full_replace\n    connections: [sqlserver]\n  b:\n    default: true\n    strategy: full_replace\n    connections: [sqlserver, postgres]\n", "DDB-106"),
        Semantic("kind lookback unit does not fit the time column", "name: marts.fct_orders\nkind: {incremental_by_time_range: {time_column: d, lookback: 2 hours}}\ngrain: [d]\ncolumns:\n  - {name: d, type: DATE}\n", "DDB-106"),
        Semantic("rename to an undeclared column", "name: marts.fct_orders\nkind: full\n" + Cols + "renames:\n  - {from: old, to: ghost}\n", "DDB-217"),
    ];

    public static TheoryData<Fixture> ModelData
    {
        get { var d = new TheoryData<Fixture>(); foreach (var f in ModelFixtures) d.Add(f); return d; }
    }

    [Theory, MemberData(nameof(ModelData))]
    public void Model_schema_and_loader_agree(Fixture f)
    {
        Assert.Equal(f.SchemaValid, SchemaAccepts(LoadSchema("model"), f.Yaml));

        var (def, diags) = TestSupport.Load(f.Yaml, f.ExpectedName);
        if (f.Codes.Length == 0)
        {
            Assert.Empty(diags.Select(DiagnosticFormatter.Format));
            Assert.NotNull(def);
        }
        else
        {
            foreach (var code in f.Codes) Assert.Contains(diags, d => d.Code == code);
            Assert.Null(def);
        }
    }

    [Fact]
    public void Corpus_covers_every_structural_code_the_model_loader_can_raise()
    {
        var raised = ModelFixtures.SelectMany(f => f.Codes).ToHashSet();
        foreach (var code in new[] { "DDB-102", "DDB-104", "DDB-105", "DDB-106", "DDB-107", "DDB-214", "DDB-215", "DDB-216", "DDB-217" })
            Assert.Contains(code, raised);
    }

    [Fact]
    public void Editor_view_of_a_valid_example_is_typed_as_an_editor_would_see_it()
    {
        var node = EditorView("nullable: false\nversion: 16\n")!;
        Assert.Equal(JsonValueKind.False, node["nullable"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, node["version"]!.GetValueKind());
    }

    [Fact]
    public void Schemas_are_valid_json_schema_documents_with_descriptions()
    {
        foreach (var name in new[] { "model", "config", "answers", "declared-model" })
        {
            var schema = LoadSchema(name);
            Assert.NotNull(schema);
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "schemas", name + ".schema.json"));
            using var doc = JsonDocument.Parse(text);
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", doc.RootElement.GetProperty("$schema").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("description").GetString()));
        }
    }
}

public class EditorAssociationTests
{
    [Fact]
    public void Editor_settings_map_model_and_config_globs_to_schemas_that_exist()
    {
        var root = PolyglotBindingTests.RepoRoot();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".vscode", "settings.json")));
        var map = doc.RootElement.GetProperty("yaml.schemas").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()! : "");
        Assert.Equal("models/**/*.yml", map["schemas/model.schema.json"]);
        Assert.Equal(DbDataBuild.Core.ProductInfo.ConfigFile, map["schemas/config.schema.json"]);
        Assert.False(map.ContainsKey("schemas/declared-model.schema.json"));       // mapped models are in models/ too: model.schema.json hands them to it
        Assert.Contains("answers.yml", doc.RootElement.GetProperty("yaml.schemas").GetProperty("schemas/answers.schema.json").EnumerateArray().Select(e => e.GetString()));
        foreach (var schema in map.Keys) Assert.True(File.Exists(Path.Combine(root, schema)), schema);
    }
}

public class DesignDocExampleTests
{
    private static string YamlBlockAfter(string marker)
    {
        var text = File.ReadAllText(Path.Combine(PolyglotBindingTests.RepoRoot(), "DESIGN.md"));
        var from = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(from >= 0, $"marker not found: {marker}");
        var start = text.IndexOf("```yaml\n", from, StringComparison.Ordinal) + "```yaml\n".Length;
        return text[start..text.IndexOf("```", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void Model_example_in_section_6_is_valid_for_the_schema_and_the_loader()
    {
        var yaml = YamlBlockAfter("## 6. Model definitions");
        Assert.Contains("name: marts.fct_orders", yaml);
        Assert.True(SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("model"), yaml));
        var (def, diags) = TestSupport.Load(yaml);
        Assert.Empty(diags.Select(DbDataBuild.Core.DiagnosticFormatter.Format));
        Assert.NotNull(def);
    }

    [Fact]
    public void Declared_model_example_in_section_6_5_1_is_valid_for_the_schema_and_the_loader()
    {
        var yaml = YamlBlockAfter("**A mapped model**");
        Assert.True(SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("declared-model"), yaml));
        var diags = new List<DbDataBuild.Core.Diagnostic>();
        var d = DbDataBuild.Models.DeclaredModelLoader.Load(yaml, "models/staging/orders.yml", "staging.orders", diags);
        Assert.Empty(diags.Select(DbDataBuild.Core.DiagnosticFormatter.Format));
        Assert.Equal(["order_id", "amount", "customer_id"], d!.Columns.Select(c => c.Name));
        Assert.Equal(["ix_orders_amount"], d.Indexes.Select(i => i.Name));
        Assert.Equal(["fk_orders_customer"], d.ForeignKeys.Select(f => f.Name));
    }

    [Fact]
    public void Loads_example_in_section_6_6_is_valid_when_completed_with_the_columns_the_text_names()
    {
        var excerpt = YamlBlockAfter("**Declaration**");
        var yaml = "name: marts.fct_orders\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n" +
                   "  - {name: order_date, type: DATE, nullable: false}\n  - {name: modified_at, type: TIMESTAMP, nullable: false}\n" + excerpt;
        Assert.True(SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("model"), yaml));
        var (def, diags) = TestSupport.Load(yaml);
        Assert.Empty(diags.Select(DbDataBuild.Core.DiagnosticFormatter.Format));
        Assert.Equal(["daily", "reload_period", "by_key"], def!.Loads.Select(o => o.Name));
        Assert.Equal(["order_id"], def.Loads[2].Key);
        Assert.Equal("order_date", def.Loads[1].Column);                           // defaults from the kind's time_column
        Assert.Equal(3, def.Loads[0].Watermark!.Lookback!.Amount);
    }

    [Fact]
    public void Answers_example_in_section_10_1_is_valid_for_the_schema_and_the_loader()
    {
        var yaml = YamlBlockAfter("### 10.1");
        Assert.True(SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("answers"), yaml));
        var diags = new List<DbDataBuild.Core.Diagnostic>();
        var file = DbDataBuild.Models.AnswerFileLoader.Load(yaml, "answers.yml", diags);
        Assert.Empty(diags.Select(DbDataBuild.Core.DiagnosticFormatter.Format));
        Assert.Equal(3, file!.Answers.Count);
        Assert.Equal("customer_name", file.Answers.Single(a => a.Choice == "rename_to").Value);
        Assert.Single(file.Answers, a => a.AcceptInferred);
    }

    [Fact]
    public void Config_example_in_section_9_4_is_valid_for_the_schema_and_the_loader()
    {
        var yaml = YamlBlockAfter("### 9.4 Project configuration");
        Assert.True(SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("config"), yaml));
        var diags = new List<DbDataBuild.Core.Diagnostic>();
        var cfg = DbDataBuild.Models.ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags);
        Assert.Empty(diags.Select(DbDataBuild.Core.DiagnosticFormatter.Format));
        Assert.Equal(DbDataBuild.Models.ProjectConfig.Default.StringSemantics, cfg!.StringSemantics with { Collations = DbDataBuild.Models.ProjectConfig.Default.StringSemantics.Collations });
        Assert.Equal(17, cfg.TargetVersions["postgres"]);
    }
}
