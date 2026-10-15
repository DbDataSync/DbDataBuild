using System.Security.Cryptography;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Define;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project model update` (DESIGN.md 6.5). Effect class: repo files only. It reads models and the config, asks DuckDB to describe
/// queries, and writes definition (.yml) files only, never a .sql, and never connects to a target. Nothing is written before the diff is
/// shown and (interactively) confirmed, and nothing is written at all if any model still needs attention.
/// </summary>
internal static class DefineCommand
{
    /// <summary>The text of a file read as bytes (to hash it), without a byte order mark: `Encoding.GetString` keeps it as a character, and an editor on Windows writes one.</summary>
    private static string Utf8Text(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes, bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0, bytes.Length - (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0));

    /// <summary>A definition with no query beside it: a mapped model, whose problems `define` shows because it reads them as the tables models are written over.</summary>
    private static bool IsMappedModelFile(string projectRoot, string file) =>
        file.StartsWith(ProjectValidator.ModelsDir + "/", StringComparison.Ordinal) && file.EndsWith(".yml", StringComparison.Ordinal)
        && !File.Exists(Path.Combine(projectRoot, file[..^".yml".Length] + ".sql"));

    public static int Run(CommandSpec spec, string projectRoot, string[] paths, FileInfo? answersFile, bool write, bool check, bool acceptInferred,
        TextWriter output, TextWriter error, TextReader input, bool interactive)
    {
        output.WriteLine($"{Core.ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");

        if (check && (write || answersFile != null || acceptInferred))
        {
            error.WriteLine("--check asks nothing and writes nothing, so it cannot be combined with --write, --answers or --accept-inferred.");
            return CliApp.ExitUsage;
        }
        if (!check && !write && !interactive)
        {
            error.WriteLine("define asks questions, and needs a terminal for that. For non-interactive use give --answers <file> and --write (or --check in CI).");
            return CliApp.ExitUsage;
        }

        // ---- load the project ----
        var configDiags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(projectRoot, configDiags);
        var project = ProjectValidator.Validate(projectRoot, config);
        var matrixDiags = new List<Diagnostic>();
        var linter = new MatrixLinter(MatrixLoader.LoadEmbedded(matrixDiags));
        if (matrixDiags.Count > 0) throw new InvalidOperationException("The embedded support matrix is invalid: " + string.Join("; ", matrixDiags.Select(d => d.Found)));

        // ---- choose the models ----
        var mapped = project.DeclaredModels.Select(d => d.File.Length > 0 ? d.File[..^".yml".Length] : $"{ProjectValidator.ModelsDir}/{d.Name.Replace('.', '/')}").ToHashSet(StringComparer.Ordinal);
        // a copy has no query and no `columns` to write: its columns are its origin's (DESIGN.md 6.5.2), so it is left out like a mapped model
        foreach (var copy in project.BuiltModels.Where(s => s.Definition.IsCopy && s.DefinitionFile.EndsWith(".yml", StringComparison.Ordinal))) mapped.Add(copy.DefinitionFile[..^".yml".Length]);
        var selection = Select(projectRoot, paths, mapped, error, out var selectionProblem);
        if (selectionProblem) return CliApp.ExitUsage;

        var targets = new List<DefineTarget>();
        ProjectContext? macroContext = null;
        var hashes = new Dictionary<string, string?>();          // definition file -> hash of the bytes read (null: no file)
        var queryHashes = new Dictionary<string, string>();       // query file -> hash of the bytes read
        var problems = new List<Diagnostic>();
        foreach (var stem in selection)
        {
            var sqlRel = stem + ".sql";
            var ymlRel = stem + ".yml";
            var sqlPath = Path.Combine(projectRoot, sqlRel);
            var ymlPath = Path.Combine(projectRoot, ymlRel);
            if (!File.Exists(sqlPath))
            {
                problems.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(ymlRel, 0, 0), $"`{ymlRel}` has no query file `{sqlRel}`.", Fix: $"Add `{sqlRel}`, or remove `{ymlRel}`."));
                continue;
            }
            var sqlBytes = File.ReadAllBytes(sqlPath);
            queryHashes[sqlRel] = DefinitionFile.Hash(sqlBytes);
            // a head (`CREATE TABLE schema_name.object_name WITH (...) AS`) has said the name and the kind: the query is what follows it
            var parsedHead = QueryHeadParser.Parse(sqlRel, Utf8Text(sqlBytes));
            if (parsedHead.Problems.Count > 0) { problems.AddRange(parsedHead.Problems); continue; }
            var head = parsedHead.Head;
            // the name is the definition's; a file with none yet is named by the layout (only a layout that spells the schema name in the file's name can say it)
            var pathName = stem[(ProjectValidator.ModelsDir.Length + 1)..].Replace('/', '.');
            var baseName = Path.GetFileName(stem);
            var name = head?.Name ?? config.Layout switch { ModelLayout.Dotted => baseName, ModelLayout.Object => baseName, _ => pathName };
            if (head == null && !File.Exists(ymlPath) && (config.Layout == ModelLayout.Object || (config.Layout == ModelLayout.Dotted && !baseName.Contains('.'))))
            {
                problems.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(sqlRel, 0, 0), $"`{sqlRel}` has no definition yet, and with `model_layout: {config.Layout.ToString().ToLowerInvariant()}` its file name does not say the schema.",
                    Fix: $"Write `{ymlRel}` by hand with `name: <schema name>.<object name>`, or name the files `<schema name>.<object name>`, or use `model_layout: folder`."));
                continue;
            }
            string? existingText = null;
            ModelDefinition? existing = null;
            var existingProblems = new List<Diagnostic>();
            if (File.Exists(ymlPath))
            {
                var bytes = File.ReadAllBytes(ymlPath);
                hashes[ymlRel] = DefinitionFile.Hash(bytes);
                existingText = Utf8Text(bytes);
                if (head != null)
                {
                    // the definition does not say its name and kind: the project's own reading of it (with the head) is what exists
                    existing = project.BuiltModels.FirstOrDefault(s => s.DefinitionFile == ymlRel)?.Definition;
                    if (existing == null) existingProblems.Add(new Diagnostic(DiagnosticCatalog.QueryHeadInvalid, new(ymlRel, 0, 0), $"`{ymlRel}` does not load together with the head of `{sqlRel}`; `{Core.ProductInfo.Cli} project compile` says why."));
                }
                else existing = ModelDefinitionLoader.Load(existingText, ymlRel, config.Layout == ModelLayout.Folder ? name : null, existingProblems, config.Connections.Keys.ToHashSet(StringComparer.Ordinal));
                if (existing != null) name = existing.Name;
            }
            else hashes[ymlRel] = null;
            var queryText = parsedHead.Body;
            if (project.BuiltModels.FirstOrDefault(s => s.DefinitionFile == ymlRel) is { } known) queryText = QueryParameters.Mark(QueryParameters.ResolveNames(queryText, known, config, (known.Definition.Targets ?? config.DefaultConnections).FirstOrDefault() ?? ""), known, config).Sql;      // a name stands as the name it is (on the model's first connection, as the render reads it), a value as a typed marker
            var macroSupport = project.BuiltModels.FirstOrDefault(s => s.DefinitionFile == ymlRel) is { } macroSource ? (macroContext ??= ProjectContext.Load(projectRoot)).MacroSupportFor(macroSource, queryText) : null;
            targets.Add(new DefineTarget(name, ymlRel, sqlRel, queryText, existingText, existing, existingProblems, macroSupport, head));
        }

        // The graph holds every valid model (their declared columns) and the declared models. Selected models with a valid definition are among them.
        var graph = new ModelGraph(project.Models, project.DeclaredModelsAndStaging);
        var engine = new DefineEngine(graph, config, linter);

        // project-level findings worth showing: config problems and mapped model problems
        var shown = new List<Diagnostic>();
        shown.AddRange(configDiags);
        shown.AddRange(project.Diagnostics.Where(d => d.Location.File == ProjectValidator.RetiredSourcesDir || IsMappedModelFile(projectRoot, d.Location.File)));
        shown.AddRange(problems);
        output.WriteLine($"Config: {(File.Exists(Path.Combine(projectRoot, Core.ProductInfo.ConfigFile)) ? Core.ProductInfo.ConfigFile : "built-in defaults")}; {config.Describe()}");

        // ---- --check ----
        if (check)
        {
            var found = new List<Diagnostic>(shown);
            found.AddRange(engine.Check(targets));
            foreach (var d in found) error.Diag(d);
            var errors = found.Count(d => d.Severity == Severity.Error);
            output.Payload("mode", "check");
            output.Payload("definitions", targets.Count);
            output.Payload("differences", errors);
            output.WriteLine(errors == 0
                ? $"OK: {targets.Count} definition(s) in sync with their queries."
                : $"FAILED: {errors} difference(s) between definitions and queries. Run `{Core.ProductInfo.Cli} project model update` to update them.");
            return errors == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
        }

        // ---- answers ----
        AnswerFile? answers = null;
        if (answersFile != null)
        {
            var answerDiags = new List<Diagnostic>();
            answers = AnswerFileLoader.Load(File.ReadAllText(answersFile.FullName), answersFile.Name, answerDiags);
            if (answers == null)
            {
                foreach (var d in answerDiags) error.Diag(d);
                return CliApp.ExitFindings;
            }
        }

        // ---- ask and generate ----
        IPrompter? prompter = write ? null : new ConsolePrompter(input, output);
        var run = engine.Run(targets, answers, answersFile?.Name ?? QuestionResolver.NoAnswersFile, prompter, acceptInferred);
        foreach (var d in shown.Concat(run.Diagnostics)) error.Diag(d);

        output.Payload("mode", write ? "write" : "interactive");
        output.Payload("models", run.Outcomes.Select(o => new
        {
            query = o.Target.QueryFile, definition = o.Target.DefinitionFile, status = o.Status, notes = o.Notes,
            open_questions = o.Unanswered.Select(PlanCommand.QuestionJson).ToList(),
        }).ToList());
        foreach (var o in run.Outcomes)
        {
            output.WriteLine();
            output.WriteLine($"{o.Target.QueryFile} -> {o.Target.DefinitionFile}: {Describe(o.Status)}");
            foreach (var note in o.Notes) output.WriteLine($"  {note}");
            foreach (var a in o.Answers.Where(a => a.Source == AnswerSource.AcceptedProposalByFlag))
                output.WriteLine($"  accepted inferred (--accept-inferred): {a.QuestionId} = {a.Choice}{(a.Value != null ? " " + a.Value : "")}");
            foreach (var d in o.Diagnostics) error.Diag(d);
            if (o.Changed) output.Write(UnifiedDiff.Create(o.Target.ExistingText, o.NewText!, o.Target.DefinitionFile));
        }

        var changed = run.Outcomes.Where(o => o.Changed).ToList();
        if (!run.Complete || shown.Any(d => d.Severity == Severity.Error))
        {
            var attention = run.Outcomes.Count(o => !o.Done);
            output.WriteLine();
            output.WriteLine($"Nothing was written: {Math.Max(attention, 1)} model(s) need attention. Answer the questions above (interactively, or in the answers file) and run define again.");
            return CliApp.ExitFindings;
        }
        if (changed.Count == 0)
        {
            output.WriteLine();
            output.WriteLine($"{run.Outcomes.Count} definition(s) checked; nothing to change.");
            return CliApp.ExitOk;
        }

        // ---- confirm, then write ----
        if (!write)
        {
            output.WriteLine();
            output.Write($"Write {changed.Count} definition file(s)? [y/N] ");
            var reply = input.ReadLine()?.Trim().ToLowerInvariant();
            if (reply is not ("y" or "yes"))
            {
                output.WriteLine("Nothing was written.");
                return CliApp.ExitFindings;
            }
        }

        var failed = false;
        var written = new List<string>();
        foreach (var o in changed)
        {
            var path = Path.Combine(projectRoot, o.Target.DefinitionFile);
            var problem = DefinitionFile.WriteIfUnchanged(path, o.Target.DefinitionFile, hashes[o.Target.DefinitionFile], o.NewText!);
            if (problem != null) { error.Diag(problem); failed = true; }
            else { output.WriteLine($"wrote {o.Target.DefinitionFile}"); written.Add(o.Target.DefinitionFile); }
        }
        output.Payload("written", written);

        // the query files must be exactly as they were read (DESIGN.md 6.5)
        foreach (var (file, hash) in queryHashes)
            if (DefinitionFile.HashIfExists(Path.Combine(projectRoot, file)) != hash)
                throw new InvalidOperationException($"`{file}` changed while define was running. define never writes query files, so something else touched it.");

        if (!failed && written.Count > 0) output.Next("project compile");
        return failed ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    private static string Describe(DefineStatus s) => s switch
    {
        DefineStatus.InSync => "in sync",
        DefineStatus.Unchanged => "unchanged (differences were deliberately kept)",
        DefineStatus.Created => "new definition",
        DefineStatus.Updated => "update",
        DefineStatus.Incomplete => "needs answers",
        DefineStatus.Failed => "failed",
        DefineStatus.Skipped => "skipped",
        _ => s.ToString(),
    };

    /// <summary>The model stems (project-relative path without extension, under models/) the given paths select; all models when none are given.</summary>
    private static List<string> Select(string projectRoot, string[] paths, IReadOnlySet<string> mapped, TextWriter error, out bool problem)
    {
        problem = false;
        var modelsRoot = Path.GetFullPath(Path.Combine(projectRoot, ProjectValidator.ModelsDir));
        if (!Directory.Exists(modelsRoot))
        {
            error.WriteLine($"`{ProjectValidator.ModelsDir}/` was not found under {projectRoot}.");
            problem = true;
            return [];
        }

        IEnumerable<string> StemsUnder(string dir) => Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".sql", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != Core.ProductInfo.FolderConfigFile && !f.EndsWith(".native.sql", StringComparison.Ordinal))         // a project file, or the text of a native model, not a model
            .Select(f => Stem(projectRoot, f))
            .Where(s => !mapped.Contains(s));                                          // a mapped model has no query to define from

        var stems = new SortedSet<string>(StringComparer.Ordinal);
        if (paths.Length == 0) stems.UnionWith(StemsUnder(modelsRoot));
        foreach (var p in paths)
        {
            var full = new[] { Path.GetFullPath(Path.Combine(projectRoot, p)), Path.GetFullPath(p) }.FirstOrDefault(c => File.Exists(c) || Directory.Exists(c));
            if (full == null) { error.WriteLine($"`{p}` was not found."); problem = true; continue; }
            if (!full.StartsWith(modelsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != modelsRoot)
            {
                error.WriteLine($"`{p}` is not under {ProjectValidator.ModelsDir}/.");
                problem = true;
                continue;
            }
            if (Directory.Exists(full)) stems.UnionWith(StemsUnder(full));
            else if (full.EndsWith(".sql", StringComparison.Ordinal) || full.EndsWith(".yml", StringComparison.Ordinal))
            {
                if (mapped.Contains(Stem(projectRoot, full))) { error.WriteLine($"`{p}` is a mapped model: it is declared, not built, so there is no query to define it from."); problem = true; }
                else if (Path.GetFileName(full) == Core.ProductInfo.FolderConfigFile) { error.WriteLine($"`{p}` is a project file, not a model."); problem = true; }
                else stems.Add(Stem(projectRoot, full));
            }
            else { error.WriteLine($"`{p}` is not a .sql or .yml file or a directory."); problem = true; }
        }
        return stems.ToList();
    }

    private static string Stem(string projectRoot, string file)
    {
        var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
        return rel[..rel.LastIndexOf('.')];
    }
}
