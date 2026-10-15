using DbDataBuild.Cli;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>DDB-236: a model that compares strings on connections that compare them differently, and DDB-237's declaration, `trimmed`.</summary>
public class StringProfileLintTests
{
    private const string Config = """
        defaults: {connections: [sqlserver, postgres]}
        tracking: none
        connections:
          postgres:
            string_semantics: { trailing_space: significant }
        string_semantics:
          case: insensitive
          accent: sensitive
          trailing_space: ignored
          collations:
            default: { duckdb: NOCASE, sqlserver: Latin1_General_100_CI_AS, postgres: "und-u-ks-level2" }
        """;

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Project(string sql, string config = Config, bool trimmed = false, string extra = "")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config);
        var t = trimmed ? ", trimmed: true" : "";
        foreach (var table in new[] { "a", "b" })
            Write(dir, $"models/src/{table}.yml", $"name: src.{table}\nkind: mapped\ncolumns:\n  - {{name: code, type: \"VARCHAR(10)\", nullable: false{t}}}\n  - {{name: label, type: \"VARCHAR(10)\", nullable: false}}\n  - {{name: n, type: INTEGER, nullable: false}}\n");
        Write(dir, "models/marts/m.yml", $"name: marts.m\nkind: full\n{extra}columns:\n  - {{name: code, type: \"VARCHAR(10)\", nullable: false}}\n  - {{name: n, type: INTEGER, nullable: false}}\n");
        Write(dir, "models/marts/m.sql", sql + "\n");
        return dir;
    }

    private static string Validate(string dir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null);
        return o.ToString() + e.ToString();
    }

    private const string Join = "SELECT a.code, a.n FROM src.a a JOIN src.b b ON a.code = b.code";

    [Fact]
    public void A_join_on_strings_is_flagged_when_the_connections_disagree_and_not_when_they_agree()
    {
        var text = Validate(Project(Join));
        Assert.Contains("warning DDB-236", text);
        Assert.Contains("`src.a.code` in a join", text);
        Assert.Contains("trailing_space: sqlserver ignored, postgres significant", text);
        Assert.DoesNotContain("DDB-236", Validate(Project(Join, Config.Replace("    string_semantics: { trailing_space: significant }\n", "    engine: postgres\n").Replace("postgres:\n    engine", "postgres:\n    engine"))));
    }

    [Fact]
    public void A_column_declared_trimmed_is_not_an_issue_when_only_trailing_spaces_differ()
    {
        Assert.DoesNotContain("DDB-236", Validate(Project(Join, trimmed: true)));
        // the same columns, but the connections also differ on case: trimming does not help
        var cases = Config.Replace("    string_semantics: { trailing_space: significant }", "    string_semantics: { trailing_space: significant, case: sensitive, collations: { default: { postgres: C } } }");
        Assert.Contains("DDB-236", Validate(Project(Join, cases, trimmed: true)));
    }

    [Fact]
    public void A_model_that_does_not_compare_strings_is_not_flagged_and_ordering_is_not_a_trailing_space_issue()
    {
        Assert.DoesNotContain("DDB-236", Validate(Project("SELECT a.code, a.n FROM src.a a WHERE a.n > 3")));         // an integer comparison
        Assert.DoesNotContain("DDB-236", Validate(Project("SELECT a.code, a.n FROM src.a a ORDER BY a.code")));       // trailing spaces do not decide an order
        var order = Config.Replace("    string_semantics: { trailing_space: significant }", "    string_semantics: { trailing_space: significant, case: sensitive, collations: { default: { postgres: C } } }");
        Assert.Contains("`src.a.code` in an ORDER BY", Validate(Project("SELECT a.code, a.n FROM src.a a ORDER BY a.code", order)));
    }

    [Fact]
    public void Distinct_group_by_and_a_filter_count_and_the_advice_can_be_silenced_on_the_model()
    {
        Assert.Contains("a DISTINCT", Validate(Project("SELECT DISTINCT a.code, a.n FROM src.a a")));
        Assert.Contains("a GROUP BY", Validate(Project("SELECT a.code, count(*) AS n FROM src.a a GROUP BY a.code")));
        Assert.Contains("a filter", Validate(Project("SELECT a.code, a.n FROM src.a a WHERE a.label = 'x'")));
        Assert.DoesNotContain("DDB-236", Validate(Project(Join, extra: "lint_ignore: [DDB-236]\n")));
    }

    [Fact]
    public void A_column_says_it_is_trimmed_and_the_file_it_is_written_to_keeps_that()
    {
        var dir = Project(Join, trimmed: true);
        var descriptor = ProjectValidator.Validate(dir).MappedModels.First(d => d.Name == "src.a");
        Assert.True(descriptor.Columns.First(c => c.Name == "code").Trimmed);
        Assert.False(descriptor.Columns.First(c => c.Name == "label").Trimmed);
        Assert.Contains("    trimmed: true\n", DeclaredModelWriter.Yaml(descriptor));
        Write(dir, "models/src/a.yml", "name: src.a\nkind: mapped\ncolumns:\n  - {name: code, type: \"VARCHAR(10)\", trimmed: maybe}\n");
        Assert.Contains("trimmed is `maybe`", Validate(dir));
    }
}
