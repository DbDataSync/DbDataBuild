using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Execution;
using DbDataBuild.Planning;
using DbDataBuild.State;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Inputs and resolvers of a load (docs/research/refresh-plans.md, section 8): declaring them, compiling them (DDB-451, DDB-452), resolving them (DDB-453) with a fake connection.</summary>
public class InputResolversTests
{
    private const string Config = "defaults: {connections: [sqlserver]}\nparameters:\n  window_days: {type: INTEGER, value: \"30\"}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n";
    private const string Events = "name: staging.events\nkind: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: event_day, type: DATE, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctHead = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_day}}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: event_day, type: DATE, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctSql = "SELECT e.event_id, e.event_ts, e.event_day, e.amount FROM staging.events e\n";

    private static string Loads(string inputs, string extra = "") =>
        "loads:\n  incremental:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: \"2000-01-01 00:00:00\"}\n" +
        "  backfill:\n    strategy: delete_insert_by_range\n    column: event_day\n" + inputs + extra;

    private static string Project(string inputs, string refresh = "refresh: { daily: backfill }\n")
    {
        var dir = NewProjectDir();
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Write("dbdatabuild.yml", Config);
        Write("models/staging/events.yml", Events);
        Write("models/marts/fct_events.yml", FctHead + Loads(inputs) + refresh);
        Write("models/marts/fct_events.sql", FctSql);
        return dir;
    }

    private const string Window = "    inputs:\n      days: {type: INTEGER, default: 7}\n      start: {resolver: {expression: \"current_date - days\"}}\n      end: {resolver: {expression: \"current_date + 1\"}}\n";

    private static (RefreshPlanFile Plan, LoadsFile Loads) Read(string dir, string plan)
    {
        var diags = new List<Diagnostic>();
        var loads = LoadsDocument.Parse(File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/loads.yml")), "loads.yml", diags);
        var file = RefreshPlanDocument.Parse(File.ReadAllText(Path.Combine(dir, $"rendered/sqlserver/refresh/{plan}.yml")), plan + ".yml", diags);
        Assert.True(loads != null && file != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return (file!, loads!);
    }

    // ---------------- declaring ----------------

    private static string Codes(string inputs, string strategy = "range", string kind = "")
    {
        var model = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_day}}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: event_day, type: DATE, nullable: false}\n" + Loads(inputs);
        var d = new List<Diagnostic>();
        ModelDefinitionLoader.Load(model, "models/marts/fct_events.yml", "marts.fct_events", d);
        return string.Join(",", d.Select(x => x.Code).Distinct());
    }

    [Fact]
    public void Inputs_are_a_type_a_default_or_a_resolver_and_a_range_loads_start_and_end_take_the_range_columns_type()
    {
        var model = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_day}}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: event_day, type: DATE, nullable: false}\n" + Loads(Window);
        var d = new List<Diagnostic>();
        var def = ModelDefinitionLoader.Load(model, "models/marts/fct_events.yml", "marts.fct_events", d);
        Assert.Empty(d);
        var backfill = def!.Loads.First(l => l.Name == "backfill");
        Assert.Equal(["days", "start", "end"], backfill.InputList.Select(i => i.Name));
        Assert.Equal("INTEGER", backfill.InputList[0].Type);
        Assert.Equal("7", backfill.InputList[0].Default);
        Assert.Equal("DATE", backfill.InputList[1].Type);                                               // the range column's own type
        Assert.Equal(("expression", "current_date - days"), (backfill.InputList[1].Resolver!.Kind, backfill.InputList[1].Resolver!.Text));
        Assert.Equal("fail", backfill.InputList[1].OnNull);
    }

    [Fact]
    public void Damaged_input_declarations_are_diagnostics_never_exceptions()
    {
        Assert.Contains("DDB-105", Codes("    inputs:\n      days: {default: 7}\n"));                                                       // no type
        Assert.Contains("DDB-106", Codes("    inputs:\n      days: {type: BLOB}\n"));
        Assert.Contains("DDB-104", Codes("    inputs:\n      days: {type: INTEGER, bogus: 1}\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      days: {type: INTEGER, default: soon}\n"));                                    // not an integer
        Assert.Contains("DDB-106", Codes("    inputs:\n      Days: {type: INTEGER}\n"));
        Assert.Contains("DDB-106", Codes("    inputs: [1]\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      days: 7\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      start: {type: BIGINT}\n"));                                                 // the range column is a DATE
        Assert.Contains("DDB-106", Codes("    inputs:\n      start: {resolver: {target: \"SELECT 1\", expression: \"1\"}}\n"));            // exactly one kind
        Assert.Contains("DDB-106", Codes("    inputs:\n      start: {resolver: {expression: \"\"}}\n"));
        Assert.Contains("DDB-104", Codes("    inputs:\n      start: {resolver: {magic: \"1\"}}\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      start: {resolver: target_max}\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      start: {resolver: {source: \"SELECT 1\"}}\n"));                               // only a copy has a source
        Assert.Contains("DDB-106", Codes("    inputs:\n      start: {on_null: skip_load}\n"));                                             // nothing to find NULL
        Assert.Contains("DDB-105", Codes("    inputs:\n      start: {resolver: {expression: \"current_date\"}, on_null: initial}\n"));     // initial needs a default
        Assert.Contains("DDB-106", Codes("    inputs:\n      start: {resolver: {expression: \"current_date\"}, default: 2020-01-01}\n"));  // a default beside a resolver is only the initial value
        Assert.Contains("DDB-106", Codes("    inputs:\n      start: {resolver: {expression: \"current_date\"}, on_null: sometimes}\n"));
        // provides: the group needs a resolver, no type, and every provided name is declared once without a resolver of its own
        Assert.Contains("DDB-105", Codes("    inputs:\n      w: {provides: [start, end]}\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      w: {type: DATE, resolver: {expression: \"SELECT 1\"}, provides: [start, end]}\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      w: {resolver: {expression: \"SELECT 1\"}, provides: [start, nope]}\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      w: {resolver: {expression: \"SELECT 1\"}, provides: [start, w]}\n"));
        Assert.Contains("DDB-106", Codes("    inputs:\n      w: {resolver: {expression: \"SELECT 1\"}, provides: [start, end]}\n      start: {resolver: {expression: \"1\"}}\n"));
        Assert.Equal("", Codes("    inputs:\n      w: {resolver: {expression: \"SELECT current_date - 7 AS start, current_date + 1 AS \\\"end\\\"\"}, provides: [start, end]}\n"));
        // the watermark input belongs to the watermark block
        var wm = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_ts}}\nloads:\n  incremental:\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max}\n    inputs:\n      watermark: {type: TIMESTAMP}\n";
        var d = new List<Diagnostic>();
        ModelDefinitionLoader.Load(wm, "models/marts/fct_events.yml", "marts.fct_events", d);
        Assert.Contains(d, x => x.Code == "DDB-106");
    }

    [Fact]
    public void A_copy_declares_a_range_load_with_the_types_of_its_range_and_may_have_a_source_resolver()
    {
        string Codes(string yaml) { var d = new List<Diagnostic>(); ModelDefinitionLoader.Load(yaml, "models/dst/events.yml", "dst.events", d); return string.Join(",", d.Select(x => x.Code).Distinct()); }
        var head = "name: dst.events\nkind: {copy: {from: src.events}}\nloads:\n  window:\n    strategy: delete_insert_by_range\n    column: day\n";
        var inputs = "    inputs:\n      start: {resolver: {source: \"SELECT min(day) FROM src.events\"}}\n      end: {resolver: {expression: \"current_date\"}}\n";
        Assert.Equal("", Codes(head + "    params: {start: DATE, end: DATE}\n" + inputs));
        Assert.Contains("DDB-105", Codes(head + inputs));                                                                         // the types are not known without params
        Assert.Contains("DDB-106", Codes(head + "    params: {start: BLOB, end: DATE}\n"));
        var d = new List<Diagnostic>();
        var def = ModelDefinitionLoader.Load(head + "    params: {start: DATE, end: DATE}\n" + inputs, "models/dst/events.yml", "dst.events", d);
        Assert.Equal(("source", "DATE"), (def!.Loads[0].InputList[0].Resolver!.Kind, def.Loads[0].InputList[0].Type));
    }

    [Fact]
    public void Damaged_input_text_never_throws()
    {
        var model = "name: marts.fct_events\nkind: {incremental_by_time_range: {time_column: event_day}}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: event_day, type: DATE, nullable: false}\n" + Loads(Window);
        var random = new Random(7);
        var from = model.IndexOf("    inputs:", StringComparison.Ordinal);
        for (var i = 0; i < 500; i++)
        {
            var chars = model.ToCharArray();
            chars[random.Next(from, chars.Length)] = "x:-\n\"'#{ [],"[random.Next(12)];
            ModelDefinitionLoader.Load(new string(chars), "models/marts/fct_events.yml", null, new List<Diagnostic>());
        }
    }

    [Fact]
    public void A_timezone_is_validated_at_the_project_and_the_connection()
    {
        var d = new List<Diagnostic>();
        var ok = ProjectConfigLoader.Load("timezone: Europe/Berlin\nconnections:\n  sqlserver: { timezone: America/New_York }\n  postgres: {}\n", "dbdatabuild.yml", d);
        Assert.Empty(d);
        Assert.Equal("America/New_York", ok!.TimezoneOf("sqlserver"));
        Assert.Equal("Europe/Berlin", ok.TimezoneOf("postgres"));
        Assert.Equal("UTC", ProjectConfigLoader.Load("{}\n", "dbdatabuild.yml", new List<Diagnostic>())!.TimezoneOf("sqlserver"));
        foreach (var bad in new[] { "timezone: Mars/Olympus\n", "timezone: 3\n", "timezone: [a]\n", "connections:\n  sqlserver: { timezone: nowhere }\n" })
        {
            var diags = new List<Diagnostic>();
            Assert.Null(ProjectConfigLoader.Load(bad, "dbdatabuild.yml", diags));
            Assert.Contains(diags, x => x.Code == "DDB-106");
        }
    }

    // ---------------- compiling ----------------

    [Fact]
    public void Compiling_writes_the_inputs_into_the_load_plan_and_a_change_to_a_resolver_changes_its_hash()
    {
        var dir = Project(Window, "refresh: { daily: { load: backfill, inputs: { days: 60 } } }\n");
        var (exit, _, err) = RenderFiles("--write", "--project", dir);
        Assert.True(exit == 0, err);
        var (plan, loads) = Read(dir, "daily");
        var backfill = loads.Find("marts.fct_events", "backfill")!;
        Assert.Equal(["start", "end", "days"], backfill.Inputs.Select(i => i.Name));
        var days = backfill.Inputs[2];
        Assert.Equal(("default", "7", true), (days.Source, days.Value, days.Free));
        var start = backfill.Inputs[0];
        Assert.Equal(("computed", "DATE", false, "expression", "current_date - days"), (start.Source, start.Type, start.Free, start.Resolver!.Kind, start.Resolver.Text));
        // a membership's value for a free input is accepted, and the plan names the load plan by its hash
        Assert.Equal([new RefreshInputValue("days", "60")], plan.Members.Single().Inputs);
        Assert.Equal(backfill.Hash, plan.Members.Single().LoadPlanHash);
        var before = (Load: backfill.Hash, Plan: plan.Hash);

        var dir2 = Project(Window.Replace("current_date + 1", "current_date + 2"), "refresh: { daily: { load: backfill, inputs: { days: 60 } } }\n");
        Assert.Equal(0, RenderFiles("--write", "--project", dir2).Exit);
        var (plan2, loads2) = Read(dir2, "daily");
        Assert.NotEqual(before.Load, loads2.Find("marts.fct_events", "backfill")!.Hash);
        Assert.NotEqual(before.Plan, plan2.Hash);
        Assert.Equal(loads.Find("marts.fct_events", "incremental")!.Hash, loads2.Find("marts.fct_events", "incremental")!.Hash);      // the other load did not change
        Assert.Equal(0, RenderFiles("--check", "--project", dir2).Exit);
    }

    [Fact]
    public void A_target_resolver_is_lowered_for_the_engine_and_committed_in_the_load_plan_beside_the_name_of_its_input()
    {
        var dir = Project("    inputs:\n      end: {resolver: {target: \"SELECT max(event_day) + 1 FROM marts.fct_events\"}}\n      start: {resolver: {expression: '\"end\" - 30'}}\n");
        var (exit, _, err) = RenderFiles("--write", "--project", dir);
        Assert.True(exit == 0, err);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "rendered/sqlserver/marts.fct_events"), "input.*"));                       // no file of its own
        var loadsFile = Path.Combine(dir, "rendered/sqlserver/loads.yml");
        var (_, loads) = Read(dir, "daily");
        var end = loads.Find("marts.fct_events", "backfill")!.Inputs.Single(i => i.Name == "end");
        Assert.Equal("target", end.Resolver!.Kind);
        Assert.Contains("input resolver", end.Resolver.Text);
        Assert.Contains("MAX", end.Resolver.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\r", end.Resolver.Text);
        var yaml = File.ReadAllText(loadsFile);
        Assert.Contains("name: \"end\"", yaml);                                                                                    // the name of the input is beside its query
        Assert.Contains("MAX(", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, RenderFiles("--check", "--project", dir).Exit);
        File.WriteAllText(loadsFile, yaml.Replace("MAX(", "MIN(", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(0, RenderFiles("--check", "--project", dir).Exit);                                                           // the text is hashed with the plan: an edit is a refusal
    }

    [Fact]
    public void An_expression_may_name_a_parameter_and_the_load_plan_carries_the_value_it_had()
    {
        var dir = Project("    inputs:\n      start: {resolver: {expression: \"current_date - ${project.window_days}\"}}\n      end: {resolver: {expression: \"current_date + 1\"}}\n");
        var (exit, _, err) = RenderFiles("--write", "--project", dir);
        Assert.True(exit == 0, err);
        var (_, loads) = Read(dir, "daily");
        var start = loads.Find("marts.fct_events", "backfill")!.Inputs.Single(i => i.Name == "start");
        Assert.Equal("current_date - ${project.window_days}", start.Resolver!.Text);
        Assert.Equal([new RefreshParameterRef("project.window_days", "INTEGER", "30")], start.Resolver.Parameters);
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), Config.Replace("\"30\"", "\"45\""));
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var (_, changed) = Read(dir, "daily");
        Assert.NotEqual(loads.Find("marts.fct_events", "backfill")!.Hash, changed.Find("marts.fct_events", "backfill")!.Hash);          // the value is part of the load plan
    }

    [Fact]
    public void A_resolver_that_cannot_be_compiled_is_DDB_451_and_a_circle_is_DDB_452()
    {
        string Err(string inputs) { var (e, _, err) = RenderFiles("--write", "--project", Project(inputs)); Assert.NotEqual(0, e); return err; }
        Assert.Contains("DDB-451", Err("    inputs:\n      start: {resolver: {expression: \"current_date - nope\"}}\n"));                      // not an input
        Assert.Contains("DDB-451", Err("    inputs:\n      start: {resolver: {expression: \"SELECT * FROM staging.events\"}}\n"));              // a table
        Assert.Contains("DDB-451", Err("    inputs:\n      start: {resolver: {expression: \"current_date -\"}}\n"));                           // not an expression
        Assert.Contains("DDB-451", Err("    inputs:\n      start: {resolver: {expression: \"current_date - ${project.nothing}\"}}\n"));        // no such parameter
        Assert.Contains("DDB-451", Err("    inputs:\n      start: {resolver: {expression: \"current_date - ${model.x}\"}}\n"));                  // a scope a resolver cannot use
        Assert.Contains("DDB-451", Err("    inputs:\n      start: {resolver: {target: \"SELECT max(nope) FROM marts.fct_events\"}}\n"));        // does not bind
        var circle = Err("    inputs:\n      start: {resolver: {expression: '\"end\" - 7'}}\n      end: {resolver: {expression: \"start + 7\"}}\n");
        Assert.Contains("DDB-452", circle);
        Assert.Contains("`start`", circle);
        Assert.Contains("DDB-452", Err("    inputs:\n      days: {type: INTEGER, resolver: {expression: \"days + 1\"}}\n"));
        // nothing was written for a project that does not compile
    }

    [Fact]
    public void A_plan_membership_may_give_a_free_input_and_a_value_for_a_computed_one_replaces_its_resolver()
    {
        var dir = Project(Window, "refresh: { daily: { load: backfill, inputs: { days: 60, end: 2024-06-01 } } }\n");
        var (exit, _, err) = RenderFiles("--write", "--project", dir);
        Assert.True(exit == 0, err);
        var (plan, _) = Read(dir, "daily");
        Assert.Equal(["days", "end"], plan.Members.Single().Inputs.Select(i => i.Name));
        var (bad, _, badErr) = RenderFiles("--write", "--project", Project(Window, "refresh: { daily: { load: backfill, inputs: { w: 1 } } }\n"));
        Assert.NotEqual(0, bad);
        Assert.Contains("DDB-246", badErr);
    }

    // ---------------- resolving (a fake connection) ----------------

    private static readonly InputRunContext Context = new(new DateTimeOffset(2026, 3, 1, 23, 30, 15, TimeSpan.Zero), "UTC", "daily", "sqlserver");

    private sealed class FakeRunner(Func<string, InputQueryResult> answer) : IInputQueryRunner
    {
        public List<string> Ran { get; } = [];
        public Task<InputQueryResult> RunAsync(RefreshInputResolver resolver, string text, string? origin, CancellationToken ct) { Ran.Add(text); return Task.FromResult(answer(text)); }
    }

    private static RefreshParameter Expr(string name, string type, string text, string onNull = "fail", string? initial = null, IReadOnlyList<string>? provides = null) =>
        new(name, type, RefreshParameter.Computed, null, new("expression", text, provides ?? [], []), Free: false, OnNull: onNull, Initial: initial);

    private static RefreshParameter Days() => new("days", "INTEGER", RefreshParameter.FromDefault, "7", Free: true);

    private static InputResolutionResult Resolve(IReadOnlyList<RefreshParameter> defs, IReadOnlyDictionary<string, (string, string)>? given = null, IInputQueryRunner? runner = null, Func<RefreshInputResolver, string?>? text = null) =>
        InputResolution.ResolveAsync("m.backfill", defs, given ?? new Dictionary<string, (string, string)>(), Context, runner, text ?? (_ => null), CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void Inputs_are_resolved_in_dependency_order_and_a_value_given_replaces_the_resolver()
    {
        var defs = new[] { Expr("start", "DATE", "current_date - days"), Days(), Expr("end", "DATE", "current_date + 1") };
        var r = Resolve(defs);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal([("start", "2026-02-22", "resolver:expression"), ("days", "7", "default"), ("end", "2026-03-02", "resolver:expression")], r.Inputs.Select(i => (i.Name, i.Value!, i.Source)));
        Assert.All(r.Inputs.Where(i => i.IsComputed), i => Assert.NotNull(i.ResolverHash));
        Assert.True(r.UsedClock);
        // --input days=30 flows into start
        var flowed = Resolve(defs, new Dictionary<string, (string, string)> { ["days"] = ("30", "flag") });
        Assert.Equal("2026-01-30", flowed.Inputs.Single(i => i.Name == "start").Value);
        Assert.Equal("flag", flowed.Inputs.Single(i => i.Name == "days").Source);
        // a value given for start: the resolver is not run, so even a broken one stands in nobody's way
        var broken = new[] { Expr("start", "DATE", "current_date - nope"), Days() };
        Assert.False(Resolve(broken).Ok);
        var pinned = Resolve(broken, new Dictionary<string, (string, string)> { ["start"] = ("2024-01-01", "membership") });
        Assert.True(pinned.Ok, pinned.Failure);
        Assert.Equal(("2024-01-01", "membership"), (pinned.Inputs.Single(i => i.Name == "start").Value!, pinned.Inputs.Single(i => i.Name == "start").Source));
    }

    [Fact]
    public void One_resolver_may_provide_several_inputs_by_column_name()
    {
        var group = new RefreshParameter("window", "", RefreshParameter.Computed, null, new("expression", "SELECT current_date - 7 AS start, current_date + 1 AS \"end\"", ["start", "end"], []), Free: true);
        var defs = new[] { group, new RefreshParameter("start", "DATE", RefreshParameter.Provided, null, ProvidedBy: "window"), new RefreshParameter("end", "DATE", RefreshParameter.Provided, null, ProvidedBy: "window") };
        var r = Resolve(defs);
        Assert.True(r.Ok, r.Failure);
        Assert.Equal([("start", "2026-02-22"), ("end", "2026-03-02")], r.Inputs.Select(i => (i.Name, i.Value!)));
        Assert.All(r.Inputs, i => Assert.Equal("resolver:expression", i.Source));
        // the other one given: the group still runs for the one that is not
        var one = Resolve(defs, new Dictionary<string, (string, string)> { ["start"] = ("2020-01-01", "flag") });
        Assert.Equal(("2020-01-01", "2026-03-02"), (one.Inputs.Single(i => i.Name == "start").Value!, one.Inputs.Single(i => i.Name == "end").Value!));
        // both given: the group is not run at all (it would fail: its column names are wrong)
        var wrongColumns = defs.Select(d => d.Name == "window" ? d with { Resolver = d.Resolver! with { Text = "SELECT 1 AS a, 2 AS b" } } : d).ToArray();
        Assert.Contains("name a column `start`", Resolve(wrongColumns).Failure);
        Assert.True(Resolve(wrongColumns, new Dictionary<string, (string, string)> { ["start"] = ("2020-01-01", "flag"), ["end"] = ("2020-02-01", "flag") }).Ok);
    }

    [Fact]
    public void NULL_goes_to_on_null_and_a_value_that_does_not_fit_is_an_error_naming_the_input_and_the_resolver()
    {
        var @null = Expr("start", "DATE", "NULL::DATE");
        var failed = Resolve([@null]);
        Assert.False(failed.Ok);
        Assert.Contains("DDB-453", failed.Failure);
        Assert.Contains("`start`", failed.Failure);
        Assert.Contains("m.backfill", failed.Failure);
        Assert.Contains("expression", failed.Failure);
        Assert.Contains("--input m.backfill.start=", failed.Failure);
        var initial = Resolve([Expr("start", "DATE", "NULL::DATE", "initial", "2000-01-01")]);
        Assert.Equal(("2000-01-01", "initial"), (initial.Inputs.Single().Value!, initial.Inputs.Single().Source));
        var skipped = Resolve([Expr("start", "DATE", "NULL::DATE", "skip_load")]);
        Assert.True(skipped.SkipLoad);
        Assert.Null(skipped.Failure);
        Assert.False(skipped.Ok);
        // no silent coercion
        var time = Resolve([Expr("start", "DATE", "now()")]);
        Assert.Contains("time of day", time.Failure);
        Assert.Contains("`start`", time.Failure);
        Assert.Contains("fraction", Resolve([Expr("n", "INTEGER", "7 / 2.0 + 0.1")]).Failure);
        Assert.Contains("could not be evaluated", Resolve([Expr("start", "DATE", "current_date - nope")]).Failure);
        Assert.Contains("is not a INTEGER", Resolve([Days()], new Dictionary<string, (string, string)> { ["days"] = ("many", "flag") }).Failure);
    }

    [Fact]
    public void A_target_resolver_returns_exactly_one_row_and_runs_only_when_nothing_replaces_it()
    {
        RefreshParameter Target(string name) => new(name, "DATE", RefreshParameter.Computed, null, new("target", "SELECT 1", [], []));
        var runner = new FakeRunner(_ => new(["x"], [[new DateTime(2026, 2, 28)]], null));
        var r = Resolve([Target("end")], runner: runner, text: _ => "SELECT 1");
        Assert.True(r.Ok, r.Failure);
        Assert.Equal(("2026-02-28", "resolver:target"), (r.Inputs.Single().Value!, r.Inputs.Single().Source));
        Assert.Single(runner.Ran);
        var given = new FakeRunner(_ => throw new InvalidOperationException("must not run"));
        Assert.True(Resolve([Target("end")], new Dictionary<string, (string, string)> { ["end"] = ("2024-01-01", "flag") }, given, _ => "SELECT 1").Ok);
        Assert.Empty(given.Ran);
        Assert.Contains("2 rows", Resolve([Target("end")], runner: new FakeRunner(_ => new(["x"], [[1], [2]], null)), text: _ => "SELECT 1").Failure);
        Assert.Contains("0 rows", Resolve([Target("end")], runner: new FakeRunner(_ => new(["x"], [], null)), text: _ => "SELECT 1").Failure);
        Assert.Contains("failed to run (SqlException)", Resolve([Target("end")], runner: new FakeRunner(_ => new([], [], "failed to run (SqlException)")), text: _ => "SELECT 1").Failure);
        Assert.Contains("2 columns", Resolve([Target("end")], runner: new FakeRunner(_ => new(["a", "b"], [[1, 2]], null)), text: _ => "SELECT 1").Failure);
        Assert.Contains("no text", Resolve([Target("end")], runner: runner).Failure);
        // offline: the target resolver is not run, and what depends on nothing else still is
        var offline = Resolve([Target("end"), Expr("start", "DATE", "current_date")]);
        Assert.Equal(["end"], offline.NotResolved);
        Assert.Equal("2026-03-01", offline.Inputs.Single().Value);
    }

    [Fact]
    public void The_loads_file_round_trips_resolvers_with_text_that_needs_quoting()
    {
        var dir = Project("    inputs:\n      start: {resolver: {expression: \"coalesce(strptime('2024-01-01', '%Y-%m-%d')::DATE, current_date) -- \\\"q\\\" \\\\ back\"}}\n      end: {resolver: {expression: \"current_date + 1\"}}\n");
        var (exit, _, err) = RenderFiles("--write", "--project", dir);
        Assert.True(exit == 0, err);
        var (_, loads) = Read(dir, "daily");
        Assert.Contains("\"q\" \\ back", loads.Find("marts.fct_events", "backfill")!.Inputs.Single(i => i.Name == "start").Resolver!.Text);
        Assert.Equal(LoadsDocument.Serialize(loads), File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/loads.yml")));
    }

    // ---------------- seeing them (project show refresh --resolve) ----------------

    private static (int Exit, string Out, string Err) Show(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = DbDataBuild.Cli.CliApp.Run(["project", "show", "refresh", .. args, "--project", dir], o, e);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void Show_refresh_resolve_evaluates_the_expression_resolvers_offline_in_the_projects_zone_and_leaves_the_ones_that_read_a_database()
    {
        var previous = DbDataBuild.Cli.RefreshCommand.Now;
        DbDataBuild.Cli.RefreshCommand.Now = () => new DateTimeOffset(2026, 3, 1, 23, 30, 15, TimeSpan.Zero);
        try
        {
            var dir = Project("    inputs:\n      days: {type: INTEGER, default: 7}\n      end: {resolver: {target: \"SELECT max(event_day) + 1 FROM marts.fct_events\"}}\n      start: {resolver: {expression: 'current_date - days'}}\n", "refresh: { daily: { load: backfill, inputs: { days: 30 } } }\n");
            Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
            var plain = Show(dir, "daily");
            Assert.Equal(0, plain.Exit);
            Assert.Contains("(resolver: expression `current_date - days`)", plain.Out);
            Assert.Contains("(resolver: target ", plain.Out);
            Assert.DoesNotContain("now:", plain.Out);                                                                       // without --resolve nothing is evaluated

            var shown = Show(dir, "daily", "--resolve");
            Assert.Equal(0, shown.Exit);
            Assert.Contains("resolved at 2026-03-01 23:30:15 UTC, clock in UTC", shown.Out);
            Assert.Contains("now: 2026-01-30  (resolver:expression)", shown.Out);                                         // days = 30 from the membership flows in
            Assert.Contains("not resolved here: it reads a database", shown.Out);                                         // `end` is a target resolver

            // the project's zone changes what today is: 23:30 UTC is already tomorrow in Berlin
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), Config + "timezone: Europe/Berlin\n");
            Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
            Assert.Contains("now: 2026-01-31  (resolver:expression)", Show(dir, "daily", "--resolve").Out);

            var json = Show(dir, "daily", "--resolve", "--format", "json");
            Assert.Equal(0, json.Exit);
            Assert.Contains("\"resolved_at\": \"2026-03-01T23:30:15Z\"", json.Out);
            Assert.Contains("\"resolved_value\": \"2026-01-31\"", json.Out);
            Assert.Contains("\"not_resolved\"", json.Out);
            // --resolve is about one plan
            var noPlan = Show(dir, "--resolve");
            Assert.Equal(2, noPlan.Exit);
            Assert.Contains("name the plan", noPlan.Err);
        }
        finally { DbDataBuild.Cli.RefreshCommand.Now = previous; }
    }

    // ---------------- planning (a deploy computes the inputs when it plans, and the plan keeps them) ----------------

    [Fact]
    public void A_deploy_plan_binds_the_values_its_inputs_were_computed_to_per_origin_and_the_plan_file_keeps_how_to_compute_them_again()
    {
        var def = new ModelDefinition("dst.orders", ModelKinds.Copy, [], null, null, ["id"], null, [new ColumnDefinition("id", "BIGINT", false), new ColumnDefinition("store_id", "VARCHAR(10)", false), new ColumnDefinition("day", "DATE", false)], [],
            From: "pos.orders") with { Slice = new CopySlice("store_id", "${origin.store_id}", "VARCHAR(10)"), SliceColumnAdded = true };
        var model = new PlannedModel(def, "SELECT 1", "models/dst/orders.yml", "h", [], Origins: [new CopyOrigin("store_17", "postgres", "pos.orders", "017"), new CopyOrigin("store_18", "postgres", "pos.orders", "018")]);
        var load = new RenderedLoad("default", true, "-- load script", new string('a', 64), null, [new("start", "DATE", "runtime", null), new("end", "DATE", "runtime", null)], null);

        PlannedInput Input(string name, string value, string source, string? origin = null) =>
            new(new RefreshParameter(name, "DATE", RefreshParameter.Computed, null, new("source", "SELECT max(day) FROM pos.orders", [], [])), source, value, "h1", origin);
        var sets = new Dictionary<string, PlannedLoadInputs>
        {
            [PlanInput.InputsKey("dst.orders", "default", "store_17")] = new([Input("start", "2026-02-01", "resolver:source", "store_17"), Input("end", "2026-03-01", "resolver:expression")], null, false),
            [PlanInput.InputsKey("dst.orders", "default", "store_18")] = new([Input("start", "2026-02-10", "resolver:source", "store_18"), Input("end", "2026-03-01", "resolver:expression")], null, false),
        };
        var input = new PlanInput("sqlserver", ProjectConfig.Default, [model], new Dictionary<string, ObjectShape>(), new HashSet<string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
            new HashSet<string>(), new Dictionary<string, IReadOnlyList<RenderedLoad>> { ["dst.orders"] = [load] }, new Dictionary<string, ResolverOutcome>()) { LoadInputs = sets };
        var plan = Planner.Plan(input, []);
        Assert.Empty(plan.Blocks);
        Assert.True(plan.Complete, string.Join(";", plan.Questions.Select(q => q.Id)));                              // nothing is asked: the values are known
        var loads = plan.Steps.Where(s => s.Type == StepType.Load).ToList();
        Assert.Equal(2, loads.Count);
        Assert.Equal(["2026-02-01", "2026-02-10"], loads.Select(l => l.Parameters.Single(p => p.Name == "start").Value));
        Assert.Equal(["resolver:source", "resolver:source"], loads.Select(l => l.Parameters.Single(p => p.Name == "start").Source));
        Assert.All(loads, l => Assert.Equal("2026-03-01", l.Parameters.Single(p => p.Name == "end").Value));
        Assert.Equal("store_18", loads[1].Inputs!.Single(i => i.Name == "start").Origin);

        var report = PlanReport.Markdown(new Plan("p1", "sqlserver", null, false, "t", [], [], plan.Steps, []));
        Assert.Contains("`start` (DATE) = 2026-02-10  [resolver:source at store_18]", report);

        // the plan file keeps the inputs with how each is computed, and reads back the same
        var file = new Plan("p1", "sqlserver", null, false, "t", [], [], plan.Steps, []);
        var text = PlanDocument.Serialize(file);
        var diags = new List<Diagnostic>();
        var back = PlanDocument.Parse(text, "plan.yml", diags);
        Assert.True(back != null, string.Join("\n", diags.Select(d => d.Found)));
        Assert.Equal(PlanDocument.Serialize(back!), text);
        Assert.Equal("SELECT max(day) FROM pos.orders", back!.Steps.First(s => s.Type == StepType.Load).Inputs![0].Def.Resolver!.Text);

        // a set that failed blocks the model, naming the input and the resolver; a set that says skip_load plans no load
        var failing = new Dictionary<string, PlannedLoadInputs> { [PlanInput.InputsKey("dst.orders", "default", "store_17")] = new([], "the target resolver of the input `start` of dst.orders.default returned 0 rows, not exactly one.", false), [PlanInput.InputsKey("dst.orders", "default", "store_18")] = sets[PlanInput.InputsKey("dst.orders", "default", "store_18")] };
        var blocked = Planner.Plan(input with { LoadInputs = failing }, []);
        Assert.Contains(blocked.Blocks, b => b.Code == "DDB-453" && b.Found.Contains("`start`"));
        var skipping = Planner.Plan(input with { LoadInputs = sets.ToDictionary(kv => kv.Key, kv => new PlannedLoadInputs([], null, true)) }, []);
        Assert.DoesNotContain(skipping.Steps, s => s.Type == StepType.Load);
        Assert.Contains(skipping.Noticed, n => n.Contains("skip_load"));
    }
}
