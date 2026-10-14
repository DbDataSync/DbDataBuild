using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Tests.Unit;

public class PlannerTests
{
    private static readonly ProjectConfig Config = ProjectConfig.Default;
    private static readonly DdlGenerator Ddl = TargetRegistry.Get("sqlserver").CreateDdl(Config);

    private static ColumnDefinition Col(string name, string type = "BIGINT", bool nullable = true, string? collation = null) => new(name, type, nullable, collation);

    private static ModelDefinition Table(string name, string kind, IEnumerable<ColumnDefinition> columns, string[]? key = null, RenameDefinition[]? renames = null, string? timeColumn = null) =>
        new(name, kind, key ?? [], timeColumn, null, kind == ModelKinds.Full || kind == ModelKinds.View ? [] : ["id"], null, columns.ToList(), renames ?? []);

    private static PlannedModel Model(ModelDefinition def, string[]? reads = null, string body = "SELECT 1 AS id", string hash = "h1") =>
        new(def, body, $"models/{def.Name.Replace('.', '/')}.sql", hash, reads ?? []);

    private static readonly ColumnDefinition[] Basic = [Col("id", "BIGINT", false), Col("label", "VARCHAR(20)")];

    private static ObjectShape LiveOf(ModelDefinition def)
    {
        var (schema, name) = DdlGenerator.Split(def.Name);
        return new ObjectShape(schema, name, def.Kind == ModelKinds.View ? ObjectKind.View : ObjectKind.Table, DdlGenerator.ExpectedShape(Ddl.MapAll(def)), []);
    }

    private static PlanInput Empty(params PlannedModel[] models) => new(
        "sqlserver", Config, models, new Dictionary<string, ObjectShape>(), new HashSet<string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, string>(), new HashSet<string>(), new Dictionary<string, IReadOnlyList<RenderedLoad>>(), new Dictionary<string, ResolverOutcome>());

    /// <summary>The model exists live, as declared, and the tool recorded exactly that shape.</summary>
    private static PlanInput Existing(PlannedModel model, ObjectShape? live = null)
    {
        live ??= LiveOf(model.Definition);
        var input = Empty(model);
        return input with
        {
            Live = new Dictionary<string, ObjectShape> { [model.Definition.Name] = live }, LiveSchemaNames = new HashSet<string> { live.SchemaName },
            RecordedShapeHashes = new Dictionary<string, string> { [model.Definition.Name] = live.ShapeHash },
        };
    }

    private static PlanInput With(PlanInput input, PlannedModel model, ObjectShape live) => input with
    {
        Live = new Dictionary<string, ObjectShape>(input.Live) { [model.Definition.Name] = live },
        LiveSchemaNames = new HashSet<string>(input.LiveSchemaNames) { live.SchemaName },
        RecordedShapeHashes = new Dictionary<string, string>(input.RecordedShapeHashes) { [model.Definition.Name] = live.ShapeHash },
    };

    private static ResolvedAnswer Ans(string id, string choice, string? value = null) => new(id, choice, value, null, AnswerSource.File);

    private static PlanStep Single(PlanResult r, Func<PlanStep, bool> where) => Assert.Single(r.Steps.Where(where));
    private static string[] Rows(PlanResult r) => r.Steps.Select(s => s.Reasons[0]).ToArray();

    // ----- object-level rows -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Missing_table_creates_the_schema_and_the_table()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var r = Planner.Plan(Empty(m), []);
        Assert.True(r.Complete);
        Assert.Empty(r.Blocks);
        Assert.Equal(["create schema marts", "create table marts.fct"], r.Steps.Select(s => s.Description));
        Assert.All(r.Steps, s => { Assert.Equal(RiskClass.Safe, s.Risk); Assert.Equal(StepType.Ddl, s.Type); });
        Assert.Equal(Hashing.ShapeHash(DdlGenerator.ExpectedShape(Ddl.MapAll(m.Definition))), r.Steps[1].HashAfter);
        Assert.Equal(["1", "2"], r.Steps.Select(s => s.Id));
        Assert.Equal(ObjectState.Missing, Assert.Single(r.Bases).State);
    }

    [Fact]
    public void A_schema_that_exists_is_not_created_again_and_one_schema_is_created_once()
    {
        var a = Model(Table("marts.a", ModelKinds.Full, Basic));
        var b = Model(Table("marts.b", ModelKinds.Full, Basic));
        var r = Planner.Plan(Empty(a, b), []);
        Assert.Single(r.Steps, s => s.Description == "create schema marts");
        var r2 = Planner.Plan(Empty(a) with { LiveSchemaNames = new HashSet<string> { "marts" } }, []);
        Assert.DoesNotContain(r2.Steps, s => s.Description.StartsWith("create schema"));
    }

    [Fact]
    public void Missing_view_is_created_after_the_table_it_reads()
    {
        var table = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var view = Model(Table("marts.v_fct", ModelKinds.View, [Col("id", "BIGINT", false)]), reads: ["marts.fct"], body: "SELECT id FROM marts.fct");
        var r = Planner.Plan(Empty(view, table), []); // input order does not matter
        Assert.Equal(["create schema marts", "create table marts.fct", "create view marts.v_fct"], r.Steps.Select(s => s.Description));
        var create = r.Steps[2];
        Assert.Contains("CREATE OR ALTER VIEW [marts].[v_fct]", create.Text);
        Assert.Null(create.HashAfter);
        Assert.Equal("obj.missing.view", create.Reasons[0]);
    }

    [Fact]
    public void An_existing_untracked_table_asks_whether_to_adopt_it()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var input = Existing(m) with { RecordedShapeHashes = new Dictionary<string, string>() };
        var r = Planner.Plan(input, []);
        Assert.False(r.Complete);
        var q = Assert.Single(r.Questions);
        Assert.Equal("Q-adopt-marts.fct", q.Id);
        Assert.Equal(["adopt", "stop"], q.Options.Select(o => o.Key));
        Assert.Null(q.Proposal);                                     // never a default
        var adopted = Planner.Plan(input, [Ans(q.Id, "adopt")]);
        Assert.True(adopted.Complete);
        var track = Assert.Single(adopted.Steps);
        Assert.Equal(StepType.Track, track.Type);
        Assert.Equal("adopted", track.ShapeSource);
        Assert.Equal(LiveOf(m.Definition).ShapeHash, track.HashAfter);
        Assert.Equal(ObjectState.Untracked, Assert.Single(adopted.Bases).State);
        Assert.Equal(q.Id, Assert.Single(adopted.UsedAnswers).QuestionId);
    }

    [Fact]
    public void Declining_adoption_blocks_the_model_and_skips_what_reads_it()
    {
        var table = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var view = Model(Table("marts.v", ModelKinds.View, [Col("id", "BIGINT", false)]), reads: ["marts.fct"], body: "SELECT id FROM marts.fct");
        var input = Existing(table) with { RecordedShapeHashes = new Dictionary<string, string>(), Models = [table, view] };
        var r = Planner.Plan(input, [Ans("Q-adopt-marts.fct", "stop")]);
        Assert.Equal("DDB-432", Assert.Single(r.Blocks).Code);
        Assert.Equal("DDB-433", Assert.Single(r.Skipped).Code);
        Assert.Empty(r.Steps);
    }

    [Fact]
    public void An_out_of_band_change_blocks_until_acknowledged()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var recorded = LiveOf(m.Definition);
        var changed = recorded with { Columns = [.. recorded.Columns, new ColumnShape("sneaky", "int", null, null, null, true, null)] };
        var input = Existing(m, changed) with { RecordedShapeHashes = new Dictionary<string, string> { ["marts.fct"] = recorded.ShapeHash } };
        var r = Planner.Plan(input, []);
        var block = Assert.Single(r.Blocks);
        Assert.Equal("DDB-430", block.Code);
        Assert.Contains(recorded.ShapeHash[..12], block.Found);
        Assert.Empty(r.Steps);
        Assert.Equal(ObjectState.OutOfBand, Assert.Single(r.Bases).State);
    }

    [Fact]
    public void An_acknowledged_change_is_recorded_and_planning_continues()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var recorded = LiveOf(m.Definition);
        var changed = recorded with { Columns = [.. recorded.Columns, new ColumnShape("sneaky", "int", null, null, null, true, null)] };
        var ack = Acknowledgements.Key("DDB-430", "marts.fct", changed.ShapeHash);
        var input = Existing(m, changed) with { RecordedShapeHashes = new Dictionary<string, string> { ["marts.fct"] = recorded.ShapeHash }, Acknowledged = new HashSet<string> { ack } };
        var r = Planner.Plan(input, []);
        Assert.Empty(r.Blocks);
        Assert.Equal(StepType.Track, r.Steps[0].Type);
        Assert.Equal("out_of_band", r.Steps[0].ShapeSource);
        Assert.Equal("obj.drift.acknowledged", r.Steps[0].Reasons[0]);
        // the extra column is now a plain model difference: not declared, so dropped (destructive)
        Assert.Equal("drop column sneaky", r.Steps[1].Description);

        // an acknowledgement of a different shape does not count
        var other = input with { Acknowledged = new HashSet<string> { Acknowledgements.Key("DDB-430", "marts.fct", "0000") } };
        Assert.Equal("DDB-430", Assert.Single(Planner.Plan(other, []).Blocks).Code);
    }

    // ----- column rows -----------------------------------------------------------------------------------------------------------------

    private static (PlannedModel Model, PlanInput Input) Evolve(ModelDefinition before, ModelDefinition after)
    {
        var m = Model(after);
        return (m, With(Empty(m), m, LiveOf(before)));
    }

    [Fact]
    public void A_new_nullable_column_asks_about_history_and_adds_it()
    {
        var (_, input) = Evolve(Table("marts.fct", ModelKinds.Full, Basic), Table("marts.fct", ModelKinds.Full, [.. Basic, Col("discount_code", "VARCHAR(20)")]));
        var asked = Planner.Plan(input, []);
        var q = Assert.Single(asked.Questions);
        Assert.Equal("Q-history-marts.fct.discount_code", q.Id);
        Assert.Equal(["not_backfilled", "backfill_later"], q.Options.Select(o => o.Key));

        var r = Planner.Plan(input, [Ans(q.Id, "not_backfilled")]);
        var step = Assert.Single(r.Steps);
        Assert.Equal("add column discount_code", step.Description);
        Assert.Equal(RiskClass.Safe, step.Risk);
        Assert.Equal(["col.added", $"answer {q.Id} = not_backfilled"], step.Reasons);
        Assert.Contains("ADD [discount_code] nvarchar(20)", step.Text);
        Assert.Contains(r.Noticed, n => n.Contains("NULL") && n.Contains("discount_code"));
    }

    [Fact]
    public void A_new_not_null_column_is_risky()
    {
        var (_, input) = Evolve(Table("marts.fct", ModelKinds.Full, Basic), Table("marts.fct", ModelKinds.Full, [.. Basic, Col("qty", "INTEGER", false)]));
        var r = Planner.Plan(input, [Ans("Q-history-marts.fct.qty", "not_backfilled")]);
        var step = Assert.Single(r.Steps);
        Assert.Equal(RiskClass.Risky, step.Risk);
        Assert.Equal("col.added.notnull", step.Reasons[0]);
    }

    [Fact]
    public void A_removed_column_is_a_destructive_drop()
    {
        var (_, input) = Evolve(Table("marts.fct", ModelKinds.Full, [.. Basic, Col("old", "INTEGER")]), Table("marts.fct", ModelKinds.Full, Basic));
        var r = Planner.Plan(input, []);
        var step = Assert.Single(r.Steps);
        Assert.Equal(RiskClass.Destructive, step.Risk);
        Assert.Equal("col.removed", step.Reasons[0]);
        Assert.Equal("ALTER TABLE [marts].[fct] DROP COLUMN [old];", step.Text);
        Assert.Empty(r.Questions);                                   // nothing new appeared, so it cannot be a rename
    }

    [Fact]
    public void Declared_and_answered_renames_become_rename_steps()
    {
        // declared in the model
        var before = Table("marts.fct", ModelKinds.Full, [.. Basic, Col("cust_nm", "VARCHAR(20)")]);
        var declared = Table("marts.fct", ModelKinds.Full, [.. Basic, Col("customer_name", "VARCHAR(20)")], renames: [new("cust_nm", "customer_name")]);
        var (_, input) = Evolve(before, declared);
        var r = Planner.Plan(input, []);
        var rename = Assert.Single(r.Steps);
        Assert.Equal("col.renamed", rename.Reasons[0]);
        Assert.Contains("sp_rename", rename.Text);
        Assert.Empty(r.Questions);

        // undeclared: a question, with an inferred proposal that must still be answered explicitly
        var undeclared = Table("marts.fct", ModelKinds.Full, [.. Basic, Col("customer_name", "VARCHAR(20)")]);
        var (_, input2) = Evolve(before, undeclared);
        var asked = Planner.Plan(input2, []);
        var q = Assert.Single(asked.Questions.Where(x => x.Id.StartsWith("Q-rename")));
        Assert.Equal("Q-rename-marts.fct.cust_nm", q.Id);
        Assert.Equal("rename_to", q.Proposal!.OptionKey);
        Assert.Equal("customer_name", q.Proposal.Value);
        Assert.Equal(ProposalCertainty.Normal, q.Proposal.Certainty);

        var renamed = Planner.Plan(input2, [Ans(q.Id, "rename_to", "customer_name")]);
        Assert.Empty(renamed.Questions);
        var step = Assert.Single(renamed.Steps);
        Assert.Equal("rename column cust_nm to customer_name", step.Description);
        Assert.Equal(Hashing.ShapeHash(DdlGenerator.ExpectedShape(Ddl.MapAll(undeclared))), step.HashAfter);   // the table now has the declared shape

        // the other answer is a destructive drop plus an add (which asks about history)
        var dropped = Planner.Plan(input2, [Ans(q.Id, "drop_and_add"), Ans("Q-history-marts.fct.customer_name", "not_backfilled")]);
        Assert.Equal(["drop column cust_nm", "add column customer_name"], dropped.Steps.Select(s => s.Description));
        Assert.Equal(RiskClass.Destructive, dropped.Steps[0].Risk);
    }

    [Fact]
    public void A_rename_answer_must_name_a_free_new_column()
    {
        var before = Table("marts.fct", ModelKinds.Full, [.. Basic, Col("cust_nm", "VARCHAR(20)")]);
        var after = Table("marts.fct", ModelKinds.Full, [.. Basic, Col("customer_name", "VARCHAR(20)")]);
        var (_, input) = Evolve(before, after);
        var r = Planner.Plan(input, [Ans("Q-rename-marts.fct.cust_nm", "rename_to", "no_such_column")]);
        var block = Assert.Single(r.Blocks);
        Assert.Equal("DDB-412", block.Code);
        Assert.Contains("col.rename.invalid", block.Found);
        Assert.Empty(r.Steps);
    }

    [Fact]
    public void Widening_is_safe_and_narrowing_is_destructive()
    {
        var (_, wide) = Evolve(Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("label", "VARCHAR(20)")]), Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("label", "VARCHAR(40)")]));
        var w = Assert.Single(Planner.Plan(wide, []).Steps);
        Assert.Equal((RiskClass.Safe, "col.type.widen"), (w.Risk, w.Reasons[0]));
        Assert.Contains("ALTER COLUMN [label] nvarchar(40)", w.Text);

        var (_, narrow) = Evolve(Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("label", "VARCHAR(40)")]), Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("label", "VARCHAR(20)")]));
        var n = Assert.Single(Planner.Plan(narrow, []).Steps);
        Assert.Equal((RiskClass.Destructive, "col.type.other"), (n.Risk, n.Reasons[0]));

        var (_, family) = Evolve(Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("label", "VARCHAR(40)")]), Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("label", "INTEGER")]));
        Assert.Equal(RiskClass.Destructive, Assert.Single(Planner.Plan(family, []).Steps).Risk);
    }

    [Fact]
    public void Nullability_and_collation_changes_are_graded()
    {
        var (_, relax) = Evolve(Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("n", "INTEGER", false)]), Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("n", "INTEGER")]));
        var a = Assert.Single(Planner.Plan(relax, []).Steps);
        Assert.Equal((RiskClass.Safe, "col.null.relax"), (a.Risk, a.Reasons[0]));

        var (_, tighten) = Evolve(Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("n", "INTEGER")]), Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("n", "INTEGER", false)]));
        var b = Assert.Single(Planner.Plan(tighten, []).Steps);
        Assert.Equal((RiskClass.Risky, "col.null.tighten"), (b.Risk, b.Reasons[0]));

        var config = Config with { StringSemantics = Config.StringSemantics with { Collations = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["default"] = new Dictionary<string, string> { ["duckdb"] = "NOCASE", ["sqlserver"] = "Latin1_General_100_CI_AS" },
            ["exact"] = new Dictionary<string, string> { ["duckdb"] = "BINARY", ["sqlserver"] = "Latin1_General_100_BIN2" },
        } } };
        var after = Model(Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("s", "VARCHAR(10)", collation: "exact")]));
        var beforeShape = LiveOf(Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("s", "VARCHAR(10)")]));
        var input = With(Empty(after) with { Config = config }, after, beforeShape);
        var c = Assert.Single(Planner.Plan(input, []).Steps);
        Assert.Equal((RiskClass.Risky, "col.collation"), (c.Risk, c.Reasons[0]));
        Assert.Contains("COLLATE Latin1_General_100_BIN2", c.Text);
    }

    [Fact]
    public void A_model_in_sync_with_its_table_plans_nothing_and_each_ddl_step_leaves_the_hash_it_promises()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        Assert.Empty(Planner.Plan(Existing(m), []).Steps);

        // several changes at once: after each step the running hash is the hash of the shape up to that step; the last is the declared shape
        var before = Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("gone", "INTEGER"), Col("label", "VARCHAR(10)")]);
        var after = Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("label", "VARCHAR(30)"), Col("extra", "DATE")]);
        var (_, input) = Evolve(before, after);
        var r = Planner.Plan(input, [Ans("Q-history-marts.fct.extra", "not_backfilled")]);
        Assert.Equal(["drop column gone", "add column extra", "alter column label"], r.Steps.Select(s => s.Description));
        Assert.Equal(Hashing.ShapeHash(DdlGenerator.ExpectedShape(Ddl.MapAll(after))), r.Steps[^1].HashAfter);
        Assert.Equal(3, r.Steps.Select(s => s.HashAfter).Distinct().Count());
    }

    [Fact]
    public void A_changed_view_statement_alters_the_view()
    {
        var view = Model(Table("marts.v", ModelKinds.View, [Col("id", "BIGINT", false)]), body: "SELECT 1 AS id");
        var live = LiveOf(view.Definition);
        var input = Existing(view, live);
        // no record of an applied statement: apply it (the view was adopted or created by an older tool)
        var first = Planner.Plan(input, []);
        Assert.Equal("alter view marts.v", Assert.Single(first.Steps).Description);
        Assert.Equal("view.changed", first.Steps[0].Reasons[0]);
        // the recorded statement hash equals the current one: nothing to do
        var same = input with { LastViewStatementHashes = new Dictionary<string, string> { ["marts.v"] = Hashing.ScriptHash(first.Steps[0].Text) } };
        Assert.Empty(Planner.Plan(same, []).Steps);
        // the body changes: the statement hash differs
        var changed = same with { Models = [Model(view.Definition, body: "SELECT 2 AS id")] };
        Assert.Single(Planner.Plan(changed, []).Steps);
    }

    // ----- indexes -----------------------------------------------------------------------------------------------------------------------

    private static IndexDefinition Ix(string name, string[] columns, bool unique = false, string[]? include = null, string[]? targets = null) => new(name, columns, unique, include ?? [], targets);

    [Fact]
    public void Declared_indexes_are_created_changed_and_never_dropped()
    {
        var def = Table("marts.fct", ModelKinds.Full, Basic) with { DeclaredIndexes = [Ix("ix_label", ["label"]), Ix("uq_id", ["id"], unique: true, include: ["label"])] };
        var m = Model(def);

        // a new table: the indexes follow the create, in declared order, with the catalog text they must show afterwards
        var fresh = Planner.Plan(Empty(m), []);
        Assert.Equal(["create schema marts", "create table marts.fct", "create index ix_label", "create index uq_id"], fresh.Steps.Select(s => s.Description));
        Assert.Equal(("index.added", RiskClass.Safe, "index:uq_id=unique=1;keys=id;include=label"), (fresh.Steps[3].Reasons[0], fresh.Steps[3].Risk, fresh.Steps[3].Expect!));
        Assert.Contains("CREATE UNIQUE INDEX [uq_id] ON [marts].[fct] ([id]) INCLUDE ([label]);", fresh.Steps[3].Text);
        Assert.Null(fresh.Steps[3].HashAfter);                                     // an index leaves the shape hash alone

        // an existing table with one of them: only the missing one is planned
        var live = LiveOf(def) with { Physical = [new PhysicalItem("index", "ix_label", "unique=0;keys=label;include=")] };
        Assert.Equal(["create index uq_id"], Planner.Plan(Existing(m, live), []).Steps.Select(s => s.Description));

        // a declared index whose definition differs live is rebuilt (drop and create in one step), risky
        var different = LiveOf(def) with { Physical = [new PhysicalItem("index", "ix_label", "unique=1;keys=label;include="), new PhysicalItem("index", "uq_id", "unique=1;keys=id;include=label")] };
        var rebuild = Assert.Single(Planner.Plan(Existing(m, different), []).Steps);
        Assert.Equal(("rebuild index ix_label", RiskClass.Risky, "index.changed"), (rebuild.Description, rebuild.Risk, rebuild.Reasons[0]));
        Assert.StartsWith("DROP INDEX [ix_label] ON [marts].[fct];\nCREATE INDEX [ix_label]", rebuild.Text);

        // in sync (names compared without regard to case on the declared side): nothing to do. An undeclared live index is left alone and reported
        var same = LiveOf(def) with { Physical = [new PhysicalItem("index", "IX_LABEL", "UNIQUE=0;KEYS=LABEL;INCLUDE="), new PhysicalItem("index", "uq_id", "unique=1;keys=id;include=label"), new PhysicalItem("index", "dba_added", "unique=0;keys=id;include="), new PhysicalItem("constraint_index", "pk_fct", "unique=1;keys=id;include=")] };
        var done = Planner.Plan(Existing(m, same), []);
        Assert.Empty(done.Steps);
        var note = Assert.Single(done.Noticed);
        Assert.Contains("`dba_added`", note);
        Assert.Contains("left alone", note);                                       // constraint-backed indexes are not noticed at all

        // an index removed from the model is not dropped either
        var without = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var kept = Planner.Plan(Existing(without, same), []);
        Assert.Empty(kept.Steps);
        Assert.Equal(3, kept.Noticed.Count);                                       // IX_LABEL, uq_id and dba_added are all undeclared now
    }

    [Fact]
    public void Indexes_for_another_target_are_ignored()
    {
        var def = Table("marts.fct", ModelKinds.Full, Basic) with { DeclaredIndexes = [Ix("ix_label", ["label"], targets: ["postgres"])] };
        Assert.DoesNotContain(Planner.Plan(Empty(Model(def)), []).Steps, s => s.Description.StartsWith("create index"));
    }

    [Fact]
    public void An_index_on_fabric_is_refused()
    {
        var def = Table("marts.fct", ModelKinds.Full, Basic) with { DeclaredIndexes = [Ix("ix_label", ["label"])] };
        var r = Planner.Plan(Empty(Model(def)) with { Target = "fabric" }, []);
        var block = Assert.Single(r.Blocks);
        Assert.Equal("DDB-322", block.Code);
        Assert.Contains("index.unsupported", block.Found);
        Assert.DoesNotContain(r.Steps, s => s.Object == "marts.fct" && s.Type == StepType.Ddl && s.Description.StartsWith("create index"));
    }

    [Fact]
    public void A_declared_index_cannot_take_a_constraints_name()
    {
        var def = Table("marts.fct", ModelKinds.Full, Basic) with { DeclaredIndexes = [Ix("pk_fct", ["id"])] };
        var live = LiveOf(def) with { Physical = [new PhysicalItem("constraint_index", "pk_fct", "unique=1;keys=id;include=")] };
        var block = Assert.Single(Planner.Plan(Existing(Model(def), live), []).Blocks);
        Assert.Equal("DDB-434", block.Code);
        Assert.Contains("index.constraint_name", block.Found);
    }

    // ----- loads -----------------------------------------------------------------------------------------------------------------------

    private static RenderedLoad Load(string operation = "default", IReadOnlyList<RenderedParameter>? parameters = null, string? resolver = null, WatermarkSpec? watermark = null) =>
        new(operation, true, "-- script\nSELECT 1;", Hashing.ScriptHash("-- script\nSELECT 1;"), resolver, parameters ?? [], watermark);

    private static PlanInput WithLoad(PlanInput input, string model, RenderedLoad load) =>
        input with { Loads = new Dictionary<string, IReadOnlyList<RenderedLoad>>(input.Loads) { [model] = [load] } };

    [Fact]
    public void A_load_step_carries_the_committed_script_and_its_parameters()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var input = WithLoad(Existing(m), "marts.fct", Load());
        var r = Planner.Plan(input, []);
        var step = Assert.Single(r.Steps);
        Assert.Equal(StepType.Load, step.Type);
        Assert.Equal("-- script\nSELECT 1;", step.Text);
        Assert.Equal(Hashing.ScriptHash(step.Text), step.FileHash);
        Assert.Equal("default", step.Operation);
        Assert.Equal("load.routine", step.Reasons[0]);

        // all structure first, then all loads (a load never runs before a later model's table exists)
        var a = Model(Table("marts.a", ModelKinds.Full, Basic));
        var b = Model(Table("marts.b", ModelKinds.Full, Basic), reads: ["marts.a"]);
        var both = WithLoad(WithLoad(Empty(a, b), "marts.a", Load()), "marts.b", Load());
        Assert.Equal([StepType.Ddl, StepType.Ddl, StepType.Ddl, StepType.Load, StepType.Load], Planner.Plan(both, []).Steps.Select(s => s.Type));
    }

    [Fact]
    public void A_changed_incremental_query_blocks_until_acknowledged()
    {
        var def = Table("marts.fct", ModelKinds.IncrementalByUniqueKey, Basic, key: ["id"]);
        var m = Model(def, hash: "newhashnewhashnewhash");
        var input = Existing(m) with { LastLoadDefinitionHashes = new Dictionary<string, string> { ["marts.fct"] = "oldhasholdhasholdhash" } };
        input = WithLoad(input, "marts.fct", Load());
        var r = Planner.Plan(input, []);
        Assert.Equal("DDB-431", Assert.Single(r.Blocks).Code);
        Assert.Empty(r.Steps);

        var acked = input with { Acknowledged = new HashSet<string> { Acknowledgements.Key("DDB-431", "marts.fct", "newhashnewhashnewhash") } };
        Assert.Empty(Planner.Plan(acked, []).Blocks);
        Assert.Single(Planner.Plan(acked, []).Steps);

        // the same hash is not a change; and a full model is not subject to the rule
        var same = input with { LastLoadDefinitionHashes = new Dictionary<string, string> { ["marts.fct"] = "newhashnewhashnewhash" } };
        Assert.Empty(Planner.Plan(same, []).Blocks);
        var full = Model(Table("marts.fct", ModelKinds.Full, Basic), hash: "newhashnewhashnewhash");
        Assert.Empty(Planner.Plan(WithLoad(Existing(full) with { LastLoadDefinitionHashes = input.LastLoadDefinitionHashes }, "marts.fct", Load()), []).Blocks);
    }

    private static readonly RenderedParameter Start = new("start", "TIMESTAMP", "runtime", null);
    private static readonly RenderedParameter End = new("end", "TIMESTAMP", "runtime", "max_span 10 days");

    [Fact]
    public void A_runtime_parameter_without_a_value_is_a_question()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var input = WithLoad(Existing(m), "marts.fct", Load("r", [Start, End]));
        var asked = Planner.Plan(input, []);
        Assert.False(asked.Complete);
        Assert.Equal(["Q-param-marts.fct-r-end", "Q-param-marts.fct-r-start"], asked.Questions.Select(q => q.Id));
        Assert.All(asked.Questions, q => Assert.Equal(["provide", "skip_load"], q.Options.Select(o => o.Key)));

        var r = Planner.Plan(input, [Ans("Q-param-marts.fct-r-start", "provide", "2024-01-01 00:00:00"), Ans("Q-param-marts.fct-r-end", "provide", "2024-01-05 00:00:00")]);
        var step = Assert.Single(r.Steps);
        Assert.Equal([("start", "runtime", "2024-01-01 00:00:00"), ("end", "runtime", "2024-01-05 00:00:00")], step.Parameters.Select(p => (p.Name, p.Source, p.Value!)));

        var skipped = Planner.Plan(input, [Ans("Q-param-marts.fct-r-start", "skip_load"), Ans("Q-param-marts.fct-r-end", "skip_load")]);
        Assert.Empty(skipped.Steps);
        Assert.True(skipped.Complete);
    }

    [Fact]
    public void Parameter_values_are_checked_against_their_type_and_max_span()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var input = WithLoad(Existing(m), "marts.fct", Load("r", [Start, End]));
        var bad = Planner.Plan(input, [Ans("Q-param-marts.fct-r-start", "provide", "yesterday"), Ans("Q-param-marts.fct-r-end", "provide", "2024-01-05 00:00:00")]);
        Assert.Equal("DDB-412", Assert.Single(bad.Blocks).Code);
        Assert.Empty(bad.Steps.Where(s => s.Type == StepType.Load));

        var tooLong = Planner.Plan(input, [Ans("Q-param-marts.fct-r-start", "provide", "2024-01-01 00:00:00"), Ans("Q-param-marts.fct-r-end", "provide", "2024-02-01 00:00:00")]);
        var block = Assert.Single(tooLong.Blocks);
        Assert.Contains("max_span of 10 days", block.Found);

        var exact = Planner.Plan(input, [Ans("Q-param-marts.fct-r-start", "provide", "2024-01-01 00:00:00"), Ans("Q-param-marts.fct-r-end", "provide", "2024-01-11 00:00:00")]);
        Assert.Empty(exact.Blocks);
    }

    [Fact]
    public void A_range_that_is_empty_or_backwards_is_refused_without_needing_a_span_limit()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var input = WithLoad(Existing(m), "marts.fct", Load("r", [Start, new("end", "TIMESTAMP", "runtime", null)]));      // no max_span at all
        foreach (var (start, end) in new[] { ("2024-01-05 00:00:00", "2024-01-01 00:00:00"), ("2024-01-05 00:00:00", "2024-01-05 00:00:00") })
        {
            var r = Planner.Plan(input, [Ans("Q-param-marts.fct-r-start", "provide", start), Ans("Q-param-marts.fct-r-end", "provide", end)]);
            var block = Assert.Single(r.Blocks);
            Assert.Equal("DDB-412", block.Code);
            Assert.Contains("empty or backwards", block.Found);
            Assert.Empty(r.Steps.Where(s => s.Type == StepType.Load));
        }
    }

    [Theory]
    [InlineData("2024-01-01 00:00:00", "2024-01-05 00:00:00", false)]       // inside what the target holds
    [InlineData("2023-12-20 00:00:00", "2024-01-05 00:00:00", false)]       // overlaps the start of it
    [InlineData("2025-01-01 00:00:00", "2025-01-05 00:00:00", true)]        // a year after: probably a typo
    [InlineData("2023-01-01 00:00:00", "2023-01-05 00:00:00", true)]        // before it
    public void A_range_the_target_data_does_not_overlap_is_noticed_in_the_plan_but_not_refused(string start, string end, bool noticed)
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var input = WithLoad(Existing(m), "marts.fct", Load("r", [Start, new("end", "TIMESTAMP", "runtime", null)])) with
        {
            RangeBounds = new Dictionary<string, ColumnBounds> { ["marts.fct|r"] = new("2024-01-01 00:00:00", "2024-03-10 00:00:00", null) },
        };
        var r = Planner.Plan(input, [Ans("Q-param-marts.fct-r-start", "provide", start), Ans("Q-param-marts.fct-r-end", "provide", end)]);
        Assert.Empty(r.Blocks);
        Assert.Single(r.Steps, s => s.Type == StepType.Load);
        var notice = r.Noticed.SingleOrDefault(n => n.Contains("does not overlap"));
        Assert.Equal(noticed, notice != null);
        if (noticed) Assert.Contains("the target holds rows from 2024-01-01 00:00:00 to 2024-03-10 00:00:00", notice);
    }

    [Fact]
    public void A_range_load_into_an_empty_table_says_it_is_a_first_load_and_an_unreadable_bound_says_nothing()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var input = WithLoad(Existing(m), "marts.fct", Load("r", [Start, new("end", "TIMESTAMP", "runtime", null)]));
        var answers = new[] { Ans("Q-param-marts.fct-r-start", "provide", "2024-01-01 00:00:00"), Ans("Q-param-marts.fct-r-end", "provide", "2024-01-05 00:00:00") };
        var empty = Planner.Plan(input with { RangeBounds = new Dictionary<string, ColumnBounds> { ["marts.fct|r"] = new(null, null, null) } }, answers);
        Assert.Contains(empty.Noticed, n => n.Contains("holds no rows") && n.Contains("first time"));
        var failed = Planner.Plan(input with { RangeBounds = new Dictionary<string, ColumnBounds> { ["marts.fct|r"] = new(null, null, "failed to run (SqlException)") } }, answers);
        Assert.DoesNotContain(failed.Noticed, n => n.Contains("holds no rows"));
        Assert.DoesNotContain(Planner.Plan(input, answers).Noticed, n => n.Contains("holds no rows"));          // no bounds were read: nothing is claimed
    }

    [Fact]
    public void A_requested_backfill_is_a_risky_backfill_step_of_the_named_operation()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var routine = Load("daily");
        var reload = Load("r", [Start, End]) with { IsDefault = false };
        var input = Existing(m) with { Loads = new Dictionary<string, IReadOnlyList<RenderedLoad>> { ["marts.fct"] = [routine, reload] } };

        // the default operation stays a routine load
        Assert.Equal(StepType.Load, Assert.Single(Planner.Plan(input, []).Steps).Type);

        var requested = input with { OperationChoice = new Dictionary<string, string> { ["marts.fct"] = "r" }, Backfills = new HashSet<string> { "marts.fct" } };
        var asked = Planner.Plan(requested, []);
        Assert.Equal(["Q-param-marts.fct-r-end", "Q-param-marts.fct-r-start"], asked.Questions.Select(q => q.Id));
        var r = Planner.Plan(requested, [Ans("Q-param-marts.fct-r-start", "provide", "2024-01-01 00:00:00"), Ans("Q-param-marts.fct-r-end", "provide", "2024-01-05 00:00:00")]);
        var step = Assert.Single(r.Steps);
        Assert.Equal((StepType.Backfill, RiskClass.Risky, "r"), (step.Type, step.Risk, step.Operation));
        Assert.Equal("load.backfill", step.Reasons[0]);
        Assert.Equal("backfill marts.fct (r)", step.Description);

        // choosing a non-default operation without asking for a backfill is a routine load of that operation
        var chosen = input with { OperationChoice = new Dictionary<string, string> { ["marts.fct"] = "r" } };
        var asked2 = Planner.Plan(chosen, [Ans("Q-param-marts.fct-r-start", "provide", "2024-01-01 00:00:00"), Ans("Q-param-marts.fct-r-end", "provide", "2024-01-02 00:00:00")]);
        Assert.Equal((StepType.Load, RiskClass.Safe), (Assert.Single(asked2.Steps).Type, asked2.Steps[0].Risk));
    }

    [Fact]
    public void A_missing_operation_blocks_the_load()
    {
        var m = Model(Table("marts.fct", ModelKinds.Full, Basic));
        var input = WithLoad(Existing(m), "marts.fct", Load("daily")) with { OperationChoice = new Dictionary<string, string> { ["marts.fct"] = "nope" } };
        var r = Planner.Plan(input, []);
        var block = Assert.Single(r.Blocks);
        Assert.Equal("DDB-434", block.Code);
        Assert.Contains("load.operation.missing", block.Found);
        Assert.Contains("`nope`", block.Found);
        Assert.Empty(r.Steps);
    }

    private static readonly RenderedParameter Mark = new("watermark", "TIMESTAMP", "resolver", null);

    [Fact]
    public void A_null_watermark_follows_the_declared_on_null()
    {
        var m = Model(Table("marts.fct", ModelKinds.IncrementalByTimeRange, [Col("id", "BIGINT", false), Col("at", "TIMESTAMP", false)], timeColumn: "at"));
        var live = Existing(m);
        var resolved = new Dictionary<string, ResolverOutcome> { ["marts.fct|default"] = new(null, null) };

        var required = WithLoad(live with { Resolved = resolved }, "marts.fct", Load(parameters: [Mark], resolver: "SELECT MAX(at) FROM t", watermark: new WatermarkSpec("at", null, WatermarkSpec.RequireParam, null, false)));
        var asked = Planner.Plan(required, []);
        Assert.Equal("Q-param-marts.fct-default-watermark", Assert.Single(asked.Questions).Id);

        var initial = WithLoad(live with { Resolved = resolved }, "marts.fct", Load(parameters: [Mark], resolver: "SELECT MAX(at) FROM t", watermark: new WatermarkSpec("at", null, WatermarkSpec.InitialLiteral, "2020-01-01", false)));
        var step = Assert.Single(Planner.Plan(initial, []).Steps);
        Assert.Equal("2020-01-01", step.Parameters.Single().Value);
        Assert.Null(step.ResolverResult);                            // what the resolver actually returned is kept apart from the value used
        Assert.True(step.HasResolver);

        // a real value is used as is, and recorded as the resolver's result
        var withValue = WithLoad(live with { Resolved = new Dictionary<string, ResolverOutcome> { ["marts.fct|default"] = new("2024-03-01 00:00:00", null) } }, "marts.fct",
            Load(parameters: [Mark], resolver: "SELECT MAX(at) FROM t", watermark: new WatermarkSpec("at", null, WatermarkSpec.RequireParam, null, false)));
        var s2 = Assert.Single(Planner.Plan(withValue, []).Steps);
        Assert.Equal(("2024-03-01 00:00:00", "2024-03-01 00:00:00"), (s2.Parameters.Single().Value, s2.ResolverResult));

        // a table that does not exist yet is empty by definition: the resolver is not consulted and the value is NULL
        var fresh = WithLoad(Empty(m) with { Resolved = new Dictionary<string, ResolverOutcome>() }, "marts.fct", Load(parameters: [Mark], resolver: "SELECT MAX(at) FROM t", watermark: new WatermarkSpec("at", null, WatermarkSpec.InitialLiteral, "2020-01-01", false)));
        Assert.Equal("2020-01-01", Planner.Plan(fresh, []).Steps.Single(s => s.Type == StepType.Load).Parameters.Single().Value);
    }

    [Fact]
    public void An_unusable_resolver_result_blocks_the_load()
    {
        var m = Model(Table("marts.fct", ModelKinds.IncrementalByTimeRange, [Col("id", "BIGINT", false), Col("at", "TIMESTAMP", false)], timeColumn: "at"));
        var load = Load(parameters: [Mark], resolver: "SELECT at FROM t");
        var bad = WithLoad(Existing(m) with { Resolved = new Dictionary<string, ResolverOutcome> { ["marts.fct|default"] = new(null, "returned 3 rows, not one") } }, "marts.fct", load);
        var r = Planner.Plan(bad, []);
        var block = Assert.Single(r.Blocks);
        Assert.Equal("DDB-222", block.Code);
        Assert.Contains("returned 3 rows", block.Found);
        Assert.DoesNotContain(r.Steps, s => s.Type == StepType.Load);

        var missing = WithLoad(Existing(m), "marts.fct", load);
        Assert.Equal("DDB-222", Assert.Single(Planner.Plan(missing, []).Blocks).Code);
    }

    // ----- whole-plan behavior ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_dependency_cycle_is_reported_and_plans_nothing()
    {
        var a = Model(Table("marts.a", ModelKinds.Full, Basic), reads: ["marts.b"]);
        var b = Model(Table("marts.b", ModelKinds.Full, Basic), reads: ["marts.a"]);
        var r = Planner.Plan(Empty(a, b), []);
        var block = Assert.Single(r.Blocks);
        Assert.Equal("DDB-221", block.Code);
        Assert.Contains("marts.a -> marts.b -> marts.a", block.Found);
        Assert.Empty(r.Steps);
    }

    [Fact]
    public void An_unmappable_type_blocks_only_that_model()
    {
        var bad = Model(Table("marts.bad", ModelKinds.Full, [Col("id", "BIGINT", false), Col("big", "HUGEINT")]));
        var good = Model(Table("marts.good", ModelKinds.Full, Basic));
        var r = Planner.Plan(Empty(bad, good), []);
        Assert.Equal("DDB-321", Assert.Single(r.Blocks).Code);
        Assert.Contains(r.Steps, s => s.Description == "create table marts.good");
        Assert.DoesNotContain(r.Steps, s => s.Object == "marts.bad");
    }

    [Fact]
    public void The_plan_does_not_depend_on_input_order_and_is_deterministic()
    {
        var a = Model(Table("marts.a", ModelKinds.Full, Basic));
        var b = Model(Table("marts.b", ModelKinds.Full, Basic), reads: ["marts.a"]);
        var c = Model(Table("marts.c", ModelKinds.Full, Basic));
        var one = Planner.Plan(Empty(a, b, c), []);
        var two = Planner.Plan(Empty(c, b, a), []);
        Assert.Equal(one.Steps.Select(s => (s.Description, s.Text)), two.Steps.Select(s => (s.Description, s.Text)));
        Assert.Equal(["marts.a", "marts.b", "marts.c"], one.Steps.Where(s => s.Description.StartsWith("create table")).Select(s => s.Object));
    }

    [Fact]
    public void Every_step_names_a_decision_table_row_first_and_every_planner_row_has_an_existing_test()
    {
        var diags = new List<Diagnostic>();
        var rows = DecisionTable.LoadEmbedded(diags);
        Assert.Empty(diags);
        var ids = rows.Select(r => r.Id).ToHashSet();
        Assert.Equal(rows.Count, ids.Count);

        var methods = new[] { typeof(PlannerTests), typeof(HookPlanningTests) }.SelectMany(t => t.GetMethods()).Select(m => m.Name).ToHashSet();
        foreach (var row in rows.Where(r => r.Owner == "planner")) Assert.True(methods.Contains(row.Test!), $"row {row.Id} cites test `{row.Test}`, which does not exist");

        // reason chains of steps from a busy scenario start with row ids
        var before = Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("gone", "INTEGER"), Col("label", "VARCHAR(10)")]);
        var after = Table("marts.fct", ModelKinds.Full, [Col("id", "BIGINT", false), Col("label", "VARCHAR(30)"), Col("extra", "DATE")]);
        var (_, input) = Evolve(before, after);
        var plan = Planner.Plan(input, [Ans("Q-history-marts.fct.extra", "not_backfilled")]);
        var fresh = Planner.Plan(WithLoad(Empty(Model(Table("marts.n", ModelKinds.Full, Basic))), "marts.n", Load()), []);
        foreach (var step in plan.Steps.Concat(fresh.Steps)) Assert.Contains(step.Reasons[0], ids);
    }
}
