using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The dependency graph that `loads.yml` carries (entry 166): compiled with the loads and hashed with them, the same rows `publish` writes as `metadata_dependency`, and read by a refresh in place of the project, which a
/// refresh no longer loads to order, skip or select.
/// </summary>
public class LoadGraphTests
{
    private const string Config = "defaults: {connections: [sqlserver]}\n";
    private const string Events = "name: staging.events\nkind: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n";
    private const string Cols = "grain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n";
    private const string Loads = "loads:\n  incremental:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: \"2000-01-01 00:00:00\"}\n";

    /// <summary>events (mapped) -> fct_events (incremental) -> recent (a view) -> summary (full) -> report (full): the view is not loaded, so summary reaches fct_events through it.</summary>
    private static string Project()
    {
        var dir = NewProjectDir();
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Write("dbdatabuild.yml", Config);
        Write("models/staging/events.yml", Events);
        Write("models/marts/fct_events.yml", "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\n" + Cols + Loads);
        Write("models/marts/fct_events.sql", "SELECT e.event_id, e.event_ts FROM staging.events e\n");
        Write("models/marts/recent.yml", "name: marts.recent\nkind: view\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n");
        Write("models/marts/recent.sql", "SELECT event_id FROM marts.fct_events\n");
        Write("models/marts/summary.yml", "name: marts.summary\nkind: full\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n");
        Write("models/marts/summary.sql", "SELECT event_id FROM marts.recent\n");
        Write("models/marts/report.yml", "name: marts.report\nkind: full\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n");
        Write("models/marts/report.sql", "SELECT event_id FROM marts.summary\n");
        return dir;
    }

    private static string LoadsPath(string dir) => Path.Combine(dir, "rendered/sqlserver/loads.yml");

    private static LoadsFile ReadLoads(string dir)
    {
        var diags = new List<Diagnostic>();
        var file = LoadsDocument.Parse(File.ReadAllText(LoadsPath(dir)), "loads.yml", diags);
        Assert.True(file != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return file!;
    }

    private static (int Exit, string Out, string Err) Cli(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: v => v is "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" ? "Server=127.0.0.1,1;User Id=r;Password=x;Connect Timeout=1" : null);
        return (exit, o.ToString(), e.ToString());
    }

    private static string Edges(LoadsFile f) => string.Join(";", (f.Dependencies ?? []).Select(d => $"{d.Model}[{d.ModelKind}]>{d.DependsOn}[{d.DependsOnKind}]@{d.Depth}"));

    [Fact]
    public void Compile_carries_the_edges_between_the_loaded_models_through_the_models_that_are_not_loaded_with_the_kind_of_each_side()
    {
        var dir = Project();
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var loads = ReadLoads(dir);
        Assert.Equal(["marts.fct_events", "marts.report", "marts.summary"], loads.Loads.Select(l => l.Model).Order(StringComparer.Ordinal));
        // the view is no load, so summary reaches fct_events in two steps; staging.events and the view are not loaded, so they are not in the graph
        Assert.Equal("marts.report[full]>marts.summary[full]@1;marts.report[full]>marts.fct_events[incremental_by_time_range]@3;marts.summary[full]>marts.fct_events[incremental_by_time_range]@2", Edges(loads));
        var text = File.ReadAllText(LoadsPath(dir));
        Assert.Contains("  dependencies: 3\n", text);
        // deterministic: the same project compiles to the same bytes, and the file is what `--check` compares
        var again = File.ReadAllText(LoadsPath(dir));
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        Assert.Equal(again, File.ReadAllText(LoadsPath(dir)));
        Assert.Equal(0, RenderFiles("--check", "--project", dir).Exit);
        // and it changes with the project: a model that stops reading another loses the edge, and the file is rewritten
        File.WriteAllText(Path.Combine(dir, "models/marts/report.sql"), "SELECT event_id FROM marts.fct_events\n");
        Assert.NotEqual(0, RenderFiles("--check", "--project", dir).Exit);
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        Assert.Equal("marts.report[full]>marts.fct_events[incremental_by_time_range]@1;marts.summary[full]>marts.fct_events[incremental_by_time_range]@2", Edges(ReadLoads(dir)));
    }

    [Fact]
    public void Project_show_refresh_shows_the_graph_of_the_loads_file_for_each_plan_in_text_and_in_json()
    {
        var dir = Project();
        Assert.Equal(0, Cli(dir, "project", "compile").Exit);
        var (exit, text, _) = Cli(dir, "project", "show", "refresh", "default");
        Assert.Equal(0, exit);
        Assert.Contains("dependency graph of the loads file:", text);
        Assert.Contains("waits for: marts.fct_events [incremental_by_time_range] (2 steps)", text);          // summary reaches fct_events through the view that is not loaded
        var json = System.Text.Json.Nodes.JsonNode.Parse(Cli(dir, "project", "show", "refresh", "default", "--format", "json").Out)!["data"]!["plans"]![0]!;
        var edges = json["dependencies"]!.AsArray().Select(e => $"{(string?)e!["model"]}[{(string?)e!["model_kind"]}]>{(string?)e!["depends_on"]}[{(string?)e!["depends_on_kind"]}]@{(int)e!["depth"]!}").ToList();
        Assert.Equal(Edges(ReadLoads(dir)).Split(';').Order(), edges.Order());                              // the document is the graph the file carries, complete
        var summary = json["load_members"]!.AsArray().Single(m => (string?)m!["model"] == "marts.summary")!["waits_for"]!.AsArray();
        Assert.Equal(("marts.fct_events", "incremental_by_time_range", 2), ((string?)summary[0]!["depends_on"], (string?)summary[0]!["depends_on_kind"], (int)summary[0]!["depth"]!));
    }

    [Fact]
    public void An_edited_graph_is_refused_like_an_edited_plan_and_so_is_a_graph_taken_out_or_a_count_that_does_not_fit()
    {
        var dir = Project();
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var text = File.ReadAllText(LoadsPath(dir));
        string Refusal(string edited)
        {
            var diags = new List<Diagnostic>();
            Assert.Null(LoadsDocument.Parse(edited, "loads.yml", diags));
            return string.Join("\n", diags.Select(DiagnosticFormatter.Format));
        }
        Assert.Contains("does not match its hash", Refusal(text.Replace("depth: 2", "depth: 1")));
        Assert.NotEqual("", Refusal(text.Replace("  dependencies: 3\n", "")));            // taking the graph out is an edit too
        Assert.NotEqual("", Refusal(text.Replace("  dependencies: 3\n", "  dependencies: 2\n")));
        Assert.NotEqual("", Refusal(text.Replace("depth: 2", "depth: 0")));
        Assert.NotEqual("", Refusal(text.Replace("depends_on: \"marts.summary\"", "depends_on: \"marts.ghost\"")));                              // a model the file does not load
        Assert.NotEqual("", Refusal(text.Replace("    model_kind:", "    kind:")));
        // an edit the person made on the plan side, where the graph does not live, is refused by the plan's own hash as before
        var plan = Path.Combine(dir, "rendered/sqlserver/refresh/default.yml");
        File.AppendAllText(plan, "# edited\n");
        Assert.NotEqual(0, RenderFiles("--check", "--project", dir).Exit);
    }

    [Fact]
    public void A_file_compiled_before_the_graph_was_carried_still_reads_and_has_none_and_a_graph_with_no_edges_is_not_the_same_as_none()
    {
        var dir = Project();
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var withGraph = ReadLoads(dir);
        var old = LoadsDocument.Serialize(withGraph with { Dependencies = null });
        Assert.DoesNotContain("dependencies", old);
        var diags = new List<Diagnostic>();
        var parsed = LoadsDocument.Parse(old, "loads.yml", diags);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Dependencies);
        Assert.Null(LoadGraph.Of(parsed));
        var empty = LoadsDocument.Parse(LoadsDocument.Serialize(withGraph with { Dependencies = [] }), "loads.yml", diags);
        Assert.NotNull(empty);
        Assert.Empty(empty!.Dependencies!);
        Assert.NotNull(LoadGraph.Of(empty));
    }

    [Fact]
    public void The_graph_answers_ancestors_descendants_and_selectors_by_depth()
    {
        var dir = Project();
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var graph = LoadGraph.Of(ReadLoads(dir))!;
        Assert.Equal(["marts.report", "marts.summary"], graph.Descendants("marts.fct_events").Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["marts.summary"], graph.Descendants("marts.fct_events", 2).Keys);
        Assert.Equal(2, graph.Descendants("marts.fct_events")["marts.summary"]);
        Assert.Equal(["marts.fct_events", "marts.summary"], graph.Ancestors("marts.report").Keys.Order(StringComparer.Ordinal));
        Assert.Equal("incremental_by_time_range", graph.KindOf("MARTS.FCT_EVENTS"));
        Assert.Empty(graph.Descendants("marts.report"));
        Assert.Empty(graph.Descendants("nothing"));
        Assert.Equal(["marts.fct_events", "marts.report", "marts.summary"], ModelSelector.Expand(ModelSelector.Parse("marts.summary"), ["marts.summary"], graph, graph.Has).Concat(ModelSelector.Expand(ModelSelector.Parse("+marts.report"), ["marts.report"], graph, graph.Has)).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Selection_and_parallelism_use_the_graph_in_the_plan_with_the_project_deleted_after_compile_and_an_old_file_has_none()
    {
        var dir = Project();
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var loads = ReadLoads(dir);
        Directory.Delete(Path.Combine(dir, "models"), recursive: true);                                              // nothing of the project is left but the config and the compiled files
        // selectors the graph can answer do not load the project either: `+` and `kind:`
        var plan = RefreshPlanDocument.Parse(File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/refresh/default.yml")), "default.yml", []);
        Assert.NotNull(plan);
        var error = new StringWriter();
        Assert.Equal(["marts.fct_events.incremental", "marts.report.default", "marts.summary.default"], RefreshCommand.Select(plan!, ["+marts.report"], dir, error, loads)!.Order(StringComparer.Ordinal));
        Assert.Equal(["marts.fct_events.incremental", "marts.summary.default"], RefreshCommand.Select(plan!, ["marts.fct_events+2"], dir, error, loads)!.Order(StringComparer.Ordinal));      // two steps down: summary, not report
        Assert.Equal(["marts.report.default", "marts.summary.default"], RefreshCommand.Select(plan!, ["kind:full"], dir, error, loads)!.Order(StringComparer.Ordinal));
        Assert.Equal(["marts.fct_events.incremental"], RefreshCommand.Select(plan!, ["exclude:kind:full"], dir, error, loads)!.Order(StringComparer.Ordinal));
        Assert.Equal("", error.ToString());

        // a file compiled before the graph carries none: LoadGraph.Of is null, and the command asks for one load at a time with the warning (the engines' test, RefreshParallelConformanceTests, runs it)
        var old = LoadsDocument.Parse(LoadsDocument.Serialize(loads with { Dependencies = null }), "loads.yml", []);
        Assert.Null(LoadGraph.Of(old!));
        Assert.Equal(1, RefreshCommand.ParallelFor(4, LoadGraph.Of(old!) != null).Parallel);
        Assert.Equal(4, RefreshCommand.ParallelFor(4, LoadGraph.Of(loads) != null).Parallel);
    }

    [Fact]
    public void Every_term_but_changed_selects_from_the_facts_in_the_loads_file_with_the_models_folder_gone_and_gives_what_the_project_gives()
    {
        var dir = Project();
        File.AppendAllText(Path.Combine(dir, "models/marts/fct_events.yml"), "tags: [fct]\nproperties: {tier: 1}\n");
        File.AppendAllText(Path.Combine(dir, "models/marts/summary.yml"), "tags: [report]\nproperties: {tier: 2}\nconnections: [sqlserver]\n");
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var loads = ReadLoads(dir);
        Assert.Equal(["marts.fct_events", "marts.report", "marts.summary"], loads.Models!.Select(m => m.Name));                       // the loaded models only, by name
        var summary = loads.Models!.Single(m => m.Name == "marts.summary");
        Assert.Equal(("full", "models/marts/summary.yml", "models/marts/summary.sql"), (summary.Kind, summary.DefinitionFile, summary.QueryFile));
        Assert.Equal("report;sqlserver;tier|number|2", $"{string.Join(",", summary.Tags)};{string.Join(",", summary.Connections)};{summary.Properties[0].Key}|{summary.Properties[0].Type}|{summary.Properties[0].Value}");
        var plan = RefreshPlanDocument.Parse(File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/refresh/default.yml")), "default.yml", [])!;
        string[] Terms = ["tag:fct", "tag:report", "property:tier=2", "property:tier", "path:models/marts", "connection:sqlserver", "kind:full", "kind:incremental_by_time_range", "marts.s*", "models/marts/summary.sql", "models/marts", "+marts.summary", "marts.fct_events+", "@marts.summary", "tag:fct,kind:incremental_by_time_range", "exclude:tag:fct"];
        var withProject = Terms.ToDictionary(x => x, x => RefreshCommand.Select(plan, [x], dir, new StringWriter())?.Order(StringComparer.Ordinal).ToList());          // the project is there: its files answer
        Directory.Delete(Path.Combine(dir, "models"), recursive: true);
        foreach (var term in Terms)
        {
            var error = new StringWriter();
            var fromFacts = RefreshCommand.Select(plan, [term], dir, error, loads)?.Order(StringComparer.Ordinal).ToList();
            Assert.True(fromFacts != null, $"{term}: {error}");
            Assert.Equal(withProject[term], fromFacts);                                                                               // the same members, with no project file read
        }
        var none = new StringWriter();
        Assert.Null(RefreshCommand.Select(plan, ["tag:nothing"], dir, none, loads));
        Assert.Contains("selects no model", none.ToString());
        var unloaded = new StringWriter();
        Assert.Null(RefreshCommand.Select(plan, ["marts.recent"], dir, unloaded, loads));                                             // the view is no load of this file
        Assert.Contains("among the models this loads file loads", unloaded.ToString());
        // `changed:` is the one term that reads the working tree: with the models folder gone the project has nothing, and the facts are not used for it
        Assert.Null(RefreshCommand.Select(plan, ["changed:HEAD"], dir, new StringWriter(), loads));
        // a file compiled before the facts were carried has none, and every term then asks the project
        var old = LoadsDocument.Parse(LoadsDocument.Serialize(loads with { Models = null }), "loads.yml", []);
        Assert.NotNull(old);
        Assert.Null(old!.Models);
    }

    [Fact]
    public void The_facts_are_deterministic_hashed_and_refused_when_edited_or_when_they_do_not_name_the_loaded_models()
    {
        var dir = Project();
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var text = File.ReadAllText(LoadsPath(dir));
        Assert.Contains("  models: 3", text);
        string Damaged(string a, string b) => text.Replace(a, b);
        Assert.Null(LoadsDocument.Parse(Damaged("    kind: \"full\"\n    file: \"models/marts/report.yml\"", "    kind: \"view\"\n    file: \"models/marts/report.yml\""), "loads.yml", []));        // an edited fact
        Assert.Null(LoadsDocument.Parse(Damaged("  models: 3", "  models: 2"), "loads.yml", []));                                      // a recount
        var diags = new List<Diagnostic>();
        Assert.Null(LoadsDocument.Parse(Damaged("  - name: \"marts.report\"\n", "  - name: \"marts.other\"\n"), "loads.yml", diags));
        Assert.NotEmpty(diags);
    }

    [Fact]
    public void Damaging_the_loads_file_in_seeded_ways_is_a_diagnostic_or_the_same_file_never_an_exception()
    {
        var dir = Project();
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var text = File.ReadAllText(LoadsPath(dir));
        var original = LoadsDocument.Parse(text, "loads.yml", []);
        Assert.NotNull(original);
        for (var seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            var lines = text.Split('\n').ToList();
            switch (random.Next(5))
            {
                case 0: lines.RemoveAt(random.Next(lines.Count)); break;
                case 1: lines.Insert(random.Next(lines.Count), lines[random.Next(lines.Count)]); break;
                case 2: { var i = random.Next(lines.Count); if (lines[i].Length > 0) lines[i] = lines[i].Remove(random.Next(lines[i].Length), 1); break; }
                case 3: { var i = random.Next(lines.Count); lines[i] = lines[i].Replace(": ", ":"); break; }
                default: { var i = random.Next(lines.Count); lines[i] = "  " + lines[i]; break; }
            }
            var damaged = string.Join('\n', lines);
            var diags = new List<Diagnostic>();
            var parsed = LoadsDocument.Parse(damaged, "loads.yml", diags);
            if (parsed != null) Assert.Equal(text.Replace("\r\n", "\n"), damaged.Replace("\r\n", "\n"));          // anything that reads is the very file
            else Assert.NotEmpty(diags);
        }
    }

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public void Compile_and_publish_agree_on_the_rows_for_every_sample_project(string template)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var o = new StringWriter(); var e = new StringWriter();
            Assert.Equal(0, CliApp.Run(["project", "create", template, dir], o, e));
            Assert.Equal(0, CliApp.Run(["project", "compile", "--project", dir], o, e));
            var ctx = ProjectContext.Load(dir);
            // publish's rows are the project's rows as `MetadataPublisher.Prepare` hands them to the store
            var published = MetadataPublisher.Prepare(ctx, null, plan: null).Dependencies.Rows;
            Assert.Equal(MetadataBuilder.Dependencies(ctx), published);
            var any = false;
            foreach (var path in Directory.EnumerateFiles(Path.Combine(dir, "rendered"), "loads.yml", SearchOption.AllDirectories))
            {
                var diags = new List<Diagnostic>();
                var loads = LoadsDocument.Parse(File.ReadAllText(path), path, diags);
                Assert.True(loads != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
                Assert.NotNull(loads!.Dependencies);
                var kinds = loads.Loads.Select(l => l.Model).Distinct().ToDictionary(m => m, m => ctx.Project.BuiltModels.First(s => s.Definition.Name == m).Definition.Kind, StringComparer.OrdinalIgnoreCase);
                var expected = published.Where(r => kinds.ContainsKey(r.Subject) && kinds.ContainsKey(r.DependsOn)).Select(r => $"{r.Subject}|{r.DependsOn}|{r.Depth}|{kinds[r.Subject]}|{kinds[r.DependsOn]}").ToList();
                Assert.Equal(expected, loads.Dependencies!.Select(d => $"{d.Model}|{d.DependsOn}|{d.Depth}|{d.ModelKind}|{d.DependsOnKind}"));
                any |= loads.Dependencies!.Count > 0;
            }
            Assert.True(any || template != "fan_in_fan_out", $"{template} has loaded models that depend on each other");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    public static TheoryData<string> TemplateNames()
    {
        var data = new TheoryData<string>();
        foreach (var t in TemplateStore.All) data.Add(t.Name);
        return data;
    }
}
