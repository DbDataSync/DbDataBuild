using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Define;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;
using static DbDataBuild.Tests.Unit.SchemaConformanceTests;

namespace DbDataBuild.Tests.Unit;

public class DefineEngineTests
{
    private const string Orders = "SELECT o.order_id, o.customer_id, o.amount, o.code AS discount_code, o.order_date FROM staging.orders o";

    private static Answer Choice(string id, string choice, string? value = null, int line = 1) => new(id, choice, value, null, false, new("answers.yml", line, 1));
    private static Answer Accept(string id, int line = 1) => new(id, null, null, null, true, new("answers.yml", line, 1));

    private static string Id(string model, string field) => QuestionIds.Define(model, field);

    private static DefineTarget Target(string model, string sql, string? existingYaml = null)
    {
        var stem = model.Replace('.', '/');
        ModelDefinition? existing = null;
        var problems = new List<Diagnostic>();
        if (existingYaml != null) existing = ModelDefinitionLoader.Load(existingYaml, $"models/{stem}.yml", model, problems);
        return new DefineTarget(model, $"models/{stem}.yml", $"models/{stem}.sql", sql, existingYaml, existing, problems);
    }

    private static DefineEngine Engine(ModelGraph? graph = null, ProjectConfig? config = null) =>
        new(graph ?? InferenceTests.Graph(), config ?? ProjectConfig.Default, new MatrixLinter(MatrixLoader.LoadEmbedded([])));

    private static DefineRun Run(DefineEngine engine, DefineTarget[] targets, Answer[]? answers = null, IPrompter? prompter = null, bool accept = false) =>
        engine.Run(targets, answers == null ? null : new AnswerFile(answers), "answers.yml", prompter, accept);

    /// <summary>Answers every column question by accepting the inference, plus the model-level answers given.</summary>
    private static List<Answer> Columns(string model, IEnumerable<string> columns) =>
        columns.SelectMany(c => new[] { Accept(Id(model, $"columns.{c}.type")), Accept(Id(model, $"columns.{c}.nullable")) }).ToList();

    private static readonly string[] OrderColumns = ["order_id", "customer_id", "amount", "discount_code", "order_date"];

    private static DefineOutcome One(DefineRun run) => Assert.Single(run.Outcomes);

    // ---------------- a new definition ----------------

    private static Answer[] UniqueKeyAnswers(string model) => new[]
    {
        Accept(Id(model, "name")),
        Choice(Id(model, "kind"), "incremental_by_unique_key"),
        Choice(Id(model, "connections"), "choose_targets", "sqlserver, fabric"),
        Accept(Id(model, "grain")),
        Accept(Id(model, "unique_key")),
        Choice(Id(model, "indexes"), "add_suggested"),
    }.Concat(Columns(model, OrderColumns)).ToArray();

    [Fact]
    public void Indexes_are_proposed_for_a_key_load_but_only_declared_when_answered_and_never_by_accept_inferred()
    {
        const string m = "marts.fct_orders";
        var noIndexes = UniqueKeyAnswers(m).Where(a => a.QuestionId != Id(m, "indexes")).Append(Choice(Id(m, "indexes"), "no_indexes")).ToArray();
        var declined = One(Run(Engine(), [Target(m, Orders)], noIndexes));
        Assert.Equal(DefineStatus.Created, declined.Status);
        Assert.DoesNotContain("indexes:", declined.NewText);

        var unanswered = UniqueKeyAnswers(m).Where(a => a.QuestionId != Id(m, "indexes")).ToArray();
        var asked = One(Run(Engine(), [Target(m, Orders)], unanswered, accept: true));       // a normal-certainty proposal is not taken by --accept-inferred
        Assert.Equal(DefineStatus.Incomplete, asked.Status);
        var q = Assert.Single(asked.Unanswered, x => x.Id == Id(m, "indexes"));
        Assert.Contains("- {name: ux_fct_orders_order_id, columns: [order_id], unique: true}", string.Join("\n", q.Context));

        var accepted = One(Run(Engine(), [Target(m, Orders)], UniqueKeyAnswers(m)));
        var diags = new List<Diagnostic>();
        var reloaded = ModelDefinitionLoader.Load(accepted.NewText!, "models/marts/fct_orders.yml", m, diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        Assert.Empty(IndexAdvisor.For(reloaded!, ["sqlserver", "fabric"]));         // the generated definition satisfies the advice that produced it
    }

    [Fact]
    public void A_new_definition_is_generated_from_answers_and_matches_the_golden_file()
    {
        var run = Run(Engine(), [Target("marts.fct_orders", Orders)], UniqueKeyAnswers("marts.fct_orders"));
        var o = One(run);
        Assert.Equal(DefineStatus.Created, o.Status);
        Assert.Empty(o.Diagnostics.Select(DiagnosticFormatter.Format));
        GoldenFile.Assert("define/new_unique_key.yml", o.NewText!);

        // valid for the schema and the loader
        Assert.True(SchemaAccepts(LoadSchema("model"), o.NewText!));
        Assert.True(run.Complete);
    }

    [Fact]
    public void Every_resolved_answer_records_how_it_was_given()
    {
        var o = One(Run(Engine(), [Target("marts.fct_orders", Orders)], UniqueKeyAnswers("marts.fct_orders")));
        Assert.Equal(AnswerSource.File, o.Answers.Single(a => a.QuestionId == Id("marts.fct_orders", "kind")).Source);
        Assert.Equal(AnswerSource.AcceptedProposal, o.Answers.Single(a => a.QuestionId == Id("marts.fct_orders", "name")).Source);
        Assert.Equal(o.Answers.Select(a => a.QuestionId).Order(StringComparer.Ordinal).Count(), o.Answers.Count);
    }

    [Fact]
    public void Without_answers_all_first_round_questions_are_listed_at_once_and_nothing_is_written()
    {
        var run = Run(Engine(), [Target("marts.fct_orders", Orders)]);
        var o = One(run);
        Assert.Equal(DefineStatus.Incomplete, o.Status);
        Assert.Null(o.NewText);
        Assert.False(run.Complete);

        var ids = o.Unanswered.Select(q => q.Id).ToList();
        Assert.Contains(Id("marts.fct_orders", "name"), ids);
        Assert.Contains(Id("marts.fct_orders", "kind"), ids);
        Assert.Contains(Id("marts.fct_orders", "connections"), ids);
        foreach (var c in OrderColumns)
        {
            Assert.Contains(Id("marts.fct_orders", $"columns.{c}.type"), ids);
            Assert.Contains(Id("marts.fct_orders", $"columns.{c}.nullable"), ids);
        }
        Assert.Equal(ids.Count, o.Diagnostics.Count(d => d.Code == "DDB-414"));
        Assert.DoesNotContain(ids, i => i.EndsWith("-grain"));                       // those depend on the kind
        Assert.Contains(o.Notes, n => n.Contains("follow once the kind is answered"));
    }

    [Fact]
    public void Accept_inferred_takes_only_high_certainty_proposals()
    {
        var run = Run(Engine(), [Target("marts.fct_orders", Orders)], accept: true);
        var o = One(run);
        Assert.Equal(DefineStatus.Incomplete, o.Status);
        Assert.All(o.Answers, a => Assert.Equal(AnswerSource.AcceptedProposalByFlag, a.Source));
        Assert.Contains(o.Answers, a => a.QuestionId == Id("marts.fct_orders", "name"));
        Assert.Equal(OrderColumns.Length * 2 + 1, o.Answers.Count);                  // name + every column's type and nullability
        Assert.Equal([Id("marts.fct_orders", "connections"), Id("marts.fct_orders", "kind")], o.Unanswered.Select(q => q.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Second_round_questions_appear_once_the_kind_is_answered()
    {
        var m = "marts.fct_orders";
        var answers = new List<Answer> { Accept(Id(m, "name")), Choice(Id(m, "kind"), "incremental_by_unique_key"), Accept(Id(m, "connections")) };
        answers.AddRange(Columns(m, OrderColumns));
        var o = One(Run(Engine(), [Target(m, Orders)], answers.ToArray()));
        Assert.Equal(DefineStatus.Incomplete, o.Status);
        Assert.Equal([Id(m, "grain"), Id(m, "unique_key")], o.Unanswered.Select(q => q.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(o.Answers, a => a.QuestionId == Id(m, "unique_key"));
    }

    [Fact]
    public void A_full_model_has_no_second_round_and_omits_targets_when_the_project_default_is_used()
    {
        var m = "marts.dim_customer";
        var answers = new List<Answer> { Accept(Id(m, "name")), Choice(Id(m, "kind"), "full"), Accept(Id(m, "connections")) };
        answers.AddRange(Columns(m, ["customer_id", "name"]));
        var o = One(Run(Engine(), [Target(m, "SELECT c.customer_id, c.name FROM staging.customers c")], answers.ToArray()));
        Assert.Equal(DefineStatus.Created, o.Status);
        GoldenFile.Assert("define/new_full_default_connections.yml", o.NewText!);
    }

    [Fact]
    public void A_view_is_defined_without_grain()
    {
        var m = "marts.v_orders";
        var answers = new List<Answer> { Accept(Id(m, "name")), Choice(Id(m, "kind"), "view"), Accept(Id(m, "connections")) };
        answers.AddRange(Columns(m, ["order_id", "amount"]));
        var o = One(Run(Engine(), [Target(m, "SELECT o.order_id, o.amount FROM staging.orders o")], answers.ToArray()));
        Assert.Equal(DefineStatus.Created, o.Status);
        Assert.StartsWith("name: marts.v_orders\nkind: view\ncolumns:\n", o.NewText);
    }

    [Fact]
    public void A_time_range_model_needs_a_time_column_and_may_have_a_lookback()
    {
        var m = "marts.fct_daily";
        const string sql = "SELECT o.order_date, o.created_at, COUNT(*) AS n FROM staging.orders o GROUP BY o.order_date, o.created_at";
        var answers = new List<Answer>
        {
            Accept(Id(m, "name")), Choice(Id(m, "kind"), "incremental_by_time_range"), Accept(Id(m, "connections")),
            Accept(Id(m, "grain")), Choice(Id(m, "time_column"), "use_column", "ORDER_DATE"), Choice(Id(m, "lookback"), "use_lookback", "3 days"),
            Choice(Id(m, "indexes"), "no_indexes"),
        };
        answers.AddRange(Columns(m, ["order_date", "created_at", "n"]));
        var o = One(Run(Engine(), [Target(m, sql)], answers.ToArray()));
        Assert.Equal(DefineStatus.Created, o.Status);
        GoldenFile.Assert("define/new_time_range.yml", o.NewText!);
    }

    [Fact]
    public void Two_time_candidates_give_no_proposal_and_one_candidate_does()
    {
        Question TimeQuestion(string model, string sql, params string[] columns)
        {
            var answers = new List<Answer> { Accept(Id(model, "name")), Choice(Id(model, "kind"), "incremental_by_time_range"), Accept(Id(model, "connections")) };
            answers.AddRange(Columns(model, columns));
            return One(Run(Engine(), [Target(model, sql)], answers.ToArray())).Unanswered.Single(q => q.Id == Id(model, "time_column"));
        }

        var two = TimeQuestion("marts.t2", "SELECT o.order_date, o.created_at FROM staging.orders o", "order_date", "created_at");
        Assert.Null(two.Proposal);
        Assert.Contains("order_date, created_at", string.Join(" ", two.Context));

        var one = TimeQuestion("marts.t1", "SELECT o.order_id, o.order_date FROM staging.orders o", "order_id", "order_date");
        Assert.Equal("order_date", one.Proposal!.Value);
    }

    [Fact]
    public void A_computed_text_column_is_unlimited_unless_a_person_limits_it()
    {
        var m = "marts.tagged";
        const string sql = "SELECT o.order_id, o.code || '-x' AS tagged FROM staging.orders o";
        var q = One(Run(Engine(), [Target(m, sql)])).Unanswered.Single(x => x.Id == Id(m, "columns.tagged.type"));
        Assert.Equal("VARCHAR", q.Proposal!.Value);                 // no length declared or written: the proposal is unlimited text
        Assert.Contains("unlimited", string.Join(" ", q.Context));

        var unlimited = new List<Answer> { Accept(Id(m, "name")), Choice(Id(m, "kind"), "full"), Accept(Id(m, "connections")), Accept(Id(m, "columns.tagged.type")) };
        unlimited.AddRange(Columns(m, ["order_id"]));
        unlimited.Add(Choice(Id(m, "columns.tagged.nullable"), "nullable"));
        Assert.Contains("  - name: tagged\n    type: VARCHAR\n", One(Run(Engine(), [Target(m, sql)], unlimited.ToArray())).NewText);

        var answers = new List<Answer> { Accept(Id(m, "name")), Choice(Id(m, "kind"), "full"), Accept(Id(m, "connections")), Choice(Id(m, "columns.tagged.type"), "use_type", "varchar(30)") };
        answers.AddRange(Columns(m, ["order_id"]));
        answers.Add(Choice(Id(m, "columns.tagged.nullable"), "nullable"));          // lineage cannot show it for an expression, so it is an explicit answer
        var o = One(Run(Engine(), [Target(m, sql)], answers.ToArray()));
        Assert.Equal(DefineStatus.Created, o.Status);
        Assert.Contains("  - name: tagged\n    type: VARCHAR(30)\n", o.NewText);
    }

    [Fact]
    public void Answers_that_do_not_fit_are_errors_and_write_nothing()
    {
        var m = "marts.fct_orders";
        Answer[] With(params Answer[] overrides) =>
            UniqueKeyAnswers(m).Where(a => !overrides.Any(o => o.QuestionId == a.QuestionId)).Concat(overrides).ToArray();

        var badName = One(Run(Engine(), [Target(m, Orders)], With(Choice(Id(m, "name"), "use_name", "marts.something_else"))));
        Assert.Equal(DefineStatus.Failed, badName.Status);
        Assert.Contains(badName.Diagnostics, d => d.Code == "DDB-107");

        var badTarget = One(Run(Engine(), [Target(m, Orders)], With(Choice(Id(m, "connections"), "choose_targets", "oracle"))));
        Assert.Contains(badTarget.Diagnostics, d => d.Code == "DDB-106" && d.Found.Contains("oracle"));

        var badType = One(Run(Engine(), [Target(m, Orders)], With(Choice(Id(m, "columns.amount.type"), "use_type", "INT); DROP TABLE x; --"))));
        Assert.Contains(badType.Diagnostics, d => d.Code == "DDB-106" && d.Found.Contains("plain SQL type"));

        var badGrain = One(Run(Engine(), [Target(m, Orders)], With(Choice(Id(m, "grain"), "custom_columns", "nope"))));
        Assert.Contains(badGrain.Diagnostics, d => d.Code == "DDB-106" && d.Found.Contains("nope"));

        Assert.All(new[] { badName, badTarget, badType, badGrain }, o => Assert.Null(o.NewText));
    }

    [Fact]
    public void Skip_model_by_answer_skips_without_writing()
    {
        var m = "marts.fct_orders";
        var answers = UniqueKeyAnswers(m).Where(a => a.QuestionId != Id(m, "name")).Append(Choice(Id(m, "name"), "skip_model")).ToArray();
        var o = One(Run(Engine(), [Target(m, Orders)], answers));
        Assert.Equal(DefineStatus.Skipped, o.Status);
        Assert.Null(o.NewText);
    }

    [Fact]
    public void A_generated_definition_is_in_sync_and_define_is_idempotent_on_it()
    {
        var m = "marts.fct_orders";
        var created = One(Run(Engine(), [Target(m, Orders)], UniqueKeyAnswers(m)));
        var target = Target(m, Orders, created.NewText);
        Assert.NotNull(target.Existing);

        var second = One(Run(Engine(), [target]));                                    // no answers: nothing may be asked
        Assert.Equal(DefineStatus.InSync, second.Status);
        Assert.Empty(second.Unanswered);
        Assert.Null(second.NewText);
        Assert.Empty(Engine().Check([target]));
    }

    // ---------------- an existing definition ----------------

    private const string Existing = """
        # Orders fact (hand-written)
        name: marts.fct_orders
        kind:
          incremental_by_unique_key:   # keyed upsert
            unique_key: [order_id]

        grain: [order_id]
        connections: [sqlserver, fabric]
        columns:
          # identifiers
          - name: order_id
            type: BIGINT
            nullable: false
          - name: customer_id
            type: BIGINT
          - name: amount
            type: DECIMAL(14, 2)   # money
          - name: discount_code
            type: VARCHAR(20)
            nullable: false
          - name: order_date
            type: DATE
            nullable: false

        # trailing comment
        """;

    private static string ExistingText => Existing.Replace("\r\n", "\n") + "\n";

    [Fact]
    public void An_in_sync_definition_asks_nothing()
    {
        var o = One(Run(Engine(), [Target("marts.fct_orders", Orders, ExistingText)]));
        Assert.Equal(DefineStatus.InSync, o.Status);
        Assert.Empty(o.Unanswered);
    }

    [Fact]
    public void A_new_query_column_is_added_with_a_splice_that_keeps_everything_else()
    {
        const string sql = "SELECT o.order_id, o.customer_id, o.amount, o.code AS discount_code, o.order_date, o.created_at FROM staging.orders o";
        var m = "marts.fct_orders";
        var o = One(Run(Engine(), [Target(m, sql, ExistingText)], Columns(m, ["created_at"]).ToArray()));
        Assert.Equal(DefineStatus.Updated, o.Status);
        Assert.Equal(ExistingText.Replace("    type: DATE\n    nullable: false\n\n# trailing", "    type: DATE\n    nullable: false\n  - name: created_at\n    type: TIMESTAMP\n\n# trailing"), o.NewText);
        GoldenFile.Assert("define/update_added_column.yml", o.NewText!);
        Assert.Empty(Engine().Check([Target(m, sql, o.NewText)]));                    // in sync afterwards
    }

    [Fact]
    public void A_removed_query_column_is_removed_only_when_confirmed()
    {
        const string sql = "SELECT o.order_id, o.customer_id, o.amount, o.order_date FROM staging.orders o";
        var m = "marts.fct_orders";
        var question = Id(m, "columns.discount_code.remove");

        var removed = One(Run(Engine(), [Target(m, sql, ExistingText)], [Choice(question, "remove_column")]));
        Assert.Equal(DefineStatus.Updated, removed.Status);
        Assert.Equal(ExistingText.Replace("  - name: discount_code\n    type: VARCHAR(20)\n    nullable: false\n", ""), removed.NewText);

        var kept = One(Run(Engine(), [Target(m, sql, ExistingText)], [Choice(question, "keep_declared")]));
        Assert.Equal(DefineStatus.Unchanged, kept.Status);
        Assert.Contains(kept.Notes, n => n.Contains("Kept the declaration of `discount_code`"));
        Assert.NotEmpty(Engine().Check([Target(m, sql, ExistingText)]));              // still out of sync, as the note says

        var open = One(Run(Engine(), [Target(m, sql, ExistingText)]));
        Assert.Equal(DefineStatus.Incomplete, open.Status);
        Assert.Equal([question], open.Unanswered.Select(q => q.Id));
    }

    [Fact]
    public void A_changed_type_is_a_question_with_old_and_new_values()
    {
        var graph = InferenceTests.Graph();
        graph.Provide("staging.orders", [new("order_id", "BIGINT", false), new("customer_id", "BIGINT"), new("amount", "DECIMAL(18, 2)"), new("code", "VARCHAR(20)", false), new("order_date", "DATE", false)], ["order_id"]);
        var m = "marts.fct_orders";
        var id = Id(m, "columns.amount.type");

        var open = One(Run(Engine(graph), [Target(m, Orders, ExistingText)]));
        var q = Assert.Single(open.Unanswered);
        Assert.Equal(id, q.Id);
        Assert.Contains("Declared: DECIMAL(14, 2)", string.Join(" ", q.Context));
        Assert.Equal("DECIMAL(18, 2)", q.Proposal!.Value);
        Assert.Equal(ProposalCertainty.High, q.Proposal.Certainty);

        var updated = One(Run(Engine(graph), [Target(m, Orders, ExistingText)], [Accept(id)]));
        Assert.Equal(ExistingText.Replace("type: DECIMAL(14, 2)   # money", "type: DECIMAL(18, 2)   # money"), updated.NewText);   // comment preserved

        var kept = One(Run(Engine(graph), [Target(m, Orders, ExistingText)], [Choice(id, "keep_declared")]));
        Assert.Equal(DefineStatus.Unchanged, kept.Status);
    }

    private const string RenameYaml = "name: marts.dim_customer\nkind: {incremental_by_unique_key: {unique_key: [cust_id]}}\ngrain: [cust_id]\ncolumns:\n  - {name: cust_id, type: BIGINT, nullable: false}\n  - {name: cust_nm, type: \"VARCHAR(50)\"}\n";
    private const string RenameSql = "SELECT c.customer_id AS cust_id, c.name AS customer_name FROM staging.customers c";

    [Fact]
    public void A_rename_candidate_is_asked_and_a_yes_updates_the_column_and_records_the_rename()
    {
        var m = "marts.dim_customer";
        var id = QuestionIds.Rename(m, "cust_nm");
        var open = One(Run(Engine(), [Target(m, RenameSql, RenameYaml)]));
        Assert.Equal([id], open.Unanswered.Select(q => q.Id));                          // add/remove questions wait for this answer
        Assert.Equal("customer_name", open.Unanswered[0].Proposal!.Value);

        var o = One(Run(Engine(), [Target(m, RenameSql, RenameYaml)], [Accept(id)]));
        Assert.Equal(DefineStatus.Updated, o.Status);
        Assert.Contains("  - {name: customer_name, type: \"VARCHAR(50)\"}", o.NewText);
        Assert.Contains("renames:\n  - from: cust_nm\n    to: customer_name\n", o.NewText);
        Assert.Empty(Engine().Check([Target(m, RenameSql, o.NewText)]));
    }

    [Fact]
    public void A_rename_of_a_key_column_updates_the_key_and_shows_it_in_the_question()
    {
        var m = "marts.dim_customer";
        var yaml = RenameYaml.Replace("cust_nm", "name_x").Replace("cust_id", "customer_id");
        var sql = "SELECT c.customer_id AS ident, c.name AS name_x FROM staging.customers c";
        var q = One(Run(Engine(), [Target(m, sql, yaml)])).Unanswered.Single();
        Assert.Equal(QuestionIds.Rename(m, "customer_id"), q.Id);
        Assert.Contains("grain [customer_id] becomes [ident]", string.Join(" ", q.Context));
        Assert.Contains("unique_key [customer_id] becomes [ident]", string.Join(" ", q.Context));

        var o = One(Run(Engine(), [Target(m, sql, yaml)], [Accept(q.Id)]));
        Assert.Equal(DefineStatus.Updated, o.Status);
        Assert.Contains("unique_key: [ident]", o.NewText);
        Assert.Contains("grain: [ident]", o.NewText);
    }

    [Fact]
    public void Drop_and_add_turns_a_rename_candidate_into_a_removal_and_an_addition()
    {
        var m = "marts.dim_customer";
        var renameId = QuestionIds.Rename(m, "cust_nm");
        var first = One(Run(Engine(), [Target(m, RenameSql, RenameYaml)], [Choice(renameId, "drop_and_add")]));
        Assert.Equal(DefineStatus.Incomplete, first.Status);
        Assert.Equal(new[] { Id(m, "columns.cust_nm.remove"), Id(m, "columns.customer_name.nullable"), Id(m, "columns.customer_name.type") }.Order(StringComparer.Ordinal),
                     first.Unanswered.Select(q => q.Id).Order(StringComparer.Ordinal));

        var done = One(Run(Engine(), [Target(m, RenameSql, RenameYaml)], [Choice(renameId, "drop_and_add"), Choice(Id(m, "columns.cust_nm.remove"), "remove_column"),
            Accept(Id(m, "columns.customer_name.type")), Accept(Id(m, "columns.customer_name.nullable"))]));
        Assert.Equal(DefineStatus.Updated, done.Status);
        Assert.DoesNotContain("renames:", done.NewText);
        Assert.Contains("customer_name", done.NewText);
        Assert.DoesNotContain("cust_nm", done.NewText);
    }

    [Fact]
    public void Rename_to_something_that_is_not_a_new_column_is_refused()
    {
        var m = "marts.dim_customer";
        var o = One(Run(Engine(), [Target(m, RenameSql, RenameYaml)], [Choice(QuestionIds.Rename(m, "cust_nm"), "rename_to", "nowhere")]));
        Assert.Equal(DefineStatus.Failed, o.Status);
        Assert.Contains(o.Diagnostics, d => d.Code == "DDB-106" && d.Found.Contains("nowhere"));
    }

    [Fact]
    public void Declared_not_null_is_kept_even_when_lineage_says_nullable_and_it_is_only_a_note()
    {
        var m = "marts.x";
        const string yaml = "name: marts.x\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: cname, type: VARCHAR(50), nullable: false}\n";
        const string sql = "SELECT o.order_id, c.name AS cname FROM staging.orders o LEFT JOIN staging.customers c ON o.customer_id = c.customer_id";
        var o = One(Run(Engine(), [Target(m, sql, yaml)]));
        Assert.Equal(DefineStatus.InSync, o.Status);
        Assert.Contains(o.Notes, n => n.Contains("`cname` is declared NOT NULL") && n.Contains("yours to decide"));
        Assert.Empty(Engine().Check([Target(m, sql, yaml)]));
    }

    [Fact]
    public void Declared_varchar_lengths_and_synonyms_are_not_differences()
    {
        const string yaml = "name: marts.x\nkind: full\ncolumns:\n  - {name: order_id, type: INT8}\n  - {name: tagged, type: VARCHAR(99)}\n  - {name: amount, type: \"NUMERIC(14,2)\"}\n";
        const string sql = "SELECT o.order_id, o.code || 'x' AS tagged, o.amount FROM staging.orders o";
        Assert.Empty(Engine().Check([Target("marts.x", sql, yaml)]));
    }

    // ---------------- check ----------------

    [Fact]
    public void Check_reports_every_difference_with_the_definition_line_and_writes_nothing()
    {
        const string sql = "SELECT o.order_id, o.customer_id, o.amount, o.order_date, o.created_at FROM staging.orders o";   // discount_code gone, created_at new
        var diags = Engine().Check([Target("marts.fct_orders", sql, ExistingText)]);
        Assert.All(diags, d => Assert.Equal("DDB-420", d.Code));
        var removed = Assert.Single(diags, d => d.Found.Contains("`discount_code` is declared"));
        Assert.Equal(("models/marts/fct_orders.yml", 18), (removed.Location.File, removed.Location.Line));
        Assert.Single(diags, d => d.Found.Contains("returns column `created_at`") && d.Found.Contains("TIMESTAMP"));
        Assert.Equal(2, diags.Count);
    }

    [Fact]
    public void Check_reports_type_changes_and_a_missing_definition_file_and_query_problems()
    {
        var graph = InferenceTests.Graph();
        graph.Provide("staging.orders", [new("order_id", "BIGINT", false), new("customer_id", "BIGINT"), new("amount", "DECIMAL(18, 2)"), new("code", "VARCHAR(20)", false), new("order_date", "DATE", false)], ["order_id"]);
        var diags = Engine(graph).Check([
            Target("marts.fct_orders", Orders, ExistingText),
            Target("marts.new_one", "SELECT 1 AS a"),
            Target("marts.broken", "SELECT * FROM staging.nope", "name: marts.broken\nkind: full\ncolumns:\n  - {name: a, type: INT}\n"),
        ]);
        Assert.Contains(diags, d => d.Code == "DDB-420" && d.Found.Contains("declared DECIMAL(14, 2), but the query resolves DECIMAL(18, 2)"));
        Assert.Contains(diags, d => d.Code == "DDB-420" && d.Found.Contains("has no definition file"));
        Assert.Contains(diags, d => d.Code == "DDB-218");
    }

    [Fact]
    public void Check_of_in_sync_definitions_is_empty()
    {
        Assert.Empty(Engine().Check([Target("marts.fct_orders", Orders, ExistingText)]));
    }

    // ---------------- several models ----------------

    [Fact]
    public void Models_are_defined_after_the_models_they_query_and_see_their_new_columns()
    {
        var graph = InferenceTests.Graph();
        var a = "marts.a_orders";
        var b = "marts.b_report";
        var answers = new List<Answer>
        {
            Accept(Id(a, "name")), Choice(Id(a, "kind"), "full"), Accept(Id(a, "connections")),
            Accept(Id(b, "name")), Choice(Id(b, "kind"), "view"), Accept(Id(b, "connections")),
        };
        answers.AddRange(Columns(a, ["order_id", "amount"]));
        answers.AddRange(Columns(b, ["order_id", "amount"]));
        var run = Run(Engine(graph), [Target(b, "SELECT x.order_id, x.amount FROM marts.a_orders x"), Target(a, "SELECT o.order_id, o.amount FROM staging.orders o")], answers.ToArray());
        Assert.True(run.Complete, string.Join("\n", run.Outcomes.SelectMany(o => o.Diagnostics).Select(DiagnosticFormatter.Format)));
        Assert.Equal([DefineStatus.Created, DefineStatus.Created], run.Outcomes.Select(o => o.Status));
        Assert.Contains("type: DECIMAL(14, 2)", run.Outcomes.Single(o => o.Target.ModelName == b).NewText);   // the type came through marts.a_orders
    }

    [Fact]
    public void A_dependent_waits_when_its_upstream_is_not_defined()
    {
        var a = "marts.a_orders";
        var b = "marts.b_report";
        var run = Run(Engine(), [Target(a, "SELECT o.order_id FROM staging.orders o"), Target(b, "SELECT x.order_id FROM marts.a_orders x")]);
        var outcome = run.Outcomes.Single(o => o.Target.ModelName == b);
        Assert.Equal(DefineStatus.Skipped, outcome.Status);
        Assert.Contains("marts.a_orders", outcome.Notes[0]);
        Assert.False(run.Complete);
    }

    [Fact]
    public void A_dependency_cycle_is_reported()
    {
        var run = Run(Engine(), [Target("marts.a", "SELECT * FROM marts.b"), Target("marts.b", "SELECT * FROM marts.a")]);
        Assert.Equal(2, run.Diagnostics.Count(d => d.Code == "DDB-221"));
    }

    [Fact]
    public void Unused_answers_are_warned_about_once_at_the_end_and_later_round_answers_are_not_unused()
    {
        var m = "marts.fct_orders";
        var withExtra = UniqueKeyAnswers(m).Append(Choice("Q-history-marts.other.x", "backfilled", line: 9)).ToArray();
        var run = Run(Engine(), [Target(m, Orders)], withExtra);
        var warning = Assert.Single(run.Diagnostics);
        Assert.Equal(("DDB-410", 9), (warning.Code, warning.Location.Line));      // grain and unique_key answers belong to round 2 and were used
        Assert.True(run.Complete);
    }

    [Fact]
    public void An_existing_definition_that_does_not_load_is_not_edited()
    {
        var o = One(Run(Engine(), [Target("marts.fct_orders", Orders, "name: marts.fct_orders\nbogus: 1\n")]));
        Assert.Equal(DefineStatus.Failed, o.Status);
        Assert.Contains(o.Diagnostics, d => d.Code == "DDB-104");
        Assert.Null(o.NewText);
    }

    [Fact]
    public void A_flow_list_of_columns_cannot_be_spliced_and_is_reported_not_rewritten()
    {
        const string yaml = "name: marts.x\nkind: full\ncolumns: [{name: order_id, type: BIGINT}]\n";
        var m = "marts.x";
        var o = One(Run(Engine(), [Target(m, "SELECT o.order_id, o.amount FROM staging.orders o", yaml)], Columns(m, ["amount"]).ToArray()));
        Assert.Equal(DefineStatus.Failed, o.Status);
        Assert.Contains(o.Diagnostics, d => d.Code == "DDB-422");
    }

    [Fact]
    public void The_prompter_answers_what_the_file_does_not()
    {
        var m = "marts.dim_customer";
        const string sql = "SELECT c.customer_id, c.name FROM staging.customers c";
        var prompter = new RecordingPrompter();
        var answers = new List<Answer> { Choice(Id(m, "kind"), "full") };
        answers.AddRange(Columns(m, ["customer_id", "name"]));
        var o = One(Run(Engine(), [Target(m, sql)], answers.ToArray(), prompter));
        Assert.Equal(DefineStatus.Created, o.Status);
        Assert.Equal([Id(m, "connections"), Id(m, "name")], prompter.Asked.Order(StringComparer.Ordinal));
    }

    private sealed class RecordingPrompter : IPrompter
    {
        public List<string> Asked { get; } = [];
        public PromptResult? Ask(Question q)
        {
            Asked.Add(q.Id);
            return new PromptResult(q.Proposal!.OptionKey, q.Proposal.Value, null, AcceptedProposal: true);
        }
    }
    [Theory]
    [InlineData("NULL")]
    [InlineData("\"NULL\"")]       // how DESCRIBE prints it on DuckDB 2.0
    [InlineData("SQLNULL")]
    public void An_untyped_NULL_column_fits_any_declared_type_and_cannot_be_proposed_as_one(string resolved)
    {
        Assert.True(LogicalTypes.IsUntypedNull(resolved));
        Assert.True(LogicalTypes.Equivalent("INTEGER", resolved));
        Assert.True(LogicalTypes.Equivalent("VARCHAR(20)", resolved));
        var proposal = LogicalTypes.FromDuckDb(resolved);
        Assert.False(proposal.HasProposal);
        Assert.Contains("CAST(NULL AS <type>)", proposal.Reason);
        Assert.False(LogicalTypes.IsUntypedNull("INTEGER"));
    }
}
