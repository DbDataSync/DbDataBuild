using DbDataBuild.Cli;
using DbDataBuild.Core;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class CliTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void Every_command_declares_an_effect_class_shown_in_help()
    {
        Assert.Equal(CommandSpecs.All.Count, CommandSpecs.All.Select(c => c.Name).Distinct().Count());
        foreach (var spec in CommandSpecs.All)
        {
            var (exit, help, _) = Run([.. spec.Name.Split(' '), "--help"]);
            Assert.Equal(0, exit);
            Assert.Contains(spec.Effect.Describe(), help);
        }
    }

    [Fact]
    public void Command_surface_matches_the_design_document()
    {
        string[] expected = ["project create", "project model create", "project model update", "project compile", "project tests run", "project tests list", "project sample", "project seed", "project import", "project update", "project show loads", "project show graph", "project show metadata", "project show plan", "project show refresh", "project agent-kit", "connection inspect", "connection init", "connection status", "connection deploy", "connection refresh", "connection monitor", "connection compare", "connection seed", "connection publish", "ui terminal", "ui web", "ui mcp", "help code", "help dialects", "help matrix"];
        Assert.Equal(expected.OrderBy(x => x), CommandSpecs.All.Select(c => c.Name).OrderBy(x => x));
    }

    [Fact]
    public void Target_commands_that_are_not_built_yet_refuse_and_do_nothing()
    {
        foreach (var spec in CommandSpecs.All.Where(c => !c.Implemented))
        {
            var (exit, _, err) = Run(spec.Name.Split(' '));
            Assert.Equal(CliApp.ExitNotImplemented, exit);
            Assert.Contains("not implemented yet", err);
        }
    }

    [Fact]
    public void Validate_prints_header_and_reports_ok()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel.Replace("connections: [sqlserver, fabric]", "connections: [sqlserver]"));
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1");
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\nlint:\n  indexes: false\n");
        var (exit, output, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(0, exit);
        Assert.Contains("effect: Repo files only", output);
        Assert.Contains("Config: dbdatabuild.yml", output);
        Assert.Contains("OK: 1 model(s) valid (1 incremental_by_unique_key).", output);
        Assert.Equal("", err);
    }

    [Fact]
    public void Validate_fails_with_formatted_diagnostics()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel.Replace("    unique_key: [order_id]\n", ""));
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1");
        var (exit, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(1, exit);
        Assert.Contains("error DDB-214  models/marts/fct_orders.yml:3", err);
    }

    private static string ProjectWith(string sql, string targets = "[sqlserver, fabric]", string? config = "defaults: {connections: [sqlserver]}\n")
    {
        var dir = NewProjectDir();
        if (config != null) File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/t.yml"), "name: staging.t\nkind: mapped\ncolumns:\n  - {name: a, type: INTEGER}\n  - {name: b, type: INTEGER}\n  - {name: s, type: VARCHAR(20)}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel.Replace("connections: [sqlserver, fabric]", $"connections: {targets}"));
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), sql);
        return dir;
    }

    [Fact]
    public void Validate_runs_the_matrix_linter_per_declared_target_and_fails_on_unsupported_constructs()
    {
        var dir = ProjectWith("SELECT a FROM staging.t WHERE REGEXP_MATCHES(s, 'a')", config: "connections:\n  sqlserver: { version: 16 }\n");
        var before = Snapshot(dir);
        var (exit, output, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("error DDB-301  rendered/lowered/marts.fct_orders/lowered.sql:", err);   // findings point at the lowered query that is checked
        Assert.Contains("sqlserver version 17 or later", err);
        Assert.Contains("warning DDB-304", err);                 // fabric: unverified, not an error
        Assert.Contains("DDB-317", err);                              // the model x target x operation pair is named
        Assert.Contains("FAILED: 2 error(s)", output);
        Assert.Equal(before, Snapshot(dir)); // read-only
    }

    [Fact]
    public void Validate_passes_with_warnings_and_notes_when_nothing_is_unsupported()
    {
        var dir = ProjectWith("SELECT a / b AS x FROM staging.t ORDER BY a", "[sqlserver]", "defaults: {connections: [sqlserver]}\nlint:\n  indexes: false\n");
        var (exit, output, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("warning DDB-302", err);
        Assert.Contains("note DDB-303", err);
        Assert.Matches(@"OK: 2 model\(s\) valid \(1 \w+, 1 mapped\)\. 1 warning\(s\), 1 note\(s\)\.", output);
    }

    [Fact]
    public void Validate_without_a_config_file_warns_and_prints_the_built_in_defaults()
    {
        var (exit, output, err) = Run("project", "compile", "--project", ProjectWith("SELECT 1 AS order_id", config: null));
        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("warning DDB-109", err);
        Assert.Contains("Config: built-in defaults", output);
        Assert.Contains("string semantics: case=insensitive, accent=sensitive, trailing_space=ignored", output);
    }

    [Fact]
    public void Validate_uses_default_connections_from_the_config_for_models_without_targets()
    {
        const string regexpSql = "SELECT s FROM staging.t WHERE REGEXP_MATCHES(s, 'a')";
        const string postgresConfig = "defaults: {connections: [postgres]}\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: en_US.utf8 }\n";
        var dir = ProjectWith(regexpSql, config: postgresConfig);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel.Replace("connections: [sqlserver, fabric]\n", ""));
        var (exit, output, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitOk, exit);                     // REGEXP_MATCHES is native on postgres
        Assert.Contains("default connections: postgres", output);
        Assert.DoesNotContain("DDB-301", err);

        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\nconnections:\n  sqlserver: { version: 16 }\n");
        Assert.Equal(CliApp.ExitFindings, Run("project", "compile", "--project", dir).Exit);
    }

    [Fact]
    public void Validate_reports_config_errors_with_file_and_position()
    {
        var (exit, _, err) = Run("project", "compile", "--project", ProjectWith("SELECT 1 AS order_id", config: "defaults: {connections: [oracle]}\n"));
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("error DDB-106  dbdatabuild.yml:1", err);
    }

    [Fact]
    public void Configured_target_version_resolves_min_version_rows()
    {
        const string regexp = "SELECT s FROM staging.t WHERE REGEXP_MATCHES(s, 'a')";
        var warn = Run("project", "compile", "--project", ProjectWith(regexp, "[sqlserver]", "defaults: {connections: [sqlserver]}\n"));
        Assert.Equal(CliApp.ExitOk, warn.Exit);
        Assert.Contains("no version is configured", warn.Err);

        var old = Run("project", "compile", "--project", ProjectWith(regexp, "[sqlserver]", "connections:\n  sqlserver: { version: 16 }\n"));
        Assert.Equal(CliApp.ExitFindings, old.Exit);
        Assert.Contains("needs sqlserver version 17 or later, but the project configures version 16", old.Err);

        var current = Run("project", "compile", "--project", ProjectWith(regexp, "[sqlserver]", "connections:\n  sqlserver: { version: 17 }\n"));
        Assert.Equal(CliApp.ExitOk, current.Exit);
        Assert.DoesNotContain("DDB-308", current.Err);
        Assert.DoesNotContain("DDB-301", current.Err);
    }

    [Fact]
    public void Policy_severity_can_turn_a_warning_into_a_failure_but_not_hide_a_finding()
    {
        const string sql = "SELECT a / b AS x FROM staging.t";
        var plain = Run("project", "compile", "--project", ProjectWith(sql, "[sqlserver]", "defaults: {connections: [sqlserver]}\n"));
        Assert.Equal(CliApp.ExitOk, plain.Exit);
        Assert.Contains("warning DDB-302", plain.Err);

        var strict = Run("project", "compile", "--project", ProjectWith(sql, "[sqlserver]", "policy:\n  severity:\n    approximated: error\n"));
        Assert.Equal(CliApp.ExitFindings, strict.Exit);
        Assert.Contains("error DDB-302", strict.Err);

        var relaxed = Run("project", "compile", "--project", ProjectWith(sql, "[sqlserver]", "policy:\n  severity:\n    approximated: note\n"));
        Assert.Equal(CliApp.ExitOk, relaxed.Exit);
        Assert.Contains("note DDB-302", relaxed.Err);                // still shown
    }

    [Fact]
    public void Validate_fails_when_a_configured_collation_contradicts_the_string_profile()
    {
        var cfg = "string_semantics:\n  collations:\n    default:\n      duckdb: NOCASE\n      sqlserver: Latin1_General_100_CS_AS\n";
        var (exit, output, err) = Run("project", "compile", "--project", ProjectWith("SELECT 1 AS order_id", "[sqlserver]", cfg));
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("error DDB-310  dbdatabuild.yml:5", err);
        Assert.Contains("case is sensitive but the profile requires insensitive", err);
        Assert.Contains("FAILED: 1 error(s)", output);
    }

    [Fact]
    public void Validate_reports_unparseable_sql()
    {
        var (exit, _, err) = Run("project", "compile", "--project", ProjectWith("SELEC FROM FROM ("));
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-306", err);
    }

    [Fact]
    public void Matrix_command_prints_every_row_with_notes_and_the_coverage_rule()
    {
        var (exit, output, _) = Run("help", "matrix");
        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("effect: Offline only", output);
        Assert.Contains("syntax.group_by_ordinal", output);
        Assert.Contains("approximated", output);
        Assert.Contains("regexp", output);
        Assert.Contains("(>= 17)", output);
        Assert.Contains("DDB-305", output);
    }

    [Fact]
    public void Explain_prints_long_form_and_rejects_unknown_codes()
    {
        var (exit, output, _) = Run("help", "code", "ddb-214");
        Assert.Equal(0, exit);
        Assert.Contains("Missing unique_key", output);

        var (exit2, _, err) = Run("help", "code", "DDB-000");
        Assert.Equal(CliApp.ExitUsage, exit2);
        Assert.Contains("Unknown diagnostic code", err);
    }

    [Fact]
    public void Internal_failures_are_reported_as_tool_bugs_without_stack_traces()
    {
        var err = new StringWriter();
        var exit = CliApp.Guarded(["project", "compile"], err, () => throw new InvalidOperationException("boom: secret-row-value"));
        Assert.Equal(CliApp.ExitInternal, exit);
        Assert.Contains("DDB-900", err.ToString());
        Assert.Contains("InvalidOperationException", err.ToString());
        Assert.DoesNotContain("   at ", err.ToString());
        Assert.DoesNotContain("secret-row-value", err.ToString()); // exception messages may carry data (DESIGN.md 14.2)
    }
}
