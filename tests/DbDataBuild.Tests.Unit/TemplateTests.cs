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
    public void The_distribution_carries_five_projects()
    {
        Assert.Contains(TemplateStore.All, t => t.Name == "starter");
        Assert.Contains(TemplateStore.All, t => t.Name == "retail");
        Assert.Contains(TemplateStore.All, t => t.Name == "chinook");
        Assert.Contains(TemplateStore.All, t => t.Name == "adventureworks");
        Assert.Contains(TemplateStore.All, t => t.Name == "stores");
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
    public void The_stores_template_compiles_a_refresh_plan_per_engine_that_leaves_the_copy_to_a_deploy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, Run("project", "create", "stores", dir).Exit);
            var (exit, output, err) = Run("project", "compile", "--project", dir);
            Assert.True(exit == 0, output + err);
            foreach (var connection in new[] { "sqlserver", "postgres" })
            {
                var plan = File.ReadAllText(Path.Combine(dir, "rendered", connection, "refresh.plan.yml"));
                Assert.Contains("model: \"marts.daily_sales\"", plan);                                        // the fan-out model is a routine load on both engines
                Assert.Matches("excluded:\\s*\n\\s*- model: \"warehouse.orders\"\\s*\n\\s*reason: \"a copy moves rows between connections; a deploy plans it\"", plan);
            }
            Assert.False(File.Exists(Path.Combine(dir, "rendered", "store_east", "refresh.plan.yml")));     // a shop system is only read: nothing is built on it
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
