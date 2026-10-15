using System.Reflection;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class DefineCommandTests
{
    private const string Sources = "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: customer_id, type: BIGINT}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: code, type: VARCHAR(20), nullable: false}\n  - {name: order_date, type: DATE, nullable: false}\n";
    private const string Customers = "name: staging.customers\nkind: mapped\ngrain: [customer_id]\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n  - {name: name, type: VARCHAR(50)}\n";
    private const string OrdersSql = "SELECT o.order_id, o.customer_id, o.amount FROM staging.orders o\n";

    private static string Project(string? config = null)
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models", "staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), Sources);
        File.WriteAllText(Path.Combine(dir, "models/staging/customers.yml"), Customers);
        if (config != null) File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        return dir;
    }

    private static string Model(string dir, string name, string sql, string? yaml = null)
    {
        var stem = Path.Combine(dir, "models", name.Replace('.', '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllText(stem + ".sql", sql);
        if (yaml != null) File.WriteAllText(stem + ".yml", yaml);
        return stem;
    }

    private static (int Exit, string Out, string Err) Run(string? input, bool interactive, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, new StringReader(input ?? ""), interactive);
        return (exit, o.ToString(), e.ToString());
    }

    private static (int Exit, string Out, string Err) Define(string dir, params string[] more) => Run(null, false, ["project", "model", "update", "--project", dir, .. more]);

    private static string Answers(string dir, string yaml)
    {
        var path = Path.Combine(dir, "answers.yml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private const string FullAnswers = """
        answers:
          - {id: Q-define-marts.fct_orders-name, accept: inferred}
          - {id: Q-define-marts.fct_orders-kind, choice: full}
          - {id: Q-define-marts.fct_orders-connections, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.order_id.type, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.order_id.nullable, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.customer_id.type, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.customer_id.nullable, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.amount.type, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.amount.nullable, accept: inferred}
        """;

    private static string QueryBytes(string dir) =>
        string.Join("\n", Directory.EnumerateFiles(dir, "*.sql", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => f + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));

    // ---------------- modes and refusals ----------------

    [Fact]
    public void Without_a_terminal_and_without_write_or_check_define_refuses_to_guess()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", OrdersSql);
        var before = Snapshot(dir);
        var (exit, _, err) = Define(dir);
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("needs a terminal", err);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Check_cannot_be_combined_with_options_that_ask_or_write()
    {
        var dir = Project();
        foreach (var flag in new[] { "--write", "--accept-inferred" })
            Assert.Equal(CliApp.ExitUsage, Define(dir, "--check", flag).Exit);
        Assert.Equal(CliApp.ExitUsage, Define(dir, "--check", "--answers", Answers(dir, "answers: []\n")).Exit);
    }

    [Fact]
    public void Unknown_or_out_of_tree_paths_are_usage_errors()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", OrdersSql);
        Assert.Equal(CliApp.ExitUsage, Define(dir, "--check", "models/nope.sql").Exit);
        Assert.NotEqual(CliApp.ExitUsage, Define(dir, "--check", "models/staging/orders.yml").Exit);      // a mapped model is the connections' part now: compared with its table, or said to be skipped
        Assert.Equal(CliApp.ExitUsage, Define(dir, "--check", "dbdatabuild.yml").Exit);
    }

    // ---------------- check ----------------

    [Fact]
    public void Check_fails_when_a_definition_is_missing_or_stale_and_writes_nothing()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.fct_orders", OrdersSql);                                   // no definition yet
        Model(dir, "marts.stale", "SELECT o.order_id, o.amount FROM staging.orders o", "name: marts.stale\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT}\n");
        var before = Snapshot(dir);
        var (exit, output, err) = Define(dir, "--check");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("has no definition file `models/marts/fct_orders.yml`", err);
        Assert.Contains("returns column `amount`", err);
        Assert.Contains("FAILED: 2 difference(s)", output);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Check_passes_for_in_sync_definitions()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.fct_orders", OrdersSql, "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: customer_id, type: BIGINT}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
        var (exit, output, err) = Define(dir, "--check");
        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("OK: 1 definition(s) in sync", output);
        Assert.Contains("Config: dbdatabuild.yml", output);
    }

    // ---------------- non-interactive write ----------------

    [Fact]
    public void Write_with_answers_creates_the_definition_leaves_the_query_alone_and_is_idempotent()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.fct_orders", OrdersSql);
        var queries = QueryBytes(dir);

        var (exit, output, err) = Define(dir, "--write", "--answers", Answers(dir, FullAnswers));
        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("models/marts/fct_orders.sql -> models/marts/fct_orders.yml: new definition", output);
        Assert.Contains("--- /dev/null\n+++ b/models/marts/fct_orders.yml", output);          // the diff was shown
        Assert.Contains("wrote models/marts/fct_orders.yml", output);
        Assert.Equal("name: marts.fct_orders\nkind: full\ncolumns:\n  - name: order_id\n    type: BIGINT\n    nullable: false\n  - name: customer_id\n    type: BIGINT\n  - name: amount\n    type: DECIMAL(14, 2)\n",
                     File.ReadAllText(Path.Combine(dir, "models/marts/fct_orders.yml")));
        Assert.Equal(queries, QueryBytes(dir));                                                // the .sql is byte-identical

        var written = Snapshot(dir);
        var (exit2, output2, _) = Define(dir, "--write");                                      // second run: no answers needed, nothing changes
        Assert.Equal(CliApp.ExitOk, exit2);
        Assert.Contains("nothing to change", output2);
        Assert.Equal(written, Snapshot(dir));
        Assert.Equal(CliApp.ExitOk, Define(dir, "--check").Exit);                               // and --check agrees
    }

    [Fact]
    public void Write_without_enough_answers_lists_every_open_question_at_once_and_writes_nothing()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", OrdersSql);
        Model(dir, "marts.dim_customer", "SELECT c.customer_id, c.name FROM staging.customers c");
        var before = Snapshot(dir);
        var (exit, output, err) = Define(dir, "--write");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("Q-define-marts.fct_orders-kind", err);
        Assert.Contains("Q-define-marts.dim_customer-kind", err);
        Assert.Contains("Add one of these to the answers file:", err);
        Assert.Contains("    choice: view", err);
        Assert.Contains("Nothing was written: 2 model(s) need attention", output);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void One_incomplete_model_stops_all_writes()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.fct_orders", OrdersSql);
        Model(dir, "marts.dim_customer", "SELECT c.customer_id, c.name FROM staging.customers c");     // no answers for this one
        var answers = Answers(dir, FullAnswers);
        var before = Snapshot(dir);
        var (exit, output, _) = Define(dir, "--write", "--answers", answers);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("Nothing was written: 1 model(s) need attention", output);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Accept_inferred_is_recorded_in_the_output_and_covers_only_high_certainty_proposals()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", OrdersSql);
        var (exit, output, err) = Define(dir, "--write", "--accept-inferred");
        Assert.Equal(CliApp.ExitFindings, exit);                                               // kind and targets still need answers
        Assert.Contains("accepted inferred (--accept-inferred): Q-define-marts.fct_orders-name = use_name marts.fct_orders", output);
        Assert.Contains("accepted inferred (--accept-inferred): Q-define-marts.fct_orders-columns.amount.type = use_type DECIMAL(14, 2)", output);
        Assert.Contains("Q-define-marts.fct_orders-kind", err);
        Assert.DoesNotContain("accepted inferred (--accept-inferred): Q-define-marts.fct_orders-connections", output);
    }

    [Fact]
    public void A_bad_answers_file_is_reported_and_unused_answers_only_warn()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.fct_orders", OrdersSql);
        var (exit, _, err) = Define(dir, "--write", "--answers", Answers(dir, "answers:\n  - {id: Q-bogus-x, choice: a}\n"));
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-106  answers.yml", err);

        var (exit2, _, err2) = Define(dir, "--write", "--answers", Answers(dir, FullAnswers + "\n  - {id: Q-history-marts.other.x, choice: backfilled}\n"));
        Assert.Equal(CliApp.ExitOk, exit2);
        Assert.Contains("warning DDB-410", err2);
    }

    // ---------------- updating an existing definition ----------------

    private const string Existing = "# keep me\nname: marts.fct_orders\nkind: full   # reloaded\ncolumns:\n  - name: order_id\n    type: BIGINT\n    nullable: false\n  - name: customer_id   # fk\n    type: BIGINT\n";

    [Fact]
    public void An_update_adds_the_new_column_with_a_splice_and_keeps_comments()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.fct_orders", OrdersSql, Existing);
        var answers = Answers(dir, "answers:\n  - {id: Q-define-marts.fct_orders-columns.amount.type, accept: inferred}\n  - {id: Q-define-marts.fct_orders-columns.amount.nullable, accept: inferred}\n");
        var (exit, output, err) = Define(dir, "--write", "--answers", answers);
        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("fct_orders.yml: update", output);
        Assert.Contains("+  - name: amount\n+    type: DECIMAL(14, 2)", output);
        Assert.Equal(Existing + "  - name: amount\n    type: DECIMAL(14, 2)\n", File.ReadAllText(Path.Combine(dir, "models/marts/fct_orders.yml")));
        Assert.Equal(CliApp.ExitOk, Define(dir, "--check").Exit);
    }

    [Fact]
    public void An_unloadable_existing_definition_is_not_touched_and_its_errors_are_shown()
    {
        var dir = Project();
        var stem = Model(dir, "marts.fct_orders", OrdersSql, "name: marts.fct_orders\nbogus: 1\n");
        var before = File.ReadAllText(stem + ".yml");
        var (exit, _, err) = Define(dir, "--write", "--answers", Answers(dir, "answers: []\n"));
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-104", err);
        Assert.Equal(before, File.ReadAllText(stem + ".yml"));
    }

    [Fact]
    public void A_yml_without_a_query_is_reported_and_ignored()
    {
        var dir = Project();
        Directory.CreateDirectory(Path.Combine(dir, "models/marts"));
        File.WriteAllText(Path.Combine(dir, "models/marts/lonely.yml"), "name: marts.lonely\nkind: full\ncolumns:\n  - {name: a, type: INT}\n");
        var (exit, _, err) = Define(dir, "--check");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-108", err);
    }

    // ---------------- paths ----------------

    [Fact]
    public void Paths_select_files_or_directories_and_leave_other_models_alone()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.fct_orders", OrdersSql);
        Model(dir, "reports.daily", "SELECT c.customer_id FROM staging.customers c");
        var (_, out1, err1) = Define(dir, "--check", "models/marts");
        Assert.Contains("fct_orders", err1);
        Assert.DoesNotContain("daily", err1);
        Assert.Contains("FAILED: 1 difference(s)", out1);

        var (_, _, err2) = Define(dir, "--check", "models/reports/daily.sql");
        Assert.Contains("daily", err2);
        Assert.DoesNotContain("fct_orders", err2);

        Assert.Contains("daily", Define(dir, "--check", Path.Combine(dir, "models/reports/daily.yml")).Err);   // a .yml path selects the same model
    }

    // ---------------- interactive ----------------

    private static string Script(params string[] lines) => string.Join("\n", lines) + "\n";

    private static readonly string[] InteractiveForSmallModel =
    [
        // questions are asked in id order: columns.customer_id.nullable, .type, columns.name.nullable, .type, connections, kind, name
        "a", "", "a", "",       // customer_id nullable, type (accept inferred; no note)
        "a", "", "a", "",       // name nullable, type
        "a", "",                // connections
        "full", "",             // kind, typed as the option key
        "a", "",                // name
        "y",                    // write?
    ];

    [Fact]
    public void Interactive_define_asks_shows_the_diff_and_writes_after_confirmation()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.dim_customer", "SELECT c.customer_id, c.name FROM staging.customers c");
        var (exit, output, err) = Run(Script(InteractiveForSmallModel), true, "project", "model", "update", "--project", dir);
        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("Q-define-marts.dim_customer-kind", output);
        Assert.Contains("Write 1 definition file(s)? [y/N]", output);
        Assert.Equal("name: marts.dim_customer\nkind: full\ncolumns:\n  - name: customer_id\n    type: BIGINT\n    nullable: false\n  - name: name\n    type: VARCHAR(50)\n",
                     File.ReadAllText(Path.Combine(dir, "models/marts/dim_customer.yml")));
    }

    [Fact]
    public void Interactive_decline_writes_nothing()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.dim_customer", "SELECT c.customer_id, c.name FROM staging.customers c");
        var lines = InteractiveForSmallModel.SkipLast(1).Append("n").ToArray();
        var before = Snapshot(dir);
        var (exit, output, _) = Run(Script(lines), true, "project", "model", "update", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("Nothing was written.", output);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Interactive_end_of_input_leaves_questions_open_and_writes_nothing()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Model(dir, "marts.dim_customer", "SELECT c.customer_id, c.name FROM staging.customers c");
        var before = Snapshot(dir);
        var (exit, output, err) = Run(Script("a", ""), true, "project", "model", "update", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("Nothing was written", output);
        Assert.Contains("DDB-414", err);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void A_file_changed_after_it_was_read_is_never_overwritten()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        var stem = Model(dir, "marts.dim_customer", "SELECT c.customer_id, c.name FROM staging.customers c");
        var yml = stem + ".yml";
        // someone creates the definition while define waits at the confirmation prompt
        var reader = new TriggerReader(Script(InteractiveForSmallModel.SkipLast(1).ToArray()), "y", () => File.WriteAllText(yml, "someone: else\n"));
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliApp.Run(["project", "model", "update", "--project", dir], o, e, reader, interactive: true);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-421", e.ToString());
        Assert.Equal("someone: else\n", File.ReadAllText(yml));
    }

    private sealed class TriggerReader(string script, string last, Action beforeLast) : TextReader
    {
        private readonly Queue<string> lines = new(script.Split('\n').SkipLast(1).Append(last));
        public override string? ReadLine()
        {
            if (lines.Count == 0) return null;
            if (lines.Count == 1) beforeLast();
            return lines.Dequeue();
        }
    }

    // ---------------- invariants ----------------

    [Fact]
    public void Define_is_declared_repo_files_only_and_never_references_a_database_driver()
    {
        var spec = CommandSpecs.All.Single(c => c.Name == "project model update");
        Assert.Equal(EffectClass.TargetReadOnly, spec.Effect);        // it reads the connections of the mapped models unless --offline
        Assert.True(spec.Implemented);

        var assemblies = new[] { typeof(CliApp), typeof(DbDataBuild.Define.DefineEngine), typeof(DbDataBuild.Models.ProjectValidator), typeof(DbDataBuild.Sql.Polyglot),
            typeof(DbDataBuild.Core.ProductInfo), typeof(DbDataBuild.Targets.DuckDb.QueryDescriber) }.Select(t => t.Assembly);
        var forbidden = new[] { "Microsoft.Data.SqlClient", "System.Data.SqlClient", "Npgsql" };
        foreach (var a in assemblies)
            Assert.DoesNotContain(a.GetReferencedAssemblies(), r => forbidden.Contains(r.Name));
    }

    [Fact]
    public void Help_describes_define_with_its_effect_class()
    {
        var (exit, help, _) = Run(null, false, "project", "model", "update", "--help");
        Assert.Equal(0, exit);
        Assert.Contains("Target read-only", help);
        Assert.Contains("--check", help);
        Assert.Contains("--accept-inferred", help);
    }

    // ---------------- a query file with a head ----------------

    private const string HeadAnswers = """
        answers:
          - {id: Q-define-marts.fct_orders-connections, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.order_id.type, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.order_id.nullable, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.customer_id.type, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.customer_id.nullable, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.amount.type, accept: inferred}
          - {id: Q-define-marts.fct_orders-columns.amount.nullable, accept: inferred}
        """;

    [Fact]
    public void A_head_has_already_said_the_name_and_the_kind_so_define_does_not_ask_or_write_them()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        var stem = Model(dir, "marts.fct_orders", "CREATE TABLE marts.fct_orders AS\n" + OrdersSql);
        var (exit, output, err) = Define(dir, "--write", "--answers", Answers(dir, HeadAnswers));
        Assert.True(exit == CliApp.ExitOk, output + err);
        var written = File.ReadAllText(stem + ".yml");
        Assert.False(written.StartsWith("name:", StringComparison.Ordinal));
        Assert.DoesNotContain("kind:", written);
        Assert.StartsWith("columns:", written);
        Assert.Contains("CREATE TABLE marts.fct_orders AS", File.ReadAllText(stem + ".sql"));                       // the query file is never written
        // the project reads the two together, and define sees them in sync
        var model = DbDataBuild.Models.ProjectValidator.Validate(dir).BuiltModels.Single(s => s.Definition.Name == "marts.fct_orders");
        Assert.Equal("full", model.Definition.Kind);
        Assert.Equal((CliApp.ExitOk, ""), (Define(dir, "--check").Exit, Define(dir, "--check").Err));
    }

    [Fact]
    public void A_head_with_a_key_gives_the_unique_key_and_define_asks_only_for_what_it_does_not_say()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        var stem = Model(dir, "marts.fct_orders", "CREATE TABLE marts.fct_orders WITH (kind = 'incremental_by_unique_key', unique_key = (order_id)) AS\n" + OrdersSql);
        var (_, _, err) = Define(dir, "--write", "--answers", Answers(dir, HeadAnswers));
        Assert.Contains("Q-define-marts.fct_orders-grain", err);                                                    // the grain is still asked
        Assert.DoesNotContain("Q-define-marts.fct_orders-unique_key", err);                                         // the key is the head's
        Assert.DoesNotContain("Q-define-marts.fct_orders-kind", err);
        var (exit, output, err2) = Define(dir, "--write", "--answers", Answers(dir, HeadAnswers.Replace("answers:\n", "answers:\n  - {id: Q-define-marts.fct_orders-grain, choice: candidate_1}\n  - {id: Q-define-marts.fct_orders-indexes, choice: no_indexes}\n")));
        Assert.True(exit == CliApp.ExitOk, output + err2);
        Assert.DoesNotContain("unique_key", File.ReadAllText(stem + ".yml"));
        Assert.Equal(["order_id"], DbDataBuild.Models.ProjectValidator.Validate(dir).BuiltModels.Single(s => s.Definition.Name == "marts.fct_orders").Definition.UniqueKey);
    }
}
