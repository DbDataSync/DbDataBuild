using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>
/// What a `kind:` node says: the model kind's name and the mapping of its settings. A kind is keyed by its name (`kind: {copy: {from: pos.orders}}`), or is a plain value when it has no settings
/// (`kind: view`). The form of the first releases (`kind: {type: copy, from: pos.orders}`) is not read: it is an error that names the form to write (DDB-248).
/// </summary>
/// <param name="Name">The kind's name, as written.</param>
/// <param name="NameNode">The node the name was written in (for a diagnostic).</param>
/// <param name="Settings">The kind's settings; an empty mapping at the name for a kind written as a plain value. </param>
public sealed record KindReading(string Name, YamlNode NameNode, YamlMapping Settings);

public static class ModelKindShape
{
    /// <summary>The key of the form before the kind was keyed by its name (not read any more; only recognised, to say what to write). No kind has this name.</summary>
    private const string RemovedKey = "type";

    /// <summary>The kind a `kind:` node names, without reporting anything: null when it names none (or more than one).</summary>
    public static string? NameOf(YamlNode? node) => node switch
    {
        YamlScalar { Value.Length: > 0 } s => s.Value,
        YamlMapping { Entries.Count: 1 } m when m.Get(RemovedKey) == null => m.Entries[0].Key.Value,
        _ => null,
    };

    /// <summary>The settings mapping of a `kind:` node that names a kind: the mapping under the name (empty for a plain value), </summary>
    public static YamlMapping? SettingsOf(YamlNode? node) => node switch
    {
        YamlScalar { Value.Length: > 0 } s => new YamlMapping([], s.Line, s.Column),
        YamlMapping { Entries.Count: 1 } m when m.Get(RemovedKey) == null => m.Entries[0].Value as YamlMapping ?? new YamlMapping([], m.Entries[0].Value.Line, m.Entries[0].Value.Column),
        _ => null,
    };

    /// <summary>
    /// Reads a `kind:` node and reports what is wrong with its shape (DDB-248 or, for a missing key, DDB-105); null when it names no kind. Whether the name is a kind, and which settings it takes, are the loader's to say.
    /// </summary>
    public static KindReading? Read(YamlNode? node, YamlNode owner, Action<DiagnosticDescriptor, YamlNode, string, string?> add)
    {
        const string Example = "`kind: full`, or `kind: {incremental_by_unique_key: {unique_key: [order_id]}}` for a kind with settings";
        switch (node)
        {
            case null:
                add(DiagnosticCatalog.MissingKey, owner, "Required key `kind` is missing.", $"Add `kind:` naming the model kind: {Example}.");
                return null;
            case YamlScalar { Value.Length: > 0 } s:
                return new KindReading(s.Value, s, new YamlMapping([], s.Line, s.Column));
            case YamlScalar empty:
                add(DiagnosticCatalog.KindShapeInvalid, empty, "`kind` is empty.", $"Name the kind: {Example}.");
                return null;
            case YamlMapping m when m.Entries.Count == 0:
                add(DiagnosticCatalog.KindShapeInvalid, m, "`kind` is an empty mapping: it names no kind.", $"Name one kind: {Example}.");
                return null;
            case YamlMapping m when m.Entries.Any(e => e.Key.Value == RemovedKey):
                var written = m.Get(RemovedKey) is YamlScalar { Value.Length: > 0 } kindName ? kindName.Value : "<kind>";
                add(DiagnosticCatalog.KindShapeInvalid, m.Get(RemovedKey) ?? m, $"`kind: {{type: {written}, ...}}` is not read any more: the kind's name is the key and its settings are beneath it.",
                    $"Write `kind: {{{written}: {{...}}}}` with the other keys of this mapping as the settings, or `kind: {written}` when it has none. {Example}.");
                return null;
            case YamlMapping m when m.Entries.Count > 1:
                add(DiagnosticCatalog.KindShapeInvalid, m.Entries[1].Key, $"`kind` names {m.Entries.Count} kinds ({string.Join(", ", m.Entries.Select(e => e.Key.Value))}): a model has exactly one.", "Keep one key under `kind`; the settings of the kind go beneath it.");
                return null;
            case YamlMapping m:
                var entry = m.Entries[0];
                switch (entry.Value)
                {
                    case YamlMapping settings:
                        return new KindReading(entry.Key.Value, entry.Key, settings);
                    case YamlScalar { Value.Length: 0 }:
                        return new KindReading(entry.Key.Value, entry.Key, new YamlMapping([], entry.Value.Line, entry.Value.Column));
                    default:
                        add(DiagnosticCatalog.KindShapeInvalid, entry.Value, $"The settings of `{entry.Key.Value}` are a mapping, for example `kind: {{{entry.Key.Value}: {{...}}}}`.", $"A kind with no settings is written `kind: {entry.Key.Value}`.");
                        return null;
                }
            default:
                add(DiagnosticCatalog.KindShapeInvalid, node, "`kind` is the name of a kind, or a mapping with the kind's name as its one key.", $"Write {Example}.");
                return null;
        }
    }
}
