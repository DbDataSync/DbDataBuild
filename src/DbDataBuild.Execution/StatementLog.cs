using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbDataBuild.Execution;

/// <summary>
/// Append-only JSON lines under a directory (default `.dbdatabuild/statement-log/` in the project, not committed), one file per run.
/// Each line is flushed to disk before <see cref="Append"/> returns.
/// </summary>
public sealed class FileStatementLog : IStatementLog, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly FileStream stream;
    public string Path { get; }

    public FileStatementLog(string directory, string command, Guid runId)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)}-{command.Replace(' ', '-')}-{runId:N}.jsonl");
        stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    public void Append(StatementLogEntry entry)
    {
        var line = JsonSerializer.Serialize(new
        {
            run = entry.RunId, n = entry.Ordinal, utc = entry.Utc.ToString("O", CultureInfo.InvariantCulture), command = entry.Command, phase = entry.Phase,
            step = entry.StepId, kind = entry.Kind, hash = entry.StatementHash, text = entry.Text,
            parameters = entry.Parameters?.Select(p => new { p.Name, p.Value }), outcome = entry.Outcome,
        }, Json);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (stream)                                      // sessions that run side by side write whole lines, one at a time
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>For tests and dry runs that must not touch the disk.</summary>
public sealed class MemoryStatementLog : IStatementLog
{
    public List<StatementLogEntry> Entries { get; } = [];
    public void Append(StatementLogEntry entry) { lock (Entries) Entries.Add(entry); }
}
