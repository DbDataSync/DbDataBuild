using System.CommandLine;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

public static class CliApp
{
    public const int ExitOk = 0, ExitFindings = 1, ExitUsage = 2, ExitNotImplemented = 3, ExitBusy = 4, ExitInternal = 70;

    /// <param name="input">Where interactive answers are read from. Only used when <paramref name="interactive"/> is true.</param>
    /// <param name="interactive">Whether a person is there to answer questions (a terminal). Commands that ask refuse to run without one unless they are given everything.</param>
    /// <param name="environment">Where logins are read from (connection strings in environment variables). Defaults to the process environment.</param>
    public static int Run(string[] args, TextWriter output, TextWriter error, TextReader? input = null, bool interactive = false, Func<string, string?>? environment = null) =>
        Guarded(args, error, () => { DbDataBuild.Execution.DriverSettings.Apply(); return Build(output, error, input ?? TextReader.Null, interactive, environment ?? Environment.GetEnvironmentVariable).Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null }).Invoke(new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false }); }, output);

    /// <summary>
    /// The error number of a database exception (SQL Server's `Number`, PostgreSQL's `SqlState`), which says what went wrong (18456 is a failed login, 28P01 a wrong password) without the driver's message, which
    /// can quote a server name or a value. Read by name so the CLI does not depend on a driver.
    /// </summary>
    internal static string DriverNumber(Exception ex) => DbDataBuild.Execution.DriverFailure.Number(ex);

    /// <summary>Top-level guard: unhandled exceptions become an internal-error diagnostic, never a stack trace.</summary>
    public static int Guarded(string[] args, TextWriter error, Func<int> body, TextWriter? output = null)
    {
        try
        {
            return body();
        }
        catch (System.Data.Common.DbException ex)
        {
            // a database that refused or lost a command is not a defect of the tool: the type and the error number, never the driver's message (it can quote a server name or a value)
            var text = $"The database reported {ex.GetType().Name}{DriverNumber(ex)} while running `{string.Join(' ', args)}`. Statements already sent to a target are recorded in the statement log under {InitCommand.StatementLogDir}/.";
            var json = output != null && (args.Zip(args.Skip(1)).Any(p => p is ("--format", "json")) || args.Contains("--format=json"));
            var diagnostic = new Diagnostic(DiagnosticCatalog.DatabaseErrorUnhandled, new("<database>", 0, 0), text);
            if (json) output!.WriteLine(CommandReport.Failure(args.FirstOrDefault(a => !a.StartsWith('-')) ?? "", diagnostic, ExitFindings));
            else error.Diag(diagnostic);
            return ExitFindings;
        }
        catch (Exception ex)
        {
            // Never a stack trace as primary output (DESIGN.md 14.2). Full detail would go to a scrubbed log file.
            var text = $"The tool failed with {ex.GetType().Name}{DriverNumber(ex)} while running `{string.Join(' ', args)}`. Statements already sent to a target are recorded in the statement log under {InitCommand.StatementLogDir}/.";
            var json = output != null && (args.Zip(args.Skip(1)).Any(p => p is ("--format", "json")) || args.Contains("--format=json"));
            if (json) output!.WriteLine(CommandReport.InternalError(args.FirstOrDefault(a => !a.StartsWith('-')) ?? "", text));
            else error.Diag(new Diagnostic(DiagnosticCatalog.InternalError, new("<internal>", 0, 0), text));
            return ExitInternal;
        }
    }

    public static RootCommand Build(TextWriter output, TextWriter error, TextReader input, bool interactive, Func<string, string?>? environment = null)
    {
        var root = new RootCommand($"{ProductInfo.Name}: explicit SQL transformation tool. Every command declares an effect class.");
        var format = new Option<string>("--format") { Description = "text (default) or json: one JSON document on standard output with the data, the diagnostics and the human text", Recursive = true, DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        root.Options.Add(format);

        // every command's work runs against a report: text goes where it always did, or into one JSON document
        int Reported(ParseResult pr, CommandSpec spec, Func<TextWriter, TextWriter, int> body)
        {
            var report = CommandReport.Create(pr.GetValue(format) == "json", spec.Name, output, error);
            return report.Finish(body(report.Output, report.Error));
        }

        foreach (var spec in CommandSpecs.All)
        {
            var cmd = new Command(spec.Path[^1], $"[{spec.Effect.Describe()}] {spec.Purpose}");
            switch (spec.Name)
            {
                case "project model create":
                    var mcName = new Argument<string>("model") { Description = "The model's name, schema_name.object_name (marts.fct_orders)" };
                    var mcProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var mcKind = new Option<string>("--kind") { Description = "view (the default), full, incremental_by_unique_key or incremental_by_time_range", DefaultValueFactory = _ => ModelKinds.View };
                    mcKind.AcceptOnlyFromAmong(ModelCreateCommand.Kinds.ToArray());
                    var mcConnection = new Option<string[]>("--connection") { Description = "The connections it is built on (default: the project's default connections); repeat for several", AllowMultipleArgumentsPerToken = false, DefaultValueFactory = _ => [] };
                    cmd.Arguments.Add(mcName); cmd.Options.Add(mcProject); cmd.Options.Add(mcKind); cmd.Options.Add(mcConnection);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => ModelCreateCommand.Run(spec, pr.GetValue(mcProject)!.FullName, pr.GetValue(mcName)!, pr.GetValue(mcKind)!, pr.GetValue(mcConnection) ?? [], o, e)));
                    break;
                case "project model update":
                    var paths = new Argument<string[]>("paths") { Description = "Model .sql or .yml files, or directories under models/ (default: every model)", Arity = ArgumentArity.ZeroOrMore };
                    var defineProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var answers = new Option<FileInfo?>("--answers") { Description = "Answers file for the questions (see schemas/answers.schema.json)" };
                    var write = new Option<bool>("--write") { Description = "Non-interactive: write the definitions without asking (needs --answers for any open questions)" };
                    var check = new Option<bool>("--check") { Description = "CI: fail if any definition is out of sync with its query; asks nothing, writes nothing" };
                    var accept = new Option<bool>("--accept-inferred") { Description = "Accept inferred proposals marked high certainty (names from paths, types from DuckDB, nullability from lineage)" };
                    var defineOffline = new Option<bool>("--offline") { Description = "Read no connection: update only the definitions of models from their queries (no mapped model is compared with its table)" };
                    cmd.Arguments.Add(paths); cmd.Options.Add(defineOffline);
                    cmd.Options.Add(defineProject); cmd.Options.Add(answers); cmd.Options.Add(write); cmd.Options.Add(check); cmd.Options.Add(accept);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => ModelUpdateCommand.Run(spec, pr.GetValue(defineProject)!.FullName, pr.GetValue(paths) ?? [], pr.GetValue(answers), pr.GetValue(write), pr.GetValue(check), pr.GetValue(accept), pr.GetValue(defineOffline), o, e, input, interactive && !o.IsJson(), environment ?? Environment.GetEnvironmentVariable)));
                    break;
                case "project compile":
                    var renderModels = new Argument<string[]>("models") { Description = "Model names (marts.fct_orders), model files, or directories (default: every model)", Arity = ArgumentArity.ZeroOrMore };
                    var renderProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var renderTarget = new Option<string[]>("--connection") { Description = "Only these connections", AllowMultipleArgumentsPerToken = false, DefaultValueFactory = _ => [] };
                    var renderCheck = new Option<bool>("--check") { Description = "CI: validate, and fail if the committed rendered/ files differ from a fresh compile; writes nothing" };
                    var renderContent = new Option<bool>("--content") { Description = "With --format json: put each rendered file's text in the document (files[].content), so a client can show the lowered and rendered scripts; validates and writes nothing" };
                    cmd.Arguments.Add(renderModels);
                    cmd.Options.Add(renderProject); cmd.Options.Add(renderTarget); cmd.Options.Add(renderCheck); cmd.Options.Add(renderContent);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => Compile(spec, pr.GetValue(renderProject)!.FullName, pr.GetValue(renderModels) ?? [], pr.GetValue(renderTarget) ?? [], pr.GetValue(renderCheck), pr.GetValue(renderContent), o, e)));
                    break;
                case "project agent-kit":
                    var kitProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var kitDir = new Option<string?>("--dir") { Description = "Where to install the kit, relative to the project (default: .claude/skills/dbdatabuild)" };
                    var kitWrite = new Option<bool>("--write") { Description = "Install the files (default: list them and write nothing)" };
                    var kitCheck = new Option<bool>("--check") { Description = "CI: fail if the installed kit differs from this version's; writes nothing" };
                    var kitMcp = new Option<bool>("--mcp") { Description = "Also add the dbdatabuild MCP server to the project's .mcp.json (Claude Code starts it: `dbdatabuild ui mcp --project .`, read-only; other servers in the file are kept). With --write it writes the file, with --check it checks it, otherwise it only says what it would do." };
                    cmd.Options.Add(kitProject); cmd.Options.Add(kitDir); cmd.Options.Add(kitWrite); cmd.Options.Add(kitCheck); cmd.Options.Add(kitMcp);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => AgentKitCommand.Run(spec, pr.GetValue(kitProject)!.FullName, pr.GetValue(kitDir), pr.GetValue(kitWrite), pr.GetValue(kitCheck), pr.GetValue(kitMcp), o, e)));
                    break;
                case "ui terminal":
                    var tuiProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var tuiTarget = ConnectionArgument("Connection to work on", spec.Name);
                    cmd.Arguments.Add(tuiTarget); cmd.Options.Add(tuiProject);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => TuiCommand.Run(spec, pr.GetValue(tuiProject)!.FullName, pr.GetValue(tuiTarget), o.IsJson(), o, e, environment ?? Environment.GetEnvironmentVariable)));
                    break;
                case "ui mcp":
                    var mcpProject = new Option<DirectoryInfo>("--project") { Description = "Project root the tools work on", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var mcpWrites = new Option<bool>("--allow-writes") { Description = "Also offer the commands that change a target or its tracking tables (apply, run, load-seeds, init, ack, publish-metadata). Off by default: a person runs those." };
                    var mcpApply = new Option<bool>("--allow-apply") { Description = "Let an MCP app (a page the host shows the person) apply plans: the person confirms in the app by typing the plan's target; the tools it calls are hidden from the model. Needs the write login in this environment. Independent of --allow-writes, which offers the commands to the model (each run then needs the person's approval)." };
                    var mcpNoShow = new Option<bool>("--no-show") { Description = "Do not offer the `show` tool (the link to the page in a browser, or the app screen). The other tools, and the app for the tools that carry it, are unchanged." };
                    cmd.Options.Add(mcpProject); cmd.Options.Add(mcpWrites); cmd.Options.Add(mcpApply); cmd.Options.Add(mcpNoShow);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => Mcp.McpCommand.Run(Path.GetFullPath(pr.GetValue(mcpProject)!.FullName), pr.GetValue(mcpWrites), pr.GetValue(mcpApply), pr.GetValue(mcpNoShow), o.IsJson(), input, o, e, environment ?? Environment.GetEnvironmentVariable)));
                    break;
                case "ui web":
                    var webProject = new Option<DirectoryInfo>("--project") { Description = "Project root to show", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var webPort = new Option<int>("--port") { Description = "Port on the loopback address (default: a free one)", DefaultValueFactory = _ => 0 };
                    var webApply = new Option<bool>("--allow-apply") { Description = "Let the page apply plans (a person confirms each one by typing the plan's id and connection; needs the write login in this environment). Off by default: the page reads and plans only." };
                    cmd.Options.Add(webProject); cmd.Options.Add(webPort); cmd.Options.Add(webApply);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => Web.WebCommand.Run(Path.GetFullPath(pr.GetValue(webProject)!.FullName), pr.GetValue(webPort), pr.GetValue(webApply), o.IsJson(), o, e, environment ?? Environment.GetEnvironmentVariable)));
                    break;
                case "project sample":
                    var sampleModels = new Argument<string[]>("models") { Description = "Model names (marts.fct_orders), model files, or directories (default: every model)", Arity = ArgumentArity.ZeroOrMore };
                    var sampleProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var sampleRows = new Option<int>("--rows") { Description = "Rows generated for each declared model", DefaultValueFactory = _ => 50 };
                    var sampleSeed = new Option<int>("--seed") { Description = "Seed of the generator: the same seed gives the same rows", DefaultValueFactory = _ => 1 };
                    var sampleLimit = new Option<int>("--limit") { Description = "Rows of each result to show (the row count is always complete)", DefaultValueFactory = _ => 20 };
                    var sampleData = new Option<DirectoryInfo?>("--data") { Description = "A directory of CSV files named after declared models (staging.orders.csv) to use instead of generated rows" };
                    var sampleDeclared = new Option<bool>("--declared") { Description = "Also show the declared (mapped and native) models the models read" };
                    var sampleScale = new Option<int?>("--scale") { Description = "For declared models that have a seed (seeds/): the scale the seed reads with getvariable('scale'), usually how many of the main entity (default: the project's own)" };
                    cmd.Arguments.Add(sampleModels); cmd.Options.Add(sampleScale);
                    cmd.Options.Add(sampleProject); cmd.Options.Add(sampleRows); cmd.Options.Add(sampleSeed); cmd.Options.Add(sampleLimit); cmd.Options.Add(sampleData); cmd.Options.Add(sampleDeclared);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => SampleCommand.Run(spec, pr.GetValue(sampleProject)!.FullName, pr.GetValue(sampleModels) ?? [], pr.GetValue(sampleRows), pr.GetValue(sampleSeed), pr.GetValue(sampleLimit), pr.GetValue(sampleScale), pr.GetValue(sampleData)?.FullName, pr.GetValue(sampleDeclared), o, e)));
                    break;
                case "project seed":
                    var seedProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var seedSeed = new Option<int>("--seed") { Description = "The variable `seed` the seeds read: the same seed and scale give the same rows", DefaultValueFactory = _ => 1 };
                    var seedScale = new Option<int?>("--scale") { Description = "The variable `scale` the seeds read, usually how many of the main entity (default: the project's own)" };
                    var seedOut = new Option<FileInfo?>("--out") { Description = $"The DuckDB file to write (default: {SeedCommand.DefaultFile})" };
                    cmd.Options.Add(seedProject); cmd.Options.Add(seedSeed); cmd.Options.Add(seedScale); cmd.Options.Add(seedOut);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => SeedCommand.Run(spec, pr.GetValue(seedProject)!.FullName, pr.GetValue(seedSeed), pr.GetValue(seedScale), pr.GetValue(seedOut)?.FullName, o, e)));
                    break;
                case "project create":
                    var newTemplate = new Argument<string?>("template") { Description = "The template to create (omit to list them)", Arity = ArgumentArity.ZeroOrOne };
                    var newDirectory = new Argument<string?>("directory") { Description = "Where to write it (default: a directory named after the template)", Arity = ArgumentArity.ZeroOrOne };
                    cmd.Arguments.Add(newTemplate); cmd.Arguments.Add(newDirectory);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => NewCommand.Run(spec, pr.GetValue(newTemplate), pr.GetValue(newDirectory), o, e)));
                    break;
                case "project show loads":
                    var loadsProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    cmd.Options.Add(loadsProject);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => RenderCommand.Loads(spec, pr.GetValue(loadsProject)!.FullName, o, e)));
                    break;
                case "connection seed":
                    var lsProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var lsTarget = ConnectionArgument("Connection to load", spec.Name);
                    var lsSeed = new Option<int>("--seed") { Description = "The variable `seed` the seeds read: the same seed and scale give the same rows", DefaultValueFactory = _ => 1 };
                    var lsScale = new Option<int?>("--scale") { Description = "The variable `scale` the seeds read, usually how many of the main entity (default: the project's own)" };
                    var lsReplace = new Option<bool>("--replace") { Description = "Drop and recreate a seeded table that already exists (default: stop at the first table that exists)" };
                    var lsApply = new Option<bool>("--apply") { Description = "Create and fill the tables on the write login (default: print what would happen and connect to nothing)" };
                    cmd.Arguments.Add(lsTarget); cmd.Options.Add(lsProject); cmd.Options.Add(lsSeed); cmd.Options.Add(lsScale); cmd.Options.Add(lsReplace); cmd.Options.Add(lsApply);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => LoadSeedsCommand.Run(spec, pr.GetValue(lsProject)!.FullName, pr.GetValue(lsTarget), pr.GetValue(lsSeed), pr.GetValue(lsScale), pr.GetValue(lsReplace), pr.GetValue(lsApply), o, e, environment ?? Environment.GetEnvironmentVariable)));
                    break;
                case "connection inspect":
                    var inspectProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var inspectTarget = ConnectionArgument("Connection to inspect", spec.Name);
                    cmd.Arguments.Add(inspectTarget); cmd.Options.Add(inspectProject);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => InspectCommand.Run(spec, pr.GetValue(inspectProject)!.FullName, pr.GetValue(inspectTarget), o, e, environment ?? Environment.GetEnvironmentVariable)));
                    break;
                case "connection init":
                    var initProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains dbdatabuild.yml)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var initTarget = ConnectionArgument("Connection to initialize", spec.Name);
                    var initApply = new Option<bool>("--apply") { Description = "Run the script on the write login (default: print it for review and connect to nothing)" };
                    var initUpgrade = new Option<bool>("--upgrade") { Description = "Bring tracking tables of an older layout (before 4) to this one first: adds the `connection` column to each record table (every existing row gets the connection being initialized), puts it in the primary keys and drops the old views; the script is printed for review like the rest" };
                    cmd.Arguments.Add(initTarget); cmd.Options.Add(initProject); cmd.Options.Add(initApply); cmd.Options.Add(initUpgrade);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => InitCommand.Run(spec, pr.GetValue(initProject)!.FullName, pr.GetValue(initTarget), pr.GetValue(initApply), pr.GetValue(initUpgrade), o, e, environment ?? Environment.GetEnvironmentVariable)));
                    break;
                case "connection deploy":
                    var planModels = new Argument<string[]>("models") { Description = "Model names, files or directories to plan (default: every model that declares the connection)", Arity = ArgumentArity.ZeroOrMore };
                    GuardOptionLike(planModels, spec.Name);
                    var planProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var planTarget = ConnectionArgument("Connection to deploy to", spec.Name);
                    var planAnswers = new Option<FileInfo?>("--answers") { Description = "Answers file for the questions (see schemas/answers.schema.json)" };
                    var planAccept = new Option<bool>("--accept-inferred") { Description = "Accept inferred proposals marked high certainty" };
                    var planOut = new Option<DirectoryInfo?>("--output") { Description = "Where to write the plan files (default: plans/<connection>/)" };
                    var planOp = new Option<string[]>("--op") { Description = "model=operation: load this model with a non-default operation (repeatable)", DefaultValueFactory = _ => [] };
                    var planBackfill = new Option<string[]>("--backfill") { Description = "model=operation: plan that operation as a backfill (risky; needs --allow-risky to apply); the model has no routine load in this plan (repeatable)", DefaultValueFactory = _ => [] };
                    var planParam = new Option<string[]>("--param") { Description = "model.operation.parameter=value: the value of a runtime parameter of a load operation, instead of an answers file (repeatable)", DefaultValueFactory = _ => [] };
                    var deployWritePlan = new Option<bool>("--write-plan") { Description = "Plan and write the plan files, then stop: nothing is applied" };
                    var deployApplyPlan = new Option<FileInfo?>("--apply-plan") { Description = "Apply a plan file written by --write-plan (plans/<connection>/<id>.plan.yml). A plan that stopped part-way continues; a completed plan is refused" };
                    var deployAck = new Option<string?>("--ack") { Description = "Record a decision instead of deploying, as kind:name: drift:<object> (it changed outside the tool), definition:<model> (an incremental model's query changed), history:<model.column> (a recorded backfill that never happened). Needs --reason" };
                    var deployReason = new Option<string?>("--reason") { Description = "With --ack: why the change is accepted (required; recorded with your login)" };
                    var deployRisky = new Option<bool>("--allow-risky") { Description = "Allow the plan's risky steps" };
                    var deployDestructive = new Option<string[]>("--allow-destructive") { Description = "Object (marts.fct) whose destructive steps are allowed; repeat for several objects", DefaultValueFactory = _ => [] };
                    var deployDirty = new Option<bool>("--allow-dirty") { Description = "Apply from a working tree with uncommitted changes (recorded)" };
                    var deployDry = new Option<bool>("--dry-run") { Description = "Apply: run every check and print every statement; execute nothing" };
                    var deployYes = new Option<bool>("--yes") { Description = "Apply the plan without asking (for a script; risky and destructive steps still need their allowances, and it does not approve computed inputs that moved since the plan: `--input` does)" };
                    var deployInput = new Option<string[]>("--input") { Description = "`[model.load.]name=value`: the value of an input of a load, instead of the one a resolver computes (repeatable). With the plan it pins the input: it is not computed, so it cannot move. With --apply-plan it is how a deployer says \"yes, with this value\" to an input that moved (the stop prints each override as it would be typed)", DefaultValueFactory = _ => [] };
                    cmd.Options.Add(planOp); cmd.Options.Add(planBackfill); cmd.Options.Add(planParam);
                    cmd.Arguments.Add(planTarget); cmd.Arguments.Add(planModels);
                    cmd.Options.Add(planProject); cmd.Options.Add(planAnswers); cmd.Options.Add(planAccept); cmd.Options.Add(planOut);
                    cmd.Options.Add(deployInput); cmd.Options.Add(deployWritePlan); cmd.Options.Add(deployApplyPlan); cmd.Options.Add(deployAck); cmd.Options.Add(deployReason); cmd.Options.Add(deployRisky); cmd.Options.Add(deployDestructive); cmd.Options.Add(deployDirty); cmd.Options.Add(deployDry); cmd.Options.Add(deployYes);
                    cmd.SetAction(pr => { var (planConn, planRest) = SplitConnection(pr.GetValue(planProject)!.FullName, pr.GetValue(planTarget), pr.GetValue(planModels) ?? []); return Reported(pr, spec, (o, e) => DeployCommand.Run(spec, pr.GetValue(planProject)!.FullName, planConn, planRest, pr.GetValue(planAnswers), pr.GetValue(planAccept), pr.GetValue(planOut), pr.GetValue(planOp) ?? [], pr.GetValue(planBackfill) ?? [], pr.GetValue(planParam) ?? [],
                        pr.GetValue(deployWritePlan), pr.GetValue(deployApplyPlan), pr.GetValue(deployAck), pr.GetValue(deployReason), pr.GetValue(deployRisky), pr.GetValue(deployDestructive) ?? [], pr.GetValue(deployDirty), pr.GetValue(deployDry), pr.GetValue(deployYes), pr.GetValue(deployInput) ?? [],
                        o, e, input, interactive && !o.IsJson(), environment ?? Environment.GetEnvironmentVariable)); });
                    break;
                case "connection status":
                    var checkModels = new Argument<string[]>("models") { Description = "A refresh plan first, to see only that plan's line (named as `connection refresh` names it), then model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:key=value`, `changed:<git ref>`, `exclude:...` (default: every model that declares the connection)", Arity = ArgumentArity.ZeroOrMore };
                    GuardOptionLike(checkModels, spec.Name);
                    var checkProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var checkTarget = ConnectionArgument("Connection to check", spec.Name);
                    cmd.Arguments.Add(checkTarget); cmd.Arguments.Add(checkModels);
                    cmd.Options.Add(checkProject);
                    cmd.SetAction(pr => { var (checkConn, checkRest) = SplitConnection(pr.GetValue(checkProject)!.FullName, pr.GetValue(checkTarget), pr.GetValue(checkModels) ?? []); return Reported(pr, spec, (o, e) => PlanCommand.Check(spec, pr.GetValue(checkProject)!.FullName, checkConn, checkRest, o, e, environment ?? Environment.GetEnvironmentVariable)); });
                    break;
                case "connection refresh":
                    var runPlan = new Argument<string?>("plan") { Description = "The refresh plan to run (default: `default`); a load named directly as `model.load` runs on its own, in no plan", Arity = ArgumentArity.ZeroOrOne };
                    GuardOptionLike(runPlan, spec.Name);
                    var runModels = new Argument<string[]>("selectors") { Description = "Which members of the plan to run: model names, `tag:`, `kind:`, `connection:`, `path:`, `property:`, `+model`, `model+`, `@model`, `changed:<ref>`, `exclude:<selector>`, `load:<name>`, or patterns with * and ? (default: the whole plan)", Arity = ArgumentArity.ZeroOrMore };
                    GuardOptionLike(runModels, spec.Name);
                    var runProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var runTarget = ConnectionArgument("Connection", spec.Name);
                    var runCheck = new Option<string?>("--check") { Description = "The safety check before the loads, instead of the project's `refresh.check`: none (nothing; the engine's errors are the check), project (this project was deployed here), objects (each object against what was last deployed), live (each object against the catalog now)" };
                    runCheck.AcceptOnlyFromAmong("none", "project", "objects", "live");
                    var runOnFail = new Option<string?>("--on-fail") { Description = "What a failed check does, instead of the project's `refresh.on_fail`: block (stop before anything runs) or warn (report and run)" };
                    runOnFail.AcceptOnlyFromAmong("block", "warn");
                    var runDirty = new Option<bool>("--allow-dirty") { Description = "Run from a working tree with uncommitted changes (recorded)" };
                    var runDry = new Option<bool>("--dry-run") { Description = "Run every check and print every statement; execute nothing" };
                    var runInput = new Option<string[]>("--input") { Description = "`[model.]name=value`: the value of an input of a load to run, instead of its default or the plan's (repeatable); it replaces a resolver, which is then not run, and is recorded as coming from the flag. `model.name` is needed when more than one load has the input", DefaultValueFactory = _ => [] };
                    var runOnlyFailed = new Option<bool>("--only-failed") { Description = "Run only the loads of the plan that did not end done in its last run: failed, skipped or not run (a full run again is also safe, loads are idempotent). Needs a plan and its recorded runs" };
                    var runParallel = new Option<int?>("--parallel") { Description = "How many loads may run at once, instead of the plan's `parallel` (1 to 64; default 1, one after another). A load starts when the loads of the plan that it reads are done; `max_parallel` on the connection caps it" };
                    cmd.Arguments.Add(runTarget); cmd.Arguments.Add(runPlan); cmd.Arguments.Add(runModels);
                    cmd.Options.Add(runProject); cmd.Options.Add(runCheck); cmd.Options.Add(runOnFail); cmd.Options.Add(runDirty); cmd.Options.Add(runDry); cmd.Options.Add(runInput); cmd.Options.Add(runOnlyFailed); cmd.Options.Add(runParallel);
                    cmd.SetAction(pr => { var (runConn, runRest) = SplitConnection(pr.GetValue(runProject)!.FullName, pr.GetValue(runTarget), [.. pr.GetValue(runPlan) is { } rp ? [rp] : Array.Empty<string>(), .. pr.GetValue(runModels) ?? []]); return Reported(pr, spec, (o, e) => RefreshCommand.Run(spec, pr.GetValue(runProject)!.FullName, runConn, runRest, pr.GetValue(runCheck), pr.GetValue(runOnFail), pr.GetValue(runDirty), pr.GetValue(runDry), pr.GetValue(runInput) ?? [], pr.GetValue(runOnlyFailed), pr.GetValue(runParallel), o, e, environment ?? Environment.GetEnvironmentVariable)); });
                    break;
                case "connection monitor":
                    var reportProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var reportTarget = ConnectionArgument("Connection", spec.Name);
                    var reportLast = new Option<int>("--last") { Description = "How many recent rows of each history to show", DefaultValueFactory = _ => 10 };
                    var reportLane = new Option<string?>("--lane") { Description = "Only the events of this lane (and their DDL and loads): deploy or refresh" };
                    reportLane.AcceptOnlyFromAmong("deploy", "refresh");
                    var reportPlan = new Argument<string?>("plan") { Description = "A refresh plan: show its runs (complete and partial apart) and each member's last success and failure, instead of the tables", Arity = ArgumentArity.ZeroOrOne };
                    GuardOptionLike(reportPlan, spec.Name);
                    var reportEvent = new Option<string?>("--event") { Description = "One event in detail, by its id (as the first table shows it): its states, its DDL and its loads" };
                    var reportAttention = new Option<bool>("--attention") { Description = "Only what needs attention (the other tables are left out of the text; the document still has them)" };
                    cmd.Arguments.Add(reportTarget); cmd.Arguments.Add(reportPlan); cmd.Options.Add(reportProject); cmd.Options.Add(reportLast); cmd.Options.Add(reportLane); cmd.Options.Add(reportEvent); cmd.Options.Add(reportAttention);
                    cmd.SetAction(pr => { var (reportConn, reportRest) = SplitConnection(pr.GetValue(reportProject)!.FullName, pr.GetValue(reportTarget), pr.GetValue(reportPlan) is { } rpn ? [rpn] : []); return Reported(pr, spec, (o, e) => ReportCommand.Run(spec, pr.GetValue(reportProject)!.FullName, reportConn, pr.GetValue(reportLast), pr.GetValue(reportLane), pr.GetValue(reportEvent), pr.GetValue(reportAttention), reportRest.FirstOrDefault(), o, e, environment ?? Environment.GetEnvironmentVariable)); });
                    break;
                case "connection compare":
                    var diffTable = new Argument<string?>("table") { Description = "The table or view to compare, as schema_name.table_name (a model's table, for example). Required; the connection comes before it", Arity = ArgumentArity.ZeroOrOne };
                    GuardOptionLike(diffTable, spec.Name);
                    var diffProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var diffTarget = ConnectionArgument("Connection", spec.Name);
                    var diffAgainst = new Option<string?>("--against") { Description = "The table or view to compare it with, as schema_name.table_name" };
                    var diffAgainstSchema = new Option<string?>("--against-schema") { Description = "Compare with the table of the same name under this schema name (a development copy, for example)" };
                    var diffAgainstConnection = new Option<string?>("--against-connection") { Description = "Compare with the table on this connection (the same table name unless --against or --against-schema says another); the connections may be on different engines, and only digests of the values are compared" };
                    var diffKey = new Option<string[]>("--key") { Description = "Columns that identify a row (default: the model's grain or unique key, or a mapped model's grain)", AllowMultipleArgumentsPerToken = true };
                    var diffOnly = new Option<string[]>("--columns") { Description = "Compare only these columns (and the key)", AllowMultipleArgumentsPerToken = true };
                    var diffExcept = new Option<string[]>("--exclude-columns") { Description = "Leave these columns out of the comparison", AllowMultipleArgumentsPerToken = true };
                    var diffValues = new Option<bool>("--show-values") { Description = "Read and show values: the smallest and largest of each column and sample rows of each difference (without it only counts are read)" };
                    var diffLimit = new Option<int>("--limit") { Description = "With --show-values, how many sample rows of each kind of difference", DefaultValueFactory = _ => 10 };
                    cmd.Arguments.Add(diffTarget); cmd.Arguments.Add(diffTable); cmd.Options.Add(diffProject); cmd.Options.Add(diffAgainst); cmd.Options.Add(diffAgainstSchema); cmd.Options.Add(diffAgainstConnection); cmd.Options.Add(diffKey);
                    cmd.Options.Add(diffOnly); cmd.Options.Add(diffExcept); cmd.Options.Add(diffValues); cmd.Options.Add(diffLimit);
                    cmd.SetAction(pr => { var (diffConn, diffTableName) = pr.GetValue(diffTable) is { } t ? (pr.GetValue(diffTarget), t) : (null, pr.GetValue(diffTarget)); if (diffTableName == null) { error.WriteLine("The table to compare is required: `connection compare [connection] <schema_name.table_name>`."); return ExitUsage; } return Reported(pr, spec, (o, e) => DiffCommand.Run(spec, pr.GetValue(diffProject)!.FullName, diffTableName, pr.GetValue(diffAgainst), pr.GetValue(diffAgainstSchema), pr.GetValue(diffAgainstConnection), diffConn,
                        pr.GetValue(diffKey) ?? [], pr.GetValue(diffOnly) ?? [], pr.GetValue(diffExcept) ?? [], pr.GetValue(diffValues), pr.GetValue(diffLimit), o, e, environment ?? Environment.GetEnvironmentVariable)); });
                    break;
                case "project show graph":
                    var graphModels = new Argument<string[]>("models") { Description = "Selectors (default: every model): names, paths, `+model`, `model+`, `2+model`, `@model`, `kind:`, `tag:`, `property:key=value`, `connection:`, `path:`, `changed:<git ref>`, `exclude:...`", Arity = ArgumentArity.ZeroOrMore };
                    var graphProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var graphColumns = new Option<bool>("--columns") { Description = "Also show which column of which table each output column comes from" };
                    var graphColumn = new Option<string?>("--column") { Description = "Follow one column (model.column) up to the columns it comes from and down to the columns built from it" };
                    var graphDiagram = new Option<string?>("--diagram") { Description = "Print a diagram instead of the list: dot (Graphviz) or mermaid" };
                    cmd.Arguments.Add(graphModels); cmd.Options.Add(graphProject); cmd.Options.Add(graphColumns); cmd.Options.Add(graphColumn); cmd.Options.Add(graphDiagram);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => GraphCommand.Run(spec, pr.GetValue(graphProject)!.FullName, pr.GetValue(graphModels) ?? [], pr.GetValue(graphColumns), pr.GetValue(graphColumn), pr.GetValue(graphDiagram), o, e)));
                    break;
                case "project import":
                    var impTables = new Argument<string[]>("tables") { Description = "Tables or views as schema_name.table_name, with * and ? as wildcards (default: refresh the mapped models the project already has)", Arity = ArgumentArity.ZeroOrMore };
                    GuardOptionLike(impTables, spec.Name);
                    var impProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var impTarget = ConnectionArgument("Connection to read", spec.Name);
                    var impWrite = new Option<bool>("--write") { Description = "Write the new and changed mapped models under models/ (without it the command only shows the diff)" };
                    var impCheck = new Option<bool>("--check") { Description = "CI: fail if a descriptor differs from the table it describes; writes nothing" };
                    cmd.Arguments.Add(impTarget); cmd.Arguments.Add(impTables); cmd.Options.Add(impProject); cmd.Options.Add(impWrite); cmd.Options.Add(impCheck);
                    cmd.SetAction(pr => { var (impConn, impRest) = SplitConnection(pr.GetValue(impProject)!.FullName, pr.GetValue(impTarget), pr.GetValue(impTables) ?? []); return Reported(pr, spec, (o, e) => ImportCommand.Run(spec, pr.GetValue(impProject)!.FullName, impConn, impRest, pr.GetValue(impWrite), pr.GetValue(impCheck), o, e, environment ?? Environment.GetEnvironmentVariable)); });
                    break;
                case "project update":
                    var updSelectors = new Argument<string[]>("selectors") { Description = "What to update (default: every model, mapped model and connection): names, `schema.*` patterns, paths, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:key=value`, `connection:`, `path:`, `changed:<git ref>`, `exclude:<selector>`", Arity = ArgumentArity.ZeroOrMore };
                    GuardOptionLike(updSelectors, spec.Name);
                    var updProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var updOffline = new Option<bool>("--offline") { Description = "Read no connection: update only the definitions of the models from their queries (no dialect is asked and no mapped model is compared with its table)" };
                    var updCheck = new Option<bool>("--check") { Description = "CI: write nothing, show what would change (per model, mapped model and connection) and fail if anything would" };
                    var updAnswers = new Option<FileInfo?>("--answers") { Description = "Answers file for the questions of the model definitions (see schemas/answers.schema.json)" };
                    var updAccept = new Option<bool>("--accept-inferred") { Description = "Accept inferred proposals marked high certainty when updating the definitions of models" };
                    cmd.Arguments.Add(updSelectors); cmd.Options.Add(updProject); cmd.Options.Add(updOffline); cmd.Options.Add(updCheck); cmd.Options.Add(updAnswers); cmd.Options.Add(updAccept);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => ProjectUpdateCommand.Run(spec, pr.GetValue(updProject)!.FullName, pr.GetValue(updSelectors) ?? [], pr.GetValue(updOffline), pr.GetValue(updCheck), pr.GetValue(updAnswers), pr.GetValue(updAccept), o, e, environment ?? Environment.GetEnvironmentVariable)));
                    break;
                case "project tests list":
                    var tlistProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var tlistTag = new Option<string[]>("--tag") { Description = "List only tests with this tag (repeat for several: any of them)", AllowMultipleArgumentsPerToken = false, DefaultValueFactory = _ => [] };
                    var tlistKind = new Option<string?>("--kind") { Description = "List only tests of this kind: metadata (tests/metadata) or model (tests/models)" };
                    cmd.Options.Add(tlistProject); cmd.Options.Add(tlistTag); cmd.Options.Add(tlistKind);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => TestListCommand.Run(spec, pr.GetValue(tlistProject)!.FullName, pr.GetValue(tlistKind), pr.GetValue(tlistTag) ?? [], o, e)));
                    break;
                case "project tests run":
                    var testNames = new Argument<string[]>("tests") { Description = "Test names (tests/metadata/naming/x.sql is naming.x) or files (default: every test)", Arity = ArgumentArity.ZeroOrMore };
                    var testProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var testTag = new Option<string[]>("--tag") { Description = "Run only tests with this tag (repeat for several: any of them)", AllowMultipleArgumentsPerToken = true };
                    var testLimit = new Option<int>("--limit") { Description = "How many violating rows to show per test", DefaultValueFactory = _ => 10 };
                    var testKind = new Option<string?>("--kind") { Description = "Run only tests of this kind: metadata (tests/metadata) or model (tests/models)" };
                    var testStrict = new Option<bool>("--strict") { Description = "Fail on warning-severity tests too" };
                    cmd.Arguments.Add(testNames); cmd.Options.Add(testProject); cmd.Options.Add(testTag); cmd.Options.Add(testLimit); cmd.Options.Add(testKind); cmd.Options.Add(testStrict);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => TestCommand.Run(spec, pr.GetValue(testProject)!.FullName, pr.GetValue(testNames) ?? [], pr.GetValue(testTag) ?? [], pr.GetValue(testKind), pr.GetValue(testLimit), pr.GetValue(testStrict), o, e)));
                    break;
                case "project show metadata":
                    var metaModels = new Argument<string[]>("models") { Description = "Model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:key=value`, `changed:<git ref>`, `exclude:...` (default: every model)", Arity = ArgumentArity.ZeroOrMore };
                    var metaProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    cmd.Arguments.Add(metaModels); cmd.Options.Add(metaProject);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => MetadataCommand.Run(spec, pr.GetValue(metaProject)!.FullName, pr.GetValue(metaModels) ?? [], o, e)));
                    break;
                case "connection publish":
                    var pubModels = new Argument<string[]>("models") { Description = "Model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `tag:`, `property:key=value`, `changed:<git ref>`, `exclude:...` (default: every model)", Arity = ArgumentArity.ZeroOrMore };
                    GuardOptionLike(pubModels, spec.Name);
                    var pubProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var pubTarget = ConnectionArgument("Connection", spec.Name);
                    cmd.Arguments.Add(pubTarget); cmd.Arguments.Add(pubModels); cmd.Options.Add(pubProject);
                    cmd.SetAction(pr => { var (pubConn, pubRest) = SplitConnection(pr.GetValue(pubProject)!.FullName, pr.GetValue(pubTarget), pr.GetValue(pubModels) ?? []); return Reported(pr, spec, (o, e) => PublishMetadataCommand.Run(spec, pr.GetValue(pubProject)!.FullName, pubConn, pubRest, o, e, environment ?? Environment.GetEnvironmentVariable)); });
                    break;
                case "project show refresh":
                    var showRefreshPlan = new Argument<string?>("plan") { Description = "A refresh plan by name; omit to list the plans of every connection", Arity = ArgumentArity.ZeroOrOne };
                    var showRefreshProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var showRefreshConnection = new Option<string?>("--connection") { Description = "Only the plans of this connection" };
                    var showRefreshResolve = new Option<bool>("--resolve") { Description = "Evaluate the `expression` resolvers of the plan's loads now, offline (what `start` would be at this moment, in the project's time zone); a `target` or `source` resolver reads a database and is not run here, `connection refresh --dry-run` runs it. Needs a plan" };
                    cmd.Arguments.Add(showRefreshPlan); cmd.Options.Add(showRefreshProject); cmd.Options.Add(showRefreshConnection); cmd.Options.Add(showRefreshResolve);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => RefreshViews.Show(spec, pr.GetValue(showRefreshProject)!.FullName, pr.GetValue(showRefreshPlan), pr.GetValue(showRefreshConnection), o, e, pr.GetValue(showRefreshResolve))));
                    break;
                case "project show plan":
                    var reviewPlan = new Argument<string?>("plan") { Description = "A plan file (plans/<connection>/<id>.plan.yml); omit to list the project's plans", Arity = ArgumentArity.ZeroOrOne };
                    var reviewProject = new Option<DirectoryInfo>("--project") { Description = "Project root", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var reviewTarget = new Option<string?>("--connection") { Description = "When listing, only the plans of this connection" };
                    cmd.Arguments.Add(reviewPlan); cmd.Options.Add(reviewProject); cmd.Options.Add(reviewTarget);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => ReviewCommand.Run(spec, pr.GetValue(reviewProject)!.FullName, pr.GetValue(reviewPlan), pr.GetValue(reviewTarget), o, e)));
                    break;
                case "help matrix":
                    var matrixRewrites = new Option<bool>("--rewrites") { Description = "List the rewrites that make the engines give DuckDB's answers (what each does, where it is required, and what the engine does without it); `rewrites:` in dbdatabuild.yml or a model turns the optional ones off" };
                    cmd.Options.Add(matrixRewrites);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => PrintMatrix(spec, pr.GetValue(matrixRewrites), o, e)));
                    break;
                case "help dialects":
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => HelpDialects.Run(spec, o)));
                    break;
                case "help code":
                    var code = new Argument<string>("code") { Description = "Diagnostic code, e.g. DDB-214" };
                    cmd.Arguments.Add(code);
                    cmd.SetAction(pr => Reported(pr, spec, (o, e) => Explain(spec, pr.GetValue(code)!, o, e)));
                    break;
                default:
                    cmd.SetAction(_ =>
                    {
                        WriteHeader(spec, output);
                        error.WriteLine($"`{ProductInfo.Cli} {spec.Name}` is not implemented yet. Nothing was run.");
                        return ExitNotImplemented;
                    });
                    break;
            }
            Attach(root, spec.Path, cmd);
        }
        return root;
    }

    /// <summary>
    /// A positional value that looks like an option is a mistake, and the parser would otherwise take it for a value (an argument that accepts anything accepts `--connection`, and the command would run on the default
    /// connection with `--connection` as a model selector). Refused with the way it is written now.
    /// </summary>
    private static T GuardOptionLike<T>(T argument, string command) where T : Argument
    {
        argument.Validators.Add(result =>
        {
            foreach (var token in result.Tokens.Select(t => t.Value).Where(v => v.StartsWith('-') && v.Length > 1 && !char.IsDigit(v[1])))
                result.AddError(token == "--connection"
                    ? $"`--connection` is not an option of `{ProductInfo.Cli} {command}`: the connection is the first argument after the command, for example `{ProductInfo.Cli} {command} sqlserver`."
                    : $"`{token}` is not an option of `{ProductInfo.Cli} {command}`.");
        });
        return argument;
    }

    /// <summary>
    /// The connection a command works on, as its first positional argument (`connection deploy <c> [models...]`). Left out, it is the project's only default connection (<see cref="CommandTargets.Resolve"/>). The old `--connection`
    /// option is gone, with no alias.
    /// </summary>
    private static Argument<string?> ConnectionArgument(string what, string command) =>
        GuardOptionLike(new Argument<string?>("connection") { Description = $"{what} (default: the project's only default connection)", Arity = ArgumentArity.ZeroOrOne }, command);

    /// <summary>
    /// For a command that takes models, tables or patterns after the connection: the first positional is the connection when it is a connection of the project, and otherwise the first of those (so
    /// `connection status marts.orders` works in a project with one connection). A name that is both a connection and a directory of models is the connection. A command with nothing after the connection never
    /// calls this: whatever is first is the connection, and an unknown one is reported as such.
    /// </summary>
    internal static (string? Connection, string[] Remaining) SplitConnection(string projectRoot, string? first, string[] rest)
    {
        if (first == null) return (null, rest);
        return CommandTargets.NamesOf(projectRoot).Contains(first, StringComparer.Ordinal) ? (first, rest) : ((string?)null, [first, .. rest]);
    }

    /// <summary>Puts a command under its groups (`project`, `show`), creating each group the first time it is needed.</summary>
    private static void Attach(Command root, string[] path, Command leaf)
    {
        var parent = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var name = path[i];
            var group = parent.Subcommands.FirstOrDefault(c => c.Name == name);
            if (group == null)
            {
                var full = string.Join(' ', path.Take(i + 1));
                group = new Command(name, CommandGroups.Descriptions.GetValueOrDefault(full, full));
                parent.Subcommands.Add(group);
            }
            parent = group;
        }
        parent.Subcommands.Add(leaf);
    }

    /// <summary>The command at a path (`connection deploy`), or null.</summary>
    public static Command? Find(Command root, string name)
    {
        var at = root;
        foreach (var word in name.Split(' '))
        {
            at = at.Subcommands.FirstOrDefault(c => c.Name == word);
            if (at == null) return null;
        }
        return at;
    }

    private static void WriteHeader(CommandSpec spec, TextWriter output) =>
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");

    /// <summary>`project compile`: validate the project and write the compiled files; with --check compare them instead; with --content only show them (that is what a read-only client asks for, and it wins over --check).</summary>
    private static int Compile(CommandSpec spec, string projectRoot, string[] models, string[] connections, bool check, bool content, TextWriter output, TextWriter error)
    {
        if (content) return RenderCommand.Render(spec, projectRoot, models, connections, write: false, check: false, content: true, output, error);
        // a project that does not validate is not compiled: scripts for a construct the engine cannot run, or a collation that cannot hold the string profile, would only look finished
        var validated = Validate(spec, projectRoot, output, error);
        if (validated != ExitOk)
        {
            output.WriteLine(check ? "Rendered files were not compared: the project has errors." : "Nothing was written: the project has errors.");
            return validated;
        }
        var rendered = RenderCommand.Render(spec, projectRoot, models, connections, write: !check, check: check, content: false, output, error, header: false);
        if (rendered == ExitOk && !check) output.Next("project tests run", "connection status");
        return rendered;
    }

    private static int Validate(CommandSpec spec, string projectRoot, TextWriter output, TextWriter error)
    {
        WriteHeader(spec, output);
        var result = ProjectValidator.Validate(projectRoot);
        var diagnostics = new List<Diagnostic>(result.Diagnostics);
        var config = ProjectConfigLoader.LoadFromProject(projectRoot, diagnostics);

        // The effective settings are never hidden (DESIGN.md 7.4): printed even when they are the built-in defaults.
        var configured = File.Exists(Path.Combine(projectRoot, ProductInfo.ConfigFile)) && !diagnostics.Any(d => d.Severity == Severity.Error && d.Location.File == ProductInfo.ConfigFile);
        output.WriteLine($"Config: {(configured ? ProductInfo.ConfigFile : "built-in defaults")}");
        output.WriteLine($"Effective: {config.Describe()}");
        // inheritance is never hidden either: what each model took from a project file above it, and from where
        foreach (var s in result.BuiltModels.Where(s => s.Inherited.Count > 0).OrderBy(s => s.Definition.Name, StringComparer.Ordinal))
            output.WriteLine($"Inherited by {s.Definition.Name}: {string.Join(", ", s.Inherited.Select(o => $"{o.Path} = {o.Value} ({o.File}:{o.Line})"))}");
        diagnostics.AddRange(ProjectChecks.Run(result.BuiltModels, config, null, projectRoot, new ModelLowering(result.Models, result.DeclaredModelsAndStaging, config, result.Macros)));
        if (!diagnostics.Any(d => d.Severity == Severity.Error)) diagnostics.AddRange(ProjectChecks.Reachability(ProjectContext.Load(projectRoot), null));

        diagnostics = diagnostics.DistinctBy(d => (d.Code, d.Location, d.Found)).ToList();      // one line per finding, however many checks reached it
        foreach (var d in diagnostics) error.Diag(d);
        var errors = diagnostics.Count(d => d.Severity == Severity.Error);
        var warnings = diagnostics.Count(d => d.Severity == Severity.Warning);
        var notes = diagnostics.Count(d => d.Severity == Severity.Note);
        var tail = $"{warnings} warning(s), {notes} note(s).";
        var allModels = ProjectModels.Of(result, config.DefaultConnections);
        output.Payload("counts", new { models = allModels.Count, by_kind = ModelKinds.Every.ToDictionary(k => k, k => allModels.Count(m => m.Kind == k)), errors, warnings, notes });
        output.Payload("config", new { file = configured ? ProductInfo.ConfigFile : null, effective = config.Describe() });
        if (output.IsJson() && errors == 0)
        {
            var ctx = ProjectContext.Load(projectRoot);
            output.Payload("project", MetadataBuilder.Project(ctx));
            output.Payload("models", MetadataBuilder.Models(ctx).Select(m => m.Document).ToList());
        }
        output.WriteLine(errors == 0
            ? $"OK: {allModels.Count} model(s) valid ({MetadataCommand.KindCounts(allModels)}). {tail}"
            : $"FAILED: {errors} error(s), {tail}");
        return errors == 0 ? ExitOk : ExitFindings;
    }

    private static int PrintMatrix(CommandSpec spec, bool rewrites, TextWriter output, TextWriter error)
    {
        WriteHeader(spec, output);
        var diags = new List<Diagnostic>();
        var matrix = MatrixLoader.LoadEmbedded(diags);
        if (diags.Count > 0)
        {
            foreach (var d in diags) error.Diag(d);
            return ExitFindings;
        }
        output.Payload("rewrites", RewriteCatalog.All.Select(r => new { name = r.Name, layer = r.Layer.ToString().ToLowerInvariant(), engines = r.Targets, required_on = r.RequiredOn, exact = r.Exact, native = r.Native }).ToList());
        if (rewrites)
        {
            output.WriteLine($"{"rewrite",-24} {"layer",-9} {"required on",-24} what it does");
            foreach (var r in RewriteCatalog.All)
            {
                output.WriteLine($"{r.Name,-24} {r.Layer.ToString().ToLowerInvariant(),-9} {(r.RequiredOn.Count == 0 ? "optional" : string.Join(", ", r.RequiredOn)),-24} {r.Exact}");
                output.WriteLine($"{"",-24} {"",-9} {"",-24} without it: {r.Native}");
            }
            output.Payload("version", MatrixLoader.EmbeddedVersion());
            output.Payload("constructs", Array.Empty<object>());
            output.Payload("covered_entries", matrix.Covered.Count);
            output.WriteLine($"\n{RewriteCatalog.All.Count} rewrite(s). Turn the optional ones off with `rewrites: {{ fidelity: native }}` or `rewrites: {{ disable: [name] }}` in dbdatabuild.yml or in a model.");
            return ExitOk;
        }
        output.WriteLine($"{"construct",-26} {"sqlserver",-13} {"fabric",-13} {"postgres",-13}");
        foreach (var row in matrix.Rows)
        {
            string Cell(string t) => row.Targets[t].Status.ToString().ToLowerInvariant() + (row.Targets[t].MinVersion is { } v ? $" (>= {v})" : "");
            output.WriteLine($"{row.Id,-26} {Cell("sqlserver"),-13} {Cell("fabric"),-13} {Cell("postgres"),-13}");
            foreach (var t in SupportMatrix.Targets.Where(t => row.Targets[t].Note is { Length: > 0 }))
                output.WriteLine($"    {t}: {row.Targets[t].Note}");
        }
        output.Payload("version", MatrixLoader.EmbeddedVersion());
        output.Payload("constructs", matrix.Rows.Select(r => new
        {
            id = r.Id,
            engines = r.Targets.ToDictionary(t => t.Key, t => new { status = t.Value.Status.ToString().ToLowerInvariant(), min_version = t.Value.MinVersion, note = t.Value.Note }),
        }).ToList());
        output.Payload("covered_entries", matrix.Covered.Count);
        output.WriteLine($"\n{matrix.Rows.Count} construct row(s); {matrix.Covered.Count} verified node/function/type entries (anything else is reported as DDB-305).");
        return ExitOk;
    }

    private static int Explain(CommandSpec spec, string code, TextWriter output, TextWriter error)
    {
        var d = DiagnosticCatalog.Find(code);
        if (d == null)
        {
            error.WriteLine($"Unknown diagnostic code `{code}`. Known codes: {string.Join(", ", DiagnosticCatalog.All.Select(x => x.Code))}.");
            return ExitUsage;
        }
        output.Payload("code", new { code = d.Code, title = d.Title, default_severity = d.DefaultSeverity.ToString().ToLowerInvariant(), supported = d.Supported, fix = d.Fix, explanation = d.Explanation });
        output.Write(DiagnosticFormatter.Explain(d));
        return ExitOk;
    }
}
