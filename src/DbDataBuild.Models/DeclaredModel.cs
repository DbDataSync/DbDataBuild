using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>
/// A **mapped** model: the declaration of a table that exists and that the tool does not build (<c>kind: mapped</c>, a <c>models/&lt;schema&gt;/&lt;table&gt;.yml</c> with no query). Models refer to
/// it by plain table name; <c>define</c> builds an empty DuckDB schema from it (DESIGN.md 6.5). Optional <c>grain</c> feeds
/// grain candidates. <c>indexes</c> and <c>foreign_keys</c> are what the table has, as exported by `import`; the tool reads them (metadata, advice) and never creates them.
/// </summary>
/// <param name="DeclaredConnections">The connections the table exists on (after the project files above it were merged in); null when nothing says, which is the project's default connections.</param>
public sealed record DeclaredModel(string Name, IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> Grain,
    IReadOnlyList<IndexDefinition>? DeclaredIndexes = null, IReadOnlyList<ForeignKeyDefinition>? DeclaredForeignKeys = null, IReadOnlyList<string>? DeclaredConnections = null, bool Generated = false, NativeQuery? Native = null, ObjectInfo? DeclaredInfo = null)
{
    /// <summary>The descriptor's title, description, tags and properties (tags and properties merged down the project files, as a model's are).</summary>
    public ObjectInfo Info => DeclaredInfo ?? ObjectInfo.Empty;

    /// <summary>True for a **native** model: its rows are computed by a query the engine runs (a table function, an engine-native select), not read from a table. Bound by queries like any table, from its declared columns.</summary>
    public bool IsNative => Native != null;

    /// <summary>The model kind of this descriptor, the word the metadata documents and tables carry: `mapped` or `native`.</summary>
    public string Kind => IsNative ? NativeQuery.Kind : DeclaredModelLoader.MappedKind;

    /// <summary>The definition file this descriptor was read from (project-relative; empty for one the tool generates). A model's name is not its path, so the file is kept.</summary>
    public string File { get; init; } = "";

    /// <summary>True for a table the tool declares itself, not a file of the project: the staging table a copy is read from (`CopyModels`). It is bound by queries like any mapped model and is left out of what is listed, imported and checked.</summary>
    public bool IsGenerated => Generated;

    public IReadOnlyList<string>? Connections => DeclaredConnections;
    public IReadOnlyList<IndexDefinition> Indexes => DeclaredIndexes ?? [];
    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys => DeclaredForeignKeys ?? [];
}

/// <summary>
/// The engine-native text of a native model (`kind: {native: {...}}`): written in the dialect of its one connection, never lowered or transpiled. <see cref="Access"/> is `select` (a query can contain it: it is
/// inlined as a derived table) or `command` (it can only be run; not built yet).
/// </summary>
/// <param name="Reads">The tables the text reads (optional): how a native model takes part in the dependency graph.</param>
/// <param name="Parameters">The model's own parameters (`${model.x}` in the text).</param>
public sealed record NativeQuery(string Access, string Text, IReadOnlyList<string> Reads, IReadOnlyDictionary<string, ParameterValue> Parameters, int Line = 0, string File = "")
{
    public const string Select = "select";
    public const string Command = "command";
    public const string Kind = "native";

    /// <summary>The routines the text depends on whose definitions the tool watches (`track_definition:`): `schema_name.object_name`, and on PostgreSQL the argument types of an overloaded function (`public.fn(date)`).</summary>
    public IReadOnlyList<string> TrackDefinition { get; init; } = [];

    /// <summary>A routine name as `track_definition` takes it: dotted identifiers, with PostgreSQL's argument types in parentheses.</summary>
    public static bool IsRoutineName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_$]*(\.[A-Za-z_][A-Za-z0-9_$]*){0,2}(\([A-Za-z0-9_ ,\[\]]*\))?$");

    /// <summary>The project parameters this model's file sees (set by the validator, like a model's).</summary>
    public IReadOnlyDictionary<string, ParameterValue> ProjectParameters { get; init; } = new Dictionary<string, ParameterValue>();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ParameterValue>> ConnectionParameterOverrides { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, ParameterValue>>();

    public IReadOnlyDictionary<string, ParameterValue> ParametersFor(ProjectConfig config, string connection) =>
        ParameterReferences.For(config, connection, ProjectParameters, ConnectionParameterOverrides, Parameters);
}

/// <summary>A foreign key of a source table: its columns, the table it points at (`schema_name.table_name`) and that table's columns, in the same order.</summary>
public sealed record ForeignKeyDefinition(string Name, IReadOnlyList<string> Columns, string Table, IReadOnlyList<string> ReferencedColumns, int Line = 0);

public static class DeclaredModelLoader
{
    private static readonly string[] Keys = ["name", "kind", "connections", "columns", "grain", "indexes", "foreign_keys", "reads", "parameters", "track_definition", "refresh", .. YamlFieldReader.InfoKeys];

    /// <summary>The kind of a model that maps an existing table: no query, nothing built, everything declared.</summary>
    public const string MappedKind = "mapped";

    /// <summary>Reads a mapped model from its own file alone (the layers above it, if any, are merged by <see cref="LoadMerged"/>).</summary>
    public static DeclaredModel? Load(string text, string file, string? expectedName, List<Diagnostic> diags, IReadOnlySet<string>? connections = null)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null)
        {
            if (diags.Count == before) diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(file, 1, 1), "The file is empty. Required keys: name, kind, columns."));
            return null;
        }
        var known = connections ?? TargetNames.All.ToHashSet(StringComparer.Ordinal);
        if (root is not YamlMapping own) { new Reader(file, diags, known).Read(root, expectedName); return null; }
        var merged = YamlMerge.Merge([new YamlLayer(file, own)], ModelDefinitionLoader.LayeredKeys, diags);
        return diags.Count > before ? null : LoadMerged(merged, file, expectedName, diags, known);
    }

    /// <summary>Reads a mapped model that was merged from the project files above it and its own file (`kind` and `connections` may come from a folder).</summary>
    public static DeclaredModel? LoadMerged(MergedYaml merged, string file, string? expectedName, List<Diagnostic> diags, IReadOnlySet<string>? connections = null, string? nativeFile = null)
    {
        var before = diags.Count;
        var result = new Reader(file, diags, connections ?? TargetNames.All.ToHashSet(StringComparer.Ordinal), nativeFile) { NodeFiles = merged.FileOf }.Read(merged.Root, expectedName);
        return diags.Count > before ? null : result;
    }

    private sealed class Reader(string file, List<Diagnostic> diags, IReadOnlySet<string> knownConnections, string? nativeFile = null) : YamlFieldReader(file, diags)
    {
        public DeclaredModel? Read(YamlNode root, string? expectedName)
        {
            if (root is not YamlMapping top) { Add(DiagnosticCatalog.InvalidValue, root, "A mapped model must be a mapping with `name`, `kind` and `columns`."); return null; }
            CheckKeys(top, Keys, "a mapped or native model");
            var reading = ModelKindShape.Read(top.Get("kind"), top, (d, n, f, fix) => Add(d, n, f, fix: fix));
            var isNative = reading?.Name == NativeQuery.Kind;
            NativeQuery? native = null;
            if (isNative) native = ReadNative(top, reading!);
            else if (reading != null)
            {
                if (reading.Name != MappedKind)
                    Add(DiagnosticCatalog.InvalidValue, reading.NameNode, $"A model with no query is `kind: mapped` or `kind: native`, not `{reading.Name}`.");
                else if (reading.Settings.Entries.Count > 0)
                    Add(DiagnosticCatalog.InvalidValue, reading.Settings.Entries[0].Key, "A mapped model's kind is `kind: mapped` and nothing else.");
                foreach (var k in new[] { "reads", "parameters", "track_definition" }.Where(k => top.Get(k) != null))
                    Add(DiagnosticCatalog.UnknownKey, top.Entries.First(e => e.Key.Value == k).Key, $"`{k}` belongs to a native model; a mapped model declares a table that exists.");
            }
            var connections = StringList(top, "connections", required: false, allowEmpty: false, unique: true);
            foreach (var c in (connections ?? []).Where(c => !knownConnections.Contains(c.Value)))
                Add(DiagnosticCatalog.InvalidValue, c, $"Unknown connection `{c.Value}`.", $"One of: {string.Join(", ", knownConnections.Order(StringComparer.Ordinal))}.");
            var name = Scalar(top, "name", required: true, at: top);
            if (name != null && expectedName != null && name.Value != expectedName)
                Add(DiagnosticCatalog.NameMismatch, name, $"name is `{name.Value}`, but the path implies `{expectedName}`.", fix: $"Change `name:` to `{expectedName}`, or move the file.");
            var grain = StringList(top, "grain", required: false, allowEmpty: false, unique: true);
            var columns = ReadColumns(top);
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in grain ?? [])
                if (columns.Count > 0 && !declared.Contains(g.Value))
                    Add(DiagnosticCatalog.UnknownColumnReference, g, $"grain refers to `{g.Value}`, which is not declared in `columns`.");
            var indexes = ReadIndexes(top, columns);
            var foreignKeys = ReadForeignKeys(top, columns);
            var info = ReadInfo(top);
            return name == null ? null : new DeclaredModel(name.Value, columns, grain?.Select(g => g.Value).ToList() ?? [], indexes, foreignKeys, connections?.Select(c => c.Value).ToList(), Native: native, DeclaredInfo: info);
        }

        /// <summary>`kind: {native: {access, query}}` and the model's `reads` and `parameters`. The text is `query:` or the `.native.sql` file beside the definition, never both.</summary>
        private NativeQuery? ReadNative(YamlMapping top, KindReading reading)
        {
            var kind = reading.Settings;
            CheckKeys(kind, ["access", "query"], "kind `native`");
            var access = (kind.Get("access") as YamlScalar)?.Value ?? NativeQuery.Select;
            if (access is not (NativeQuery.Select or NativeQuery.Command)) Add(DiagnosticCatalog.InvalidValue, kind.Get("access") ?? kind, $"`access` is `select` (a query can contain it) or `command` (it can only be run), not `{access}`.");
            var inline = (kind.Get("query") as YamlScalar)?.Value;
            string? text = inline;
            if (kind.Get("query") != null && inline == null) Add(DiagnosticCatalog.InvalidValue, kind.Get("query")!, "`query` is the text of the native query.");
            if (inline != null && nativeFile != null) Add(DiagnosticCatalog.InvalidValue, kind.Get("query")!, "A native model has its text in `query:` or in its `.native.sql` file, not both.");
            else if (inline == null) text = nativeFile;
            if (string.IsNullOrWhiteSpace(text)) { Add(DiagnosticCatalog.MissingKey, reading.NameNode, "A native model needs its text: `query:` under `native:`, or a `.native.sql` file beside the definition."); return null; }
            if (access == NativeQuery.Select && ReadGuard.Check(text) is { } refused) Add(DiagnosticCatalog.InvalidValue, (YamlNode?)kind.Get("query") ?? kind, $"The native text is not a single SELECT: {refused.Found}");
            if (access == NativeQuery.Command && CommandProblem(text) is { } bad) Add(DiagnosticCatalog.InvalidValue, (YamlNode?)kind.Get("query") ?? kind, bad);
            var reads = StringList(top, "reads", required: false, allowEmpty: false, unique: true)?.Select(r => r.Value).ToList() ?? [];
            var own = top.Get("parameters") is { } pn ? ParameterReferences.Read(pn, "`parameters`", (d, n, f) => Add(d, n, f)) : null;
            var tracked = StringList(top, "track_definition", required: false, allowEmpty: false, unique: true) ?? [];
            foreach (var t in tracked.Where(t => !NativeQuery.IsRoutineName(t.Value)))
                Add(DiagnosticCatalog.InvalidValue, t, $"`{t.Value}` is not a routine name: write `schema_name.object_name`, and for an overloaded PostgreSQL function its argument types, `public.fn(date)`.");
            return new NativeQuery(access, text.Trim(), reads, own ?? new Dictionary<string, ParameterValue>(), kind.Line) { TrackDefinition = tracked.Select(t => t.Value).ToList() };
        }

        /// <summary>A command is one statement that calls something and returns rows: `EXEC proc ...`, `CALL proc(...)` (or a SELECT of a function). Anything longer is a script, and is refused.</summary>
        private static string? CommandProblem(string text)
        {
            var t = text.Trim().TrimEnd(';').Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"^(EXEC|EXECUTE|CALL|SELECT)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "A native command is `EXEC procedure ...` or `CALL procedure(...)` (or a SELECT of a function): one call that returns rows.";
            return ReadGuard.Strip(t).Contains(';') ? "A native command is one statement: it contains a `;`." : null;
        }

        private List<IndexDefinition> ReadIndexes(YamlMapping top, List<ColumnDefinition> columns)
        {
            var result = new List<IndexDefinition>();
            if (top.Get("indexes") is not { } node) return result;
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, "`indexes` must be a list."); return result; }
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, item, "Each index must be a mapping with `name` and `columns`."); continue; }
                CheckKeys(m, ["name", "columns", "unique", "include", .. InfoKeys], "an index of a source");
                var name = Scalar(m, "name", required: true, at: m);
                var cols = StringList(m, "columns", required: true, allowEmpty: false, unique: true);
                var include = StringList(m, "include", required: false, allowEmpty: false, unique: true);
                var unique = false;
                if (Scalar(m, "unique", required: false, at: m) is { } u)
                {
                    if (u.Value is "true" or "false") unique = u.Value == "true";
                    else Add(DiagnosticCatalog.InvalidValue, u, $"unique is `{u.Value}`.", "true or false (lowercase).");
                }
                // the name is whatever the table's index is called: it is not a name this tool creates, so it is not held to an identifier pattern
                if (name != null && !names.Add(name.Value)) Add(DiagnosticCatalog.DuplicateKey, name, $"The index name `{name.Value}` is used more than once.");
                foreach (var c in (cols ?? []).Concat(include ?? []).Where(c => columns.Count > 0 && !declared.Contains(c.Value)))
                    Add(DiagnosticCatalog.UnknownColumnReference, c, $"Index `{name?.Value}` refers to `{c.Value}`, which is not declared in `columns`.");
                if (name != null && cols != null) result.Add(new IndexDefinition(name.Value, cols.Select(c => c.Value).ToList(), unique, include?.Select(c => c.Value).ToList() ?? [], null, m.Line, ReadInfo(m)));
            }
            return result;
        }

        private List<ForeignKeyDefinition> ReadForeignKeys(YamlMapping top, List<ColumnDefinition> columns)
        {
            var result = new List<ForeignKeyDefinition>();
            if (top.Get("foreign_keys") is not { } node) return result;
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, "`foreign_keys` must be a list."); return result; }
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, item, "Each foreign key must be a mapping with `name`, `columns` and `references`."); continue; }
                CheckKeys(m, ["name", "columns", "references"], "a foreign key");
                var name = Scalar(m, "name", required: true, at: m);
                var cols = StringList(m, "columns", required: true, allowEmpty: false, unique: true);
                string? table = null;
                List<YamlScalar>? refCols = null;
                if (m.Get("references") is not { } r) Add(DiagnosticCatalog.MissingKey, m, "Required key `references` is missing.", fix: "Add `references: {table: schema_name.table_name, columns: [...]}`.");
                else if (r is not YamlMapping rm) Add(DiagnosticCatalog.InvalidValue, r, "`references` must be a mapping with `table` and `columns`.");
                else
                {
                    CheckKeys(rm, ["table", "columns"], "`references`");
                    table = Scalar(rm, "table", required: true, at: rm)?.Value;
                    refCols = StringList(rm, "columns", required: true, allowEmpty: false, unique: true);
                }
                if (name != null && !names.Add(name.Value)) Add(DiagnosticCatalog.DuplicateKey, name, $"The foreign key name `{name.Value}` is used more than once.");
                foreach (var c in (cols ?? []).Where(c => columns.Count > 0 && !declared.Contains(c.Value)))
                    Add(DiagnosticCatalog.UnknownColumnReference, c, $"Foreign key `{name?.Value}` refers to `{c.Value}`, which is not declared in `columns`.");
                if (cols != null && refCols != null && cols.Count != refCols.Count)
                    Add(DiagnosticCatalog.InvalidValue, m, $"Foreign key `{name?.Value}` has {cols.Count} column(s) and references {refCols.Count}.");
                if (name != null && cols != null && table != null && refCols != null)
                    result.Add(new ForeignKeyDefinition(name.Value, cols.Select(c => c.Value).ToList(), table, refCols.Select(c => c.Value).ToList(), m.Line));
            }
            return result;
        }
    }
}

/// <summary>Writes a mapped model in canonical formatting: name, grain, columns; <c>nullable</c> only when false (the same style as definitions).</summary>
public static class DeclaredModelWriter
{
    /// <param name="withInfo">False for the text the definition hash is made of: what a person says about an object in words is not part of what the object is.</param>
    public static string Yaml(DeclaredModel d, bool withInfo = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("name: ").Append(YamlText.Scalar(d.Name)).Append('\n');
        if (withInfo) sb.Append(YamlText.InfoLines(d.DeclaredInfo, ""));
        sb.Append("kind: mapped\n");
        if (d.Connections is { Count: > 0 }) sb.Append("connections=: ").Append(YamlText.FlowList(d.Connections)).Append('\n');
        if (d.Grain.Count > 0) sb.Append("grain: ").Append(YamlText.FlowList(d.Grain)).Append('\n');
        sb.Append("columns:\n");
        foreach (var c in d.Columns)
        {
            sb.Append("  - name: ").Append(YamlText.Scalar(c.Name)).Append('\n');
            sb.Append("    type: ").Append(YamlText.Scalar(c.Type)).Append('\n');
            if (!c.Nullable) sb.Append("    nullable: false\n");
            if (c.Collation != null) sb.Append("    collation: ").Append(YamlText.Scalar(c.Collation)).Append('\n');
            if (c.Trimmed) sb.Append("    trimmed: true\n");
            if (withInfo) sb.Append(YamlText.InfoLines(c.Info, "    "));
        }
        if (d.Indexes.Count > 0)
        {
            sb.Append("indexes:\n");
            foreach (var i in d.Indexes)
            {
                if (withInfo && i.Info is { IsEmpty: false })
                {
                    sb.Append("  - name: ").Append(YamlText.Scalar(i.Name)).Append("\n    columns: ").Append(YamlText.FlowList(i.Columns)).Append('\n');
                    if (i.Unique) sb.Append("    unique: true\n");
                    if (i.Include.Count > 0) sb.Append("    include: ").Append(YamlText.FlowList(i.Include)).Append('\n');
                    sb.Append(YamlText.InfoLines(i.Info, "    "));
                    continue;
                }
                sb.Append("  - {name: ").Append(YamlText.FlowScalar(i.Name)).Append(", columns: ").Append(YamlText.FlowList(i.Columns));
                if (i.Unique) sb.Append(", unique: true");
                if (i.Include.Count > 0) sb.Append(", include: ").Append(YamlText.FlowList(i.Include));
                sb.Append("}\n");
            }
        }
        if (d.ForeignKeys.Count > 0)
        {
            sb.Append("foreign_keys:\n");
            foreach (var f in d.ForeignKeys)
                sb.Append("  - {name: ").Append(YamlText.FlowScalar(f.Name)).Append(", columns: ").Append(YamlText.FlowList(f.Columns))
                  .Append(", references: {table: ").Append(YamlText.FlowScalar(f.Table)).Append(", columns: ").Append(YamlText.FlowList(f.ReferencedColumns)).Append("}}\n");
        }
        return sb.ToString();
    }

    /// <summary>The project-relative path of the descriptor for a table (`staging.orders` is `models/staging/orders.yml`), or null when the name cannot be a path (a dot, slash or backslash inside the schema name or table name).</summary>
    public static string? PathFor(string schema, string table, ModelLayout layout = ModelLayout.Folder)
    {
        static bool Bad(string s) => s.Length == 0 || s.AsSpan().IndexOfAny('.', '/', '\\') >= 0 || s.Trim() != s;
        if (Bad(schema) || Bad(table)) return null;
        return layout switch
        {
            ModelLayout.Dotted => $"{ProjectValidator.ModelsDir}/{schema}.{table}.yml",
            ModelLayout.Object => $"{ProjectValidator.ModelsDir}/{table}.yml",
            _ => $"{ProjectValidator.ModelsDir}/{schema}/{table}.yml",
        };
    }
}
