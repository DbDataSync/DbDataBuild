using DbDataBuild.Cli;
using DbDataBuild.Core;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`connection refresh` up to the point where a connection is needed: the compiled plan it reads, the files it checks against their hashes, and the options it takes. DDB-446, DDB-447 and DDB-448.</summary>
public class RefreshTests
{
    private const string Config = "defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n";
    private const string Orders = "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctYaml = "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\nindexes:\n  - {name: ux_fct_orders_order_id, columns: [order_id], unique: true}\n";
    private const string FctSql = "SELECT o.order_id, o.amount FROM staging.orders o\n";

    private static string Project(bool compiled = true)
    {
        var dir = NewProjectDir();
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Write("dbdatabuild.yml", Config);
        Write("models/staging/orders.yml", Orders);
        Write("models/marts/fct_orders.yml", FctYaml); Write("models/marts/fct_orders.sql", FctSql);
        if (compiled) Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        return dir;
    }

    // a login that points nowhere: if the command tried to connect it would fail differently from what these tests look for
    private static (int Exit, string Out, string Err) Refresh(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["connection", "refresh", .. args, "--project", dir], o, e, environment: v => v == "DBDATABUILD_SQLSERVER_READ" || v == "DBDATABUILD_SQLSERVER_WRITE" ? "Server=127.0.0.1,1;User Id=r;Password=x;Connect Timeout=1" : null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void A_project_that_was_never_compiled_has_no_refresh_plan_and_the_command_says_what_to_run()
    {
        var r = Refresh(Project(compiled: false));
        Assert.Equal(CliApp.ExitFindings, r.Exit);
        Assert.Contains("DDB-446", r.Err);
        Assert.Contains("Nothing was executed.", r.Out);
        Assert.Contains("Next: dbdatabuild project compile", r.Out);
    }

    [Theory]
    [InlineData("rendered/sqlserver/marts.fct_orders/load.default.sql", "load script")]
    [InlineData("rendered/sqlserver/marts.fct_orders/load.default.resolve.sql", "resolver")]
    public void A_script_that_is_not_the_one_the_plan_was_compiled_from_stops_the_refresh_before_it_connects(string file, string what)
    {
        var dir = Project();
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;                                         // this model's default operation has no resolver
        File.AppendAllText(path, "\n-- edited by hand\n");
        var r = Refresh(dir);
        Assert.Equal(CliApp.ExitFindings, r.Exit);
        Assert.Contains("DDB-448", r.Err);
        Assert.Contains(what, r.Err);
        Assert.DoesNotContain("could not connect", (r.Err + r.Out).ToLowerInvariant());
    }

    [Fact]
    public void A_script_that_is_gone_stops_the_refresh_too_and_an_edited_plan_is_refused()
    {
        var dir = Project();
        File.Delete(Path.Combine(dir, "rendered/sqlserver/marts.fct_orders/load.default.sql"));
        var gone = Refresh(dir);
        Assert.Equal(CliApp.ExitFindings, gone.Exit);
        Assert.Contains("DDB-448", gone.Err);
        Assert.Contains("does not exist", gone.Err);

        var dir2 = Project();
        var planFile = Path.Combine(dir2, "rendered/sqlserver/refresh/default.yml");
        File.WriteAllText(planFile, File.ReadAllText(planFile).Replace("marts.fct_orders", "marts.fct_ordersx"));
        var edited = Refresh(dir2);
        Assert.Equal(CliApp.ExitFindings, edited.Exit);
        Assert.Contains("DDB-435", edited.Err);                                 // the plan's own hash no longer matches
    }

    [Fact]
    public void The_options_are_checked_before_anything_is_read()
    {
        var dir = Project();
        Assert.NotEqual(CliApp.ExitOk, Refresh(dir, "--check", "always").Exit);                    // the parser refuses a level that is not one
        var none = Refresh(dir, "no.such.model");
        Assert.Equal(CliApp.ExitUsage, none.Exit);
        Assert.Contains("does not name a valid model", none.Err);                                  // a selector is the grammar of the other commands
        var excluded = Refresh(dir, "exclude:marts.fct_orders");
        Assert.Equal(CliApp.ExitUsage, excluded.Exit);
        Assert.Contains("No member of the plan `default` matches", excluded.Err);
        Assert.Contains("marts.fct_orders.default", excluded.Err);                                 // it names what the plan does hold
        var plan = Refresh(dir, "hourly");
        Assert.Equal(CliApp.ExitUsage, plan.Exit);
        Assert.Contains("DDB-450", plan.Err);
        Assert.Contains("default", plan.Err);                                                      // and the plans there are
    }

    [Fact]
    public void A_pattern_selects_loads_and_the_check_failure_text_names_the_remedy()
    {
        var dir = Project();
        // nothing listens at the login: with the default check (objects) the tracking tables are read first, and without tracking the check says so rather than guess
        var r = Refresh(dir, "marts.*");
        Assert.Equal(CliApp.ExitFindings, r.Exit);
        Assert.Contains("DDB-447", r.Err);
        Assert.Contains("nothing is tracked", r.Err);
        Assert.Contains("--check live", r.Err);
        Assert.Contains("Next: dbdatabuild connection deploy", r.Out);
    }
}
