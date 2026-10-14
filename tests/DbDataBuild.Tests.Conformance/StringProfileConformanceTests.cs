using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>`trimmed: true` on the real engines: `check` counts the rows whose value ends in a space, and says so without showing a value.</summary>
[Trait("Group", "apply")]
public class StringProfileConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Check_counts_the_values_that_end_in_a_space_in_a_column_declared_trimmed(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-trimmed-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        try
        {
            await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA IF NOT EXISTS src" : "IF SCHEMA_ID('src') IS NULL EXEC('CREATE SCHEMA src')");
            await engine.ExecAsync("DROP TABLE IF EXISTS src.codes");
            await engine.ExecAsync("CREATE TABLE src.codes (id bigint NOT NULL, code varchar(10) NOT NULL, label varchar(10) NOT NULL)");
            await engine.ExecAsync("INSERT INTO src.codes VALUES (1, 'a', 'x '), (2, 'b ', 'y'), (3, 'c  ', 'z')");
            Write("dbdatabuild.yml", $"defaults: {{connections: [{name}]}}\ntracking: none\n" + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : ""));
            Write("models/src/codes.yml", "name: src.codes\nkind: mapped\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: code, type: \"VARCHAR(10)\", nullable: false, trimmed: true}\n  - {name: label, type: \"VARCHAR(10)\", nullable: false}\n");
            Write("models/marts/m.yml", "name: marts.m\nkind: full\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
            Write("models/marts/m.sql", "SELECT id FROM src.codes\n");

            Assert.Equal(0, Cli("project", "compile").Exit);
            var broken = Cli("connection", "status", name);
            Assert.NotEqual(0, broken.Exit);
            Assert.Contains("DDB-237", broken.Err + broken.Out);
            Assert.Contains("`src.codes.code` is declared `trimmed: true`", broken.Err + broken.Out);
            Assert.Contains("2 row(s) hold a value that ends in a space", broken.Err + broken.Out);                  // the two rows of `code`; `label` is not declared, its row is not counted
            Assert.DoesNotContain("'b '", broken.Err + broken.Out);                                                    // a count, never a value

            await engine.ExecAsync(name == "postgres" ? "UPDATE src.codes SET code = rtrim(code)" : "UPDATE src.codes SET code = RTRIM(code)");
            var fixedUp = Cli("connection", "status", name);
            Assert.DoesNotContain("DDB-237", fixedUp.Err + fixedUp.Out);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    public static TheoryData<string, string, int, long, int> Profiles => new()
    {
        // engine, the collation its text column has, groups of a / A / "a " , rows of a self-join on them, rows equal to 'a'
        { "sqlserver", "Latin1_General_100_CI_AS", 1, 9, 3 },        // case-insensitive, trailing spaces ignored
        { "sqlserver", "Latin1_General_100_BIN2", 2, 5, 2 },         // case-sensitive, trailing spaces ignored
        { "postgres", "C", 3, 3, 1 },                                // case-sensitive, trailing spaces significant
    };

    /// <summary>The numbers the emulation gives in DuckDB (`StringEmulationTests`) are the numbers the engine gives for the same data and collation.</summary>
    [SkippableTheory, MemberData(nameof(Profiles))]
    public async Task The_engine_gives_the_answers_the_duckdb_emulation_gives(string name, string collation, int groups, long joined, int equalToA)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA IF NOT EXISTS src" : "IF SCHEMA_ID('src') IS NULL EXEC('CREATE SCHEMA src')");
        await engine.ExecAsync("DROP TABLE IF EXISTS src.codes");
        await engine.ExecAsync($"CREATE TABLE src.codes (id bigint NOT NULL, code varchar(10) COLLATE {(name == "postgres" ? $"\"{collation}\"" : collation)} NOT NULL)");
        await engine.ExecAsync("INSERT INTO src.codes VALUES (1, 'a'), (2, 'A'), (3, 'a ')");
        async Task<long> One(string sql) => long.Parse((await engine.RowsAsync(sql)).Single());
        Assert.Equal(groups, await One("SELECT CAST(COUNT(*) AS VARCHAR(20)) FROM (SELECT code FROM src.codes GROUP BY code) g"));
        Assert.Equal(joined, await One("SELECT CAST(COUNT(*) AS VARCHAR(20)) FROM src.codes a JOIN src.codes b ON a.code = b.code"));
        Assert.Equal(equalToA, await One("SELECT CAST(COUNT(*) AS VARCHAR(20)) FROM src.codes WHERE code = 'a' AND code IN ('a', 'q')"));
    }
}
