using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>Shared field-reading helpers for definition and configuration loaders. Problems become diagnostics at the node's position.</summary>
internal abstract class YamlFieldReader(string file, List<Diagnostic> diags)
{
    /// <summary>The four keys every described object may carry (<see cref="ObjectInfo"/>): a reader of such an object adds them to the keys it allows and calls <see cref="ReadInfo"/>.</summary>
    internal static readonly string[] InfoKeys = ["title", "description", "tags", "properties"];

    private static readonly string[] ColumnKeys = ["name", "type", "nullable", "collation", "trimmed", .. InfoKeys];

    /// <summary>For a mapping merged from several files: the file each node was read from, so a diagnostic names the file the problem is in.</summary>
    public IReadOnlyDictionary<YamlNode, string>? NodeFiles { get; init; }

    protected string File => file;
    protected List<Diagnostic> Diagnostics => diags;

    protected void Add(DiagnosticDescriptor d, YamlNode at, string found, string? supported = null, string? fix = null) =>
        diags.Add(new Diagnostic(d, new(NodeFiles != null && NodeFiles.TryGetValue(at, out var origin) ? origin : file, at.Line, at.Column), found, supported, fix));

    protected void CheckKeys(YamlMapping map, IEnumerable<string> allowed, string where)
    {
        var allowedList = allowed.ToList();
        foreach (var e in map.Entries.Where(e => !allowedList.Contains(e.Key.Value)))
            Add(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in {where}.", $"Keys: {string.Join(", ", allowedList)}.");
    }

    protected YamlScalar? Scalar(YamlMapping map, string key, bool required, YamlNode at)
    {
        var node = map.Get(key);
        if (node == null)
        {
            if (required) Add(DiagnosticCatalog.MissingKey, at, $"Required key `{key}` is missing.", fix: $"Add `{key}:`.");
            return null;
        }
        if (node is YamlScalar s && s.Value.Length > 0) return s;
        Add(DiagnosticCatalog.InvalidValue, node, $"`{key}` must be a non-empty string.");
        return null;
    }

    /// <summary>Reads a list of non-empty strings. An empty list is reported as invalid unless <paramref name="allowEmpty"/>.</summary>
    protected List<YamlScalar>? StringList(YamlMapping map, string key, bool required, bool allowEmpty = true, bool unique = false)
    {
        var node = map.Get(key);
        if (node == null)
        {
            if (required) Add(DiagnosticCatalog.MissingKey, map, $"Required key `{key}` is missing.");
            return null;
        }
        if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, $"`{key}` must be a list, for example `{key}: [a, b]`."); return null; }
        var list = new List<YamlScalar>();
        foreach (var item in seq.Items)
        {
            if (item is YamlScalar s && s.Value.Length > 0) list.Add(s);
            else Add(DiagnosticCatalog.InvalidValue, item, $"Entries of `{key}` must be non-empty strings.");
        }
        if (!allowEmpty && seq.Items.Count == 0) Add(DiagnosticCatalog.InvalidValue, seq, $"`{key}` must not be an empty list.");
        if (unique)
            foreach (var dup in list.GroupBy(x => x.Value).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)))
                Add(DiagnosticCatalog.InvalidValue, dup, $"`{dup.Value}` is listed more than once in `{key}`.");
        return list;
    }

    /// <summary>
    /// Reads `title`, `description`, `tags` and `properties` of a mapping, or null when it has none of them. Every problem is a diagnostic at the node: a value of the wrong shape is DDB-106, one past a limit
    /// or a key or tag of the wrong form is DDB-243.
    /// </summary>
    protected ObjectInfo? ReadInfo(YamlMapping map)
    {
        var title = Scalar(map, "title", required: false, at: map);
        if (title != null && (title.Value.Length > InfoLimits.TitleLength || title.Value.Contains('\n')))
            Add(DiagnosticCatalog.InfoOutOfBounds, title, title.Value.Contains('\n') ? "A title is one line." : $"A title is at most {InfoLimits.TitleLength} characters; this one has {title.Value.Length}.");
        var description = Scalar(map, "description", required: false, at: map);
        if (description != null && description.Value.Length > InfoLimits.DescriptionLength)
            Add(DiagnosticCatalog.InfoOutOfBounds, description, $"A description is at most {InfoLimits.DescriptionLength} characters; this one has {description.Value.Length}.");
        var tags = ReadTags(map);
        var properties = ReadProperties(map);
        var info = new ObjectInfo(title?.Value, description?.Value, tags.Select(t => t.Value).ToList(), properties);
        return info.IsEmpty ? null : info;
    }

    /// <summary>`tags:` as a list of words (letters, digits, `_`, `-`, `.` and `:`), each at most <see cref="InfoLimits.TagLength"/> characters. Empty when the key is absent.</summary>
    protected List<YamlScalar> ReadTags(YamlMapping map)
    {
        var tags = StringList(map, "tags", required: false, allowEmpty: false, unique: true) ?? [];
        foreach (var tag in tags)
        {
            if (!InfoLimits.TagForm.IsMatch(tag.Value)) Add(DiagnosticCatalog.InvalidValue, tag, $"`{tag.Value}` is not a tag.", "Letters, digits, `_`, `-`, `.` and `:`, starting with a letter, digit or `_`.");
            else if (tag.Value.Length > InfoLimits.TagLength) Add(DiagnosticCatalog.InfoOutOfBounds, tag, $"A tag is at most {InfoLimits.TagLength} characters; `{tag.Value[..20]}...` has {tag.Value.Length}.");
        }
        return tags;
    }

    /// <summary>
    /// `properties:` as a mapping of keys to single values (text, a number, or true or false; see <see cref="PropertyValue"/>). A key is at most <see cref="InfoLimits.PropertyKeyLength"/> characters of the form of a tag that does not
    /// end in `-`; a value is at most <see cref="InfoLimits.PropertyValueLength"/>. Empty when the key is absent.
    /// </summary>
    protected Dictionary<string, PropertyValue> ReadProperties(YamlMapping map)
    {
        var result = new Dictionary<string, PropertyValue>(StringComparer.Ordinal);
        if (map.Get("properties") is not { } node) return result;
        if (node is not YamlMapping props) { Add(DiagnosticCatalog.InvalidValue, node, "`properties` must be a mapping of keys to values, for example `properties: { owner: finance, tier: 1 }`."); return result; }
        foreach (var e in props.Entries)
        {
            var key = e.Key.Value;
            if (!InfoLimits.PropertyKeyForm.IsMatch(key))
            { Add(DiagnosticCatalog.InfoOutOfBounds, e.Key, $"`{(key.Length > 40 ? key[..40] + "..." : key)}` is not a property key.", "Letters, digits, `_`, `-`, `.` and `:`, starting with a letter or `_` and not ending in `-`."); continue; }
            if (key.Length > InfoLimits.PropertyKeyLength)
            { Add(DiagnosticCatalog.InfoOutOfBounds, e.Key, $"A property key is at most {InfoLimits.PropertyKeyLength} characters; `{key[..20]}...` has {key.Length}."); continue; }
            if (e.Value is not YamlScalar value)
            { Add(DiagnosticCatalog.InvalidValue, e.Value, $"The property `{key}` must be one value: text, a number, or true or false."); continue; }
            if (!value.Quoted && (value.Value.Length == 0 || value.Value is "null" or "~"))
            { Add(DiagnosticCatalog.InvalidValue, value, $"The property `{key}` has no value.", "Write the value, or `\"\"` for empty text; to leave a property out, remove it."); continue; }
            if (value.Value.Length > InfoLimits.PropertyValueLength)
            { Add(DiagnosticCatalog.InfoOutOfBounds, value, $"A property value is at most {InfoLimits.PropertyValueLength} characters; the value of `{key}` has {value.Value.Length}."); continue; }
            result[key] = PropertyValue.From(value.Value, value.Quoted);
        }
        return result;
    }

    /// <summary>Reads `rewrites:` (project or model): `fidelity: exact | native`, `disable: [names]`, `enable: [names]`. A name that is not a rewrite is reported with the list of names.</summary>
    protected RewriteSettings? ReadRewrites(YamlMapping top)
    {
        if (top.Get("rewrites") is not { } node) return null;
        if (node is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, node, "`rewrites` must be a mapping, for example `rewrites: { fidelity: native }`."); return null; }
        CheckKeys(m, ["fidelity", "disable", "enable"], "`rewrites`");
        string? fidelity = null;
        if (Scalar(m, "fidelity", required: false, at: m) is { } f)
        {
            if (f.Value is RewriteSettings.Exact or RewriteSettings.Native) fidelity = f.Value;
            else Add(DiagnosticCatalog.InvalidValue, f, $"`fidelity` is `{f.Value}`.", "exact (the engines are made to give DuckDB's answers) or native (the engines' own behavior).");
        }
        List<string> Names(string key)
        {
            var result = new List<string>();
            foreach (var n in StringList(m, key, required: false, unique: true) ?? [])
            {
                if (RewriteCatalog.Find(n.Value) == null) Add(DiagnosticCatalog.InvalidValue, n, $"`{n.Value}` is not a rewrite.", $"One of: {string.Join(", ", RewriteCatalog.Names)}.");
                else result.Add(n.Value);
            }
            return result;
        }
        var disable = Names("disable");
        var enable = Names("enable");
        foreach (var both in disable.Intersect(enable)) Add(DiagnosticCatalog.InvalidValue, m, $"`{both}` is in both `disable` and `enable`.");
        return new RewriteSettings(fidelity, disable, enable, m.Line);
    }

    /// <summary>Reads a required, non-empty `columns:` list (name, type, nullable, collation), reporting every problem.</summary>
        protected List<ColumnDefinition> ReadColumns(YamlMapping top)
        {
            var result = new List<ColumnDefinition>();
            var node = top.Get("columns");
            if (node == null) { Add(DiagnosticCatalog.MissingKey, top, "Required key `columns` is missing. Declared columns are required for all models.", fix: "Add `columns:` listing each output column with `name:` and `type:`."); return result; }
            if (node is not YamlSequence seq || seq.Items.Count == 0) { Add(DiagnosticCatalog.InvalidValue, node, "`columns` must be a non-empty list."); return result; }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping col) { Add(DiagnosticCatalog.InvalidValue, item, "Each column must be a mapping with `name` and `type`."); continue; }
                CheckKeys(col, ColumnKeys, "a column");
                var n = Scalar(col, "name", required: true, at: col);
                var t = Scalar(col, "type", required: true, at: col);
                var nullable = true;
                if (Scalar(col, "nullable", required: false, at: col) is { } nb)
                {
                    if (nb.Value is "true" or "false") nullable = nb.Value == "true";
                    else Add(DiagnosticCatalog.InvalidValue, nb, $"nullable is `{nb.Value}`.", "true or false (lowercase).");
                }
                var trimmed = false;
                if (Scalar(col, "trimmed", required: false, at: col) is { } tb)
                {
                    if (tb.Value is "true" or "false") trimmed = tb.Value == "true";
                    else Add(DiagnosticCatalog.InvalidValue, tb, $"trimmed is `{tb.Value}`.", "true or false (lowercase).");
                }
                if (n != null && !names.Add(n.Value))
                    Add(DiagnosticCatalog.DuplicateKey, n, $"Column `{n.Value}` is declared more than once.");
                var info = ReadInfo(col);
                if (n != null && t != null) result.Add(new ColumnDefinition(n.Value, t.Value, nullable, (col.Get("collation") as YamlScalar)?.Value, n.Line, (col.Get("collation") as YamlScalar)?.Line ?? 0, trimmed, info));
            }
            return result;
        }
}
