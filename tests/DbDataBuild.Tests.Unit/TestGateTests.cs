using DbDataBuild.Cli;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`tests: { gate: { tags: [...] } }` (DESIGN.md 9.8): `plan`, `check` and `run` run the tests with those tags first, and a failing one refuses the plan before the target is read.</summary>
public class TestGateTests
{
    private static string Project(string gate, string ruleBody)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n" + gate);
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        Directory.CreateDirectory(Path.Combine(dir, "models/marts"));
        Directory.CreateDirectory(Path.Combine(dir, "tests/metadata"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/o.yml"), "name: marts.o\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/o.sql"), "SELECT order_id FROM staging.orders\n");
        File.WriteAllText(Path.Combine(dir, "tests/metadata/critical_rule.sql"), ruleBody);
        Assert.Equal(0, Cli(dir, "project", "compile").Exit);
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var env = new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_READ"] = "Server=127.0.0.1,1;User Id=r;Password=x;Connect Timeout=1" };
        var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: v => env.GetValueOrDefault(v));
        return (exit, o.ToString(), e.ToString());
    }

    private const string Gate = "tests:\n  gate:\n    tags: [critical]\n";
    private const string Failing = "-- tags: critical\nSELECT 'marts.o' AS violation\n";
    private const string Passing = "-- tags: critical\nSELECT 1 AS violation WHERE false\n";

    [Fact]
    public void A_failing_gated_test_refuses_the_plan_before_the_target_is_read()
    {
        var (exit, output, err) = Cli(Project(Gate, Failing), "connection", "deploy", "--write-plan");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("Nothing was planned: a test tagged critical", output);
        Assert.Contains("FAIL  critical_rule", output);
        Assert.DoesNotContain("could not connect", err + output, StringComparison.OrdinalIgnoreCase);     // the login points nowhere: the gate stopped first
        Assert.Equal(CliApp.ExitFindings, Cli(Project(Gate, Failing), "connection", "status").Exit);
    }

    [Fact]
    public void A_passing_gate_is_reported_and_planning_goes_on_to_the_target()
    {
        var (_, output, _) = Cli(Project(Gate, Passing), "connection", "deploy", "--write-plan");
        Assert.Contains("Test gate (tags critical): 1 test(s): 1 passed", output);
    }

    [Fact]
    public void A_test_with_another_tag_does_not_gate_and_no_gate_means_no_tests_are_run()
    {
        var other = Failing.Replace("critical", "naming");
        Assert.DoesNotContain("Nothing was planned: a test tagged", Cli(Project(Gate, other), "connection", "deploy", "--write-plan").Out);
        Assert.DoesNotContain("Test gate", Cli(Project("", Failing), "connection", "deploy", "--write-plan").Out);
    }

    [Theory]
    [InlineData("tests: []\n", "`tests` must be a mapping")]
    [InlineData("tests:\n  gate: {}\n", "needs `tags`")]
    [InlineData("tests:\n  gate:\n    tags: []\n", "must be a list of tags")]
    [InlineData("tests:\n  gate:\n    tags: [a]\n    severity: error\n", "severity")]
    [InlineData("tests:\n  nothing: 1\n", "nothing")]
    public void A_damaged_gate_setting_is_a_diagnostic(string gate, string message)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n" + gate);
        Assert.Contains(message, Cli(dir, "project", "compile").Err);
    }
}
