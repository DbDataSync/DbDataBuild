using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Tests.Unit;

public class LoadRendererTests
{
    internal static readonly string[] AllTargets = ["sqlserver", "fabric", "postgres"];

    private static LoadRenderer Renderer(ProjectConfig? config = null)
    {
        var matrix = MatrixLoader.LoadEmbedded([]);
        return new LoadRenderer(matrix, new MatrixLinter(matrix), config ?? ProjectConfig.Default);
    }

    internal static ModelDefinition Def(string yaml)
    {
        var diags = new List<Diagnostic>();
        var def = ModelDefinitionLoader.Load(yaml, "models/marts/m.yml", null, diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return def!;
    }

    private static RenderResult Render(ModelDefinition def, string sql, IReadOnlyList<string>? targets = null, ProjectConfig? config = null) =>
        Renderer(config).Render(def, sql, "models/marts/m.sql", targets ?? AllTargets);

    private static string Joined(RenderResult r, string target) =>
        string.Join("\n", r.Files.Where(f => f.Path.StartsWith(target + "/", StringComparison.Ordinal)).Select(f => $"===== {f.Path} =====\n{f.Content}"));

    // ---- the models ----

    internal const string FullYaml = "name: marts.dim_customer\nkind: full\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n  - {name: name, type: \"VARCHAR(50)\"}\n";
    internal const string FullSql = "SELECT c.customer_id, c.name FROM staging.customers c";

    internal const string UniqueYaml = "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: customer_id, type: BIGINT}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    internal const string UniqueSql = "WITH recent AS (SELECT * FROM staging.orders WHERE order_date >= DATE '2024-01-01')\nSELECT r.order_id, r.customer_id, r.amount FROM recent r ORDER BY r.order_id";

    internal const string TimeYaml = "name: marts.fct_daily\nkind: {incremental_by_time_range: {time_column: order_date, lookback: 3 days}}\ngrain: [order_date]\ncolumns:\n  - {name: order_date, type: DATE, nullable: false}\n  - {name: total, type: \"DECIMAL(18, 2)\"}\n";
    internal const string TimeSql = "SELECT o.order_date, SUM(o.amount) AS total FROM staging.orders o GROUP BY o.order_date";

    internal const string AllOpsYaml = """
        name: marts.fct_events
        kind: {incremental_by_time_range: {time_column: event_ts}}
        grain: [event_id]
        columns:
          - {name: event_id, type: BIGINT, nullable: false}
          - {name: event_ts, type: TIMESTAMP, nullable: false}
          - {name: seq, type: BIGINT, nullable: false}
          - {name: payload, type: "VARCHAR(100)"}
        loads:
          daily:
            default: true
            strategy: watermark_append
            watermark: {column: event_ts, resolver: target_max, lookback: 6 hours, on_null: initial, initial: "2020-01-01 00:00:00", overridable: true}
          tail:
            strategy: watermark_append
            watermark: {column: seq, resolver: target_max}
          reload_period:
            strategy: delete_insert_by_range
            params: {start: TIMESTAMP, end: TIMESTAMP}
            max_span: 400 days
          by_key:
            strategy: merge_by_key
            key: [event_id]
          replace_keys:
            strategy: delete_insert_by_key
            key: [event_id]
            connections: [postgres]
          everything:
            strategy: full_replace
        """;
    internal const string AllOpsSql = "SELECT e.event_id, e.event_ts, e.seq, e.payload FROM staging.events e";

    public static TheoryData<string, string, string, string> Cases => new()
    {
        { "full", FullYaml, FullSql, "sqlserver" }, { "full", FullYaml, FullSql, "fabric" }, { "full", FullYaml, FullSql, "postgres" },
        { "unique_key", UniqueYaml, UniqueSql, "sqlserver" }, { "unique_key", UniqueYaml, UniqueSql, "fabric" }, { "unique_key", UniqueYaml, UniqueSql, "postgres" },
        { "time_range", TimeYaml, TimeSql, "sqlserver" }, { "time_range", TimeYaml, TimeSql, "fabric" }, { "time_range", TimeYaml, TimeSql, "postgres" },
        { "all_operations", AllOpsYaml, AllOpsSql, "sqlserver" }, { "all_operations", AllOpsYaml, AllOpsSql, "fabric" }, { "all_operations", AllOpsYaml, AllOpsSql, "postgres" },
    };

    [Theory, MemberData(nameof(Cases))]
    public void Every_rendered_operation_and_resolver_matches_its_golden_file(string name, string yaml, string sql, string target)
    {
        var r = Render(Def(yaml), sql, [target]);
        Assert.Empty(r.Diagnostics.Where(d => d.Severity == Severity.Error).Select(DiagnosticFormatter.Format));
        GoldenFile.Assert($"render/{name}.{target}.txt", Joined(r, target));
    }

    // ---- invariants ----

    [Fact]
    public void Rendering_is_deterministic_and_contains_no_timestamps_or_machine_details()
    {
        var def = Def(AllOpsYaml);
        var first = Render(def, AllOpsSql);
        var second = Render(def, AllOpsSql);
        Assert.Equal(first.Files.Select(f => (f.Path, f.Content)), second.Files.Select(f => (f.Path, f.Content)));
        foreach (var f in first.Files)
        {
            Assert.DoesNotMatch(@"\b20\d\d-\d\d-\d\d[ T]\d\d:\d\d", f.Content.Replace("2020-01-01 00:00:00", ""));   // the committed initial literal is the only timestamp
            Assert.DoesNotContain(Environment.MachineName, f.Content);
            Assert.DoesNotContain(Environment.UserName, f.Content);
        }
    }

    [Fact]
    public void Every_script_and_resolver_starts_with_the_header_comment()
    {
        var r = Render(Def(AllOpsYaml), AllOpsSql);
        var hash = AstHasher.Hash(AllOpsSql).Hash!;
        foreach (var f in r.Files.Where(f => f.Path.EndsWith(".sql", StringComparison.Ordinal)))
        {
            var lines = f.Content.Split('\n').Take(9).ToList();
            Assert.StartsWith("-- dbdatabuild ", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("-- model:           marts.fct_events"));
            Assert.Contains(lines, l => l.StartsWith("-- operation:       "));
            Assert.Contains(lines, l => l.StartsWith("-- connection:      " + f.Path.Split('/')[0]));
            Assert.Contains(lines, l => l.StartsWith("-- strategy:        "));
            Assert.Contains(lines, l => l == "-- definition hash: " + hash);
            Assert.DoesNotContain(lines, l => l.Contains("version", StringComparison.OrdinalIgnoreCase) && !l.Contains("definition"));      // nothing that changes with a release: a file changes when its text does
            Assert.Contains(lines, l => l.StartsWith("-- parameters:      "));
        }
    }

    [Fact]
    public void Only_declared_value_parameters_appear_as_placeholders()
    {
        var r = Render(Def(AllOpsYaml), AllOpsSql);
        var expected = new Dictionary<string, string[]>
        {
            ["load.daily.sql"] = ["@watermark"], ["load.tail.sql"] = ["@watermark"], ["load.reload_period.sql"] = ["@start", "@end"],
            ["load.by_key.sql"] = [], ["load.everything.sql"] = [], ["load.replace_keys.sql"] = [],
        };
        foreach (var f in r.Files.Where(f => !f.Path.EndsWith("manifest.yml", StringComparison.Ordinal) && !f.Path.Contains(".resolve.")))
        {
            var found = Placeholders.Find(f.Content).Distinct().Order().ToArray();
            Assert.Equal(expected[Path.GetFileName(f.Path)].Order(), found);
        }
        foreach (var f in r.Files.Where(f => f.Path.Contains(".resolve.")))
            Assert.Empty(Placeholders.Find(f.Content));     // a resolver takes no parameters
    }

    [Fact]
    public void Every_script_is_one_transaction_that_the_script_itself_opens_and_closes()
    {
        var r = Render(Def(AllOpsYaml), AllOpsSql);
        foreach (var f in r.Files.Where(f => f.Path.EndsWith(".sql", StringComparison.Ordinal) && !f.Path.Contains(".resolve.")))
        {
            var code = string.Join("\n", f.Content.Split('\n').Where(l => !l.StartsWith("--")));
            var (open, close) = f.Path.StartsWith("postgres/", StringComparison.Ordinal) ? ("BEGIN;", "COMMIT;") : ("BEGIN TRANSACTION;", "COMMIT TRANSACTION;");
            Assert.Single(Regex.Matches(code, Regex.Escape(open)));
            Assert.True(code.TrimEnd().EndsWith(close), f.Path);
            Assert.True(code.IndexOf(open, StringComparison.Ordinal) < code.IndexOf("DELETE", StringComparison.Ordinal) || !code.Contains("DELETE"), f.Path);
            if (!f.Path.StartsWith("postgres/", StringComparison.Ordinal)) Assert.Contains("SET XACT_ABORT ON;", code);
        }
    }

    [Fact]
    public void The_body_is_staged_once_and_never_repeated()
    {
        var r = Render(Def(UniqueYaml), UniqueSql);
        foreach (var f in r.Files.Where(f => f.Path.EndsWith("load.default.sql", StringComparison.Ordinal)))
            Assert.Single(Regex.Matches(f.Content, "staging.orders", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void The_manifest_lists_operations_hashes_sources_and_matrix_status()
    {
        var r = Render(Def(AllOpsYaml), AllOpsSql, ["sqlserver"]);
        var manifest = r.Files.Single(f => f.Path.EndsWith("manifest.yml", StringComparison.Ordinal)).Content;
        Assert.Contains("model: marts.fct_events\nconnection: sqlserver\n", manifest);
        Assert.Contains("sources: [staging.events]", manifest);
        Assert.Contains("  - name: daily\n    default: true\n    strategy: watermark_append\n    transactional: true\n", manifest);
        Assert.Contains("    resolver: load.daily.resolve.sql\n", manifest);
        Assert.Contains("      - name: watermark\n        type: TIMESTAMP\n        source: resolver\n        constraint: overridable\n", manifest);
        Assert.Contains("        constraint: max_span 400 days\n", manifest);

        foreach (var f in r.Files.Where(f => f.Path.EndsWith(".sql", StringComparison.Ordinal)))
        {
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(f.Content))).ToLowerInvariant();
            Assert.Contains($"{Path.GetFileName(f.Path)}", manifest);
            Assert.Contains(sha, manifest);                                            // the hash of the committed text, so apply can verify it
        }
    }

    [Fact]
    public void Operations_with_targets_render_only_for_those_targets()
    {
        var r = Render(Def(AllOpsYaml), AllOpsSql);
        Assert.Contains(r.Files, f => f.Path == "postgres/marts.fct_events/load.replace_keys.sql");
        Assert.DoesNotContain(r.Files, f => f.Path == "sqlserver/marts.fct_events/load.replace_keys.sql");
        Assert.DoesNotContain(r.Files, f => f.Path == "fabric/marts.fct_events/load.replace_keys.sql");
        Assert.Equal(6, r.Operations.Count(o => o.Target == "postgres"));
        Assert.Equal(5, r.Operations.Count(o => o.Target == "sqlserver"));
    }

    [Fact]
    public void A_kind_supplies_its_single_default_operation_and_a_view_has_none()
    {
        Assert.Equal(["default:full_replace"], Render(Def(FullYaml), FullSql, ["sqlserver"]).Operations.Select(o => $"{o.Operation}:{o.Strategy}"));
        Assert.Equal(["default:delete_insert_by_key"], Render(Def(UniqueYaml), UniqueSql, ["postgres"]).Operations.Select(o => $"{o.Operation}:{o.Strategy}"));
        Assert.Equal(["default:watermark_append"], Render(Def(TimeYaml), TimeSql, ["sqlserver"]).Operations.Select(o => $"{o.Operation}:{o.Strategy}"));
        var view = Render(Def("name: marts.v\nkind: view\ncolumns:\n  - {name: a, type: INTEGER}\n"), "SELECT 1 AS a");
        Assert.Empty(view.Files);
        Assert.Empty(view.Operations);
        Assert.Empty(view.Diagnostics);
    }

    [Fact]
    public void Declared_loads_replace_the_implicit_default()
    {
        var r = Render(Def(AllOpsYaml), AllOpsSql, ["sqlserver"]);
        Assert.DoesNotContain(r.Operations, o => o.Operation == "default");
        Assert.Equal(["by_key", "daily", "everything", "reload_period", "tail"], r.Operations.Select(o => o.Operation).Order(StringComparer.Ordinal));
        Assert.Equal(["daily"], r.Operations.Where(o => o.IsDefault).Select(o => o.Operation));
    }

    // ---- unsupported pairs and diagnostics ----

    [Fact]
    public void A_pair_whose_query_uses_an_unsupported_construct_is_reported_by_name_and_renders_nothing()
    {
        var r = Render(Def(FullYaml), "SELECT COUNT(*) AS customer_id, c.name FROM staging.customers c GROUP BY 2", ["sqlserver", "postgres"]);
        var d = Assert.Single(r.Diagnostics, x => x.Code == "DDB-317");
        Assert.Contains("marts.dim_customer x sqlserver x default", d.Found);
        Assert.DoesNotContain(r.Files, f => f.Path.StartsWith("sqlserver/", StringComparison.Ordinal));
        Assert.Contains(r.Files, f => f.Path.StartsWith("postgres/", StringComparison.Ordinal));
        Assert.Equal("unsupported", r.Operations.Single(o => o.Target == "sqlserver").Status);
    }

    [Fact]
    public void A_construct_the_transpiler_rejects_is_a_named_pair_error_too()
    {
        var r = Render(Def(FullYaml), "SELECT c.customer_id, c.name FROM staging.customers c UNION ALL SELECT customer_id, name FROM staging.customers", ["sqlserver"]);
        Assert.Empty(r.Diagnostics.Where(d => d.Severity == Severity.Error));         // a plain UNION ALL is fine: no false positive
        var bad = Render(Def(FullYaml), "SELECT c.customer_id, c.name FROM staging.customers c LEFT JOIN staging.customers d USING (customer_id)", ["sqlserver"]);
        Assert.Contains(bad.Diagnostics, d => d.Code == "DDB-317");
    }

    [Fact]
    public void A_nullable_key_column_is_warned_about()
    {
        var yaml = UniqueYaml.Replace("{name: order_id, type: BIGINT, nullable: false}", "{name: order_id, type: BIGINT}");
        var r = Render(Def(yaml), UniqueSql, ["sqlserver"]);
        var d = Assert.Single(r.Diagnostics, x => x.Code == "DDB-320");
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("`order_id`", d.Found);
        Assert.NotEmpty(r.Files);                                                     // a warning does not stop rendering
    }

    [Fact]
    public void A_configured_engine_version_below_a_strategys_minimum_blocks_that_pair()
    {
        var def = Def(AllOpsYaml);
        var cfg = ProjectConfigLoader.Load("connections:\n  postgres: { version: 14 }\n", "dbdatabuild.yml", [])!;
        var r = Render(def, AllOpsSql, ["postgres"], cfg);
        var d = Assert.Single(r.Diagnostics, x => x.Code == "DDB-317");
        Assert.Contains("marts.fct_events x postgres x by_key", d.Found);
        Assert.Contains("needs postgres version 15", d.Found);
        Assert.DoesNotContain(r.Files, f => f.Path.EndsWith("load.by_key.sql", StringComparison.Ordinal));
        Assert.Contains(r.Files, f => f.Path.EndsWith("load.everything.sql", StringComparison.Ordinal));   // the other operations still render

        var ok = ProjectConfigLoader.Load("connections:\n  postgres: { version: 15 }\n", "dbdatabuild.yml", [])!;
        Assert.Empty(Render(def, AllOpsSql, ["postgres"], ok).Diagnostics.Where(x => x.Severity == Severity.Error));
    }

    [Fact]
    public void Status_is_the_worst_of_the_strategy_and_the_constructs()
    {
        var r = Render(Def(FullYaml), "SELECT c.customer_id, c.name FROM staging.customers c ORDER BY c.name", AllTargets);
        Assert.Equal("emulated", r.Operations.Single(o => o.Target == "sqlserver").Status);     // ORDER BY NULLS emulation; strategy is translated
        Assert.Equal("unverified", r.Operations.Single(o => o.Target == "fabric").Status);       // Fabric strategies are untested
        Assert.Equal("supported", r.Operations.Single(o => o.Target == "postgres").Status);
    }

    [Fact]
    public void A_body_that_does_not_parse_is_a_diagnostic()
    {
        var r = Render(Def(FullYaml), "SELEC FROM FROM (");
        Assert.Equal("DDB-306", Assert.Single(r.Diagnostics).Code);
        Assert.Empty(r.Files);
    }

    [Fact]
    public void An_unknown_target_name_is_a_bug_in_the_caller()
    {
        Assert.Throws<ArgumentException>(() => Render(Def(FullYaml), FullSql, ["oracle"]));
    }
}

public class AstHasherTests
{
    private static string Hash(string sql) => AstHasher.Hash(sql).Hash!;

    [Fact]
    public void Comments_whitespace_and_formatting_do_not_change_the_hash()
    {
        var plain = Hash("SELECT a, b FROM t WHERE a > 1");
        Assert.Equal(plain, Hash("SELECT   a,\n       b\nFROM t\nWHERE a > 1"));
        Assert.Equal(plain, Hash("-- a comment\nSELECT a, b /* inline */ FROM t WHERE a > 1 -- trailing"));
        Assert.Equal(plain, Hash("select a, b from t where a > 1"));
    }

    [Fact]
    public void Any_change_in_meaning_changes_the_hash()
    {
        var plain = Hash("SELECT a, b FROM t WHERE a > 1");
        foreach (var changed in new[] { "SELECT a, b FROM t WHERE a > 2", "SELECT a, b FROM t WHERE a >= 1", "SELECT b, a FROM t WHERE a > 1", "SELECT a, b FROM u WHERE a > 1", "SELECT a FROM t WHERE a > 1" })
            Assert.NotEqual(plain, Hash(changed));
    }

    [Fact]
    public void The_hash_is_a_stable_lowercase_sha256_and_unparseable_sql_is_an_error()
    {
        Assert.Matches("^[0-9a-f]{64}$", Hash("SELECT 1"));
        Assert.Equal(Hash("SELECT 1"), Hash("SELECT 1"));
        var (hash, error) = AstHasher.Hash("SELEC FROM FROM (");
        Assert.Null(hash);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
