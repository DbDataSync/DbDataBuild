using DbDataBuild.Cli;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// A driver exception that no command handles must reach the person as the scrubbed internal error (the exception's type), never as System.CommandLine's default "Unhandled exception: ..." with the driver's own
/// message and a stack trace: a driver message can hold a server name, a login or a value (DESIGN.md 14.2).
/// </summary>
public class UnhandledDriverErrorTests
{
    [Fact]
    public void An_unreachable_server_under_a_command_that_does_not_catch_it_is_reported_without_the_driver_text()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        var env = new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_READ"] = "Server=secret-host.invalid,1;User Id=r;Password=hunter2;Connect Timeout=1" };
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["connection", "compare", "staging.orders", "--against-schema", "dev", "--project", dir], o, e, environment: v => env.GetValueOrDefault(v));
        var all = o.ToString() + e.ToString();
        Assert.Equal(CliApp.ExitFindings, exit);                                  // the database, not the tool, is what failed
        Assert.Contains("DDB-242", all);
        Assert.DoesNotContain("DDB-900", all);
        Assert.Contains("The database reported", all);
        Assert.Matches(@"SqlException \(error \d+\)", all);                 // the error number says what happened; the driver's message does not appear
        foreach (var leak in new[] { "secret-host", "hunter2", "Unhandled exception", "network-related", "   at " })
            Assert.DoesNotContain(leak, all);
    }

    [Fact]
    public void In_json_mode_the_database_error_is_one_document_with_the_diagnostic_and_no_driver_text()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        var env = new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_READ"] = "Server=secret-host.invalid,1;User Id=r;Password=hunter2;Connect Timeout=1" };
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["connection", "compare", "staging.orders", "--against-schema", "dev", "--project", dir, "--format", "json"], o, e, environment: v => env.GetValueOrDefault(v));
        var doc = System.Text.Json.Nodes.JsonNode.Parse(o.ToString())!;
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Equal("DDB-242", (string?)doc["diagnostics"]![0]!["code"]);
        Assert.False((bool)doc["ok"]!);
        Assert.DoesNotContain("hunter2", o.ToString() + e);
        Assert.DoesNotContain("secret-host", o.ToString() + e);
    }
}
