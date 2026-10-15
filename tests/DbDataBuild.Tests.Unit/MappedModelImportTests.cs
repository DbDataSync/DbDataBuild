using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.State;
using Json.Schema;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`import`: native types to logical types, merging with committed descriptors, and source documents in the metadata. The catalog reads are covered against real engines.</summary>
public class MappedModelImportTests
{
    private static ColumnShape Col(string type, int? length = null, int? precision = null, int? scale = null, bool nullable = true, string name = "c") =>
        new(name, type, length, precision, scale, nullable, null);

    // target, native type, length, precision, scale, expected logical type ("" none), expected fit
    [Theory]
    [InlineData("sqlserver", "bigint", null, null, null, "BIGINT", "Exact")]
    [InlineData("sqlserver", "int", null, null, null, "INTEGER", "Exact")]
    [InlineData("sqlserver", "smallint", null, null, null, "SMALLINT", "Exact")]
    [InlineData("sqlserver", "tinyint", null, null, null, "SMALLINT", "Widened")]
    [InlineData("sqlserver", "bit", null, null, null, "BOOLEAN", "Exact")]
    [InlineData("sqlserver", "decimal", null, 14, 2, "DECIMAL(14, 2)", "Exact")]
    [InlineData("sqlserver", "numeric", null, 10, 0, "DECIMAL(10, 0)", "Exact")]
    [InlineData("sqlserver", "money", null, null, null, "DECIMAL(19, 4)", "Widened")]
    [InlineData("sqlserver", "smallmoney", null, null, null, "DECIMAL(10, 4)", "Widened")]
    [InlineData("sqlserver", "float", null, null, null, "DOUBLE", "Exact")]
    [InlineData("sqlserver", "real", null, null, null, "FLOAT", "Exact")]
    [InlineData("sqlserver", "date", null, null, null, "DATE", "Exact")]
    [InlineData("sqlserver", "datetime2", null, null, 6, "TIMESTAMP", "Exact")]
    [InlineData("sqlserver", "datetime2", null, null, 7, "TIMESTAMP", "Lossy")]
    [InlineData("sqlserver", "datetime", null, null, null, "TIMESTAMP", "Widened")]
    [InlineData("sqlserver", "time", null, null, 3, "TIME", "Exact")]
    [InlineData("sqlserver", "datetimeoffset", null, null, 7, "TIMESTAMP WITH TIME ZONE", "Lossy")]
    [InlineData("sqlserver", "uniqueidentifier", null, null, null, "UUID", "Exact")]
    [InlineData("sqlserver", "nvarchar", 50, null, null, "VARCHAR(50)", "Exact")]
    [InlineData("sqlserver", "varchar", 20, null, null, "VARCHAR(20)", "Exact")]
    [InlineData("sqlserver", "char", 3, null, null, "VARCHAR(3)", "Widened")]
    [InlineData("sqlserver", "nvarchar", -1, null, null, "VARCHAR", "Exact")]
    [InlineData("sqlserver", "varchar", -1, null, null, "VARCHAR", "Exact")]
    [InlineData("sqlserver", "text", null, null, null, "VARCHAR", "Exact")]
    [InlineData("sqlserver", "ntext", null, null, null, "VARCHAR", "Exact")]
    [InlineData("sqlserver", "varbinary", -1, null, null, "BLOB", "Widened")]
    [InlineData("sqlserver", "xml", null, null, null, "VARCHAR", "Lossy")]
    [InlineData("sqlserver", "geography", null, null, null, "", "None")]
    [InlineData("sqlserver", "rowversion", null, null, null, "", "None")]
    [InlineData("postgres", "bigint", null, null, null, "BIGINT", "Exact")]
    [InlineData("postgres", "integer", null, null, null, "INTEGER", "Exact")]
    [InlineData("postgres", "smallint", null, null, null, "SMALLINT", "Exact")]
    [InlineData("postgres", "boolean", null, null, null, "BOOLEAN", "Exact")]
    [InlineData("postgres", "numeric", null, 14, 2, "DECIMAL(14, 2)", "Exact")]
    [InlineData("postgres", "numeric", null, null, null, "", "None")]
    [InlineData("postgres", "numeric", null, 60, 4, "", "None")]
    [InlineData("postgres", "real", null, null, null, "FLOAT", "Exact")]
    [InlineData("postgres", "double precision", null, null, null, "DOUBLE", "Exact")]
    [InlineData("postgres", "timestamp without time zone", null, null, 6, "TIMESTAMP", "Exact")]
    [InlineData("postgres", "timestamp with time zone", null, null, 6, "TIMESTAMP WITH TIME ZONE", "Exact")]
    [InlineData("postgres", "time without time zone", null, null, 6, "TIME", "Exact")]
    [InlineData("postgres", "uuid", null, null, null, "UUID", "Exact")]
    [InlineData("postgres", "bytea", null, null, null, "BLOB", "Exact")]
    [InlineData("postgres", "character varying", 40, null, null, "VARCHAR(40)", "Exact")]
    [InlineData("postgres", "character varying", null, null, null, "VARCHAR", "Exact")]
    [InlineData("postgres", "character", 2, null, null, "VARCHAR(2)", "Widened")]
    [InlineData("postgres", "text", null, null, null, "VARCHAR", "Exact")]
    [InlineData("postgres", "jsonb", null, null, null, "VARCHAR", "Lossy")]
    [InlineData("postgres", "json", null, null, null, "VARCHAR", "Lossy")]
    [InlineData("postgres", "interval", null, null, null, "", "None")]
    [InlineData("postgres", "ARRAY", null, null, null, "", "None")]
    public void Native_types_map_to_logical_types_with_their_fit(string target, string type, int? length, int? precision, int? scale, string logical, string fit)
    {
        var t = ImportedTypes.From(target, Col(type, length, precision, scale));
        Assert.Equal(logical == "" ? null : logical, t.LogicalType);
        Assert.Equal(Enum.Parse<TypeFit>(fit), t.Fit);
        Assert.Equal(t.Fit == TypeFit.Exact, t.Reason.Length == 0);        // anything but exact says why
    }

    [Fact]
    public void Every_logical_type_a_mapping_produces_is_one_the_models_can_use()
    {
        foreach (var target in new[] { "sqlserver", "postgres" })
            foreach (var type in new[] { "bigint", "int", "integer", "smallint", "tinyint", "bit", "boolean", "float", "real", "double precision", "date", "datetime", "uniqueidentifier", "uuid", "bytea" })
                if (ImportedTypes.From(target, Col(type)).LogicalType is { } logical)
                    Assert.Equal(logical, Define.LogicalTypes.Canonical(logical));   // canonical spelling, so `define` compares it as written
    }

    private static ObjectShape Shape(string schema, string name, params ColumnShape[] columns) => new(schema, name, ObjectKind.Table, columns, []);

    [Fact]
    public void A_new_descriptor_takes_live_columns_nullability_and_the_primary_key_as_grain()
    {
        var shape = Shape("staging", "orders", Col("bigint", nullable: false, name: "order_id"), Col("decimal", precision: 14, scale: 2, name: "amount"), Col("text", name: "notes"));
        var live = MappedModelImport.Describe("sqlserver", shape, ["order_id"]);
        Assert.Equal("models/staging/orders.yml", live.File);
        var d = MappedModelImport.ToDescriptor(live, null);
        Assert.Equal("staging.orders", d.Name);
        Assert.Equal(["order_id"], d.Grain);
        Assert.Equal([("order_id", "BIGINT", false), ("amount", "DECIMAL(14, 2)", true), ("notes", "VARCHAR", true)], d.Columns.Select(c => (c.Name, c.Type, c.Nullable)));   // `text` is unlimited text: a bare VARCHAR
        Assert.Equal([MappedModelChangeKind.New], MappedModelImport.Compare(null, d).Select(c => c.Kind));
    }

    [Fact]
    public void Written_descriptors_load_back_identically_and_in_canonical_form()
    {
        var live = MappedModelImport.Describe("postgres", Shape("sales", "line items", Col("bigint", nullable: false, name: "id"), Col("character varying", 12, name: "Sku Code")), ["id"]);
        var d = MappedModelImport.ToDescriptor(live, null);
        var yaml = DeclaredModelWriter.Yaml(d);
        Assert.Equal("name: sales.line items\nkind: mapped\ngrain: [id]\ncolumns:\n  - name: id\n    type: BIGINT\n    nullable: false\n  - name: Sku Code\n    type: VARCHAR(12)\n", yaml);
        var diags = new List<Diagnostic>();
        var loaded = DeclaredModelLoader.Load(yaml, "models/sales/line items.yml", "sales.line items", diags);
        Assert.Empty(diags);
        Assert.Equal(d.Columns.Select(c => (c.Name, c.Type, c.Nullable)), loaded!.Columns.Select(c => (c.Name, c.Type, c.Nullable)));
        Assert.Equal(d.Grain, loaded.Grain);
    }

    [Fact]
    public void A_committed_grain_and_a_hand_typed_column_survive_a_refresh_and_the_rest_follows_the_table()
    {
        var committed = new DeclaredModel("staging.orders", [
            new ColumnDefinition("order_id", "INTEGER", true),         // the table says BIGINT NOT NULL now
            new ColumnDefinition("notes", "VARCHAR(500)", true),       // a length a person put on unlimited text: the same type, so kept as written
            new ColumnDefinition("shape", "VARCHAR(40)", true),        // typed by hand: the catalog cannot type a geography column
            new ColumnDefinition("legacy", "INTEGER", true),           // gone from the table
        ], ["order_id", "notes"]);
        var shape = Shape("staging", "orders", Col("bigint", nullable: false, name: "order_id"), Col("text", name: "notes"), Col("date", name: "placed"), Col("geography", name: "shape"));
        var live = MappedModelImport.Describe("sqlserver", shape, ["order_id"]);
        var d = MappedModelImport.ToDescriptor(live, committed);

        Assert.Equal(["order_id", "notes"], d.Grain);                                                  // human knowledge wins over the primary key
        Assert.Equal([("order_id", "BIGINT", false), ("notes", "VARCHAR(500)", true), ("placed", "DATE", true), ("shape", "VARCHAR(40)", true)], d.Columns.Select(c => (c.Name, c.Type, c.Nullable)));
        var changes = MappedModelImport.Compare(committed, d).Select(c => (c.Kind, c.Column)).ToList();
        Assert.Contains((MappedModelChangeKind.TypeChanged, "order_id"), changes);
        Assert.Contains((MappedModelChangeKind.NullabilityChanged, "order_id"), changes);
        Assert.Contains((MappedModelChangeKind.ColumnAdded, "placed"), changes);
        Assert.Contains((MappedModelChangeKind.ColumnRemoved, "legacy"), changes);
        Assert.DoesNotContain(changes, c => c.Column is "notes" or "shape");
        Assert.Empty(MappedModelImport.Compare(d, d));
    }

    private static ObjectShape WithIndexes(params PhysicalItem[] physical) =>
        new("sales", "orders", ObjectKind.Table, [Col("bigint", nullable: false, name: "id"), Col("int", name: "customer_id"), Col("date", name: "placed"), Col("text", name: "notes"), Col("geography", name: "shape")], physical);

    [Fact]
    public void Indexes_and_foreign_keys_are_exported_without_the_primary_keys_own_index_or_what_cannot_be_described()
    {
        var shape = WithIndexes(
            new("constraint_index", "PK_orders", IndexText.Canonical(true, ["id"], [])),
            new("index", "IX_orders_customer", IndexText.Canonical(false, ["customer_id", "placed desc"], ["notes"])),
            new("constraint_index", "UQ_orders_placed", IndexText.Canonical(true, ["placed"], [])),
            new("index", "IX_lower", IndexText.Canonical(false, ["<expression>"], [])),
            new("index", "IX_shape", IndexText.Canonical(false, ["shape"], [])),
            new("compression", "IX_orders_customer", "NONE partitions=1"));
        var live = MappedModelImport.Describe("sqlserver", shape, ["id"], [
            new ForeignKeyShape("FK_orders_customer", ["customer_id"], "sales", "customers", ["id"]),
            new ForeignKeyShape("FK_orders_shape", ["shape"], "sales", "shapes", ["id"]),
        ]);
        var d = MappedModelImport.ToDescriptor(live, null);
        Assert.Equal([("IX_orders_customer", "customer_id,placed", false, "notes"), ("UQ_orders_placed", "placed", true, "")], d.Indexes.Select(i => (i.Name, string.Join(",", i.Columns), i.Unique, string.Join(",", i.Include))));
        Assert.Equal([("FK_orders_customer", "sales.customers")], d.ForeignKeys.Select(f => (f.Name, f.Table)));
        Assert.Equal(3, live.Notes!.Count);                                                        // the expression index, the index and the foreign key on the geography column
        Assert.Contains(live.Notes, n => n.Contains("IX_lower") && n.Contains("expression"));

        var yaml = DeclaredModelWriter.Yaml(d);
        Assert.EndsWith("indexes:\n  - {name: IX_orders_customer, columns: [customer_id, placed], include: [notes]}\n  - {name: UQ_orders_placed, columns: [placed], unique: true}\n" +
                        "foreign_keys:\n  - {name: FK_orders_customer, columns: [customer_id], references: {table: sales.customers, columns: [id]}}\n", yaml);
        var diags = new List<Diagnostic>();
        var loaded = DeclaredModelLoader.Load(yaml, "models/sales/orders.yml", "sales.orders", diags)!;
        Assert.Empty(diags);
        Assert.Equal(d.Indexes.Select(i => (i.Name, i.Unique)), loaded.Indexes.Select(i => (i.Name, i.Unique)));
        Assert.Equal(d.ForeignKeys.Select(f => (f.Name, f.Table)), loaded.ForeignKeys.Select(f => (f.Name, f.Table)));
        Assert.Empty(MappedModelImport.Compare(loaded, d));                                              // what was written is in sync
    }

    [Fact]
    public void A_changed_added_or_dropped_index_or_foreign_key_is_a_difference()
    {
        var committed = new DeclaredModel("sales.orders", [new ColumnDefinition("a", "INTEGER"), new ColumnDefinition("b", "INTEGER")], [],
            [new IndexDefinition("ix_a", ["a"], false, [], null), new IndexDefinition("ix_gone", ["b"], false, [], null), new IndexDefinition("ix_same", ["a", "b"], true, [], null)],
            [new ForeignKeyDefinition("fk_a", ["a"], "s.t", ["id"]), new ForeignKeyDefinition("fk_gone", ["b"], "s.t", ["id"])]);
        var live = new DeclaredModel("sales.orders", committed.Columns, [],
            [new IndexDefinition("ix_a", ["a"], true, [], null), new IndexDefinition("IX_SAME", ["a", "b"], true, [], null), new IndexDefinition("ix_new", ["b"], false, ["a"], null)],
            [new ForeignKeyDefinition("fk_a", ["a"], "s.t2", ["id"]), new ForeignKeyDefinition("fk_new", ["b"], "s.t", ["id"])]);
        var changes = MappedModelImport.Compare(committed, live).Select(c => (c.Kind, c.Column)).ToList();
        Assert.Equal([(MappedModelChangeKind.IndexChanged, "ix_a"), (MappedModelChangeKind.IndexAdded, "ix_new"), (MappedModelChangeKind.IndexRemoved, "ix_gone"),
                      (MappedModelChangeKind.ForeignKeyChanged, "fk_a"), (MappedModelChangeKind.ForeignKeyAdded, "fk_new"), (MappedModelChangeKind.ForeignKeyRemoved, "fk_gone")], changes);
        Assert.Empty(MappedModelImport.Compare(live, live));
    }

    [Fact]
    public void Synonyms_and_column_order_are_not_differences()
    {
        var committed = new DeclaredModel("s.t", [new ColumnDefinition("b", "INT"), new ColumnDefinition("a", "NUMERIC(10,2)")], []);
        var live = new DeclaredModel("s.t", [new ColumnDefinition("a", "DECIMAL(10, 2)"), new ColumnDefinition("B", "INTEGER")], []);
        Assert.Empty(MappedModelImport.Compare(committed, live));
    }

    [Fact]
    public void Names_that_cannot_be_paths_are_not_importable()
    {
        Assert.Null(DeclaredModelWriter.PathFor("sales", "a.b"));
        Assert.Null(DeclaredModelWriter.PathFor("s/x", "t"));
        Assert.Null(MappedModelImport.Describe("postgres", Shape("sales", "a.b", Col("bigint")), null).File);
        Assert.Equal("models/sales/orders.yml", DeclaredModelWriter.PathFor("sales", "orders"));
    }

    // ---- the command, without a database ----

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, environment: _ => null);
        return (exit, o.ToString(), e.ToString());
    }

    private static string Project(bool withSource)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        if (withSource)
        {
            Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
            File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
        }
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), "name: marts.fct_orders\nkind: view\nconnections: [sqlserver]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id FROM staging.orders o\n");
        return dir;
    }

    [Fact]
    public void Import_needs_the_read_login_and_changes_nothing_without_it()
    {
        var dir = Project(withSource: true);
        var before = Snapshot(dir);
        var (exit, _, err) = Run("project", "import", "--project", dir, "--write");
        Assert.Equal(1, exit);
        Assert.Contains("DDB-501", err);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Import_without_patterns_and_without_descriptors_asks_for_a_pattern()
    {
        var (exit, _, err) = Run("project", "import", "--project", Project(withSource: false));
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("no mapped models yet", err);
    }

    [Fact]
    public void Import_refuses_check_with_write_and_a_pattern_without_a_schema()
    {
        var dir = Project(withSource: true);
        Assert.Equal(CliApp.ExitUsage, Run("project", "import", "--project", dir, "--check", "--write").Exit);
        var (exit, _, err) = Run("project", "import", "--project", dir, "orders");
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("not `schema_name.table_name`", err);
    }

    // ---- declared models in the metadata ----

    [Fact]
    public void Metadata_has_one_document_per_model_and_a_mapped_model_is_one_of_them()
    {
        var dir = Project(withSource: true);
        var o = new StringWriter();
        Assert.Equal(0, CliApp.Run(["project", "show", "metadata", "--project", dir, "--format", "json"], o, new StringWriter(), environment: _ => null));
        var doc = JsonNode.Parse(o.ToString())!;
        var metadata = SchemaConformanceTests.LoadSchema("metadata");
        bool Valid(JsonNode? n) => metadata.Evaluate(JsonSerializer.SerializeToNode(n), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

        Assert.Null(doc["data"]!["sources"]);                                                      // one array of models, of every kind
        var models = doc["data"]!["models"]!.AsArray();
        Assert.Equal(["marts.fct_orders", "staging.orders"], models.Select(m => (string)m!["name"]!));
        var mapped = models.Single(m => (string?)m!["name"] == "staging.orders")!;
        Assert.True(Valid(mapped), mapped.ToJsonString());
        Assert.Equal("dbdatabuild.model/3", (string?)mapped["schema"]);
        Assert.Equal("mapped", (string?)mapped["kind"]);
        Assert.Empty(mapped["kind_settings"]!.AsObject());
        Assert.Equal("models/staging/orders.yml", (string?)mapped["files"]!["definition"]);
        Assert.Null(mapped["files"]!["query"]);
        Assert.Null(mapped["loads"]);                                                              // a declared model has no loads, hooks or lineage
        Assert.Equal(["order_id"], mapped["grain"]!.AsArray().Select(x => (string)x!));
        Assert.NotNull(mapped["columns"]![0]!["native"]);                                          // columns carry their native types like a built model's
        var projectModels = doc["data"]!["project"]!["models"]!.AsArray();
        Assert.Equal(("mapped", "models/staging/orders.yml"), (projectModels.Single(m => (string?)m!["name"] == "staging.orders")!["kind"]!.ToString(), projectModels.Single(m => (string?)m!["name"] == "staging.orders")!["file"]!.ToString()));
        Assert.Null(doc["data"]!["project"]!["sources"]);
        var broken = JsonNode.Parse(mapped.ToJsonString())!;
        broken["schema"] = "dbdatabuild.source/2";
        Assert.False(Valid(broken));
    }

    [Fact]
    public void Publishing_includes_the_declared_models_a_selected_model_reads_and_their_documents_do_not_change()
    {
        var dir = Project(withSource: true);
        var ctx = ProjectContext.Load(dir);
        var all = MetadataPublisher.Collect(ctx, null, null);
        Assert.Equal(["project", "model", "model"], all.Select(d => d.SubjectKind));
        var one = MetadataPublisher.Collect(ctx, ["marts.fct_orders"], null);
        Assert.Equal(all.Single(d => d.Subject == "staging.orders").Hash, one.Single(d => d.Subject == "staging.orders").Hash);
        Assert.DoesNotContain(MetadataPublisher.Collect(ctx, [], null), d => d.Subject == "staging.orders");   // no model selected, nothing it reads
        Assert.Equal("mapped", MetadataDependencies.ModelKindOf("model", all.Single(d => d.Subject == "staging.orders").Json));
    }
}
