using System.Security.Cryptography;
using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Sql.Matrix;

namespace DbDataBuild.Targets.Rendering;

/// <summary>A file the renderer produces. <see cref="Path"/> is relative to the rendered/ directory and uses '/'.</summary>
public sealed record RenderedFile(string Path, string Content);

/// <param name="Status">supported, emulated, approximated, unverified or unsupported: the worst matrix status among the strategy and the constructs it uses.</param>
public sealed record OperationReport(string Model, string Target, string Operation, string Strategy, bool IsDefault, string Status, IReadOnlyList<string> Findings);

/// <summary>
/// The resolver of one input of a load, as committed. An `expression` is its DuckDB text; a `target` or `source` resolver is a SELECT lowered for the engine it runs on, in the file at <see cref="Path"/> (relative to
/// rendered/) with <see cref="Text"/> as its content. A `source` resolver has one per engine of the copy's origins (<see cref="Engine"/>).
/// </summary>
public sealed record RenderedInput(string Name, string Kind, string Text, string? Engine = null);

/// <summary>A rendered load operation with everything a plan needs: the script as committed, its resolver, parameters and watermark rule.</summary>
public sealed record RenderedOperation(
    string Model, string Target, string Operation, bool IsDefault, string Strategy, string ScriptPath, string Script, string? ResolverPath, string? Resolver,
    IReadOnlyList<RenderedParameter> Parameters, WatermarkSpec? Watermark, IReadOnlyList<RenderedInput>? Inputs = null)
{
    public IReadOnlyList<RenderedInput> InputList => Inputs ?? [];
}

public sealed record RenderResult(IReadOnlyList<RenderedFile> Files, IReadOnlyList<OperationReport> Operations, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<RenderedOperation>? RenderedOperations = null)
{
    public IReadOnlyList<RenderedOperation> Loads => RenderedOperations ?? [];
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

/// <summary>
/// Renders the load operations of a model for each target (DESIGN.md 6.6). Offline and deterministic: no timestamps, no connection.
/// Every declared model x target x operation pair renders or fails with a diagnostic that names it.
/// </summary>
public sealed class LoadRenderer(SupportMatrix matrix, MatrixLinter linter, ProjectConfig config)
{
    private const string TailOfWrapper = "SELECT * FROM " + LoadersBodyName;
    private const string LoadersBodyName = Loaders.LoaderBase.BodyName;

    /// <param name="bodyFile">Where <paramref name="bodySql"/> is committed when it is not the author's file (the lowered query), so findings point at what was checked.</param>
    public RenderResult Render(ModelDefinition def, string bodySql, string queryFile, IReadOnlyList<string> targets, string? bodyFile = null, IReadOnlyList<QueryParameter>? queryParameters = null, IReadOnlyList<NativeUse>? natives = null)
    {
        var files = new List<RenderedFile>();
        var reports = new List<OperationReport>();
        var diags = new List<Diagnostic>();
        var operations = new List<RenderedOperation>();

        var (bodyHash, hashError) = AstHasher.Hash(bodySql);
        if (bodyHash == null)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.SqlParseFailure, new(queryFile, 0, 0), $"The DuckDB parser reported: {hashError}", Fix: SqlParseHints.Fix(bodySql, hashError)));
            return new RenderResult(files, reports, diags);
        }
        var sources = QueryAnalyzer.Analyze(bodySql).Facts?.BaseTables.Select(t => t.QualifiedName).Order(StringComparer.Ordinal).ToList() ?? [];

        foreach (var targetName in targets.Distinct().Order(StringComparer.Ordinal))
        {
            var engine = config.EngineOf(targetName) ?? targetName;      // a model names connections; the SQL is written for the connection's engine
            var target = TargetRegistry.Get(engine);
            var lint = linter.Lint(Rules.TargetRules.Apply(bodySql, engine, RewriteCatalog.For(config, def), config.TargetVersions.TryGetValue(targetName, out var tv) ? tv : null).Sql, bodyFile ?? queryFile, [targetName], config);
            var manifestOps = new List<ManifestOperation>();

            foreach (var op in LoadPlan.For(def, targetName))
            {
                var pair = $"{def.Name} x {targetName} x {op.Name}";
                var findings = FindingIds(lint);
                var blockers = Blockers(lint, op, targetName, engine, out var strategyStatus);
                if (blockers.Count > 0)
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.PairUnsupported, new(queryFile, 0, 0), $"{pair} cannot be rendered: {string.Join("; ", blockers)}.",
                        Fix: "Change the strategy or the query, or remove the target from the model or from this operation's `connections:`."));
                    reports.Add(new OperationReport(def.Name, targetName, op.Name, op.Strategy, op.IsDefault, "unsupported", findings));
                    continue;
                }

                var rendered = RenderOne(def, op, target, targetName, bodySql, bodyHash, queryFile, pair, diags, queryParameters ?? [], natives ?? []);
                if (rendered == null)
                {
                    reports.Add(new OperationReport(def.Name, targetName, op.Name, op.Strategy, op.IsDefault, "unsupported", findings));
                    continue;
                }

                var dir = $"{targetName}/{def.Name}";
                var scriptPath = $"{dir}/load.{op.Name}.sql";
                files.Add(new RenderedFile(scriptPath, rendered.Script));
                string? resolverPath = null;
                if (rendered.Resolver != null)
                {
                    resolverPath = $"{dir}/load.{op.Name}.resolve.sql";
                    files.Add(new RenderedFile(resolverPath, rendered.Resolver));
                }
                var status = WorstStatus(lint, strategyStatus);
                operations.Add(new RenderedOperation(def.Name, targetName, op.Name, op.IsDefault, op.Strategy, scriptPath, rendered.Script, resolverPath, rendered.Resolver, rendered.Parameters, op.Watermark));
                manifestOps.Add(new ManifestOperation(op.Name, op.IsDefault, op.Strategy, status, findings, Path.GetFileName(scriptPath), Sha(rendered.Script),
                    resolverPath == null ? null : Path.GetFileName(resolverPath), rendered.Resolver == null ? null : Sha(rendered.Resolver), rendered.Parameters));
                reports.Add(new OperationReport(def.Name, targetName, op.Name, op.Strategy, op.IsDefault, status, findings));
            }

            if (manifestOps.Count > 0)
                files.Add(new RenderedFile($"{targetName}/{def.Name}/manifest.yml",
                    ManifestWriter.Write(def.Name, targetName, bodyHash, sources, manifestOps)));
        }
        return new RenderResult(files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), reports, diags, operations);
    }

    /// <summary>
    /// A resolver's SELECT (DuckDB dialect, already lowered) made into the text its engine runs: the target rules, the matrix lint (a construct the engine cannot do refuses it), the transpile and a parse by the engine's
    /// own grammar. Offline and deterministic like a load script. The text starts with a comment that says what it is for.
    /// </summary>
    public (string? Text, IReadOnlyList<Diagnostic> Diagnostics) RenderInputQuery(ModelDefinition def, string operation, string input, string loweredSql, string connection, string engineConnection, string file)
    {
        var diags = new List<Diagnostic>();
        var engine = config.EngineOf(engineConnection) ?? engineConnection;
        var who = $"{def.Name}.{operation} input `{input}` on {engineConnection}";
        if (!TargetNames.All.Contains(engine))
        {
            // an origin that is not one of the three engines (a DuckDB file, an ODBC source) runs the resolver in the dialect it was lowered to: there is nothing to transpile and no engine rule to apply, and its own parser is the check
            var verdict = Polyglot.Validate(loweredSql, Dialects.Canonical);
            if (!verdict.Valid)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InputResolverInvalid, new(file, 0, 0), $"{who} cannot be rendered: the DuckDB parser reported: {verdict.Error ?? verdict.ErrorsJson}."));
                return (null, diags);
            }
            return ($"-- dbdatabuild input resolver. Generated by `project compile`; do not edit.\n-- model:      {def.Name}\n-- operation:  {operation}\n-- input:      {input}\n-- connection: {engineConnection}\n" + loweredSql.Trim().TrimEnd(';') + "\n", diags);
        }
        var target = TargetRegistry.Get(engine);
        int? version = config.TargetVersions.TryGetValue(engineConnection, out var v) ? v : null;
        var ruled = Rules.TargetRules.Apply(loweredSql, engine, RewriteCatalog.For(config, def), version);
        var lint = linter.Lint(ruled.Sql, file, [engineConnection], config);
        var blockers = lint.Where(d => d.Code == DiagnosticCatalog.ConstructUnsupported.Code).Select(d => d.Found).ToList();
        if (blockers.Count > 0)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.InputResolverInvalid, new(file, 0, 0), $"{who} cannot be rendered: {string.Join("; ", blockers)}.", Fix: "Rewrite the construct, or compute the value with an `expression` resolver."));
            return (null, diags);
        }
        var (outcome, transpiled) = Rules.TargetRules.Transpile(ruled.Sql.Trim().TrimEnd(';'), Dialects.Canonical, target.Dialect);
        if (transpiled == null)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.InputResolverInvalid, new(file, 0, 0), $"{who} cannot be rendered: the transpiler reported: {outcome.Error}."));
            return (null, diags);
        }
        transpiled = Rules.TargetRules.Finish(transpiled, target.Name).Trim().TrimEnd(';');
        if (target.Dialect is "tsql" or "fabric")
            transpiled = System.Text.RegularExpressions.Regex.Replace(transpiled, @"(GENERATE_SERIES\([^()]*\)) AS (\w+)\(value\)", "$1 AS $2", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var text = $"-- dbdatabuild input resolver. Generated by `project compile`; do not edit.\n-- model:      {def.Name}\n-- operation:  {operation}\n-- input:      {input}\n-- connection: {engineConnection}\n" + transpiled + "\n";
        diags.AddRange(target.Validate(text, file, version).Select(d => d with { Found = $"{who}: {d.Found}" }));
        return diags.Any(d => d.Severity == Severity.Error) ? (null, diags) : (text, diags);
    }

    private sealed record Rendered(string Script, string? Resolver, IReadOnlyList<RenderedParameter> Parameters);

    private Rendered? RenderOne(ModelDefinition def, LoadOperation op, ITarget target, string connection, string bodySql, string bodyHash, string queryFile, string pair, List<Diagnostic> diags, IReadOnlyList<QueryParameter> queryParameters, IReadOnlyList<NativeUse> natives)
    {
        // The body, as a CTE named ddb_body, transpiled by polyglot. The support matrix decides what is allowed: polyglot's own
        // `unsupportedLevel: raise` is not used because it misses constructs and also rejects supported ones (REGEXP_LIKE on SQL Server 2025).
        var ruled = Rules.TargetRules.Apply(bodySql, target.Name, RewriteCatalog.For(config, def), config.TargetVersions.TryGetValue(connection, out var targetVersion) ? targetVersion : null);
        bodySql = ruled.Sql;
        var wrapped = $"WITH {LoadersBodyName} AS ({bodySql.Trim().TrimEnd(';').TrimEnd()})\nSELECT * FROM {LoadersBodyName}";
        var (outcome, transpiled) = Rules.TargetRules.Transpile(wrapped, Dialects.Canonical, target.Dialect);
        if (transpiled == null)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.PairUnsupported, new(queryFile, 0, 0), $"{pair} cannot be rendered: the transpiler reported: {outcome.Error}.",
                Fix: "Rewrite the construct the transpiler names, or remove the target."));
            return null;
        }
        transpiled = Rules.TargetRules.Finish(transpiled, target.Name);
        // T-SQL takes no column list after a table function; the lowered series names its column `value`, which is what SQL Server's is called
        if (target.Dialect is "tsql" or "fabric")
            transpiled = System.Text.RegularExpressions.Regex.Replace(transpiled, @"(GENERATE_SERIES\([^()]*\)) AS (\w+)\(value\)", "$1 AS $2", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var tail = transpiled.TrimEnd();
        if (!tail.EndsWith(TailOfWrapper, StringComparison.Ordinal))
            throw new InvalidOperationException($"The transpiled body of {pair} does not end with `{TailOfWrapper}`; the renderer cannot place its own statements. This is a tool bug.");
        var prefix = tail[..(tail.Length - TailOfWrapper.Length)];
        // each parameter the query uses as a value becomes a placeholder the plan fills in and apply binds: the value is never in the text
        var (boundPrefix, usedParametersRaw, lostParameters) = QueryParameters.Bind(prefix, queryParameters);
        prefix = boundPrefix;
        IReadOnlyList<QueryParameter> usedParameters = usedParametersRaw;
        // a native model this query reads is spliced in as a derived table, in the engine's own text (never transpiled), with placeholders of its own
        var (splicedPrefix, nativeParameters) = NativeInline.Splice(prefix, natives, target.Name);
        prefix = splicedPrefix;
        usedParameters = [.. usedParameters, .. nativeParameters];
        foreach (var lost in lostParameters)
            diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(queryFile, 0, 0), $"{pair}: `${{{lost.Key}}}` is not a value in the lowered query any more (DuckDB folded it into a constant), so it cannot be bound.",
                Fix: "Use the parameter as a plain value, in a comparison or a projection, not inside an expression DuckDB can evaluate at plan time."));

        ColumnDefinition? Column(string? name) => name == null ? null : def.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (var k in op.Key.Select(Column).Where(c => c is { Nullable: true }))
            diags.Add(new Diagnostic(DiagnosticCatalog.KeyColumnNullable, new(queryFile, 0, 0), $"{pair}: key column `{k!.Name}` is nullable, so rows with a NULL key are never matched and would be inserted again on every load."));

        var request = new LoadRequest(def.Name, def.Columns, op, prefix, Column(op.Watermark?.Column)?.Type, Column(op.Column)?.Type);
        var script = target.Loader.Render(request);
        script = script with { Parameters = [.. script.Parameters, .. usedParameters.Select(p => new RenderedParameter(p.Placeholder, p.Type, "parameter", null))] };

        var header = Header(def.Name, op.Name, connection, op.Strategy, bodyHash, script.Parameters, resolver: false, ruled.Rules, RewriteCatalog.For(config, def));
        var text = header + script.Text;
        var resolverText = script.ResolverText == null ? null : Header(def.Name, op.Name, connection, op.Strategy, bodyHash, script.Parameters, resolver: true, ruled.Rules, RewriteCatalog.For(config, def)) + script.ResolverText + "\n";

        var before = diags.Count;
        int? version = config.TargetVersions.TryGetValue(connection, out var v) ? v : null;
        diags.AddRange(target.Validate(text, $"rendered/{connection}/{def.Name}/load.{op.Name}.sql", version).Select(d => d with { Found = $"{pair}: {d.Found}" }));
        if (resolverText != null)
            diags.AddRange(target.Validate(resolverText, $"rendered/{connection}/{def.Name}/load.{op.Name}.resolve.sql", version).Select(d => d with { Found = $"{pair} (resolver): {d.Found}" }));
        var declared = script.Parameters.Select(p => p.Name).ToList();
        foreach (var bad in Placeholders.Undeclared(text, declared))
            diags.Add(new Diagnostic(DiagnosticCatalog.PlaceholderUndeclared, new(queryFile, 0, 0), $"{pair}: the rendered script contains the placeholder `{bad}`, which is not a declared parameter."));
        if (resolverText != null)
            foreach (var bad in Placeholders.Undeclared(resolverText, []))
                diags.Add(new Diagnostic(DiagnosticCatalog.PlaceholderUndeclared, new(queryFile, 0, 0), $"{pair} (resolver): the resolver contains the placeholder `{bad}`; a resolver takes no parameters."));
        return diags.Skip(before).Any(d => d.Severity == Severity.Error) ? null : new Rendered(text, resolverText, script.Parameters);
    }

    private IReadOnlyList<string> Blockers(IReadOnlyList<Diagnostic> lint, LoadOperation op, string target, string engine, out SupportStatus strategyStatus)
    {
        var blockers = new List<string>();
        foreach (var d in lint.Where(d => d.Code == DiagnosticCatalog.ConstructUnsupported.Code))
            blockers.Add(d.Found);
        var row = matrix.Strategies.FirstOrDefault(r => r.Id == "strategy." + op.Strategy);
        strategyStatus = row?.Targets.GetValueOrDefault(engine)?.Status ?? SupportStatus.Unverified;
        if (row != null && row.Targets.TryGetValue(engine, out var entry))
        {
            if (entry.Status == SupportStatus.Unsupported) blockers.Add($"strategy `{op.Strategy}` is unsupported on {target}{(entry.Note == null ? "" : ": " + entry.Note)}");
            if (entry.MinVersion is { } min && config.TargetVersions.TryGetValue(target, out var version) && version < min)
                blockers.Add($"strategy `{op.Strategy}` needs {target} version {min} or later, but the project configures version {version}");
        }
        return blockers;
    }

    private static IReadOnlyList<string> FindingIds(IReadOnlyList<Diagnostic> lint) =>
        lint.Select(d => d.Found).Where(f => f.StartsWith('`')).Select(f => f[1..f.IndexOf('`', 1)]).Distinct().Order(StringComparer.Ordinal).ToList();

    private static string WorstStatus(IReadOnlyList<Diagnostic> lint, SupportStatus strategy)
    {
        var rank = new Dictionary<string, int> { ["supported"] = 1, ["emulated"] = 2, ["approximated"] = 3, ["unverified"] = 4 };
        var statuses = new List<string> { strategy switch { SupportStatus.Unverified => "unverified", SupportStatus.Approximated => "approximated", SupportStatus.Emulated => "emulated", _ => "supported" } };
        foreach (var d in lint)
            statuses.Add(d.Code switch
            {
                "DDB-302" => "approximated",
                "DDB-303" => "emulated",
                "DDB-304" or "DDB-305" => "unverified",
                _ => "supported",
            });
        return statuses.MaxBy(s => rank[s])!;
    }

    private string Header(string model, string operation, string target, string strategy, string bodyHash, IReadOnlyList<RenderedParameter> parameters, bool resolver, IReadOnlyList<string> targetRules, RewritePolicy rewrites)
    {
        var sb = new StringBuilder();
        sb.Append(resolver ? "-- dbdatabuild resolver for a rendered load operation. Generated by `project compile`; do not edit.\n" : "-- dbdatabuild rendered load operation. Generated by `project compile`; do not edit.\n");
        sb.Append($"-- model:           {model}\n-- operation:       {operation}\n-- connection:      {target}\n-- strategy:        {strategy}\n");
        sb.Append($"-- definition hash: {bodyHash}\n");
        sb.Append("-- parameters:      ");
        sb.Append(parameters.Count == 0 ? "none" : string.Join(", ", parameters.Select(p => $"@{p.Name} ({p.Type}, {p.Source}{(p.Constraint == null ? "" : ", " + p.Constraint)})")));
        sb.Append('\n');
        if (targetRules.Count > 0) sb.Append($"-- target rules:    {string.Join(", ", targetRules)}\n");
        if (!rewrites.IsDefault) sb.Append($"-- rewrites off:    {rewrites.Describe()}\n");
        return sb.ToString();
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
