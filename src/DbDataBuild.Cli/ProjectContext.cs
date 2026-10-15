using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

internal sealed record LoadedModel(BuiltModel Model, string Sql);

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
        return new ProjectContext { Root = root, Project = project, Config = config, Matrix = matrix, Linter = linter, Renderer = new LoadRenderer(matrix, linter, config), Diagnostics = diags, Lowering = new ModelLowering(project.Models, project.DeclaredModelsAndStaging, config, project.Macros) };
    }

    /// <summary>
    /// Renders a model's load operations. With lowering on, the query is bound by DuckDB and lowered first, the matrix lint and the transpile work on the lowered query,
    /// and the lowered artifact is one of the files; a query that cannot be lowered renders nothing and says why (DDB-324).
    /// </summary>
    public (RenderResult Result, string BodySql) RenderModel(BuiltModel built, string authorSql, IReadOnlyList<string> targets)
    {
        var variants = built.QueryVariants(Root, Config, targets);
        if (variants.Count == 1) return RenderVariant(built, variants[0].Variant == null ? authorSql : variants[0].Sql, variants[0].Targets, variants[0].Variant);
        // the model names something by a parameter that is not the same on all its connections: each group of connections that reads the same query is lowered and rendered on its own
        var files = new List<RenderedFile>();
        var operations = new List<OperationReport>();
        var diagnostics = new List<Diagnostic>();
        var loads = new List<RenderedOperation>();
        foreach (var v in variants)
        {
            var (part, _) = RenderVariant(built, v.Sql, v.Targets, v.Variant);
            files.AddRange(part.Files);
            operations.AddRange(part.Operations);
            diagnostics.AddRange(part.Diagnostics);
            loads.AddRange(part.Loads);
        }
        return (new RenderResult(files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), operations, diagnostics, loads), variants[0].Sql);
    }

    /// <summary>The distinct texts a model's query has on its connections (one, for a model that names nothing by a parameter).</summary>
    public IReadOnlyList<string> QueryVariants(BuiltModel built) =>
        built.QueryVariants(Root, Config, TargetsOf(built.Definition)).Select(v => v.Sql).ToList();

    private (RenderResult Result, string BodySql) RenderVariant(BuiltModel built, string authorSql, IReadOnlyList<string> targets, string? variant)
    {
        var parameters = built.QueryParameterList(Root, Config);
        if (!Lowering.Enabled) return (InputCompiler.Attach(this, built, Renderer.Render(built.Definition, authorSql, built.QueryFile, targets, queryParameters: parameters, natives: Lowering.NativeUsesFor(authorSql))), authorSql);
        var (lowered, error) = Lowering.Lower(built, authorSql, parameters, variant);
        if (lowered == null) return (new RenderResult([], [], [error!]), authorSql);
        var result = Renderer.Render(built.Definition, lowered.Sql, built.QueryFile, targets, bodyFile: $"rendered/{lowered.ArtifactPath}", queryParameters: parameters, natives: Lowering.NativeUsesFor(lowered.Sql));
        var files = result.Files.Append(new RenderedFile(lowered.ArtifactPath, lowered.ArtifactText)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return (InputCompiler.Attach(this, built, result with { Files = files }), lowered.Sql);
    }

    /// <summary>
    /// Where a copy reads from, for one destination connection: each connection of the model it copies (validation guarantees there is at least one and that the destination is not among them), its engine, the table there,
    /// and, for a copy with a slice, the value that tells that origin's rows apart (`${origin.name}` is the origin's parameter, `${connection.name}` the destination's). Empty for a model that is not a copy.
    /// </summary>
    public IReadOnlyList<DbDataBuild.Planning.CopyOrigin> OriginsOf(ModelDefinition model, string destination)
    {
        if (!model.IsCopy || model.LocalCopy || model.From == null) return [];
        var connections = Project.Models.FirstOrDefault(m => string.Equals(m.Name, model.From, StringComparison.OrdinalIgnoreCase)) is { } originModel
            ? originModel.Targets ?? Config.DefaultConnections
            : Project.DeclaredModels.FirstOrDefault(d => string.Equals(d.Name, model.From, StringComparison.OrdinalIgnoreCase))?.Connections ?? Config.DefaultConnections;
        var built = Project.BuiltModels.FirstOrDefault(s => s.Definition.Name == model.Name);
        string? Value(string origin) => model.Slice == null || built == null ? model.Slice?.Value : ParameterReferences.Substitute(model.Slice.Value, (scope, name) => scope switch
        {
            "origin" => built.ParametersFor(Config, origin).GetValueOrDefault($"connection.{name}")?.Value,
            _ => built.ParametersFor(Config, destination).GetValueOrDefault($"{scope}.{name}")?.Value,
        });
        var native = Project.NativeModels.FirstOrDefault(n => string.Equals(n.Name, model.From, StringComparison.OrdinalIgnoreCase));
        var use = native == null ? null : NativeInline.Prepare(native, Config);
        return connections.Select(c => new DbDataBuild.Planning.CopyOrigin(c, Config.EngineOf(c) ?? c, model.From, Value(c), null, use, native == null ? null : NativeInline.Values(native, use!, Config, c))).ToList();
    }

    /// <summary>
    /// The tables a model's query reads. The names in its text, and, when it calls a macro of the project, the tables of the bound plan (DuckDB has expanded the macro by then, so a table that is only
    /// an argument, `snapshot_at('snap.orders', ...)`, is there). The macro's own name is not a table.
    /// </summary>
    public IReadOnlyList<string> BaseTablesOf(BuiltModel built, string sql)
    {
        var macros = Project.Macros;
        var named = (QueryAnalyzer.Analyze(sql).Facts?.BaseTables.Select(t => t.QualifiedName) ?? []).ToList();
        var (called, mentioned) = macros.ReachedBy(sql);
        if (called.Count == 0 && mentioned.Count == 0) return named;
        var scanned = Lowering.TablesRead(built, sql, built.QueryParameterList(Root, Config)) ?? [];
        return named.Where(n => !called.Contains(n, StringComparer.OrdinalIgnoreCase)).Concat(scanned).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The text lineage and nullability are read from: the query as written, or, for one that calls macros, the lowered query (what DuckDB expanded the macros to), which is plain SQL on real tables, so the
    /// analyzer follows each output column through the macro to the column it comes from. Falls back to the written query when it cannot be lowered (its error is reported where it is lowered).
    /// </summary>
    public string AnalysisSql(BuiltModel built, string sql)
    {
        if (!Lowering.Enabled || !Lowering.ReachesMacros(sql)) return sql;
        return Lowering.Lower(built, sql, built.QueryParameterList(Root, Config)).Model?.Sql ?? sql;
    }

    private static readonly System.Text.RegularExpressions.Regex SafeCollation = new(@"^[A-Za-z0-9_.\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A model run in DuckDB (`sample`, `test`) the way its connection compares strings (DESIGN.md 7.4): the profile of the model's first connection, as a `default_collation` for the query and, where the
    /// profile ignores trailing spaces, the lowered query with `rtrim()` where it matters. What was done is returned as notes, so a run never hides it. Falls back to the query as written, with a note, when it
    /// cannot be lowered.
    /// </summary>
    public (string Sql, string? Collation, IReadOnlyList<string> Notes) StringEmulation(BuiltModel built, string valuesSql)
    {
        var connection = TargetsOf(built.Definition).FirstOrDefault();
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
                var (lowered, error) = Lowering.LowerForDuckDb(built, valuesSql);
                if (lowered != null) { sql = lowered; trailing = "; trailing spaces ignored (rtrim in comparisons, joins, GROUP BY, DISTINCT, window partitions; not in set operations, count(DISTINCT) or LIKE)"; }
                else notes.Add($"trailing spaces are not ignored in this run: the query could not be lowered ({error})");
            }
        }
        notes.Insert(0, $"strings compare as on `{connection}` ({semantics.Describe()}): collation {collation ?? "none (binary)"}{trailing}");
        return (sql, collation, notes);
    }

    /// <summary>What binding a query that calls macros needs (null for one that does not): the macros it reaches and the tables its bound plan scans.</summary>
    public DbDataBuild.Define.MacroSupport? MacroSupportFor(BuiltModel built, string sql)
    {
        var (called, mentioned) = Project.Macros.ReachedBy(sql);
        if (called.Count == 0 && mentioned.Count == 0) return null;
        var scanned = Lowering.TablesRead(built, sql, built.QueryParameterList(Root, Config)) ?? [];
        return new DbDataBuild.Define.MacroSupport(Project.Macros.PreludeFor([sql]), scanned, called, AnalysisSql(built, sql));
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

    private IReadOnlyList<ProjectModel>? models;

    /// <summary>Every model of the project, built and declared (all seven kinds), as the selectors, the graph, the counts and the interfaces see them.</summary>
    public IReadOnlyList<ProjectModel> Models => models ??= ProjectModels.Of(Project, Config.DefaultConnections);

    private ModelSelection? selection;

    private ModelSelection Selection => selection ??= new ModelSelection(Models, Graph, new SelectionHost(Config.Connections.Keys.ToList(), Changed));

    /// <summary>
    /// The models the arguments select, of all seven kinds, as selectors (DESIGN.md 9.9; the grammar is <see cref="ModelSelection"/>, the one every command uses): a model name or pattern, a `.sql` or `.yml` path, or a
    /// directory under models/, with the graph operators `+model`, `model+`, `2+model`, `model+1`, `@model`, the filters `kind:`, `tag:`, `property:key=value`, `connection:`, `path:` and `changed:<ref>`, a comma for
    /// an intersection, and `exclude:<selector>` to take models out again. Several arguments are a union; every model when none are given. Returns null and writes the problem when a term selects nothing.
    /// </summary>
    public List<ProjectModel>? SelectModels(IReadOnlyList<string> args, TextWriter error)
    {
        var names = Selection.Select(args, error);
        if (names == null) return null;
        var byName = Models.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        return names.Select(n => byName[n]).ToList();
    }

    /// <summary>
    /// The built models the arguments select (the same grammar and the same result as <see cref="SelectModels"/>, narrowed to the models the tool builds, with their queries), for the commands that render, plan, run or
    /// sample. A selection that names only mapped or native models says so rather than doing nothing.
    /// </summary>
    public List<LoadedModel>? Select(IReadOnlyList<string> args, TextWriter error)
    {
        var chosen = SelectModels(args, error);
        if (chosen == null) return null;
        var built = Project.BuiltModels.ToDictionary(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase);
        var result = chosen.Where(m => built.ContainsKey(m.Name)).Select(m => built[m.Name]).OrderBy(s => s.Definition.Name, StringComparer.Ordinal)
            .Select(s => new LoadedModel(s, s.ReadQuery(Root, Config))).ToList();
        if (result.Count == 0 && chosen.Count > 0 && args.Count > 0)
        {
            error.WriteLine($"The selection names {chosen.Count} model(s) of the kind {string.Join(", ", chosen.Select(m => m.Kind).Distinct().Order(StringComparer.Ordinal))}, and the tool builds none of them: a mapped or native model is declared, not built.");
            return null;
        }
        return result;
    }

    /// <summary>The `changed:` term: the models whose files changed since a revision, with what reads them. A declared model counts as itself and for what reads it.</summary>
    private IReadOnlySet<string>? Changed(string revision, TextWriter error)
    {
        var (files, problem) = GitInfo.ChangedFiles(Root, revision);
        if (files == null) { error.WriteLine(problem); return null; }
        var built = Project.BuiltModels.OrderBy(s => s.Definition.Name, StringComparer.Ordinal).ToList();
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (files.Contains(ProductInfo.ConfigFile) || files.Contains(ProductInfo.DialectLockFile)) changed.UnionWith(Models.Select(m => m.Name));                    // the project settings reach every model
        foreach (var s in built.Where(s => files.Contains(s.QueryFile) || files.Contains(s.DefinitionFile))) changed.Add(s.Definition.Name);
        foreach (var d in Project.DeclaredModels.Where(d => files.Contains(d.File.Length > 0 ? d.File : ModelFilePath(d.Name))))
        {
            changed.Add(d.Name);
            changed.UnionWith(Graph.ReadBy(d.Name));      // a changed declared model changes what reads it
        }
        changed.UnionWith(ChangedThroughOtherFiles(files, built));
        return changed;
    }

    /// <summary>
    /// The models that a changed file reaches without being the model's own query or definition: a folder file (`_dbdatabuild.yml`: every model beneath it), the text of a native model, a hook script a model runs, a macro file
    /// (the models whose queries reach one of its macros), and a rendered file (the model it was rendered for).
    /// </summary>
    private HashSet<string> ChangedThroughOtherFiles(HashSet<string> files, List<BuiltModel> all)
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
            foreach (var d in Project.DeclaredModels.Where(d => string.Equals(d.File, definition, StringComparison.Ordinal))) result.UnionWith(Graph.ReadBy(d.Name));
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

    private static string ModelFilePath(string name) => $"{ProjectValidator.ModelsDir}/{name.Replace('.', '/')}.yml";
}
