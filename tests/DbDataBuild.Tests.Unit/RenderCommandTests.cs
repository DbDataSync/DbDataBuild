using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Targets;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class RenderCommandTests
{
    private const string Orders = "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: customer_id, type: BIGINT}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: order_date, type: DATE, nullable: false}\n";

    private const string FctYaml = "name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\ngrain: [order_id]\nconnections: [sqlserver, postgres]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctSql = "SELECT o.order_id, o.amount FROM staging.orders o\n";

    private static string Project(string? config = "defaults: {connections: [sqlserver]}\n")
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), Orders);
        if (config != null) File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        return dir;
    }

    private static void Model(string dir, string name, string yaml, string sql)
    {
        var stem = Path.Combine(dir, "models", name.Replace('.', '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllText(stem + ".yml", yaml);
        File.WriteAllText(stem + ".sql", sql);
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    /// <summary>
    /// The rendering half of `project compile`, called directly: these tests are about what is rendered, written and compared, with fixtures that are not meant to pass the project's other checks
    /// (`project compile` runs those too and fails on them). `--write` writes, `--check` compares, `--content` or nothing prints.
    /// </summary>
    private static (int Exit, string Out, string Err) Render(string dir, params string[] more)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var models = new List<string>();
        var connections = new List<string>();
        for (var i = 0; i < more.Length; i++)
            if (more[i] == "--connection") connections.Add(more[++i]);
            else if (!more[i].StartsWith("--", StringComparison.Ordinal)) models.Add(more[i]);
        var write = more.Contains("--write");
        var check = more.Contains("--check");
        var spec = CommandSpecs.All.First(c => c.Name == "project compile");
        var exit = RenderCommand.Render(spec, dir, [.. models], [.. connections], write, check, content: !write && !check, o, e);
        return (exit, o.ToString(), e.ToString());
    }

    private static string RenderedTree(string dir)
    {
        var root = Path.Combine(dir, "rendered");
        return Directory.Exists(root) ? string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/') + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))))) : "";
    }

    // ---------------- render (print) ----------------

    [Fact]
    public void Render_prints_every_file_by_default_and_writes_nothing()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        var before = Snapshot(dir);
        var (exit, output, err) = Render(dir);
        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("effect: Repo files only", output);
        Assert.Contains("===== rendered/sqlserver/marts.fct_orders/load.default.sql =====", output);
        Assert.Contains("===== rendered/postgres/marts.fct_orders/manifest.yml =====", output);
        Assert.Contains("DELETE FROM [marts].[fct_orders] WHERE EXISTS", output);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Render_uses_the_default_connections_for_models_without_targets_and_target_filters_them()
    {
        var dir = Project("defaults: {connections: [postgres]}\n");
        Model(dir, "marts.fct_orders", FctYaml.Replace("connections: [sqlserver, postgres]\n", ""), FctSql);
        Assert.DoesNotContain("rendered/sqlserver", Render(dir).Out);
        Assert.Contains("rendered/postgres/marts.fct_orders/load.default.sql", Render(dir).Out);

        var dir2 = Project();
        Model(dir2, "marts.fct_orders", FctYaml, FctSql);
        var only = Render(dir2, "--connection", "postgres").Out;
        Assert.DoesNotContain("rendered/sqlserver", only);
        Assert.Contains("rendered/postgres", only);
        Assert.Equal(CliApp.ExitUsage, Render(dir2, "--connection", "oracle").Exit);
    }

    [Fact]
    public void Render_can_select_models_by_name_file_or_directory_and_refuses_unknown_ones()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        Model(dir, "marts.dim_customer", "name: marts.dim_customer\nkind: full\nconnections: [sqlserver]\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n", "SELECT o.customer_id FROM staging.orders o");
        Assert.DoesNotContain("dim_customer", Render(dir, "marts.fct_orders").Out);
        Assert.DoesNotContain("fct_orders", Render(dir, "models/marts/dim_customer.sql").Out);
        Assert.DoesNotContain("fct_orders", Render(dir, "models/marts/dim_customer.yml").Out);
        Assert.Contains("fct_orders", Render(dir, "models/marts").Out);
        Assert.Contains("dim_customer", Render(dir, "models/marts").Out);
        var (exit, _, err) = Render(dir, "marts.nope");
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("does not name a valid model", err);
    }

    // ---------------- --write ----------------

    [Fact]
    public void Write_creates_the_rendered_tree_is_idempotent_and_leaves_models_alone()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        var models = Snapshot(Path.Combine(dir, "models"));

        var (exit, output, err) = Render(dir, "--write");
        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("wrote rendered/sqlserver/marts.fct_orders/load.default.sql", output);
        Assert.True(File.Exists(Path.Combine(dir, "rendered/postgres/marts.fct_orders/manifest.yml")));
        Assert.Equal(models, Snapshot(Path.Combine(dir, "models")));

        var tree = RenderedTree(dir);
        var (exit2, output2, _) = Render(dir, "--write");
        Assert.Equal(CliApp.ExitOk, exit2);
        Assert.Contains("already up to date", output2);
        Assert.DoesNotContain("wrote ", output2);
        Assert.Equal(tree, RenderedTree(dir));
    }

    [Fact]
    public void Written_files_are_utf8_without_bom_with_lf_line_endings_and_end_with_a_newline()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        Render(dir, "--write");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "rendered"), "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(f);
            Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), f);
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
        }
    }

    [Fact]
    public void Write_removes_stale_generated_files_only_and_keeps_anything_else()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        Render(dir, "--write");
        var modelDir = Path.Combine(dir, "rendered/sqlserver/marts.fct_orders");
        File.WriteAllText(Path.Combine(modelDir, "load.removed_op.sql"), "-- an operation that no longer exists\n");
        File.WriteAllText(Path.Combine(modelDir, "NOTES.md"), "hand-written\n");
        Directory.CreateDirectory(Path.Combine(dir, "rendered/sqlserver/marts.gone_model"));
        File.WriteAllText(Path.Combine(dir, "rendered/sqlserver/marts.gone_model/load.default.sql"), "-- model was deleted\n");
        File.WriteAllText(Path.Combine(dir, "rendered/sqlserver/marts.gone_model/manifest.yml"), "model: marts.gone_model\n");

        var (exit, output, _) = Render(dir, "--write");
        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("removed rendered/sqlserver/marts.fct_orders/load.removed_op.sql", output);
        Assert.Contains("removed rendered/sqlserver/marts.gone_model/load.default.sql", output);
        Assert.False(File.Exists(Path.Combine(modelDir, "load.removed_op.sql")));
        Assert.True(File.Exists(Path.Combine(modelDir, "NOTES.md")));                    // not ours
        Assert.False(Directory.Exists(Path.Combine(dir, "rendered/sqlserver/marts.gone_model")));
    }

    [Fact]
    public void Write_with_errors_writes_nothing()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\nconnections:\n  sqlserver: { version: 16 }\n");
        Model(dir, "marts.bad", "name: marts.bad\nkind: full\nconnections: [sqlserver]\ncolumns:\n  - {name: a, type: BIGINT}\n", "SELECT o.order_id AS a FROM staging.orders o WHERE REGEXP_MATCHES(CAST(o.order_id AS VARCHAR), '1')");
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        var before = Snapshot(dir);
        var (exit, output, err) = Render(dir, "--write");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-317", err);
        Assert.Contains("marts.bad x sqlserver x default", err);
        Assert.Contains("Nothing was written", output);
        Assert.Equal(before, Snapshot(dir));
    }

    // ---------------- --check ----------------

    [Fact]
    public void Check_passes_right_after_write_and_writes_nothing_itself()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        Render(dir, "--write");
        var before = Snapshot(dir);
        var (exit, output, err) = Render(dir, "--check");
        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("OK: ", output);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Check_fails_for_a_missing_file_a_changed_file_and_a_stale_file_without_touching_them()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        Render(dir, "--write");
        var modelDir = Path.Combine(dir, "rendered/sqlserver/marts.fct_orders");
        File.Delete(Path.Combine(dir, "rendered/postgres/marts.fct_orders/manifest.yml"));
        File.AppendAllText(Path.Combine(modelDir, "load.default.sql"), "-- hand edit\n");
        File.WriteAllText(Path.Combine(modelDir, "load.old.sql"), "-- stale\n");
        var before = Snapshot(dir);

        var (exit, output, err) = Render(dir, "--check");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-424", err);
        Assert.Contains("`rendered/postgres/marts.fct_orders/manifest.yml` is missing", err);
        Assert.Contains("`rendered/sqlserver/marts.fct_orders/load.default.sql` differs from a fresh render", err);
        Assert.Contains("`rendered/sqlserver/marts.fct_orders/load.old.sql` is committed but is no longer rendered", err);
        Assert.Contains("FAILED: 3 rendered file(s) out of date", output);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Check_fails_when_a_query_or_a_load_declaration_changes()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        Render(dir, "--write");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id, o.amount FROM staging.orders o WHERE o.amount > 0\n");
        Assert.Equal(CliApp.ExitFindings, Render(dir, "--check").Exit);

        Render(dir, "--write");
        Assert.Equal(CliApp.ExitOk, Render(dir, "--check").Exit);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), FctYaml + "loads:\n  merge:\n    default: true\n    strategy: merge_by_key\n");
        var (exit, _, err) = Render(dir, "--check");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("load.default.sql", err);                                      // the implicit operation is no longer rendered
        Assert.Contains("load.merge.sql", err);
    }

    [Fact]
    public void A_comment_or_formatting_change_in_the_query_changes_the_script_text_but_not_the_definition_hash()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml, FctSql);
        var a = Render(dir).Out;
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "-- explained\nSELECT   o.order_id,\n  o.amount\nFROM staging.orders o\n");
        var b = Render(dir).Out;
        var hashLine = "-- definition hash: ";
        Assert.Equal(a.Split('\n').First(l => l.StartsWith(hashLine)), b.Split('\n').First(l => l.StartsWith(hashLine)));
    }

    // ---------------- loads ----------------

    [Fact]
    public void Loads_prints_the_pairing_table_with_matrix_status()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml + "loads:\n  merge:\n    default: true\n    strategy: merge_by_key\n  rebuild:\n    strategy: full_replace\n    connections: [postgres]\n", FctSql);
        Model(dir, "marts.v_orders", "name: marts.v_orders\nkind: view\nconnections: [sqlserver]\ncolumns:\n  - {name: order_id, type: BIGINT}\n", "SELECT o.order_id FROM staging.orders o");
        var (exit, output, err) = Run("project", "show", "loads", "--project", dir);
        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("effect: Offline only", output);
        Assert.Matches(@"marts\.fct_orders\s+postgres\s+merge\s+merge_by_key\s+default\s+supported", output);
        Assert.Matches(@"marts\.fct_orders\s+postgres\s+rebuild\s+full_replace\s+supported", output);
        Assert.Matches(@"marts\.fct_orders\s+sqlserver\s+merge\s+merge_by_key\s+default\s+supported", output);
        Assert.DoesNotContain("sqlserver  rebuild", output);                               // restricted to postgres
        Assert.Matches(@"marts\.v_orders\s+sqlserver\s+-\s+-\s+-\s+view: DDL only", output);
        Assert.Contains("0 unsupported", output);
    }

    [Fact]
    public void Loads_shows_unsupported_pairs_and_fails()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\nconnections:\n  sqlserver: { version: 16 }\n");
        Model(dir, "marts.bad", "name: marts.bad\nkind: full\nconnections: [sqlserver, postgres]\ncolumns:\n  - {name: a, type: BIGINT}\n", "SELECT o.order_id AS a FROM staging.orders o WHERE REGEXP_MATCHES(CAST(o.order_id AS VARCHAR), '1')");
        var (exit, output, err) = Run("project", "show", "loads", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Matches(@"marts\.bad\s+sqlserver\s+default\s+full_replace\s+default\s+unsupported", output);
        Assert.Matches(@"marts\.bad\s+postgres\s+default\s+full_replace\s+default\s+supported", output);
        Assert.Contains("DDB-317", err);
        Assert.Contains("1 unsupported", output);
    }

    [Fact]
    public void Fabric_pairs_are_reported_unverified_because_no_fabric_engine_has_run_them()
    {
        var dir = Project();
        Model(dir, "marts.fct_orders", FctYaml.Replace("[sqlserver, postgres]", "[fabric]"), FctSql);
        Assert.Matches(@"marts\.fct_orders\s+fabric\s+default\s+delete_insert_by_key\s+default\s+unverified", Run("project", "show", "loads", "--project", dir).Out);
    }

    // ---------------- validate and the T-SQL grammar ----------------

    [Fact]
    public void Validate_reports_a_pair_that_cannot_render_by_name()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\nconnections:\n  sqlserver: { version: 16 }\n");
        Model(dir, "marts.bad", "name: marts.bad\nkind: full\nconnections: [sqlserver]\ncolumns:\n  - {name: a, type: BIGINT}\n", "SELECT o.order_id AS a FROM staging.orders o WHERE REGEXP_MATCHES(CAST(o.order_id AS VARCHAR), '1')");
        var (exit, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-317", err);
        Assert.Contains("marts.bad x sqlserver x default", err);
    }

    [Fact]
    public void The_t_sql_grammar_follows_the_configured_sql_server_version()
    {
        var target = new SqlServerTarget();
        const string script = "SELECT 1 WHERE REGEXP_LIKE('a', 'a');";
        Assert.NotEmpty(target.Validate(script, "x.sql", 16));        // 2022: REGEXP_LIKE is not a predicate
        Assert.Empty(target.Validate(script, "x.sql", 17));           // 2025
        Assert.Empty(target.Validate(script, "x.sql"));               // unknown version: the newest grammar, the matrix warns instead
        Assert.Empty(target.Validate("SELECT 1;", "x.sql", 14));
    }

    [Fact]
    public void Rendered_t_sql_with_parameters_passes_scriptdom()
    {
        // DESIGN.md 17: ScriptDOM handling of parameterized scripts
        var result = new SqlServerTarget().Validate("DECLARE @x INT = 1;\nSELECT 1 WHERE 1 >= @start AND 2 < @end;", "x.sql", 16);
        Assert.Empty(result);
        Assert.Empty(new SqlServerTarget().Validate("SELECT 1 WHERE 1 >= @start AND 2 < @end;", "x.sql", 16));
    }

    [Fact]
    public void Render_and_loads_never_reference_a_database_driver_and_declare_their_effect_classes()
    {
        Assert.Equal(EffectClass.RepoFilesOnly, CommandSpecs.All.Single(c => c.Name == "project compile").Effect);
        Assert.Equal(EffectClass.OfflineOnly, CommandSpecs.All.Single(c => c.Name == "project show loads").Effect);
        var forbidden = new[] { "Microsoft.Data.SqlClient", "System.Data.SqlClient", "Npgsql" };
        Assert.DoesNotContain(typeof(TargetRegistry).Assembly.GetReferencedAssemblies(), r => forbidden.Contains(r.Name));
    }
}
