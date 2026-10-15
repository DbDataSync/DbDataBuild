using System.Text.Json;
using DbDataBuild.Execution;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>
/// The rows of `object_info` (tracking layout 6) that a metadata document says: its own title, description, tags and properties, and those of its parts, a column, an index, or a connection of the project. They are read
/// from the document's JSON, so the rows and the document that is stored beside them cannot disagree about what the project said.
/// </summary>
internal static class MetadataInfoRows
{
    /// <summary>The subject kinds whose documents have rows. Plans and the recorded definitions of native routines have none.</summary>
    public static bool HasRows(string subjectKind) => subjectKind is "project" or "model";

    public static List<InfoRow> From(string kind, string json)
    {
        var rows = new List<InfoRow>();
        if (!HasRows(kind)) return rows;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Of(root, "", rows);
        if (kind == "project")
        {
            if (root.TryGetProperty("config", out var config) && config.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Object)
                foreach (var c in connections.EnumerateObject().OrderBy(c => c.Name, StringComparer.Ordinal)) Of(c.Value, "connection:" + c.Name, rows);
            return rows;
        }
        foreach (var (property, prefix) in new[] { ("columns", "column:"), ("indexes", "index:") })
            if (root.TryGetProperty(property, out var parts) && parts.ValueKind == JsonValueKind.Array)
                foreach (var part in parts.EnumerateArray())
                    if (part.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) Of(part, prefix + name.GetString(), rows);
        return rows;
    }

    private static void Of(JsonElement element, string name, List<InfoRow> rows)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        if (element.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String) rows.Add(new(name, "title", "", null, title.GetString()));
        if (element.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String) rows.Add(new(name, "description", "", null, description.GetString()));
        if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var tag in tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Order(StringComparer.Ordinal)) rows.Add(new(name, "tag", tag, null, null));
        if (element.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            foreach (var p in properties.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                if (PropertyValue.FromJson(p.Value) is { } value) rows.Add(new(name, "property", p.Name, value.Type, value.Text));
    }
}
