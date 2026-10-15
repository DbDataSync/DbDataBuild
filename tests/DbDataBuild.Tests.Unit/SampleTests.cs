using DbDataBuild.Cli;
using DbDataBuild.Models;
using DbDataBuild.Sample;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Sample data (DESIGN.md 15.2): deterministic, awkward on purpose, and run through models offline.</summary>
public class SampleTests
{
    private static DeclaredModel Orders(params string[] grain) => new("staging.orders",
    [
        new ColumnDefinition("order_id", "BIGINT", false), new ColumnDefinition("customer", "VARCHAR(20)"), new ColumnDefinition("amount", "DECIMAL(14, 2)"),
        new ColumnDefinition("placed", "DATE"), new ColumnDefinition("score", "DOUBLE"), new ColumnDefinition("paid", "BOOLEAN", false), new ColumnDefinition("at", "TIMESTAMP"),
    ], grain);

    [Fact]
    public void The_same_seed_gives_the_same_rows_and_another_seed_gives_others()
    {
        var a = SampleGenerator.Rows(Orders("order_id"), 30, 1);
        Assert.Equal(a.Select(r => string.Join("|", r)), SampleGenerator.Rows(Orders("order_id"), 30, 1).Select(r => string.Join("|", r)));
        Assert.NotEqual(a.Select(r => string.Join("|", r)), SampleGenerator.Rows(Orders("order_id"), 30, 2).Select(r => string.Join("|", r)));
    }

    [Fact]
    public void Grain_columns_are_unique_not_null_columns_never_null_and_nullable_ones_sometimes_are()
    {
        var src = Orders("order_id");
        var rows = SampleGenerator.Rows(src, 200, 7);
        Assert.Equal(200, rows.Select(r => r[0]).Distinct().Count());
        Assert.DoesNotContain(rows, r => r[0] == "NULL" || r[5] == "NULL");
        Assert.Contains(rows, r => r[1] == "NULL");
        Assert.Contains(rows, r => r[2] == "NULL");

        var composite = new DeclaredModel("s.t", [new ColumnDefinition("a", "INTEGER", false), new ColumnDefinition("b", "INTEGER", false)], ["a", "b"]);
        var crows = SampleGenerator.Rows(composite, 100, 1);
        Assert.Equal(100, crows.Select(r => r[0] + "," + r[1]).Distinct().Count());
        Assert.True(crows.Select(r => r[0]).Distinct().Count() < 100);                // the first grain column repeats, so groups and joins find matches
    }

    [Fact]
    public void Strings_include_the_cases_the_string_profile_cares_about_and_respect_the_declared_length()
    {
        var rows = SampleGenerator.Rows(new DeclaredModel("s.t", [new ColumnDefinition("w", "VARCHAR(3)", false)], []), 300, 1).Select(r => r[0]).ToList();
        Assert.All(rows, r => Assert.True(r[1..^1].Replace("''", "'").Length <= 3));        // the declared length, after unquoting
        Assert.Contains("'Alp'", rows); Assert.Contains("'alp'", rows);                  // differ only by case
        Assert.Contains("''", rows);                                                    // empty
        var wide = SampleGenerator.Rows(new DeclaredModel("s.t", [new ColumnDefinition("w", "VARCHAR(50)", false)], []), 300, 1).Select(r => r[0]).ToList();
        Assert.Contains("'alpha '", wide);                                              // trailing space
        Assert.Contains("'été'", wide); Assert.Contains("'ete'", wide);                 // accent
        Assert.Contains("'O''Brien'", wide);                                            // quoting
    }

    [Fact]
    public void A_type_with_no_generator_says_so_and_how_to_supply_rows()
    {
        var ex = Assert.Throws<SampleException>(() => SampleGenerator.Rows(new DeclaredModel("s.t", [new ColumnDefinition("p", "GEOMETRY", false)], []), 5, 1));
        Assert.Contains("column `p` of type GEOMETRY", ex.Message);
        Assert.Contains("--data", ex.Message);
    }

    private static SampleModel Model(string name, string sql, params string[] upstream) => new(name, [], sql, upstream);

    [Fact]
    public void A_downstream_model_reads_what_its_upstream_model_produced_and_sources_are_filled()
    {
        var models = new[]
        {
            Model("marts.per_customer", "SELECT customer, COUNT(*) AS n, SUM(amount) AS total FROM staging.orders GROUP BY customer", "staging.orders"),
            Model("marts.top", "SELECT customer, n FROM marts.per_customer WHERE n >= 1 ORDER BY n DESC, customer LIMIT 3", "marts.per_customer"),
        };
        var result = SampleRun.Run([Orders("order_id")], models, ["marts.top"], new SampleOptions(Rows: 100, Seed: 3, Limit: 10));
        var top = result.Tables.Single(t => t.Name == "marts.top");
        Assert.Null(top.Error);
        Assert.Equal(3, top.RowCount);
        Assert.Equal(["customer", "n"], top.Columns.Select(c => c.Name));
        var source = result.Tables.Single(t => t.Name == "staging.orders");
        Assert.Equal(100, source.RowCount);
        Assert.StartsWith("generated (100 rows, seed 3)", source.Origin);
        Assert.DoesNotContain(result.Tables, t => t.Name == "marts.per_customer");          // an intermediate model ran but is not shown unless selected
    }

    [Fact]
    public void A_failing_model_reports_its_error_and_its_dependents_are_not_run()
    {
        var models = new[]
        {
            Model("marts.bad", "SELECT CAST(customer AS INTEGER) AS x FROM staging.orders WHERE customer <> ''", "staging.orders"),
            Model("marts.next", "SELECT x FROM marts.bad", "marts.bad"),
        };
        var result = SampleRun.Run([Orders("order_id")], models, ["marts.next"], new SampleOptions(Rows: 50, Seed: 1));
        Assert.Contains("Could not convert", result.Tables.Single(t => t.Name == "marts.bad").Error);
        Assert.Contains("reads `marts.bad`", result.Tables.Single(t => t.Name == "marts.next").Error);
    }

    [Fact]
    public void Declared_columns_that_the_query_does_not_return_are_a_warning()
    {
        var model = new SampleModel("marts.m", [new ColumnDefinition("customer", "VARCHAR(20)"), new ColumnDefinition("missing", "INTEGER")], "SELECT customer, 1 AS extra FROM staging.orders", ["staging.orders"]);
        var t = SampleRun.Run([Orders()], [model], ["marts.m"], new SampleOptions(Rows: 5)).Tables.Single(x => x.Name == "marts.m");
        Assert.Contains(t.Warnings, w => w.Contains("missing"));
        Assert.Contains(t.Warnings, w => w.Contains("extra"));
    }

    [Fact]
    public void Supplied_csv_rows_replace_generated_ones_and_a_bad_file_is_explained()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "staging.orders.csv"), "order_id,customer,amount,placed,score,paid,at\n1,ann,10.50,2024-02-29,1.5,true,2024-02-29 10:00:00\n2,,0.005,2024-03-01,,false,\n");
        var model = Model("marts.m", "SELECT order_id, customer, amount FROM staging.orders ORDER BY order_id", "staging.orders");
        var r = SampleRun.Run([Orders("order_id")], [model], ["marts.m"], new SampleOptions(DataDir: dir));
        Assert.Equal("staging.orders.csv", r.Tables.Single(t => t.Name == "staging.orders").Origin);
        var rows = r.Tables.Single(t => t.Name == "marts.m").Rows;
        Assert.Equal(["1", "ann", "10.50"], rows[0]);
        Assert.Equal(["2", null, "0.01"], rows[1].Select((v, i) => i == 2 ? "0.01" : v).ToList());   // NULLs stay NULL

        File.WriteAllText(Path.Combine(dir, "staging.orders.csv"), "order_id,customer\nnot-a-number,x\n");
        var bad = SampleRun.Run([Orders("order_id")], [model], ["marts.m"], new SampleOptions(DataDir: dir));
        Assert.Contains("could not be read as `staging.orders`", bad.Tables.Single(t => t.Name == "staging.orders").Error);
    }

    [Fact]
    public void A_model_cannot_read_files_while_it_runs_on_sample_data()
    {
        var model = Model("marts.m", "SELECT * FROM read_csv('/etc/hostname')");
        var r = SampleRun.Run([], [model], ["marts.m"], new SampleOptions());
        Assert.NotNull(r.Tables.Single(t => t.Name == "marts.m").Error);
    }

    // ---- the command ----

    private static string Project()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\nlint:\n  indexes: false\n");
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: customer, type: \"VARCHAR(20)\"}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/by_customer.yml"), "name: marts.by_customer\nkind: full\ncolumns:\n  - {name: customer, type: \"VARCHAR(20)\"}\n  - {name: n, type: BIGINT}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/by_customer.sql"), "SELECT customer, COUNT(*) AS n FROM staging.orders GROUP BY customer\n");
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void The_sample_command_prints_a_table_and_makes_no_change_to_the_project()
    {
        var dir = Project();
        var before = Snapshot(dir);
        var (exit, output, err) = Cli("project", "sample", "--project", dir, "--rows", "40", "--limit", "5");
        Assert.Equal(0, exit);
        Assert.Contains("effect: Offline only", output);
        Assert.Contains("marts.by_customer  (full, ", output);
        Assert.Contains("customer  n", output);
        Assert.Contains("more row(s)", output);
        Assert.Contains("OK: 1 model(s) ran on sample data (seed 1).", output);
        Assert.Equal("", err);
        Assert.Equal(before, Snapshot(dir));
        Assert.DoesNotContain("staging.orders  (mapped", output);
        Assert.Contains("staging.orders  (mapped", Cli("project", "sample", "--project", dir, "--declared").Out);
    }

    [Fact]
    public void The_sample_command_refuses_nonsense_options_and_unknown_models()
    {
        var dir = Project();
        Assert.Equal(CliApp.ExitUsage, Cli("project", "sample", "--project", dir, "--rows", "0").Exit);
        Assert.Equal(CliApp.ExitUsage, Cli("project", "sample", "--project", dir, "--limit", "-1").Exit);
        Assert.Equal(CliApp.ExitUsage, Cli("project", "sample", "--project", dir, "--data", Path.Combine(dir, "nope")).Exit);
        Assert.Equal(CliApp.ExitUsage, Cli("project", "sample", "nothing.here", "--project", dir).Exit);
    }

    [Fact]
    public void A_failing_model_makes_the_command_fail_with_the_reason()
    {
        var dir = Project();
        File.WriteAllText(Path.Combine(dir, "models/marts/by_customer.sql"), "SELECT CAST(customer AS INTEGER) AS customer, COUNT(*) AS n FROM staging.orders WHERE customer <> '' GROUP BY 1\n");
        var (exit, output, err) = Cli("project", "sample", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("failed", output);
        Assert.Contains("marts.by_customer failed on sample data", err);
    }
}
