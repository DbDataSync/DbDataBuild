using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>What a Flight SQL server reports (<c>GetSqlInfo</c>) becomes a dialect, and the dialect is committed in <c>dialects.lock.yml</c> (docs/research/read-origins.md section 10). Pure: no connection.</summary>
public class DialectDiscoveryTests
{
    private static readonly IReadOnlyDictionary<string, DialectProfile> None = new Dictionary<string, DialectProfile>();

    [Theory]
    [InlineData("PostgreSQL", "\"", null, "postgres", "\"", IdentifierCase.Lower)]
    [InlineData("Microsoft SQL Server", "\"", null, "sqlserver", "\"", IdentifierCase.Preserve)]      // the quote the server reported wins over the built-in's brackets
    [InlineData("Microsoft SQL Server", "[", null, "sqlserver", "[", IdentifierCase.Preserve)]
    [InlineData("DuckDB", "\"", null, "duckdb", "\"", IdentifierCase.Preserve)]
    [InlineData("Microsoft Fabric", null, null, "fabric", "[", IdentifierCase.Preserve)]
    [InlineData("Oracle", "\"", "uppercase", "ansi", "\"", IdentifierCase.Upper)]
    [InlineData("ClickHouse", "`", null, "ansi", "`", IdentifierCase.Preserve)]
    [InlineData(null, null, null, "ansi", "\"", IdentifierCase.Preserve)]
    [InlineData("MySQL", "'", null, "ansi", "\"", IdentifierCase.Preserve)]                           // a quote the tool does not write is kept out, and said
    public void The_product_picks_the_dialect_and_the_reported_facts_correct_it(string? product, string? quote, string? idCase, string dialect, string open, IdentifierCase folded)
    {
        var chosen = DialectDiscovery.Choose(new ServerFacts(product, "1", quote, idCase), None);
        Assert.Equal(dialect, chosen.Dialect);
        Assert.Equal(open, chosen.Profile.QuoteOpen);
        Assert.Equal(folded, chosen.Profile.Case);
        Assert.Equal(ParameterStyle.Positional, chosen.Profile.Parameters);                           // nothing on Flight SQL binds by name
        Assert.NotEmpty(chosen.Reasons);
    }

    [Fact]
    public void A_product_named_like_a_dialect_of_the_project_is_that_dialect()
    {
        var own = DialectProfiles.Build("clickhouse", DialectProfiles.Ansi, null, "`", null, "SELECT 1 FROM system.one", null, null, null, null).Profile!;
        var chosen = DialectDiscovery.Choose(new ServerFacts("ClickHouse", "24", "`"), new Dictionary<string, DialectProfile> { ["clickhouse"] = own });
        Assert.Equal("clickhouse", chosen.Dialect);
        Assert.Equal("SELECT 1 FROM system.one", chosen.Profile.Ping);
        Assert.Contains("the project's dialect `clickhouse`", string.Join(' ', chosen.Reasons));
    }

    [Fact]
    public void The_hash_covers_what_the_committed_text_depends_on_and_nothing_else()
    {
        var a = DialectDiscovery.Choose(new ServerFacts("PostgreSQL", "16.4", "\""), None).Profile;
        Assert.Equal(DialectDiscovery.Hash(a), DialectDiscovery.Hash(DialectDiscovery.Choose(new ServerFacts("PostgreSQL", "17.0", "\"", ReadOnly: true), None).Profile));
        Assert.NotEqual(DialectDiscovery.Hash(a), DialectDiscovery.Hash(a with { QuoteOpen = "`", QuoteClose = "`" }));
        Assert.NotEqual(DialectDiscovery.Hash(a), DialectDiscovery.Hash(a with { Limit = LimitStyle.Top }));
        Assert.Equal(64, DialectDiscovery.Hash(a).Length);
    }

    private static DialectLockEntry Entry(string connection, ServerFacts facts) => DialectLockEntry.From(connection, facts, DialectDiscovery.Choose(facts, None));

    [Fact]
    public void The_file_is_deterministic_sorted_and_read_back_to_the_same_entries()
    {
        var entries = new[] { Entry("zeta", new ServerFacts("DuckDB", "1.5", "\"", "case_insensitive", "case_insensitive", false)), Entry("alpha", new ServerFacts("ClickHouse", "24", "`")) };
        var text = DialectLockFile.Render(entries);
        Assert.Equal(text, DialectLockFile.Render(entries.Reverse()));
        Assert.True(text.IndexOf("  alpha:", StringComparison.Ordinal) < text.IndexOf("  zeta:", StringComparison.Ordinal));
        Assert.DoesNotContain("\r", text);
        var diags = new List<Diagnostic>();
        var read = DialectLockFile.Parse(text, ProductInfo.DialectLockFile, diags, None)!;
        Assert.Empty(diags);
        Assert.Equal(["alpha", "zeta"], read.Select(e => e.Connection));
        Assert.True(entries.OrderBy(e => e.Connection).Zip(read).All(p => p.First.SameContentAs(p.Second)));
        Assert.Equal("{}", DialectLockFile.Render([]).Split('\n').Single(l => l.StartsWith("connections:")).Split(": ")[1]);
    }

    [Theory]
    [InlineData("format: 1\nconnections:\n  x:\n    dialect: postgres\n", "`profile` is missing")]
    [InlineData("format: 2\nconnections: {}\n", "`format` must be 1")]
    [InlineData("connections: {}\n", "`format` must be 1")]
    [InlineData("format: 1\nconnections: [a]\n", "`connections` must map")]
    [InlineData("format: 1\nsurprise: 1\nconnections: {}\n", "Unknown key `surprise`")]
    [InlineData("- a\n", "must be a mapping")]
    public void A_damaged_file_is_DDB_457_with_the_reason(string text, string reason)
    {
        var diags = new List<Diagnostic>();
        Assert.Null(DialectLockFile.Parse(text, ProductInfo.DialectLockFile, diags, None));
        Assert.All(diags, d => Assert.Equal("DDB-457", d.Code));
        Assert.Contains(diags, d => d.Found.Contains(reason));
    }

    [Fact]
    public void An_entry_edited_by_hand_does_not_match_its_hash_and_a_bad_value_is_refused()
    {
        var text = DialectLockFile.Render([Entry("p", new ServerFacts("PostgreSQL", "16", "\""))]);
        var edited = text.Replace("quote: '\"'", "quote: \"`\"").Replace("quote: \"\\\"\"", "quote: \"`\"");
        Assert.NotEqual(text, edited);
        var diags = new List<Diagnostic>();
        Assert.Null(DialectLockFile.Parse(edited, ProductInfo.DialectLockFile, diags, None));
        Assert.Contains(diags, d => d.Code == "DDB-457" && d.Found.Contains("edited by hand"));
        var bad = new List<Diagnostic>();
        Assert.Null(DialectLockFile.Parse(text.Replace("limit: limit", "limit: sideways"), ProductInfo.DialectLockFile, bad, None));
        Assert.Contains(bad, d => d.Code == "DDB-457" && d.Found.Contains("`limit` is `sideways`"));
        var unknown = new List<Diagnostic>();
        Assert.Null(DialectLockFile.Parse(text.Replace("dialect: postgres", "dialect: nowhere"), ProductInfo.DialectLockFile, unknown, None));
        Assert.Contains(unknown, d => d.Found.Contains("neither a built-in nor declared"));
    }

    [Fact]
    public void Missing_names_the_flightsql_connections_without_a_keyword_or_an_entry()
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.Load("connections:\n  a: { engine: readonly flightsql }\n  b: { engine: readonly flightsql postgres }\n  c: { engine: readonly odbc }\n  d: { engine: readonly flightsql }\n", "dbdatabuild.yml", diags)!;
        Assert.Empty(diags);
        Assert.Equal(["a", "d"], DialectLockFile.Missing(config, ["a", "b", "c", "d", "a", "nowhere"]));
        var committed = DialectLockFile.Apply(config, [Entry("a", new ServerFacts("PostgreSQL", "16", "\""))], diags);
        Assert.Equal(["d"], DialectLockFile.Missing(committed, ["a", "d"]));
        Assert.Equal("postgres", committed.DialectOf("a").Name);
        Assert.Equal("ansi", committed.DialectOf("d").Name);
        Assert.Empty(diags);
        var stale = new List<Diagnostic>();
        DialectLockFile.Apply(config, [Entry("b", new ServerFacts("PostgreSQL", "16", "\"")), Entry("gone", ServerFacts.Nothing)], stale);
        Assert.Equal(["DDB-459", "DDB-459"], stale.Select(d => d.Code));
    }

    [Fact]
    public void The_comparison_names_what_differs_and_ignores_a_new_version()
    {
        var committed = Entry("p", new ServerFacts("PostgreSQL", "16.4", "\""));
        var now = new ServerFacts("Microsoft SQL Server", "16.0", "[");
        var differences = DialectLockComparison.Differences(committed, DialectDiscovery.Choose(now, None), now);
        Assert.Contains("the dialect was `postgres` and is now `sqlserver`", differences);
        Assert.Contains(differences, d => d.StartsWith("the identifier quote was `\"` and is now `[]`"));
        Assert.Contains("the server was `PostgreSQL` and now says `Microsoft SQL Server`", differences);
        var upgraded = new ServerFacts("PostgreSQL", "17.1", "\"");
        Assert.Empty(DialectLockComparison.Differences(committed, DialectDiscovery.Choose(upgraded, None), upgraded));
    }
}
