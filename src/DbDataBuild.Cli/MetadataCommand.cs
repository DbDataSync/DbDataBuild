using DbDataBuild.Models;
using DbDataBuild.Core;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project show metadata` (effect: offline only): everything the tool knows about the project and its models (of all seven kinds), as data. `--format json` prints the documents; the text form
/// is a summary table. The same documents can be stored in a target for introspection with `publish-metadata`.
/// </summary>
internal static class MetadataCommand
{
    public static int Run(CommandSpec spec, string root, string[] models, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        var ctx = ProjectContext.Load(root);
        var selected = ctx.SelectModels(models, error);
        if (selected == null) return CliApp.ExitUsage;

        var diags = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code).ToList();
        foreach (var d in diags) error.Diag(d);
        if (diags.Count > 0)
        {
            output.WriteLine($"No metadata was produced: {diags.Count} error(s) in the project. Fix them (`{ProductInfo.Cli} project compile` shows them).");
            return CliApp.ExitFindings;
        }

        var documents = MetadataBuilder.Models(ctx, selected.Select(m => m.Name).ToList()).Select(m => m.Document).ToList();
        output.Payload("project", MetadataBuilder.Project(ctx));
        output.Payload("models", documents);

        output.WriteLine($"{"model",-32} {"kind",-26} {"connections",-22} columns  definition hash");
        var hashes = MetadataBuilder.DefinitionHashes(ctx);
        foreach (var m in selected)
        {
            var hash = hashes[m.Name];
            output.WriteLine($"{m.Name,-32} {m.Kind,-26} {string.Join(",", m.Connections),-22} {m.Columns.Count,-8} {hash[..Math.Min(12, hash.Length)]}");
        }
        output.WriteLine($"\n{selected.Count} model(s) ({KindCounts(selected)}). Use `--format json` for the full documents.");
        return CliApp.ExitOk;
    }

    /// <summary>The models counted by kind, in the order of the kinds: `3 view, 2 full, 1 mapped`.</summary>
    public static string KindCounts(IEnumerable<ProjectModel> models) =>
        string.Join(", ", models.GroupBy(m => m.Kind).OrderBy(g => ModelKinds.Every.ToList().IndexOf(g.Key)).Select(g => $"{g.Count()} {g.Key}"));
}
