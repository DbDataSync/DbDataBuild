using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Define;

public abstract record ColumnEdit(string Column);
public sealed record SetColumnType(string Column, string Type) : ColumnEdit(Column);
public sealed record SetColumnNullable(string Column, bool Nullable) : ColumnEdit(Column);
public sealed record AddColumn(string Column, string Type, bool Nullable) : ColumnEdit(Column);
public sealed record RemoveColumn(string Column) : ColumnEdit(Column);

/// <summary>Renames a declared column and records the rename (`renames: from, to`), which planning needs because renames are never inferred.</summary>
public sealed record RenameColumn(string Column, string NewName) : ColumnEdit(Column);

/// <summary>
/// Edits an existing definition by minimal text splices located with the YAML parser's source offsets (DESIGN.md 6.5), so comments,
/// key order and the author's formatting survive. Nothing is re-serialized: every change replaces, inserts or deletes only the
/// characters it has to. A structure it cannot splice safely is reported (DDB-422), never rewritten wholesale.
/// </summary>
public static class DefinitionEditor
{
    private readonly record struct Splice(int Start, int End, string Text);

    public static (string? NewText, Diagnostic? Problem) Apply(string text, string file, IReadOnlyList<ColumnEdit> edits)
    {
        if (edits.Count == 0) return (text, null);

        Diagnostic Problem(string why) => new(DiagnosticCatalog.DefinitionNotEditable, new(file, 0, 0), why);

        var parseDiags = new List<Diagnostic>();
        if (StrictYamlReader.Read(text, file, parseDiags) is not YamlMapping root || parseDiags.Count > 0)
            return (null, Problem("The file does not parse as a definition."));
        if (root.Get("columns") is not YamlSequence columns || columns.Items.Count == 0)
            return (null, Problem("`columns` is missing or empty."));
        if (columns.Flow)
            return (null, Problem("`columns` is written as a flow list (`[...]`); rewrite it as a block list (`- name: ...`)."));
        if (columns.Items.Any(i => i is not YamlMapping))
            return (null, Problem("Every entry of `columns` must be a mapping."));

        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var items = columns.Items.Cast<YamlMapping>().ToList();
        YamlMapping? Find(string name) => items.FirstOrDefault(i => i.Get("name") is YamlScalar s && string.Equals(s.Value, name, StringComparison.OrdinalIgnoreCase));

        var splices = new List<Splice>();
        var appendedColumns = new List<string>();
        var newRenames = new List<(string From, string To)>();

        foreach (var edit in edits)
        {
            switch (edit)
            {
                case SetColumnType set:
                {
                    if (Find(set.Column) is not { } item || item.Get("type") is not YamlScalar type) return (null, Problem($"Column `{set.Column}` has no `type:` to change."));
                    splices.Add(new(type.Start, type.End, YamlText.Scalar(set.Type)));
                    break;
                }
                case SetColumnNullable set:
                {
                    if (Find(set.Column) is not { } item) return (null, Problem($"Column `{set.Column}` was not found."));
                    if (item.Get("nullable") is YamlScalar existing) splices.Add(new(existing.Start, existing.End, set.Nullable ? "true" : "false"));
                    else if (!set.Nullable) // absent means nullable; only `false` needs writing
                    {
                        if (InsertNullableFalse(text, item, nl) is not { } splice) return (null, Problem($"Column `{set.Column}` has no entries to place `nullable:` after."));
                        splices.Add(splice);
                    }
                    break;
                }
                case RemoveColumn remove:
                {
                    if (Find(remove.Column) is not { } item) return (null, Problem($"Column `{remove.Column}` was not found."));
                    if (items.Count == 1) return (null, Problem("The last declared column cannot be removed: `columns` may not be empty."));
                    splices.Add(new(LineStart(text, item.Start), LineEndInclusive(text, item.End - 1), ""));
                    break;
                }
                case RenameColumn rename:
                {
                    if (Find(rename.Column) is not { } item || item.Get("name") is not YamlScalar name) return (null, Problem($"Column `{rename.Column}` was not found."));
                    splices.Add(new(name.Start, name.End, YamlText.Scalar(rename.NewName)));
                    newRenames.Add((name.Value, rename.NewName));
                    // references to the old name (grain, unique_key, time_column) follow the rename, or the definition would name a column that is gone
                    foreach (var reference in References(root, rename.Column))
                        splices.Add(new(reference.Start, reference.End, YamlText.Scalar(rename.NewName)));
                    break;
                }
                case AddColumn add:
                    appendedColumns.Add(add.Column + "\u0000" + add.Type + "\u0000" + (add.Nullable ? "1" : "0"));
                    break;
            }
        }

        // appended items go after the last item that survives the edits, in the style the file already uses
        var removed = edits.OfType<RemoveColumn>().Select(r => r.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var survivors = items.Where(i => i.Get("name") is YamlScalar n && !removed.Contains(n.Value)).ToList();
        if (survivors.Count == 0)
            return (null, Problem("Removing every declared column at once cannot be spliced; edit the definition by hand."));
        var last = survivors[^1];
        var dashIndent = DashIndent(text, last);
        var keyIndent = new string(' ', last.Column - 1);
        var afterColumns = new StringBuilder();
        foreach (var packed in appendedColumns)
        {
            var parts = packed.Split('\u0000');
            afterColumns.Append(nl).Append(last.Flow
                ? $"{dashIndent}- {{name: {YamlText.FlowScalar(parts[0])}, type: {YamlText.FlowScalar(parts[1])}{(parts[2] == "0" ? ", nullable: false" : "")}}}"
                : $"{dashIndent}- name: {YamlText.Scalar(parts[0])}{nl}{keyIndent}type: {YamlText.Scalar(parts[1])}{(parts[2] == "0" ? $"{nl}{keyIndent}nullable: false" : "")}");
        }

        // renames: appended to an existing block list, or a new block after `columns`
        if (newRenames.Count > 0)
        {
            if (root.Get("renames") is { } existing)
            {
                if (existing is not YamlSequence { Flow: false, Items.Count: > 0 } list || list.Items.Any(i => i is not YamlMapping))
                    return (null, Problem("`renames` is written as a flow list or is empty; rewrite it as a block list (`- from: ... / to: ...`)."));
                var lastRename = (YamlMapping)list.Items[^1];
                var rd = DashIndent(text, lastRename);
                var rk = new string(' ', lastRename.Column - 1);
                var sb = new StringBuilder();
                foreach (var (from, to) in newRenames) sb.Append(nl).Append($"{rd}- from: {YamlText.Scalar(from)}{nl}{rk}to: {YamlText.Scalar(to)}");
                var at = LineEnd(text, lastRename.End - 1);
                splices.Add(new(at, at, sb.ToString()));
            }
            else
            {
                afterColumns.Append(nl).Append("renames:");
                foreach (var (from, to) in newRenames)
                    afterColumns.Append(nl).Append($"{dashIndent}- from: {YamlText.Scalar(from)}{nl}{dashIndent}  to: {YamlText.Scalar(to)}");
            }
        }
        if (afterColumns.Length > 0)
        {
            var at = LineEnd(text, last.End - 1);
            splices.Add(new(at, at, afterColumns.ToString()));
        }

        // apply from the end so earlier offsets stay valid; overlapping edits mean a bug in the caller
        var ordered = splices.OrderByDescending(s => s.Start).ThenByDescending(s => s.End).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].End > ordered[i - 1].Start) return (null, Problem("Two edits touch the same text."));
        var result = new StringBuilder(text);
        foreach (var s in ordered) result.Remove(s.Start, s.End - s.Start).Insert(s.Start, s.Text);
        var newText = result.ToString();

        var verify = new List<Diagnostic>();
        if (StrictYamlReader.Read(newText, file, verify) == null || verify.Count > 0)
            throw new InvalidOperationException("define produced a definition that does not parse; nothing was written. This is a tool bug.");
        return (newText, null);
    }

    /// <summary>Scalars in grain and the unique_key and time_column of the kind that name <paramref name="column"/> (case-insensitive).</summary>
    private static IEnumerable<YamlScalar> References(YamlMapping root, string column)
    {
        bool Is(YamlNode n) => n is YamlScalar s && string.Equals(s.Value, column, StringComparison.OrdinalIgnoreCase);
        if (root.Get("grain") is YamlSequence grain)
            foreach (var g in grain.Items.Where(Is)) yield return (YamlScalar)g;
        if (ModelKindShape.SettingsOf(root.Get("kind")) is { } kind)
        {
            if (kind.Get("unique_key") is YamlSequence key)
                foreach (var k in key.Items.Where(Is)) yield return (YamlScalar)k;
            if (kind.Get("time_column") is { } time && Is(time)) yield return (YamlScalar)time;
        }
    }

    private static Splice? InsertNullableFalse(string text, YamlMapping item, string nl)
    {
        var anchor = item.Get("type") is { } type ? type : item.Entries.Count > 0 ? item.Entries[^1].Value : null;
        if (anchor == null) return null;
        if (item.Flow)
        {
            var last = item.Entries[^1].Value;
            return new Splice(last.End, last.End, ", nullable: false");
        }
        var indent = new string(' ', item.Entries[0].Key.Column - 1);
        var at = LineEnd(text, anchor.End - 1);
        return new Splice(at, at, nl + indent + "nullable: false");
    }

    /// <summary>The leading whitespace of the line a list item starts on, up to its dash.</summary>
    private static string DashIndent(string text, YamlMapping item)
    {
        var lineStart = LineStart(text, item.Start);
        var prefix = text[lineStart..item.Start];
        var dash = prefix.LastIndexOf('-');
        return dash < 0 ? new string(' ', Math.Max(0, item.Column - 3)) : prefix[..dash];
    }

    private static int LineStart(string text, int index)
    {
        var i = text.LastIndexOf('\n', Math.Max(0, index - 1));
        return i < 0 ? 0 : i + 1;
    }

    /// <summary>Offset of the end of the line containing <paramref name="index"/>, before its line break (where a trailing comment ends).</summary>
    private static int LineEnd(string text, int index)
    {
        var i = text.IndexOf('\n', index);
        if (i < 0) return text.Length;
        return i > 0 && text[i - 1] == '\r' ? i - 1 : i;
    }

    /// <summary>Offset just after the line break ending the line containing <paramref name="index"/> (or the end of the text).</summary>
    private static int LineEndInclusive(string text, int index)
    {
        var i = text.IndexOf('\n', index);
        return i < 0 ? text.Length : i + 1;
    }
}
