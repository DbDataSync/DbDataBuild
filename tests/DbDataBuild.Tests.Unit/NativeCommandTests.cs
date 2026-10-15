using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Native commands: a call that returns rows (`EXEC proc`, `CALL proc()`), never read by a query, only run: as a copy origin, remote or local, on a connection that allows it.</summary>
public class NativeCommandTests
{
    private const string Cols = "grain: [n]\ncolumns:\n  - {name: n, type: INTEGER, nullable: false}\n";
    private const string Allowed = "defaults: {connections: [sqlserver]}\nconnections:\n  sqlserver: { allow_native_commands: true }\nparameters:\n  list: \"1,2\"\n";

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Project(string config = Allowed)
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config);
        return dir;
    }

    private static void Command(string dir, string text = "EXEC dbo.usp_nums @list = ${project.list}", string extra = "")
        => Write(dir, "models/erp/nums.yml", $"name: erp.nums\nkind:\n  native:\n    access: command\n    query: {text}\n{extra}{Cols}");

    private static string Diags(string dir) => string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Select(DiagnosticFormatter.Format));

    [Fact]
    public void A_command_is_declared_like_a_native_select_on_a_connection_that_allows_commands()
    {
        var dir = Project();
        Command(dir);
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, Diags(dir));
        var native = result.NativeModels.Single().Native!;
        Assert.Equal((NativeQuery.Command, "EXEC dbo.usp_nums @list = ${project.list}"), (native.Access, native.Text));
    }

    [Theory]
    [InlineData("EXEC dbo.p; DROP TABLE t", "contains a `;`")]
    [InlineData("INSERT INTO t VALUES (1)", "`EXEC procedure ...` or `CALL procedure(...)`")]
    [InlineData("UPDATE t SET a = 1", "`EXEC procedure ...`")]
    public void A_command_is_one_call_and_nothing_else(string text, string expected)
    {
        var dir = Project();
        Command(dir, text);
        Assert.Contains(expected, Diags(dir));
    }

    [Fact]
    public void A_connection_that_does_not_allow_commands_refuses_one_and_the_setting_is_read_and_checked()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n");
        Command(dir, "EXEC dbo.usp_nums @list = 'a'");
        Assert.Contains("the connection must allow it: `connections.sqlserver.allow_native_commands: true`", Diags(dir));
        var cfg = ProjectConfigLoader.Load(Allowed, "dbdatabuild.yml", [])!;
        Assert.True(cfg.Connections["sqlserver"].AllowNativeCommands);
        Assert.False(cfg.Connections["postgres"].AllowNativeCommands);
        var diags = new List<Diagnostic>();
        Assert.Null(ProjectConfigLoader.Load("connections:\n  sqlserver: { allow_native_commands: maybe }\n", "dbdatabuild.yml", diags));
        Assert.Contains(diags, d => d.Found.Contains("`true` or `false`"));
    }

    [Fact]
    public void A_query_cannot_read_a_command_not_even_on_its_own_connection()
    {
        var dir = Project();
        Command(dir);
        Write(dir, "models/marts/m.yml", "name: marts.m\nkind: full\n" + Cols);
        Write(dir, "models/marts/m.sql", "SELECT n FROM erp.nums\n");
        var o = new StringWriter(); var e = new StringWriter();
        Assert.NotEqual(0, CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null));
        Assert.Contains("a command can only be run, never read inside a query", e.ToString());
        Assert.Contains("Copy it", e.ToString());
    }

    [Fact]
    public void A_copy_of_a_command_is_always_a_transfer_even_on_the_commands_own_connection()
    {
        var dir = Project();
        Command(dir);
        Write(dir, "models/marts/nums.yml", "name: marts.nums\nkind:\n  copy:\n    from: erp.nums\n");        // the same connection: not a local copy
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, Diags(dir));
        Assert.False(result.BuiltModels.Single().Definition.LocalCopy);
        Assert.Contains(result.DeclaredModelsAndStaging, d => d.IsGenerated);                                          // it has a staging table
    }

    [Fact]
    public void An_incremental_copy_of_a_command_must_pass_the_bound_itself()
    {
        var dir = Project();
        Command(dir);
        Write(dir, "models/marts/nums.yml", "name: marts.nums\nkind:\n  copy:\n    from: erp.nums\n    unique_key: [n]\n    watermark: {column: n}\n");
        Assert.Contains("must use `@watermark`", Diags(dir));
        Command(dir, "EXEC dbo.usp_nums @since = @watermark");
        Assert.DoesNotContain("@watermark`", Diags(dir));
    }

    private static readonly ProjectConfig Config = ProjectConfig.Default;

    private static PlanInput Input(PlannedModel model) => new(
        "sqlserver", Config, [model], new Dictionary<string, DbDataBuild.State.ObjectShape>(), new HashSet<string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, string>(), new HashSet<string>(),
        new Dictionary<string, IReadOnlyList<RenderedLoad>> { [model.Definition.Name] = [new RenderedLoad("default", true, "-- load script", new string('a', 64), null, [], null)] },
        new Dictionary<string, ResolverOutcome>());

    [Fact]
    public void The_plan_runs_a_command_as_written_marks_it_risky_and_carries_its_bound_values()
    {
        var def = new ModelDefinition("dst.nums", ModelKinds.Copy, [], null, null, ["n"], null, [new ColumnDefinition("n", "INTEGER", false)], [], From: "erp.nums");
        var use = new NativeUse("erp.nums", "EXEC dbo.usp_nums @list = @p_native_erp_nums__project_list", [new QueryParameter("native", "erp_nums__project_list", "VARCHAR", "")], NativeQuery.Command);
        var values = new Dictionary<string, ParameterValue> { ["native.erp_nums__project_list"] = new("1,2") };
        var model = new PlannedModel(def, "SELECT 1", "models/dst/nums.yml", "h", [], Origins: [new CopyOrigin("sqlserver", "sqlserver", "erp.nums", Native: use, NativeValues: values)]);
        var transfer = Planner.Plan(Input(model), []).Steps.Single(s => s.Type == StepType.Transfer);
        Assert.Equal(RiskClass.Risky, transfer.Risk);
        Assert.Contains("native command", transfer.Reasons[1]);
        Assert.True(transfer.Transfer!.Command);
        Assert.Equal("EXEC dbo.usp_nums @list = @p_native_erp_nums__project_list", transfer.Transfer.ReadText);   // not wrapped in a SELECT
        Assert.Equal(new PlanParameter("p_native_erp_nums__project_list", "VARCHAR", "parameter", "1,2"), Assert.Single(transfer.Transfer.Parameters!));

        var text = PlanDocument.Serialize(new Plan("2026-10-12-00000000", "sqlserver", null, false, "0.1.0", [], [], [transfer], []));
        Assert.True(PlanDocument.Parse(text, "p.yml", [])!.Steps.Single().Transfer!.Command);
    }
}
