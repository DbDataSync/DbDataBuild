using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`title`, `description`, `tags` and `properties` on the project, a connection, a model, a column and an index (DESIGN.md 6.5.8).</summary>
public class ObjectInfoTests
{
    private const string Words = "title: Orders fact\ndescription: \"One row per order.\"\ntags: [finance]\nproperties:\n  owner: finance\n  tier: 1\n  live: true\n  code: \"007\"\n";

    private static string ModelWith(string extra, string columnExtra = "", string indexes = "") =>
        "name: marts.fct_orders\nkind: view\n" + extra + "columns:\n  - {name: order_id, type: BIGINT, nullable: false" + columnExtra + "}\n" + indexes;

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void A_model_a_column_and_an_index_carry_the_four_and_a_property_keeps_the_type_it_was_written_with()
    {
        var yaml = "name: marts.fct_orders\nkind: full\n" + Words + "columns:\n" +
                   "  - name: order_id\n    type: BIGINT\n    nullable: false\n    title: Order id\n    description: The key\n    tags: [key]\n    properties: {pk: true, width: 8}\n" +
                   "indexes:\n  - {name: ix_order, columns: [order_id], title: By order, description: Lookups, tags: [lookup], properties: {rank: 0.5}}\n";
        var (def, diags) = Load(yaml);
        Assert.Empty(diags);
        Assert.Equal("Orders fact", def!.Info.Title);
        Assert.Equal("One row per order.", def.Info.Description);
        Assert.Equal(["finance"], def.Tags);
        Assert.Equal(new PropertyValue("text", "finance"), def.Info.Properties["owner"]);
        Assert.Equal(new PropertyValue("number", "1"), def.Info.Properties["tier"]);
        Assert.Equal(new PropertyValue("boolean", "true"), def.Info.Properties["live"]);
        Assert.Equal(new PropertyValue("text", "007"), def.Info.Properties["code"]);        // quoted: text
        var column = def.Columns.Single();
        Assert.Equal("Order id", column.Info!.Title);
        Assert.Equal(["key"], column.Info.Tags);
        Assert.Equal(new PropertyValue("boolean", "true"), column.Info.Properties["pk"]);
        var index = def.Indexes.Single();
        Assert.Equal("By order", index.Info!.Title);
        Assert.Equal(new PropertyValue("number", "0.5"), index.Info.Properties["rank"]);
    }

    [Theory]
    [InlineData("1", "number")]
    [InlineData("-12", "number")]
    [InlineData("0.5", "number")]
    [InlineData("\"1\"", "text")]
    [InlineData("007", "text")]
    [InlineData("1e3", "text")]
    [InlineData("+5", "text")]
    [InlineData("true", "boolean")]
    [InlineData("false", "boolean")]
    [InlineData("\"true\"", "text")]
    [InlineData("True", "text")]
    [InlineData("yes", "text")]
    [InlineData("\"\"", "text")]
    [InlineData("a b c", "text")]
    public void A_value_is_a_number_or_a_boolean_only_when_it_is_written_as_one(string written, string type)
    {
        var (def, diags) = Load(ModelWith("properties: {k: " + written + "}\n"));
        Assert.Empty(diags);
        Assert.Equal(type, def!.Info.Properties["k"].Type);
    }

    [Theory]
    [InlineData("properties: [a, b]\n", "DDB-106")]
    [InlineData("properties: {\"two words\": 1}\n", "DDB-243")]
    [InlineData("properties: {\"ends-\": 1}\n", "DDB-106")]
    [InlineData("properties: {\"1starts\": 1}\n", "DDB-243")]
    [InlineData("properties: {k: [1, 2]}\n", "DDB-106")]
    [InlineData("properties: {k: {a: 1}}\n", "DDB-106")]
    [InlineData("properties: {k: }\n", "DDB-106")]
    [InlineData("properties: {k: null}\n", "DDB-106")]
    [InlineData("properties: {k: ~}\n", "DDB-106")]
    [InlineData("tags: [\"bad tag\"]\n", "DDB-106")]
    [InlineData("tags: []\n", "DDB-106")]
    [InlineData("tags: [a, a]\n", "DDB-106")]
    [InlineData("title: \"\"\n", "DDB-106")]
    [InlineData("title: [a]\n", "DDB-106")]
    [InlineData("description: {a: b}\n", "DDB-106")]
    [InlineData("title: \"two\\nlines\"\n", "DDB-243")]
    public void A_damaged_title_description_tag_or_property_is_a_diagnostic(string extra, string code)
    {
        var (def, diags) = Load(ModelWith(extra));
        Assert.Null(def);
        Assert.Contains(diags, d => d.Code == code);
        Assert.All(diags, d => Assert.True(d.Location.Line >= 1));
    }

    [Fact]
    public void The_limits_are_200_for_a_title_4000_for_a_description_128_for_a_key_or_tag_and_4000_for_a_value()
    {
        void Fits(string extra) => Assert.Empty(Load(ModelWith(extra)).Diags);
        void Over(string extra) => Assert.Contains(Load(ModelWith(extra)).Diags, d => d.Code == "DDB-243");
        Fits($"title: {new string('t', 200)}\n");
        Over($"title: {new string('t', 201)}\n");
        Fits($"description: \"{new string('d', 4000)}\"\n");
        Over($"description: \"{new string('d', 4001)}\"\n");
        Fits($"tags: [{new string('a', 128)}]\n");
        Over($"tags: [{new string('a', 129)}]\n");
        Fits($"properties: {{{new string('k', 128)}: 1}}\n");
        Over($"properties: {{{new string('k', 129)}: 1}}\n");
        Fits($"properties: {{k: \"{new string('v', 4000)}\"}}\n");
        Over($"properties: {{k: \"{new string('v', 4001)}\"}}\n");
        // the same limits on a column and an index
        Assert.Contains(Load(ModelWith("", $", description: \"{new string('d', 4001)}\"")).Diags, d => d.Code == "DDB-243");
    }

    [Fact]
    public void A_diagnostic_names_the_file_of_a_property_that_came_from_a_folder()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults:\n  connections: [sqlserver]\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/_dbdatabuild.yml"), "defaults:\n  properties: {\"bad key\": 1}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ModelWith(""));
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1 AS order_id\n");
        var r = Cli("project", "compile", "--project", dir);
        Assert.Contains("DDB-243", r.Err);
        Assert.Contains("models/marts/_dbdatabuild.yml", r.Err);
    }

    private static string LayeredProject()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults:\n  connections: [sqlserver]\n  tags: [everywhere]\n  properties: {owner: data, tier: 3, zone: raw}\n");
        Directory.CreateDirectory(Path.Combine(dir, "models/finance"));
        Directory.CreateDirectory(Path.Combine(dir, "models/ops"));
        File.WriteAllText(Path.Combine(dir, "models/finance/_dbdatabuild.yml"), "defaults:\n  tags: [finance]\n  properties: {owner: finance, tier: 1}\n");
        void Model(string path, string name, string words)
        {
            File.WriteAllText(Path.Combine(dir, "models", path + ".yml"), $"name: {name}\nkind: view\n{words}columns:\n  - {{name: order_id, type: BIGINT, nullable: false}}\n");
            File.WriteAllText(Path.Combine(dir, "models", path + ".sql"), "SELECT 1 AS order_id\n");
        }
        Model("finance/revenue", "finance.revenue", "title: Revenue\ndescription: Money in\nproperties: {reviewed: true}\n");
        Model("finance/costs", "finance.costs", "properties=: {only: mine}\n");
        Model("finance/margin", "finance.margin", "properties-: [zone, tier]\n");
        Model("finance/tier_two", "finance.tier_two", "properties: {tier: 2}\n");
        Model("ops/queue", "ops.queue", "");
        return dir;
    }

    private static JsonNode Model(string dir, string name)
    {
        var r = Cli("project", "show", "metadata", name, "--project", dir, "--format", "json");
        Assert.Equal(0, r.Exit);
        return JsonNode.Parse(r.Out)!["data"]!["models"]!.AsArray().Single(m => (string?)m!["name"] == name)!;
    }

    [Fact]
    public void Properties_merge_down_the_project_files_nearest_first_with_the_same_suffixes_as_tags_and_a_title_is_never_inherited()
    {
        var dir = LayeredProject();
        var revenue = Model(dir, "finance.revenue");
        Assert.Equal("Revenue", (string?)revenue["title"]);
        Assert.Equal("Money in", (string?)revenue["description"]);
        Assert.Equal("finance", (string?)revenue["properties"]!["owner"]);                      // the folder beats the project
        Assert.Equal(1, (int?)revenue["properties"]!["tier"]);                                  // a JSON number
        Assert.Equal("raw", (string?)revenue["properties"]!["zone"]);                           // inherited from the project
        Assert.True((bool?)revenue["properties"]!["reviewed"]);                                 // the model's own, a JSON boolean
        Assert.Equal(["everywhere", "finance"], revenue["tags"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(["only"], Model(dir, "finance.costs")["properties"]!.AsObject().Select(p => p.Key));                 // `properties=` replaces
        Assert.Equal(["owner"], Model(dir, "finance.margin")["properties"]!.AsObject().Select(p => p.Key));              // `properties-` removes keys
        Assert.Equal(2, (int?)Model(dir, "finance.tier_two")["properties"]!["tier"]);                                    // the model's own wins
        var queue = Model(dir, "ops.queue");
        Assert.Null(queue["title"]);                                                           // a title and a description belong to the file that writes them
        Assert.Equal("data", (string?)queue["properties"]!["owner"]);
        Assert.Contains("owner", Cli("project", "compile", "--project", dir).Out + Cli("project", "show", "metadata", "ops.queue", "--project", dir).Out);
    }

    [Fact]
    public void A_title_or_a_description_in_a_defaults_section_is_an_unknown_key()
    {
        var dir = LayeredProject();
        File.WriteAllText(Path.Combine(dir, "models/finance/_dbdatabuild.yml"), "defaults:\n  title: Everything\n");
        var r = Cli("project", "compile", "--project", dir);
        Assert.Contains("DDB-104", r.Err);
        Assert.Contains("title", r.Err);
    }

    [Fact]
    public void The_property_selector_selects_by_the_value_as_text_whatever_its_type()
    {
        var dir = LayeredProject();
        string[] Chosen(params string[] selectors)
        {
            var r = Cli(["project", "show", "metadata", .. selectors, "--project", dir, "--format", "json"]);
            Assert.Equal(0, r.Exit);
            return JsonNode.Parse(r.Out)!["data"]!["models"]!.AsArray().Select(m => (string)m!["name"]!).Order().ToArray();
        }
        Assert.Equal(["finance.costs", "finance.margin", "finance.revenue", "finance.tier_two", "ops.queue"], Chosen());
        Assert.Equal(["finance.revenue"], Chosen("property:reviewed=true"));
        Assert.Equal(["finance.tier_two"], Chosen("property:tier=2"));
        Assert.Equal(["finance.revenue"], Chosen("property:tier=1"));
        Assert.Equal(["finance.costs"], Chosen("property:only=mine"));
        Assert.Equal(["finance.revenue", "finance.tier_two", "ops.queue"], Chosen("property:tier"));                      // the key alone: has the property
        Assert.Equal(["finance.revenue"], Chosen("property:tier=1,tag:finance"));
        var none = Cli("project", "show", "metadata", "property:tier=99", "--project", dir);
        Assert.Equal(CliApp.ExitUsage, none.Exit);
        Assert.Contains("no model has the property `tier` with the value `99`", none.Err);
    }

    [Fact]
    public void The_project_and_a_connection_carry_the_four_and_the_project_document_says_so()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"),
            "title: Sales\ndescription: \"Sales analytics\"\ntags: [sales, \"team:data\"]\nproperties: {owner: data, tier: 1}\nconnections:\n  sqlserver: {version: 16, title: Warehouse, description: Main, tags: [prod], properties: {region: eu}}\n  reporting: {engine: postgres}\n");
        var r = Cli("project", "show", "metadata", "--project", dir, "--format", "json");
        Assert.Equal(0, r.Exit);
        var project = JsonNode.Parse(r.Out)!["data"]!["project"]!;
        Assert.Equal("Sales", (string?)project["title"]);
        Assert.Equal(["sales", "team:data"], project["tags"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(1, (int?)project["properties"]!["tier"]);
        var warehouse = project["config"]!["connections"]!["sqlserver"]!;
        Assert.Equal("Warehouse", (string?)warehouse["title"]);
        Assert.Equal(16, (int?)warehouse["version"]);
        Assert.Equal("eu", (string?)warehouse["properties"]!["region"]);
        var reporting = project["config"]!["connections"]!["reporting"]!.AsObject();
        Assert.Equal(["engine"], reporting.Select(p => p.Key));                               // a connection with nothing said has nothing added
    }

    [Fact]
    public void A_damaged_project_or_connection_entry_is_a_diagnostic_and_the_project_keys_do_not_clash_with_defaults()
    {
        void Bad(string yaml, string code)
        {
            var diags = new List<Diagnostic>();
            ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags);
            Assert.Contains(diags, d => d.Code == code);
        }
        Bad("properties: [a]\n", "DDB-106");
        Bad("tags: [\"x y\"]\n", "DDB-106");
        Bad("title: " + new string('x', 201) + "\n", "DDB-243");
        Bad("connections:\n  sqlserver: {properties: {\"a b\": 1}}\n", "DDB-243");
        Bad("connections:\n  sqlserver: {title: [1]}\n", "DDB-106");
        var diags = new List<Diagnostic>();
        var cfg = ProjectConfigLoader.Load("tags: [project]\ndefaults:\n  tags: [model]\n", "dbdatabuild.yml", diags)!;
        Assert.Empty(diags);
        Assert.Equal(["project"], cfg.Info.Tags);
        Assert.Equal(["model"], ((Models.Yaml.YamlSequence)cfg.Defaults!.Get("tags")!).Items.Select(i => ((Models.Yaml.YamlScalar)i).Value));
    }

    [Fact]
    public void A_mapped_model_carries_them_the_writer_writes_them_back_and_the_definition_hash_ignores_them()
    {
        var yaml = "name: staging.orders\ntitle: Orders\ndescription: \"From the shop\"\ntags: [raw]\nproperties: {system: shop, rows: 12}\nkind: mapped\ncolumns:\n" +
                   "  - {name: order_id, type: BIGINT, nullable: false, title: Id, properties: {pk: true}}\nindexes:\n  - {name: ix_order, columns: [order_id], title: Key, tags: [pk]}\n";
        var diags = new List<Diagnostic>();
        var d = DeclaredModelLoader.Load(yaml, "models/staging/orders.yml", "staging.orders", diags)!;
        Assert.Empty(diags);
        Assert.Equal("Orders", d.Info.Title);
        Assert.Equal(new PropertyValue("number", "12"), d.Info.Properties["rows"]);
        Assert.Equal("Id", d.Columns.Single().Info!.Title);
        Assert.Equal("Key", d.Indexes.Single().Info!.Title);

        var written = DeclaredModelWriter.Yaml(d);
        var back = DeclaredModelLoader.Load(written, "models/staging/orders.yml", "staging.orders", diags)!;
        Assert.Empty(diags);
        Assert.Equal(d.Info.Title, back.Info.Title);
        Assert.Equal(d.Info.Properties.OrderBy(p => p.Key), back.Info.Properties.OrderBy(p => p.Key));
        Assert.Equal(d.Columns.Single().Info!.Properties, back.Columns.Single().Info!.Properties);
        Assert.Equal(d.Indexes.Single().Info!.Tags, back.Indexes.Single().Info!.Tags);
        Assert.Equal(written, DeclaredModelWriter.Yaml(back));                                         // stable

        Assert.DoesNotContain("title", DeclaredModelWriter.Yaml(d, withInfo: false));                   // what the hash is made of
        var plain = DeclaredModelLoader.Load("name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\nindexes:\n  - {name: ix_order, columns: [order_id]}\n", "models/staging/orders.yml", "staging.orders", diags)!;
        Assert.Equal(DeclaredModelWriter.Yaml(plain), DeclaredModelWriter.Yaml(d, withInfo: false));
    }

    [Fact]
    public void An_import_refresh_keeps_what_a_person_said_about_the_table_its_columns_and_its_indexes()
    {
        var yaml = "name: staging.orders\ntitle: Orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false, description: The key}\nindexes:\n  - {name: ix_order, columns: [order_id], tags: [pk]}\n";
        var diags = new List<Diagnostic>();
        var committed = DeclaredModelLoader.Load(yaml, "models/staging/orders.yml", "staging.orders", diags)!;
        var live = new ImportedObject("staging", "orders", ObjectKind.Table, "models/staging/orders.yml",
            [new ImportedColumn("order_id", "bigint", "BIGINT", TypeFit.Exact, "", false)], ["order_id"], [new IndexDefinition("ix_order", ["order_id"], false, [], null)], [], []);
        var refreshed = MappedModelImport.ToDescriptor(live, committed);
        Assert.Equal("Orders", refreshed.Info.Title);
        Assert.Equal("The key", refreshed.Columns.Single().Info!.Description);
        Assert.Equal(["pk"], refreshed.Indexes.Single().Info!.Tags);
    }

    [Fact]
    public void A_document_without_any_of_it_is_what_it_was_and_one_with_it_is_stored_as_rows()
    {
        var dir = LayeredProject();
        var plain = Cli("project", "show", "metadata", "ops.queue", "--project", dir, "--format", "json");
        var ctx = ProjectContext.Load(dir);
        var docs = MetadataPublisher.Collect(ctx, ["finance.revenue", "ops.queue"], null);
        var revenue = docs.Single(d => d.SubjectKind == "model" && d.Subject == "finance.revenue");
        var rows = MetadataInfoRows.From(revenue.SubjectKind, revenue.Json);
        Assert.Contains(new InfoRow("", "title", "", null, "Revenue"), rows);
        Assert.Contains(new InfoRow("", "description", "", null, "Money in"), rows);
        Assert.Contains(new InfoRow("", "tag", "finance", null, null), rows);
        Assert.Contains(new InfoRow("", "property", "tier", "number", "1"), rows);
        Assert.Contains(new InfoRow("", "property", "reviewed", "boolean", "true"), rows);
        Assert.Contains(new InfoRow("", "property", "owner", "text", "finance"), rows);
        Assert.DoesNotContain(rows, r => r.Element.StartsWith("column:", StringComparison.Ordinal));            // no column said anything

        // a model that says nothing of its own keeps only what the project files above it gave it
        var queue = docs.Single(d => d.Subject == "ops.queue");
        Assert.DoesNotContain("\"title\"", queue.Json);
        Assert.DoesNotContain("\"description\"", queue.Json);

        // a document with no words at all has no such key
        var bare = NewProjectDir();
        File.WriteAllText(Path.Combine(bare, "dbdatabuild.yml"), "defaults:\n  connections: [sqlserver]\n");
        File.WriteAllText(Path.Combine(bare, "models/marts/fct_orders.yml"), ModelWith(""));
        File.WriteAllText(Path.Combine(bare, "models/marts/fct_orders.sql"), "SELECT 1 AS order_id\n");
        foreach (var d in MetadataPublisher.Collect(ProjectContext.Load(bare), null, null))
        {
            Assert.DoesNotContain("\"title\"", d.Json);
            Assert.DoesNotContain("\"properties\"", d.Json);
            Assert.Empty(MetadataInfoRows.From(d.SubjectKind, d.Json));
        }
        Assert.Equal(0, plain.Exit);
    }

    [Fact]
    public void The_rows_of_a_project_document_name_the_connection_and_those_of_a_model_name_the_column_and_the_index()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "title: Sales\nconnections:\n  sqlserver: {title: Warehouse, tags: [prod]}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"),
            "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false, title: Id, properties: {pk: true}}\nindexes:\n  - {name: ix_order, columns: [order_id], description: By id}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1 AS order_id\n");
        var docs = MetadataPublisher.Collect(ProjectContext.Load(dir), null, null);
        var project = MetadataInfoRows.From("project", docs.Single(d => d.SubjectKind == "project").Json);
        Assert.Contains(new InfoRow("", "title", "", null, "Sales"), project);
        Assert.Contains(new InfoRow("connection:sqlserver", "title", "", null, "Warehouse"), project);
        Assert.Contains(new InfoRow("connection:sqlserver", "tag", "prod", null, null), project);
        var model = MetadataInfoRows.From("model", docs.Single(d => d.SubjectKind == "model").Json);
        Assert.Contains(new InfoRow("column:order_id", "title", "", null, "Id"), model);
        Assert.Contains(new InfoRow("column:order_id", "property", "pk", "boolean", "true"), model);
        Assert.Contains(new InfoRow("index:ix_order", "description", "", null, "By id"), model);
        Assert.Empty(MetadataInfoRows.From("plan", "{\"id\":\"x\"}"));
    }

    [Fact]
    public void A_metadata_rule_can_ask_for_the_words_the_view_has_the_rows_of_the_tracking_table()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "title: Sales\nconnections:\n  sqlserver: {title: Warehouse, tags: [prod]}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"),
            "name: marts.fct_orders\ntitle: Orders\ntags: [finance]\nproperties: {tier: 2, live: true, owner: me}\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false, title: Id}\nindexes:\n  - {name: ix_order, columns: [order_id], description: By id}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1 AS order_id\n");
        var ctx = ProjectContext.Load(dir);
        var documents = MetadataPublisher.Collect(ctx, null, null);
        using var db = DbDataBuild.Targets.DuckDb.MetadataDatabase.Open(documents.Select(d => new DbDataBuild.Targets.DuckDb.MetadataDocument(d.SubjectKind, d.Subject, d.Json, d.Hash)), "x");
        var run = db.Run("SELECT subject_kind, subject, element, item, item_key, coalesce(value_type, '-'), coalesce(value, '-') FROM metadata_info ORDER BY ALL", 100);
        Assert.Equal(DbDataBuild.Targets.DuckDb.RuleOutcome.Ran, run.Outcome);
        var rows = run.Rows.Select(r => string.Join("|", r.Values.Select(v => v?.ToString()))).ToList();
        Assert.Contains("model|marts.fct_orders||property|tier|number|2", rows);
        Assert.Contains("model|marts.fct_orders||property|live|boolean|true", rows);
        Assert.Contains("model|marts.fct_orders||property|owner|text|me", rows);
        Assert.Contains("model|marts.fct_orders||tag|finance|-|-", rows);
        Assert.Contains("model|marts.fct_orders|column:order_id|title||-|Id", rows);
        Assert.Contains("model|marts.fct_orders|index:ix_order|description||-|By id", rows);
        Assert.Contains("project|project||title||-|Sales", rows);
        Assert.Contains("project|project|connection:sqlserver|tag|prod|-|-", rows);
        var models = db.Run("SELECT title, tags FROM metadata_models", 10);
        Assert.Equal("Orders", models.Rows.Single()["title"]);
        // the rows the view shows are the rows the tracking table gets
        var stored = MetadataInfoRows.From("model", documents.Single(d => d.SubjectKind == "model").Json).Count + MetadataInfoRows.From("project", documents.Single(d => d.SubjectKind == "project").Json).Count;
        Assert.Equal(stored, rows.Count);
    }

    [Fact]
    public void The_tracking_layout_has_the_info_table_and_the_init_script_creates_it_and_records_the_layout_only_when_it_is_there()
    {
        Assert.Equal(9, TrackingSchema.Version);
        var info = TrackingSchema.Table("object_info");
        Assert.Equal(["connection", "subject_kind", "subject", "element", "item", "item_key"], info.PrimaryKey);
        foreach (var target in new[] { "sqlserver", "postgres" })
        {
            var script = TrackingDdl.Render(TrackingDdl.For(target).InitScript("dbdatabuild", "x"));
            Assert.Contains("object_info", script);
            Assert.Contains(target == "sqlserver" ? "OBJECT_ID('dbdatabuild.object_info', N'U') IS NOT NULL" : "to_regclass('dbdatabuild.object_info') IS NOT NULL", script);
        }
    }

    [Fact]
    public void A_template_says_what_it_is_in_words()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-info-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, Cli("project", "create", "starter", dir).Exit);
        var yml = File.ReadAllText(Path.Combine(dir, "dbdatabuild.yml"));
        Assert.Contains("title:", yml);
        var r = Cli("project", "show", "metadata", "--project", dir, "--format", "json");
        Assert.Equal(0, r.Exit);
        var models = JsonNode.Parse(r.Out)!["data"]!["models"]!.AsArray();
        Assert.Contains(models, m => m!["title"] != null);
        Assert.Contains(models, m => m!["properties"] != null);
    }
}
