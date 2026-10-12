using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>Writing YAML scalars for definitions: plain when that is unambiguous for people and editors, double-quoted otherwise.</summary>
public static partial class YamlText
{
    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "true", "false", "yes", "no", "on", "off", "y", "n", "null", "~" };

    // Starts with a letter or underscore, or with a digit when the value is not number-like ("3 days" is plain; "2024", "2026-10-12", "1e5" are quoted).
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_ .,()\-]*$")]
    private static partial Regex BlockSafe();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_ .()\-]*$")]
    private static partial Regex FlowSafe();

    [GeneratedRegex(@"^[0-9][0-9_.,eE+\- ]*$")]
    private static partial Regex NumberLike();

    /// <summary>A scalar in a block context (<c>type: DECIMAL(14, 2)</c>).</summary>
    public static string Scalar(string value) => Plain(value, BlockSafe()) ? value : Quoted(value);

    /// <summary>A scalar inside a flow collection (<c>[a, b]</c>), where a comma would end it.</summary>
    public static string FlowScalar(string value) => Plain(value, FlowSafe()) ? value : Quoted(value);

    public static string FlowList(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(FlowScalar)) + "]";

    /// <summary>
    /// The lines of an object's title, description, tags and properties (<see cref="ObjectInfo"/>), each indented by <paramref name="indent"/> and ending in a newline; nothing when there are none. A text value
    /// that would read back as a number or a boolean is quoted, so the type survives a rewrite.
    /// </summary>
    public static string InfoLines(ObjectInfo? info, string indent)
    {
        if (info == null || info.IsEmpty) return "";
        var sb = new System.Text.StringBuilder();
        if (info.Title != null) sb.Append(indent).Append("title: ").Append(Scalar(info.Title)).Append('\n');
        if (info.Description != null) sb.Append(indent).Append("description: ").Append(Scalar(info.Description)).Append('\n');
        if (info.Tags.Count > 0) sb.Append(indent).Append("tags: ").Append(FlowList(info.Tags)).Append('\n');
        if (info.Properties.Count > 0)
        {
            sb.Append(indent).Append("properties:\n");
            foreach (var (key, value) in info.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                sb.Append(indent).Append("  ").Append(Scalar(key)).Append(": ").Append(value.Type == PropertyValue.TextType ? Scalar(value.Text) : value.Text).Append('\n');
        }
        return sb.ToString();
    }

    private static bool Plain(string value, Regex safe) =>
        value.Length > 0 && value == value.Trim() && safe.IsMatch(value) && !Reserved.Contains(value) && !NumberLike().IsMatch(value);

    private static string Quoted(string value) => JsonSerializer.Serialize(value, Quote);
}
