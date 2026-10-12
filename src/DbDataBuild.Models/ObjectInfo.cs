using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>
/// One value of a `properties:` entry. The type is read from how the value is written: `true` and `false` (lowercase, no quotes) are booleans, a number written as plain digits (`-12`, `0.5`; no exponent, no leading
/// zero, no plus sign) is a number, and everything else, and anything in quotes, is text. It is stored and compared as its text (<see cref="Text"/>) with the type beside it (<see cref="Type"/>), so `tier: 1` and
/// `tier: "1"` are two different declarations that `property:tier=1` selects alike.
/// </summary>
public sealed record PropertyValue(string Type, string Text)
{
    public const string TextType = "text";
    public const string NumberType = "number";
    public const string BooleanType = "boolean";

    private static readonly Regex NumberForm = new(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant);

    /// <summary>The value of a scalar as written: <paramref name="quoted"/> is whether it had quotes.</summary>
    public static PropertyValue From(string text, bool quoted)
    {
        if (quoted) return new(TextType, text);
        if (text is "true" or "false") return new(BooleanType, text);
        return NumberForm.IsMatch(text) ? new(NumberType, text) : new(TextType, text);
    }

    /// <summary>The value as a JSON value in a metadata document: a boolean, a number with exactly the digits written, or a string.</summary>
    public object ToJson() => Type switch
    {
        BooleanType => Text == "true",
        NumberType => JsonDocument.Parse(Text).RootElement.Clone(),
        _ => Text,
    };

    /// <summary>The value read back from a JSON value of a metadata document (the inverse of <see cref="ToJson"/>), or null for a shape the tool does not write.</summary>
    public static PropertyValue? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => new(BooleanType, "true"),
        JsonValueKind.False => new(BooleanType, "false"),
        JsonValueKind.Number => new(NumberType, e.GetRawText()),
        JsonValueKind.String => new(TextType, e.GetString() ?? ""),
        _ => null,
    };
}

/// <summary>
/// What a person says about an object in words: a short display name, a description, labels and free key-value data. The same four on the project, a connection, a model, a column and an index. Nothing here
/// changes what is built: it is kept in the metadata documents and the tracking tables, and is never written into a deployed SQL object.
/// </summary>
/// <param name="Properties">Key to value.</param>
public sealed record ObjectInfo(string? Title, string? Description, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, PropertyValue> Properties)
{
    public static ObjectInfo Empty { get; } = new(null, null, [], new Dictionary<string, PropertyValue>());

    public bool IsEmpty => Title == null && Description == null && Tags.Count == 0 && Properties.Count == 0;

    /// <summary>The tags sorted, or null when there are none (a document leaves out what is empty, so a document without any of this is what it was before).</summary>
    public IReadOnlyList<string>? TagsForDocument => Tags.Count == 0 ? null : Tags.Order(StringComparer.Ordinal).ToList();

    /// <summary>The properties sorted by key as JSON values, or null when there are none.</summary>
    public IReadOnlyDictionary<string, object>? PropertiesForDocument => Properties.Count == 0 ? null : Properties.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value.ToJson(), StringComparer.Ordinal);
}

/// <summary>The limits on the words a person writes about an object (a diagnostic when they are passed, DDB-243).</summary>
public static class InfoLimits
{
    public const int TitleLength = 200;
    public const int DescriptionLength = 4000;
    public const int TagLength = 128;
    public const int PropertyKeyLength = 128;
    public const int PropertyValueLength = 4000;

    /// <summary>A tag: letters, digits, `_`, `-`, `.` and `:`, starting with a letter, digit or `_`.</summary>
    public static readonly Regex TagForm = new(@"^[A-Za-z0-9_][A-Za-z0-9_.:\-]*$");

    /// <summary>A property key: like a tag but starting with a letter or `_` and not ending in `-`, so `key-`, `key=` and `key+` keep their meaning in the layers.</summary>
    public static readonly Regex PropertyKeyForm = new(@"^[A-Za-z_]([A-Za-z0-9_.:\-]*[A-Za-z0-9_.:])?$");
}
