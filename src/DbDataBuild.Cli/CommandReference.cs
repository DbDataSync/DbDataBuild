using System.CommandLine;
using System.Text;
using DbDataBuild.Core;

namespace DbDataBuild.Cli;

/// <summary>
/// The command reference (`docs/commands.md`), generated from the command tree that the program itself runs, so a command, an option or a description cannot be documented wrongly: a unit test compares
/// the file with this text, and `UPDATE_GOLDEN=1` rewrites it.
/// </summary>
public static class CommandReference
{
    public static string Render()
    {
        var root = CliApp.Build(TextWriter.Null, TextWriter.Null, TextReader.Null, interactive: false);
        var sb = new StringBuilder();
        sb.AppendLineLf("# Command reference");
        sb.AppendLineLf();
        sb.AppendLineLf($"Generated from the command tree of `{ProductInfo.Cli}` (`UPDATE_GOLDEN=1 dotnet test tests/DbDataBuild.Tests.Unit` rewrites it; a test fails when it is out of date). Every command takes `--format json` to give one JSON document on standard output (see `schemas/output.schema.json`). The effect class says what a command may touch: offline only, repository files only, a target read-only, the tracking tables only, or a target's data or definitions (DESIGN.md section 9.1).");
        sb.AppendLineLf();
        sb.AppendLineLf("| Command | Reads and writes | Effect | Purpose |");
        sb.AppendLineLf("|---|---|---|---|");
        foreach (var spec in CommandSpecs.All) sb.AppendLineLf($"| [`{spec.Name}`](#{Anchor(spec.Name)}) | `{spec.Disposition}` | {spec.Effect.Describe()} | {Cell(spec.Purpose)} |");
        foreach (var spec in CommandSpecs.All)
        {
            var command = CliApp.Find(root, spec.Name);
            if (command == null) continue;
            sb.AppendLineLf();
            sb.AppendLineLf($"## {spec.Name}");
            sb.AppendLineLf();
            sb.AppendLineLf($"{spec.Purpose}.".Replace("..", "."));
            sb.AppendLineLf();
            sb.AppendLineLf($"Effect: {spec.Effect.Describe()}. Reads and writes: `{spec.Disposition}`{(spec.Lane.Length > 0 ? $". Lane: {spec.Lane}" : "")}.");
            sb.AppendLineLf();
            sb.AppendLineLf($"Usage: `{ProductInfo.Cli} {spec.Name}{string.Concat(command.Arguments.Select(a => " " + (a.Arity.MinimumNumberOfValues > 0 ? $"<{a.Name}>" : $"[{a.Name}]") + (a.Arity.MaximumNumberOfValues > 1 ? "..." : "")))}{(command.Options.Count > 0 ? " [options]" : "")}`");
            if (command.Arguments.Count > 0)
            {
                sb.AppendLineLf();
                sb.AppendLineLf("| Argument | Meaning |");
                sb.AppendLineLf("|---|---|");
                foreach (var a in command.Arguments) sb.AppendLineLf($"| `{a.Name}`{(a.Arity.MaximumNumberOfValues > 1 ? " (several)" : "")} | {Cell(a.Description)} |");
            }
            if (command.Options.Count > 0)
            {
                sb.AppendLineLf();
                sb.AppendLineLf("| Option | Meaning |");
                sb.AppendLineLf("|---|---|");
                foreach (var o in command.Options.OrderBy(o => o.Name, StringComparer.Ordinal)) sb.AppendLineLf($"| `{o.Name}`{(o.ValueType == typeof(bool) ? "" : $" `<{(Nullable.GetUnderlyingType(o.ValueType) ?? o.ValueType).Name.ToLowerInvariant().Replace("string", "text").Replace("int32", "number").Replace("directoryinfo", "path").Replace("fileinfo", "file").Replace("[]", " ...")}>`")} | {Cell(o.Description)} |");
            }
        }
        return sb.ToString();
    }

    private static string Anchor(string name) => name.Replace(' ', '-');

    private static string Cell(string? text) => (text ?? "").Replace("|", "\\|").Replace("\n", " ");
}
