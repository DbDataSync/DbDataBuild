using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Planning;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The compiled load plans and refresh plans: what `project compile` writes for a connection (`loads.yml`, `refresh/default.yml`), and that the files are read back, checked against their own hash and never trusted when edited.</summary>
public class RefreshPlanTests
{
    private const string Config = "defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n";
    private const string Events = "name: staging.events\nkind: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctYaml = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n" +
        "loads:\n  daily:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: \"2000-01-01 00:00:00\"}\n" +
        "  reload:\n    strategy: delete_insert_by_range\n    params: {start: TIMESTAMP, end: TIMESTAMP}\n    max_span: 10 days\n";
    private const string FctSql = "SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n";
    private const string ViewYaml = "name: marts.v_events\nkind: view\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n";
    private const string ViewSql = "SELECT event_id FROM marts.fct_events\n";
    private const string DailyYaml = "name: marts.daily\nkind: full\ngrain: [d]\ncolumns:\n  - {name: d, type: DATE, nullable: false}\n";
    private const string DailySql = "SELECT CAST(event_ts AS DATE) AS d FROM marts.fct_events GROUP BY ALL\n";

    private static string Project()
    {
        var dir = NewProjectDir();
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Write("dbdatabuild.yml", Config);
        Write("models/staging/events.yml", Events);
        Write("models/marts/fct_events.yml", FctYaml); Write("models/marts/fct_events.sql", FctSql);
        Write("models/marts/v_events.yml", ViewYaml); Write("models/marts/v_events.sql", ViewSql);
        Write("models/marts/daily.yml", DailyYaml); Write("models/marts/daily.sql", DailySql);
        return dir;
    }

    private static LoadsFile Compiled(string dir)
    {
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var path = Path.Combine(dir, "rendered", "sqlserver", LoadsDocument.FileName);
        var diags = new List<Diagnostic>();
        var file = LoadsDocument.Parse(File.ReadAllText(path), path, diags);
        Assert.True(file != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return file!;
    }

    private static RefreshPlanFile CompiledPlan(string dir, string name = "default")
    {
        var path = Path.Combine(dir, "rendered", "sqlserver", "refresh", name + ".yml");
        var diags = new List<Diagnostic>();
        var plan = RefreshPlanDocument.Parse(File.ReadAllText(path), path, diags);
        Assert.True(plan != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return plan!;
    }

    [Fact]
    public void Compile_writes_every_load_in_dependency_order_with_what_each_expects_and_a_default_plan_of_the_default_loads()
    {
        var dir = Project();
        var file = Compiled(dir);
        Assert.Equal("sqlserver", file.Connection);
        // every load of every model, the non-default `reload` too; daily reads fct_events; the view has no load
        Assert.Equal(["marts.fct_events.daily", "marts.fct_events.reload", "marts.daily.default"], file.Loads.Select(l => l.FullName));
        var load = file.Loads[0];
        Assert.Equal(("daily", "sqlserver/marts.fct_events/load.daily.sql", "sqlserver/marts.fct_events/load.daily.resolve.sql"), (load.Load, load.File, load.ResolverFile));
        Assert.Matches("^[0-9a-f]{64}$", load.FileHash);
        Assert.Equal(("initial", "2000-01-01 00:00:00"), (load.OnNull, load.Initial));
        Assert.Equal([("watermark", "resolver")], load.Inputs.Select(p => (p.Name, p.Source)));
        Assert.Null(load.Inputs[0].Value);                                                                          // the watermark is found when the refresh runs, never recorded here
        var reload = file.Loads[1];
        Assert.Equal([("start", "required"), ("end", "required")], reload.Inputs.Select(p => (p.Name, p.Source)));      // a load that needs a person is a load plan; it is in no plan
        Assert.All(file.Loads, l => Assert.Matches("^[0-9a-f]{64}$", l.ShapeHash));
        Assert.Matches("^[0-9a-f]{64}$", file.ProjectHash);
        Assert.Empty(file.Excluded);

        var plan = CompiledPlan(dir);
        Assert.Equal(["marts.fct_events.daily", "marts.daily.default"], plan.Members.Select(m => m.FullName));        // the default load of each model, in dependency order
        Assert.Equal(file.Loads[0].Hash, plan.Members[0].LoadPlanHash);                                              // a member names its load plan by hash
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void A_model_whose_default_load_needs_a_person_is_a_load_plan_but_in_no_plan_without_a_membership_that_gives_the_values()
    {
        var dir = Project();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_events.yml"), FctYaml.Replace("    default: true\n    strategy: watermark_append", "    strategy: watermark_append").Replace("  reload:\n", "  reload:\n    default: true\n"));
        var file = Compiled(dir);
        Assert.Contains(file.Loads, l => l.Model == "marts.fct_events" && l.Load == "reload");
        var plan = CompiledPlan(dir);
        Assert.DoesNotContain(plan.Members, m => m.Model == "marts.fct_events");
        Assert.Contains(plan.Excluded, x => x.Model == "marts.fct_events" && x.Reason.Contains("needs a value from a person"));
    }

    [Fact]
    public void Compile_is_deterministic_and_a_change_that_alters_what_runs_alters_the_files_and_the_project_hash()
    {
        var dir = Project();
        var first = Compiled(dir);
        var firstPlan = CompiledPlan(dir);
        Assert.Equal(LoadsDocument.Serialize(first), LoadsDocument.Serialize(Compiled(dir)));
        File.WriteAllText(Path.Combine(dir, "models/marts/daily.yml"), DailyYaml + "  - {name: n, type: BIGINT}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/daily.sql"), "SELECT CAST(event_ts AS DATE) AS d, COUNT(*) AS n FROM marts.fct_events GROUP BY ALL\n");
        var second = Compiled(dir);
        Assert.NotEqual(first.ProjectHash, second.ProjectHash);
        Assert.NotEqual(first.Loads.Single(l => l.Model == "marts.daily").ShapeHash, second.Loads.Single(l => l.Model == "marts.daily").ShapeHash);
        Assert.Equal(first.Loads.Single(l => l.FullName == "marts.fct_events.daily").Hash, second.Loads.Single(l => l.FullName == "marts.fct_events.daily").Hash);       // what did not change did not move
        Assert.NotEqual(firstPlan.Hash, CompiledPlan(dir).Hash);                                                    // the plan names the changed load plan by its new hash
    }

    [Fact]
    public void A_file_that_was_edited_or_damaged_is_refused_with_a_diagnostic_never_an_exception()
    {
        var dir = Project();
        var text = LoadsDocument.Serialize(Compiled(dir));
        var diags = new List<Diagnostic>();
        Assert.NotNull(LoadsDocument.Parse(text, "loads.yml", diags));
        Assert.Empty(diags);
        foreach (var bad in new[] { text.Replace("marts.daily", "marts.dailyx"), text.Replace("\"default\"", "\"other\""), text.Replace("loads: 3", "loads: 4"), text + "extra: 1\n", "", "- not a mapping\n", text[..(text.Length / 2)] })
        {
            var d = new List<Diagnostic>();
            Assert.Null(LoadsDocument.Parse(bad, "loads.yml", d));
            Assert.NotEmpty(d);
        }
        var random = new Random(7);
        for (var i = 0; i < 300; i++)
        {
            var chars = text.ToCharArray();
            chars[random.Next(chars.Length)] = "x:-\n\"'#{ ["[random.Next(10)];
            var parsed = LoadsDocument.Parse(new string(chars), "loads.yml", []);
            if (parsed != null) Assert.Equal(text, LoadsDocument.Serialize(parsed));        // a mutation that still reads is the same file
        }

        var planText = RefreshPlanDocument.Serialize(CompiledPlan(dir));
        Assert.NotNull(RefreshPlanDocument.Parse(planText, "default.yml", []));
        foreach (var bad in new[] { planText.Replace("marts.daily", "marts.dailyx"), planText.Replace("members: 2", "members: 3"), planText + "extra: 1\n", "", "- not a mapping\n", planText[..(planText.Length / 2)] })
        {
            var d = new List<Diagnostic>();
            Assert.Null(RefreshPlanDocument.Parse(bad, "default.yml", d));
            Assert.NotEmpty(d);
        }
        for (var i = 0; i < 300; i++)
        {
            var chars = planText.ToCharArray();
            chars[random.Next(chars.Length)] = "x:-\n\"'#{ ["[random.Next(10)];
            var parsed = RefreshPlanDocument.Parse(new string(chars), "default.yml", []);
            if (parsed != null) Assert.Equal(planText, RefreshPlanDocument.Serialize(parsed));
        }
    }

    [Fact]
    public void A_selection_of_models_does_not_rewrite_the_files_and_the_old_single_plan_file_is_removed()
    {
        var dir = Project();
        Compiled(dir);
        var path = Path.Combine(dir, "rendered", "sqlserver", LoadsDocument.FileName);
        var planPath = Path.Combine(dir, "rendered", "sqlserver", "refresh", "default.yml");
        var before = File.ReadAllText(path); var planBefore = File.ReadAllText(planPath);
        var retired = Path.Combine(dir, "rendered", "sqlserver", "refresh.plan.yml");
        File.WriteAllText(retired, "refresh_plan:\n");                                    // what an older tool left
        Assert.Equal(0, RenderFiles("--write", "marts.daily", "--project", dir).Exit);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(planBefore, File.ReadAllText(planPath));
        Assert.True(File.Exists(retired));                                                  // a selection touches only the models' folders
        Assert.NotEqual(0, RenderFiles("--check", "--project", dir).Exit);                 // and the check names it
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        Assert.False(File.Exists(retired));                                                 // a whole compile removes it: breaking, no alias
        Assert.Equal(0, RenderFiles("--check", "--project", dir).Exit);
    }
}
