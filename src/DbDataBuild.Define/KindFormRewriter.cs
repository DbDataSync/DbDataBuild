using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Define;

/// <summary>
/// Rewrites the old form of a model's kind (<c>kind: {type: copy, from: pos.orders}</c>) to the form keyed by the kind's name (<c>kind: {copy: {from: pos.orders}}</c>, or <c>kind: view</c> when the kind has
/// no settings), by text splices located with the parser's offsets, so comments, key order and the author's layout survive (the same discipline as <see cref="DefinitionEditor"/>). The result is read again and
/// must name the same kind with the same settings; if it does not, nothing is changed and the problem is said. The files are a model's definition, and a project file (<c>dbdatabuild.yml</c> and a folder's
/// <c>_dbdatabuild.yml</c>), where the kind may be given under <c>defaults:</c>.
/// </summary>
public static class KindFormRewriter
{
    /// <param name="NewText">The rewritten text; null when nothing in the file is in the old form, or when it could not be rewritten safely (then <paramref name="Problem"/> says why).</param>
    /// <param name="Count">How many `kind` entries were in the old form.</param>
    public sealed record Result(string? NewText, int Count, string? Problem);

    private readonly record struct Splice(int Start, int End, string Text);

    /// <param name="projectFile">True for `dbdatabuild.yml` or a folder's `_dbdatabuild.yml`: the kind is under `defaults:`.</param>
    public static Result Rewrite(string text, string file, bool projectFile)
    {
        var diags = new List<Diagnostic>();
        if (StrictYamlReader.Read(text, file, diags) is not YamlMapping root || diags.Count > 0) return new(null, 0, null);   // a file that does not load is reported by the loader, not by this
        var owner = projectFile ? root.Get("defaults") as YamlMapping : root;
        if (owner == null) return new(null, 0, null);
        var olds = owner.Entries.Where(e => KindKeyName(e.Key.Value) && ModelKindShape.IsOldForm(e.Value) && ((YamlMapping)e.Value).Get(ModelKindShape.OldKey) is YamlScalar { Value.Length: > 0 }).ToList();
        if (olds.Count == 0) return new(null, 0, null);

        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var splices = new List<Splice>();
        foreach (var entry in olds)
        {
            var (made, problem) = Splices(text, entry, nl);
            if (made == null) return new(null, olds.Count, problem);
            splices.AddRange(made);
        }
        var ordered = splices.OrderByDescending(s => s.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].End > ordered[i - 1].Start) return new(null, olds.Count, "two edits touch the same text");
        var result = new StringBuilder(text);
        foreach (var s in ordered) result.Remove(s.Start, s.End - s.Start).Insert(s.Start, s.Text);
        var newText = result.ToString();

        // the rewrite must say the same thing: read it again and compare the kind each entry names, and its settings
        var check = new List<Diagnostic>();
        if (StrictYamlReader.Read(newText, file, check) is not YamlMapping again || check.Count > 0) return new(null, olds.Count, "the rewritten text does not parse");
        var ownerAgain = projectFile ? again.Get("defaults") as YamlMapping : again;
        if (ownerAgain == null) return new(null, olds.Count, "the rewritten text lost its `defaults`");
        foreach (var entry in olds)
        {
            var after = ownerAgain.Entries.FirstOrDefault(e => e.Key.Value == entry.Key.Value);
            if (after == null || ModelKindShape.IsOldForm(after.Value) || Canonical(after.Value) != Canonical(entry.Value))
                return new(null, olds.Count, $"the rewritten `{entry.Key.Value}` would not say what the old one says");
        }
        return new(newText, olds.Count, null);
    }

    private static bool KindKeyName(string key) => key is "kind" or "kind=" or "kind+";

    /// <summary>The kind and its settings as one comparable text, whichever form they are written in.</summary>
    private static string Canonical(YamlNode kind)
    {
        var name = ModelKindShape.NameOf(kind);
        var settings = ModelKindShape.SettingsOf(kind);
        var sb = new StringBuilder(name).Append('{');
        foreach (var e in (settings?.Entries ?? []).Where(e => e.Key.Value != ModelKindShape.OldKey).OrderBy(e => e.Key.Value, StringComparer.Ordinal))
            sb.Append(e.Key.Value).Append('=').Append(Node(e.Value)).Append(';');
        return sb.Append('}').ToString();
    }

    private static string Node(YamlNode n) => n switch
    {
        YamlScalar s => "'" + s.Value + "'",
        YamlSequence q => "[" + string.Join(",", q.Items.Select(Node)) + "]",
        YamlMapping m => "{" + string.Join(",", m.Entries.Select(e => e.Key.Value + ":" + Node(e.Value))) + "}",
        _ => "?",
    };

    private static (List<Splice>? Made, string? Problem) Splices(string text, YamlEntry entry, string nl)
    {
        var map = (YamlMapping)entry.Value;
        var type = map.Entries.First(e => e.Key.Value == ModelKindShape.OldKey);
        var name = ((YamlScalar)type.Value).Value;
        var others = map.Entries.Where(e => e != type).ToList();
        var key = entry.Key.Value;

        if (map.Flow)
        {
            var body = others.Count == 0 ? name : "{" + name + ": {" + string.Join(", ", others.Select(e => text[e.Key.Start..e.Value.End])) + "}}";
            return ([new Splice(map.Start, map.End, body)], null);
        }

        if (others.Count == 0)
        {
            // `kind:` and `type: X` on the next line: both become `kind: X`. A comment on the `kind:` line has nowhere to go, so that one is left to a person.
            var between = text[entry.Key.End..type.Value.Start];
            if (between.Contains('#')) return (null, $"`{key}:` has a comment before its `type:`; rewrite it by hand: `{key}: {name}`");
            return ([new Splice(entry.Key.End, type.Value.End, ": " + name)], null);
        }

        // the lines of the mapping: the `type` line becomes `<name>:` (keeping its trailing comment), the lines of the settings move two spaces in
        var first = map.Entries.Min(e => e.Key.Start);
        var indent = text[LineStart(text, first)..first];
        if (indent.Trim().Length > 0) return (null, $"`{key}` is written in a layout this cannot rewrite; do it by hand: `{key}: {{{name}: {{...}}}}`");
        var typeLineStart = LineStart(text, type.Key.Start);
        var typeLineEnd = LineEndInclusive(text, type.Value.End - 1);
        var trailing = text[type.Value.End..LineEnd(text, type.Value.End)].Trim();
        var header = indent + name + ":" + (trailing.StartsWith('#') ? "   " + trailing : "") + nl;

        var splices = new List<Splice>();
        var blockStart = LineStart(text, first);
        var blockEnd = LineEndInclusive(text, map.End - 1);
        // every line of the block except the `type` line moves in; the header goes where the block starts
        foreach (var (start, line) in Lines(text, blockStart, blockEnd))
        {
            if (start == typeLineStart || line.Trim().Length == 0) continue;
            if (start == blockStart && typeLineStart != blockStart) continue;       // this one is indented together with the header below
            splices.Add(new Splice(start, start, "  "));
        }
        if (typeLineStart == blockStart) splices.Add(new Splice(typeLineStart, typeLineEnd, header));
        else
        {
            splices.Add(new Splice(typeLineStart, typeLineEnd, ""));
            splices.Add(new Splice(blockStart, blockStart, header + "  "));
        }
        return (splices, null);
    }

    private static IEnumerable<(int Start, string Line)> Lines(string text, int from, int to)
    {
        var i = from;
        while (i < to)
        {
            var end = text.IndexOf('\n', i);
            end = end < 0 || end >= to ? to : end + 1;
            yield return (i, text[i..end]);
            i = end;
        }
    }

    private static int LineStart(string text, int index)
    {
        var i = text.LastIndexOf('\n', Math.Max(0, index - 1));
        return i < 0 ? 0 : i + 1;
    }

    private static int LineEnd(string text, int index)
    {
        var i = text.IndexOf('\n', index);
        if (i < 0) return text.Length;
        return i > 0 && text[i - 1] == '\r' ? i - 1 : i;
    }

    private static int LineEndInclusive(string text, int index)
    {
        var i = text.IndexOf('\n', index);
        return i < 0 ? text.Length : i + 1;
    }
}
