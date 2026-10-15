using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Define;

public sealed record NullabilityProposal(bool? Nullable, string Reason)
{
    public bool HasProposal => Nullable != null;
}

public sealed record InferredColumn(
    string Name, string DuckDbType, TypeProposal Type, NullabilityProposal Nullability, string TransformKind, IReadOnlyList<ColumnRef> Upstream);

public sealed record GrainCandidate(IReadOnlyList<string> Columns, string Evidence);

public sealed record Inference(
    IReadOnlyList<InferredColumn> Columns,
    IReadOnlyList<GrainCandidate> GrainCandidates,
    IReadOnlyList<string> TimeColumnCandidates,
    IReadOnlyList<string> UpstreamNames,
    IReadOnlyList<ProjectionFact> Lineage);

/// <summary>What a query that calls the project's macros needs besides its text: the macros to bind it with, and the tables the bound plan scans once they are expanded (and the names its text gave that are macros, not tables).</summary>
public sealed record MacroSupport(DuckPrelude Prelude, IReadOnlyList<string> Tables, IReadOnlyList<string> MacroNames, string? LoweredSql = null);

/// <summary>What `define` learns from a model body, offline: output columns and types from DuckDB's describe, lineage and nullability from polyglot.</summary>
public static class ModelInference
{
    /// <returns>The inference, or null with error diagnostics. Never touches a target, and never runs the query.</returns>
    public static (Inference? Value, IReadOnlyList<Diagnostic> Diagnostics) Infer(string queryFile, string sql, ModelGraph graph, MacroSupport? macros = null)
    {
        var diags = new List<Diagnostic>();
        SourceLocation At() => new(queryFile, 0, 0);

        // 1. which tables does the query use?
        var (first, firstError) = QueryAnalyzer.Analyze(sql);
        if (first == null)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.SqlParseFailure, At(), $"The DuckDB parser reported: {firstError}", Fix: SqlParseHints.Fix(sql, firstError)));
            return (null, diags);
        }

        // 2. resolve them against models and sources
        var upstream = new List<UpstreamTable>();
        var read = first.BaseTables.Select(t => t.QualifiedName).Where(n => macros == null || !macros.MacroNames.Contains(n, StringComparer.OrdinalIgnoreCase)).Concat(macros?.Tables ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in read)
        {
            var t = new { QualifiedName = name };
            if (graph.Find(t.QualifiedName) is { } u) { if (!upstream.Contains(u)) upstream.Add(u); }
            else diags.Add(new Diagnostic(DiagnosticCatalog.UpstreamNotFound, At(),
                $"The query uses `{t.QualifiedName}`, which is neither a built nor a declared (mapped or native) model.",
                Fix: $"Add a mapped model `models/{t.QualifiedName.Replace('.', '/')}.yml` (kind: mapped) describing it, or define the model that produces it."));
        }
        if (diags.Count > 0) return (null, diags);

        // 3. DuckDB describe against empty tables built from the declared upstream columns
        var duckTables = upstream.Select(u =>
        {
            var (schema, name) = Split(u.Name);
            return new DuckTable(schema, name, u.Columns.Select(c => new DuckColumn(c.Name, c.Type, c.Nullable)).ToList());
        }).ToList();
        var described = QueryDescriber.Describe(duckTables, sql, macros?.Prelude);
        if (!described.Ok)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.QueryNotDescribable, At(), $"DuckDB reported: {described.Error}"));
            return (null, diags);
        }
        var output = described.Columns!;

        // 4. lineage and nullability, matched to the described columns by position and name
        var specs = upstream.Select(u =>
        {
            var (schema, name) = Split(u.Name);
            return new SchemaTableSpec(schema == "main" && !u.Name.Contains('.') ? null : schema, name,
                u.Columns.Select(c => new SchemaColumnSpec(c.Name, c.Type, c.Nullable)).ToList());
        }).ToList();
        var (facts, _) = QueryAnalyzer.Analyze(macros?.LoweredSql ?? sql, specs);
        // Projections align with DuckDB's columns by position. A name must agree, except that polyglot labels an unaliased expression
        // `_col_<index>` while DuckDB names it by its text; that mismatch is how an expression without an alias is recognized.
        var lineage = facts != null && facts.Projections.Count == output.Count &&
                      facts.Projections.Zip(output).All(p => IsUnaliased(p.First) || string.Equals(p.First.Name, p.Second.Name, StringComparison.OrdinalIgnoreCase))
            ? facts.Projections : null;

        // 5. every output column needs a unique name (and an alias if it is an expression)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < output.Count; i++)
        {
            if (!seen.Add(output[i].Name))
                diags.Add(new Diagnostic(DiagnosticCatalog.OutputColumnUnusable, At(), $"The query returns the column name `{output[i].Name}` more than once."));
            else if (lineage != null && IsUnaliased(lineage[i]) && !string.Equals(lineage[i].Name, output[i].Name, StringComparison.OrdinalIgnoreCase))
                diags.Add(new Diagnostic(DiagnosticCatalog.OutputColumnUnusable, At(), $"Output column {i + 1} (`{output[i].Name}`) is an expression without an alias."));
        }
        if (diags.Count > 0) return (null, diags);

        var columns = output.Select((o, i) => BuildColumn(o, lineage?[i], graph)).ToList();
        var grain = GrainCandidates(facts, columns, graph, sql);
        var time = columns.Where(c => IsTemporal(c.DuckDbType)).Select(c => c.Name).ToList();
        return (new Inference(columns, grain, time, upstream.Select(u => u.Name).ToList(), lineage ?? []), diags);
    }

    private static bool IsUnaliased(ProjectionFact p) => string.IsNullOrWhiteSpace(p.Name) || p.Name == $"_col_{p.Index}";

    private static InferredColumn BuildColumn(DescribedColumn described, ProjectionFact? fact, ModelGraph graph)
    {
        var type = ResolveType(described, fact, graph);
        var nullability = fact?.Nullability switch
        {
            "non_null" => new NullabilityProposal(false, "non-null by lineage (declared NOT NULL upstream, no outer join)"),
            "nullable" => new NullabilityProposal(true, "may be NULL by lineage (declared nullable upstream or outer-joined)"),
            _ => new NullabilityProposal(null, "lineage cannot show whether it can be NULL"),
        };
        return new InferredColumn(described.Name, described.DuckDbType, type, nullability, fact?.TransformKind ?? "", fact?.Upstream ?? []);
    }

    private static TypeProposal ResolveType(DescribedColumn described, ProjectionFact? fact, ModelGraph graph)
    {
        // a pass-through column keeps its upstream declared type exactly (including a VARCHAR length) when DuckDB agrees it is that type
        if (fact is { TransformKind: "direct", Upstream.Count: 1 } && fact.Upstream[0] is { Table: { } table } up &&
            graph.Find(table) is { } u && u.Columns.FirstOrDefault(c => string.Equals(c.Name, up.Column, StringComparison.OrdinalIgnoreCase)) is { } declared &&
            LogicalTypes.Equivalent(declared.Type, described.DuckDbType))
            return new TypeProposal(LogicalTypes.Normalize(declared.Type), ProposalCertainty.High, $"passes through {table}.{declared.Name}, declared {LogicalTypes.Normalize(declared.Type)}");

        // a written CAST(x AS VARCHAR(n)) states the length
        if (fact is { TransformKind: "cast" } && LogicalTypes.SizedVarchar(fact.CastType) is { } sized && described.DuckDbType == "VARCHAR")
            return new TypeProposal(sized, ProposalCertainty.High, $"the query casts to {sized}");

        return LogicalTypes.FromDuckDb(described.DuckDbType);
    }

    private static IReadOnlyList<GrainCandidate> GrainCandidates(QueryFacts? facts, IReadOnlyList<InferredColumn> columns, ModelGraph graph, string sql)
    {
        var result = new List<GrainCandidate>();
        void Add(IReadOnlyList<string> cols, string evidence)
        {
            if (cols.Count > 0 && !result.Any(r => r.Columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).SequenceEqual(cols.OrderBy(c => c, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)))
                result.Add(new GrainCandidate(cols, evidence));
        }

        string? OutputFor(ColumnRef r) => columns.FirstOrDefault(c =>
            c.TransformKind == "direct" && c.Upstream.Count == 1 &&
            string.Equals(c.Upstream[0].Table, r.Table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Upstream[0].Column, r.Column, StringComparison.OrdinalIgnoreCase))?.Name;

        if (facts is { IsSetOperation: false })
        {
            // GROUP BY columns that appear in the output
            if (facts.GroupedColumns.Count > 0)
            {
                var mapped = facts.GroupedColumns.Select(OutputFor).ToList();
                if (mapped.All(m => m != null)) Add(mapped.Select(m => m!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), "the query groups by these columns");
            }
            // DISTINCT: the whole output row
            if (facts.IsDistinct) Add(columns.Select(c => c.Name).ToList(), "the query is SELECT DISTINCT over every output column");
            // an upstream key that passes straight through a one-to-one query
            if (facts is { GroupedColumns.Count: 0, IsDistinct: false, JoinCount: 0, BaseTables.Count: 1 } &&
                graph.Find(facts.BaseTables[0].QualifiedName) is { Grain.Count: > 0 } up)
            {
                var mapped = up.Grain.Select(g => OutputFor(new ColumnRef(facts.BaseTables[0].QualifiedName, g))).ToList();
                if (mapped.All(m => m != null)) Add(mapped.Select(m => m!).ToList(), $"the grain of {up.Name} passes through unchanged");
            }
        }
        return result;
    }

    private static bool IsTemporal(string duckType) => duckType is "DATE" or "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" || duckType.StartsWith("TIMESTAMP_");

    public static (string SchemaName, string Name) Split(string qualified)
    {
        var i = qualified.LastIndexOf('.');
        return i < 0 ? ("main", qualified) : (qualified[..i], qualified[(i + 1)..]);
    }
}
