using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Index lint (DDB-223, DDB-224): advice only, never inferred silently, always with the exact index to declare.</summary>
public class IndexAdvisorTests
{
    private const string Cols = "columns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: customer_id, type: BIGINT}\n  - {name: event_ts, type: TIMESTAMP}\n";
    private const string KeyModel = "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\ngrain: [order_id]\n" + Cols;
    private const string TimeModel = "name: marts.fct_orders\nkind: {incremental_by_time_range: {time_column: event_ts}}\ngrain: [order_id]\n" + Cols;
    private static readonly string[] Two = ["sqlserver", "postgres"];

    private static IReadOnlyList<IndexAdvice> Advise(string yaml, params string[] targets)
    {
        var (def, diags) = Load(yaml);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return IndexAdvisor.For(def!, targets.Length == 0 ? Two : targets);
    }

    [Fact]
    public void A_key_load_with_no_index_gets_a_warning_with_the_index_to_declare()
    {
        var a = Assert.Single(Advise(KeyModel));
        Assert.Equal(DiagnosticCatalog.MergeKeyNotIndexed.Code, a.Code);
        Assert.Equal(Severity.Warning, a.Severity);
        Assert.Equal(["order_id"], a.Columns);
        Assert.Equal("ux_fct_orders_order_id", a.SuggestedName);
        Assert.Equal("- {name: ux_fct_orders_order_id, columns: [order_id], unique: true}", IndexAdvisor.Yaml(a));
        Assert.Equal(Two, a.Targets);
    }

    [Fact]
    public void The_advice_is_satisfied_by_declaring_exactly_what_it_says()
    {
        var a = Assert.Single(Advise(KeyModel));
        Assert.Empty(Advise(KeyModel + "indexes:\n  " + IndexAdvisor.Yaml(a) + "\n"));
    }

    [Fact]
    public void An_index_that_leads_with_the_key_in_any_order_satisfies_a_composite_key()
    {
        const string model = "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id, customer_id]}}\ngrain: [order_id, customer_id]\n" + Cols;
        Assert.Single(Advise(model));
        Assert.Empty(Advise(model + "indexes:\n  - {name: u, columns: [customer_id, order_id], unique: true}\n"));
        Assert.Empty(Advise(model + "indexes:\n  - {name: u, columns: [order_id, customer_id], unique: true, include: [event_ts]}\n"));
        Assert.Single(Advise(model + "indexes:\n  - {name: u, columns: [event_ts, order_id, customer_id], unique: true}\n"));        // does not lead with the key
        Assert.Single(Advise(model + "indexes:\n  - {name: u, columns: [order_id], unique: true}\n"));                                // covers only part of it
    }

    [Fact]
    public void An_indexed_key_that_is_not_declared_unique_is_a_note_because_enforcement_is_the_operators_choice()
    {
        var a = Assert.Single(Advise(KeyModel + "indexes:\n  - {name: ix_key, columns: [order_id]}\n"));
        Assert.Equal(Severity.Note, a.Severity);
        Assert.Equal(DiagnosticCatalog.LoadColumnNotIndexed.Code, a.Code);
        Assert.Equal("ix_key", a.Existing);
    }

    [Fact]
    public void An_index_restricted_to_one_target_does_not_cover_the_other()
    {
        const string indexes = "indexes:\n  - {name: u, columns: [order_id], unique: true, connections: [sqlserver]}\n";
        var a = Assert.Single(Advise(KeyModel + indexes));
        Assert.Equal(["postgres"], a.Targets);
        Assert.Empty(Advise(KeyModel + indexes, "sqlserver"));
    }

    [Fact]
    public void A_time_range_model_is_advised_to_index_its_time_column_with_a_note()
    {
        var a = Assert.Single(Advise(TimeModel));
        Assert.Equal((IndexReason.TimeColumn, Severity.Note, false), (a.Reason, a.Severity, a.WantUnique));
        Assert.Equal("ix_fct_orders_event_ts", a.SuggestedName);
        Assert.Empty(Advise(TimeModel + "indexes:\n  - {name: ix_ts, columns: [event_ts]}\n"));
    }

    [Fact]
    public void Declared_loads_are_each_advised_on_what_they_use()
    {
        const string model = "name: marts.fct_orders\nkind: {incremental_by_time_range: {time_column: event_ts}}\ngrain: [order_id]\n" + Cols +
            "loads:\n  daily:\n    default: true\n    strategy: watermark_append\n    watermark: {column: order_id, resolver: target_max}\n  by_key:\n    strategy: merge_by_key\n    key: [customer_id]\n  reload:\n    strategy: delete_insert_by_range\n    params: {start: TIMESTAMP, end: TIMESTAMP}\n    max_span: 30 days\n";
        var (def, diags) = Load(model);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        var advice = IndexAdvisor.For(def!, ["sqlserver"]);
        Assert.Equal([IndexReason.MergeKey, IndexReason.TimeColumn, IndexReason.Watermark], advice.Select(a => a.Reason).Order());
        Assert.Equal(["customer_id"], advice.Single(a => a.Reason == IndexReason.MergeKey).Columns);
    }

    [Fact]
    public void Full_and_view_models_need_no_index_advice()
    {
        Assert.Empty(Advise("name: marts.fct_orders\nkind: full\n" + Cols));
    }

    [Fact]
    public void Generated_names_stay_within_PostgreSQLs_limit_and_do_not_collide()
    {
        var shortName = IndexAdvisor.NameFor("marts.t", ["a"], unique: true);
        Assert.Equal("ux_t_a", shortName);
        var cols1 = Enumerable.Range(0, 8).Select(i => $"very_long_column_name_{i}a").ToList();
        var cols2 = Enumerable.Range(0, 8).Select(i => $"very_long_column_name_{i}b").ToList();
        var n1 = IndexAdvisor.NameFor("marts.fct_orders", cols1, false);
        var n2 = IndexAdvisor.NameFor("marts.fct_orders", cols2, false);
        Assert.All([n1, n2], n => Assert.InRange(n.Length, 1, 60));
        Assert.NotEqual(n1, n2);
        Assert.Equal(n1, IndexAdvisor.NameFor("marts.fct_orders", cols1, false));
        Assert.Matches("^[a-z_][a-z0-9_]*$", n1);
    }

    // ---- through the CLI ----

    private static string Project(string modelYaml, string config = "defaults: {connections: [sqlserver]}\n")
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/t.yml"), "name: staging.t\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: customer_id, type: BIGINT}\n  - {name: event_ts, type: TIMESTAMP}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), modelYaml);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT order_id, customer_id, event_ts FROM staging.t");
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void Validate_shows_the_advice_with_the_exact_index_and_does_not_fail()
    {
        var (exit, output, err) = Cli("project", "compile", "--project", Project(KeyModel));
        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("warning DDB-223  models/marts/fct_orders.yml", err);
        Assert.Contains("loads by key (order_id)", err);
        Assert.Contains("- {name: ux_fct_orders_order_id, columns: [order_id], unique: true}", err);
        Assert.Contains("1 warning(s)", output);
    }

    [Fact]
    public void An_operator_can_silence_one_code_for_a_model_or_all_index_advice_for_the_project()
    {
        Assert.DoesNotContain("DDB-223", Cli("project", "compile", "--project", Project(KeyModel + "lint_ignore: [DDB-223]\n")).Err);
        Assert.DoesNotContain("DDB-223", Cli("project", "compile", "--project", Project(KeyModel, "defaults: {connections: [sqlserver]}\nlint:\n  indexes: false\n")).Err);
        // silencing 224 does not silence 223
        Assert.Contains("DDB-223", Cli("project", "compile", "--project", Project(KeyModel + "lint_ignore: [DDB-224]\n")).Err);
    }

    [Fact]
    public void An_unknown_lint_code_or_lint_setting_is_an_error()
    {
        var model = Cli("project", "compile", "--project", Project(KeyModel + "lint_ignore: [DDB-999]\n"));
        Assert.Equal(CliApp.ExitFindings, model.Exit);
        Assert.Contains("`DDB-999` is not an advisory lint code", model.Err);
        var cfg = Cli("project", "compile", "--project", Project(KeyModel, "defaults: {connections: [sqlserver]}\nlint:\n  indexes: maybe\n"));
        Assert.Equal(CliApp.ExitFindings, cfg.Exit);
        Assert.Contains("`lint.indexes` must be true or false", cfg.Err);
    }
}
