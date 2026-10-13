using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Planning;

namespace DbDataBuild.Tests.Unit;

/// <summary>The pieces of the moved-input rule that need no database (DDB-454): the override a stop prints as it would be typed, reading and matching `--input`, and the code's catalog entry. The stop itself is proved on real engines.</summary>
public class InputMovedTests
{
    [Fact]
    public void A_moved_input_prints_the_override_as_it_would_be_typed_quoted_when_it_has_a_space()
    {
        Assert.Equal("--input marts.fct.backfill.start=2024-03-01", new MovedInput("3", "marts.fct", "backfill", "start", "2024-02-28", "2024-03-01").Override);
        Assert.Equal("--input \"marts.fct.backfill.start=2024-03-01 10:30:00\"", new MovedInput("3", "marts.fct", "backfill", "start", "2024-02-28 00:00:00", "2024-03-01 10:30:00").Override);
        // what it prints is what the command reads back
        var pin = InputPin.Parse("marts.fct.backfill.start=2024-03-01", out var problem)!;
        Assert.Null(problem);
        Assert.Equal(("marts.fct.backfill", "start", "2024-03-01"), (pin.Target, pin.Name, pin.Value));
        Assert.True(pin.Matches("marts.fct", "backfill"));
        Assert.False(pin.Matches("marts.fct", "default"));
        Assert.False(pin.Matches("marts.other", "backfill"));
    }

    [Fact]
    public void An_input_flag_is_a_name_and_a_value_with_an_optional_load_before_the_name()
    {
        Assert.True(InputPin.Parse("days=7", out _)!.Matches("any.model", "any_load"));
        Assert.True(InputPin.Parse("marts.fct.days=7", out _)!.Matches("marts.fct", "x"));                                   // the model alone is enough
        Assert.True(InputPin.Parse("start=2024-01-01 10:00:00", out _) is { Value: "2024-01-01 10:00:00" });
        foreach (var bad in new[] { "days", "=7", "days=", ".days=7", "m.=7" })
        {
            Assert.Null(InputPin.Parse(bad, out var problem));
            Assert.Contains("--input", problem);
        }
    }

    [Fact]
    public void A_malformed_input_on_a_deploy_is_a_usage_error_before_anything_is_read()
    {
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["connection", "deploy", "--write-plan", "--input", "days", "--project", TestSupport.NewProjectDir()], o, e));
        Assert.Contains("--input is `[model.]name=value`", e.ToString());
    }

    [Fact]
    public void The_code_says_what_moved_and_how_to_continue()
    {
        var d = DiagnosticCatalog.InputsMoved;
        Assert.Equal("DDB-454", d.Code);
        Assert.Contains("--yes", d.Explanation);
        Assert.Contains("--input", d.Fix);
    }
}
