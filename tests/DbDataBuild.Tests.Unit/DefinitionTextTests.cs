using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.SchemaConformanceTests;

namespace DbDataBuild.Tests.Unit;

public class UnifiedDiffTests
{
    /// <summary>Applies a unified diff produced by <see cref="UnifiedDiff"/> to the old text, to prove the diff is faithful.</summary>
    internal static string Patch(string oldText, string diff)
    {
        var oldLines = oldText.Length == 0 ? [] : oldText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var result = new List<string>();
        var pos = 0;
        var lines = diff.Split('\n');
        var i = 0;
        while (i < lines.Length && !lines[i].StartsWith("@@")) i++;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("@@"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^@@ -(\d+)(?:,(\d+))? ");
                var start = int.Parse(m.Groups[1].Value);
                var count = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                var target = count == 0 ? start : start - 1;
                while (pos < target) result.Add(oldLines[pos++]);
            }
            else if (line.StartsWith('+')) result.Add(line[1..]);
            else if (line.StartsWith('-')) pos++;
            else if (line.StartsWith(' ')) { result.Add(oldLines[pos]); pos++; }
        }
        while (pos < oldLines.Count) result.Add(oldLines[pos++]);
        return result.Count == 0 ? "" : string.Join('\n', result) + "\n";
    }

    [Fact]
    public void Equal_texts_give_no_diff() => Assert.Equal("", UnifiedDiff.Create("a\nb\n", "a\nb\n", "m.yml"));

    [Fact]
    public void A_change_shows_context_and_hunk_headers()
    {
        var oldText = string.Join('\n', Enumerable.Range(1, 12).Select(n => $"line{n}")) + "\n";
        var newText = oldText.Replace("line6\n", "changed\n");
        var diff = UnifiedDiff.Create(oldText, newText, "models/m.yml");
        Assert.StartsWith("--- a/models/m.yml\n+++ b/models/m.yml\n@@ -3,7 +3,7 @@\n line3\n line4\n line5\n-line6\n+changed\n line7\n line8\n line9\n", diff);
        Assert.Equal(newText, Patch(oldText, diff));
    }

    [Fact]
    public void A_new_file_is_a_diff_from_dev_null()
    {
        var diff = UnifiedDiff.Create(null, "a\nb\n", "models/m.yml");
        Assert.StartsWith("--- /dev/null\n+++ b/models/m.yml\n@@ -0,0 +1,2 @@\n+a\n+b\n", diff);
        Assert.Equal("a\nb\n", Patch("", diff));
    }

    [Fact]
    public void Distant_changes_make_separate_hunks_and_the_patch_reproduces_the_new_text()
    {
        var oldText = string.Join('\n', Enumerable.Range(1, 30).Select(n => $"line{n}")) + "\n";
        var newText = oldText.Replace("line2\n", "two\n").Replace("line28\n", "twenty-eight\nextra\n");
        var diff = UnifiedDiff.Create(oldText, newText, "m.yml");
        Assert.Equal(2, diff.Split('\n').Count(l => l.StartsWith("@@")));
        Assert.Equal(newText, Patch(oldText, diff));
    }

    [Fact]
    public void Insertions_deletions_and_a_missing_final_newline_are_represented()
    {
        Assert.Contains("\\ No newline at end of file", UnifiedDiff.Create("a\nb\n", "a\nb", "m.yml"));
        var removed = UnifiedDiff.Create("a\nb\nc\n", "a\nc\n", "m.yml");
        Assert.Contains("-b\n", removed);
        Assert.Equal("a\nc\n", Patch("a\nb\nc\n", removed));
    }
}

public class DefinitionWriterTests
{
    private static ModelDefinition Def(string kind = "full", string[]? unique = null, string? time = null, string? lookback = null, string[]? grain = null,
        string[]? targets = null, ColumnDefinition[]? cols = null, RenameDefinition[]? renames = null) =>
        new("marts.fct_orders", kind, unique ?? [], time, lookback, grain ?? [], targets, cols ?? [new("a", "INTEGER")], renames ?? []);

    [Fact]
    public void Matches_the_design_document_example_exactly()
    {
        var text = DefinitionWriter.Create(Def("incremental_by_unique_key", ["order_id"], grain: ["order_id"], targets: ["sqlserver", "fabric"],
            cols: [new("order_id", "BIGINT", false), new("customer_id", "BIGINT"), new("order_date", "DATE"), new("amount", "DECIMAL(14, 2)"), new("discount_code", "VARCHAR(20)")]));
        Assert.Equal("""
            name: marts.fct_orders
            kind:
              incremental_by_unique_key:
                unique_key: [order_id]
            grain: [order_id]
            connections=: [sqlserver, fabric]
            columns:
              - name: order_id
                type: BIGINT
                nullable: false
              - name: customer_id
                type: BIGINT
              - name: order_date
                type: DATE
              - name: amount
                type: DECIMAL(14, 2)
              - name: discount_code
                type: VARCHAR(20)

            """.Replace("\r\n", "\n"), text);
    }

    [Fact]
    public void Time_range_collation_and_renames_use_a_fixed_key_order()
    {
        var text = DefinitionWriter.Create(Def("incremental_by_time_range", time: "order_date", lookback: "3 days", grain: ["order_date"],
            cols: [new("order_date", "DATE", false, "default")], renames: [new("old", "order_date")]));
        Assert.Equal("name: marts.fct_orders\nkind:\n  incremental_by_time_range:\n    time_column: order_date\n    lookback: 3 days\ngrain: [order_date]\n" +
                     "columns:\n  - name: order_date\n    type: DATE\n    nullable: false\n    collation: default\nrenames:\n  - from: old\n    to: order_date\n", text);
    }

    [Theory]
    [InlineData("Order Id", "Order Id")]
    [InlineData("order-id", "order-id")]
    [InlineData("yes", "\"yes\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("2024", "\"2024\"")]
    [InlineData("a: b", "\"a: b\"")]
    [InlineData("#tag", "\"#tag\"")]
    [InlineData("trailing ", "\"trailing \"")]
    [InlineData("naïve", "\"naïve\"")]
    [InlineData("DECIMAL(14, 2)", "DECIMAL(14, 2)")]
    public void Scalars_are_plain_only_when_that_is_unambiguous(string value, string expected) => Assert.Equal(expected, YamlText.Scalar(value));

    [Fact]
    public void Flow_scalars_quote_commas()
    {
        Assert.Equal("[a, \"b, c\", d_e]", YamlText.FlowList(["a", "b, c", "d_e"]));
    }

    [Fact]
    public void Every_written_definition_is_valid_for_the_schema_and_the_loader_and_round_trips()
    {
        var def = Def("incremental_by_unique_key", ["Order Id", "yes"], grain: ["Order Id", "yes"], targets: ["postgres"],
            cols: [new("Order Id", "BIGINT", false), new("yes", "VARCHAR(5)", true, "default"), new("naïve", "TIMESTAMP")]);
        var text = DefinitionWriter.Create(def);
        Assert.True(SchemaAccepts(LoadSchema("model"), text));
        var diags = new List<Diagnostic>();
        var back = ModelDefinitionLoader.Load(text, "models/marts/fct_orders.yml", "marts.fct_orders", diags)!;
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        Assert.Equal(def.Columns.Select(c => (c.Name, c.Type, c.Nullable, c.Collation)), back.Columns.Select(c => (c.Name, c.Type, c.Nullable, c.Collation)));
        Assert.Equal(def.UniqueKey, back.UniqueKey);
        Assert.Equal(def.Grain, back.Grain);
        Assert.Equal(def.Targets, back.Targets);
    }
}

public class DefinitionEditorTests
{
    private const string Base = """
        # Orders fact table (hand-written comment)
        name: marts.fct_orders
        kind:
          incremental_by_unique_key:   # keyed upsert
            unique_key: [order_id]

        grain: [order_id]
        connections: [sqlserver, fabric]
        columns:
          # identifiers
          - name: order_id
            type: BIGINT
            nullable: false
          - name: customer_id
            type: BIGINT
          - name: amount
            type: DECIMAL(14, 2)   # money
          - name: discount_code
            type: VARCHAR(20)

        # trailing comment
        """;

    private static string Text => Base.Replace("\r\n", "\n") + "\n";

    private static string Edit(string text, params ColumnEdit[] edits)
    {
        var (result, problem) = DefinitionEditor.Apply(text, "models/marts/fct_orders.yml", edits);
        Assert.Null(problem);
        // the result must remain a valid definition
        var diags = new List<Diagnostic>();
        Assert.NotNull(ModelDefinitionLoader.Load(result!, "models/marts/fct_orders.yml", "marts.fct_orders", diags));
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return result!;
    }

    [Fact]
    public void Changing_a_type_replaces_only_that_scalar()
    {
        var result = Edit(Text, new SetColumnType("amount", "DECIMAL(18, 2)"));
        Assert.Equal(Text.Replace("type: DECIMAL(14, 2)   # money", "type: DECIMAL(18, 2)   # money"), result);   // the comment and spacing survive
    }

    [Fact]
    public void Setting_nullable_false_inserts_one_line_after_type_with_the_right_indent()
    {
        var result = Edit(Text, new SetColumnNullable("customer_id", false));
        Assert.Equal(Text.Replace("    type: BIGINT\n  - name: amount", "    type: BIGINT\n    nullable: false\n  - name: amount"), result);
    }

    [Fact]
    public void Changing_an_existing_nullable_replaces_the_value_and_true_when_absent_changes_nothing()
    {
        Assert.Equal(Text.Replace("nullable: false", "nullable: true"), Edit(Text, new SetColumnNullable("order_id", true)));
        Assert.Equal(Text, Edit(Text, new SetColumnNullable("customer_id", true)));
    }

    [Fact]
    public void Adding_a_column_appends_a_block_item_after_the_last_one_and_before_what_follows()
    {
        var result = Edit(Text, new AddColumn("net_amount", "DECIMAL(14, 2)", false));
        Assert.Equal(Text.Replace("    type: VARCHAR(20)\n\n# trailing", "    type: VARCHAR(20)\n  - name: net_amount\n    type: DECIMAL(14, 2)\n    nullable: false\n\n# trailing"), result);
    }

    [Fact]
    public void Removing_a_column_deletes_exactly_its_lines()
    {
        var result = Edit(Text, new RemoveColumn("customer_id"));
        Assert.Equal(Text.Replace("  - name: customer_id\n    type: BIGINT\n", ""), result);
    }

    [Fact]
    public void Renaming_changes_the_name_and_adds_a_renames_block_after_columns()
    {
        var result = Edit(Text, new RenameColumn("discount_code", "promo_code"));
        Assert.Equal(Text.Replace("  - name: discount_code\n    type: VARCHAR(20)\n\n# trailing",
            "  - name: promo_code\n    type: VARCHAR(20)\nrenames:\n  - from: discount_code\n    to: promo_code\n\n# trailing"), result);
    }

    [Fact]
    public void A_rename_is_appended_to_an_existing_renames_block()
    {
        var withRenames = Text.Replace("\n# trailing", "renames:\n  - from: old_a\n    to: order_id\n\n# trailing");
        var result = Edit(withRenames, new RenameColumn("amount", "total"));
        Assert.Contains("renames:\n  - from: old_a\n    to: order_id\n  - from: amount\n    to: total\n", result);
        Assert.Contains("  - name: total\n    type: DECIMAL(14, 2)   # money", result);
    }

    [Fact]
    public void Several_edits_at_once_touch_only_what_they_name_and_keep_every_comment()
    {
        var result = Edit(Text,
            new SetColumnType("amount", "DECIMAL(18, 2)"), new SetColumnNullable("customer_id", false), new RemoveColumn("discount_code"),
            new AddColumn("net", "BIGINT", true), new RenameColumn("order_id", "id"));
        foreach (var kept in new[] { "# Orders fact table (hand-written comment)", "# keyed upsert", "# identifiers", "# money", "# trailing comment", "unique_key: [id]", "connections: [sqlserver, fabric]" })
            Assert.Contains(kept, result);
        var diags = new List<Diagnostic>();
        var def = ModelDefinitionLoader.Load(result, "models/marts/fct_orders.yml", "marts.fct_orders", diags)!;
        Assert.Equal(["id", "customer_id", "amount", "net"], def.Columns.Select(c => c.Name));
        Assert.False(def.Columns[1].Nullable);
        Assert.Equal("DECIMAL(18, 2)", def.Columns[2].Type);
        Assert.Equal(new RenameDefinition("order_id", "id"), Assert.Single(def.Renames));
        Assert.Equal(["id"], def.UniqueKey);                                // the rename carries its references with it
        Assert.Equal(["id"], def.Grain);
    }

    [Fact]
    public void Renaming_a_key_or_time_column_updates_every_reference_and_nothing_else()
    {
        const string text = "name: marts.fct_orders\nkind:\n  incremental_by_time_range:\n    time_column: order_date   # the clock\n    lookback: 3 days\n" +
                            "grain: [order_date, Order_ID]\ncolumns:\n  - {name: order_date, type: DATE}\n  - {name: order_id, type: BIGINT}\n";
        var result = Edit(text, new RenameColumn("order_date", "day"), new RenameColumn("order_id", "id"));
        Assert.Contains("time_column: day   # the clock", result);
        Assert.Contains("grain: [day, id]", result);
        Assert.Contains("lookback: 3 days", result);
    }

    [Fact]
    public void Edits_never_change_the_order_of_the_other_top_level_keys()
    {
        var result = Edit(Text, new AddColumn("net", "BIGINT", true), new RenameColumn("amount", "total"));
        var keys = result.Split('\n').Where(l => l.Length > 0 && char.IsLetter(l[0])).Select(l => l.Split(':')[0]).ToList();
        Assert.Equal(["name", "kind", "grain", "connections", "columns", "renames"], keys);
    }

    private const string FlowItems = "name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: a, type: INTEGER}\n  - {name: b, type: VARCHAR(5), nullable: false}\n";

    [Fact]
    public void Flow_style_items_are_edited_in_flow_style()
    {
        var result = Edit(FlowItems, new SetColumnNullable("a", false), new AddColumn("c", "DECIMAL(10, 2)", false), new SetColumnType("b", "VARCHAR(9)"));
        Assert.Equal("name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: a, type: INTEGER, nullable: false}\n  - {name: b, type: VARCHAR(9), nullable: false}\n" +
                     "  - {name: c, type: \"DECIMAL(10, 2)\", nullable: false}\n", result);
    }

    [Fact]
    public void Crlf_line_endings_are_preserved()
    {
        var crlf = Text.Replace("\n", "\r\n");
        var result = Edit(crlf, new AddColumn("net", "BIGINT", false), new SetColumnNullable("customer_id", false));
        Assert.DoesNotContain("\r\r", result);
        Assert.Equal(result.Replace("\r\n", "\n").Count(c => c == '\n'), result.Count(c => c == '\n'));
        Assert.All(result.Split('\n').SkipLast(1), l => Assert.EndsWith("\r", l));
    }

    [Fact]
    public void A_file_without_a_final_newline_stays_valid()
    {
        var text = FlowItems.TrimEnd('\n');
        var result = Edit(text, new AddColumn("c", "INTEGER", true));
        Assert.EndsWith("  - {name: c, type: INTEGER}", result);
        Assert.Equal(text + "\n  - {name: c, type: INTEGER}", result);
    }

    [Fact]
    public void Structures_that_cannot_be_spliced_are_reported_not_rewritten()
    {
        var (_, flowList) = DefinitionEditor.Apply("name: m\nkind: full\ncolumns: [{name: a, type: INT}]\n", "m.yml", [new AddColumn("b", "INT", true)]);
        Assert.Equal("DDB-422", flowList!.Code);
        Assert.Contains("flow list", flowList.Found);

        var (_, flowRenames) = DefinitionEditor.Apply("name: m\nkind: full\ncolumns:\n  - {name: a, type: INT}\nrenames: []\n", "m.yml", [new RenameColumn("a", "b")]);
        Assert.Equal("DDB-422", flowRenames!.Code);

        var (_, onlyColumn) = DefinitionEditor.Apply("name: m\nkind: full\ncolumns:\n  - {name: a, type: INT}\n", "m.yml", [new RemoveColumn("a")]);
        Assert.Equal("DDB-422", onlyColumn!.Code);

        var (_, missing) = DefinitionEditor.Apply("name: m\nkind: full\ncolumns:\n  - {name: a, type: INT}\n", "m.yml", [new SetColumnType("zzz", "INT")]);
        Assert.Equal("DDB-422", missing!.Code);

        var (_, broken) = DefinitionEditor.Apply("a: [1, 2\n", "m.yml", [new RemoveColumn("a")]);
        Assert.Equal("DDB-422", broken!.Code);
    }

    [Fact]
    public void No_edits_returns_the_text_untouched()
    {
        var original = Text;
        var (text, problem) = DefinitionEditor.Apply(original, "m.yml", []);
        Assert.Null(problem);
        Assert.Same(original, text);
    }

    [Fact]
    public void Column_names_match_case_insensitively_like_the_loader()
    {
        Assert.Contains("type: BIGINT\n    nullable: false\n  - name: amount", Edit(Text, new SetColumnNullable("CUSTOMER_ID", false)));
    }
}

public class DefinitionFileTests
{
    [Fact]
    public void Writes_a_new_file_and_an_unchanged_existing_one_atomically()
    {
        var dir = TestSupport.NewProjectDir();
        var path = Path.Combine(dir, "models/marts/x.yml");
        Assert.Null(DefinitionFile.WriteIfUnchanged(path, "models/marts/x.yml", null, "a: 1\n"));
        Assert.Equal("a: 1\n", File.ReadAllText(path));

        var hash = DefinitionFile.HashIfExists(path);
        Assert.Null(DefinitionFile.WriteIfUnchanged(path, "models/marts/x.yml", hash, "a: 2\n"));
        Assert.Equal("a: 2\n", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "models/marts"), "*.tmp"));
        Assert.False(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));       // no BOM
    }

    [Fact]
    public void Refuses_to_overwrite_a_file_that_changed_after_it_was_read()
    {
        var dir = TestSupport.NewProjectDir();
        var path = Path.Combine(dir, "models/marts/x.yml");
        File.WriteAllText(path, "a: 1\n");
        var hash = DefinitionFile.HashIfExists(path);
        File.WriteAllText(path, "a: edited by someone else\n");

        var problem = DefinitionFile.WriteIfUnchanged(path, "models/marts/x.yml", hash, "a: 2\n");
        Assert.Equal("DDB-421", problem!.Code);
        Assert.Equal("a: edited by someone else\n", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "models/marts"), "*.tmp"));
    }

    [Fact]
    public void Refuses_to_create_a_file_that_appeared_meanwhile()
    {
        var dir = TestSupport.NewProjectDir();
        var path = Path.Combine(dir, "models/marts/x.yml");
        File.WriteAllText(path, "someone: here\n");
        Assert.Equal("DDB-421", DefinitionFile.WriteIfUnchanged(path, "models/marts/x.yml", null, "a: 1\n")!.Code);
        Assert.Equal("someone: here\n", File.ReadAllText(path));
    }

    [Fact]
    public void Will_not_write_anything_but_a_yml_so_a_query_body_cannot_be_written()
    {
        var dir = TestSupport.NewProjectDir();
        var sql = Path.Combine(dir, "models/marts/x.sql");
        File.WriteAllText(sql, "SELECT 1");
        Assert.Throws<InvalidOperationException>(() => DefinitionFile.WriteIfUnchanged(sql, "models/marts/x.sql", DefinitionFile.HashIfExists(sql), "SELECT 2"));
        Assert.Equal("SELECT 1", File.ReadAllText(sql));
    }
}
