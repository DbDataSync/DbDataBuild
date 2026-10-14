using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>SQL Server ignores trailing spaces in a comparison and PostgreSQL keeps them: a project that builds on both says how strings compare per connection, over the project's own setting.</summary>
public class StringSemanticsPerConnectionTests
{
    private static string Project(string config)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        Directory.CreateDirectory(Path.Combine(dir, "models", "marts"));
        File.WriteAllText(Path.Combine(dir, "models", "marts", "m.yml"), "name: marts.m\nkind: full\ncolumns:\n  - {name: code, type: \"VARCHAR(20)\", nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models", "marts", "m.sql"), "SELECT CAST('a' AS VARCHAR(20)) AS code\n");
        return dir;
    }

    private static string Validate(string dir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null);
        return o.ToString() + e.ToString();
    }

    private const string Both = "defaults: {connections: [sqlserver, postgres]}\ntracking: none\nstring_semantics:\n  case: insensitive\n  accent: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NOCASE, sqlserver: Latin1_General_100_CI_AS, postgres: \"und-u-ks-level2\" }\n";

    [Fact]
    public void One_profile_cannot_hold_on_both_engines_and_a_connection_can_say_its_own()
    {
        Assert.Contains("trailing_space is significant but the profile requires ignored", Validate(Project(Both)));        // the whole project's setting, PostgreSQL cannot ignore trailing spaces

        var perConnection = Both.Replace("defaults:", "connections:\n  postgres:\n    string_semantics: { trailing_space: significant }\ndefaults:");
        var text = Validate(Project(perConnection));
        Assert.DoesNotContain("DDB-310", text);
        Assert.Contains("string semantics: case=insensitive, accent=sensitive, trailing_space=ignored (on postgres: case=insensitive, accent=sensitive, trailing_space=significant)", text);   // not hidden
    }

    [Fact]
    public void A_connection_changes_only_what_it_says_and_its_collations_replace_the_projects_for_that_engine()
    {
        var dir = Project(Both.Replace("defaults:", "connections:\n  postgres:\n    string_semantics:\n      trailing_space: significant\n      collations:\n        default: { postgres: \"und-u-ks-level1\" }\ndefaults:"));
        var config = ProjectConfigLoader.LoadFromProject(dir, new List<Diagnostic>());
        var postgres = config.SemanticsOf("postgres");
        Assert.Equal((CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant), (postgres.Case, postgres.Accent, postgres.TrailingSpace));
        Assert.Equal("und-u-ks-level1", postgres.Collations["default"]["postgres"]);
        Assert.Equal("Latin1_General_100_CI_AS", postgres.Collations["default"]["sqlserver"]);                             // what the connection did not say is the project's
        Assert.Equal(TrailingSpace.Ignored, config.SemanticsOf("sqlserver").TrailingSpace);
        Assert.Equal("und-u-ks-level2", config.StringSemantics.Collations["default"]["postgres"]);                         // the project's own is not touched
        Assert.Same(config, config.ForConnection("sqlserver"));
        Assert.Equal(TrailingSpace.Significant, config.ForConnection("postgres").StringSemantics.TrailingSpace);
        Assert.Contains("DDB-310", Validate(dir));                                                                          // level1 ignores accents; the profile keeps them
    }

    [Fact]
    public void A_connections_string_semantics_is_checked_like_the_projects()
    {
        var text = Validate(Project(Both.Replace("defaults:", "connections:\n  postgres:\n    string_semantics: { trailing_space: sometimes, colour: red }\ndefaults:")));
        Assert.Contains("`trailing_space` is `sometimes`", text);
        Assert.Contains("colour", text);
    }
}
