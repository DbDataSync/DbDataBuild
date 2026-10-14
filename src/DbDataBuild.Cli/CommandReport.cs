using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataBuild.Core;

namespace DbDataBuild.Cli;

/// <summary>
/// Output of a command in `--format json` mode: one JSON document on standard output and nothing else. The human text a command prints is kept (as `messages` and `errors`)
/// so nothing is lost, diagnostics become structured objects, and each command adds a `data` payload of what it found or did. Exit codes are the same in both modes.
/// </summary>
internal sealed class CommandReport
{
    public const string SchemaId = "dbdatabuild.output/1";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string command;
    private readonly TextWriter realOutput;
    private readonly List<Diagnostic> diagnostics = [];
    private readonly Dictionary<string, object?> data = new(StringComparer.Ordinal);
    private readonly List<string> next = [];

    public bool IsJson { get; }
    public TextWriter Output { get; }
    public TextWriter Error { get; }

    private CommandReport(bool json, string command, TextWriter output, TextWriter error)
    {
        IsJson = json; this.command = command; realOutput = output;
        (Output, Error) = json ? (new CapturingWriter(this), new CapturingWriter(this)) : (output, error);
    }

    public static CommandReport Create(bool json, string command, TextWriter output, TextWriter error) => new(json, command, output, error);

    /// <summary>For a command that runs another and reports its result as part of its own (`project update` runs the model update): what the run put in `data`, its diagnostics and its text. Meaningful for a report made in JSON mode.</summary>
    internal IReadOnlyDictionary<string, object?> Data => data;
    internal IReadOnlyList<Diagnostic> Diagnostics => diagnostics;
    internal IReadOnlyList<string> MessageLines { get { Output.Flush(); return ((CapturingWriter)Output).Lines; } }
    internal IReadOnlyList<string> ErrorLines { get { Error.Flush(); return ((CapturingWriter)Error).Lines; } }

    /// <summary>Adds to a list a command has already put in `data` (or starts it): the files two steps of one command wrote.</summary>
    internal void Append(string key, IEnumerable<string> items)
    {
        if (data.TryGetValue(key, out var existing) && existing is List<string> list) list.AddRange(items); else data[key] = items.ToList();
    }

    internal void AddDiagnostic(Diagnostic d) => diagnostics.Add(d);
    internal void Set(string key, object? value) => data[key] = value;
    internal void AddNext(IEnumerable<string> commands) => next.AddRange(commands);

    /// <summary>Writes the document in JSON mode (and returns the exit code unchanged in both modes).</summary>
    public int Finish(int exit)
    {
        if (!IsJson) return exit;
        var doc = new Dictionary<string, object?>
        {
            ["schema"] = SchemaId,
            ["command"] = command,
            ["tool_version"] = ProductInfo.Version,
            ["exit_code"] = exit,
            ["ok"] = exit == CliApp.ExitOk,
            ["data"] = data,
            ["diagnostics"] = diagnostics.Select(Describe).ToList(),
            ["messages"] = ((CapturingWriter)Output).Lines,
            ["errors"] = ((CapturingWriter)Error).Lines,
        };
        if (next.Count > 0) doc["next"] = next;
        realOutput.WriteLine(JsonSerializer.Serialize(doc, Json));
        return exit;
    }

    /// <summary>The document for a failure of the tool itself (an unhandled exception), so a caller reading JSON never gets a stack trace.</summary>
    public static string InternalError(string command, string text) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["schema"] = SchemaId, ["command"] = command, ["tool_version"] = ProductInfo.Version, ["exit_code"] = CliApp.ExitInternal, ["ok"] = false,
        ["data"] = new Dictionary<string, object?>(),
        ["diagnostics"] = new[] { Describe(new Diagnostic(DiagnosticCatalog.InternalError, new("<internal>", 0, 0), text)) },
        ["messages"] = Array.Empty<string>(), ["errors"] = Array.Empty<string>(),
    }, Json);

    /// <summary>The document for a command that stopped on one diagnostic outside its own flow (a database exception).</summary>
    public static string Failure(string command, Diagnostic diagnostic, int exitCode) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["schema"] = SchemaId, ["command"] = command, ["tool_version"] = ProductInfo.Version, ["exit_code"] = exitCode, ["ok"] = false,
        ["data"] = new Dictionary<string, object?>(),
        ["diagnostics"] = new[] { Describe(diagnostic) },
        ["messages"] = Array.Empty<string>(), ["errors"] = Array.Empty<string>(),
    }, Json);

    public static object Describe(Diagnostic d) => new
    {
        code = d.Code,
        severity = d.Severity.ToString().ToLowerInvariant(),
        title = d.Descriptor.Title,
        location = new { file = d.Location.File, line = d.Location.Line, column = d.Location.Column },
        found = d.Found,
        supported = d.Supported ?? d.Descriptor.Supported,
        fix = d.Fix ?? d.Descriptor.Fix,
    };

    /// <summary>Collects the text a command writes, one line at a time.</summary>
    internal sealed class CapturingWriter(CommandReport owner) : TextWriter
    {
        private readonly StringBuilder current = new();
        public List<string> Lines { get; } = [];
        public CommandReport Owner => owner;
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n') { Lines.Add(current.ToString().TrimEnd('\r')); current.Clear(); }
            else current.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            foreach (var c in value) Write(c);
        }

        protected override void Dispose(bool disposing)
        {
            if (current.Length > 0) { Lines.Add(current.ToString()); current.Clear(); }
            base.Dispose(disposing);
        }
    }
}

internal static class ReportExtensions
{
    /// <summary>Reports a diagnostic: formatted text on the error stream, or a structured entry in JSON mode.</summary>
    public static void Diag(this TextWriter w, Diagnostic d)
    {
        if (w is CommandReport.CapturingWriter c) c.Owner.AddDiagnostic(d);
        else w.Write(DiagnosticFormatter.Format(d));
    }

    /// <summary>Adds items to a list in the JSON document's `data` that another step may have started. Ignored in text mode.</summary>
    public static void PayloadAppend(this TextWriter w, string key, IEnumerable<string> items)
    {
        if (w is CommandReport.CapturingWriter c) c.Owner.Append(key, items);
    }

    /// <summary>Adds a named part to the JSON document's `data`. Ignored in text mode.</summary>
    public static void Payload(this TextWriter w, string key, object? value)
    {
        if (w is CommandReport.CapturingWriter c) c.Owner.Set(key, value);
    }

    /// <summary>
    /// What a person (or an agent) would do next, as commands: a `Next:` block of text, and `next` in the JSON document. A command names one to three, the ones that follow from what just happened,
    /// so nobody has to remember the order of the steps.
    /// </summary>
    public static void Next(this TextWriter w, params string[] commands)
    {
        if (commands.Length == 0) return;
        var shown = commands.Select(c => $"{ProductInfo.Cli} {c}").ToArray();
        if (w is CommandReport.CapturingWriter owner) owner.Owner.AddNext(shown);
        w.WriteLine();
        w.WriteLine(shown.Length == 1 ? $"Next: {shown[0]}" : "Next:");
        if (shown.Length > 1) foreach (var c in shown) w.WriteLine($"  {c}");
    }

    /// <summary>True when output is going into a JSON document (so decorative text and prompts are pointless).</summary>
    public static bool IsJson(this TextWriter w) => w is CommandReport.CapturingWriter;
}
