using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Planning;
using DbDataBuild.Define;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Cli;

/// <summary>
/// Everything the tool can say about a project and its models as plain data: declared and native types per target, lineage and inferred nullability, rendered operations and their
/// hashes with matrix status, indexes, hooks, and the hashes that identify each model's definition and expected shape. The same documents are printed by `--format json`
/// and stored in the target for introspection (`metadata --store`), so what a person reads and what SQL can query are one thing.
/// </summary>
internal static class MetadataBuilder
{
    public const string ModelSchema = "dbdatabuild.model/3";
    public const string ProjectSchema = "dbdatabuild.project/2";

    private static readonly JsonSerializerOptions Compact = new(CommandReport.Json) { WriteIndented = false };

    /// <summary>The document as stored in a database: compact, snake_case, stable key order.</summary>
    public static string ToStoredJson(object document) => JsonSerializer.Serialize(document, Compact);

    public static object Project(ProjectContext ctx)
    {
        var cfg = ctx.Config;
        return new
        {
            schema = ProjectSchema,
            title = cfg.Info.Title, description = cfg.Info.Description, tags = cfg.Info.TagsForDocument, properties = cfg.Info.PropertiesForDocument,
            tool_version = ProductInfo.Version,
            matrix_version = MatrixLoader.EmbeddedVersion(),
            config = new
            {
                default_connections = cfg.DefaultConnections,
                connections = cfg.Connections.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => (object)new { engine = c.Value.Engine, read_only = c.Value.ReadOnly ? true : (bool?)null, dialect = c.Value.Kind.Dialect == c.Value.Engine ? null : c.Value.Kind.Dialect, version = c.Value.Version, title = c.Value.Info.Title, description = c.Value.Info.Description, tags = c.Value.Info.TagsForDocument, properties = c.Value.Info.PropertiesForDocument }),
                target_versions = cfg.TargetVersions,
                tracking_schema = cfg.TrackingSchemaName,
                rewrites = new { fidelity = cfg.Rewrites?.Fidelity ?? RewriteSettings.Exact, disable = cfg.Rewrites?.Disable ?? [], enable = cfg.Rewrites?.Enable ?? [] },
                string_semantics = new
                {
                    @case = cfg.StringSemantics.Case.ToString().ToLowerInvariant(),
                    accent = cfg.StringSemantics.Accent.ToString().ToLowerInvariant(),
                    trailing_space = cfg.StringSemantics.TrailingSpace.ToString().ToLowerInvariant(),
                    collations = cfg.StringSemantics.Collations,
                },
                policy = cfg.Policy.ToDictionary(p => p.Key, p => p.Value.ToString().ToLowerInvariant()),
                hook_groups = cfg.HookGroups.ToDictionary(g => g.Key, g => g.Value.Select(HookJson).ToList()),
            },
            models = ctx.Models.Select(m => new
            {
                name = m.Name,
                kind = m.Kind,
                file = m.DefinitionFile,
                connections = m.Connections,
                definition_hash = DefinitionHashOf(ctx, m.Name),
            }).ToList(),
        };
    }

    /// <summary>The definition hash of every model of the project, of any kind, by name.</summary>
    public static IReadOnlyDictionary<string, string> DefinitionHashes(ProjectContext ctx) =>
        ctx.Models.ToDictionary(m => m.Name, m => DefinitionHashOf(ctx, m.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>The hash that says whether a model's definition changed, for a model of any kind.</summary>
    private static string DefinitionHashOf(ProjectContext ctx, string name) =>
        ctx.Project.BuiltModels.FirstOrDefault(s => string.Equals(s.Definition.Name, name, StringComparison.OrdinalIgnoreCase)) is { } built
            ? ctx.DefinitionHashOf(built.ReadQuery(ctx.Root, ctx.Config))
            : DefinitionHash(ctx.Project.DeclaredModels.First(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The settings of a model's kind, each kind its own (an empty object when it has none): the document says the kind as a plain string, and these beside it.</summary>
    public static object KindSettings(ModelDefinition d) => d.Kind switch
    {
        ModelKinds.IncrementalByUniqueKey => new { unique_key = d.UniqueKey },
        ModelKinds.IncrementalByTimeRange => new { time_column = d.TimeColumn, lookback = d.Lookback },
        ModelKinds.Copy => new
        {
            from = d.From,
            slice = d.Slice == null ? null : new { column = d.Slice.Column, value = d.Slice.Value, type = d.Slice.Type },
            on_mismatch = d.OnMismatch,
            unique_key = d.UniqueKey.Count == 0 ? null : d.UniqueKey,
            watermark = d.Watermark == null ? null : new { column = d.Watermark.Column, lookback = d.Watermark.Lookback },
        },
        _ => new { },
    };

    /// <summary>What a model depends on: a name, what sort of thing it is (a model, a macro, or a table that is none of the project's) and, for a model, its model kind (any of the seven).</summary>
    public sealed record Upstream(string Name, string Category, string? Kind);

    /// <summary>The category and model kind of a table name in the project: a model of one of the seven kinds (a copy's staging table counts as a mapped model), else an unknown table.</summary>
    private static Func<string, (string Category, string? Kind)> KindLookup(ProjectContext ctx)
    {
        var kinds = ctx.Project.BuiltModels.Select(m => (m.Definition.Name, m.Definition.Kind)).Concat(ctx.Project.DeclaredModelsAndStaging.Select(d => (d.Name, d.Kind)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase);
        return n => kinds.TryGetValue(n, out var kind) ? (DependencyCategories.Model, kind) : (DependencyCategories.Unknown, null);
    }

    /// <summary>
    /// What a model's query reads, with the category of each (model, macro, unknown) and the model kind of each model, and the macros it calls: the `upstream` of its document, and the direct edges of the dependency rows. Null when the
    /// query could not be read and calls no macro, so the document says nothing rather than something wrong.
    /// </summary>
    public static List<Upstream>? UpstreamOf(ProjectContext ctx, BuiltModel built, string sql, bool analysed, Func<string, (string Category, string? Kind)>? kindOf = null)
    {
        var macros = ctx.MacrosCalledBy(sql);
        if (!analysed && macros.Count == 0) return null;
        kindOf ??= KindLookup(ctx);
        return ctx.BaseTablesOf(built, sql).Select(n => { var (category, kind) = kindOf(n); return new Upstream(n, category, kind); })
            .Concat(macros.Select(m => new Upstream(m, DependencyCategories.Macro, null))).ToList();
    }

    /// <summary>
    /// The dependency rows of the whole project (DESIGN.md 12, `metadata_dependency`): each model with what its query reads, each native model with what it says it reads, and the transitive closure of both. Computed from the
    /// project and not from the documents, so publishing some of the models still gives each of them its whole ancestry. `project compile` calls this too and keeps, in each `loads.yml`, the rows between the models that
    /// file loads (<see cref="LoadDependencies"/>): there is no second computation, so what a refresh orders by and what is published cannot differ.
    /// </summary>
    public static IReadOnlyList<DependencyRow> Dependencies(ProjectContext ctx)
    {
        var kindOf = KindLookup(ctx);
        var direct = new List<(string, string, string, string?)>();
        foreach (var m in ctx.Project.BuiltModels)
        {
            // a model that names something by a parameter reads other tables on connections that give other names: every variant of its query counts (as in the dependency graph selectors and a refresh use)
            foreach (var sql in ctx.QueryVariants(m))
            {
                var analysed = AnalyzeAgainstUpstream(ctx, m.Definition.Name, ctx.AnalysisSql(m, sql)).Facts != null;
                foreach (var u in UpstreamOf(ctx, m, sql, analysed, kindOf) ?? []) direct.Add((m.Definition.Name, u.Name, u.Category, u.Kind));
            }
        }
        foreach (var n in ctx.Project.NativeModels)
            foreach (var read in n.Native?.Reads ?? []) { var (category, kind) = kindOf(read); direct.Add((n.Name, read, category, kind)); }
        return MetadataDependencies.Closure(direct);
    }

    /// <summary>
    /// The rows of <see cref="Dependencies"/> that run between two models a loads file loads, with the model kind of each side: the dependency graph `loads.yml` carries. Same order as the rows (subject, then depth, then name).
    /// </summary>
    /// <param name="loaded">The models of the loads file, with the model kind of each.</param>
    public static IReadOnlyList<LoadDependency> LoadDependencies(IReadOnlyList<DependencyRow> rows, IReadOnlyDictionary<string, string> loaded) =>
        rows.Where(r => loaded.ContainsKey(r.Subject) && loaded.ContainsKey(r.DependsOn))
            .Select(r => new LoadDependency(r.Subject, loaded[r.Subject], r.DependsOn, loaded[r.DependsOn], r.Depth)).ToList();

    private static object HookJson(HookDefinition h) => h.IsReference
        ? new { use = h.Use }
        : new { name = h.Name, @event = h.Event, script = (object?)h.Script ?? h.ScriptByTarget, connections = h.Targets, effect = h.Effect, risk = h.Risk };

    /// <summary>The declared columns of everything a model could read, for lineage and nullability.</summary>
    public static IReadOnlyList<SchemaTableSpec> UpstreamSchema(ProjectContext ctx, string modelName) => UpstreamSpecs(ctx, modelName);

    /// <summary>
    /// The query analysed against the declared columns of the tables it names (not of every table of the project: handing the analyzer all of them for each model made the cost grow with the square of the
    /// models). A text whose tables cannot be found is analysed against all of them.
    /// </summary>
    public static (QueryFacts? Facts, string? Error) AnalyzeAgainstUpstream(ProjectContext ctx, string modelName, string sql)
    {
        var all = UpstreamSpecs(ctx, modelName);
        var named = QueryAnalyzer.Analyze(sql).Facts?.BaseTables;
        if (named == null) return QueryAnalyzer.Analyze(sql, all);
        var wanted = named.Select(t => ((t.SchemaName ?? "").ToLowerInvariant(), t.Table.ToLowerInvariant())).ToHashSet();
        return QueryAnalyzer.Analyze(sql, all.Where(s => wanted.Contains(((s.SchemaName ?? "").ToLowerInvariant(), s.Name.ToLowerInvariant()))).ToList());
    }

    private static List<SchemaTableSpec> UpstreamSpecs(ProjectContext ctx, string modelName)
    {
        var upstream = ctx.Project.Models.Where(m => m.Name != modelName).Select(m => (m.Name, m.Columns))
            .Concat(ctx.Project.DeclaredModelsAndStaging.Select(d => (d.Name, d.Columns))).ToList();
        return upstream.Select(u =>
        {
            var i = u.Name.LastIndexOf('.');
            return new SchemaTableSpec(i < 0 ? null : u.Name[..i], i < 0 ? u.Name : u.Name[(i + 1)..], u.Columns.Select(c => new SchemaColumnSpec(c.Name, c.Type, c.Nullable)).ToList());
        }).ToList();
    }

    /// <summary>The path of a declared model's file: its own, else the conventional path (`staging.orders` is `models/staging/orders.yml`).</summary>
    public static string ModelFile(DeclaredModel d) => d.File.Length > 0 ? d.File : ModelFile(d.Name);

    /// <summary>The conventional path of a model's definition: `staging.orders` is `models/staging/orders.yml`.</summary>
    public static string ModelFile(string name) => $"{ProjectValidator.ModelsDir}/{name.Replace('.', '/')}.yml";

    /// <summary>A native model's text is part of what it is, so a change to it changes the hash.</summary>
    private static string DefinitionHash(DeclaredModel d) => Hashing.ScriptHash(DeclaredModelWriter.Yaml(d, withInfo: false) + (d.Native == null ? "" : "\n" + d.Native.Access + "\n" + d.Native.Text));

    /// <summary>The settings of a mapped or native model's kind, as a model's are: empty for a mapped model; for a native one how it is run, where, the hash of its text (the text itself is in the file), and the tables it says it reads.</summary>
    public static object KindSettings(DeclaredModel d) => d.Native == null ? new { } : new
    {
        access = d.Native.Access,
        connections = d.Connections ?? [],
        text_hash = Hashing.ScriptHash(d.Native.Text),
        reads = d.Native.Reads,
    };

    /// <summary>
    /// The metadata documents of the models of any kind, ordered by name: the ones named (all when null). The same document for a built model and for a declared one, `kind` saying which; a declared model has no query, loads,
    /// hooks or lineage, so those properties are left out of its document.
    /// </summary>
    public static List<(string Name, object Document)> Models(ProjectContext ctx, IReadOnlyCollection<string>? names = null)
    {
        var wanted = names?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var built = ctx.Project.BuiltModels.Where(s => wanted == null || wanted.Contains(s.Definition.Name)).Select(s => (Name: s.Definition.Name, Make: (Func<object>)(() => Model(ctx, s, s.ReadQuery(ctx.Root, ctx.Config)))));
        var declared = ctx.Project.DeclaredModels.Where(d => wanted == null || wanted.Contains(d.Name)).Select(d => (Name: d.Name, Make: (Func<object>)(() => Model(ctx, d))));
        return built.Concat(declared).OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => (x.Name, x.Make())).ToList();
    }

    /// <summary>The document of a declared (mapped or native) model.</summary>
    public static object Model(ProjectContext ctx, DeclaredModel d)
    {
        var targets = d.Connections ?? ctx.Config.DefaultConnections;
        var ddls = targets.ToDictionary(t => t, t => TargetRegistry.Get(ctx.Config.EngineOf(t) ?? t).CreateDdl(ctx.Config.ForConnection(t)));
        object Native(ColumnDefinition c) => targets.ToDictionary(t => t, t =>
        {
            try { var n = ddls[t].Map(d.Name, c); return (object)new { type = n.Declaration, collation = n.Collation }; }
            catch (DdlUnsupportedException ex) { return new { error = ex.Diagnostic.Code, found = ex.Diagnostic.Found }; }
        });
        var kindOf = KindLookup(ctx);
        return new
        {
            schema = ModelSchema,
            name = d.Name,
            title = d.Info.Title, description = d.Info.Description,
            kind = d.Kind,
            kind_settings = KindSettings(d),
            grain = d.Grain,
            tags = d.Info.TagsForDocument,
            properties = d.Info.PropertiesForDocument,
            connections = targets,
            files = new { definition = ModelFile(d), query = (string?)null },
            definition_hash = DefinitionHash(d),
            upstream = d.Native?.Reads.Select(r => { var (category, kind) = kindOf(r); return new { name = r, category, kind }; }).ToList(),
            columns = d.Columns.Select(c => new { name = c.Name, title = c.Info?.Title, description = c.Info?.Description, tags = c.Info?.TagsForDocument, properties = c.Info?.PropertiesForDocument, logical_type = c.Type, nullable = c.Nullable, collation = c.Collation, native = Native(c) }).ToList(),
            indexes = d.Indexes.Select(i => new { name = i.Name, title = i.Info?.Title, description = i.Info?.Description, tags = i.Info?.TagsForDocument, properties = i.Info?.PropertiesForDocument, columns = i.Columns, unique = i.Unique, include = i.Include }).ToList(),
            foreign_keys = d.ForeignKeys.Select(f => new { name = f.Name, columns = f.Columns, references = new { table = f.Table, columns = f.ReferencedColumns } }).ToList(),
        };
    }

    public static object Model(ProjectContext ctx, BuiltModel built, string sql)
    {
        var def = built.Definition;
        var targets = ctx.TargetsOf(def);
        var hash = ctx.DefinitionHashOf(sql);

        // lineage and inferred nullability, against the declared columns of everything upstream
        var (facts, _) = AnalyzeAgainstUpstream(ctx, def.Name, ctx.AnalysisSql(built, sql));

        var ddls = targets.ToDictionary(t => t, t => TargetRegistry.Get(ctx.Config.EngineOf(t) ?? t).CreateDdl(ctx.Config.ForConnection(t)));      // the DDL is the engine's; the keys are the model's connections
        object Native(ColumnDefinition c) => targets.ToDictionary(t => t, t =>
        {
            try { var n = ddls[t].Map(def.Name, c); return (object)new { type = n.Declaration, collation = n.Collation }; }
            catch (DdlUnsupportedException ex) { return new { error = ex.Diagnostic.Code, found = ex.Diagnostic.Found }; }
        });
        object? Lineage(ColumnDefinition c)
        {
            var p = facts?.Projections.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            return p == null ? null : new
            {
                transform = p.TransformKind,
                cast_type = p.CastType,
                inferred_nullability = p.Nullability,
                upstream = p.Upstream.Select(u => new { table = u.Table, column = u.Column }).ToList(),
            };
        }

        var shapeHashes = new Dictionary<string, object?>();
        foreach (var t in targets)
        {
            try { shapeHashes[t] = Hashing.ShapeHash(DdlGenerator.ExpectedShape(ddls[t].MapAll(def))); }
            catch (DdlUnsupportedException ex) { shapeHashes[t] = new { error = ex.Diagnostic.Code }; }
        }

        var (render, _) = ctx.RenderModel(built, sql, targets);
        var lowered = ctx.Lowering.Enabled ? ctx.Lowering.Lower(built, sql, built.QueryParameterList(ctx.Root, ctx.Config)).Model : null;
        var rendered = render.Operations.OrderBy(o => o.Target, StringComparer.Ordinal).ThenBy(o => o.Operation, StringComparer.Ordinal).Select(o =>
        {
            var op = render.Loads.FirstOrDefault(l => l.Target == o.Target && l.Operation == o.Operation);
            return new
            {
                connection = o.Target, operation = o.Operation, strategy = o.Strategy, is_default = o.IsDefault, matrix_status = o.Status, findings = o.Findings,
                script_path = op?.ScriptPath, script_hash = op == null ? null : Hashing.ScriptHash(op.Script),
                resolver_path = op?.ResolverPath, resolver_hash = op?.Resolver == null ? null : Hashing.ScriptHash(op.Resolver),
                parameters = op?.Parameters.Select(p => new { name = p.Name, type = p.Type, built = p.Source, constraint = p.Constraint }).ToList(),
            };
        }).ToList();

        var hooks = new Dictionary<string, object>();
        foreach (var t in targets)
        {
            var resolved = HookReader.Resolve(def, ctx.Config, t, built.DefinitionFile, new List<Diagnostic>());
            hooks[t] = resolved.Select(h =>
            {
                var path = Path.Combine(ctx.Root, h.ScriptPath);
                return new { name = h.Name, @event = h.Event, group = h.Group, script = h.ScriptPath, script_hash = File.Exists(path) ? Hashing.ScriptHash(File.ReadAllText(path)) : null, effect = h.Effect, risk = h.Risk };
            }).ToList();
        }

        return new
        {
            schema = ModelSchema,
            name = def.Name,
            title = def.Info.Title, description = def.Info.Description,
            kind = def.Kind,
            kind_settings = KindSettings(def),
            grain = def.Grain,
            tags = def.Info.TagsForDocument,
            properties = def.Info.PropertiesForDocument,
            connections = targets,
            files = new { definition = built.DefinitionFile, query = built.QueryFile },
            inherited = built.Inherited.Select(o => new { path = o.Path, file = o.File, line = o.Line, value = o.Value }).ToList(),
            definition_hash = hash,
            upstream = UpstreamOf(ctx, built, sql, facts != null)?.Select(u => new { name = u.Name, category = u.Category, kind = u.Kind }).ToList(),
            rewrites_off = RewriteCatalog.For(ctx.Config, def).Disabled.Order(StringComparer.Ordinal).ToList(),
            lowered = lowered == null ? null : new
            {
                file = $"rendered/{lowered.ArtifactPath}", hash = lowered.Hash, rules = lowered.Query.Rules,
                output = lowered.Query.Columns.Select(c => new { name = c.Name, duckdb_type = c.DuckDbType }).ToList(),
            },
            columns = def.Columns.Select(c => new
            {
                name = c.Name, title = c.Info?.Title, description = c.Info?.Description, tags = c.Info?.TagsForDocument, properties = c.Info?.PropertiesForDocument,
                logical_type = c.Type, duckdb_type = lowered?.Query.Columns.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase))?.DuckDbType,
                nullable = c.Nullable, collation = c.Collation, native = Native(c), lineage = Lineage(c),
            }).ToList(),
            expected_shape_hash = shapeHashes,
            renames = def.Renames.Select(r => new { from = r.From, to = r.To }).ToList(),
            loads = rendered,
            indexes = def.Indexes.Select(i => new { name = i.Name, title = i.Info?.Title, description = i.Info?.Description, tags = i.Info?.TagsForDocument, properties = i.Info?.PropertiesForDocument, columns = i.Columns, unique = i.Unique, include = i.Include, connections = i.Targets }).ToList(),
            hooks,
            index_advice = IndexAdvisor.For(def, targets).Select(a => new
            {
                code = a.Code, severity = a.Severity, reason = a.Reason, columns = a.Columns, unique = a.WantUnique,
                suggested = a.SuggestedName, existing_index = a.Existing, connections = a.Targets, silenced = def.LintIgnore.Contains(a.Code) || !ctx.Config.LintIndexes,
            }).ToList(),
        };
    }
}
