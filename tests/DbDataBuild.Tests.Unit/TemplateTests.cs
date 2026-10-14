using DbDataBuild.Cli;

namespace DbDataBuild.Tests.Unit;

/// <summary>Every template built into the executable has to work the way its README says: it validates clean, it seeds, its models run on the seeded data, and its own tests pass.</summary>
public class TemplateTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    public static TheoryData<string> Templates()
    {
        var data = new TheoryData<string>();
        foreach (var t in TemplateStore.All) data.Add(t.Name);
        return data;
    }

    [Fact]
    public void The_distribution_carries_six_projects()
    {
        Assert.Contains(TemplateStore.All, t => t.Name == "starter");
        Assert.Contains(TemplateStore.All, t => t.Name == "retail");
        Assert.Contains(TemplateStore.All, t => t.Name == "chinook");
        Assert.Contains(TemplateStore.All, t => t.Name == "adventureworks");
        Assert.Contains(TemplateStore.All, t => t.Name == "fan_in_fan_out");
        Assert.Contains(TemplateStore.All, t => t.Name == "macro_examples");
        Assert.All(TemplateStore.All, t => Assert.NotEqual("", t.Description));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void A_template_creates_validates_seeds_samples_and_passes_its_tests(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (newExit, _, newErr) = Run("project", "create", name, dir);
            Assert.True(newExit == 0, newErr);

            var (validateExit, validateOut, _) = Run("project", "compile", "--project", dir);
            Assert.True(validateExit == 0, validateOut);
            Assert.Contains(" 0 warning(s)", validateOut);

            var (seedExit, seedOut, seedErr) = Run("project", "seed", "--project", dir);
            Assert.True(seedExit == 0, seedOut + seedErr);

            var models = Directory.EnumerateFiles(Path.Combine(dir, "models"), "*.sql", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Path.Combine(dir, "models"), f).Replace('\\', '/'))
                .Where(f => f.StartsWith("marts/", StringComparison.Ordinal))
                .Select(f => "marts." + Path.GetFileNameWithoutExtension(f))
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();
            Assert.NotEmpty(models);
            foreach (var model in models)
            {
                var (sampleExit, sampleOut, sampleErr) = Run("project", "sample", model, "--project", dir, "--limit", "3");
                Assert.True(sampleExit == 0, $"{model}: {sampleOut}{sampleErr}");
            }

            var (defineExit, defineOut, defineErr) = Run("project", "model", "update", "--project", dir, "--check");        // every declared column is what the query returns (a deploy refuses the model otherwise)
            Assert.True(defineExit == 0, defineOut + defineErr);

            var (testExit, testOut, _) = Run("project", "tests", "run", "--project", dir);
            Assert.True(testExit == 0, testOut);
            Assert.Contains(" 0 failed", testOut);

            var (renderExit, renderOut, renderErr) = Run("project", "compile", "--project", dir, "--content");
            Assert.True(renderExit == 0, renderOut + renderErr);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_fan_in_template_compiles_a_refresh_plan_per_engine_in_which_the_copy_is_a_load_before_the_report()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, Run("project", "create", "fan_in_fan_out", dir).Exit);
            var (exit, output, err) = Run("project", "compile", "--project", dir);
            Assert.True(exit == 0, output + err);
            foreach (var connection in new[] { "sqlserver", "postgres" })
            {
                var loads = File.ReadAllText(Path.Combine(dir, "rendered", connection, "loads.yml"));
                Assert.Contains("model: \"marts.daily_sales\"", loads);                                       // the fan-out model is a load on both engines
                Assert.DoesNotContain("excluded:", loads);                                                    // the copy is a load like any other
                Assert.Contains("model: \"warehouse.orders\"", loads);
                Assert.Contains("origins:", loads);
                Assert.Contains("connection: \"store_east\"", loads);
                Assert.Contains("connection: \"store_west\"", loads);
                Assert.Contains("read_incremental:", loads);                                                  // the copy is incremental: its read has the watermark bound
                // the load `full` of the copy reads each shop in full: its copy block has the plain read and no bounded one
                var fullAt = loads.IndexOf("load: \"full\"", StringComparison.Ordinal);
                Assert.True(fullAt > 0, loads);
                var afterFull = loads[fullAt..];
                var nextLoad = afterFull.IndexOf("\n  - hash:", StringComparison.Ordinal);
                var fullBlock = nextLoad < 0 ? afterFull : afterFull[..nextLoad];
                Assert.Contains("read: ", fullBlock);
                Assert.DoesNotContain("read_incremental:", fullBlock);
                Assert.DoesNotContain("watermark:", fullBlock);
                Assert.Contains("load: \"full\"", File.ReadAllText(Path.Combine(dir, "rendered", connection, "refresh", "weekly.yml")));
                foreach (var plan in new[] { "hourly", "daily", "weekly" })
                {
                    var text = File.ReadAllText(Path.Combine(dir, "rendered", connection, "refresh", plan + ".yml"));
                    Assert.True(text.IndexOf("warehouse.orders", StringComparison.Ordinal) is var copyAt and >= 0 && text.IndexOf("marts.daily_sales", StringComparison.Ordinal) > copyAt, plan + ": the copy comes before the report that reads it\n" + text);
                }
            }
            Assert.False(File.Exists(Path.Combine(dir, "rendered", "store_east", "loads.yml")));     // a shop system is only read: nothing is built on it
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_macros_template_gives_each_table_the_query_written_for_it_and_no_engine_is_ever_given_a_macro()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, Run("project", "create", "macro_examples", dir).Exit);
            var (exit, output, err) = Run("project", "compile", "--project", dir);
            Assert.True(exit == 0, output + err);
            string Script(string model) => File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", model, "load.default.sql"));
            var live = Script("marts.status_totals_live");
            var history = Script("marts.status_totals_history");
            // one macro, two tables of one shape: the live one has no day column, the history one groups by it, and neither carries the choice between them
            Assert.Contains("'2024-06-30'", live);
            Assert.DoesNotContain("orders_history", live);
            Assert.DoesNotContain("snap_date", live);
            Assert.Contains("orders_history", history);
            Assert.Contains("snap_date", history);
            foreach (var script in new[] { live, history, Script("marts.orders") })
            {
                Assert.DoesNotContain("CASE WHEN NULL", script);
                Assert.DoesNotContain("UNION", script);
                foreach (var macro in new[] { "dollars(", "size_band(", "status_group(", "snapshot_at(", "status_totals(", "money" })
                    Assert.DoesNotContain(macro, script);
            }
            Assert.Contains("NUMERIC(12, 2)", Script("marts.orders"));                                     // the `money` alias is the type it stands for
            // every definition says what its query returns, also where the query is given a table by a NAME parameter (`model update` binds the query too)
            var define = Run("project", "model", "update", "--project", dir, "--check");
            Assert.True(define.Exit == 0, define.Out + define.Err);
            // the name parameters reach the query as the names they hold
            var graph = Run("project", "show", "graph", "--project", dir, "--format", "json", "marts.status_totals_history");
            Assert.Contains("ledger.orders_history", graph.Out);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
