using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Models.Yaml;
using DbDataBuild.Planning;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Refresh plans (docs/research/refresh-plans.md, step 1): `refresh:` in models and folder files, `refresh.plans` in the project, the compiled files, selection, inputs and the plan views. DDB-244, DDB-449 and DDB-450.</summary>
public class RefreshPlansTests
{
    private const string Config = "defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n";
    private const string Events = "name: staging.events\nkind: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string Cols = "grain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string Loads = "loads:\n  incremental:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: \"2000-01-01 00:00:00\"}\n" +
                                 "  reload:\n    strategy: delete_insert_by_range\n    column: event_ts\n    params: {start: TIMESTAMP, end: TIMESTAMP}\n    max_span: 10 days\n";
    private const string FctHead = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\n";
    private const string FctSql = "SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n";
    private const string SummaryYaml = "name: marts.summary\nkind: full\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n";
    private const string SummarySql = "SELECT event_id FROM marts.fct_events\n";

    private static string Project(string fctRefresh, string configExtra = "", string? summaryRefresh = null, string? folderFile = null)
    {
        var dir = NewProjectDir();
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Write("dbdatabuild.yml", Config + configExtra);
        Write("models/staging/events.yml", Events);
        Write("models/marts/fct_events.yml", FctHead + Cols + Loads + fctRefresh);
        Write("models/marts/fct_events.sql", FctSql);
        Write("models/marts/summary.yml", SummaryYaml + (summaryRefresh ?? ""));
        Write("models/marts/summary.sql", SummarySql);
        if (folderFile != null) Write("models/marts/_dbdatabuild.yml", folderFile);
        return dir;
    }

    private static (int Exit, string Out, string Err) Compile(string dir) => RenderFiles("--write", "--project", dir);

    private static (RefreshPlanFile Plan, LoadsFile Loads) Read(string dir, string plan)
    {
        var diags = new List<Diagnostic>();
        var loads = LoadsDocument.Parse(File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/loads.yml")), "loads.yml", diags);
        var file = RefreshPlanDocument.Parse(File.ReadAllText(Path.Combine(dir, $"rendered/sqlserver/refresh/{plan}.yml")), plan + ".yml", diags);
        Assert.True(loads != null && file != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return (file!, loads!);
    }

    // ---------------- declaring ----------------

    [Fact]
    public void A_membership_is_a_load_a_mapping_or_a_list_and_a_name_is_the_loads_unless_as_says_another()
    {
        var (def, diags) = Load(ValidModel.Replace("connections: [sqlserver, fabric]\n", "") + "\nrefresh:\n  hourly: default\n  daily: { load: default, inputs: { days: 60 }, on_failure: ignore }\n  monthly:\n    - { load: default, as: recent, inputs: { a: 1 } }\n    - { load: default, as: last_year }\n    - default\n");
        Assert.Empty(diags);
        var m = def!.Refresh;
        Assert.Equal(["hourly:default", "daily:default", "monthly:recent", "monthly:last_year", "monthly:default"], m.Select(x => $"{x.Plan}:{x.Name}"));
        Assert.Equal([new KeyValuePair<string, string>("days", "60")], m[1].Inputs);
        Assert.Equal("ignore", m[1].OnFailure);
    }

    [Fact]
    public void The_same_name_twice_in_a_plan_a_load_the_model_lacks_and_damaged_shapes_are_diagnostics_never_exceptions()
    {
        string Codes(string refresh) { var (_, d) = Load(ValidModel.Replace("connections: [sqlserver, fabric]\n", "") + "\n" + refresh); return string.Join(",", d.Select(x => x.Code).Distinct()); }
        Assert.Contains("DDB-245", Codes("refresh:\n  hourly: [default, default]\n"));
        Assert.Contains("DDB-245", Codes("refresh:\n  hourly: [default, { load: default }]\n"));
        Assert.Equal("", Codes("refresh:\n  hourly: [default, { load: default, as: again }]\n"));
        Assert.Contains("DDB-246", Codes("refresh:\n  hourly: nope\n"));
        foreach (var bad in new[] { "refresh: 3\n", "refresh:\n  Hourly: default\n", "refresh:\n  hourly: []\n", "refresh:\n  hourly: { load: default, inputs: [1] }\n", "refresh:\n  hourly: { load: default, inputs: { 'a b': 1 } }\n",
                                    "refresh:\n  hourly: { load: default, on_failure: sometimes }\n", "refresh:\n  hourly: { load: default, extra: 1 }\n", "refresh:\n  hourly: { as: x }\n", "refresh:\n  hourly: { load: default, as: Bad Name }\n", "refresh:\n  hourly: [[default]]\n" })
            Assert.NotEqual("", Codes(bad));
        var view = Load("name: marts.fct_orders\nkind: view\ncolumns:\n  - {name: a, type: BIGINT}\nrefresh:\n  hourly: default\n").Diags;
        Assert.Contains(view, d => d.Code == "DDB-106");                                                      // a view has no load to put in a plan
    }

    [Fact]
    public void Damaged_refresh_keys_of_a_model_and_of_the_project_never_throw()
    {
        var model = ValidModel.Replace("connections: [sqlserver, fabric]\n", "") + "\nrefresh:\n  hourly: { load: default, as: x, inputs: { days: 60 } }\n  daily: [default]\n";
        var random = new Random(11);
        for (var i = 0; i < 400; i++)
        {
            var chars = model.ToCharArray();
            chars[random.Next(model.IndexOf("refresh:", StringComparison.Ordinal), chars.Length)] = "x:-\n\"'#{ [],"[random.Next(12)];
            var diags = new List<Diagnostic>();
            ModelDefinitionLoader.Load(new string(chars), "models/marts/fct_orders.yml", null, diags);
        }
        var config = "refresh:\n  check: live\n  plans:\n    hourly: { title: T, tags: [a], on_failure: stop, on_busy: wait, expected_every: 1h, parallel: 2, check: objects, on_fail: warn }\n    daily: {}\n    weekly:\n";
        var parsed = ProjectConfigLoader.Load(config, "dbdatabuild.yml", new List<Diagnostic>());
        Assert.NotNull(parsed);
        Assert.Equal(["daily", "hourly", "weekly"], parsed!.RefreshPlans.Keys.Order());
        Assert.Equal(60, parsed.RefreshPlans["hourly"].ExpectedEvery!.Minutes);
        for (var i = 0; i < 400; i++)
        {
            var chars = config.ToCharArray();
            chars[random.Next(chars.Length)] = "x:-\n\"'#{ [],"[random.Next(12)];
            ProjectConfigLoader.Load(new string(chars), "dbdatabuild.yml", new List<Diagnostic>());
        }
        foreach (var bad in new[] { "refresh:\n  plans: 3\n", "refresh:\n  plans:\n    Hourly: {}\n", "refresh:\n  plans:\n    h: { on_failure: nope }\n", "refresh:\n  plans:\n    h: { on_busy: nope }\n", "refresh:\n  plans:\n    h: { expected_every: soon }\n",
                                    "refresh:\n  plans:\n    h: { parallel: 0 }\n", "refresh:\n  plans:\n    h: { check: sometimes }\n", "refresh:\n  plans:\n    h: { bogus: 1 }\n", "refresh:\n  plans:\n    h: 4\n", "connections:\n  sqlserver: { refresh: { plans: { h: {} } } }\n" })
        {
            var d = new List<Diagnostic>();
            Assert.Null(ProjectConfigLoader.Load(bad, "dbdatabuild.yml", d));
            Assert.NotEmpty(d);
        }
    }

    [Fact]
    public void A_plan_the_project_does_not_declare_is_an_error_only_when_it_declares_some_and_default_is_always_there()
    {
        var declared = Project("refresh: { hourly: incremental, typo: incremental }\n", "refresh:\n  plans:\n    hourly: {}\n");
        var (exit, _, err) = Compile(declared);
        Assert.NotEqual(0, exit);
        Assert.Contains("DDB-244", err);
        Assert.Contains("typo", err);
        Assert.Contains("models/marts/fct_events.yml", err);

        var free = Project("refresh: { hourly: incremental, typo: incremental }\n");
        Assert.Equal(0, Compile(free).Exit);                                                                       // no plans declared: any name

        var onlyDefault = Project("refresh: { default: incremental }\n", "refresh:\n  plans:\n    hourly: {}\n");
        Assert.Equal(0, Compile(onlyDefault).Exit);
    }

    // ---------------- layering ----------------

    [Fact]
    public void A_folder_file_puts_every_model_beneath_it_in_a_plan_and_the_models_own_refresh_adds_to_it_or_replaces_it()
    {
        var dir = Project("refresh: { hourly: incremental }\n", folderFile: "defaults:\n  refresh: { daily: default }\n", summaryRefresh: "refresh=: { weekly: default }\n");
        // fct_events has no load called default: the folder's membership is wrong for it, and the compile says which
        var (exit, _, err) = Compile(dir);
        Assert.NotEqual(0, exit);
        Assert.Contains("DDB-246", err);
        Assert.Contains("models/marts/_dbdatabuild.yml", err);                                                      // the place the membership was written

        var fixedUp = Project("refresh: { hourly: incremental }\nrefresh-: [daily]\n", folderFile: "defaults:\n  refresh: { daily: default }\n", summaryRefresh: "refresh=: { weekly: default }\n");
        Assert.Equal(0, Compile(fixedUp).Exit);
        Assert.Equal(["hourly", "weekly"], Directory.EnumerateFiles(Path.Combine(fixedUp, "rendered/sqlserver/refresh")).Select(f => Path.GetFileNameWithoutExtension(f)!).Order());
        Assert.Equal(["marts.fct_events.incremental"], Read(fixedUp, "hourly").Plan.Members.Select(m => m.FullName));
        Assert.Equal(["marts.summary.default"], Read(fixedUp, "weekly").Plan.Members.Select(m => m.FullName));

        var added = Project("refresh: { hourly: incremental, daily: incremental }\n", folderFile: "defaults:\n  refresh: { daily: default }\n");
        Assert.Equal(0, Compile(added).Exit);                                                                       // a scalar of the model replaces the folder's scalar for the same plan
        Assert.Equal(["marts.fct_events.incremental", "marts.summary.default"], Read(added, "daily").Plan.Members.Select(m => m.FullName));
    }

    // ---------------- compiling ----------------

    [Fact]
    public void A_model_with_a_default_load_and_no_refresh_is_in_the_plan_default_and_declaring_refresh_replaces_that()
    {
        var dir = Project("");
        Assert.Equal(0, Compile(dir).Exit);
        Assert.Equal(["marts.fct_events.incremental", "marts.summary.default"], Read(dir, "default").Plan.Members.Select(m => m.FullName));

        var declared = Project("refresh: { hourly: incremental }\n");
        Assert.Equal(0, Compile(declared).Exit);
        Assert.Equal(["marts.summary.default"], Read(declared, "default").Plan.Members.Select(m => m.FullName));    // fct_events chose its plans, so it is not in `default`
    }

    [Fact]
    public void The_same_load_twice_in_one_plan_runs_twice_in_the_order_written_with_each_ones_inputs()
    {
        var dir = Project("refresh:\n  monthly:\n    - { load: reload, as: recent, inputs: { start: '2024-01-01 00:00:00', end: '2024-01-05 00:00:00' } }\n    - { load: reload, as: older, inputs: { start: '2023-01-01 00:00:00', end: '2023-01-05 00:00:00' } }\n");
        Assert.Equal(0, Compile(dir).Exit);
        var (plan, loads) = Read(dir, "monthly");
        Assert.Equal(["marts.fct_events.recent", "marts.fct_events.older"], plan.Members.Select(m => m.FullName));
        Assert.Single(plan.Members.Select(m => m.LoadPlanHash).Distinct());                           // one load plan
        Assert.Equal("2024-01-01 00:00:00", plan.Members[0].Inputs.Single(i => i.Name == "start").Value);
        Assert.Equal("2023-01-01 00:00:00", plan.Members[1].Inputs.Single(i => i.Name == "start").Value);
        Assert.Contains(loads.Loads, l => l.Hash == plan.Members[0].LoadPlanHash);
    }

    [Fact]
    public void A_membership_of_a_load_that_needs_a_person_must_give_the_values_and_they_must_fit()
    {
        var (exit, _, err) = Compile(Project("refresh: { backfill: reload }\n"));
        Assert.NotEqual(0, exit);
        Assert.Contains("DDB-449", err);
        Assert.Contains("start", err);

        var unknown = Compile(Project("refresh: { backfill: { load: reload, inputs: { start: '2024-01-01 00:00:00', end: '2024-01-02 00:00:00', nope: 1 } } }\n"));
        Assert.Contains("DDB-246", unknown.Err);
        var badValue = Compile(Project("refresh: { backfill: { load: reload, inputs: { start: yesterday, end: '2024-01-02 00:00:00' } } }\n"));
        Assert.Contains("DDB-246", badValue.Err);
        Assert.Contains("TIMESTAMP", badValue.Err);
        Assert.Equal(0, Compile(Project("refresh: { backfill: { load: reload, inputs: { start: '2024-01-01 00:00:00', end: '2024-01-02 00:00:00' } } }\n")).Exit);
        Assert.Equal(0, Compile(Project("refresh: { hourly: { load: incremental, inputs: { watermark: '2024-01-01 00:00:00' } } }\n")).Exit);       // a membership may replace a resolver's value too
    }

    [Fact]
    public void The_settings_of_a_plan_and_its_words_are_kept_in_its_file_and_a_declared_plan_with_no_member_still_has_one()
    {
        var dir = Project("refresh: { hourly: incremental }\n", "refresh:\n  plans:\n    hourly: { title: Events, description: \"Every hour\", tags: [a], properties: {tier: 1}, on_failure: stop, on_busy: skip, expected_every: 1h, parallel: 2, check: live, on_fail: warn }\n    quiet: {}\n");
        Assert.Equal(0, Compile(dir).Exit);
        var hourly = Read(dir, "hourly").Plan;
        Assert.Equal(("Events", "Every hour"), (hourly.Info.Title, hourly.Info.Description));
        Assert.Equal(["a"], hourly.Info.Tags);
        Assert.Equal(("stop", "skip", "1h", 2, "live", "warn"), (hourly.Settings.OnFailure, hourly.Settings.OnBusy, hourly.Settings.ExpectedEvery, hourly.Settings.Parallel, hourly.Settings.Check, hourly.Settings.OnFail));
        Assert.Empty(Read(dir, "quiet").Plan.Members);
        Assert.Equal(0, RenderFiles("--check", "--project", dir).Exit);                                             // and the check covers them
        File.AppendAllText(Path.Combine(dir, "rendered/sqlserver/refresh/hourly.yml"), "# edited\n");
        Assert.NotEqual(0, RenderFiles("--check", "--project", dir).Exit);
    }

    [Fact]
    public void A_plan_that_is_no_longer_declared_loses_its_file_on_the_next_compile()
    {
        var dir = Project("refresh: { hourly: incremental }\n");
        Assert.Equal(0, Compile(dir).Exit);
        Assert.True(File.Exists(Path.Combine(dir, "rendered/sqlserver/refresh/hourly.yml")));
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_events.yml"), FctHead + Cols + Loads);
        Assert.NotEqual(0, RenderFiles("--check", "--project", dir).Exit);
        Assert.Equal(0, Compile(dir).Exit);
        Assert.False(File.Exists(Path.Combine(dir, "rendered/sqlserver/refresh/hourly.yml")));
    }

    // ---------------- selecting and inputs ----------------

    private static (RefreshPlanFile Plan, LoadsFile Loads, string Dir) Compiled(string refresh, string? summaryRefresh = null)
    {
        var dir = Project(refresh, summaryRefresh: summaryRefresh);
        Assert.Equal(0, Compile(dir).Exit);
        var (p, l) = Read(dir, "daily");
        return (p, l, dir);
    }

    [Fact]
    public void Selectors_are_the_model_grammar_plus_load_and_patterns_and_a_selection_keeps_the_plans_order()
    {
        var (plan, _, dir) = Compiled("refresh: { daily: [incremental, { load: reload, as: back, inputs: { start: '2024-01-01 00:00:00', end: '2024-01-02 00:00:00' } }] }\ntags: [fct]\n", "refresh: { daily: default }\ntags: [report]\n");
        var error = new StringWriter();
        HashSet<string>? Pick(params string[] s) => RefreshCommand.Select(plan, [.. s], dir, error);
        Assert.Equal(["marts.fct_events.incremental", "marts.fct_events.back", "marts.summary.default"], plan.Members.Select(m => m.FullName));
        Assert.Equal(3, Pick()!.Count);
        Assert.Equal(["marts.fct_events.back", "marts.fct_events.incremental"], Pick("tag:fct")!.Order());
        Assert.Equal(["marts.summary.default"], Pick("tag:report")!.Order());
        Assert.Equal(["marts.fct_events.back"], Pick("tag:fct", "load:reload")!.Order());                         // load: chooses by the load's name, within what the other selectors chose
        Assert.Equal(["marts.fct_events.incremental", "marts.summary.default"], Pick("exclude:load:reload")!.Order());
        Assert.Equal(["marts.fct_events.back", "marts.fct_events.incremental"], Pick("marts.fct*")!.Order());
        Assert.Equal(["marts.summary.default"], Pick("exclude:tag:fct")!.Order());
        Assert.Equal(["marts.fct_events.back", "marts.fct_events.incremental", "marts.summary.default"], Pick("+marts.summary")!.Order());   // the model and what it reads
        Assert.Null(Pick("tag:nothing"));
        Assert.Contains("selects no model", error.ToString());
    }

    [Fact]
    public void An_input_takes_the_flag_then_the_membership_then_the_default_and_the_source_is_recorded()
    {
        var (plan, loads, dir) = Compiled("refresh: { daily: [incremental, { load: reload, as: back, inputs: { start: '2024-01-01 00:00:00', end: '2024-01-02 00:00:00' } }] }\n");
        RefreshCommand.RunItem Item(string member) { var m = plan.Members.Single(x => x.FullName == member); return new(loads.Loads.Single(l => l.Hash == m.LoadPlanHash), m, 1); }
        var incremental = Item("marts.fct_events.incremental"); var back = Item("marts.fct_events.back");
        var result = new Dictionary<RefreshCommand.RunItem, List<RefreshCommand.EffectiveInput>>();
        var error = new StringWriter();

        Assert.True(RefreshCommand.ResolveInputs([incremental, back], [], "sqlserver", result, error), error.ToString());
        Assert.Equal([("watermark", "resolver", (string?)null)], result[incremental].Select(i => (i.Name, i.Source, i.Value)));
        Assert.Equal([("start", "membership", "2024-01-01 00:00:00"), ("end", "membership", "2024-01-02 00:00:00")], result[back].Select(i => (i.Name, i.Source, (string?)i.Value)));

        result.Clear();
        var twice = new List<RefreshCommand.InputFlag> { new(null, "start", "2025-05-05 00:00:00", "start=2025-05-05 00:00:00") };
        Assert.False(RefreshCommand.ResolveInputs([back, back with { Position = 2 }], twice, "sqlserver", result, error));
        Assert.Contains("fits more than one load", error.ToString());                                                  // two memberships of one load: name which
        result.Clear();
        var flags = new List<RefreshCommand.InputFlag> { new(null, "start", "2025-05-05 00:00:00", "start=2025-05-05 00:00:00"), new("marts.fct_events.incremental", "watermark", "2025-01-01 00:00:00", "y") };
        _ = flags;
        flags = [new(null, "start", "2025-05-05 00:00:00", "start=2025-05-05 00:00:00"), new("marts.fct_events.incremental", "watermark", "2025-01-01 00:00:00", "y")];
        Assert.True(RefreshCommand.ResolveInputs([incremental, back], flags, "sqlserver", result, new StringWriter()));
        Assert.Equal(("flag", "2025-01-01 00:00:00"), (result[incremental][0].Source, result[incremental][0].Value));       // a flag replaces the resolver, which is then not run
        Assert.Equal([("start", "flag", "2025-05-05 00:00:00"), ("end", "membership", "2024-01-02 00:00:00")], result[back].Select(i => (i.Name, i.Source, (string?)i.Value)));

        var bad = new List<RefreshCommand.InputFlag> { new(null, "nope", "1", "nope=1") };
        Assert.False(RefreshCommand.ResolveInputs([back], bad, "sqlserver", new Dictionary<RefreshCommand.RunItem, List<RefreshCommand.EffectiveInput>>(), error));
        Assert.Contains("none of the loads to run has an input called `nope`", error.ToString());
        var wrongType = new List<RefreshCommand.InputFlag> { new(null, "start", "tomorrow", "start=tomorrow") };
        Assert.False(RefreshCommand.ResolveInputs([back], wrongType, "sqlserver", new Dictionary<RefreshCommand.RunItem, List<RefreshCommand.EffectiveInput>>(), error));
    }

    // ---------------- the command, up to the connection ----------------

    private static (int Exit, string Out, string Err) Cli(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: v => v is "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" ? "Server=127.0.0.1,1;User Id=r;Password=x;Connect Timeout=1" : null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void The_command_names_the_plan_in_its_header_lists_the_plans_when_there_is_no_default_and_refuses_an_unknown_one()
    {
        var dir = Project("refresh: { hourly: incremental }\n", summaryRefresh: "refresh: { daily: default }\n");
        Assert.Equal(0, Compile(dir).Exit);
        var none = Cli(dir, "connection", "refresh");
        Assert.Equal(2, none.Exit);
        Assert.Contains("no plan called `default`", none.Out);
        Assert.Contains("Next:", none.Out);
        Assert.Contains("connection refresh sqlserver daily", none.Out);
        var unknown = Cli(dir, "connection", "refresh", "weekly");
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("DDB-450", unknown.Err);
        var run = Cli(dir, "connection", "refresh", "hourly", "--check", "none");
        Assert.Contains("plan: hourly", run.Out);                                                                   // the header names it
        Assert.Contains("Refresh plan rendered/sqlserver/refresh/hourly.yml: 1 of 1 member(s)", run.Out);
        var direct = Cli(dir, "connection", "refresh", "marts.fct_events.incremental", "--check", "none");
        Assert.Contains("1 load(s) named directly (no plan)", direct.Out);
        var mixed = Cli(dir, "connection", "refresh", "marts.fct_events.incremental", "tag:x");
        Assert.Equal(2, mixed.Exit);
        var needs = Cli(dir, "connection", "refresh", "marts.fct_events.reload", "--check", "none");
        Assert.Equal(2, needs.Exit);
        Assert.Contains("DDB-449", needs.Err);
        Assert.Contains("--input marts.fct_events.reload.start=", needs.Err);                                       // it says how to give the value
        var badFlag = Cli(dir, "connection", "refresh", "hourly", "--input", "novalue");
        Assert.Equal(2, badFlag.Exit);
    }

    // ---------------- seeing ----------------

    [Fact]
    public void Project_show_refresh_lists_the_plans_and_shows_one_with_members_inputs_and_hashes_in_text_and_json()
    {
        var dir = Project("refresh:\n  daily:\n    - incremental\n    - { load: reload, as: back, inputs: { start: '2024-01-01 00:00:00', end: '2024-01-02 00:00:00' } }\n", summaryRefresh: "refresh: { daily: default }\n");
        Assert.Equal(0, Compile(dir).Exit);
        var list = Cli(dir, "project", "show", "refresh");
        Assert.Equal(0, list.Exit);
        Assert.Contains("daily", list.Out);
        var one = Cli(dir, "project", "show", "refresh", "daily");
        Assert.Contains("marts.fct_events.back  [delete_insert_by_range]", one.Out);
        Assert.Contains("@start (TIMESTAMP): 2024-01-01 00:00:00  (membership)", one.Out);
        Assert.Contains("@watermark (TIMESTAMP): found when it runs  (resolver)", one.Out);
        var json = JsonNode.Parse(Cli(dir, "project", "show", "refresh", "daily", "--format", "json").Out)!["data"]!["plans"]![0]!;
        Assert.Equal("daily", json["name"]!.GetValue<string>());
        Assert.Equal(3, json["load_members"]!.AsArray().Count);
        Assert.Equal("membership", json["load_members"]![1]!["inputs"]![0]!["source"]!.GetValue<string>());
        Assert.Equal(2, Cli(dir, "project", "show", "refresh", "nothing").Exit);
    }
}
