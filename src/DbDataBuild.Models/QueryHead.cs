using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>A value in a head's `WITH (...)` list: text (a word, a string or a number) or a list of them.</summary>
public sealed record HeadValue(string? Text, IReadOnlyList<string>? Items, int Line, int Column);

/// <summary>One `key = value` of the `WITH (...)` list.</summary>
public sealed record HeadProperty(string Key, HeadValue Value, int Line, int Column);

/// <summary>
/// The first statement of a model's query file when it says what the file builds (DESIGN.md 6.5.7): `CREATE TABLE marts.fct_orders WITH (kind = 'incremental_by_unique_key', unique_key = (order_id)) AS SELECT ...`
/// or `CREATE VIEW schema_name.object_name AS SELECT ...`. It gives the model's name, whether it is a table or a view, and its reload options; everything else about the model stays in its definition file.
/// </summary>
/// <param name="Name">`schema_name.object_name`, as written (quotes removed).</param>
/// <param name="Line">Where the statement starts (1-based), for diagnostics about the head.</param>
public sealed record QueryHead(string Name, bool IsView, IReadOnlyList<HeadProperty> Properties, int Line, int Column, int NameLine, int NameColumn)
{
    public const string Full = ModelKinds.Full;

    /// <summary>The properties a head may carry (the reload options of a kind); anything else belongs in the definition file.</summary>
    public static readonly IReadOnlyList<string> Keys = ["kind", "unique_key", "time_column", "lookback"];

    public HeadProperty? Property(string key) => Properties.FirstOrDefault(p => p.Key == key);

    /// <summary>True when the head says the kind: a view, or a `kind` property. A plain `CREATE TABLE` says "a table", and is a full replace unless the definition file says another kind.</summary>
    public bool KindIsExplicit => IsView || Property("kind") != null;

    /// <summary>The kind the head stands for.</summary>
    public string Kind => IsView ? ModelKinds.View : Property("kind")?.Value.Text ?? Full;
}

/// <summary>
/// Reads the head of a query file by hand (nothing in DuckDB or the SQL parser knows `WITH (kind = ...)` for a model): the grammar is small, the errors name a line and a column, and the query that follows `AS` is left
/// exactly as written. The grammar: <c>CREATE (TABLE | VIEW) name [WITH ( key = value [, ...] )] AS query</c>, where a name is `schema_name.object_name` (words or "quoted names"), a key is a word and a value is a word, a
/// 'string', a number or a ( list ) of those. Comments may come first. A file whose first word is not CREATE has no head and is only a query.
/// </summary>
public static class QueryHeadParser
{
    public sealed record Result(QueryHead? Head, string Body, IReadOnlyList<Diagnostic> Problems);

    /// <summary>The head (null when the file has none), the query (the whole text when there is no head), and what is wrong with the head.</summary>
    public static Result Parse(string file, string text)
    {
        var p = new Reader(file, text);
        return p.Run();
    }

    /// <summary>The query of a file: what follows the head, or the whole text.</summary>
    public static string Body(string text) => Parse("", text).Body;

    private sealed class Reader(string file, string text)
    {
        private int i;
        private readonly List<Diagnostic> problems = [];

        public Result Run()
        {
            SkipTrivia();
            if (!IsWord("CREATE")) return new Result(null, text, []);
            var (line, column) = Where(i);
            i += "CREATE".Length;
            var mode = Word();
            if (mode is "OR" or "TEMP" or "TEMPORARY" or "IF" or "MATERIALIZED")
                return Fail($"`CREATE {mode} ...` is not used: the tool creates, alters and replaces objects itself. Write `CREATE TABLE` or `CREATE VIEW`.");
            if (mode is not ("TABLE" or "VIEW")) return Fail($"a head starts `CREATE TABLE` or `CREATE VIEW`, not `CREATE {mode ?? "..."}`.");
            var isView = mode == "VIEW";

            SkipTrivia();
            var (nameLine, nameColumn) = Where(i);
            var parts = new List<string>();
            while (true)
            {
                var part = Identifier();
                if (part == null) return Fail("a name is expected here: `schema_name.object_name`.");
                parts.Add(part);
                if (i < text.Length && text[i] == '.') { i++; continue; }
                break;
            }
            if (parts.Count < 2) return Fail($"the name `{parts[0]}` has no schema: write `schema.{parts[0]}`.", nameLine, nameColumn);
            var name = string.Join(".", parts);

            var properties = new List<HeadProperty>();
            SkipTrivia();
            if (IsWord("WITH"))
            {
                i += "WITH".Length;
                SkipTrivia();
                if (Peek() != '(') return Fail("`WITH` is followed by a list in parentheses: `WITH (kind = 'full')`.");
                i++;
                while (true)
                {
                    SkipTrivia();
                    var (kl, kc) = Where(i);
                    var key = Word();
                    if (key == null) return Fail("an option name is expected here, for example `kind`.");
                    key = key.ToLowerInvariant();
                    SkipTrivia();
                    if (Peek() != '=') return Fail($"`{key}` is followed by `=` and its value: `{key} = ...`.");
                    i++;
                    SkipTrivia();
                    var value = Value();
                    if (value == null) return Fail($"the value of `{key}` is a word, a 'string', a number or a (list) of those.");
                    properties.Add(new HeadProperty(key, value, kl, kc));
                    SkipTrivia();
                    if (Peek() == ',') { i++; continue; }
                    if (Peek() == ')') { i++; break; }
                    return Fail("`,` or `)` is expected here.");
                }
            }

            SkipTrivia();
            if (!IsWord("AS")) return Fail("`AS` and the query are expected after the name" + (properties.Count == 0 ? " (or `WITH (...)`)" : "") + ".");
            i += "AS".Length;
            var body = text[i..].Trim();
            if (body.Length == 0) return Fail("the query after `AS` is missing.");
            var head = new QueryHead(name, isView, properties, line, column, nameLine, nameColumn);
            Validate(head);
            return new Result(head, body + "\n", problems);
        }

        private void Validate(QueryHead head)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in head.Properties)
            {
                if (!seen.Add(property.Key)) { Add(property.Line, property.Column, $"`{property.Key}` is given twice."); continue; }
                if (!QueryHead.Keys.Contains(property.Key))
                    Add(property.Line, property.Column, $"`{property.Key}` is not an option of a head.", $"The options of a head are {string.Join(", ", QueryHead.Keys.Select(k => $"`{k}`"))}; everything else about the model is in its definition file.");
                else if (head.IsView) Add(property.Line, property.Column, $"a view has no options (`{property.Key}` is for a table).", "Write `CREATE TABLE` for a table with a kind, or remove the `WITH (...)`.");
                else if (property.Key == "kind" && (property.Value.Text is not { } k || !new[] { ModelKinds.Full, ModelKinds.IncrementalByUniqueKey, ModelKinds.IncrementalByTimeRange }.Contains(k)))
                    Add(property.Line, property.Column, $"`kind` is `{property.Value.Text ?? "(a list)"}`.", $"One of: {ModelKinds.Full}, {ModelKinds.IncrementalByUniqueKey}, {ModelKinds.IncrementalByTimeRange}. A view is `CREATE VIEW`.");
                else if (property.Key is "time_column" or "lookback" && property.Value.Text == null) Add(property.Line, property.Column, $"`{property.Key}` is a single value, not a list.");
            }
        }

        private Result Fail(string message, int? line = null, int? column = null)
        {
            var (l, c) = line != null ? (line.Value, column!.Value) : Where(i);
            Add(l, c, message);
            return new Result(null, text, problems);
        }

        private void Add(int line, int column, string message, string? fix = null) =>
            problems.Add(new Diagnostic(DiagnosticCatalog.QueryHeadInvalid, new(file, line, column), message, Fix: fix ?? "The head is `CREATE TABLE schema_name.object_name [WITH (option = value, ...)] AS` (or `CREATE VIEW schema_name.object_name AS`) followed by the query."));

        private (int Line, int Column) Where(int at)
        {
            var line = 1; var lineStart = 0;
            for (var k = 0; k < at && k < text.Length; k++) if (text[k] == '\n') { line++; lineStart = k + 1; }
            return (line, at - lineStart + 1);
        }

        private char Peek() => i < text.Length ? text[i] : '\0';

        private void SkipTrivia()
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) i++;
                else if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-') { while (i < text.Length && text[i] != '\n') i++; }
                else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 2; }
                else break;
            }
        }

        private bool IsWord(string word) =>
            string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0 && (i + word.Length >= text.Length || !(char.IsLetterOrDigit(text[i + word.Length]) || text[i + word.Length] == '_'));

        private string? Word()
        {
            SkipTrivia();
            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            return i == start ? null : text[start..i].ToUpperInvariant();      // keywords and option names are not case-sensitive: the caller lower-cases a name
        }

        private string? Identifier()
        {
            SkipTrivia();
            if (Peek() == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; } i++; return sb.Length == 0 ? null : sb.ToString(); }
                    sb.Append(text[i++]);
                }
                return null;
            }
            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            return i == start || char.IsDigit(text[start]) ? null : text[start..i];
        }

        private HeadValue? Value()
        {
            var (line, column) = Where(i);
            if (Peek() == '(')
            {
                i++;
                var items = new List<string>();
                while (true)
                {
                    SkipTrivia();
                    var item = Scalar();
                    if (item == null) return null;
                    items.Add(item);
                    SkipTrivia();
                    if (Peek() == ',') { i++; continue; }
                    if (Peek() == ')') { i++; break; }
                    return null;
                }
                return new HeadValue(null, items, line, column);
            }
            return Scalar() is { } text1 ? new HeadValue(text1, null, line, column) : null;
        }

        private string? Scalar()
        {
            if (Peek() == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'') { if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i += 2; continue; } i++; return sb.ToString(); }
                    sb.Append(text[i++]);
                }
                return null;
            }
            var start = i;
            if (Peek() == '"') return Identifier();
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) i++;
            return i == start ? null : text[start..i];
        }
    }

    /// <summary>
    /// The head as a layer of the definition file (what `kind` and `name` would say in YAML), for the same merge and the same checks as any other source of settings. `kind=` replaces what a folder says (the
    /// head wins over a default). Null settings are left out: a plain `CREATE TABLE` says no kind of its own.
    /// </summary>
    public static YamlMapping ToLayer(QueryHead head, bool withKind)
    {
        var entries = new List<YamlEntry> { new(new YamlScalar("name", head.NameLine, head.NameColumn), new YamlScalar(head.Name, head.NameLine, head.NameColumn)) };
        if (withKind)
        {
            var kindLine = head.Property("kind")?.Value.Line ?? head.Line;
            var kindColumn = head.Property("kind")?.Value.Column ?? head.Column;
            var settings = new List<YamlEntry>();
            foreach (var key in new[] { "unique_key", "time_column", "lookback" })
                if (head.Property(key) is { } property)
                {
                    YamlNode value = property.Value.Items != null
                        ? new YamlSequence(property.Value.Items.Select(x => (YamlNode)new YamlScalar(x, property.Value.Line, property.Value.Column)).ToList(), property.Value.Line, property.Value.Column) { Flow = true }
                        : key == "unique_key" ? new YamlSequence([new YamlScalar(property.Value.Text!, property.Value.Line, property.Value.Column)], property.Value.Line, property.Value.Column) { Flow = true }
                        : new YamlScalar(property.Value.Text!, property.Value.Line, property.Value.Column);
                    settings.Add(new YamlEntry(new YamlScalar(key, property.Line, property.Column), value));
                }
            // the kind keyed by its name, with its settings beneath it; a plain value when the head gives none
            YamlNode kind = settings.Count == 0
                ? new YamlScalar(head.Kind, kindLine, kindColumn)
                : new YamlMapping([new YamlEntry(new YamlScalar(head.Kind, kindLine, kindColumn), new YamlMapping(settings, head.Line, head.Column))], head.Line, head.Column);
            entries.Add(new YamlEntry(new YamlScalar("kind=", head.Line, head.Column), kind));
        }
        return new YamlMapping(entries, head.Line, head.Column);
    }

    /// <summary>The head's name and kind as the lines of a definition file, for what must read the definition alone (`define` checking the file it just wrote).</summary>
    public static string YamlFields(QueryHead head)
    {
        var sb = new StringBuilder();
        sb.Append("name: ").Append(YamlText.Scalar(head.Name)).Append('\n');
        var settings = new[] { "unique_key", "time_column", "lookback" }.Where(k => head.Property(k) != null).ToList();
        if (settings.Count == 0) sb.Append("kind: ").Append(head.Kind).Append('\n');
        else
        {
            sb.Append("kind:\n  ").Append(head.Kind).Append(":\n");
            foreach (var key in settings)
            {
                var property = head.Property(key)!;
                sb.Append("    ").Append(key).Append(": ").Append(property.Value.Items != null || key == "unique_key" ? YamlText.FlowList(property.Value.Items ?? [property.Value.Text!]) : YamlText.Scalar(property.Value.Text!)).Append('\n');
            }
        }
        return sb.ToString();
    }
}
