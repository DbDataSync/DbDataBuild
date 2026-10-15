using DbDataBuild.State;
using DbDataBuild.Targets;

namespace DbDataBuild.Tests.Unit;

public class TrackingDdlTests
{
    public static TheoryData<string> AllTargets => new() { "sqlserver", "fabric", "postgres" };

    [Theory, MemberData(nameof(AllTargets))]
    public void Script_creates_the_schema_every_table_and_the_version_row(string target)
    {
        var script = TrackingDdl.For(target).InitScript("dbdatabuild", "0.1.0");
        var views = target == "fabric" ? 1 : 2;                                  // metadata_current, and metadata_columns where OPENJSON is verified
        Assert.Equal(1 + TrackingSchema.Tables.Count + views + 1, script.Count);   // schema, tables, views, the version row
        Assert.Equal(script.Count, script.Select(s => s.Id).Distinct().Count());
        var text = TrackingDdl.Render(script);
        foreach (var t in TrackingSchema.Tables) Assert.Contains(t.Name, text);
        Assert.Contains("0.1.0", text);
    }

    [Theory, MemberData(nameof(AllTargets))]
    public void Every_statement_is_idempotent_and_never_drops_or_alters(string target)
    {
        foreach (var s in TrackingDdl.For(target).InitScript("dbdatabuild", "0.1.0"))
        {
            Assert.DoesNotContain("DROP ", s.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER ", s.Text.Replace("CREATE OR ALTER VIEW", ""), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TRUNCATE", s.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("(IF |CREATE SCHEMA IF NOT EXISTS|NOT EXISTS|CREATE OR ALTER VIEW|CREATE OR REPLACE VIEW)", s.Text);
        }
    }

    [Theory, InlineData("sqlserver"), InlineData("fabric")]
    public void T_sql_statements_parse_with_the_offline_validator(string target)
    {
        var t = TargetRegistry.Get(target);
        foreach (var s in TrackingDdl.For(target).InitScript("dbdatabuild", "0.1.0"))
            Assert.Empty(t.Validate(s.Text, s.Id));
    }

    [Fact]
    public void Postgres_statements_parse_with_the_offline_validator()
    {
        var t = TargetRegistry.Get("postgres");
        foreach (var s in TrackingDdl.For("postgres").InitScript("dbdatabuild", "0.1.0"))
            if (!s.Text.StartsWith("DO $$", StringComparison.Ordinal))      // the one DO block (the view that has `model_kind` only when the table does) is run on PostgreSQL by the conformance tests, not parsed here
                Assert.Empty(t.Validate(s.Text, s.Id));
    }

    [Fact]
    public void A_schema_name_with_quote_characters_is_quoted_not_interpolated()
    {
        var tsql = TrackingDdl.Render(TrackingDdl.For("sqlserver").InitScript("a]b'c", "0.1.0"));
        Assert.Contains("[a]]b'c]", tsql);
        Assert.Contains("SCHEMA_ID('a]b''c')", tsql); // the literal doubles the quote
        var pg = TrackingDdl.Render(TrackingDdl.For("postgres").InitScript("a\"b", "0.1.0"));
        Assert.Contains("\"a\"\"b\"", pg);
    }

    [Fact]
    public void Only_fabric_is_marked_unverified()
    {
        Assert.False(TrackingDdl.For("sqlserver").Unverified);
        Assert.False(TrackingDdl.For("postgres").Unverified);
        Assert.True(TrackingDdl.For("fabric").Unverified);
    }

    [Fact]
    public void Every_table_has_a_key_made_of_its_own_non_null_columns()
    {
        foreach (var t in TrackingSchema.Tables)
        {
            Assert.NotEmpty(t.PrimaryKey);
            foreach (var k in t.PrimaryKey) Assert.False(t.Columns.Single(c => c.Name == k).Nullable, $"{t.Name}.{k}");
        }
    }

    [Theory, MemberData(nameof(AllTargets))]
    public void The_metadata_table_holds_json_and_the_views_read_it(string target)
    {
        var text = TrackingDdl.Render(TrackingDdl.For(target).InitScript("dbdatabuild", "0.1.0"));
        Assert.Contains("metadata_document", text);
        Assert.Contains("metadata_current", text);
        Assert.Equal(target != "fabric", text.Contains("VIEW [dbdatabuild].[metadata_columns]") || text.Contains("VIEW \"dbdatabuild\".\"metadata_columns\""));
        if (target == "postgres") Assert.Contains("\"document\" jsonb NOT NULL", text);
        if (target == "sqlserver") Assert.Contains("CHECK (ISJSON([document]) = 1)", text);          // SQL Server holds JSON as text, so the engine checks it
        Assert.Equal(9, TrackingSchema.Version);
        Assert.Contains("[version], [tool_version]", TrackingDdl.Render(TrackingDdl.For("sqlserver").InitScript("d", "x")));
    }
}
