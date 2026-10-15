using DbDataBuild.Core;

namespace DbDataBuild.Cli;

/// <summary>Effect classes (DESIGN.md section 9.1). Printed in --help and in each run header.</summary>
public enum EffectClass
{
    OfflineOnly,
    RepoFilesOnly,
    TargetReadOnly,
    TargetWrites,
    TargetDataWrites,
    TrackingTablesOnly,
}

public static class EffectClasses
{
    public static string Describe(this EffectClass e) => e switch
    {
        EffectClass.OfflineOnly => "Offline only",
        EffectClass.RepoFilesOnly => "Repo files only (no target connection)",
        EffectClass.TargetReadOnly => "Target read-only",
        EffectClass.TargetWrites => "Target writes (DDL and/or data, as the plan states)",
        EffectClass.TargetDataWrites => "Target writes (data only)",
        EffectClass.TrackingTablesOnly => "Tracking tables only",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };
}

/// <summary>
/// One command. <paramref name="Name"/> is its whole path (`connection deploy`); <paramref name="Lane"/> is where it belongs in the life of a deployment (`inspect`, `deploy`, `refresh`, or empty for authoring and reference);
/// <paramref name="Disposition"/> says what it reads and writes in one code (see docs/concepts.md): P is the project, C a connection, T its tracking tables, S its structure, D its data, `→` reads, `⇒` writes.
/// </summary>
public sealed record CommandSpec(string Name, EffectClass Effect, string Purpose, bool Implemented, string Lane = "", string Disposition = "")
{
    /// <summary>The words of the path: `connection deploy` is [connection, deploy].</summary>
    public string[] Path => Name.Split(' ');

    /// <summary>What a header says after the effect: the reads-and-writes code and the lane (`reads/writes: P⇒S  |  lane: deploy`).</summary>
    public string Marks => $"reads/writes: {Disposition}" + (Lane.Length > 0 ? $"  |  lane: {Lane}" : "");

    /// <summary>The name as one token, for file names, tool names and schema keys (`connection_deploy`).</summary>
    public string Id => Name.Replace(' ', '_').Replace('-', '_');
}

/// <summary>The groups the commands sit under, with what each is for.</summary>
public static class CommandGroups
{
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["project"] = "Work on the project: its files, never a database",
        ["project model"] = "Create and update models",
        ["project show"] = "Read-only views of the project",
        ["project tests"] = "The project's tests",
        ["connection"] = "Work on a database the project is built on",
        ["ui"] = "Interfaces: the terminal screens, the web page, the server for AI agents",
        ["help"] = "Reference: explain a diagnostic code, the support matrix, the dialects of read-only origins",
    };
}

/// <summary>The full command surface as data. A test asserts every command declares an effect class.</summary>
public static class CommandSpecs
{
    public static readonly IReadOnlyList<CommandSpec> All =
    [
        new("project create", EffectClass.RepoFilesOnly, "List the project templates built in, or create a ready-to-run project from one", true, "", "P⇒P"),
        new("project model create", EffectClass.RepoFilesOnly, "Create a model to start from: a definition and a placeholder query that runs, in the project's layout (a view, a full table, or an incremental one)", true, "", "P⇒P"),
        new("project model update", EffectClass.TargetReadOnly, "Generate or update model definition files from the models' queries, and the mapped models (and the tables copies read) from their tables on the connections they are on; --offline reads no connection, --check writes nothing", true, "", "C→P"),
        new("project compile", EffectClass.RepoFilesOnly, "Validate the project and write what is compiled from it (rendered/): the lowered queries and the load scripts per connection", true, "", "P⇒P"),
        new("project tests run", EffectClass.OfflineOnly, "Run the project's tests: metadata rules (DuckDB SQL over the metadata views) in tests/metadata/ and model tests (given rows, expected rows) in tests/models/", true, "", "P→"),
        new("project tests list", EffectClass.OfflineOnly, "List the project's tests with their kind, severity and tags (nothing is run)", true, "", "P→"),
        new("project sample", EffectClass.OfflineOnly, "Run models on generated or supplied sample data, offline", true, "", "P→"),
        new("project seed", EffectClass.RepoFilesOnly, "Run the seeds (DuckDB queries that generate the rows of the mapped models) into a DuckDB file", true, "", "P⇒P"),
        new("project import", EffectClass.TargetReadOnly, "Export tables and views from a connection as mapped models (models/), so models over them bind offline (writes files only with --write)", true, "", "C→P"),
        new("project update", EffectClass.TargetReadOnly, "Bring in everything that comes from connections: the definitions of the models, the metadata of the mapped models and of the tables copies read (columns, types, keys, indexes), and the dialect each Flight SQL server reports, committed in dialects.lock.yml; --offline reads no connection, --check writes nothing", true, "", "C→P"),
        new("project show loads", EffectClass.OfflineOnly, "Print the model x connection x operation pairing table", true, "", "P→"),
        new("project show graph", EffectClass.OfflineOnly, "Show the dependency graph (which table each model reads), column lineage, or a diagram of it; selectors pick the part to show", true, "", "P→"),
        new("project show metadata", EffectClass.OfflineOnly, "Print everything the tool knows about the project and its models (use --format json)", true, "", "P→"),
        new("project show refresh", EffectClass.OfflineOnly, "Read the compiled refresh plans (offline): the plans of each connection, or one plan with its members in the order they run, the inputs each load takes and where each default comes from, what each load waits for (the dependency graph the loads file carries, with model kinds and depth), what was left out and the hashes", true, "", "P→"),
        new("project show plan", EffectClass.OfflineOnly, "Read plan files (offline, nothing applied): list a project's plans, or show one with its steps, risk, exact statements, report and what applying it would need to be allowed", true, "", "P→"),
        new("project agent-kit", EffectClass.RepoFilesOnly, "Install the skill and JSON Schemas an AI coding agent needs to work in a project (lists them unless --write)", true, "", "P⇒P"),
        new("connection inspect", EffectClass.TargetReadOnly, "Can the tool use this connection, and if not, why: whether each login is set, connects and as whom, what it may do, whether the tracking tables are ready, which schema names the models use exist", true, "inspect", "C→"),
        new("connection init", EffectClass.TrackingTablesOnly, "Create the tracking tables and their schema name (prints the script; --apply runs it)", true, "", "P⇒T"),
        new("connection status", EffectClass.TargetReadOnly, "Where a connection stands: drift, blocks, what a deploy would do", true, "inspect", "C→"),
        new("connection deploy", EffectClass.TargetWrites, "Change a connection's structure to match the models: plan with questions, show the plan, apply it (--write-plan stops after writing the plan; --apply-plan applies a written one; --ack records a decision)", true, "deploy", "P⇒S"),
        new("connection refresh", EffectClass.TargetDataWrites, "Run the loads of a refresh plan that project compile wrote (the plan default unless one is named, all of it or the members the selectors choose), or one load named model.load, as an event of its own: it checks what refresh.check says first, never changes structure, and leaves what a refresh cannot run to a deploy", true, "refresh", "P⇒D"),
        new("connection monitor", EffectClass.TargetReadOnly, "The events (deploys and refreshes), DDL and load history, recorded shapes, and what needs attention; with a refresh plan named, that plan's runs and each member's last success and failure", true, "inspect", "C→"),
        new("connection compare", EffectClass.TargetReadOnly, "Compare the data of two tables, of one connection or across connections and engines (by digests): schemas, row counts and a key-based row diff (values are read only with --show-values)", true, "inspect", "C→"),
        new("connection seed", EffectClass.TargetWrites, "Create the seeded tables (the mapped models) on a connection and fill them from the seeds (prints what it would do unless --apply)", true, "", "P⇒S"),
        new("connection publish", EffectClass.TrackingTablesOnly, "Store the project and model metadata as JSON in the connection for introspection", true, "", "P⇒T"),
        new("ui terminal", EffectClass.OfflineOnly, "Interactive terminal interface: choose, plan and run operations (each action it runs declares its own effect)", true, "", "—"),
        new("ui web", EffectClass.OfflineOnly, "Web interface on this machine's loopback address: project health, lineage, models and their rendered scripts, tests, the support matrix (it runs only commands that read, unless started with --allow-apply)", true, "", "—"),
        new("ui mcp", EffectClass.OfflineOnly, "Model Context Protocol server on standard input and output: the commands as tools for an AI agent, the agent kit as resources (each tool it offers declares its own effect; commands that change a connection are offered only with --allow-writes)", true, "", "—"),
        new("help code", EffectClass.OfflineOnly, "Long-form explanation of a diagnostic code", true, "", "—"),
        new("help matrix", EffectClass.OfflineOnly, "Print the support matrix and portability report", true, "", "—"),
        new("help dialects", EffectClass.OfflineOnly, "What a dialect profile is: its fields, the built-in dialects, how to add one for a system behind odbc or flightsql (with an example)", true, "", "—"),
    ];
}
