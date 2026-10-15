using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Lowering;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Cli;

/// <summary>A model's lowered query and the committed artifact that shows it.</summary>
/// <param name="ArtifactPath">Relative to rendered/, `lowered/&lt;model&gt;/lowered.sql`.</param>
/// <param name="ArtifactText">The file: a header naming its source, hashes and the DuckDB version, then the query.</param>
internal sealed record LoweredModel(string Sql, LoweredQuery Query, string ArtifactPath, string ArtifactText, string Hash);

/// <summary>
/// The lowering stage (docs/research/duckdb-plan-lowering): DuckDB binds each model query against an empty schema built from the declared columns of everything
/// else in the project, and the bound plan becomes one explicit query that the matrix lint and polyglot then work on. A query that cannot be lowered is DDB-324.
/// </summary>
internal sealed class ModelLowering(IReadOnlyList<ModelDefinition> models, IReadOnlyList<DeclaredModel> descriptors, ProjectConfig config, MacroLibrary? macros = null)
{
    private readonly Dictionary<string, (LoweredModel? Model, Diagnostic? Error)> cache = new(StringComparer.Ordinal);
    private string? duckDbVersion;

    public bool Enabled => config.LoweringEnabled;

    /// <summary>The declared grain of a model or source, by the name a query uses for it.</summary>
    private IReadOnlyList<string> GrainOf(string table) =>
        models.FirstOrDefault(m => string.Equals(m.Name, table, StringComparison.OrdinalIgnoreCase))?.Grain
        ?? descriptors.FirstOrDefault(d => string.Equals(d.Name, table, StringComparison.OrdinalIgnoreCase))?.Grain ?? [];

    /// <summary>
    /// The source columns a query's output column is read from, directly or through a cast, when the project declares something about the source (its `indexes:` or its `grain`) and no declared index leads with the
    /// column: what the slice advice (DDB-239) reports. A computed column, a source with nothing declared and a model (whose indexes are the tool's to create) give nothing.
    /// </summary>
    public IReadOnlyList<(string Table, string Column)> MappedColumnsWithoutLeadingIndex(string sql, string column)
    {
        var facts = QueryAnalyzer.Analyze(sql).Facts;
        var projection = facts?.Projections.FirstOrDefault(p => string.Equals(p.Name, column, StringComparison.OrdinalIgnoreCase));
        if (projection is not { TransformKind: "direct" or "cast" }) return [];
        var result = new List<(string, string)>();
        foreach (var from in projection.Upstream)
        {
            var table = descriptors.FirstOrDefault(d => !d.IsNative && !d.IsGenerated && from.Table != null && (string.Equals(d.Name, from.Table, StringComparison.OrdinalIgnoreCase) || d.Name.EndsWith("." + from.Table, StringComparison.OrdinalIgnoreCase)));
            if (table == null || table.Grain.Count == 0 && (table.DeclaredIndexes?.Count ?? 0) == 0) continue;
            var leads = table.Grain.Take(1).Concat((table.DeclaredIndexes ?? []).Select(i => i.Columns[0]));
            if (!leads.Contains(from.Column, StringComparer.OrdinalIgnoreCase)) result.Add((table.Name, from.Column));
        }
        return result;
    }

    private readonly Dictionary<string, NativeUse> nativeUses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The native models a query reads (by lineage of its text), ready to be spliced into the target's text after transpiling.</summary>
    public IReadOnlyList<NativeUse> NativeUsesFor(string sql)
    {
        if (!descriptors.Any(d => d.IsNative && d.Native!.Access == NativeQuery.Select)) return [];
        var read = QueryAnalyzer.Analyze(sql).Facts?.BaseTables.Select(t => t.QualifiedName).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var result = new List<NativeUse>();
        foreach (var d in descriptors.Where(d => d.IsNative && d.Native!.Access == NativeQuery.Select && read.Contains(d.Name)))
        {
            if (!nativeUses.TryGetValue(d.Name, out var use)) nativeUses[d.Name] = use = NativeInline.Prepare(d, config);
            result.Add(use);
        }
        return result;
    }

    /// <summary>The value of each placeholder of the native models a query reads, on one connection.</summary>
    public IReadOnlyDictionary<string, ParameterValue> NativeValuesFor(string sql, string connection)
    {
        var values = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
        foreach (var use in NativeUsesFor(sql))
            foreach (var (k, v) in NativeInline.Values(descriptors.First(d => d.Name == use.Name), use, config, connection)) values[k] = v;
        return values;
    }

    /// <summary>What a query reads after DuckDB has expanded its macros: the tables of the bound plan. Null when the query cannot be lowered (the caller falls back to the names in its text).</summary>
    public IReadOnlyList<string>? TablesRead(BuiltModel built, string authorSql, IReadOnlyList<QueryParameter> parameters)
    {
        if (!Enabled) return null;
        var (lowered, _) = Lower(built, authorSql, parameters);
        return lowered?.Query.Tables;
    }

    /// <summary>
    /// Each macro and type of the project created on its own (with what it calls) against the declared tables, as a binding of a query would: a warning for one DuckDB refuses (a name it mentions that no
    /// model declares, a type that is missing). A query that reaches it gets the error itself when it is lowered; one that does not is not affected, since only what a query reaches is ever created.
    /// </summary>
    public IReadOnlyList<Diagnostic> CheckMacros()
    {
        var library = macros ?? MacroLibrary.Empty;
        var found = new List<Diagnostic>();
        if (library.IsEmpty) return found;
        var upstream = models.Select(m => (m.Name, m.Columns)).Concat(descriptors.Select(d => (d.Name, d.Columns))).Select(ToTable).ToList();
        foreach (var d in library.Definitions)
        {
            if (QueryDescriber.CheckPrelude(upstream, library.PreludeFor([d.Sql])) is { } why)
                found.Add(why.StartsWith(QueryDescriber.ClockDefaultCode + ": ", StringComparison.Ordinal)
                    ? new Diagnostic(DiagnosticCatalog.MacroDefaultIsClock, new(d.File, d.Line, 0), $"`{d.Name}`: {why[(QueryDescriber.ClockDefaultCode.Length + 2)..]}.") with { SeverityOverride = Severity.Warning }
                    : new Diagnostic(DiagnosticCatalog.InvalidValue, new(d.File, d.Line, 0), $"`{d.Name}` could not be created: {why}.",
                    Fix: "A macro that names a table or type directly needs it to be declared; one that takes its table as a parameter (`query_table(tbl)`) needs nothing. Declare what it names or pass the name in.") with { SeverityOverride = Severity.Warning });
        }
        return found;
    }

    private static DuckTable ToTable((string Name, IReadOnlyList<ColumnDefinition> Columns) u)
    {
        var i = u.Name.LastIndexOf('.');
        return new DuckTable(i < 0 ? "main" : u.Name[..i], i < 0 ? u.Name : u.Name[(i + 1)..], u.Columns.Select(c => new DuckColumn(c.Name, c.Type, c.Nullable)).ToList());
    }

    private static readonly System.Text.RegularExpressions.Regex TextType = new(@"^\s*(N?VARCHAR|N?CHAR|CHARACTER|TEXT|STRING|BPCHAR)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A string column the query uses in a way that depends on how strings compare: where (clause or distinct and set operations), the column, and whether it is declared `trimmed`.</summary>
    public sealed record StringUse(string Context, string Column, bool Trimmed);

    /// <summary>
    /// The uses a (lowered) query makes of string columns in a clause that depends on how strings compare: equality and grouping (`filter`, `join`, `group`, `having`, `window_partition`, `distinct`, `set_operation`;
    /// all of them depend on case, accent and trailing spaces) and ordering (`order`, `window_order`; case and accent only). A column is a string column by its declared type. A column named inside a larger
    /// expression is counted as used there: this says where to look, not what the engine will do.
    /// </summary>
    public IReadOnlyList<StringUse> StringUses(string modelName, string loweredSql)
    {
        var declared = models.Where(m => m.Name != modelName).Select(m => (m.Name, m.Columns)).Concat(descriptors.Select(d => (d.Name, d.Columns))).ToList();
        var specs = declared.Select(u =>
        {
            var i = u.Name.LastIndexOf('.');
            return new SchemaTableSpec(i < 0 ? null : u.Name[..i], i < 0 ? u.Name : u.Name[(i + 1)..], u.Columns.Select(c => new SchemaColumnSpec(c.Name, c.Type, c.Nullable)).ToList());
        }).ToList();
        var facts = QueryAnalyzer.Analyze(loweredSql, specs).Facts;
        if (facts == null) return [];
        ColumnDefinition? Column(ColumnRef r) => r.Table == null ? null
            : declared.FirstOrDefault(d => string.Equals(d.Name, r.Table, StringComparison.OrdinalIgnoreCase)).Columns?.FirstOrDefault(c => string.Equals(c.Name, r.Column, StringComparison.OrdinalIgnoreCase));
        var uses = new List<StringUse>();
        void Add(string context, ColumnRef r)
        {
            if (Column(r) is { } c && TextType.IsMatch(c.Type)) uses.Add(new StringUse(context, $"{r.Table}.{r.Column}", c.Trimmed));
        }
        foreach (var use in facts.ColumnUses) foreach (var r in use.References) Add(use.Context, r);
        if (facts.IsDistinct || facts.IsSetOperation)
            foreach (var p in facts.Projections) foreach (var r in p.Upstream) Add(facts.IsSetOperation ? "set_operation" : "distinct", r);
        return uses.DistinctBy(u => (u.Context, u.Column)).ToList();
    }

    /// <summary>True when a query calls a macro (or mentions a type) of the project.</summary>
    public bool ReachesMacros(string sql)
    {
        var (called, mentioned) = (macros ?? MacroLibrary.Empty).ReachedBy(sql);
        return called.Count + mentioned.Count > 0;
    }

    private readonly Dictionary<string, (string? Sql, string? Error)> duckDbCache = new(StringComparer.Ordinal);

    /// <summary>
    /// The query as DuckDB must run it to answer as a connection that ignores trailing spaces in a string comparison does (`sample`, `test`): the lowered query with its string operands trimmed where it
    /// matters (see <see cref="PlanLowerer.Lower"/>). Never written to the project and never given to an engine.
    /// </summary>
    public (string? Sql, string? Error) LowerForDuckDb(BuiltModel built, string sql)
    {
        var key = built.Definition.Name + "\0" + sql;
        if (duckDbCache.TryGetValue(key, out var hit)) return hit;
        var upstream = UpstreamFor(built.Definition.Name, sql, restrict: true);
        var prelude = (macros ?? MacroLibrary.Empty).PreludeFor([sql]);
        var (json, error) = QueryDescriber.SerializePlan(upstream, sql, prelude);
        if (json == null && upstream.Count < models.Count + descriptors.Count - 1)
        {
            upstream = UpstreamFor(built.Definition.Name, sql, restrict: false);                                  // an error is reported on the whole project's tables
            (json, error) = QueryDescriber.SerializePlan(upstream, sql, prelude);
        }
        if (json == null) return duckDbCache[key] = (null, error);
        try
        {
            PlanLowerer.ThrowIfError(json);
            var described = QueryDescriber.Describe(upstream, sql, prelude);
            if (!described.Ok) return duckDbCache[key] = (null, described.Error);
            var lowered = PlanLowerer.Lower(json, described.Columns!.Select(c => c.Name).ToList(), GrainOf, RewriteCatalog.For(config, built.Definition), ignoreTrailingSpaces: true);
            return duckDbCache[key] = (lowered.Sql, null);
        }
        catch (LoweringException ex) { return duckDbCache[key] = (null, ex.Message.Split('\n')[0]); }
    }

    public static string ArtifactPathFor(string model, string? variant = null) => variant == null ? $"lowered/{model}/lowered.sql" : $"lowered/{model}/lowered.{variant}.sql";

    /// <param name="parameters">The parameters the query uses as values (their markers are in <paramref name="authorSql"/>); the committed artifact shows them as the references they stand for.</param>
    /// <param name="variant">Which of several lowerings of one model this is (the first connection of the group that reads this text), when a name it is given differs between its connections; it is part of the artifact's file name.</param>
    public (LoweredModel? Model, Diagnostic? Error) Lower(BuiltModel built, string authorSql, IReadOnlyList<QueryParameter>? parameters = null, string? variant = null)
    {
        var key = built.Definition.Name + "\0" + variant + "\0" + authorSql;
        if (cache.TryGetValue(key, out var hit)) return hit;
        var result = Compute(built, authorSql, parameters ?? [], variant, restrict: true);
        if (result.Item1 == null) result = Compute(built, authorSql, parameters ?? [], variant, restrict: false);      // a refusal is given on the whole project's tables, so a table the quick way missed cannot cause one
        return cache[key] = result;
    }

    /// <summary>
    /// The declared tables DuckDB needs to bind a query: the ones its text names (plus none else), so a project of a few hundred models does not create every table of the project for each query (the cost grew with the
    /// square of the models). Every table when the query reaches a macro (a macro can name a table through an argument) or its text cannot be read.
    /// </summary>
    private List<DuckTable> UpstreamFor(string modelName, string sql, bool restrict)
    {
        var all = models.Where(m => m.Name != modelName).Select(m => (m.Name, m.Columns)).Concat(descriptors.Select(d => (d.Name, d.Columns))).Select(ToTable).ToList();
        if (!restrict || ReachesMacros(sql)) return all;
        var named = QueryAnalyzer.Analyze(sql).Facts?.BaseTables;
        if (named == null) return all;
        var wanted = named.Select(t => ((t.SchemaName ?? "main").ToLowerInvariant(), t.Table.ToLowerInvariant())).ToHashSet();
        return all.Where(t => wanted.Contains((t.SchemaName.ToLowerInvariant(), t.Name.ToLowerInvariant()))).ToList();
    }

    private (LoweredModel?, Diagnostic?) Compute(BuiltModel built, string authorSql, IReadOnlyList<QueryParameter> parameters, string? variant, bool restrict) =>
        Compute(built.Definition.Name, built.QueryFile, built.Definition, authorSql, parameters, variant, restrict);

    /// <summary>
    /// The SELECT of a `target` or `source` resolver of an input, lowered like a model's query (DDB-324 when it cannot be). It is written over the project's models, all of them (a resolver may read the model whose load it
    /// belongs to), and its text is not committed as a model: the caller renders it for the engine it runs on.
    /// </summary>
    public (string? Sql, Diagnostic? Error) LowerResolver(ModelDefinition model, string file, string label, string sql)
    {
        var key = "\0resolver\0" + label + "\0" + sql;
        if (!cache.TryGetValue(key, out var hit))
        {
            var result = Compute(label, file, model, sql, [], null, restrict: true);
            if (result.Item1 == null) result = Compute(label, file, model, sql, [], null, restrict: false);
            hit = cache[key] = result;
        }
        return (hit.Model?.Sql, hit.Error);
    }

    private (LoweredModel?, Diagnostic?) Compute(string name, string queryFile, ModelDefinition definition, string authorSql, IReadOnlyList<QueryParameter> parameters, string? variant, bool restrict)
    {
        Diagnostic Fail(string why) => new(DiagnosticCatalog.QueryNotLowerable, new(queryFile, 0, 0), $"{name} cannot be lowered: {why}.");

        var upstream = UpstreamFor(name, authorSql, restrict);

        var policy = RewriteCatalog.For(config, definition);
        var prelude = (macros ?? MacroLibrary.Empty).PreludeFor([authorSql]);
        var (json, error) = QueryDescriber.SerializePlan(upstream, authorSql, prelude);
        if (json == null && error != null && error.StartsWith(QueryDescriber.ClockDefaultCode + ": ", StringComparison.Ordinal))
            return (null, new Diagnostic(DiagnosticCatalog.MacroDefaultIsClock, new(queryFile, 0, 0), $"{name}: {error[(QueryDescriber.ClockDefaultCode.Length + 2)..]}."));
        if (json == null) return (null, Fail(error ?? "DuckDB returned no plan"));
        LoweredQuery query;
        try
        {
            PlanLowerer.ThrowIfError(json);                                        // DuckDB's own parse and bind errors first
            var described = QueryDescriber.Describe(upstream, authorSql, prelude);
            if (!described.Ok) return (null, Fail(described.Error ?? "DuckDB could not describe the query"));
            // the author's table aliases come back where the text and the plan line up (not for a query that reaches a macro: the plan then has scans the text never names)
            var written = ReachesMacros(authorSql) ? null : DuckParseTree.TableAliases(authorSql);
            query = PlanLowerer.Lower(json, described.Columns!.Select(c => c.Name).ToList(), GrainOf, policy, authorAliases: written?.Select(a => (a.SchemaName, a.Name, a.Alias)).ToList());
        }
        catch (LoweringException ex) when (ex.Kind == "parser") { return (null, new Diagnostic(DiagnosticCatalog.SqlParseFailure, new(queryFile, 0, 0), $"The DuckDB parser reported: {ex.Message}")); }
        catch (LoweringException ex) when (ex.Kind is "binder" or "catalog" && ex.Message.StartsWith("Table with name", StringComparison.Ordinal))
        {
            return (null, new Diagnostic(DiagnosticCatalog.UpstreamNotFound, new(queryFile, 0, 0), $"{name}: {ex.Message.Split('\n')[0]}"));
        }
        catch (LoweringException ex) { return (null, Fail(ex.Kind != "unsupported" ? "DuckDB could not bind the query: " + ex.Message.Split('\n')[0] : ex.Message)); }

        duckDbVersion ??= QueryDescriber.DuckDbVersion();
        var (authorHash, _) = AstHasher.Hash(authorSql);
        var sb = new StringBuilder();
        sb.Append("-- dbdatabuild lowered query. Generated by `project compile`; do not edit.\n");
        sb.Append($"-- model:        {name}\n-- source:       {queryFile}\n-- source hash:  {authorHash}\n-- duckdb:       {duckDbVersion}\n");
        sb.Append($"-- output:       {string.Join(", ", query.Columns.Select(c => $"{c.Name} {c.DuckDbType}"))}\n");
        if (query.Rules.Count > 0) sb.Append($"-- type rules:   {string.Join(", ", query.Rules)}\n");
        if (!policy.IsDefault) sb.Append($"-- rewrites off: {policy.Describe()}\n");
        sb.Append(QueryParameters.BackToReferences(query.Sql, parameters)).Append('\n');
        var text = sb.ToString();
        return (new LoweredModel(query.Sql, query, ArtifactPathFor(name, variant), text, DbDataBuild.State.Hashing.ScriptHash(text)), null);
    }
}
