using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>A valid model definition with the project-relative paths of its two files.</summary>
public sealed record BuiltModel(ModelDefinition Definition, string DefinitionFile, string QueryFile)
{
    /// <summary>The query of a model that has none on disk: a copy reads its generated staging table (DuckDB dialect). Null for a model with a `.sql` file.</summary>
    public string? GeneratedQuery { get; init; }

    /// <summary>The head of the query file (`CREATE TABLE schema_name.object_name WITH (...) AS`), when it has one (DESIGN.md 6.5.7).</summary>
    public QueryHead? Head { get; init; }

    /// <summary>The project parameters this model sees: the root file's, overridden by the folder files above it (nearest wins).</summary>
    public IReadOnlyDictionary<string, ParameterValue> ProjectParameters { get; init; } = new Dictionary<string, ParameterValue>();

    /// <summary>Connection parameters the folder files above the model override, by connection name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ParameterValue>> ConnectionParameterOverrides { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, ParameterValue>>();

    /// <summary>Every parameter value the model sees when built on <paramref name="connection"/>, keyed `project.x`, `connection.x`, `model.x`.</summary>
    public IReadOnlyDictionary<string, ParameterValue> ParametersFor(ProjectConfig config, string connection)
    {
        return ParameterReferences.For(config, connection, ProjectParameters, ConnectionParameterOverrides, Definition.Parameters);
    }

    /// <summary>The model's query in DuckDB dialect: its `.sql` file, or the generated one of a copy.</summary>
    private string Raw(string projectRoot) => GeneratedQuery ?? (Head == null ? File.ReadAllText(Path.Combine(projectRoot, QueryFile)) : QueryHeadParser.Body(File.ReadAllText(Path.Combine(projectRoot, QueryFile))));

    /// <summary>The query as the tool works on it (lowering, rendering, hashing, lineage): each parameter reference stands as a marker literal of its type, so the text, its hash and the rendered files do not depend on a value.</summary>
    public string ReadQuery(string projectRoot, ProjectConfig config, string? connection = null) =>
        QueryParameters.Mark(QueryParameters.ResolveNames(Raw(projectRoot), this, config, connection ?? FirstConnection(config)), this, config).Sql;

    /// <summary>
    /// The queries a model reads, each with the connections that read it and the name that tells its lowering apart: one for a model that names nothing by a parameter, or whose names are the same on every
    /// connection (the text is then the one for the first of <paramref name="requested"/>, and the variant is null); otherwise one per distinct text, labelled with the first connection of the model that reads it
    /// (whatever subset is asked for, so a plan for one connection and `render` of all agree on the files). Only the groups with a requested connection are returned.
    /// </summary>
    public IReadOnlyList<(string Sql, IReadOnlyList<string> Targets, string? Variant)> QueryVariants(string projectRoot, ProjectConfig config, IReadOnlyList<string> requested)
    {
        var all = Definition.Targets ?? config.DefaultConnections;
        if (all.Count < 2 || !HasNames(projectRoot, config)) return [(ReadQuery(projectRoot, config, requested.FirstOrDefault()), requested, null)];
        var groups = all.GroupBy(c => ReadQuery(projectRoot, config, c)).ToList();
        if (groups.Count == 1) return [(groups[0].Key, requested, null)];
        return groups.Select(g => (Sql: g.Key, Targets: (IReadOnlyList<string>)g.Where(requested.Contains).ToList(), Variant: (string?)g.First())).Where(v => v.Targets.Count > 0).ToList();
    }

    /// <summary>The connection a text that does not say which is read for: the model's first.</summary>
    private string FirstConnection(ProjectConfig config) => (Definition.Targets ?? config.DefaultConnections).FirstOrDefault() ?? "";

    /// <summary>True when the query names something by a parameter (a table or column given to a macro), so it reads differently on different connections.</summary>
    public bool HasNames(string projectRoot, ProjectConfig config) => QueryParameters.HasNames(Raw(projectRoot), this, config);

    /// <summary>The query with each parameter reference replaced by its value on <paramref name="connection"/>: what DuckDB runs for real (`sample`, `test`).</summary>
    public string ReadQueryWithValues(string projectRoot, ProjectConfig config, string connection) => QueryParameters.WithValues(Raw(projectRoot), this, config, connection);

    /// <summary>The problems with the parameter references in the query (an undefined one, a view's, a clash with a marker).</summary>
    public IReadOnlyList<string> QueryParameterProblems(string projectRoot, ProjectConfig config) =>
        QueryParameters.NameProblems(Raw(projectRoot), this, config).Concat(QueryParameters.Mark(QueryParameters.ResolveNames(Raw(projectRoot), this, config, FirstConnection(config)), this, config).Problems).ToList();

    /// <summary>The parameters the query uses as values, with their markers.</summary>
    public IReadOnlyList<QueryParameter> QueryParameterList(string projectRoot, ProjectConfig config) => QueryParameters.Mark(QueryParameters.ResolveNames(Raw(projectRoot), this, config, FirstConnection(config)), this, config).Parameters;

    /// <summary>The settings this model took from a project file above it (`defaults:` of the root file or of a folder's `_dbdatabuild.yml`), with the file and line each was written on. Empty when the model's own file says everything.</summary>
    public IReadOnlyList<SettingOrigin> Inherited { get; init; } = [];
}

/// <summary>One scalar of a model's effective settings that was written in another file, by dotted path (`kind.copy.from`, `connections[1]`).</summary>
public sealed record SettingOrigin(string Path, string File, int Line, string Value);

public sealed record ProjectValidationResult(
    IReadOnlyList<BuiltModel> BuiltModels, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<DeclaredModel>? DeclaredAndStaging = null, MacroLibrary? MacroFiles = null)
{
    /// <summary>The macros and types of the project (`macros/`), for every binding of a model's query.</summary>
    public MacroLibrary Macros => MacroFiles ?? MacroLibrary.Empty;

    /// <summary>The mapped models of the project, as files (native models are listed apart).</summary>
    public IReadOnlyList<DeclaredModel> MappedModels => (DeclaredAndStaging ?? []).Where(d => !d.IsGenerated && !d.IsNative).ToList();

    /// <summary>The native models of the project: tables computed by an engine-native query.</summary>
    public IReadOnlyList<DeclaredModel> NativeModels => (DeclaredAndStaging ?? []).Where(d => d.IsNative).ToList();

    /// <summary>Every descriptor that is a file of the project (mapped and native; not a generated staging table).</summary>
    public IReadOnlyList<DeclaredModel> DeclaredModels => (DeclaredAndStaging ?? []).Where(d => !d.IsGenerated).ToList();

    /// <summary>Everything a query binds against: the mapped models and the staging tables that copies read from.</summary>
    public IReadOnlyList<DeclaredModel> DeclaredModelsAndStaging => DeclaredAndStaging ?? [];
    public IReadOnlyList<ModelDefinition> Models => BuiltModels.Select(s => s.Definition).ToList();
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

/// <summary>Offline validation of every model under <c>models/</c>. Reads files only; never writes, never connects.</summary>
public static class ProjectValidator
{
    /// <summary>With a layout other than `folder` or `none`, the definition file's name must spell the model's name: `dotted` its whole name, `object` the part after the schema name.</summary>
    private static void CheckLayout(ModelLayout layout, string file, string name, List<Diagnostic> diags)
    {
        if (layout is ModelLayout.Folder or ModelLayout.None) return;
        var expected = layout == ModelLayout.Dotted ? name : name[(name.LastIndexOf('.') + 1)..];
        if (Path.GetFileNameWithoutExtension(file) != expected)
            diags.Add(new Diagnostic(DiagnosticCatalog.NameMismatch, new(file, 0, 0), $"name is `{name}`, but `model_layout: {layout.ToString().ToLowerInvariant()}` expects the file to be called `{expected}.yml`.",
                Fix: $"Rename the file to `{expected}.yml` (and its query beside it), change `name:`, or set `model_layout` in {ProductInfo.ConfigFile} to another layout, or `none`."));
    }

    public const string ModelsDir = "models";

    /// <summary>The folder `sources/` held the descriptors of tables the tool does not build, before they became **mapped** models in `models/`.</summary>
    public const string RetiredSourcesDir = "sources";

    public static ProjectValidationResult Validate(string projectRoot, ProjectConfig? config = null)
    {
        var diags = new List<Diagnostic>();
        var effectiveConfig = config ?? ProjectConfigLoader.LoadFromProject(projectRoot, new List<Diagnostic>());
        // the connections a model may name come from the project's configuration (a problem in the configuration itself is reported where the configuration is loaded)
        var connections = effectiveConfig.Connections.Keys.ToHashSet(StringComparer.Ordinal);
        var models = new List<BuiltModel>();
        var descriptors = new List<DeclaredModel>();
        var macros = MacroLibrary.Load(projectRoot, diags);
        if (Directory.Exists(Path.Combine(projectRoot, RetiredSourcesDir)))
            diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(RetiredSourcesDir, 0, 0),
                $"`{RetiredSourcesDir}/` is no longer read: the tables the tool does not build are mapped models now.",
                Fix: $"Move each `{RetiredSourcesDir}/<schema name>/<table>.yml` to `{ModelsDir}/<schema name>/<table>.yml` and add `kind: mapped` (or set it once in a folder's `{ProductInfo.FolderConfigFile}`)."));
        var modelsRoot = Path.Combine(projectRoot, ModelsDir);
        if (!Directory.Exists(modelsRoot))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(ModelsDir, 0, 0),
                $"Directory `{ModelsDir}/` was not found under {projectRoot}.", Fix: $"Create `{ModelsDir}/` or run from the project root."));
            return new(models, diags, descriptors, macros);
        }

        var files = Directory.EnumerateFiles(modelsRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yml", StringComparison.Ordinal) || f.EndsWith(".sql", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != ProductInfo.FolderConfigFile)         // a project file, not a model
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var set = files.ToHashSet(StringComparer.Ordinal);
        var folders = new FolderLayers(projectRoot, diags);
        var mappedStems = new HashSet<string>(StringComparer.Ordinal);      // files with no query: mapped models, native models and copies
        var nativeStems = new HashSet<string>(StringComparer.Ordinal);
        var copies = new List<(ModelDefinition Definition, string File)>();
        var declared = new List<(string Name, string File)>();               // every model, mapped model and copy by the name its definition gives it
        // the parameters each file sees from the project files above it: the root's, overridden by each folder's, and the connection parameters the folders override
        var parameterFiles = new Dictionary<string, (IReadOnlyDictionary<string, ParameterValue> Project, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ParameterValue>> Connections)>(StringComparer.Ordinal);
        BuiltModel WithParameters(BuiltModel s) => parameterFiles.TryGetValue(s.DefinitionFile, out var p) ? s with { ProjectParameters = p.Project, ConnectionParameterOverrides = p.Connections } : s;

        foreach (var file in files.Where(f => f.EndsWith(".yml", StringComparison.Ordinal)))
        {
            var stem = file[..^".yml".Length];
            // only the `folder` layout can say what the name is before the file is read; the others check the file name against the name once it is
            var expected = effectiveConfig.Layout == ModelLayout.Folder ? stem[(ModelsDir.Length + 1)..].Replace('/', '.') : null;
            var text = File.ReadAllText(Path.Combine(projectRoot, file));
            var projectParameters = new Dictionary<string, ParameterValue>(effectiveConfig.Parameters, StringComparer.Ordinal);
            var connectionOverrides = new Dictionary<string, Dictionary<string, ParameterValue>>(StringComparer.Ordinal);
            foreach (var folder in folders.ParametersAbove(file))
            {
                foreach (var (k, v) in folder.Project) projectParameters[k] = v;
                foreach (var (c, values) in folder.Connections)
                {
                    if (!connectionOverrides.TryGetValue(c, out var target)) connectionOverrides[c] = target = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
                    foreach (var (k, v) in values) target[k] = v;
                }
            }
            parameterFiles[file] = (projectParameters, connectionOverrides.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, ParameterValue>)kv.Value, StringComparer.Ordinal));
            var above = new List<YamlLayer>();
            if (effectiveConfig.Defaults != null) above.Add(new YamlLayer(ProductInfo.ConfigFile, effectiveConfig.Defaults));
            above.AddRange(folders.Above(file));

            // a query file may start with a head that says what it builds: its name, table or view, and the reload options of its kind (DESIGN.md 6.5.7)
            QueryHead? head = null;
            if (set.Contains(stem + ".sql"))
            {
                var parsedHead = QueryHeadParser.Parse(stem + ".sql", File.ReadAllText(Path.Combine(projectRoot, stem + ".sql")));
                diags.AddRange(parsedHead.Problems);
                if (parsedHead.Problems.Count > 0) continue;
                head = parsedHead.Head;
            }

            // the kind decides what the file is, and the kind may come from a folder: so the file is merged first
            var before = diags.Count;
            var root = StrictYamlReader.Read(text, file, diags);
            if (root is YamlMapping own)
            {
                if (diags.Count > before) continue;
                YamlLayer? headLayer = null;
                if (head != null)
                {
                    var ownKind = own.Entries.Any(e => e.Key.Value is "kind" or "kind=" or "kind+" or "kind-");
                    var headWhere = new SourceLocation(stem + ".sql", head.Line, head.Column);
                    if (own.Get("name") is YamlScalar ownName && ownName.Value != head.Name)
                    {
                        diags.Add(new Diagnostic(DiagnosticCatalog.NameMismatch, new(file, ownName.Line, ownName.Column), $"name is `{ownName.Value}`, but the head of `{stem}.sql` says `{head.Name}`.", Fix: "Remove `name:` (the head names the model) or make them the same."));
                        continue;
                    }
                    if (ownKind && head.KindIsExplicit)
                    {
                        diags.Add(new Diagnostic(DiagnosticCatalog.QueryHeadInvalid, headWhere, $"the kind is said in the head ({(head.IsView ? "CREATE VIEW" : "`kind` in `WITH`")}) and again in `{file}`: it is said in one place.", Fix: $"Remove `kind:` from `{file}` or the kind from the head."));
                        continue;
                    }
                    if (ownKind && !head.IsView && ModelKindShape.NameOf(own.Get("kind")) == ModelKinds.View)
                    {
                        diags.Add(new Diagnostic(DiagnosticCatalog.QueryHeadInvalid, headWhere, $"the head says `CREATE TABLE`, and `{file}` says the kind is `view`.", Fix: "Write `CREATE VIEW` in the head, or change the kind."));
                        continue;
                    }
                    headLayer = new YamlLayer(stem + ".sql", QueryHeadParser.ToLayer(head, withKind: !ownKind));
                }
                var merged = YamlMerge.Merge(headLayer == null ? [.. above, new YamlLayer(file, own)] : [.. above, headLayer, new YamlLayer(file, own)], ModelDefinitionLoader.LayeredKeys, diags);
                if (diags.Count > before) continue;
                var kindName = ModelKindShape.NameOf(merged.Root.Get("kind"));
                if (kindName is DeclaredModelLoader.MappedKind or NativeQuery.Kind)
                {
                    mappedStems.Add(stem);
                    if (kindName == NativeQuery.Kind) nativeStems.Add(stem);
                    var nativeFile = set.Contains(stem + ".native.sql") ? File.ReadAllText(Path.Combine(projectRoot, stem + ".native.sql")) : null;
                    if (DeclaredModelLoader.LoadMerged(merged, file, expected, diags, connections, nativeFile) is { } mapped)
                    {
                        CheckLayout(effectiveConfig.Layout, file, mapped.Name, diags);
                        declared.Add((mapped.Name, file));
                        descriptors.Add((kindName == NativeQuery.Kind ? CheckNative(mapped, file, projectParameters, connectionOverrides, effectiveConfig, diags) : mapped) with { File = file });
                    }
                    continue;
                }
                var isCopy = kindName == ModelKinds.Copy;
                if (isCopy) mappedStems.Add(stem);
                else if (!set.Contains(stem + ".sql"))
                    diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{file}` has no query file `{stem}.sql`.", Fix: $"Add `{stem}.sql`, or remove `{file}`."));
                if (ModelDefinitionLoader.LoadMerged(merged, file, expected, diags, connections) is { } def)
                {
                    CheckLayout(effectiveConfig.Layout, file, def.Name, diags);
                    declared.Add((def.Name, file));
                    CheckRefreshPlans(def, file, effectiveConfig, diags);
                    CheckDestinations(def, file, effectiveConfig, diags);
                    if (isCopy) copies.Add((def, file));
                    else models.Add(WithParameters(new BuiltModel(def, file, stem + ".sql") { Inherited = Inherited(merged, file, stem + ".sql"), Head = head }));
                }
                continue;
            }
            // empty, damaged or not a mapping: the loader says what is wrong with it, once
            diags.RemoveRange(before, diags.Count - before);
            if (!set.Contains(stem + ".sql"))
                diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{file}` has no query file `{stem}.sql`.", Fix: $"Add `{stem}.sql`, or remove `{file}`."));
            ModelDefinitionLoader.Load(text, file, expected, diags, connections);
        }

        // a name is the definition's, not the path's, so two files can claim one
        foreach (var group in declared.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var later in group.Skip(1))
                diags.Add(new Diagnostic(DiagnosticCatalog.NameMismatch, new(later.File, 0, 0), $"`{later.Name}` is the name of `{group.First().File}` too: a name belongs to one model.", Fix: "Give one of them another `name:`."));

        foreach (var file in files.Where(f => f.EndsWith(".sql", StringComparison.Ordinal)))
        {
            if (file.EndsWith(".native.sql", StringComparison.Ordinal))
            {
                if (!nativeStems.Contains(file[..^".native.sql".Length]))
                    diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{file}` is native text, but `{file[..^".native.sql".Length]}.yml` is not a native model.", Fix: $"Give `{file[..^".native.sql".Length]}.yml` `kind: native`, or remove `{file}`."));
                continue;
            }
            var stem = file[..^".sql".Length];
            if (mappedStems.Contains(stem))
                diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{stem}.yml` has no query of its own (a mapped model is not built, a copy reads its origin), so `{file}` has no use.", Fix: $"Remove `{file}`, or give `{stem}.yml` a kind that has a query."));
            else if (!set.Contains(stem + ".yml"))
                diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{file}` has no definition file `{stem}.yml`.", Fix: $"Run `{ProductInfo.Cli} project model update {file}`."));
        }

        ResolveCopies(copies, models, descriptors, effectiveConfig, diags, WithParameters);
        return new(models, diags, descriptors, macros);
    }

    /// <summary>
    /// A copy has the columns (and the grain, unless it names its own) of the model it copies, found among the project's models, mapped models and other copies. The origin must be on one connection and
    /// the copy on others: a copy moves rows between connections. Each resolved copy gets its generated query and the staging table it reads declared as a generated mapped table.
    /// </summary>
    private static void ResolveCopies(List<(ModelDefinition Definition, string File)> copies, List<BuiltModel> models, List<DeclaredModel> descriptors, ProjectConfig config, List<Diagnostic> diags, Func<BuiltModel, BuiltModel> withParameters)
    {
        var pending = copies.ToDictionary(c => c.Definition.Name, c => c, StringComparer.OrdinalIgnoreCase);
        var done = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // the columns, grain and connections of what a copy copies; null when it cannot be found (reported by the caller)
        (IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> Grain, IReadOnlyList<string> Connections)? Origin(string name, List<string> stack)
        {
            if (models.FirstOrDefault(m => string.Equals(m.Definition.Name, name, StringComparison.OrdinalIgnoreCase)) is { } m)
                return (m.Definition.Columns, m.Definition.Grain, m.Definition.Targets ?? config.DefaultConnections);
            if (descriptors.FirstOrDefault(d => !d.IsGenerated && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) is { } d)
                return (d.Columns, d.Grain, d.Connections ?? config.DefaultConnections);
            if (pending.TryGetValue(name, out var c))
            {
                if (done.TryGetValue(name, out var resolved)) return (resolved.Columns, resolved.Grain, resolved.Targets ?? config.DefaultConnections);
                if (stack.Contains(name, StringComparer.OrdinalIgnoreCase) || failed.Contains(name)) return null;
                stack.Add(name);
                return Resolve(c, stack) is { } r ? (r.Columns, r.Grain, r.Targets ?? config.DefaultConnections) : null;
            }
            return null;
        }

        ModelDefinition? Resolve((ModelDefinition Definition, string File) c, List<string> stack)
        {
            var def = c.Definition;
            if (done.TryGetValue(def.Name, out var already)) return already;
            SourceLocation At() => new(c.File, def.FromLine, 1);
            var origin = Origin(def.From!, stack);
            if (origin == null)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, At(), $"`{def.Name}` copies `{def.From}`, which is not a model, a mapped model or a copy of this project{(stack.Contains(def.From!, StringComparer.OrdinalIgnoreCase) ? " (it copies itself, through the others)" : "")}.",
                    Fix: "Name the model to copy as `kind: {copy: {from: schema_name.table_name}}`."));
                failed.Add(def.Name);
                return null;
            }
            var origins = origin.Value.Connections;
            var destinations = def.Targets ?? config.DefaultConnections;
            string? problem = null;
            // local or remote is decided by the connections: every destination is the origin's connection (a local copy: an ordinary load over a query of the origin), or none is (a transfer)
            // a native command is never read by a query, so a copy of it always goes through the transfer (read on the origin, staged, loaded), even when the origin is the copy's own connection
            var commandOrigin = descriptors.FirstOrDefault(d => d.IsNative && string.Equals(d.Name, def.From, StringComparison.OrdinalIgnoreCase))?.Native is { Access: NativeQuery.Command } cmd ? cmd : null;
            var localCount = commandOrigin != null ? 0 : destinations.Count(d => origins.Contains(d, StringComparer.Ordinal));
            var isLocal = commandOrigin == null && localCount == destinations.Count;
            if (commandOrigin != null && def.Watermark != null && !commandOrigin.Text.Contains("@watermark", StringComparison.Ordinal))
                problem = $"`{def.From}` is a native command, so an incremental copy of it has to pass the bound itself: its text must use `@watermark`.";
            else if (localCount > 0 && !isLocal)
                problem = $"`{def.Name}` is on {string.Join(", ", destinations.Select(d => $"`{d}`"))}, and some of them are connections of `{def.From}`: a copy is local (every connection is the origin's) or between connections (none is), not both.";
            else if (isLocal && origins.Count > 1)
                problem = $"`{def.From}` is on {string.Join(", ", origins)}, so a copy on one of them has no single origin.";
            else if (isLocal && (def.Slice != null || def.UniqueKey.Count > 0 || def.Watermark != null))
                problem = $"`{def.Name}` copies `{def.From}` on its own connection: a local copy is a full replace, so `slice`, `unique_key` and `watermark` do not apply (an incremental local load is an incremental model over `{def.From}`).";
            else if (origins.Count > 1 && def.Slice == null)
                problem = $"`{def.From}` is on {string.Join(", ", origins)}, so the rows of each must be told apart: a copy from several connections needs `slice` (the column that says which rows are an origin's, and its value).";
            else if (origins.Count > 1 && !def.Slice!.Value.Contains("${origin.", StringComparison.Ordinal))
                problem = $"The slice's value `{def.Slice.Value}` is the same for every origin of `{def.From}`; it has to differ, for example `${{origin.store_id}}`.";
            if (problem == null && def.Slice != null)
            {
                var seen = withParameters(new BuiltModel(def, c.File, c.File));
                foreach (System.Text.RegularExpressions.Match r in ParameterReferences.Pattern.Matches(def.Slice.Value))
                {
                    var (scope, pname) = (r.Groups[1].Value, r.Groups[2].Value);
                    foreach (var named in scope == "origin" ? origins : scope == "connection" ? destinations : ["-"])
                    {
                        var key = scope is "origin" ? $"connection.{pname}" : $"{scope}.{pname}";
                        var on = scope is "origin" or "connection" ? named : destinations[0];
                        if (problem == null && !seen.ParametersFor(config, on).ContainsKey(key))
                            problem = scope is "origin" or "connection"
                                ? $"The slice uses `{r.Value}`, but the connection `{named}` has no parameter `{pname}` (`connections.{named}.parameters`)."
                                : $"The slice uses `{r.Value}`, but there is no such {scope} parameter (`parameters:` in {(scope == "model" ? "the model's file" : "a project file")}).";
                    }
                }
            }
            if (problem != null)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, At(), problem, Fix: "A copy reads from one connection, or from several with a `slice`, and is built on others."));
                failed.Add(def.Name);
                return null;
            }

            var columns = origin.Value.Columns.Select(x => x with { Line = 0, CollationLine = 0 }).ToList();
            var grain = def.Grain.Count > 0 ? def.Grain : origin.Value.Grain;
            var sliceAdded = false;
            if (def.Slice != null)
            {
                if (!columns.Any(x => string.Equals(x.Name, def.Slice.Column, StringComparison.OrdinalIgnoreCase)))
                {
                    if (def.Slice.Type == null)
                    {
                        diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, At(), $"`{def.From}` has no column `{def.Slice.Column}`, so the copy adds it, and the slice needs a `type` for it, for example `type: VARCHAR(20)`."));
                        failed.Add(def.Name);
                        return null;
                    }
                    columns.Add(new ColumnDefinition(def.Slice.Column, LogicalNormalize(def.Slice.Type), false));
                    sliceAdded = true;
                }
                if (grain.Count > 0 && !grain.Contains(def.Slice.Column, StringComparer.OrdinalIgnoreCase)) grain = [.. grain, def.Slice.Column];       // the rows of different origins may share a key: the origin's value is part of it
            }
            foreach (var named in def.UniqueKey.Select(k => ("unique_key", k)).Concat(def.Watermark == null ? [] : [("watermark.column", def.Watermark.Column)]))
                if (!columns.Any(x => string.Equals(x.Name, named.Item2, StringComparison.OrdinalIgnoreCase)))
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.UnknownColumnReference, At(), $"{named.Item1} refers to `{named.Item2}`, which `{def.From}` does not have."));
                    failed.Add(def.Name);
                    return null;
                }
            var resolved = def with { Columns = columns, Grain = grain, SliceColumnAdded = sliceAdded, LocalCopy = isLocal };
            done[def.Name] = resolved;
            return resolved;
        }

        foreach (var c in copies)
        {
            var resolved = Resolve(c, [c.Definition.Name]);
            if (resolved == null) continue;
            models.Add(withParameters(new BuiltModel(resolved, c.File, c.File) { GeneratedQuery = resolved.LocalCopy ? CopyModels.LocalQuery(resolved) : CopyModels.Query(config, resolved) }));
            if (!resolved.LocalCopy) descriptors.Add(CopyModels.StagingDescriptor(config, resolved, resolved.Columns));
        }
    }

    private static string LogicalNormalize(string type) => System.Text.RegularExpressions.Regex.Replace(type.Trim().ToUpperInvariant(), @"\s*,\s*", ", ");

    /// <summary>
    /// What a native model needs beyond its text: exactly one connection (the text is in that engine's dialect), a form its engine can inline (T-SQL takes no WITH inside a derived table), parameter references
    /// that exist, and the parameters its file sees from the project files above it.
    /// </summary>
    private static DeclaredModel CheckNative(DeclaredModel d, string file, IReadOnlyDictionary<string, ParameterValue> project, Dictionary<string, Dictionary<string, ParameterValue>> overrides, ProjectConfig config, List<Diagnostic> diags)
    {
        var native = d.Native! with { };
        native = native with { };
        var connections = d.Connections ?? config.DefaultConnections;
        void Problem(string text) => diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, native.Line, 1), $"{d.Name}: {text}"));
        if (connections.Count != 1) Problem($"a native model is written in one engine's dialect, so it is on exactly one connection, not {connections.Count} ({string.Join(", ", connections)}).");
        else if (native.Access == NativeQuery.Command && !(config.Connections.TryGetValue(connections[0], out var host) && host.AllowNativeCommands))
            Problem($"a native command runs a procedure, which can write, so the connection must allow it: `connections.{connections[0]}.allow_native_commands: true` (the read login's permissions are what keeps it read-only).");
        else if (native.Access == NativeQuery.Select && config.EngineOf(connections[0]) is "sqlserver" or "fabric" && native.Text.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            Problem("a T-SQL native select starts with SELECT: a WITH cannot sit inside the derived table it is inlined as. Wrap the common table expression in a table-valued function.");
        var withParameters = new NativeQuery(native.Access, native.Text, native.Reads, native.Parameters, native.Line, file)
        {
            TrackDefinition = native.TrackDefinition,
            ProjectParameters = project,
            ConnectionParameterOverrides = overrides.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, ParameterValue>)kv.Value, StringComparer.Ordinal),
        };
        if (connections.Count == 1)
            foreach (var (scope, name) in ParameterReferences.In(native.Text))
            {
                if (scope is not ("project" or "connection" or "model")) { Problem($"`${{{scope}.{name}}}` is not a parameter a native text can use (`project`, `connection`, `model`)."); continue; }
                if (!withParameters.ParametersFor(config, connections[0]).ContainsKey($"{scope}.{name}"))
                    Problem($"`${{{scope}.{name}}}` has no value (`parameters:` in {(scope == "model" ? "the model's file" : scope == "connection" ? $"`connections.{connections[0]}`" : "a project file")}).");
            }
        return d with { Native = withParameters };
    }

    /// <summary>A model that names a refresh plan the project does not declare (when the project declares any; `default` is always there) is told, at the place the membership was written.</summary>
    /// <summary>A model is built on its connections, and a copy is written to them: none may be `readonly` (it is an origin only).</summary>
    private static void CheckDestinations(ModelDefinition def, string file, ProjectConfig config, List<Diagnostic> diags)
    {
        foreach (var c in (def.Targets ?? config.DefaultConnections).Where(config.IsReadOnly))
            diags.Add(new Diagnostic(DiagnosticCatalog.ReadOnlyConnectionNotWritable, new(file, 0, 0),
                $"`{def.Name}` is {(def.IsCopy ? "copied to" : "built on")} the connection `{c}`, which is `readonly`: the tool never writes there.",
                Fix: "Name connections that are not readonly in the model's `connections`, or use the readonly connection as an origin (a mapped or native model, or what a copy reads)."));
    }

    private static void CheckRefreshPlans(ModelDefinition def, string file, ProjectConfig config, List<Diagnostic> diags)
    {
        if (config.RefreshPlans.Count == 0) return;
        foreach (var m in def.Refresh.Where(m => m.Plan != RefreshPlanSettings.DefaultName && !config.RefreshPlans.ContainsKey(m.Plan)))
            diags.Add(new Diagnostic(DiagnosticCatalog.RefreshPlanUndeclared, new(m.File ?? file, m.Line, 1),
                $"`{def.Name}` is in the refresh plan `{m.Plan}`, which `refresh.plans` does not declare.", $"Declared: {string.Join(", ", config.RefreshPlans.Keys.Order(StringComparer.Ordinal))}."));
    }

    /// <summary>What a model's effective settings took from other files: the scalars under a layered key written in a file other than the model's own.</summary>
    private static List<SettingOrigin> Inherited(MergedYaml merged, string modelFile, string queryFile) =>
        merged.Origins().Where(o => o.File != modelFile && o.File != queryFile && ModelDefinitionLoader.LayeredKeys.Contains(o.Path.Split('.', '[')[0]))
            .Select(o => new SettingOrigin(o.Path, o.File, o.Line, o.Value)).ToList();

    /// <summary>
    /// The project files in the folders above a model, root down: each folder's `_dbdatabuild.yml`, read once, its `defaults:` the layer. A folder file holds `defaults:` and nothing else yet;
    /// what it says wrong is reported once, where it is, not once per model beneath it.
    /// </summary>
    private sealed class FolderLayers(string projectRoot, List<Diagnostic> diags)
    {
        private readonly Dictionary<string, YamlLayer?> cache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FolderParameters?> parameterCache = new(StringComparer.Ordinal);

        /// <summary>What one folder's project file says about parameters: the project's (`parameters:`) and a connection's (`connections.<name>.parameters:`), which override the root file's for what is beneath it.</summary>
        public sealed record FolderParameters(IReadOnlyDictionary<string, ParameterValue> Project, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ParameterValue>> Connections);

        public IEnumerable<YamlLayer> Above(string modelFile)
        {
            foreach (var file in FilesAbove(modelFile))
            {
                if (!cache.TryGetValue(file, out var layer)) { Load(file); layer = cache[file]; }
                if (layer != null) yield return layer;
            }
        }

        public IEnumerable<FolderParameters> ParametersAbove(string modelFile)
        {
            foreach (var file in FilesAbove(modelFile))
            {
                if (!cache.ContainsKey(file)) Load(file);
                if (parameterCache.GetValueOrDefault(file) is { } p) yield return p;
            }
        }

        private static IEnumerable<string> FilesAbove(string modelFile)
        {
            var parts = modelFile.Split('/');
            for (var depth = 1; depth < parts.Length; depth++)           // models/, models/a/, models/a/b/ ... (the first part is the models folder)
                yield return string.Join('/', parts.Take(depth).Append(ProductInfo.FolderConfigFile));
        }

        private void Load(string file)
        {
            cache[file] = null; parameterCache[file] = null;
            var path = Path.Combine(projectRoot, file);
            if (!File.Exists(path)) return;
            var before = diags.Count;
            var root = StrictYamlReader.Read(File.ReadAllText(path), file, diags);
            if (root == null) return;
            if (root is not YamlMapping top) { diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, root.Line, root.Column), $"{ProductInfo.FolderConfigFile} must be a mapping of settings.")); return; }
            foreach (var e in top.Entries.Where(e => e.Key.Value is not ("defaults" or "parameters" or "connections")))
                diags.Add(new Diagnostic(DiagnosticCatalog.UnknownKey, new(file, e.Key.Line, e.Key.Column), $"Unknown key `{e.Key.Value}` in {ProductInfo.FolderConfigFile}.", "Keys: defaults, parameters, connections.", "Put the model settings under `defaults:`."));

            void Report(DiagnosticDescriptor d, YamlNode n, string found) => diags.Add(new Diagnostic(d, new(file, n.Line, n.Column), found));
            var project = top.Get("parameters") is { } pn ? ParameterReferences.Read(pn, "`parameters`", Report) : null;
            var byConnection = new Dictionary<string, IReadOnlyDictionary<string, ParameterValue>>(StringComparer.Ordinal);
            if (top.Get("connections") is { } cn)
            {
                if (cn is not YamlMapping cm) Report(DiagnosticCatalog.InvalidValue, cn, "`connections` maps connection names to their `parameters`.");
                else foreach (var e in cm.Entries)
                {
                    if (e.Value is not YamlMapping settings) { Report(DiagnosticCatalog.InvalidValue, e.Value, $"`connections.{e.Key.Value}` is a mapping with `parameters`."); continue; }
                    foreach (var k in settings.Entries.Where(k => k.Key.Value != "parameters"))
                        Report(DiagnosticCatalog.UnknownKey, k.Key, $"Unknown key `{k.Key.Value}` in `connections.{e.Key.Value}`: a folder's project file can only override a connection's `parameters` (the connections themselves are declared once, in {ProductInfo.ConfigFile}).");
                    if (settings.Get("parameters") is { } pm && ParameterReferences.Read(pm, $"`connections.{e.Key.Value}.parameters`", Report) is { } values) byConnection[e.Key.Value] = values;
                }
            }
            if (project != null || byConnection.Count > 0) parameterCache[file] = new FolderParameters(project ?? new Dictionary<string, ParameterValue>(), byConnection);

            if (top.Get("defaults") is not { } node) return;
            if (node is not YamlMapping defaults) { diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, node.Line, node.Column), "`defaults` must be a mapping of model settings.")); return; }
            foreach (var e in defaults.Entries.Where(e => !ModelDefinitionLoader.LayeredKeys.Contains(e.Key.Value.TrimEnd('=', '-', '+'))))
                diags.Add(new Diagnostic(DiagnosticCatalog.UnknownKey, new(file, e.Key.Line, e.Key.Column), $"Unknown key `{e.Key.Value}` in `defaults`.", $"Keys: {string.Join(", ", ModelDefinitionLoader.LayeredKeys.Order(StringComparer.Ordinal))}."));
            cache[file] = diags.Count > before ? null : new YamlLayer(file, defaults);
        }
    }
}
