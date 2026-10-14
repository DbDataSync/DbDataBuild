using DbDataBuild.Cli;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`sample` and `test` run a model in DuckDB the way its connection compares strings: the profile's collation for case and accent, `rtrim()` where trailing spaces are ignored.</summary>
public class StringEmulationTests
{
    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    // the engine each profile is true of: SQL Server ignores trailing spaces, PostgreSQL keeps them
    private static string Config(string @case, string trailing)
    {
        var sqlserver = trailing == "ignored";
        var collation = sqlserver ? (@case == "insensitive" ? "Latin1_General_100_CI_AS" : "Latin1_General_100_BIN2") : (@case == "insensitive" ? "und-u-ks-level2" : "C");
        var duck = @case == "insensitive" ? "NOCASE" : "NFC";
        return $"defaults: {{connections: [{(sqlserver ? "sqlserver" : "postgres")}]}}\ntracking: none\nstring_semantics:\n  case: {@case}\n  accent: sensitive\n  trailing_space: {trailing}\n  collations:\n    default: {{ duckdb: {duck}, {(sqlserver ? "sqlserver" : "postgres")}: \"{collation}\" }}\n";
    }

    private static string Project(string @case, string trailing, int groups, long joined)
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", Config(@case, trailing));
        Write(dir, "models/src/codes.yml", "name: src.codes\nkind: mapped\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: code, type: \"VARCHAR(10)\", nullable: false}\n");
        const string cols = "grain: [n]\ncolumns:\n  - {name: n, type: BIGINT, nullable: false}\n";
        Write(dir, "models/marts/groups.yml", "name: marts.groups\nkind: full\n" + cols);
        Write(dir, "models/marts/groups.sql", "SELECT count(*) AS n FROM (SELECT code FROM src.codes GROUP BY code) g\n");
        Write(dir, "models/marts/joined.yml", "name: marts.joined\nkind: full\n" + cols);
        Write(dir, "models/marts/joined.sql", "SELECT count(*) AS n FROM src.codes a JOIN src.codes b ON a.code = b.code\n");
        Write(dir, "models/marts/filtered.yml", "name: marts.filtered\nkind: full\n" + cols);
        Write(dir, "models/marts/filtered.sql", "SELECT count(*) AS n FROM src.codes WHERE code = 'a' AND code IN ('a', 'q')\n");
        const string given = "    given:\n      src.codes:\n        - {id: 1, code: \"a\"}\n        - {id: 2, code: \"A\"}\n        - {id: 3, code: \"a \"}\n";
        Write(dir, "tests/models/marts/groups.yml", $"model: marts.groups\ncases:\n  - name: groups\n{given}    expect: [{{n: {groups}}}]\n");
        Write(dir, "tests/models/marts/joined.yml", $"model: marts.joined\ncases:\n  - name: joined\n{given}    expect: [{{n: {joined}}}]\n");
        // how many rows are `a`, written as the filter: the case and the space decide
        var filtered = (@case == "insensitive" ? 2 : 1) + (trailing == "ignored" ? 1 : 0);
        Write(dir, "tests/models/marts/filtered.yml", $"model: marts.filtered\ncases:\n  - name: filtered\n{given}    expect: [{{n: {filtered}}}]\n");
        return dir;
    }

    private static (int Exit, string Text) Test(string dir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "tests", "run", "--project", dir], o, e, environment: _ => null);
        return (exit, o + "\n" + e);
    }

    [Theory]
    //          case          trailing       groups of a, A, "a "  rows of a self-join on them
    [InlineData("sensitive",   "significant", 3, 3)]      // all three differ: three groups, each joins itself
    [InlineData("insensitive", "significant", 2, 5)]      // a and A are one: two groups; 2x2 + 1
    [InlineData("sensitive",   "ignored",     2, 5)]      // a and "a " are one: two groups; 2x2 + 1
    [InlineData("insensitive", "ignored",     1, 9)]      // all one: one group, 3x3
    public void A_model_is_run_the_way_its_connection_compares_strings(string @case, string trailing, int groups, long joined)
    {
        var (exit, text) = Test(Project(@case, trailing, groups, joined));
        Assert.True(exit == 0, text);
    }

    [Fact]
    public void Expecting_the_binary_answer_fails_under_a_profile_that_ignores_case_and_trailing_spaces()
    {
        var (exit, text) = Test(Project("insensitive", "ignored", groups: 3, joined: 3));
        Assert.NotEqual(0, exit);
        Assert.Contains("fail", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sample_says_how_strings_were_compared()
    {
        var dir = Project("insensitive", "ignored", 1, 9);
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(0, CliApp.Run(["project", "sample", "--project", dir, "--rows", "5", "marts.groups"], o, e, environment: _ => null));
        Assert.Contains("strings compare as on `sqlserver` (case=insensitive, accent=sensitive, trailing_space=ignored): collation NOCASE; trailing spaces ignored (rtrim in comparisons", o.ToString() + e.ToString());
    }
}
