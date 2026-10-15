using System.Text.Json;

namespace DbDataBuild.Core;

/// <summary>The sorts of thing a model can depend on: another model (of any of the seven kinds), a macro of the project, or a table that is none of the project's.</summary>
public static class DependencyCategories
{
    public const string Model = "model";
    public const string Macro = "macro";
    public const string Unknown = "unknown";
}

/// <summary>
/// One row of the tracking table `metadata_dependency` (and of the in-memory table of the same name): <paramref name="Subject"/> depends on <paramref name="DependsOn"/>, a thing of <paramref name="DependsOnCategory"/> (model, macro or unknown),
/// <paramref name="Depth"/> steps away: 1 is a direct edge, more is the transitive closure. <paramref name="DependsOnKind"/> is the model kind of the thing (any of the seven) and is null when it is not a model.
/// </summary>
public sealed record DependencyRow(string Subject, string DependsOn, string DependsOnCategory, string? DependsOnKind, int Depth);

/// <summary>
/// The dependency rows (DESIGN.md 12): each model, and each native model that says what it reads, with everything it depends on, directly (depth 1) and through the things it depends on (greater depths, the shortest path
/// when there are several). They are computed from the committed project when it is published, and the in-memory `metadata_upstream` and `metadata_ancestors` views read the same rows, so a rule means the same against
/// the project and against what was published.
/// </summary>
public static class MetadataDependencies
{
    /// <param name="direct">The direct edges: a subject, what it depends on, the category of that and, for a model, its model kind. A thing that is not a model with edges of its own (a mapped model, a macro, an unknown table) has none in the closure.</param>
    public static IReadOnlyList<DependencyRow> Closure(IEnumerable<(string Subject, string DependsOn, string Category, string? Kind)> direct)
    {
        var edges = new Dictionary<string, List<(string DependsOn, string Category, string? Kind)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (subject, dependsOn, category, kind) in direct)
        {
            if (string.Equals(subject, dependsOn, StringComparison.OrdinalIgnoreCase)) continue;
            if (!edges.TryGetValue(subject, out var list)) edges[subject] = list = [];
            if (!list.Any(e => string.Equals(e.DependsOn, dependsOn, StringComparison.OrdinalIgnoreCase))) list.Add((dependsOn, category, kind));
        }
        var rows = new List<DependencyRow>();
        foreach (var subject in edges.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            var seen = new Dictionary<string, DependencyRow>(StringComparer.OrdinalIgnoreCase);
            var frontier = new List<string> { subject };
            for (var depth = 1; frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var node in frontier)
                    foreach (var (dependsOn, category, kind) in edges.GetValueOrDefault(node) ?? [])
                    {
                        if (string.Equals(dependsOn, subject, StringComparison.OrdinalIgnoreCase) || seen.ContainsKey(dependsOn)) continue;
                        seen[dependsOn] = new DependencyRow(subject, dependsOn, category, kind, depth);
                        next.Add(dependsOn);
                    }
                frontier = next;
            }
            rows.AddRange(seen.Values.OrderBy(r => r.Depth).ThenBy(r => r.DependsOn, StringComparer.Ordinal));
        }
        return rows;
    }

    /// <summary>
    /// The rows from the metadata documents alone (a model document's `upstream`, which for a native model is the tables it says it reads): what the in-memory database builds when it is given no rows of its own.
    /// </summary>
    public static IReadOnlyList<DependencyRow> FromDocuments(IEnumerable<(string SubjectKind, string Subject, string Json)> documents)
    {
        var direct = new List<(string, string, string, string?)>();
        foreach (var (subjectKind, subject, json) in documents)
        {
            if (subjectKind != "model") continue;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("upstream", out var upstream) && upstream.ValueKind == JsonValueKind.Array)
                foreach (var u in upstream.EnumerateArray())
                    direct.Add((subject, u.GetProperty("name").GetString()!, u.GetProperty("category").GetString()!, u.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null));
        }
        return Closure(direct);
    }

    /// <summary>The model kind of a model document (the `model_kind` column of the stored documents): any of the seven kinds; null for any other subject kind.</summary>
    public static string? ModelKindOf(string subjectKind, string json)
    {
        if (subjectKind != "model") return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
    }
}
