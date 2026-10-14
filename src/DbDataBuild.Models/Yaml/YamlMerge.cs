using DbDataBuild.Core;

namespace DbDataBuild.Models.Yaml;

/// <summary>One file's mapping, to be merged onto the layers above it.</summary>
public sealed record YamlLayer(string File, YamlMapping Root);

/// <summary>
/// The merged mapping, and the file every node of it came from (by reference: the leaves are the nodes the files were read into, so a line number is still the line in its own file).
/// </summary>
public sealed class MergedYaml(YamlMapping root, IReadOnlyDictionary<YamlNode, string> fileOf)
{
    public YamlMapping Root { get; } = root;
    public IReadOnlyDictionary<YamlNode, string> FileOf { get; } = fileOf;

    /// <summary>Every scalar of the result with the file and line it was written on, keyed by its dotted path (`kind.copy.from`, `connections[1]`), in document order.</summary>
    public IReadOnlyList<(string Path, string File, int Line, string Value)> Origins()
    {
        var result = new List<(string, string, int, string)>();
        void Walk(YamlNode node, string path)
        {
            switch (node)
            {
                case YamlScalar s: result.Add((path, FileOf.GetValueOrDefault(s, ""), s.Line, s.Value)); break;
                case YamlSequence q: for (var i = 0; i < q.Items.Count; i++) Walk(q.Items[i], $"{path}[{i}]"); break;
                case YamlMapping m: foreach (var e in m.Entries) Walk(e.Value, path.Length == 0 ? e.Key.Value : $"{path}.{e.Key.Value}"); break;
            }
        }
        Walk(Root, "");
        return result;
    }
}

/// <summary>
/// The merge every layer of a project goes through (DESIGN.md 6.5). A layer merges into the ones above it: a mapping by key (recursively, the nearer layer wins a conflict), a list by appending
/// (what is inherited first, a repeated scalar kept once; a list of mappings that have a `name` merges by `name`), a scalar by replacing. A suffix on a key changes that for the key alone:
/// `key=` replaces what was inherited, `key-` removes the listed items (or, for a mapping, the listed keys) from it, `key+` says "merge", which is the default. A suffix on a scalar other than `=`
/// is an error, and so is a key that is a list in one layer and a mapping in another. Problems are diagnostics, never exceptions.
/// </summary>
public static class YamlMerge
{
    /// <param name="layers">From the root down, the model's own file last.</param>
    /// <param name="layeredKeys">The top-level keys that inherit. A suffix is understood only on these (and on everything beneath them); on any other key it stays part of the name, so the usual unknown-key check reports it.</param>
    public static MergedYaml Merge(IReadOnlyList<YamlLayer> layers, IReadOnlySet<string> layeredKeys, List<Diagnostic> diags)
    {
        var fileOf = new Dictionary<YamlNode, string>(ReferenceEqualityComparer.Instance);
        var context = new Context(fileOf, diags);
        YamlMapping merged = new([], 1, 1);
        fileOf[merged] = layers.Count > 0 ? layers[^1].File : "";
        foreach (var layer in layers)
        {
            context.File = layer.File;
            merged = MergeTop(context, merged, layer, layeredKeys);
        }
        return new MergedYaml(merged, fileOf);
    }

    private sealed class Context(Dictionary<YamlNode, string> fileOf, List<Diagnostic> diags)
    {
        public string File { get; set; } = "";
        public Dictionary<YamlNode, string> FileOf => fileOf;

        public T Own<T>(T node) where T : YamlNode { fileOf[node] = File; return node; }

        public void Problem(YamlNode at, string found, string? fix = null) =>
            diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(File, at.Line, at.Column), found, Fix: fix));
    }

    private static YamlMapping MergeTop(Context cx, YamlMapping below, YamlLayer layer, IReadOnlySet<string> layeredKeys)
    {
        var entries = below.Entries.ToList();
        foreach (var e in layer.Root.Entries)
        {
            var (name, op) = Split(e.Key.Value);
            if (!layeredKeys.Contains(name)) { Put(cx, entries, e.Key, e.Value); continue; }   // not a layered key: the name is kept as written, and a suffix on it is an unknown key to the loader
            // a model has one kind, and its settings belong to it: a nearer layer's `kind` replaces the one above whole (merging `copy: {...}` into `view` would leave two kinds)
            if (name == "kind" && op == Op.Merge) op = Op.Replace;
            Apply(cx, entries, e, name, op);
        }
        return cx.Own(new YamlMapping(entries, below.Line, below.Column));
    }

    private static void Put(Context cx, List<YamlEntry> entries, YamlScalar key, YamlNode value)
    {
        var at = entries.FindIndex(x => x.Key.Value == key.Value);
        cx.Own(key); Claim(cx, value);
        if (at < 0) entries.Add(new YamlEntry(key, value)); else entries[at] = new YamlEntry(key, value);
    }

    private static void Claim(Context cx, YamlNode node)
    {
        if (cx.FileOf.ContainsKey(node)) return;
        cx.FileOf[node] = cx.File;
        switch (node)
        {
            case YamlSequence q: foreach (var i in q.Items) Claim(cx, i); break;
            case YamlMapping m: foreach (var x in m.Entries) { Claim(cx, x.Key); Claim(cx, x.Value); } break;
        }
    }

    private enum Op { Merge, Replace, Remove }

    /// <summary>`key=`, `key-` and `key+` give the name and what to do; `+` is the default spelled out.</summary>
    private static (string Name, Op Op) Split(string key) => key.Length > 1 ? key[^1] switch
    {
        '=' => (key[..^1], Op.Replace),
        '-' => (key[..^1], Op.Remove),
        '+' => (key[..^1], Op.Merge),
        _ => (key, Op.Merge),
    } : (key, Op.Merge);

    private static void Apply(Context cx, List<YamlEntry> entries, YamlEntry layerEntry, string name, Op op)
    {
        var at = entries.FindIndex(x => x.Key.Value == name);
        var keyNode = at >= 0 ? entries[at].Key : cx.Own(new YamlScalar(name, layerEntry.Key.Line, layerEntry.Key.Column));
        if (op == Op.Merge && layerEntry.Key.Value != name && layerEntry.Value is YamlScalar)
        {
            cx.Problem(layerEntry.Key, $"`{layerEntry.Key.Value}` has a suffix on a single value; only `{name}=` (replace) applies to one.", $"Write `{name}:` or `{name}=:`.");
            return;
        }
        YamlNode? inherited = at >= 0 ? entries[at].Value : null;
        var result = op switch
        {
            Op.Replace => Normalize(cx, layerEntry.Value),
            Op.Remove => Remove(cx, name, inherited, layerEntry.Value),
            _ => MergeValue(cx, name, inherited, layerEntry.Value),
        };
        if (result == null) return;
        if (at < 0) entries.Add(new YamlEntry(keyNode, result)); else entries[at] = new YamlEntry(keyNode, result);
    }

    /// <summary>A layer's value that has nothing above it: merged into nothing, so a `-` has nothing to remove and a `+` or `=` is just the value.</summary>
    private static YamlNode Normalize(Context cx, YamlNode value) => MergeValue(cx, "", null, value)!;

    private static YamlNode? MergeValue(Context cx, string name, YamlNode? inherited, YamlNode layer)
    {
        switch (layer)
        {
            case YamlMapping lm:
                if (inherited != null && inherited is not YamlMapping) return Mismatch(cx, name, layer, "a mapping", inherited);
                var entries = ((YamlMapping?)inherited)?.Entries.ToList() ?? [];
                foreach (var e in lm.Entries)
                {
                    var (n, op) = Split(e.Key.Value);
                    Apply(cx, entries, e, n, op);
                }
                return cx.Own(new YamlMapping(entries, lm.Line, lm.Column) { Flow = lm.Flow });

            case YamlSequence ls:
                if (inherited != null && inherited is not YamlSequence) return Mismatch(cx, name, layer, "a list", inherited);
                var items = ((YamlSequence?)inherited)?.Items.ToList() ?? [];
                var inheritedCount = items.Count;       // a repeat within the layer itself is its own mistake, left for the loader to report
                foreach (var item in ls.Items) AppendItem(cx, name, items, inheritedCount, item);
                return cx.Own(new YamlSequence(items, ls.Line, ls.Column) { Flow = ls.Flow });

            default:
                if (inherited is YamlMapping or YamlSequence) return Mismatch(cx, name, layer, "a single value", inherited);
                Claim(cx, layer);
                return layer;
        }
    }

    private static void AppendItem(Context cx, string name, List<YamlNode> items, int inheritedCount, YamlNode item)
    {
        var inherited = items.Take(inheritedCount).ToList();
        if (item is YamlScalar s)
        {
            if (!inherited.Any(x => x is YamlScalar o && o.Value == s.Value)) { Claim(cx, s); items.Add(s); }
            return;
        }
        if (item is YamlMapping m && m.Get("name") is YamlScalar itemName)
        {
            var at = inherited.FindIndex(x => x is YamlMapping o && o.Get("name") is YamlScalar n && n.Value == itemName.Value);
            if (at >= 0) { items[at] = MergeValue(cx, name, items[at], m) ?? items[at]; return; }
        }
        Claim(cx, item);
        items.Add(item);
    }

    private static YamlNode? Remove(Context cx, string name, YamlNode? inherited, YamlNode listed)
    {
        if (listed is not YamlSequence names || names.Items.Any(i => i is not YamlScalar))
        {
            cx.Problem(listed, $"`{name}-` takes a list of the items (or keys) to remove, for example `{name}-: [a, b]`.");
            return null;
        }
        var remove = names.Items.Cast<YamlScalar>().Select(s => s.Value).ToHashSet(StringComparer.Ordinal);
        switch (inherited)
        {
            case null: return null;
            case YamlSequence q:
                return cx.Own(new YamlSequence(q.Items.Where(i => !(i is YamlScalar s && remove.Contains(s.Value)) && !(i is YamlMapping m && m.Get("name") is YamlScalar n && remove.Contains(n.Value))).ToList(), q.Line, q.Column) { Flow = q.Flow });
            case YamlMapping m:
                return cx.Own(new YamlMapping(m.Entries.Where(e => !remove.Contains(e.Key.Value)).ToList(), m.Line, m.Column) { Flow = m.Flow });
            default:
                cx.Problem(listed, $"`{name}-` removes from a list or a mapping, and `{name}` is a single value.", $"Use `{name}=` to replace it.");
                return null;
        }
    }

    private static YamlNode? Mismatch(Context cx, string name, YamlNode layer, string layerIs, YamlNode inherited)
    {
        var inheritedIs = inherited switch { YamlMapping => "a mapping", YamlSequence => "a list", _ => "a single value" };
        cx.Problem(layer, $"`{name}` is {layerIs} here and {inheritedIs} in a file above it.", $"Use `{name}=` to replace what was inherited.");
        return null;
    }
}
