using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

/// <summary>
/// The offline checks of `validate`, shared with `plan` (DESIGN.md 13, layers 2 to 4): matrix lint per declared target, every declared model x target x
/// operation pair renders and passes offline validation, and the collation profile holds. Loading and per-file schema checks are done earlier by the loaders.
/// </summary>
internal static class ProjectChecks
{
    /// <param name="onlyTargets">Restrict lint and rendering to these targets (null: each model's own targets).</param>
    public static List<Diagnostic> Run(IReadOnlyList<BuiltModel> models, ProjectConfig config, IReadOnlyList<string>? onlyTargets, string? projectRoot = null, ModelLowering? lowering = null)
    {
        var matrixDiags = new List<Diagnostic>();
        var matrix = MatrixLoader.LoadEmbedded(matrixDiags);
        if (matrixDiags.Count > 0) throw new InvalidOperationException("The embedded support matrix is invalid: " + string.Join("; ", matrixDiags.Select(d => d.Found)));
        var linter = new MatrixLinter(matrix);
        var renderer = new LoadRenderer(matrix, linter, config);
        var diagnostics = new List<Diagnostic>();
        if (lowering != null) diagnostics.AddRange(lowering.CheckMacros());
        foreach (var built in models)
        {
            foreach (var problem in built.QueryParameterProblems(projectRoot ?? Directory.GetCurrentDirectory(), config))
                diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(built.QueryFile, 0, 0), $"{built.Definition.Name}: {problem}."));
            if (built.QueryParameterProblems(projectRoot ?? Directory.GetCurrentDirectory(), config).Count > 0) continue;
            var root = projectRoot ?? Directory.GetCurrentDirectory();
            var targets = (built.Definition.Targets ?? config.DefaultConnections).Where(t => onlyTargets == null || onlyTargets.Contains(t)).ToList();
            // a model that names something by a parameter reads a different query on connections that give different names: each such query is checked on its own
            var variants = built.QueryVariants(root, config, targets);
            foreach (var (sql, variantTargets, variant) in variants.DefaultIfEmpty((Sql: built.ReadQuery(root, config), Targets: (IReadOnlyList<string>)targets, Variant: (string?)null)))
            {
                if (lowering is { Enabled: false } && lowering.ReachesMacros(sql))
                {
                    diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(built.QueryFile, 0, 0), $"{built.Definition.Name} calls a macro of the project, and a macro is expanded by DuckDB while the query is lowered, which is switched off.",
                        Fix: "Remove `lowering: { enabled: false }` from dbdatabuild.yml, or do not call macros."));
                    continue;
                }
                // with lowering on, the matrix lint and the transpile work on the lowered query (what actually runs), and findings point at its committed artifact
                var body = sql;
                string? bodyFile = null;
                if (lowering is { Enabled: true })
                {
                    var (lowered, error) = lowering.Lower(built, sql, built.QueryParameterList(root, config), variant);
                    if (lowered == null) { diagnostics.Add(error!); continue; }
                    body = lowered.Sql;
                    bodyFile = $"rendered/{lowered.ArtifactPath}";
                }
                // each target is linted on the query it will actually get: the lowered one with that target's rules applied
                var rewrites = RewriteCatalog.For(config, built.Definition, diagnostics);
                foreach (var t in variantTargets) diagnostics.AddRange(linter.Lint(DbDataBuild.Targets.Rules.TargetRules.Apply(body, config.EngineOf(t) ?? t, rewrites, config.TargetVersions.TryGetValue(t, out var tv) ? tv : null).Sql, bodyFile ?? built.QueryFile, [t], config));
                // every declared model x target x operation pair must render (in memory; nothing is written), and the scripts must pass offline validation
                if (config.LintSlices) diagnostics.AddRange(SliceAdvice(built, variantTargets, body));
                if (config.LintSlices && lowering != null) diagnostics.AddRange(SliceSourceAdvice(built, variantTargets, body, lowering));
                if (lowering != null && !built.Definition.LintIgnore.Contains(DiagnosticCatalog.StringsCompareDifferently.Code)) diagnostics.AddRange(StringProfileAdvice(built, variantTargets, config, lowering, body));
                diagnostics.AddRange(renderer.Render(built.Definition, body, built.QueryFile, variantTargets, bodyFile, built.QueryParameterList(root, config), lowering?.NativeUsesFor(body) ?? []).Diagnostics.Where(d => d.Code != DiagnosticCatalog.SqlParseFailure.Code));
            }
            diagnostics.AddRange(NameLengths(built, config, targets));
            foreach (var target in targets) HookLoader.Load(built, config, target, projectRoot ?? Directory.GetCurrentDirectory(), diagnostics);   // missing or unparseable hook scripts
        }
        if (config.LintIndexes)
            foreach (var built in models)
                diagnostics.AddRange(IndexAdvice(built, (built.Definition.Targets ?? config.DefaultConnections).Where(t => onlyTargets == null || onlyTargets.Contains(t)).ToList()));
        diagnostics.AddRange(CollationChecker.Check(config, models));
        return diagnostics;
    }

    /// <summary>The longest name an engine keeps: 63 bytes on PostgreSQL (it cuts a longer one without a word), 128 characters on SQL Server and Fabric.</summary>
    internal static bool TooLong(string engine, string name) => engine == "postgres" ? System.Text.Encoding.UTF8.GetByteCount(name) > 63 : name.Length > 128;

    /// <summary>The names a model gives (its schema name, object name, columns and indexes) that an engine it is built on would cut or refuse (DDB-241).</summary>
    internal static IEnumerable<Diagnostic> NameLengths(BuiltModel built, ProjectConfig config, IReadOnlyList<string> targets)
    {
        var def = built.Definition;
        var names = new List<(string Kind, string Name)>();
        var dot = def.Name.LastIndexOf('.');
        if (dot > 0) { names.Add(("schema name", def.Name[..dot])); names.Add(("object name", def.Name[(dot + 1)..])); } else names.Add(("object name", def.Name));
        names.AddRange(def.Columns.Select(c => ("column", c.Name)));
        names.AddRange(def.Indexes.Select(i => ("index", i.Name)));
        foreach (var target in targets)
        {
            var engine = config.EngineOf(target) ?? target;
            foreach (var (kind, name) in names.Where(n => TooLong(engine, n.Name)))
                yield return new Diagnostic(DiagnosticCatalog.NameTooLongForEngine, new(built.DefinitionFile, 0, 0),
                    $"{def.Name}: the {kind} `{(name.Length > 40 ? name[..40] + "..." : name)}` is {(engine == "postgres" ? $"{System.Text.Encoding.UTF8.GetByteCount(name)} bytes, and PostgreSQL keeps 63" : $"{name.Length} characters, and {(engine == "fabric" ? "Fabric" : "SQL Server")} accepts 128")} (connection `{target}`).");
        }
    }

    /// <summary>
    /// A query runs on one connection (DDB-231): every table a model reads must exist on each connection the model is built on, as a mapped model declared there or a model (or copy) built there. The staging
    /// table of a copy is its own. Reads of a table the project does not know are reported by the lowering (DDB-218), not here.
    /// </summary>
    public static List<Diagnostic> Reachability(ProjectContext ctx, IReadOnlyList<string>? onlyTargets)
    {
        var found = new List<Diagnostic>();
        var config = ctx.Config;
        IReadOnlyList<string>? ConnectionsOf(string table)
        {
            if (ctx.Project.BuiltModels.FirstOrDefault(s => string.Equals(s.Definition.Name, table, StringComparison.OrdinalIgnoreCase)) is { } model) return ctx.TargetsOf(model.Definition);
            if (ctx.Project.DeclaredModelsAndStaging.FirstOrDefault(d => string.Equals(d.Name, table, StringComparison.OrdinalIgnoreCase)) is { } mapped) return mapped.Connections ?? config.DefaultConnections;
            return null;
        }
        // a table read through a name that differs between connections is read on the connections that give it that name only
        bool ReadsOn(BuiltModel built, string target, string read) =>
            !built.HasNames(ctx.Root, ctx.Config) || read.EndsWith("()", StringComparison.Ordinal) || ctx.BaseTablesOf(built, built.ReadQuery(ctx.Root, ctx.Config, target)).Contains(read, StringComparer.OrdinalIgnoreCase);
        foreach (var n in ctx.Project.NativeModels.Where(n => n.Native is { Reads.Count: 0 }).OrderBy(n => n.Name, StringComparer.Ordinal))
            found.Add(new Diagnostic(DiagnosticCatalog.NativeReadsNotDeclared, new(n.Native!.File, n.Native.Line, 0), $"{n.Name} is a native {n.Native.Access} and does not declare what it reads, so it has no ancestors in the graph."));
        foreach (var built in ctx.Project.BuiltModels.OrderBy(s => s.Definition.Name, StringComparer.Ordinal))
        {
            var targets = ctx.TargetsOf(built.Definition).Where(t => onlyTargets == null || onlyTargets.Contains(t)).ToList();
            foreach (var read in ctx.Graph.Reads(built.Definition.Name))
            {
                if (built.HasNames(ctx.Root, ctx.Config) && targets.All(t => !ctx.BaseTablesOf(built, built.ReadQuery(ctx.Root, ctx.Config, t)).Contains(read, StringComparer.OrdinalIgnoreCase)) && !read.EndsWith("()", StringComparison.Ordinal)) continue;
                if (ctx.Project.NativeModels.FirstOrDefault(n => string.Equals(n.Name, read, StringComparison.OrdinalIgnoreCase)) is { Native.Access: NativeQuery.Command })
                    found.Add(new Diagnostic(DiagnosticCatalog.ModelReadsAnotherConnection, new(built.DefinitionFile, 0, 0), $"{built.Definition.Name} reads `{read}`, a native command: a command can only be run, never read inside a query.",
                        Fix: $"Copy it (`kind: {{copy: {{from: {read}}}}}`, on its own connection or another) and read the copy."));
                if (ConnectionsOf(read) is { } where)
                    foreach (var target in targets.Where(t => !where.Contains(t, StringComparer.Ordinal) && ReadsOn(built, t, read)))
                        found.Add(new Diagnostic(DiagnosticCatalog.ModelReadsAnotherConnection, new(built.DefinitionFile, 0, 0),
                            $"{built.Definition.Name} is built on `{target}` and reads `{read}`, which is on {string.Join(", ", where.Select(w => $"`{w}`"))}, not on `{target}`.",
                            Fix: $"Copy `{read}` to `{target}` (a model of `kind: {{copy: {{from: {read}}}}}` on `{target}`) and read the copy, or build {built.Definition.Name} on {where[0]}."));
            }
        }
        // a copy that reads from a Flight SQL server whose dialect nobody has said: its read is written for ansi until `project update` has asked the server and committed the answer
        var copyOrigins = ctx.Project.BuiltModels.Where(s => s.Definition.IsCopy).SelectMany(s => ctx.TargetsOf(s.Definition).Where(t => onlyTargets == null || onlyTargets.Contains(t)).SelectMany(t => ctx.OriginsOf(s.Definition, t))).Select(o => o.Connection);
        foreach (var name in DialectLockFile.Missing(config, copyOrigins))
            found.Add(new Diagnostic(DiagnosticCatalog.DialectLockMissing, new(ProductInfo.DialectLockFile, 0, 0),
                $"The connection `{name}` is `{config.Connections[name].Kind.Describe()}` with no dialect keyword and no entry in {ProductInfo.DialectLockFile}, so the copies that read from it are written for `ansi`. Run `{ProductInfo.Cli} project update` to ask the server and commit its dialect, or name the dialect in `engine`."));
        return found;
    }

    private static readonly string[] EqualityContexts = ["filter", "join", "group", "having", "window_partition", "distinct", "set_operation"];

    /// <summary>
    /// DDB-236 (advice): a model built on connections that compare strings differently, whose query uses string columns where that matters. Trailing spaces alone are not an issue for the columns the model declares
    /// `trimmed: true` (and ordering does not depend on them). Silenced with `lint_ignore: [DDB-236]`.
    /// </summary>
    internal static IEnumerable<Diagnostic> StringProfileAdvice(BuiltModel built, IReadOnlyList<string> targets, ProjectConfig config, ModelLowering lowering, string body)
    {
        var differences = config.StringProfileDifferences(targets);
        if (differences.Count == 0) return [];
        var dimensions = differences.Select(d => d.Dimension).ToHashSet(StringComparer.Ordinal);
        var onlyTrailing = dimensions.Count == 1 && dimensions.Contains("trailing_space");
        var uses = lowering.StringUses(built.Definition.Name, body)
            .Where(u => !onlyTrailing || (EqualityContexts.Contains(u.Context) && !u.Trimmed)).ToList();
        if (uses.Count == 0) return [];
        static string Where(string context) => context switch
        {
            "filter" => "a filter", "join" => "a join", "group" => "a GROUP BY", "having" => "a HAVING", "window_partition" => "a window partition", "window_order" => "a window order",
            "order" => "an ORDER BY", "distinct" => "a DISTINCT", _ => "a set operation",
        };
        var shown = string.Join(", ", uses.Take(3).Select(u => $"`{u.Column}` in {Where(u.Context)}")) + (uses.Count > 3 ? $" and {uses.Count - 3} more" : "");
        var how = string.Join("; ", differences.Select(d => $"{d.Dimension}: {string.Join(", ", d.Values.Select(v => $"{v.Connection} {v.Value}"))}"));
        return [new Diagnostic(DiagnosticCatalog.StringsCompareDifferently, new(built.QueryFile, 0, 0), $"{built.Definition.Name} uses string columns where it matters how strings compare ({shown}), and it is built on connections that compare them differently ({how}): it can return different rows on each.")];
    }

    /// <summary>Index lint (DDB-223, DDB-224): advice only, with the exact index to declare. A model silences single codes with `lint_ignore`.</summary>
    internal static IEnumerable<Diagnostic> IndexAdvice(BuiltModel built, IReadOnlyList<string> targets)
    {
        var def = built.Definition;
        foreach (var a in IndexAdvisor.For(def, targets).Where(a => !def.LintIgnore.Contains(a.Code)))
        {
            var columns = string.Join(", ", a.Columns);
            var what = a.Reason switch
            {
                IndexReason.MergeKey => $"loads by key ({columns})",
                IndexReason.TimeColumn => $"loads by the time column ({columns})",
                IndexReason.Watermark => $"reads its watermark column ({columns})",
                _ => $"deletes by the range column ({columns})",
            };
            var found = a.Existing != null
                ? $"{def.Name} {what}; index `{a.Existing}` leads with it but is not declared unique, so the engine will not reject a duplicate key ({string.Join(", ", a.Targets)})."
                : $"{def.Name} {what}, but no declared index leads with it, so each load scans the table ({string.Join(", ", a.Targets)}).";
            var fix = a.Existing != null
                ? $"Add `unique: true` to index `{a.Existing}` if you want the engine to enforce the key; leaving it is allowed."
                : $"Add under `indexes:` in {built.DefinitionFile}:  {Models.IndexAdvisor.Yaml(a)}";
            yield return new Diagnostic(a.Severity == Severity.Warning ? DiagnosticCatalog.MergeKeyNotIndexed : DiagnosticCatalog.LoadColumnNotIndexed, new(built.DefinitionFile, 0, 0), found, Fix: fix);
        }
    }

    /// <summary>
    /// Slice lint (DDB-239): the slice column of a load that reads only the rows after its watermark or in its range is read from a source column, and the project says what indexes that source has, and none
    /// leads with the column. Advice only; the tool never creates an index on a source.
    /// </summary>
    internal static IEnumerable<Diagnostic> SliceSourceAdvice(BuiltModel built, IReadOnlyList<string> targets, string body, ModelLowering lowering)
    {
        var def = built.Definition;
        if (def.LintIgnore.Contains(DiagnosticCatalog.LoadSliceSourceNotIndexed.Code)) yield break;
        var done = new HashSet<(string Operation, string Column)>();
        foreach (var target in targets)
            foreach (var op in LoadPlan.For(def, target))
            {
                var (column, what) = op.Strategy switch
                {
                    LoadStrategies.WatermarkAppend when op.Watermark?.Column is { } w => (w, "loads rows at or after its watermark"),
                    LoadStrategies.DeleteInsertByRange => (op.Column ?? def.TimeColumn, "reloads a range"),
                    _ => (null, ""),
                };
                if (column == null || !done.Add((op.Name, column))) continue;
                foreach (var (table, sourceColumn) in lowering.MappedColumnsWithoutLeadingIndex(body, column))
                    yield return new Diagnostic(DiagnosticCatalog.LoadSliceSourceNotIndexed, new(built.DefinitionFile, 0, 0),
                        $"{def.Name} / {op.Name} {what} by `{column}`, which the query reads from `{table}.{sourceColumn}`, and no index declared for `{table}` leads with that column, so each load scans the source to find the slice.",
                        Fix: $"If `{table}` has an index that leads with `{sourceColumn}`, add it under `indexes:` in its declaration (`dbdatabuild project import {table} --write` exports it); otherwise ask its owner for one. Silence this with `lint_ignore: [DDB-239]` if the cost is acceptable.");
            }
    }

    /// <summary>
    /// Slice lint (DDB-225): for each load that selects its slice from the finished query, whether the filter can be applied below the query's aggregates and windows. Advice only;
    /// the query is never rewritten. A model silences it with `lint_ignore`.
    /// </summary>
    internal static IEnumerable<Diagnostic> SliceAdvice(BuiltModel built, IReadOnlyList<string> targets, string body)
    {
        var def = built.Definition;
        if (def.LintIgnore.Contains(DiagnosticCatalog.LoadSliceNotPushable.Code)) yield break;
        var done = new HashSet<(string Operation, string Column)>();
        foreach (var target in targets)
            foreach (var op in LoadPlan.For(def, target))
            {
                var (column, what) = op.Strategy switch
                {
                    LoadStrategies.WatermarkAppend when op.Watermark?.Column is { } w => (w, "loads rows at or after its watermark"),
                    LoadStrategies.DeleteInsertByRange => (op.Column ?? def.TimeColumn, "reloads a range"),
                    _ => (null, ""),
                };
                if (column == null || !done.Add((op.Name, column))) continue;
                var blockers = DbDataBuild.Sql.Analysis.SliceAnalyzer.Analyze(body, column);
                if (blockers.Count == 0) continue;
                yield return new Diagnostic(DiagnosticCatalog.LoadSliceNotPushable, new(built.DefinitionFile, 0, 0),
                    $"{def.Name} / {op.Name} {what} by `{column}`, which is applied to the finished query, but {string.Join("; and ", blockers.Select(b => b.Detail))}. " +
                    "Every load therefore does the work for the whole history and keeps only the slice.",
                    Fix: "Slice by a grouping or partition column, or use a strategy that does not slice the finished result (for example filter the source inside the query yourself and use full_replace or a key-based load). Silence this with `lint_ignore: [DDB-225]` if the cost is acceptable.");
            }
    }
}

