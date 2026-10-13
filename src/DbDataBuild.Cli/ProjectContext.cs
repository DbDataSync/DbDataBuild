using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

internal sealed record LoadedModel(ModelSource Source, string Sql);

/// <summary>Everything an offline command needs from a project: models, sources, config, the matrix and a renderer.</summary>
internal sealed class ProjectContext
{
    public required string Root { get; init; }
    public required ProjectValidationResult Project { get; init; }
    public required ProjectConfig Config { get; init; }
    public required SupportMatrix Matrix { get; init; }
    public required MatrixLinter Linter { get; init; }
    public required LoadRenderer Renderer { get; init; }
    public required List<Diagnostic> Diagnostics { get; init; }
    public required ModelLowering Lowering { get; init; }

    public static ProjectContext Load(string root)
    {
        var diags = new List<Diagnostic>();
        var project = ProjectValidator.Validate(root);
        diags.AddRange(project.Diagnostics);
        var config = ProjectConfigLoader.LoadFromProject(root, diags);

        var matrixDiags = new List<Diagnostic>();
        var matrix = MatrixLoader.LoadEmbedded(matrixDiags);
        if (matrixDiags.Count > 0) throw new InvalidOperationException("The embedded support matrix is invalid: " + string.Join("; ", matrixDiags.Select(d => d.Found)));
        var linter = new MatrixLinter(matrix);
        return new ProjectContext { Root = root, Project = project, Config = config, Matrix = matrix, Linter = linter, Renderer = new LoadRenderer(matrix, linter, config), Diagnostics = diags, Lowering = new ModelLowering(project.Models, project.AllDescriptors, config, project.Macros) };
    }

    /// <summary>
    /// Renders a model's load operations. With lowering on, the query is bound by DuckDB and lowered first, the matrix lint and the transpile work on the lowered query,
    /// and the lowered artifact is one of the files; a query that cannot be lowered renders nothing and says why (DDB-324).
    /// </summary>
    public (RenderResult Result, string BodySql) RenderModel(ModelSource source, string authorSql, IReadOnlyList<string> targets)
    {
        var variants = source.QueryVariants(Root, Config, targets);
        if (variants.Count == 1) return RenderVariant(source, variants[0].Variant == null ? authorSql : variants[0].Sql, variants[0].Targets, variants[0].Variant);
        // the model names something by a parameter that is not the same on all its connections: each group of connections that reads the same query is lowered and rendered on its own
        var files = new List<RenderedFile>();
        var operations = new List<OperationReport>();
        var diagnostics = new List<Diagnostic>();
        var loads = new List<RenderedOperation>();
        foreach (var v in variants)
        {
            var (part, _) = RenderVariant(source, v.Sql, v.Targets, v.Variant);
            files.AddRange(part.Files);
            operations.AddRange(part.Operations);
            diagnostics.AddRange(part.Diagnostics);
            loads.AddRange(part.Loads);
        }
        return (new RenderResult(files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), operations, diagnostics, loads), variants[0].Sql);
    }

    /// <summary>The distinct texts a model's query has on its connections (one, for a model that names nothing by a parameter).</summary>
    public IReadOnlyList<string> QueryVariants(ModelSource source) =>
        source.QueryVariants(Root, Config, TargetsOf(source.Definition)).Select(v => v.Sql).ToList();

    private (RenderResult Result, string BodySql) RenderVariant(ModelSource source, string authorSql, IReadOnlyList<string> targets, string? variant)
    {
        var parameters = source.QueryParameterList(Root, Config);
        if (!Lowering.Enabled) return (InputCompiler.Attach(this, source, Renderer.Render(source.Definition, authorSql, source.QueryFile, targets, queryParameters: parameters, natives: Lowering.NativeUsesFor(authorSql))), authorSql);
        var (lowered, error) = Lowering.Lower(source, authorSql, parameters, variant);
        if (lowered == null) return (new RenderResult([], [], [error!]), authorSql);
        var result = Renderer.Render(source.Definition, lowered.Sql, source.QueryFile, targets, bodyFile: $"rendered/{lowered.ArtifactPath}", queryParameters: parameters, natives: Lowering.NativeUsesFor(lowered.Sql));
        var files = result.Files.Append(new RenderedFile(lowered.ArtifactPath, lowered.ArtifactText)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return (InputCompiler.Attach(this, source, result with { Files = files }), lowered.Sql);
    }

    /// <summary>
    /// Where a copy reads from, for one destination connection: each connection of the model it copies (validation guarantees there is at least one and that the destination is not among them), its engine, the table there,
    /// and, for a copy with a slice, the value that tells that origin's rows apart (`${origin.name}` is the origin's parameter, `${connection.name}` the destination's). Empty for a model that is not a copy.
    /// </summary>
    public IReadOnlyList<DbDataBuild.Planning.CopyOrigin> OriginsOf(ModelDefinition model, string destination)
    {
        if (!model.IsCopy || model.LocalCopy || model.From == null) return [];
        var connections = Project.Models.FirstOrDefault(m => string.Equals(m.Name, model.From, StringComparison.OrdinalIgnoreCase)) is { } built
            ? built.Targets ?? Config.DefaultConnections
            : Project.FileDescriptors.FirstOrDefault(d => string.Equals(d.Name, model.From, StringComparison.OrdinalIgnoreCase))?.Connections ?? Config.DefaultConnections;
        var source = Project.Sources.FirstOrDefault(s => s.Definition.Name == model.Name);
        string? Value(string origin) => model.Slice == null || source == null ? model.Slice?.Value : ParameterReferences.Substitute(model.Slice.Value, (scope, name) => scope switch
        {
            "origin" => source.ParametersFor(Config, origin).GetValueOrDefault($"connection.{name}")?.Value,
            _ => source.ParametersFor(Config, destination).GetValueOrDefault($"{scope}.{name}")?.Value,
        });
        var native = Project.NativeModels.FirstOrDefault(n => string.Equals(n.Name, model.From, StringComparison.OrdinalIgnoreCase));
        var use = native == null ? null : NativeInline.Prepare(native, Config);
        return connections.Select(c => new DbDataBuild.Planning.CopyOrigin(c, Config.EngineOf(c) ?? c, model.From, Value(c), null, use, native == null ? null : NativeInline.Values(native, use!, Config, c))).ToList();
    }

    /// <summary>
    /// The tables a model's query reads. The names in its text, and, when it calls a macro of the project, the tables of the bound plan (DuckDB has expanded the macro by then, so a table that is only
    /// an argument, `snapshot_at('snap.orders', ...)`, is there). The macro's own name is not a table.
    /// </summary>
    public IReadOnlyList<string> BaseTablesOf(ModelSource source, string sql)
    {
        var macros = Project.Macros;
        var named = (QueryAnalyzer.Analyze(sql).Facts?.BaseTables.Select(t => t.QualifiedName) ?? []).ToList();
        var (called, mentioned) = macros.ReachedBy(sql);
        if (called.Count == 0 && mentioned.Count == 0) return named;
        var scanned = Lowering.TablesRead(source, sql, source.QueryParameterList(Root, Config)) ?? [];
        return named.Where(n => !called.Contains(n, StringComparer.OrdinalIgnoreCase)).Concat(scanned).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The text lineage and nullability are read from: the query as written, or, for one that calls macros, the lowered query (what DuckDB expanded the macros to), which is plain SQL on real tables, so the
    /// analyzer follows each output column through the macro to the column it comes from. Falls back to the written query when it cannot be lowered (its error is reported where it is lowered).
    /// </summary>
    public string AnalysisSql(ModelSource source, string sql)
    {
        if (!Lowering.Enabled || !Lowering.ReachesMacros(sql)) return sql;
        return Lowering.Lower(source, sql, source.QueryParameterList(Root, Config)).Model?.Sql ?? sql;
    }

    private static readonly System.Text.RegularExpressions.Regex SafeCollation = new(@"^[A-Za-z0-9_.\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A model run in DuckDB (`sample`, `test`) the way its connection compares strings (DESIGN.md 7.4): the profile of the model's first connection, as a `default_collation` for the query and, where the
    /// profile ignores trailing spaces, the lowered query with `rtrim()` where it matters. What was done is returned as notes, so a run never hides it. Falls back to the query as written, with a note, when it
    /// cannot be lowered.
    /// </summary>
    public (string Sql, string? Collation, IReadOnlyList<string> Notes) StringEmulation(ModelSource source, string valuesSql)
    {
        var connection = TargetsOf(source.Definition).FirstOrDefault();
        if (connection == null) return (valuesSql, null, []);
        var semantics = Config.SemanticsOf(connection);
        var notes = new List<string>();
        string? collation = null;
        if (semantics.Collations.TryGetValue("default", out var byEngine) && byEngine.TryGetValue("duckdb", out var name) && SafeCollation.IsMatch(name)) collation = name;
        var sql = valuesSql;
        var trailing = "";
        if (semantics.TrailingSpace == DbDataBuild.Models.TrailingSpace.Ignored)
        {
            if (!Lowering.Enabled) notes.Add("trailing spaces are not ignored in this run: lowering is off, so the query is run as written");
            else
            {
                var (lowered, error) = Lowering.LowerForDuckDb(source, valuesSql);
                if (lowered != null) { sql = lowered; trailing = "; trailing spaces ignored (rtrim in comparisons, joins, GROUP BY, DISTINCT, window partitions; not in set operations, count(DISTINCT) or LIKE)"; }
                else notes.Add($"trailing spaces are not ignored in this run: the query could not be lowered ({error})");
            }
        }
        notes.Insert(0, $"strings compare as on `{connection}` ({semantics.Describe()}): collation {collation ?? "none (binary)"}{trailing}");
        return (sql, collation, notes);
    }

    /// <summary>What binding a query that calls macros needs (null for one that does not): the macros it reaches and the tables its bound plan scans.</summary>
    public DbDataBuild.Define.MacroSupport? MacroSupportFor(ModelSource source, string sql)
    {
        var (called, mentioned) = Project.Macros.ReachedBy(sql);
        if (called.Count == 0 && mentioned.Count == 0) return null;
        var scanned = Lowering.TablesRead(source, sql, source.QueryParameterList(Root, Config)) ?? [];
        return new DbDataBuild.Define.MacroSupport(Project.Macros.PreludeFor([sql]), scanned, called, AnalysisSql(source, sql));
    }

    /// <summary>
    /// The hash that says whether a model's definition changed: the hash of its query's syntax, and, for a query that reaches macros, the text of those macros too (the query expands to something else when
    /// one changes, though its own text did not). A query that calls no macro has the plain hash, unchanged.
    /// </summary>
    public string DefinitionHashOf(string sql)
    {
        var hash = AstHasher.Hash(sql).Hash ?? "";
        var macros = Project.Macros.HashOf([sql]);
        return macros.Length == 0 ? hash : DbDataBuild.State.Hashing.ScriptHash(hash + "\n" + macros);
    }

    /// <summary>The macros a model's query calls directly, by the name its node has in the graph (`name()`).</summary>
    public IReadOnlyList<string> MacrosCalledBy(string sql) => Project.Macros.ReachedBy(sql).Macros.Select(MacroNode).ToList();

    public static string MacroNode(string name) => name + "()";

    /// <summary>The targets a model is built for: its own `connections:`, else the project default.</summary>
    public IReadOnlyList<string> TargetsOf(ModelDefinition model) => model.Targets ?? Config.DefaultConnections;

    /// <summary>
    /// What a query reads for ordering: the tables it names, and for each native model among them the tables that model declares under `reads:` (the native text is opaque, so the declaration is all the
    /// tool knows), so a model that reads a native select is built after the models inside it.
    /// </summary>
    public IReadOnlyList<string> WithNativeReads(IEnumerable<string> bases)
    {
        var result = new List<string>();
        foreach (var b in bases)
        {
            result.Add(b);
            if (Project.NativeModels.FirstOrDefault(n => string.Equals(n.Name, b, StringComparison.OrdinalIgnoreCase)) is { Native: { } native })
                result.AddRange(native.Reads);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private DependencyGraph? graph;

    /// <summary>Which tables each model reads, from parsing the queries (names only; no DuckDB, no target).</summary>
    public DependencyGraph Graph => graph ??= ProjectGraph.Build(this);

    /// <summary>
    /// The models the arguments select, as selectors (DESIGN.md 9.9): a model name, a `.sql` or `.yml` path, or a directory under models/, with the graph operators `+model`, `model+`, `2+model`, `model+1`, `@model`, the filters
    /// `kind:`, `tag:`, `property:key=value`, `connection:`, `path:` and `changed:<git ref>`, a comma for an intersection, and `exclude:<selector>` to take models out again. Several arguments are a union; every model when none are given.
    /// Returns null and writes the problem when a term selects nothing.
    /// </summary>
    public List<LoadedModel>? Select(IReadOnlyList<string> args, TextWriter error)
    {
        var all = Project.Sources.OrderBy(s => s.Definition.Name, StringComparer.Ordinal).ToList();
        var chosen = new List<string>();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includes = args.Where(a => !a.StartsWith("exclude:", StringComparison.Ordinal)).ToList();
        if (includes.Count == 0) chosen.AddRange(all.Select(s => s.Definition.Name));
        foreach (var arg in includes)
        {
            var set = SelectTerm(arg, all, error);
            if (set == null) return null;
            foreach (var name in set) if (!chosen.Contains(name, StringComparer.OrdinalIgnoreCase)) chosen.Add(name);
        }
        foreach (var arg in args.Where(a => a.StartsWith("exclude:", StringComparison.Ordinal)))
        {
            var set = SelectTerm(arg["exclude:".Length..], all, error);
            if (set == null) return null;
            excluded.UnionWith(set);
        }
        var byName = all.ToDictionary(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase);
        return chosen.Where(n => !excluded.Contains(n) && byName.ContainsKey(n)).Select(n => byName[n]).DistinctBy(s => s.Definition.Name)
            .Select(s => new LoadedModel(s, s.ReadQuery(Root, Config))).ToList();
    }

    /// <summary>One argument: terms joined by commas are intersected, each term is an optional operator around a core.</summary>
    private HashSet<string>? SelectTerm(string arg, List<ModelSource> all, TextWriter error)
    {
        HashSet<string>? result = null;
        foreach (var part in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var term = ModelSelector.Parse(part);
            var core = ResolveCore(term.Core, all, error);
            if (core == null) return null;
            var set = term is { Upstream: false, Downstream: false, At: false } ? new HashSet<string>(core, StringComparer.OrdinalIgnoreCase) : ModelSelector.Expand(term, core, Graph);
            if (result == null) result = set; else result.IntersectWith(set);
        }
        return result ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private List<string>? ResolveCore(string core, List<ModelSource> all, TextWriter error)
    {
        List<string>? Found(IEnumerable<ModelSource> models, string what)
        {
            var names = models.Select(s => s.Definition.Name).ToList();
            if (names.Count == 0) { error.WriteLine($"`{core}` selects no model ({what})."); return null; }
            return names;
        }
        if (core is "all" or "*") return all.Select(s => s.Definition.Name).ToList();
        if (core.StartsWith("kind:", StringComparison.Ordinal)) { var k = core[5..]; return Found(all.Where(s => string.Equals(s.Definition.KindType, k, StringComparison.OrdinalIgnoreCase)), $"no model has the kind `{k}`"); }
        if (core.StartsWith("tag:", StringComparison.Ordinal)) { var t = core[4..]; return Found(all.Where(s => s.Definition.Tags.Contains(t, StringComparer.Ordinal)), $"no model has the tag `{t}`"); }
        if (core.StartsWith("property:", StringComparison.Ordinal))
        {
            // `property:key=value` (the value as text, whatever its type; a comma cannot be in it) or `property:key` (has the property)
            var spec = core[9..]; var eq = spec.IndexOf('=');
            var (key, wanted) = eq < 0 ? (spec, null) : (spec[..eq], spec[(eq + 1)..]);
            return Found(all.Where(s => s.Definition.Info.Properties.TryGetValue(key, out var v) && (wanted == null || v.Text == wanted)), wanted == null ? $"no model has the property `{key}`" : $"no model has the property `{key}` with the value `{wanted}`");
        }
        if (core.StartsWith("connection:", StringComparison.Ordinal)) { var t = core[11..]; return Found(all.Where(s => TargetsOf(s.Definition).Contains(t, StringComparer.OrdinalIgnoreCase)), $"no model is built for `{t}`"); }
        if (core.StartsWith("path:", StringComparison.Ordinal)) { var d = core[5..].Replace('\\', '/').Trim('/'); return Found(all.Where(s => s.QueryFile.StartsWith(d + "/", StringComparison.Ordinal)), $"no model is under `{d}/`"); }
        if (core.StartsWith("changed:", StringComparison.Ordinal))
        {
            var (files, problem) = GitInfo.ChangedFiles(Root, core[8..]);
            if (files == null) { error.WriteLine(problem); return null; }
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (files.Contains(ProductInfo.ConfigFile)) changed.UnionWith(all.Select(s => s.Definition.Name));                    // the project settings reach every model
            foreach (var s in all.Where(s => files.Contains(s.QueryFile) || files.Contains(s.DefinitionFile))) changed.Add(s.Definition.Name);
            foreach (var d in Project.FileDescriptors.Where(d => files.Contains(d.File.Length > 0 ? d.File : ModelSourcePath(d.Name)))) changed.UnionWith(Graph.ReadBy(d.Name));      // a changed source changes what reads it
            changed.UnionWith(ChangedThroughOtherFiles(files, all));
            return changed.OrderBy(n => n, StringComparer.Ordinal).ToList();                                                          // nothing changed is a valid, empty answer
        }

        var normalized = core.Replace('\\', '/');
        var byName = all.Where(s => string.Equals(s.Definition.Name, core, StringComparison.OrdinalIgnoreCase)).ToList();
        var byPath = all.Where(s => normalized.EndsWith(s.QueryFile, StringComparison.Ordinal) || normalized.EndsWith(s.DefinitionFile, StringComparison.Ordinal) ||
                                    (Path.GetFullPath(Path.Combine(Root, s.QueryFile)) == Path.GetFullPath(core)) || (Path.GetFullPath(Path.Combine(Root, s.DefinitionFile)) == Path.GetFullPath(core))).ToList();
        var byDir = all.Where(s => normalized.TrimEnd('/') is var d && s.QueryFile.StartsWith(d + "/", StringComparison.Ordinal)).ToList();
        var found = byName.Count > 0 ? byName : byPath.Count > 0 ? byPath : byDir;
        if (found.Count == 0) { error.WriteLine($"`{core}` does not name a valid model, a model file, or a directory containing models.{(System.Text.RegularExpressions.Regex.IsMatch(core, "^[A-Za-z][A-Za-z0-9_]*$") ? $" (If it was meant to be a connection: the project's are {string.Join(", ", Config.Connections.Keys.Order(StringComparer.Ordinal))}.)" : "")}"); return null; }
        return found.Select(s => s.Definition.Name).ToList();
    }

    /// <summary>
    /// The models that a changed file reaches without being the model's own query or definition: a folder file (`_dbdatabuild.yml`: every model beneath it), the text of a native model, a hook script a model runs, a macro file
    /// (the models whose queries reach one of its macros), and a rendered file (the model it was rendered for).
    /// </summary>
    private HashSet<string> ChangedThroughOtherFiles(HashSet<string> files, List<ModelSource> all)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in files.Where(f => f.StartsWith(ProjectValidator.ModelsDir + "/", StringComparison.Ordinal) && f.EndsWith("/_dbdatabuild.yml", StringComparison.Ordinal)))
        {
            var dir = folder[..^"_dbdatabuild.yml".Length];
            result.UnionWith(all.Where(s => s.DefinitionFile.StartsWith(dir, StringComparison.Ordinal) || s.QueryFile.StartsWith(dir, StringComparison.Ordinal)).Select(s => s.Definition.Name));
        }
        foreach (var native in files.Where(f => f.EndsWith(".native.sql", StringComparison.Ordinal)))
        {
            var definition = native[..^".native.sql".Length] + ".yml";
            foreach (var d in Project.FileDescriptors.Where(d => string.Equals(d.File, definition, StringComparison.Ordinal))) result.UnionWith(Graph.ReadBy(d.Name));
        }
        foreach (var s in all)
        {
            var scripts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in TargetsOf(s.Definition))
                foreach (var hook in HookReader.Resolve(s.Definition, Config, target, s.DefinitionFile, []))
                    scripts.Add(hook.ScriptPath.Replace('\\', '/'));
            if (scripts.Overlaps(files)) result.Add(s.Definition.Name);
        }
        var changedMacroFiles = files.Where(f => f.StartsWith("macros/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (changedMacroFiles.Count > 0)
            foreach (var s in all)
                if (Project.Macros.Closure(s.QueryVariants(Root, Config, TargetsOf(s.Definition)).Select(v => v.Sql)).Any(m => changedMacroFiles.Contains(m.File.Replace('\\', '/')))) result.Add(s.Definition.Name);
        foreach (var f in files.Where(f => f.StartsWith("rendered/", StringComparison.Ordinal)))
        {
            var parts = f.Split('/');
            if (parts.Length >= 4) result.UnionWith(all.Where(s => string.Equals(s.Definition.Name, parts[2], StringComparison.OrdinalIgnoreCase)).Select(s => s.Definition.Name));
        }
        return result;
    }

    private static string ModelSourcePath(string name) => $"{ProjectValidator.ModelsDir}/{name.Replace('.', '/')}.yml";
}
