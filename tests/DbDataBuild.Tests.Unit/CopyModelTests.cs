using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>A copy: a table filled with the rows of another model on another connection. Declared here, resolved and rendered; the rows move at apply.</summary>
public class CopyModelTests
{
    private const string Config = "connections:\n  crm: { engine: postgres }\n  wh: { engine: sqlserver }\n  lake: { engine: postgres }\ndefaults:\n  connections: [wh]\n";
    private const string Customers = "name: crm.customers\nkind: mapped\nconnections=: [crm]\ngrain: [customer_id]\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n  - {name: name, type: \"VARCHAR(50)\"}\n  - {name: born, type: DATE}\n";

    private static string Project(string config = Config)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        Write(dir, "models/crm/customers.yml", Customers);
        return dir;
    }

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static void Copy(string dir, string name, string from, string extra = "")
    {
        Write(dir, "models/" + name.Replace('.', '/') + ".yml", $"name: {name}\nkind: {{copy: {{from: {from}}}}}\n{extra}");
    }

    private static (ProjectValidationResult Result, string Text) Validate(string dir)
    {
        var result = ProjectValidator.Validate(dir);
        return (result, string.Join("\n", result.Diagnostics.Select(DiagnosticFormatter.Format)));
    }

    [Fact]
    public void A_copy_has_the_columns_and_grain_of_its_origin_and_a_generated_query_over_its_staging_table()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var copy = result.BuiltModels.Single();
        Assert.Equal((ModelKinds.Copy, "crm.customers"), (copy.Definition.Kind, copy.Definition.From));
        Assert.Equal(["customer_id", "name", "born"], copy.Definition.Columns.Select(c => c.Name));
        Assert.Equal(["customer_id"], copy.Definition.Grain);
        Assert.False(copy.Definition.Columns[0].Nullable);
        Assert.Equal("models/warehouse/customers.yml", copy.QueryFile);                              // no .sql: diagnostics point at the definition
        Assert.Equal("SELECT \"customer_id\", \"name\", \"born\" FROM \"dbdatabuild\".\"stg_warehouse__customers\"\n", copy.GeneratedQuery);
        // the staging table is declared to the project for binding, and is not one of its mapped models
        Assert.Equal(["crm.customers"], result.MappedModels.Select(d => d.Name));
        Assert.Contains(result.DeclaredModelsAndStaging, d => d.Name == "dbdatabuild.stg_warehouse__customers" && d.IsGenerated);
    }

    [Fact]
    public void A_copy_renders_a_full_replace_from_its_staging_table_for_its_connection()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers", "connections=: [wh]\n");
        var o = new StringWriter(); var e = new StringWriter();
        Assert.True(CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null) == 0, o + "\n" + e);
        var script = File.ReadAllText(Path.Combine(dir, "rendered/wh/warehouse.customers/load.default.sql"));
        Assert.Contains("FROM dbdatabuild.stg_warehouse__customers", script);                          // reads the staging table, in the destination's dialect
        Assert.Contains("-- model:           warehouse.customers", script);
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir, "rendered")).Where(d => !d.EndsWith("wh") && !d.EndsWith("lowered")));
        Assert.Equal(0, CliApp.Run(["project", "compile", "--check", "--project", dir], new StringWriter(), new StringWriter(), environment: _ => null));
    }

    [Fact]
    public void A_copy_of_a_copy_and_a_copy_of_a_model_that_is_built_resolve()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers");                                              // on wh
        Copy(dir, "lake.customers", "warehouse.customers", "connections=: [lake]\n");                    // on lake, from the copy on wh
        Write(dir, "models/crm/recent.yml", "name: crm.recent\nkind: view\nconnections=: [crm]\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n");
        Write(dir, "models/crm/recent.sql", "SELECT customer_id FROM crm.customers\n");
        Copy(dir, "lake.recent", "crm.recent", "connections=: [lake]\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        Assert.Equal(["customer_id", "name", "born"], result.BuiltModels.Single(s => s.Definition.Name == "lake.customers").Definition.Columns.Select(c => c.Name));
        Assert.Equal(["customer_id"], result.BuiltModels.Single(s => s.Definition.Name == "lake.recent").Definition.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Model_update_leaves_a_copy_alone_since_it_has_no_query_and_its_columns_are_its_origins()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers");
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "model", "update", "--check", "--project", dir], o, e, environment: _ => null);
        Assert.True(exit == 0, o + "\n" + e);                                                          // not DDB-108, "has no query file"
        Assert.DoesNotContain("DDB-108", o.ToString() + e.ToString());
    }

    [Theory]
    [InlineData("crm.nothing", "", "which is not a model, a mapped model or a copy")]
    [InlineData("crm.customers", "columns:\n  - {name: a, type: INT}\n", "has no `columns`")]
    [InlineData("a", "", "DDB-106")]
    public void A_copy_that_cannot_be_resolved_says_why(string from, string extra, string expected)
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", from, extra);
        var (result, text) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Contains(expected, text);
    }

    // ---- fan-in: one application on several connections ----

    private const string Stores = "connections:\n  crm: { engine: postgres }\n  store_17: { engine: postgres, parameters: { store_id: \"017\", region: eu } }\n  store_18: { engine: postgres, parameters: { store_id: \"018\", region: us } }\n  wh: { engine: sqlserver, parameters: { region: eu } }\ndefaults:\n  connections: [wh]\n";

    private static string StoreProject(string orders = "")
    {
        var dir = Project(Stores);
        Write(dir, "models/pos/orders.yml", "name: pos.orders\nkind: mapped\nconnections=: [store_17, store_18]\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: total, type: \"DECIMAL(10, 2)\"}\n" + orders);
        return dir;
    }

    [Fact]
    public void A_copy_from_several_connections_names_the_slice_that_keeps_each_ones_rows_apart()
    {
        var dir = StoreProject();
        Copy(dir, "warehouse.orders", "pos.orders", "");
        Write(dir, "models/warehouse/orders.yml", "name: warehouse.orders\nkind:\n  copy:\n    from: pos.orders\n    slice: {column: store_id, value: \"${origin.store_id}\", type: \"VARCHAR(10)\"}\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var copy = result.BuiltModels.Single(s => s.Definition.Name == "warehouse.orders").Definition;
        Assert.Equal(["order_id", "total", "store_id"], copy.Columns.Select(c => c.Name));              // the origin has no such column, so the copy adds it
        Assert.True(copy.SliceColumnAdded);
        Assert.Equal("VARCHAR(10)", copy.Columns[2].Type);
        Assert.False(copy.Columns[2].Nullable);
        Assert.Equal(["order_id", "store_id"], copy.Grain);                                              // the key has to include the slice: two stores may use the same order number
        var load = Assert.Single(LoadPlan.For(copy, "wh"));
        Assert.Equal($"{LoadStrategies.DeleteInsertByKey} store_id", $"{load.Strategy} {string.Join(",", load.Key)}");     // a run replaces the rows of the origin it loads, and no others
    }

    [Fact]
    public void A_slice_on_a_column_the_origin_already_has_adds_nothing()
    {
        var dir = StoreProject("  - {name: store_code, type: \"VARCHAR(10)\"}\n");
        Write(dir, "models/warehouse/orders.yml", "name: warehouse.orders\nkind:\n  copy:\n    from: pos.orders\n    slice: {column: store_code, value: \"${origin.store_id}\"}\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var copy = result.BuiltModels.Single(s => s.Definition.Name == "warehouse.orders").Definition;
        Assert.False(copy.SliceColumnAdded);
        Assert.Equal(["order_id", "total", "store_code"], copy.Columns.Select(c => c.Name));
    }

    [Theory]
    [InlineData("", "needs `slice`")]                                                                                                                       // several origins, nothing to tell their rows apart
    [InlineData("    slice: {column: store_id, value: \"fixed\", type: \"VARCHAR(10)\"}\n", "is the same for every origin")]
    [InlineData("    slice: {column: store_id, value: \"${origin.nothing}\", type: \"VARCHAR(10)\"}\n", "has no parameter `nothing`")]
    [InlineData("    slice: {column: store_id, value: \"${origin.store_id}\"}\n", "needs a `type`")]
    [InlineData("    slice: {column: store_id, value: \"${nothing.store_id}\", type: \"VARCHAR(10)\"}\n", "is not a parameter scope")]
    [InlineData("    on_mismatch: maybe\n    slice: {column: store_id, value: \"${origin.store_id}\", type: \"VARCHAR(10)\"}\n", "`fail` or `skip`")]
    public void A_copy_from_several_connections_that_cannot_work_says_why(string kindExtra, string expected)
    {
        var dir = StoreProject();
        Write(dir, "models/warehouse/orders.yml", "name: warehouse.orders\nkind:\n  copy:\n    from: pos.orders\n" + kindExtra);
        var (result, text) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void A_connection_parameter_is_read_and_checked()
    {
        var cfg = ProjectConfigLoader.Load(Stores, "dbdatabuild.yml", [])!;
        Assert.Equal("017", cfg.Connections["store_17"].Parameters["store_id"].Value);
        Assert.Empty(cfg.Connections["sqlserver"].Parameters);
        foreach (var bad in new[] { "connections:\n  a: { engine: postgres, parameters: [x] }\n", "connections:\n  a: { engine: postgres, parameters: { Bad Name: 1 } }\n", "connections:\n  a: { engine: postgres, parameters: { x: [1] } }\n" })
        {
            var diags = new List<Diagnostic>();
            Assert.Null(ProjectConfigLoader.Load(bad, "dbdatabuild.yml", diags));
            Assert.Contains(diags, d => d.Code == "DDB-106");
        }
    }

    [Fact]
    public void Copies_that_copy_each_other_are_refused_not_looped_on()
    {
        var dir = Project();
        Copy(dir, "warehouse.a", "warehouse.b", "connections=: [wh]\n");
        Copy(dir, "warehouse.b", "warehouse.a", "connections=: [lake]\n");
        var (result, text) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Contains("copies itself, through the others", text);
    }

    [Fact]
    public void A_query_beside_a_copy_is_refused()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers");
        Write(dir, "models/warehouse/customers.sql", "SELECT 1\n");
        Assert.Contains("has no query of its own", Validate(dir).Text);
    }

    [Fact]
    public void The_staging_name_is_stable_and_short_enough_for_every_engine()
    {
        Assert.Equal("stg_warehouse__customers", CopyModels.StagingTable("warehouse.customers"));
        var longName = "warehouse." + new string('x', 80);
        var staged = CopyModels.StagingTable(longName);
        Assert.True(staged.Length <= 63);
        Assert.Equal(staged, CopyModels.StagingTable(longName));                                       // deterministic: an interrupted run is run again on the same table
        Assert.NotEqual(staged, CopyModels.StagingTable("warehouse." + new string('x', 79) + "y"));
    }

    // ---- a query runs on one connection ----

    private static (int Exit, string Text) CliValidate(string dir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null);
        return (exit, o + "\n" + e);
    }

    [Fact]
    public void A_model_that_reads_a_table_on_another_connection_is_refused_until_the_table_is_copied_there()
    {
        var dir = Project();
        Write(dir, "models/marts/local.yml", "name: marts.local\nkind: view\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n");
        Write(dir, "models/marts/local.sql", "SELECT customer_id FROM crm.customers\n");
        var (exit, text) = CliValidate(dir);
        Assert.NotEqual(0, exit);
        Assert.Contains("DDB-231", text);
        Assert.Contains("marts.local is built on `wh` and reads `crm.customers`, which is on `crm`, not on `wh`", text);

        Copy(dir, "staging.customers", "crm.customers");                                                     // the copy is on wh (the default)
        Write(dir, "models/marts/local.sql", "SELECT customer_id FROM staging.customers\n");
        var fixedUp = CliValidate(dir);
        Assert.True(fixedUp.Exit == 0, fixedUp.Text);
        Assert.DoesNotContain("DDB-231", fixedUp.Text);
    }

    [Fact]
    public void A_model_may_read_a_copy_built_on_another_default_connection_and_models_on_the_connection_of_the_table_are_fine()
    {
        var dir = Project(Config + "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C, sqlserver: Latin1_General_100_BIN2 }\n");
        Write(dir, "models/crm/recent.yml", "name: crm.recent\nkind: view\nconnections=: [crm]\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n");
        Write(dir, "models/crm/recent.sql", "SELECT customer_id FROM crm.customers\n");                    // on crm, where crm.customers is
        Copy(dir, "lake.customers", "crm.customers", "connections=: [lake]\n");
        Write(dir, "models/lake/summary.yml", "name: lake.summary\nkind: view\nconnections=: [lake]\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n");
        Write(dir, "models/lake/summary.sql", "SELECT customer_id FROM lake.customers\n");                  // the copy's own connection
        var (exit, text) = CliValidate(dir);
        Assert.True(exit == 0, text);
    }

    // ---- incremental copies ----

    [Fact]
    public void An_incremental_copy_names_a_unique_key_and_a_watermark_column_and_loads_by_key()
    {
        var dir = Project();
        Write(dir, "models/warehouse/customers.yml", "name: warehouse.customers\nkind:\n  copy:\n    from: crm.customers\n    unique_key: [customer_id]\n    watermark: {column: born, lookback: 3 days}\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var copy = result.BuiltModels.Single().Definition;
        Assert.Equal(("born", "3 days"), (copy.Watermark!.Column, copy.Watermark.Lookback));
        var load = Assert.Single(LoadPlan.For(copy, "wh"));
        Assert.Equal($"{LoadStrategies.DeleteInsertByKey} customer_id", $"{load.Strategy} {string.Join(",", load.Key)}");
    }

    [Theory]
    [InlineData("    watermark: {column: born}\n", "needs a `unique_key`")]
    [InlineData("    unique_key: [customer_id]\n    watermark: {column: nowhere}\n", "watermark.column refers to `nowhere`")]
    [InlineData("    unique_key: [ghost]\n    watermark: {column: born}\n", "unique_key refers to `ghost`")]
    [InlineData("    unique_key: [customer_id]\n    watermark: {column: born, lookback: soon}\n", "lookback is `soon`")]
    [InlineData("    unique_key: [customer_id]\n    watermark: [born]\n", "`watermark` is a mapping")]
    public void An_incremental_copy_that_cannot_work_says_why(string kindExtra, string expected)
    {
        var dir = Project();
        Write(dir, "models/warehouse/customers.yml", "name: warehouse.customers\nkind:\n  copy:\n    from: crm.customers\n" + kindExtra);
        var (result, text) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void A_sliced_incremental_copy_loads_by_its_key_and_the_slice()
    {
        var dir = StoreProject();
        Write(dir, "models/warehouse/orders.yml", "name: warehouse.orders\nkind:\n  copy:\n    from: pos.orders\n    unique_key: [order_id]\n    watermark: {column: order_id}\n    slice: {column: store_id, value: \"${origin.store_id}\", type: \"VARCHAR(10)\"}\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var load = Assert.Single(LoadPlan.For(result.BuiltModels.Single(s => s.Definition.Name == "warehouse.orders").Definition, "wh"));
        Assert.Equal(["order_id", "store_id"], load.Key);                                                 // two stores may use one order number
    }

    [Fact]
    public void A_sliced_copy_can_declare_a_full_load_that_replaces_everything_of_an_origin_and_reads_it_in_full()
    {
        var dir = StoreProject();
        Write(dir, "models/warehouse/orders.yml", "name: warehouse.orders\nkind:\n  copy:\n    from: pos.orders\n    unique_key: [order_id]\n    watermark: {column: order_id}\n    slice: {column: store_id, value: \"${origin.store_id}\", type: \"VARCHAR(10)\"}\n"
            + "loads:\n  default: {default: true, strategy: delete_insert_by_key}\n  full: {strategy: full_replace}\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var def = result.BuiltModels.Single(s => s.Definition.Name == "warehouse.orders").Definition;
        var loads = LoadPlan.For(def, "wh");
        var routine = loads.Single(l => l.Name == "default");
        Assert.Equal(["order_id", "store_id"], routine.Key);                                              // the key a copy defaults to leads to the slice, as the implicit load does
        Assert.False(routine.ReadsAll);
        var full = loads.Single(l => l.Name == "full");
        Assert.Equal(($"{LoadStrategies.DeleteInsertByKey} store_id", true), ($"{full.Strategy} {string.Join(",", full.Key)}", full.ReadsAll));   // the origin's rows only: another origin's stay
    }

    [Fact]
    public void A_copy_without_a_slice_replaces_the_whole_table_with_its_full_load()
    {
        var dir = Project();
        Write(dir, "models/warehouse/customers.yml", "name: warehouse.customers\nkind:\n  copy:\n    from: crm.customers\n    unique_key: [customer_id]\n    watermark: {column: born}\nloads:\n  default: {default: true, strategy: delete_insert_by_key}\n  full: {strategy: full_replace}\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var full = LoadPlan.For(result.BuiltModels.Single().Definition, "wh").Single(l => l.Name == "full");
        Assert.Equal((LoadStrategies.FullReplace, true), (full.Strategy, full.ReadsAll));
    }

    // ---- local copies: a copy on the origin's own connection ----

    [Fact]
    public void A_copy_on_its_origins_own_connection_is_a_local_copy_an_ordinary_load_with_no_staging()
    {
        var dir = Project();
        Copy(dir, "crm.snapshot", "crm.customers", "connections=: [crm]\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var copy = result.BuiltModels.Single();
        Assert.True(copy.Definition.LocalCopy);
        Assert.Equal("SELECT \"customer_id\", \"name\", \"born\" FROM \"crm\".\"customers\"\n", copy.GeneratedQuery);      // reads the origin by name
        Assert.DoesNotContain(result.DeclaredModelsAndStaging, d => d.IsGenerated);                                                  // no staging table
    }

    [Fact]
    public void A_local_copy_renders_a_full_replace_that_reads_the_origin_and_plans_no_transfer()
    {
        var dir = Project(Config + "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C, sqlserver: Latin1_General_100_BIN2 }\n");
        Copy(dir, "crm.snapshot", "crm.customers", "connections=: [crm]\n");
        var o = new StringWriter(); var e = new StringWriter();
        Assert.True(CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null) == 0, o + "\n" + e);
        var script = File.ReadAllText(Path.Combine(dir, "rendered/crm/crm.snapshot/load.default.sql"));
        Assert.Contains("FROM crm.customers", script);
    }

    [Theory]
    [InlineData("connections=: [crm, wh]\n", "some of them are connections of `crm.customers`")]
    public void A_copy_cannot_be_local_and_remote_at_once(string extra, string expected)
    {
        var dir = Project();
        Copy(dir, "crm.snapshot", "crm.customers", extra);
        var (result, text) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void A_local_copy_is_a_full_replace_so_incremental_options_are_refused_with_the_reason()
    {
        var dir = Project();
        Write(dir, "models/crm/snapshot.yml", "name: crm.snapshot\nkind:\n  copy:\n    from: crm.customers\n    unique_key: [customer_id]\nconnections=: [crm]\n");
        var (result, text) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Contains("a local copy is a full replace", text);
    }
}
