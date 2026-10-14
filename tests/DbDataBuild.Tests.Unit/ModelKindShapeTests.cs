using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The kind of a model is a key named for the kind with its settings beneath it (`kind: {copy: {from: x}}`), a plain value for a kind with none (`kind: view`); the old form still reads, with a warning.</summary>
public class ModelKindShapeTests
{
    private const string Cols = "columns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: ordered_on, type: DATE, nullable: false}\n";

    private static (ModelDefinition? Def, List<Diagnostic> Diags) LoadWith(string kind, string rest = "") =>
        Load($"name: marts.fct_orders\n{kind}\n{rest}{(rest.Contains("columns") ? "" : Cols)}");

    [Theory]
    [InlineData("kind: view", ModelKinds.View)]
    [InlineData("kind: full", ModelKinds.Full)]
    [InlineData("kind: {full: {}}", ModelKinds.Full)]
    [InlineData("kind:\n  full:", ModelKinds.Full)]
    [InlineData("kind: {incremental_by_unique_key: {unique_key: [order_id]}}", ModelKinds.IncrementalByUniqueKey)]
    [InlineData("kind:\n  incremental_by_unique_key:\n    unique_key: [order_id]", ModelKinds.IncrementalByUniqueKey)]
    [InlineData("kind: {incremental_by_time_range: {time_column: ordered_on, lookback: 3 days}}", ModelKinds.IncrementalByTimeRange)]
    public void The_new_forms_load(string kind, string expected)
    {
        var (def, diags) = LoadWith(kind, expected.StartsWith("incremental") ? "grain: [order_id]\n" : "");
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        Assert.Equal(expected, def!.Kind);
    }

    [Fact]
    public void A_kinds_settings_are_read_from_beneath_its_name()
    {
        var (def, diags) = LoadWith("kind: {incremental_by_time_range: {time_column: ordered_on, lookback: 3 days}}", "grain: [ordered_on]\n");
        Assert.Empty(diags);
        Assert.Equal(("ordered_on", "3 days"), (def!.TimeColumn, def.Lookback));
        var (key, _) = LoadWith("kind: {incremental_by_unique_key: {unique_key: [order_id]}}", "grain: [order_id]\n");
        Assert.Equal(["order_id"], key!.UniqueKey);
    }

    [Theory]
    [InlineData("kind: {full: {}, view: {}}", "DDB-248", "names 2 kinds")]                           // two kinds
    [InlineData("kind: {}", "DDB-248", "empty mapping")]                                              // an empty mapping
    [InlineData("kind: [full]", "DDB-248", "name of a kind, or a mapping")]
    [InlineData("kind: {full: nope}", "DDB-248", "are a mapping")]
    [InlineData("kind: {view: {unique_key: [order_id]}}", "DDB-104", "Unknown key `unique_key` in kind `view`")]       // a setting of another kind is refused by the shape
    [InlineData("kind: {full: {from: a.b}}", "DDB-104", "Unknown key `from`")]
    [InlineData("kind: nope", "DDB-106", "Unknown kind `nope`")]
    [InlineData("kind: incremental_by_unique_key", "DDB-214", "requires a unique_key")]                // a shorthand for a kind that needs settings
    [InlineData("kind: copy", "DDB-106", "A copy names the model it copies")]
    [InlineData("kind.full: {}", "DDB-104", "Unknown key `kind.full`")]                              // a dotted key is an ordinary key with a dot in it
    public void A_kind_that_is_not_one_kind_with_its_own_settings_is_refused(string kind, string code, string found)
    {
        var (_, diags) = LoadWith(kind);
        Assert.Contains(diags, d => d.Code == code && d.Found.Contains(found, StringComparison.Ordinal));
    }

    [Fact]
    public void No_kind_at_all_is_a_missing_key()
    {
        var (_, diags) = Load("name: marts.fct_orders\n" + Cols);
        Assert.Contains(diags, d => d.Code == "DDB-105" && d.Found.Contains("`kind`", StringComparison.Ordinal));
    }

    [Fact]
    public void The_old_form_still_loads_as_the_same_kind()
    {
        var (old, oldDiags) = Load("name: marts.fct_orders\nkind: {type: incremental_by_unique_key, unique_key: [order_id]}\ngrain: [order_id]\n" + Cols);
        var (now, nowDiags) = Load("name: marts.fct_orders\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\ngrain: [order_id]\n" + Cols);
        Assert.Empty(oldDiags);
        Assert.Empty(nowDiags);
        Assert.Equal(now!.Kind, old!.Kind);
        Assert.Equal(now.UniqueKey, old.UniqueKey);
    }

    // ---- a project: the warning, and the same output either way ----

    private static string Project(bool old)
    {
        string K(string name, string settings = "") => old ? $"kind: {{type: {name}{(settings.Length > 0 ? ", " + settings : "")}}}" : settings.Length > 0 ? $"kind: {{{name}: {{{settings}}}}}" : $"kind: {name}";
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "connections:\n  wh: { engine: sqlserver }\ndefaults:\n  connections: [wh]\n");
        string[] models =
        [
            "marts.v|" + K("view") + "|",
            "marts.f|" + K("full") + "|",
            "marts.u|" + K("incremental_by_unique_key", "unique_key: [order_id]") + "|grain: [order_id]\n",
            "marts.t|" + K("incremental_by_time_range", "time_column: ordered_on, lookback: 3 days") + "|grain: [ordered_on]\n",
        ];
        foreach (var m in models)
        {
            var parts = m.Split('|');
            var stem = "models/" + parts[0].Replace('.', '/');
            File.WriteAllText(Path.Combine(dir, stem + ".yml"), $"name: {parts[0]}\n{parts[1]}\n{parts[2]}columns:\n  - {{name: order_id, type: BIGINT, nullable: false}}\n  - {{name: ordered_on, type: DATE, nullable: false}}\n");
            File.WriteAllText(Path.Combine(dir, stem + ".sql"), "SELECT order_id, CAST(NULL AS DATE) AS ordered_on FROM staging.orders\n");
        }
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), $"name: staging.orders\n{K("mapped")}\ncolumns:\n  - {{name: order_id, type: BIGINT, nullable: false}}\n");
        return dir;
    }

    [Fact]
    public void The_old_form_warns_once_for_each_file_and_the_new_form_does_not()
    {
        var old = ProjectValidator.Validate(Project(old: true));
        Assert.False(old.HasErrors, string.Join("\n", old.Diagnostics.Select(DiagnosticFormatter.Format)));
        var warned = old.Diagnostics.Where(d => d.Code == "DDB-247").ToList();
        Assert.Equal(5, warned.Count);
        Assert.All(warned, d => Assert.Equal(Severity.Warning, d.Severity));
        Assert.DoesNotContain(ProjectValidator.Validate(Project(old: false)).Diagnostics, d => d.Code == "DDB-247");
    }

    [Fact]
    public void The_old_form_and_the_new_render_the_same_files_byte_for_byte()
    {
        string Render(bool old)
        {
            var dir = Project(old);
            var (exit, _, err) = RenderFiles("--project", dir, "--write");
            Assert.True(exit == 0, err);
            var root = Path.Combine(dir, "rendered");
            return string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(f => Path.GetRelativePath(root, f) + "\n" + File.ReadAllText(f)));
        }
        var (a, b) = (Render(old: true), Render(old: false));
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_folder_that_gives_the_kind_is_replaced_whole_by_the_model()
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "connections:\n  wh: { engine: sqlserver }\ndefaults:\n  connections: [wh]\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/_dbdatabuild.yml"), "defaults:\n  kind: {incremental_by_unique_key: {unique_key: [order_id]}}\n");
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), $"name: staging.orders\nkind: mapped\n{Cols}");
        File.WriteAllText(Path.Combine(dir, "models/marts/f.yml"), $"name: marts.f\nkind: full\n{Cols}");     // not merged key by key: no unique_key survives
        File.WriteAllText(Path.Combine(dir, "models/marts/f.sql"), "SELECT order_id FROM staging.orders\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/u.yml"), $"name: marts.u\ngrain: [order_id]\n{Cols}");   // takes the folder's kind as it is
        File.WriteAllText(Path.Combine(dir, "models/marts/u.sql"), "SELECT order_id FROM staging.orders\n");
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(DiagnosticFormatter.Format)));
        Assert.Equal((ModelKinds.Full, 0), (result.Models.Single(m => m.Name == "marts.f").Kind, result.Models.Single(m => m.Name == "marts.f").UniqueKey.Count));
        Assert.Equal(["order_id"], result.Models.Single(m => m.Name == "marts.u").UniqueKey);
    }

    // ---- the rewrite ----

    private static string Rewritten(string text, bool projectFile = false)
    {
        var r = KindFormRewriter.Rewrite(text, "models/m.yml", projectFile);
        Assert.Null(r.Problem);
        return r.NewText ?? text;
    }

    [Theory]
    [InlineData("name: a\nkind: {type: view}\ncolumns: []\n", "name: a\nkind: view\ncolumns: []\n")]
    [InlineData("name: a\nkind: {type: copy, from: pos.orders, slice: {column: s, value: \"${origin.s}\"}}\n", "name: a\nkind: {copy: {from: pos.orders, slice: {column: s, value: \"${origin.s}\"}}}\n")]
    [InlineData("name: a\nkind:\n  type: full   # reloaded\ncolumns: []\n", "name: a\nkind: full   # reloaded\ncolumns: []\n")]
    [InlineData("# c\nname: a\nkind:\n  type: incremental_by_unique_key   # keyed upsert\n  unique_key: [id]   # the key\n\ngrain: [id]\n", "# c\nname: a\nkind:\n  incremental_by_unique_key:   # keyed upsert\n    unique_key: [id]   # the key\n\ngrain: [id]\n")]
    [InlineData("name: a\nkind:\n  unique_key: [id]\n  type: incremental_by_unique_key\ngrain: [id]\n", "name: a\nkind:\n  incremental_by_unique_key:\n    unique_key: [id]\ngrain: [id]\n")]
    [InlineData("name: a\nkind:\n  type: native\n  access: select\n  query: |\n    SELECT 1\n      FROM t\nconnections: [x]\n", "name: a\nkind:\n  native:\n    access: select\n    query: |\n      SELECT 1\n        FROM t\nconnections: [x]\n")]
    [InlineData("name: a\r\nkind:\r\n  type: copy\r\n  from: a.b\r\n", "name: a\r\nkind:\r\n  copy:\r\n    from: a.b\r\n")]
    [InlineData("name: a\nkind=: {type: view}\n", "name: a\nkind=: view\n")]
    public void The_old_form_is_rewritten_by_splices_that_keep_everything_else(string old, string expected)
    {
        Assert.Equal(expected, Rewritten(old));
    }

    [Fact]
    public void A_file_in_the_new_form_is_left_alone()
    {
        var r = KindFormRewriter.Rewrite("name: a\nkind: {copy: {from: a.b}}\n", "m.yml", projectFile: false);
        Assert.Equal((null, 0, null), (r.NewText, r.Count, r.Problem));
    }

    [Fact]
    public void A_project_file_is_rewritten_under_defaults()
    {
        Assert.Equal("defaults:\n  kind: full\n  connections: [a]\n", Rewritten("defaults:\n  kind:\n    type: full\n  connections: [a]\n", projectFile: true));
        Assert.Null(KindFormRewriter.Rewrite("kind: {type: full}\n", "dbdatabuild.yml", projectFile: true).NewText);
    }

    [Fact]
    public void A_rewritten_definition_loads_to_the_same_model()
    {
        const string old = "name: marts.fct_orders\nkind:\n  type: incremental_by_time_range\n  time_column: ordered_on\n  lookback: 3 days\ngrain: [ordered_on]\n" + Cols;
        var (before, _) = Load(old);
        var (after, diags) = Load(Rewritten(old));
        Assert.Empty(diags);
        Assert.Equal((before!.Kind, before.TimeColumn, before.Lookback), (after!.Kind, after.TimeColumn, after.Lookback));
    }

    // ---- `project model update` and `project update` ----

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, new StringReader(""), false);
        return (exit, o.ToString(), e.ToString());
    }

    private static string OldProject()
    {
        var dir = Project(old: true);
        File.WriteAllText(Path.Combine(dir, "models/marts/_dbdatabuild.yml"), "defaults:\n  tags: [x]\n");
        return dir;
    }

    private static string Snapshot(string dir) => string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => f + ":" + File.ReadAllText(f)));

    [Fact]
    public void Check_lists_the_files_in_the_old_form_and_writes_nothing()
    {
        var dir = OldProject();
        var before = Snapshot(dir);
        var (exit, _, err) = Cli("project", "model", "update", "--project", dir, "--check");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-247  models/marts/f.yml", err);
        Assert.Contains("DDB-247  models/staging/orders.yml", err);
        Assert.Equal(before, Snapshot(dir));
        var (jsonExit, json, _) = Cli("project", "model", "update", "--project", dir, "--check", "--format", "json");
        Assert.Equal(CliApp.ExitFindings, jsonExit);
        Assert.Contains("\"old_kind_forms\"", json);
    }

    [Fact]
    public void Write_rewrites_them_and_the_project_then_says_nothing_about_the_kind_and_renders_the_same()
    {
        var dir = OldProject();
        var oldRendered = RenderedOf(OldCopy(dir));
        var (exit, output, err) = Cli("project", "model", "update", "--project", dir, "--write");
        Assert.True(exit == CliApp.ExitOk, err + output);
        Assert.Contains("wrote models/marts/u.yml", output);
        Assert.Contains("kind: {incremental_by_unique_key: {unique_key: [order_id]}}", File.ReadAllText(Path.Combine(dir, "models/marts/u.yml")));
        Assert.DoesNotContain(ProjectValidator.Validate(dir).Diagnostics, d => d.Code == "DDB-247");
        Assert.Equal(CliApp.ExitOk, Cli("project", "model", "update", "--project", dir, "--check").Exit);
        Assert.Equal(oldRendered, RenderedOf(dir));
    }

    private static string OldCopy(string dir)
    {
        var copy = dir + "-copy";
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(dir, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
        return copy;
    }

    private static string RenderedOf(string dir)
    {
        var (exit, _, err) = RenderFiles("--project", dir, "--write");
        Assert.True(exit == 0, err);
        var root = Path.Combine(dir, "rendered");
        return string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => Path.GetRelativePath(root, f) + "\n" + File.ReadAllText(f)));
    }

    [Fact]
    public void Project_update_check_reports_them_too()
    {
        var dir = OldProject();
        var (exit, output, _) = Cli("project", "update", "--project", dir, "--offline", "--check");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("difference(s)", output);
    }
}
