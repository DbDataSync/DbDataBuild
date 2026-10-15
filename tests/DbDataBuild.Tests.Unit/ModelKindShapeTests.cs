using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The kind of a model is a key named for the kind with its settings beneath it (`kind: {copy: {from: x}}`), a plain value for a kind with none (`kind: view`); the earlier form (`kind: {type: copy, ...}`) is an error that names the form to write.</summary>
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

    [Theory]
    [InlineData("kind: {type: view}", "view")]
    [InlineData("kind: {type: incremental_by_unique_key, unique_key: [order_id]}", "incremental_by_unique_key")]
    [InlineData("kind:\n  type: copy\n  from: a.b", "copy")]
    [InlineData("kind:\n  unique_key: [order_id]\n  type: incremental_by_unique_key", "incremental_by_unique_key")]
    public void The_earlier_form_is_an_error_that_names_the_form_to_write(string kind, string name)
    {
        var (def, diags) = LoadWith(kind, "grain: [order_id]\n");
        Assert.Null(def);
        var d = Assert.Single(diags, x => x.Code == "DDB-248");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains($"kind: {{{name}: {{...}}}}", d.Fix);                 // the fix writes the new form with the kind named
        Assert.DoesNotContain(diags, x => x.Code == "DDB-247");              // the warning of the release that still read it is retired
    }

    [Fact]
    public void A_project_that_writes_the_earlier_form_in_a_folder_file_or_a_model_does_not_compile()
    {
        var dir = Project();
        File.WriteAllText(Path.Combine(dir, "models/marts/_dbdatabuild.yml"), "defaults:\n  kind: {type: view}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/v.yml"), $"name: marts.v\n{Cols}");
        File.WriteAllText(Path.Combine(dir, "models/marts/v.sql"), "SELECT order_id, CAST(NULL AS DATE) AS ordered_on FROM staging.orders\n");
        var result = ProjectValidator.Validate(dir);
        Assert.Contains(result.Diagnostics, d => d.Code == "DDB-248" && d.Location.File == "models/marts/_dbdatabuild.yml");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "DDB-247");
        var (exit, _, err) = Cli("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-248", err);
    }

    // ---- a project in the new form ----

    private static string Project()
    {
        string K(string name, string settings = "") => settings.Length > 0 ? $"kind: {{{name}: {{{settings}}}}}" : $"kind: {name}";
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

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, new StringReader(""), false);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void A_project_in_the_new_form_validates_without_a_word_about_the_kind()
    {
        var result = ProjectValidator.Validate(Project());
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(DiagnosticFormatter.Format)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "DDB-247" or "DDB-248");
        Assert.Equal(CliApp.ExitOk, Cli("project", "model", "update", "--project", Project(), "--check").Exit);
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
}
