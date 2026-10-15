using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Models;
using DbDataBuild.Targets.DuckDb;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The dependency graph, the model selectors every command shares, and `graph` (DESIGN.md 9.9).</summary>
public class GraphTests
{
    // ---- the pure graph ----

    private static DependencyGraph Chain() => new(["m.a", "m.b", "m.c", "m.d", "m.island"], ["s.x", "s.y"],
        [("m.a", "s.x"), ("m.b", "m.a"), ("m.b", "s.y"), ("m.c", "m.b"), ("m.d", "m.a"), ("m.d", "m.c"), ("m.island", "ghost.table")]);

    [Fact]
    public void Ancestors_and_descendants_walk_the_edges_with_their_distance_and_an_optional_limit()
    {
        var g = Chain();
        Assert.Equal(["m.a", "m.b", "m.c", "s.x", "s.y"], g.Ancestors("m.d").Keys.Order());
        Assert.Equal(1, g.Ancestors("m.d")["m.a"]);                                  // reached directly, so one step, not three
        Assert.Equal(["m.a", "m.c"], g.Ancestors("m.d", 1).Keys.Order());
        Assert.Equal(["m.b", "m.c", "m.d"], g.Descendants("m.a").Keys.Order());
        Assert.Equal(["m.b", "m.d"], g.Descendants("m.a", 1).Keys.Order());
        Assert.Empty(g.Descendants("m.d"));
        Assert.Equal(["ghost.table"], g.Unknown);
        Assert.Equal(["s.y"], g.Reads("m.b").Where(r => r.StartsWith("s.")));
    }

    [Fact]
    public void Levels_count_the_longest_chain_from_a_source_and_a_cycle_does_not_hang()
    {
        var levels = Chain().Levels();
        Assert.Equal((1, 2, 3, 4), (levels["m.a"], levels["m.b"], levels["m.c"], levels["m.d"]));
        var cyclic = new DependencyGraph(["m.a", "m.b"], [], [("m.a", "m.b"), ("m.b", "m.a")]);
        Assert.Equal(2, cyclic.Levels().Count);
        Assert.Equal(["m.b"], cyclic.Ancestors("m.a").Keys);                         // the walk ends instead of coming back to where it started
    }

    [Theory]
    [InlineData("a", "a", false, null, false, null, false)]
    [InlineData("+a", "a", true, null, false, null, false)]
    [InlineData("a+", "a", false, null, true, null, false)]
    [InlineData("+a+", "a", true, null, true, null, false)]
    [InlineData("2+a", "a", true, 2, false, null, false)]
    [InlineData("a+3", "a", false, null, true, 3, false)]
    [InlineData("1+a+2", "a", true, 1, true, 2, false)]
    [InlineData("@a", "a", false, null, false, null, true)]
    [InlineData("+kind:full", "kind:full", true, null, false, null, false)]
    [InlineData("changed:origin/main+", "changed:origin/main", false, null, true, null, false)]
    [InlineData("marts.fct_orders", "marts.fct_orders", false, null, false, null, false)]
    public void Selector_terms_parse_their_operators(string token, string core, bool up, int? upDepth, bool down, int? downDepth, bool at)
    {
        var t = ModelSelector.Parse(token);
        Assert.Equal((core, up, upDepth, down, downDepth, at), (t.Core, t.Upstream, t.UpstreamDepth, t.Downstream, t.DownstreamDepth, t.At));
    }

    [Fact]
    public void The_at_operator_is_the_model_what_depends_on_it_and_everything_those_need()
    {
        var g = Chain();
        // m.b: below it are m.c and m.d; m.d also needs m.a and m.c, so m.a comes in; m.island is not connected
        Assert.Equal(["m.a", "m.b", "m.c", "m.d"], ModelSelector.Expand(ModelSelector.Parse("@m.b"), ["m.b"], g, g.IsModel).Order());
        Assert.Equal(["m.a", "m.b", "m.c"], ModelSelector.Expand(ModelSelector.Parse("+m.c"), ["m.c"], g, g.IsModel).Order());
        Assert.Equal(["m.b", "m.c", "m.d"], ModelSelector.Expand(ModelSelector.Parse("m.b+"), ["m.b"], g, g.IsModel).Order());
        Assert.Equal(["m.b", "m.c"], ModelSelector.Expand(ModelSelector.Parse("m.b+1"), ["m.b"], g, g.IsModel).Order());       // m.d reads m.a and m.c, not m.b
    }

    // ---- a project ----

    private static string Project()
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        Directory.CreateDirectory(Path.Combine(dir, "models/stg"));
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [postgres, sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C, sqlserver: Latin1_General_100_BIN2 }\n");
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: customer_id, type: BIGINT}\n");
        File.WriteAllText(Path.Combine(dir, "models/staging/customers.yml"), "name: staging.customers\nkind: mapped\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n  - {name: name, type: \"VARCHAR(50)\"}\n");
        Model(dir, "stg/orders", "stg.orders", "view", "SELECT order_id, amount, customer_id FROM staging.orders", "  - {name: order_id, type: BIGINT}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: customer_id, type: BIGINT}\n");
        Model(dir, "stg/customers", "stg.customers", "view", "SELECT customer_id, upper(name) AS name FROM staging.customers", "  - {name: customer_id, type: BIGINT}\n  - {name: name, type: \"VARCHAR(50)\"}\n");
        Model(dir, "marts/fct_orders", "marts.fct_orders", "view", "SELECT o.order_id, o.amount * 2 AS double_amount, c.name FROM stg.orders o JOIN stg.customers c ON c.customer_id = o.customer_id",
            "  - {name: order_id, type: BIGINT}\n  - {name: double_amount, type: \"DECIMAL(18, 3)\"}\n  - {name: name, type: \"VARCHAR(50)\"}\n");
        Model(dir, "marts/big", "marts.big", "full", "SELECT order_id FROM marts.fct_orders WHERE double_amount > 100", "  - {name: order_id, type: BIGINT}\n", targets: "sqlserver");
        return dir;
    }

    private static void Model(string dir, string path, string name, string kind, string sql, string columns, string? targets = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, "models", path))!);
        File.WriteAllText(Path.Combine(dir, "models", path + ".yml"), $"name: {name}\nkind: {kind}\n{(targets != null ? $"connections=: [{targets}]\n" : "")}columns:\n{columns}");
        File.WriteAllText(Path.Combine(dir, "models", path + ".sql"), sql + "\n");
    }

    private static (int Exit, JsonNode Doc, string Err) Graph(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "show", "graph", "--project", dir, .. args, "--format", "json"], o, e, environment: _ => null);
        var doc = JsonNode.Parse(o.ToString())!;
        var result = SchemaConformanceTests.LoadSchema("output").Evaluate(JsonSerializer.SerializeToNode(doc), new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        Assert.True(result.IsValid, string.Join("\n", result.Details.Where(x => x.Errors != null).SelectMany(x => x.Errors!.Select(kv => $"{x.InstanceLocation}: {kv.Key} {kv.Value}"))) + "\n" + o);
        return (exit, doc, e.ToString());
    }

    private static string[] Models(JsonNode doc) => doc["data"]!["nodes"]!.AsArray().Where(n => (string?)n!["category"] == "model").Select(n => (string)n!["name"]!).Order().ToArray();

    [Fact]
    public void Graph_lists_models_and_sources_by_level_with_what_each_model_reads()
    {
        var (exit, doc, _) = Graph(Project());
        Assert.Equal(0, exit);
        var nodes = doc["data"]!["nodes"]!.AsArray();
        Assert.Equal(["staging.customers:0", "staging.orders:0", "stg.customers:1", "stg.orders:1", "marts.fct_orders:2", "marts.big:3"], nodes.Select(n => $"{(string)n!["name"]!}:{(int)n["level"]!}"));
        Assert.Equal(new[] { "staging.customers>stg.customers", "staging.orders>stg.orders", "stg.customers>marts.fct_orders", "stg.orders>marts.fct_orders", "marts.fct_orders>marts.big" }.Order(),
            doc["data"]!["edges"]!.AsArray().Select(e => $"{(string)e!["from"]!}>{(string)e["to"]!}").Order());
        var big = nodes.Single(n => (string?)n!["name"] == "marts.big")!;
        Assert.Equal(("model", "full", "marts/big.sql"), ((string)big["category"]!, (string)big["kind"]!, ((string)big["file"]!).Replace("models/", "")));
        Assert.Equal("mapped", (string?)nodes.Single(n => (string?)n!["name"] == "staging.orders")!["kind"]);        // a mapped model is a node of the kind mapped, not a "source"
        Assert.Equal(["sqlserver"], big["connections"]!.AsArray().Select(t => (string)t!));
    }

    [Theory]
    [InlineData("+marts.big", "marts.big,marts.fct_orders,staging.customers,staging.orders,stg.customers,stg.orders")]       // the mapped models under it are models of the selection
    [InlineData("2+marts.big", "marts.big,marts.fct_orders,stg.customers,stg.orders")]
    [InlineData("1+marts.big", "marts.big,marts.fct_orders")]
    [InlineData("stg.orders+", "marts.big,marts.fct_orders,stg.orders")]
    [InlineData("stg.orders+1", "marts.fct_orders,stg.orders")]
    [InlineData("@stg.orders", "marts.big,marts.fct_orders,staging.customers,staging.orders,stg.customers,stg.orders")]
    [InlineData("kind:full", "marts.big")]
    [InlineData("kind:view,stg.orders+", "marts.fct_orders,stg.orders")]
    [InlineData("connection:postgres", "marts.fct_orders,staging.customers,staging.orders,stg.customers,stg.orders")]       // marts.big is built for SQL Server only
    [InlineData("connection:sqlserver", "marts.big,marts.fct_orders,staging.customers,staging.orders,stg.customers,stg.orders")]
    [InlineData("path:models/stg", "stg.customers,stg.orders")]
    [InlineData("models/marts", "marts.big,marts.fct_orders")]
    [InlineData("stg.customers marts.big", "marts.big,stg.customers")]
    [InlineData("marts.fct_orders+ exclude:marts.big", "marts.fct_orders")]
    [InlineData("exclude:kind:view", "marts.big,staging.customers,staging.orders")]
    [InlineData("kind:mapped", "staging.customers,staging.orders")]
    [InlineData("staging.*", "staging.customers,staging.orders")]
    [InlineData("+stg.orders", "staging.orders,stg.orders")]
    [InlineData("models/staging", "staging.customers,staging.orders")]
    [InlineData("exclude:stg.orders+", "staging.customers,staging.orders,stg.customers")]
    public void Selectors_choose_models_by_the_graph_by_kind_target_and_path_and_exclusions_take_models_out(string selectors, string expected)
    {
        var chosen = Graph(Project(), selectors.Split(' ')).Doc["data"]!["nodes"]!.AsArray().Where(n => (string?)n!["category"] == "model").Select(n => (string)n!["name"]!).ToHashSet();
        // the graph also shows the tables a chosen model reads; the chosen ones are those the selector named, so compare through metadata, which prints exactly the selection
        var o = new StringWriter();
        var dir = Project();
        Assert.Equal(0, CliApp.Run(["project", "show", "metadata", .. selectors.Split(' '), "--project", dir, "--format", "json"], o, new StringWriter(), environment: _ => null));
        Assert.Equal(expected.Split(','), JsonNode.Parse(o.ToString())!["data"]!["models"]!.AsArray().Select(m => (string)m!["name"]!).Order());
        Assert.NotEmpty(chosen);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("kind:snapshot")]
    [InlineData("target:fabric")]
    [InlineData("path:models/none")]
    [InlineData("changed:no-such-ref")]
    public void A_selector_that_selects_nothing_is_a_usage_error_that_says_so(string selector)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var dir = Project();
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["project", "show", "metadata", selector, "--project", dir], o, e, environment: _ => null));
        Assert.NotEqual("", e.ToString());
    }

    // ---- columns and diagrams ----

    [Fact]
    public void Graph_shows_which_column_each_column_comes_from_and_follows_one_through_the_models()
    {
        var dir = Project();
        var columns = Graph(dir, "marts.fct_orders", "--columns").Doc["data"]!["column_edges"]!.AsArray();
        Assert.Equal(["double_amount<-stg.orders.amount:expression", "name<-stg.customers.name:direct", "order_id<-stg.orders.order_id:direct"],
            columns.Select(c => $"{(string)c!["to_column"]!}<-{(string)c["from_table"]!}.{(string)c["from_column"]!}:{(string)c["transform"]!}").Order());

        var lineage = Graph(dir, "--column", "staging.orders.amount").Doc["data"]!["column_lineage"]!;
        Assert.Empty(lineage["upstream"]!.AsArray());
        Assert.Equal(["stg.orders.amount@1", "marts.fct_orders.double_amount@2"], lineage["downstream"]!.AsArray().Select(c => $"{(string)c!["table"]!}.{(string)c["column"]!}@{(int)c["distance"]!}"));
        var up = Graph(dir, "--column", "marts.fct_orders.name").Doc["data"]!["column_lineage"]!;
        Assert.Equal(["stg.customers.name@1", "staging.customers.name@2"], up["upstream"]!.AsArray().Select(c => $"{(string)c!["table"]!}.{(string)c["column"]!}@{(int)c["distance"]!}"));

        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["project", "show", "graph", "--column", "marts.fct_orders.ghost", "--project", dir], o, e, environment: _ => null));
        Assert.Contains("no column `ghost`", e.ToString());
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["project", "show", "graph", "--column", "nodot", "--project", dir], new StringWriter(), new StringWriter(), environment: _ => null));
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["project", "show", "graph", "--diagram", "svg", "--project", dir], new StringWriter(), new StringWriter(), environment: _ => null));
    }

    [Fact]
    public void Diagrams_are_graphviz_or_mermaid_text()
    {
        var dir = Project();
        var dot = (string)Graph(dir, "marts.big", "--diagram", "dot").Doc["data"]!["diagram"]!;
        Assert.StartsWith("digraph dbdatabuild {", dot);
        Assert.Contains("\"marts.fct_orders\" -> \"marts.big\";", dot);
        var mermaid = (string)Graph(dir, "+marts.big", "--diagram", "mermaid").Doc["data"]!["diagram"]!;
        Assert.StartsWith("graph LR\n", mermaid);
        Assert.Contains("[(\"staging.orders\")]", mermaid);
        Assert.Matches(@"n\d+ --> n\d+", mermaid);
        Assert.Null(Graph(dir).Doc["data"]!["diagram"]);                                            // none unless asked for
    }

    [Fact]
    public void A_project_with_errors_has_no_graph_and_writes_nothing()
    {
        var dir = Project();
        File.WriteAllText(Path.Combine(dir, "models/stg/orders.yml"), "name: stg.orders\n");
        var before = Snapshot(dir);
        var (exit, _, _) = Graph(dir);
        Assert.Equal(1, exit);
        Assert.Equal(before, Snapshot(dir));
    }

    // ---- changed since a git ref ----

    private static bool Git(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();       // both at once: git's warnings must not fill a pipe nobody is reading
        if (!p.WaitForExit(60_000)) { p.Kill(entireProcessTree: true); return false; }
        return p.ExitCode == 0;
    }

    [Fact]
    public void Changed_selects_the_models_whose_files_differ_from_a_git_ref_and_a_changed_source_or_the_config_reaches_what_reads_it()
    {
        var dir = Project();
        Assert.True(Git(dir, "init -q") && Git(dir, "config user.email t@example.com") && Git(dir, "config user.name t") && Git(dir, "add -A") && Git(dir, "-c commit.gpgsign=false commit -q -m base"), "git must be available to test `changed:`");
        string[] Chosen(params string[] selectors)
        {
            var o = new StringWriter();
            Assert.Equal(0, CliApp.Run(["project", "show", "metadata", .. selectors, "--project", dir, "--format", "json"], o, new StringWriter(), environment: _ => null));
            return JsonNode.Parse(o.ToString())!["data"]!["models"]!.AsArray().Select(m => (string)m!["name"]!).Order().ToArray();
        }
        Assert.Empty(Chosen("changed:HEAD"));                                                         // nothing changed is a valid, empty selection

        File.AppendAllText(Path.Combine(dir, "models/stg/orders.sql"), "-- touched\n");
        Assert.Equal(["stg.orders"], Chosen("changed:HEAD"));
        Assert.Equal(["marts.big", "marts.fct_orders", "stg.orders"], Chosen("changed:HEAD+"));        // and what depends on it
        Assert.Equal(["stg.orders"], Chosen("changed:HEAD", "exclude:marts.big"));

        File.WriteAllText(Path.Combine(dir, "models/marts/new_one.yml"), "name: marts.new_one\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/new_one.sql"), "SELECT order_id FROM stg.orders\n");
        Assert.Contains("marts.new_one", Chosen("changed:HEAD"));                                      // an untracked model counts

        Git(dir, "stash -u -q");                                                                       // back to the base: now change a source
        File.AppendAllText(Path.Combine(dir, "models/staging/customers.yml"), "# touched\n");
        Assert.Equal(["staging.customers", "stg.customers"], Chosen("changed:HEAD"));                // the changed mapped model, and the model that reads it
        Git(dir, "checkout -q -- .");

        File.AppendAllText(Path.Combine(dir, "dbdatabuild.yml"), "# touched\n");
        Assert.Equal(["marts.big", "marts.fct_orders", "staging.customers", "staging.orders", "stg.customers", "stg.orders"], Chosen("changed:HEAD"));   // the project settings reach every model
    }

    [Fact]
    public void Changed_also_follows_a_folder_file_a_hook_script_a_macro_file_and_a_rendered_file_to_the_models_they_reach()
    {
        var dir = Project();
        Directory.CreateDirectory(Path.Combine(dir, "hooks"));
        Directory.CreateDirectory(Path.Combine(dir, "macros"));
        Directory.CreateDirectory(Path.Combine(dir, "rendered/lowered/stg.customers"));
        File.WriteAllText(Path.Combine(dir, "hooks/g.sql"), "GRANT SELECT ON stg.customers TO reader;\n");
        File.AppendAllText(Path.Combine(dir, "models/stg/customers.yml"), "hooks:\n  - {name: grant, event: post_create, script: hooks/g.sql}\n");
        File.WriteAllText(Path.Combine(dir, "macros/twice.sql"), "CREATE MACRO twice(x) AS x * 2;\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id, twice(o.amount) AS double_amount, c.name FROM stg.orders o JOIN stg.customers c ON c.customer_id = o.customer_id\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/_dbdatabuild.yml"), "defaults:\n  lint_ignore: [DDB-223]\n");
        File.WriteAllText(Path.Combine(dir, "rendered/lowered/stg.customers/lowered.sql"), "SELECT 1\n");
        Assert.True(Git(dir, "init -q") && Git(dir, "config user.email t@example.com") && Git(dir, "config user.name t") && Git(dir, "add -A") && Git(dir, "-c commit.gpgsign=false commit -q -m base"), "git must be available to test `changed:`");
        string[] Chosen()
        {
            var o = new StringWriter(); var e = new StringWriter();
            Assert.Equal(0, CliApp.Run(["project", "show", "metadata", "changed:HEAD", "--project", dir, "--format", "json"], o, e, environment: _ => null));
            return JsonNode.Parse(o.ToString())!["data"]!["models"]!.AsArray().Select(m => (string)m!["name"]!).Order().ToArray();
        }
        Assert.Empty(Chosen());

        File.AppendAllText(Path.Combine(dir, "hooks/g.sql"), "-- touched\n");
        Assert.Equal(["stg.customers"], Chosen());                                                    // the model that runs the script
        Git(dir, "checkout -q -- .");

        File.AppendAllText(Path.Combine(dir, "macros/twice.sql"), "-- touched\n");
        Assert.Equal(["marts.fct_orders"], Chosen());                                                 // the model whose query calls the macro
        Git(dir, "checkout -q -- .");

        File.AppendAllText(Path.Combine(dir, "models/marts/_dbdatabuild.yml"), "# touched\n");
        Assert.Equal(["marts.big", "marts.fct_orders"], Chosen());                                    // every model beneath the folder file
        Git(dir, "checkout -q -- .");

        File.AppendAllText(Path.Combine(dir, "rendered/lowered/stg.customers/lowered.sql"), "-- touched\n");
        Assert.Equal(["stg.customers"], Chosen());                                                    // the model the file was rendered for
    }

    // ---- for rules ----

    [Fact]
    public void The_ancestors_view_gives_every_table_a_model_depends_on_with_its_distance()
    {
        var ctx = ProjectContext.Load(Project());
        using var db = MetadataDatabase.Open(MetadataPublisher.Collect(ctx, null, null).Select(d => new MetadataDocument(d.SubjectKind, d.Subject, d.Json, d.Hash)), DbDataBuild.Core.ProductInfo.Version);
        var run = db.Run("SELECT ancestor, depth FROM metadata_ancestors WHERE model = 'marts.big' ORDER BY depth, ancestor", 20);
        Assert.Equal(RuleOutcome.Ran, run.Outcome);
        Assert.Equal(["marts.fct_orders:1", "stg.customers:2", "stg.orders:2", "staging.customers:3", "staging.orders:3"], run.Rows.Select(r => $"{r["ancestor"]}:{r["depth"]}"));
        Assert.Equal(0, db.Run("SELECT model FROM metadata_ancestors WHERE model = 'stg.orders' AND ancestor LIKE 'marts.%'", 5).Count);
    }
}
