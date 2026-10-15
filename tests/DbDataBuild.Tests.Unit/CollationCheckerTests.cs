using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Tests.Unit;

public class CollationCheckerTests
{
    // ---- the parser, as enumerated data ----

    public sealed record ParseCase(string Engine, string Name, CaseSensitivity? Case, AccentSensitivity? Accent, TrailingSpace? Trailing)
    {
        public override string ToString() => $"{Engine}: {Name}";
    }

    public static readonly ParseCase[] ParseCases =
    [
        new("sqlserver", "Latin1_General_100_CI_AS", CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored),
        new("sqlserver", "SQL_Latin1_General_CP1_CI_AS", CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored),
        new("sqlserver", "Latin1_General_100_CS_AS", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored),
        new("sqlserver", "Latin1_General_CI_AI", CaseSensitivity.Insensitive, AccentSensitivity.Insensitive, TrailingSpace.Ignored),
        new("sqlserver", "Latin1_General_CS_AI", CaseSensitivity.Sensitive, AccentSensitivity.Insensitive, TrailingSpace.Ignored),
        new("sqlserver", "Latin1_General_100_CI_AS_KS_WS_SC_UTF8", CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored),
        new("sqlserver", "Latin1_General_BIN2", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored),
        new("sqlserver", "Latin1_General_100_BIN2_UTF8", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored),
        new("sqlserver", "latin1_general_100_ci_as", CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored),   // names are case-insensitive
        new("sqlserver", "SomethingElse", null, null, TrailingSpace.Ignored),
        new("fabric", "Latin1_General_100_CI_AS_KS_WS_SC_UTF8", CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, null),   // trailing: [VERIFY]
        new("fabric", "Latin1_General_100_BIN2_UTF8", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, null),
        new("duckdb", "NOCASE", CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("duckdb", "NOACCENT", CaseSensitivity.Sensitive, AccentSensitivity.Insensitive, TrailingSpace.Significant),
        new("duckdb", "NOCASE.NOACCENT", CaseSensitivity.Insensitive, AccentSensitivity.Insensitive, TrailingSpace.Significant),
        new("duckdb", "noaccent.nocase", CaseSensitivity.Insensitive, AccentSensitivity.Insensitive, TrailingSpace.Significant),
        new("duckdb", "NFC", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("duckdb", "de", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("duckdb", "en_US", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("duckdb", "", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("duckdb", "NOCASE.WHATEVER", null, null, TrailingSpace.Significant),
        new("postgres", "en_US.utf8", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("postgres", "C", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("postgres", "en-x-icu", CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("postgres", "und-u-ks-level2", CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant),
        new("postgres", "und-u-ks-level1", CaseSensitivity.Insensitive, AccentSensitivity.Insensitive, TrailingSpace.Significant),
        new("postgres", "my_custom_collation", null, null, TrailingSpace.Significant),
        new("oracle", "BINARY_CI", null, null, null),
    ];

    public static TheoryData<ParseCase> ParseData
    {
        get { var d = new TheoryData<ParseCase>(); foreach (var c in ParseCases) d.Add(c); return d; }
    }

    [Theory, MemberData(nameof(ParseData))]
    public void Parser_reads_collation_names(ParseCase c)
    {
        var t = CollationTraitsParser.Parse(c.Engine, c.Name);
        Assert.Equal((c.Case, c.Accent, c.Trailing), (t.Case, t.Accent, t.Trailing));
    }

    // ---- the checker ----

    private static ProjectConfig Config(string yaml)
    {
        var diags = new List<Diagnostic>();
        var cfg = ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return cfg!;
    }

    private static BuiltModel Model(string targets, string columns = "  - {name: a, type: INT}")
    {
        var diags = new List<Diagnostic>();
        var yaml = $"name: marts.fct_orders\nkind: full\n{(targets == "" ? "" : $"connections: {targets}\n")}columns:\n{columns}\n";
        var def = ModelDefinitionLoader.Load(yaml, "models/marts/fct_orders.yml", "marts.fct_orders", diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return new BuiltModel(def!, "models/marts/fct_orders.yml", "models/marts/fct_orders.sql");
    }

    private static IReadOnlyList<Diagnostic> Check(ProjectConfig cfg, params BuiltModel[] models) => CollationChecker.Check(cfg, models);

    [Fact]
    public void Built_in_defaults_satisfy_the_profile_for_sqlserver_and_duckdb()
    {
        Assert.Empty(Check(ProjectConfig.Default, Model("[sqlserver]")).Select(DiagnosticFormatter.Format));
    }

    [Fact]
    public void Fabric_trailing_space_behavior_is_reported_as_unverifiable_not_assumed()
    {
        var d = Assert.Single(Check(ProjectConfig.Default, Model("[fabric]")));
        Assert.Equal("DDB-311", d.Code);
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("trailing_space", d.Found);
        Assert.DoesNotContain(Check(ProjectConfig.Default, Model("[fabric]")), x => x.Code == "DDB-310");
    }

    [Fact]
    public void A_collation_that_contradicts_the_profile_is_an_error_with_the_config_line()
    {
        var cfg = Config("string_semantics:\n  collations:\n    default:\n      duckdb: NOCASE\n      sqlserver: Latin1_General_100_CS_AS\n");
        var d = Assert.Single(Check(cfg, Model("[sqlserver]")));
        Assert.Equal("DDB-310", d.Code);
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Equal("dbdatabuild.yml", d.Location.File);
        Assert.Equal(5, d.Location.Line);
        Assert.Contains("case is sensitive but the profile requires insensitive", d.Found);
    }

    [Fact]
    public void Accent_mismatch_is_detected()
    {
        var cfg = Config("string_semantics:\n  collations:\n    default: { duckdb: NOCASE, sqlserver: Latin1_General_CI_AI }\n");
        Assert.Contains("accent is insensitive but the profile requires sensitive", Assert.Single(Check(cfg, Model("[sqlserver]"))).Found);
    }

    [Fact]
    public void Profile_can_be_changed_to_match_the_collation()
    {
        var cfg = Config("string_semantics:\n  case: sensitive\n  accent: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n");
        Assert.Empty(Check(cfg, Model("[sqlserver]")).Select(DiagnosticFormatter.Format));
    }

    [Fact]
    public void Duckdb_must_emulate_the_profile_so_it_needs_a_collation_that_ignores_case_when_required()
    {
        var cfg = Config("string_semantics:\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CI_AS }\n");
        var d = Assert.Single(Check(cfg, Model("[sqlserver]")));
        Assert.Equal("DDB-310", d.Code);
        Assert.Contains("duckdb", d.Found);
    }

    [Fact]
    public void Duckdb_trailing_spaces_are_met_by_the_rewrite_not_the_collation()
    {
        // ignored trailing spaces + NOCASE passes: DuckDB cannot do it natively, the offline rtrim() rewrite does.
        Assert.DoesNotContain(Check(ProjectConfig.Default, Model("[sqlserver]")), d => d.Found.Contains("trailing"));
    }

    [Fact]
    public void Postgres_needs_a_collation_and_cannot_ignore_trailing_spaces_natively()
    {
        var missing = Assert.Single(Check(ProjectConfig.Default, Model("[postgres]")));
        Assert.Equal("DDB-312", missing.Code);
        Assert.Contains("`postgres`", missing.Found);

        var cfg = Config("string_semantics:\n  collations:\n    default: { duckdb: NOCASE, postgres: en_US.utf8 }\n");
        var d = Assert.Single(Check(cfg, Model("[postgres]")));
        Assert.Equal("DDB-310", d.Code);
        Assert.Contains("case is sensitive", d.Found);
        Assert.Contains("trailing_space is significant", d.Found);

        var matching = Config("string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: en_US.utf8 }\n");
        Assert.Empty(Check(matching, Model("[postgres]")).Select(DiagnosticFormatter.Format));
    }

    [Fact]
    public void Unrecognized_collation_names_are_unverifiable_warnings()
    {
        var cfg = Config("string_semantics:\n  collations:\n    default: { duckdb: NOCASE, sqlserver: Weird_Name }\n");
        var d = Assert.Single(Check(cfg, Model("[sqlserver]")));
        Assert.Equal("DDB-311", d.Code);
        Assert.Contains("case (profile requires insensitive)", d.Found);
    }

    [Fact]
    public void Only_engines_in_use_are_checked()
    {
        // postgres is unconfigured, but no model targets it and it is not a default target
        Assert.Empty(Check(ProjectConfig.Default, Model("[sqlserver]")));
        Assert.Contains(Check(Config("defaults: {connections: [postgres]}\n"), Model("")), d => d.Code == "DDB-312");   // a model relies on the default
        Assert.Empty(Check(Config("defaults: {connections: [postgres]}\n"), Model("[sqlserver]")));                    // none does
        Assert.Contains(Check(Config("defaults: {connections: [postgres]}\n")), d => d.Code == "DDB-312");             // no models yet
    }

    [Fact]
    public void A_collations_section_without_default_is_an_error()
    {
        var d = Assert.Single(Check(Config("string_semantics:\n  collations:\n    legacy: { sqlserver: Latin1_General_CI_AS }\n"), Model("[sqlserver]")));
        Assert.Equal("DDB-312", d.Code);
        Assert.Contains("no `default` entry", d.Found);
    }

    [Fact]
    public void Column_collations_must_be_defined_for_every_engine_the_model_targets()
    {
        var cfg = Config("string_semantics:\n  collations:\n    default: { duckdb: NOCASE, sqlserver: Latin1_General_100_CI_AS, fabric: Latin1_General_100_CI_AS_KS_WS_SC_UTF8 }\n    legacy: { sqlserver: SQL_Latin1_General_CP1_CI_AS }\n");
        var unknown = Model("[sqlserver]", "  - {name: a, type: VARCHAR(10), collation: nope}");
        var partial = Model("[sqlserver, fabric]", "  - {name: a, type: VARCHAR(10), collation: legacy}");
        var ok = Model("[sqlserver]", "  - {name: a, type: VARCHAR(10), collation: default}");

        var d1 = Assert.Single(Check(cfg, unknown), x => x.Code == "DDB-312");
        Assert.Contains("`nope`", d1.Found);
        Assert.Equal("models/marts/fct_orders.yml", d1.Location.File);
        Assert.Equal(5, d1.Location.Line);

        var d2 = Check(cfg, partial).Where(x => x.Code == "DDB-312").ToList();
        Assert.Contains(d2, x => x.Found.Contains("`legacy`") && x.Found.Contains("`fabric`"));
        Assert.Contains(d2, x => x.Found.Contains("`legacy`") && x.Found.Contains("`duckdb`"));
        Assert.DoesNotContain(Check(cfg, ok), x => x.Code == "DDB-312");
    }

    [Fact]
    public void Only_the_default_collation_must_satisfy_the_profile()
    {
        // `legacy` is a declared exception (case-sensitive) and is not checked against the profile
        var cfg = Config("string_semantics:\n  collations:\n    default: { duckdb: NOCASE, sqlserver: Latin1_General_100_CI_AS }\n    exact: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n");
        var m = Model("[sqlserver]", "  - {name: a, type: VARCHAR(10), collation: exact}");
        Assert.Empty(Check(cfg, m).Select(DiagnosticFormatter.Format));
    }

    [Fact]
    public void Check_is_deterministic_and_reads_nothing_from_disk_or_a_target()
    {
        var cfg = Config("defaults: {connections: [sqlserver, postgres, fabric]}\n");
        Assert.Equal(Check(cfg, Model("")).Select(DiagnosticFormatter.Format), Check(cfg, Model("")).Select(DiagnosticFormatter.Format));
    }
}
