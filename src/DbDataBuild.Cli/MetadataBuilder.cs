using System.Text.Json;
using DbDataBuild.Core;
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
    public const string ModelSchema = "dbdatabuild.model/1";
    public const string ProjectSchema = "dbdatabuild.project/1";
    public const string SourceSchema = "dbdatabuild.source/1";

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
                connections = cfg.Connections.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => (object)new { engine = c.Value.Engine, version = c.Value.Version, title = c.Value.Info.Title, description = c.Value.Info.Description, tags = c.Value.Info.TagsForDocument, properties = c.Value.Info.PropertiesForDocument }),
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
            sources = ctx.Project.Descriptors.Concat(ctx.Project.NativeModels).OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => new
            {
                name = d.Name,
                file = SourceFile(d),
                definition_hash = DefinitionHash(d),
                native = NativeJson(d),
            }).ToList(),
            models = ctx.Project.Sources.OrderBy(s => s.Definition.Name, StringComparer.Ordinal).Select(s => new
            {
                name = s.Definition.Name,
                kind = s.Definition.KindType,
                connections = ctx.TargetsOf(s.Definition),
                definition_hash = ctx.DefinitionHashOf(s.ReadQuery(ctx.Root, ctx.Config)),
            }).ToList(),
        };
    }

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
            .Concat(ctx.Project.AllDescriptors.Select(d => (d.Name, d.Columns))).ToList();
        return upstream.Select(u =>
        {
            var i = u.Name.LastIndexOf('.');
            return new SchemaTableSpec(i < 0 ? null : u.Name[..i], i < 0 ? u.Name : u.Name[(i + 1)..], u.Columns.Select(c => new SchemaColumnSpec(c.Name, c.Type, c.Nullable)).ToList());
        }).ToList();
    }

    /// <summary>The source descriptors a model's query reads, by name.</summary>
    public static IReadOnlyList<string> SourcesRead(ProjectContext ctx, string modelName, string sql)
    {
        var (facts, _) = AnalyzeAgainstUpstream(ctx, modelName, sql);
        var names = ctx.Project.AllDescriptors.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return facts?.BaseTables.Select(b => b.QualifiedName).Where(names.Contains).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).ToList() ?? [];
    }

    /// <summary>Which models read each source, across the whole project (so a source's document does not change with the models selected for a run).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Consumers(ProjectContext ctx)
    {
        var result = ctx.Project.AllDescriptors.ToDictionary(d => d.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var m in ctx.Project.Sources.OrderBy(m => m.Definition.Name, StringComparer.Ordinal))
            foreach (var n in SourcesRead(ctx, m.Definition.Name, m.ReadQuery(ctx.Root, ctx.Config))) result[n].Add(m.Definition.Name);
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
    /// <summary>The path of a mapped model: `staging.orders` is `models/staging/orders.yml`.</summary>
    /// <summary>The path of a source descriptor: `staging.orders` is `sources/staging/orders.yml`.</summary>
    public static string SourceFile(string name) => $"{ProjectValidator.ModelsDir}/{name.Replace('.', '/')}.yml";

    /// <summary>The metadata document of a source descriptor: what the project declares about a table it reads but does not build, and which models read it.</summary>
    /// <summary>The file a source is declared in: the native model's own, else the conventional path of a mapped one.</summary>
    public static string SourceFile(SourceDescriptor d) => d.File.Length > 0 ? d.File : d.Native is { File.Length: > 0 } n ? n.File.Replace('\\', '/') : SourceFile(d.Name);

    /// <summary>A native model's text is part of what it is, so a change to it changes the hash.</summary>
    private static string DefinitionHash(SourceDescriptor d) => Hashing.ScriptHash(SourceDescriptorWriter.Yaml(d, withInfo: false) + (d.Native == null ? "" : "\n" + d.Native.Access + "\n" + d.Native.Text));

    /// <summary>What makes a source native, or null for a mapped one: how it is run, where, the hash of its text (the text itself is in the file), and the tables it says it reads.</summary>
    private static object? NativeJson(SourceDescriptor d) => d.Native == null ? null : new
    {
        access = d.Native.Access,
        connections = d.Connections ?? [],
        text_hash = Hashing.ScriptHash(d.Native.Text),
        reads = d.Native.Reads,
    };

    public static object Source(SourceDescriptor d, IReadOnlyList<string> consumers) => new
    {
        schema = SourceSchema,
        name = d.Name,
        title = d.Info.Title, description = d.Info.Description, tags = d.Info.TagsForDocument, properties = d.Info.PropertiesForDocument,
        file = SourceFile(d),
        definition_hash = DefinitionHash(d),
        native = NativeJson(d),
        grain = d.Grain,
        columns = d.Columns.Select(c => new { name = c.Name, title = c.Info?.Title, description = c.Info?.Description, tags = c.Info?.TagsForDocument, properties = c.Info?.PropertiesForDocument, logical_type = c.Type, nullable = c.Nullable, collation = c.Collation }).ToList(),
        indexes = d.Indexes.Select(i => new { name = i.Name, title = i.Info?.Title, description = i.Info?.Description, tags = i.Info?.TagsForDocument, properties = i.Info?.PropertiesForDocument, columns = i.Columns, unique = i.Unique, include = i.Include }).ToList(),
        foreign_keys = d.ForeignKeys.Select(f => new { name = f.Name, columns = f.Columns, references = new { table = f.Table, columns = f.ReferencedColumns } }).ToList(),
        consumers,
    };

    public static List<object> Sources(ProjectContext ctx, IEnumerable<string>? selectedModels)
    {
        var consumers = Consumers(ctx);
        var wanted = selectedModels?.ToHashSet(StringComparer.Ordinal);
        return ctx.Project.Descriptors.Concat(ctx.Project.NativeModels).OrderBy(d => d.Name, StringComparer.Ordinal)
            .Where(d => wanted == null || consumers[d.Name].Any(wanted.Contains))
            .Select(d => Source(d, consumers[d.Name])).ToList();
    }

    public static object Model(ProjectContext ctx, ModelSource source, string sql)
    {
        var def = source.Definition;
        var targets = ctx.TargetsOf(def);
        var hash = ctx.DefinitionHashOf(sql);

        // lineage and inferred nullability, against the declared columns of everything upstream
        var (facts, _) = AnalyzeAgainstUpstream(ctx, def.Name, ctx.AnalysisSql(source, sql));
        var known = ctx.Project.Models.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = ctx.Project.AllDescriptors.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        var (render, _) = ctx.RenderModel(source, sql, targets);
        var lowered = ctx.Lowering.Enabled ? ctx.Lowering.Lower(source, sql, source.QueryParameterList(ctx.Root, ctx.Config)).Model : null;
        var rendered = render.Operations.OrderBy(o => o.Target, StringComparer.Ordinal).ThenBy(o => o.Operation, StringComparer.Ordinal).Select(o =>
        {
            var op = render.Loads.FirstOrDefault(l => l.Target == o.Target && l.Operation == o.Operation);
            return new
            {
                connection = o.Target, operation = o.Operation, strategy = o.Strategy, is_default = o.IsDefault, matrix_status = o.Status, findings = o.Findings,
                script_path = op?.ScriptPath, script_hash = op == null ? null : Hashing.ScriptHash(op.Script),
                resolver_path = op?.ResolverPath, resolver_hash = op?.Resolver == null ? null : Hashing.ScriptHash(op.Resolver),
                parameters = op?.Parameters.Select(p => new { name = p.Name, type = p.Type, source = p.Source, constraint = p.Constraint }).ToList(),
            };
        }).ToList();

        var hooks = new Dictionary<string, object>();
        foreach (var t in targets)
        {
            var resolved = HookReader.Resolve(def, ctx.Config, t, source.DefinitionFile, new List<Diagnostic>());
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
            kind = new { type = def.KindType, unique_key = def.UniqueKey, time_column = def.TimeColumn, lookback = def.Lookback },
            grain = def.Grain,
            tags = def.Info.TagsForDocument,
            properties = def.Info.PropertiesForDocument,
            connections = targets,
            files = new { definition = source.DefinitionFile, query = source.QueryFile },
            inherited = source.Inherited.Select(o => new { path = o.Path, file = o.File, line = o.Line, value = o.Value }).ToList(),
            definition_hash = hash,
            upstream = facts == null && ctx.MacrosCalledBy(sql).Count == 0 ? null : ctx.BaseTablesOf(source, sql).Select(n => new { name = n, kind = known.Contains(n) ? "model" : ctx.Project.NativeModels.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)) ? "native" : sources.Contains(n) ? "source" : "unknown" })
                .Concat(ctx.MacrosCalledBy(sql).Select(m => new { name = m, kind = "macro" })).ToList(),
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
