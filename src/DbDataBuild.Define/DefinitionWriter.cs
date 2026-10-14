using System.Text;
using DbDataBuild.Models;

namespace DbDataBuild.Define;

/// <summary>
/// Writes a new definition in canonical formatting with a fixed key order (DESIGN.md 6.5):
/// name, kind, grain, targets, columns, renames, indexes. <c>nullable</c> is written only when false.
/// </summary>
public static class DefinitionWriter
{
    /// <param name="withNameAndKind">False for a model whose query file has a head: the head says the name, the kind and its options, so the definition does not.</param>
    public static string Create(ModelDefinition d, bool withNameAndKind = true)
    {
        var sb = new StringBuilder();
        if (withNameAndKind)
        {
            sb.Append("name: ").Append(YamlText.Scalar(d.Name)).Append('\n');
            var settings = new List<string>();
            if (d.UniqueKey.Count > 0) settings.Add("unique_key: " + YamlText.FlowList(d.UniqueKey));
            if (d.TimeColumn != null) settings.Add("time_column: " + YamlText.Scalar(d.TimeColumn));
            if (d.Lookback != null) settings.Add("lookback: " + YamlText.Scalar(d.Lookback));
            if (settings.Count == 0) sb.Append("kind: ").Append(d.Kind).Append('\n');
            else
            {
                sb.Append("kind:\n  ").Append(d.Kind).Append(":\n");
                foreach (var line in settings) sb.Append("    ").Append(line).Append('\n');
            }
        }
        if (d.Grain.Count > 0) sb.Append("grain: ").Append(YamlText.FlowList(d.Grain)).Append('\n');
        if (d.Targets is { Count: > 0 }) sb.Append("connections=: ").Append(YamlText.FlowList(d.Targets)).Append('\n');

        sb.Append("columns:\n");
        foreach (var c in d.Columns)
        {
            sb.Append("  - name: ").Append(YamlText.Scalar(c.Name)).Append('\n');
            sb.Append("    type: ").Append(YamlText.Scalar(c.Type)).Append('\n');
            if (!c.Nullable) sb.Append("    nullable: false\n");
            if (c.Collation != null) sb.Append("    collation: ").Append(YamlText.Scalar(c.Collation)).Append('\n');
        }
        if (d.Renames.Count > 0)
        {
            sb.Append("renames:\n");
            foreach (var r in d.Renames)
                sb.Append("  - from: ").Append(YamlText.Scalar(r.From)).Append("\n    to: ").Append(YamlText.Scalar(r.To)).Append('\n');
        }
        if (d.Indexes.Count > 0)
        {
            sb.Append("indexes:\n");
            foreach (var i in d.Indexes)
            {
                sb.Append("  - {name: ").Append(YamlText.Scalar(i.Name)).Append(", columns: ").Append(YamlText.FlowList(i.Columns));
                if (i.Unique) sb.Append(", unique: true");
                if (i.Include.Count > 0) sb.Append(", include: ").Append(YamlText.FlowList(i.Include));
                if (i.Targets is { Count: > 0 }) sb.Append(", connections: ").Append(YamlText.FlowList(i.Targets));
                sb.Append("}\n");
            }
        }
        return sb.ToString();
    }
}
