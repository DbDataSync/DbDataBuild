using DbDataBuild.Core;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project show metadata` (effect: offline only): everything the tool knows about the project, its source tables and its models, as data. `--format json` prints the documents; the text form
/// is a summary table. The same documents can be stored in a target for introspection with `publish-metadata`.
/// </summary>
internal static class MetadataCommand
{
    public static int Run(CommandSpec spec, string root, string[] models, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        var ctx = ProjectContext.Load(root);
        var selected = ctx.Select(models, error);
        if (selected == null) return CliApp.ExitUsage;

        var diags = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code).ToList();
        foreach (var d in diags) error.Diag(d);
        if (diags.Count > 0)
        {
            output.WriteLine($"No metadata was produced: {diags.Count} error(s) in the project. Fix them (`{ProductInfo.Cli} project compile` shows them).");
            return CliApp.ExitFindings;
        }

        var documents = selected.Select(m => MetadataBuilder.Model(ctx, m.Source, m.Sql)).ToList();
        output.Payload("project", MetadataBuilder.Project(ctx));
        output.Payload("sources", MetadataBuilder.Sources(ctx, models.Length == 0 ? null : selected.Select(m => m.Source.Definition.Name)));
        output.Payload("models", documents);

        output.WriteLine($"{"model",-32} {"kind",-26} {"connections",-22} columns  definition hash");
        foreach (var m in selected)
        {
            var hash = ctx.DefinitionHashOf(m.Sql);
            output.WriteLine($"{m.Source.Definition.Name,-32} {m.Source.Definition.Kind,-26} {string.Join(",", ctx.TargetsOf(m.Source.Definition)),-22} {m.Source.Definition.Columns.Count,-8} {hash[..Math.Min(12, hash.Length)]}");
        }
        output.WriteLine($"\n{selected.Count} model(s). Use `--format json` for the full documents.");
        return CliApp.ExitOk;
    }
}
