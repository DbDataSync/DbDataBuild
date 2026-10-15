using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Macros and types (`macros/`): DuckDB statements the tool loads on demand, expanded by DuckDB while it lowers a query, so the SQL of each model is written for the table the macro was given.</summary>
public class MacroTests
{
    private const string Snapshots = """
        -- a live table or a snapshot of it: the rows with the date they are as of; col names the snapshot date column, NULL for a live table
        CREATE MACRO snapshot_at(tbl, col) AS TABLE
          SELECT * EXCLUDE (__none), CASE WHEN col IS NULL THEN current_date ELSE COLUMNS(lambda c: c = coalesce(col, '__none')) END AS as_of_date
          FROM (SELECT *, NULL::DATE AS __none FROM query_table(tbl));

        CREATE MACRO status_totals(tbl, col) AS TABLE
          SELECT as_of_date, status, sum(amount) AS total FROM snapshot_at(tbl, col) GROUP BY as_of_date, status;
        """;

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Project(string macros = Snapshots, string? config = null)
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config ?? "defaults: {connections: [sqlserver]}\ntracking: none\n");
        Write(dir, "models/src/orders.yml", "name: src.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n");
        Write(dir, "models/src/orders_snap.yml", "name: src.orders_snap\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: snap_date, type: DATE, nullable: false}\n");
        const string cols = "grain: [as_of_date, status]\ncolumns:\n  - {name: as_of_date, type: DATE, nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: total, type: \"DECIMAL(38,2)\", nullable: false}\n";
        Write(dir, "models/marts/live_totals.yml", "name: marts.live_totals\nkind: full\n" + cols);
        Write(dir, "models/marts/live_totals.sql", "SELECT * FROM status_totals('src.orders', NULL)\n");
        Write(dir, "models/marts/snap_totals.yml", "name: marts.snap_totals\nkind: full\n" + cols);
        Write(dir, "models/marts/snap_totals.sql", "SELECT * FROM status_totals('src.orders_snap', 'snap_date')\n");
        if (macros.Length > 0) Write(dir, "macros/snapshots.sql", macros);
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: _ => null), o.ToString(), e.ToString());
    }

    private static string Lowered(string dir, string model) => File.ReadAllText(Path.Combine(dir, "rendered", "lowered", model, "lowered.sql"));

    // ---- the files ----

    [Fact]
    public void A_file_holds_several_statements_and_a_semicolon_in_text_or_a_comment_does_not_end_one()
    {
        var statements = MacroLibrary.SplitStatements("""
            CREATE MACRO a(x) AS x || ';';   -- not the end; here
            /* nor ; this */
            CREATE MACRO b(x) AS $$a;b$$ || x;
            CREATE TYPE mood AS ENUM ('a;b', 'c')
            """);
        Assert.Equal(["CREATE MACRO a(x) AS x || ';'", "CREATE MACRO b(x) AS $$a;b$$ || x", "CREATE TYPE mood AS ENUM ('a;b', 'c')"], statements.Select(s => s.Sql.Replace("   -- not the end; here", "").Trim()));
        Assert.Equal([1, 3, 4], statements.Select(s => s.Line));
    }

    [Fact]
    public void Only_macros_and_types_may_be_in_a_macro_file()
    {
        var diags = new List<Diagnostic>();
        var found = MacroLibrary.Parse("macros/x.sql", "CREATE MACRO a(x) AS x; CREATE TABLE t(a INT); INSERT INTO t VALUES (1); INSTALL httpfs; CREATE TYPE m AS ENUM ('a')", diags);
        Assert.Equal(["a", "m"], found.Select(f => f.Name));
        Assert.Equal(3, diags.Count);
        Assert.All(diags, d => Assert.Contains("is not allowed in a macro file", d.Found));
    }

    [Fact]
    public void A_macro_defined_twice_and_macros_that_call_each_other_in_a_circle_are_refused()
    {
        var dir = Project("CREATE MACRO a(x) AS x;\nCREATE MACRO a(y) AS y + 1;\nCREATE MACRO p(x) AS q(x);\nCREATE MACRO q(x) AS p(x);\nCREATE MACRO s(x) AS s(x);");
        var text = string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Select(d => d.Found));
        Assert.Contains("`a` is defined twice", text);
        Assert.Contains("circle: p -> q -> p", text);
        Assert.Contains("circle: s -> s", text);
    }

    [Fact]
    public void A_binding_gets_only_the_macros_its_queries_reach_with_callees_first_and_types_before_both()
    {
        var library = MacroLibrary.Load(Project("""
            CREATE MACRO outer_one(x) AS inner_one(x) + 1;
            CREATE MACRO inner_one(x) AS x * 2;
            CREATE MACRO unrelated(x) AS nowhere.missing(x);
            CREATE TYPE mood AS ENUM ('sad', 'happy');
            CREATE MACRO feeling(x) AS CAST(x AS mood);
            """), []);
        var prelude = library.PreludeFor(["SELECT outer_one(1), feeling('sad')"]);
        Assert.Equal(["CREATE TYPE mood AS ENUM ('sad', 'happy')"], prelude.Types);
        Assert.Equal(["CREATE MACRO inner_one(x) AS x * 2", "CREATE MACRO outer_one(x) AS inner_one(x) + 1", "CREATE MACRO feeling(x) AS CAST(x AS mood)"], prelude.Macros);   // `unrelated` is not created
        Assert.True(library.PreludeFor(["SELECT 1"]).IsEmpty);
    }

    // ---- lowering ----

    [Fact]
    public void A_macro_expands_to_the_query_for_the_table_it_was_given_with_nothing_left_for_the_engine_to_decide()
    {
        var dir = Project();
        var render = Cli("project", "compile", "--project", dir);
        Assert.True(render.Exit == 0, render.Out + render.Err);
        var snap = Lowered(dir, "marts.snap_totals");
        Assert.Contains("SELECT snap_date AS as_of_date, status, sum(amount) AS total\nFROM src.orders_snap\nGROUP BY snap_date, status", snap);
        var live = Lowered(dir, "marts.live_totals");
        Assert.Contains("SELECT current_date() AS as_of_date, status, sum(amount) AS total\nFROM src.orders\nGROUP BY status", live);
        foreach (var text in new[] { snap, live }) { Assert.DoesNotContain("CASE", text); Assert.DoesNotContain("UNION", text); }
        Assert.Contains("FROM src.orders_snap", File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "marts.snap_totals", "load.default.sql")));
    }

    [Fact]
    public void Dependencies_show_the_tables_the_macro_reads_and_the_macros_themselves()
    {
        var dir = Project();
        var ctx = ProjectContext.Load(dir);
        Assert.Equal(["src.orders_snap"], ctx.BaseTablesOf(ctx.Project.BuiltModels.Single(s => s.Definition.Name == "marts.snap_totals"), "SELECT * FROM status_totals('src.orders_snap', 'snap_date')"));
        Assert.Equal(["src.orders_snap", "status_totals()"], ctx.Graph.Reads("marts.snap_totals").Order(StringComparer.Ordinal));
        Assert.Equal(["snapshot_at()"], ctx.Graph.Reads("status_totals()"));
        Assert.Contains("src.orders", ctx.Graph.Ancestors("marts.live_totals").Keys);
        Assert.Contains("snapshot_at()", ctx.Graph.Ancestors("marts.live_totals").Keys);
        Assert.Contains("marts.live_totals", ctx.Graph.Descendants("snapshot_at()").Keys);

        var graph = System.Text.Json.Nodes.JsonNode.Parse(Cli("project", "show", "graph", "--project", dir, "--format", "json", "marts.snap_totals").Out)!["data"]!;
        Assert.Equal(["marts.snap_totals:full", "snapshot_at():macro", "src.orders_snap:mapped", "status_totals():macro"], graph["nodes"]!.AsArray().Select(n => $"{(string?)n!["name"]}:{(string?)n["kind"] ?? (string?)n["category"]}").Order(StringComparer.Ordinal));

        var metadata = System.Text.Json.Nodes.JsonNode.Parse(Cli("project", "show", "metadata", "--project", dir, "--format", "json").Out)!["data"]!["models"]!.AsArray();
        var upstream = metadata.Single(m => (string?)m!["name"] == "marts.snap_totals")!["upstream"]!.AsArray().Select(u => $"{(string?)u!["name"]}:{(string?)u["category"]}/{(string?)u["kind"]}");
        Assert.Contains("status_totals():macro/", upstream);
        Assert.Contains("src.orders_snap:model/mapped", upstream);
        var rows = MetadataBuilder.Dependencies(ctx);                                                  // the same facts as rows: what sort of thing, and the model kind only for a model
        Assert.Contains(rows, r => r is { Subject: "marts.snap_totals", DependsOn: "status_totals()", DependsOnCategory: "macro", DependsOnKind: null, Depth: 1 });
        Assert.Contains(rows, r => r is { Subject: "marts.snap_totals", DependsOn: "src.orders_snap", DependsOnCategory: "model", DependsOnKind: "mapped", Depth: 1 });
    }

    [Fact]
    public void A_change_to_a_macro_changes_the_definition_hash_of_every_model_that_reaches_it_and_no_other()
    {
        var dir = Project();
        var before = ProjectContext.Load(dir);
        var snap = before.DefinitionHashOf("SELECT * FROM status_totals('src.orders_snap', 'snap_date')");
        var plain = before.DefinitionHashOf("SELECT 1 AS n");
        File.WriteAllText(Path.Combine(dir, "macros", "snapshots.sql"), Snapshots.Replace("sum(amount)", "max(amount)"));
        var after = ProjectContext.Load(dir);
        Assert.NotEqual(snap, after.DefinitionHashOf("SELECT * FROM status_totals('src.orders_snap', 'snap_date')"));
        Assert.Equal(plain, after.DefinitionHashOf("SELECT 1 AS n"));
    }

    [Fact]
    public void A_macro_that_cannot_be_created_is_a_warning_and_only_a_model_that_reaches_it_fails()
    {
        var dir = Project(Snapshots + "\nCREATE MACRO broken(x) AS TABLE SELECT * FROM nowhere.orders WHERE a = x;");
        var validate = Cli("project", "compile", "--project", dir);
        Assert.Contains("`broken` could not be created", validate.Err);
        Assert.Contains("warning DDB-106", validate.Err);
        Assert.DoesNotContain("snap_totals cannot be lowered", validate.Err);            // the models that do not reach it are fine
        Write(dir, "models/marts/uses_broken.yml", "name: marts.uses_broken\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        Write(dir, "models/marts/uses_broken.sql", "SELECT order_id FROM broken(1)\n");
        Assert.Contains("uses_broken cannot be lowered", Cli("project", "compile", "--project", dir).Err);
    }

    [Fact]
    public void A_cast_to_an_enum_type_is_refused_and_a_type_alias_lowers_to_the_type_it_stands_for()
    {
        var enumDir = Project(Snapshots + "\nCREATE TYPE mood AS ENUM ('sad', 'happy');\nCREATE MACRO feeling(x) AS CAST(x AS mood);");
        Write(enumDir, "models/marts/snap_totals.sql", "SELECT feeling(status) AS status, as_of_date, sum(amount) AS total FROM snapshot_at('src.orders_snap', 'snap_date') GROUP BY ALL\n");
        var refused = Cli("project", "compile", "--project", enumDir);
        Assert.Contains("DDB-324", refused.Err);
        Assert.Contains("a cast to or from an enum type", refused.Err);                       // not a warning about a type `custom` and a script that says ENUM

        var aliasDir = Project(Snapshots + "\nCREATE TYPE money AS DECIMAL(12, 2);\nCREATE MACRO as_money(x) AS CAST(x AS money);");
        Write(aliasDir, "models/marts/snap_totals.sql", "SELECT status, snap_date AS as_of_date, as_money(sum(amount)) AS total FROM src.orders_snap GROUP BY ALL\n");
        var ok = Cli("project", "compile", "--project", aliasDir);
        Assert.True(ok.Exit == 0, ok.Out + ok.Err);
        var script = File.ReadAllText(Path.Combine(aliasDir, "rendered", "sqlserver", "marts.snap_totals", "load.default.sql"));
        Assert.Contains("NUMERIC(12, 2)", script);
        Assert.DoesNotContain("money", script);
    }

    [Fact]
    public void A_macro_call_with_lowering_switched_off_is_refused_and_a_wrong_column_name_is_a_bind_error()
    {
        var off = Project(config: "defaults: {connections: [sqlserver]}\ntracking: none\nlowering: { enabled: false }\n");
        Assert.Contains("a macro is expanded by DuckDB while the query is lowered, which is switched off", Cli("project", "compile", "--project", off).Err);

        var dir = Project();
        Write(dir, "models/marts/snap_totals.sql", "SELECT * FROM status_totals('src.orders_snap', 'nope')\n");
        Assert.Contains("snap_totals cannot be lowered", Cli("project", "compile", "--project", dir).Err);
    }

    [Fact]
    public void A_model_that_calls_a_macro_can_be_defined_sampled_and_tested()
    {
        var dir = Project();
        var define = Cli("project", "model", "update", "--project", dir, "--check");
        Assert.True(define.Exit == 0, define.Out + define.Err);
        var sample = Cli("project", "sample", "--project", dir, "--format", "json", "marts.snap_totals");
        Assert.True(sample.Exit == 0, sample.Out + sample.Err);
        Assert.Contains("as_of_date", sample.Out);
    }

    // ---- names as parameters ----

    private const string TwoConnections = """
        defaults: {connections: [live, snap]}
        tracking: none
        connections:
          live:
            engine: sqlserver
            parameters:
              table: { type: NAME, value: src.orders }
              date_col: { type: NAME, value: "" }
          snap:
            engine: sqlserver
            parameters:
              table: { type: NAME, value: src.orders_snap }
              date_col: { type: NAME, value: snap_date }
        """;

    private static string NamedProject(string config = TwoConnections)
    {
        var dir = Project(config: config);
        File.Delete(Path.Combine(dir, "models", "marts", "live_totals.yml")); File.Delete(Path.Combine(dir, "models", "marts", "live_totals.sql"));
        File.Delete(Path.Combine(dir, "models", "marts", "snap_totals.yml")); File.Delete(Path.Combine(dir, "models", "marts", "snap_totals.sql"));
        Write(dir, "models/marts/totals.yml", "name: marts.totals\nkind: full\ngrain: [as_of_date, status]\ncolumns:\n  - {name: as_of_date, type: DATE, nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: total, type: \"DECIMAL(38,2)\", nullable: false}\n");
        Write(dir, "models/marts/totals.sql", "SELECT * FROM status_totals(${connection.table}, ${connection.date_col})\n");
        return dir;
    }

    [Fact]
    public void A_name_given_by_a_connection_parameter_makes_each_connection_read_its_own_table()
    {
        var dir = NamedProject();
        var render = Cli("project", "compile", "--project", dir);
        Assert.True(render.Exit == 0, render.Out + render.Err);
        var live = File.ReadAllText(Path.Combine(dir, "rendered", "lowered", "marts.totals", "lowered.live.sql"));
        var snap = File.ReadAllText(Path.Combine(dir, "rendered", "lowered", "marts.totals", "lowered.snap.sql"));
        Assert.Contains("FROM src.orders\nGROUP BY status", live);
        Assert.Contains("FROM src.orders_snap\nGROUP BY snap_date, status", snap);
        Assert.False(File.Exists(Path.Combine(dir, "rendered", "lowered", "marts.totals", "lowered.sql")));
        Assert.Contains("FROM src.orders_snap", File.ReadAllText(Path.Combine(dir, "rendered", "snap", "marts.totals", "load.default.sql")));
        Assert.DoesNotContain("snap_date", File.ReadAllText(Path.Combine(dir, "rendered", "live", "marts.totals", "load.default.sql")));
        Assert.Equal(0, Cli("project", "compile", "--project", dir, "--check").Exit);
        Assert.Equal(0, Cli("project", "compile", "--project", dir).Exit);
    }

    [Fact]
    public void The_graph_has_every_table_a_name_can_point_at_and_a_connection_only_has_its_own()
    {
        var dir = NamedProject();
        var ctx = ProjectContext.Load(dir);
        Assert.Equal(["src.orders", "src.orders_snap", "status_totals()"], ctx.Graph.Reads("marts.totals").Order(StringComparer.Ordinal));
        var source = ctx.Project.BuiltModels.Single(s => s.Definition.Name == "marts.totals");
        Assert.Equal(["src.orders"], ctx.BaseTablesOf(source, source.ReadQuery(dir, ctx.Config, "live")));
        Assert.Equal(["src.orders_snap"], ctx.BaseTablesOf(source, source.ReadQuery(dir, ctx.Config, "snap")));
        Assert.Equal(2, ctx.QueryVariants(source).Count);
        Assert.NotEqual(ctx.DefinitionHashOf(source.ReadQuery(dir, ctx.Config, "live")), ctx.DefinitionHashOf(source.ReadQuery(dir, ctx.Config, "snap")));
    }

    [Fact]
    public void A_parameter_is_a_name_everywhere_or_nowhere_and_a_name_must_be_a_name()
    {
        var dir = NamedProject(TwoConnections.Replace("date_col: { type: NAME, value: snap_date }", "date_col: snap_date"));
        var validate = Cli("project", "compile", "--project", dir);
        Assert.Contains("a name on some of the model's connections and a VARCHAR on `snap`", validate.Err);

        var unknown = NamedProject(TwoConnections.Replace("table: { type: NAME, value: src.orders_snap }", "table: { type: NAME, value: \"x; DROP TABLE y\" }"));
        Assert.Contains("is not a name", Cli("project", "compile", "--project", unknown).Err);

        var missing = NamedProject(TwoConnections.Replace("\n      date_col: { type: NAME, value: snap_date }", ""));
        Assert.Contains("a name but has no value on the connection `snap`", Cli("project", "compile", "--project", missing).Err);
    }

    [Fact]
    public void A_name_that_is_the_same_on_every_connection_gives_one_lowering()
    {
        var dir = NamedProject(TwoConnections.Replace("table: { type: NAME, value: src.orders_snap }", "table: { type: NAME, value: src.orders }").Replace("date_col: { type: NAME, value: snap_date }", "date_col: { type: NAME, value: \"\" }"));
        Assert.Equal(0, Cli("project", "compile", "--project", dir).Exit);
        Assert.True(File.Exists(Path.Combine(dir, "rendered", "lowered", "marts.totals", "lowered.sql")));
        Assert.Single(ProjectContext.Load(dir).QueryVariants(ProjectContext.Load(dir).Project.BuiltModels.Single(s => s.Definition.Name == "marts.totals")));
    }

    [Fact]
    public void Column_lineage_follows_a_column_through_a_macro_to_the_table_column_it_was_given()
    {
        var dir = Project();
        string[] Upstream(string column)
        {
            var doc = System.Text.Json.Nodes.JsonNode.Parse(Cli("project", "show", "graph", "--project", dir, "--format", "json", "--column", column).Out)!["data"]!["column_lineage"]!["upstream"]!.AsArray();
            return doc.Select(u => $"{(string?)u!["table"]}.{(string?)u["column"]}").Order(StringComparer.Ordinal).ToArray();
        }
        Assert.Equal(["src.orders_snap.amount"], Upstream("marts.snap_totals.total"));
        Assert.Equal(["src.orders_snap.snap_date"], Upstream("marts.snap_totals.as_of_date"));          // the column the caller named
        Assert.Equal(["src.orders.amount"], Upstream("marts.live_totals.total"));
        var model = System.Text.Json.Nodes.JsonNode.Parse(Cli("project", "show", "metadata", "--project", dir, "--format", "json", "marts.snap_totals").Out)!["data"]!["models"]!.AsArray().Single()!;
        var total = model["columns"]!.AsArray().Single(c => (string?)c!["name"] == "total")!;
        Assert.Equal("src.orders_snap", (string?)total["lineage"]!["upstream"]![0]!["table"]);
    }

    // ---- default parameters and named arguments ----

    private const string WithDefaults = """
        CREATE MACRO snapshot_at(tbl, col := NULL, lag_days := 0) AS TABLE
          SELECT * EXCLUDE (__none), CASE WHEN col IS NULL THEN current_date - lag_days ELSE COLUMNS(lambda c: c = coalesce(col, '__none')) END AS as_of_date
          FROM (SELECT *, NULL::DATE AS __none FROM query_table(tbl));

        CREATE MACRO totals(tbl, col := NULL) AS TABLE
          SELECT as_of_date, status, sum(amount) AS total FROM snapshot_at(tbl, col := col) GROUP BY as_of_date, status;
        """;

    private static string DefaultsProject(string sql)
    {
        var dir = Project(WithDefaults);
        Write(dir, "models/marts/live_totals.sql", "SELECT * FROM totals('src.orders')\n");       // the project's other model reads the new macros too
        Write(dir, "models/marts/snap_totals.sql", sql + "\n");
        return dir;
    }

    [Fact]
    public void A_date_default_of_a_macro_is_written_as_a_date_literal_not_as_a_number_of_days()
    {
        // DuckDB folds the default into a DATE constant that the plan carries as days since 1970-01-01 (19904 is 2024-06-30); `CAST(19904 AS DATE)` fails in DuckDB and PostgreSQL and means 1954 on SQL Server
        var dir = Project(WithDefaults.Replace("lag_days := 0", "lag_days := 0, live_as_of := DATE '2024-06-30'").Replace("current_date - lag_days", "live_as_of - lag_days"));
        Write(dir, "models/marts/live_totals.sql", "SELECT * FROM totals('src.orders')\n");
        Write(dir, "models/marts/snap_totals.sql", "SELECT * FROM totals('src.orders_snap', 'snap_date')\n");
        var render = Cli("project", "compile", "--project", dir);
        Assert.True(render.Exit == 0, render.Out + render.Err);
        Assert.Contains("DATE '2024-06-30'", Lowered(dir, "marts.live_totals"));
        Assert.DoesNotContain("19904", Lowered(dir, "marts.live_totals"));
        Assert.Contains("'2024-06-30'", File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "marts.live_totals", "load.default.sql")));
    }

    [Fact]
    public void A_macro_default_that_is_a_clock_function_is_DDB_325_with_the_fix_where_the_macro_is_defined_and_where_a_query_reaches_it()
    {
        // with the clock kept as calls (what DuckDB 2.0 needs; forced on here so that DuckDB 1.5 behaves the same) DuckDB refuses the default itself, and the tool says why and what to write instead
        using var _ = DbDataBuild.Targets.DuckDb.PlanningConnection.UseMode(DbDataBuild.Targets.DuckDb.ClockOverrideMode.Always);
        var dir = Project(WithDefaults.Replace("lag_days := 0", "lag_days := 0, live_as_of := current_date"));
        Write(dir, "models/marts/live_totals.sql", "SELECT * FROM totals('src.orders')\n");
        Write(dir, "models/marts/snap_totals.sql", "SELECT * FROM totals('src.orders_snap', 'snap_date')\n");
        var compile = Cli("project", "compile", "--project", dir);
        Assert.NotEqual(0, compile.Exit);
        Assert.Contains("warning DDB-325", compile.Err);                                                           // where the macro is defined
        Assert.Contains("error DDB-325", compile.Err);                                                             // where a query reaches it
        Assert.Contains("live_as_of", compile.Err);
        Assert.Contains("body", compile.Err);                                                                      // the fix: move the call into the body or the query
        Assert.DoesNotContain("not a constant expression", compile.Err);                                           // not the raw binder error
        Assert.DoesNotContain("DDB-324", compile.Err);
    }

    [Theory]
    [InlineData("SELECT * FROM totals('src.orders_snap', 'snap_date')", "FROM src.orders_snap\nGROUP BY snap_date, status")]                        // every argument given
    [InlineData("SELECT * FROM totals('src.orders_snap', col := 'snap_date')", "FROM src.orders_snap\nGROUP BY snap_date, status")]               // a named argument at the call
    [InlineData("SELECT * FROM totals(col := 'snap_date', tbl := 'src.orders_snap')", "FROM src.orders_snap\nGROUP BY snap_date, status")]       // named, in another order
    [InlineData("SELECT * FROM totals('src.orders_snap')", "SELECT (CAST(current_date() AS DATE) - CAST(0 AS INTEGER)) AS as_of_date")]            // the defaults stand for what was left out
    public void A_macro_with_default_parameters_is_called_with_arguments_omitted_or_named(string sql, string expected)
    {
        var dir = DefaultsProject(sql);
        var render = Cli("project", "compile", "--project", dir);
        Assert.True(render.Exit == 0, render.Out + render.Err);
        Assert.Contains(expected, Lowered(dir, "marts.snap_totals"));
        Assert.DoesNotContain("CASE", Lowered(dir, "marts.snap_totals"));
    }

    [Fact]
    public void A_default_that_is_overridden_by_name_reaches_the_query_and_a_changed_default_changes_the_models_hash()
    {
        var dir = DefaultsProject("SELECT as_of_date, status, sum(amount) AS total FROM snapshot_at(tbl := 'src.orders', lag_days := 2) GROUP BY as_of_date, status");
        Assert.Equal(0, Cli("project", "compile", "--project", dir).Exit);
        Assert.Contains("CAST(2 AS INTEGER)", Lowered(dir, "marts.snap_totals"));
        var ctx = ProjectContext.Load(dir);
        var graph = ctx.Graph.Reads("marts.snap_totals");
        Assert.Contains("snapshot_at()", graph);
        Assert.Contains("src.orders", graph);

        const string call = "SELECT * FROM totals('src.orders')";
        var before = ctx.DefinitionHashOf(call);
        File.WriteAllText(Path.Combine(dir, "macros", "snapshots.sql"), WithDefaults.Replace("lag_days := 0", "lag_days := 1"));
        Assert.NotEqual(before, ProjectContext.Load(dir).DefinitionHashOf(call));                                      // the default is part of the macro's text
    }

    [Fact]
    public void Column_lineage_define_and_sample_work_for_a_call_with_a_named_argument()
    {
        var dir = DefaultsProject("SELECT * FROM totals('src.orders_snap', col := 'snap_date')");
        Assert.Equal(0, Cli("project", "model", "update", "--project", dir, "--check").Exit);
        var lineage = System.Text.Json.Nodes.JsonNode.Parse(Cli("project", "show", "graph", "--project", dir, "--format", "json", "--column", "marts.snap_totals.as_of_date").Out)!["data"]!["column_lineage"]!["upstream"]!.AsArray();
        Assert.Equal("src.orders_snap.snap_date", $"{(string?)lineage[0]!["table"]}.{(string?)lineage[0]!["column"]}");
        Assert.Equal(0, Cli("project", "sample", "--project", dir, "--rows", "5", "marts.snap_totals").Exit);
    }
}
