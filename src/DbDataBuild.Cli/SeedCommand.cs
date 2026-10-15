using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sample;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project seed` (effect: repo files only; DESIGN.md 15.6). Runs the project's seeds (`seeds/`: DuckDB queries that generate the rows of the mapped models, deterministically from a seed and a scale) and
/// writes the result as a DuckDB database file (default `.dbdatabuild/seed.duckdb`), one table per mapped model, so the data can be inspected with any DuckDB tool and is the starting point for loading a
/// target. It connects to no target. The same seeds fill the mapped models in `sample`.
/// </summary>
internal static class SeedCommand
{
    public const string DefaultFile = ".dbdatabuild/seed.duckdb";

    public static int Run(CommandSpec spec, string root, int seed, int? scale, string? outFile, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        var ctx = ProjectContext.Load(root);
        var seeds = SeedLoader.Load(root);
        if (seeds.Seeds.Count == 0) { error.WriteLine($"The project has no seeds: put a DuckDB query per mapped model in {SeedLoader.Directory}/<schema name>/<table>.sql."); return CliApp.ExitUsage; }
        var path = Path.GetFullPath(outFile ?? Path.Combine(root, DefaultFile));
        IReadOnlyList<SeededTable> loaded;
        try { loaded = SeedRun.WriteFile(path, ctx.Project.MappedModels, seeds, seed, scale); }
        catch (SampleException ex) { error.WriteLine(ex.Message); return CliApp.ExitFindings; }

        var without = ctx.Project.MappedModels.Where(d => seeds.Find(d.Name) == null).Select(d => d.Name).ToList();
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        output.Payload("file", relative);
        output.Payload("seed", seed);
        output.Payload("scale", scale);
        output.Payload("tables", loaded.Select(t => new { name = t.Name, seed_file = t.File, rows = t.Rows }).ToList());
        output.Payload("declared_models_without_a_seed", without);
        output.WriteLine();
        foreach (var t in loaded) output.WriteLine($"{t.Name,-32} {t.Rows,10} row(s)   {t.File}");
        if (without.Count > 0) output.WriteLine($"\nNo seed for: {string.Join(", ", without)}.");
        output.WriteLine($"\nWrote {relative} (seed {seed}{(scale != null ? $", scale {scale}" : "")}).");
        return CliApp.ExitOk;
    }
}
