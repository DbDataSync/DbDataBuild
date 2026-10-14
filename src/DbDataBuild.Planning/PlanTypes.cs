using DbDataBuild.Core.Questions;
using DbDataBuild.State;

namespace DbDataBuild.Planning;

/// <summary>`track` is a tracking-table write (recording an adopted or acknowledged shape); the other types are those of DESIGN.md 10.2.</summary>
public enum StepType { Ddl, Load, Backfill, Hook, Track, Transfer }

public enum RiskClass { Safe, Risky, Destructive }

/// <param name="Source">runtime (a person supplied it) or resolver (read from the target).</param>
/// <param name="Value">Invariant-culture text of the value in the parameter's logical type, or null for SQL NULL.</param>
/// <param name="Planned">Only while a plan is applied: the value the plan was made with, when apply bound another (a moved input the deployer approved, or a value given with `--input` at apply). Recorded in `run_log`; never written to a plan file.</param>
public sealed record PlanParameter(string Name, string Type, string Source, string? Value, string? Planned = null);

/// <summary>An `--input [model[.load].]name=value`: the value replaces the input's resolver, which is then not run (and a value that is not computed cannot move between a plan and its apply).</summary>
public sealed record InputPin(string? Target, string Name, string Value, string Text)
{
    /// <summary>Reads `[model[.load].]name=value`; null (with a message) when it is not that.</summary>
    public static InputPin? Parse(string text, out string? problem)
    {
        problem = null;
        var eq = text.IndexOf('=');
        if (eq <= 0 || eq == text.Length - 1) { problem = $"--input is `[model.]name=value`, not `{text}`."; return null; }
        var key = text[..eq]; var dot = key.LastIndexOf('.');
        var (target, name) = dot < 0 ? ((string?)null, key) : (key[..dot], key[(dot + 1)..]);
        if (name.Length == 0 || (target != null && target.Length == 0)) { problem = $"--input is `[model.]name=value`, not `{text}`."; return null; }
        return new InputPin(target, name, text[(eq + 1)..], text);
    }

    /// <summary>Whether the pin is meant for the load of this model (`model`, or `model.load`; the model's own name may have dots).</summary>
    public bool Matches(string model, string load) => Target == null || Target == model || Target == $"{model}.{load}";
}

/// <summary>A computed input whose value at apply is not the value in the plan.</summary>
/// <param name="Planned">The value in the plan (null: NULL).</param>
/// <param name="Now">The value computed again when the plan is applied.</param>
public sealed record MovedInput(string StepId, string Model, string Operation, string Name, string? Planned, string? Now)
{
    /// <summary>The override as it would be typed to say "yes, with this value": appended to the same command.</summary>
    public string Override => Quote($"--input {Model}.{Operation}.{Name}={Now}");

    private static string Quote(string arg) { var space = arg.IndexOf(' ', "--input ".Length); return space < 0 ? arg : $"--input \"{arg["--input ".Length..].Replace("\"", "\\\"")}\""; }
}

/// <summary>
/// One input of a planned load: how it is defined (the compiled input, with the resolver's text inline so the plan is self-contained), and the value this plan was made with, its source
/// (`flag`, `default`, `parameter`, `resolver:expression`, `resolver:target`, `resolver:source`, `initial`) and the hash of the resolver that found it.
/// </summary>
/// <param name="Origin">For a `source` resolver of a copy: the origin it ran at.</param>
public sealed record PlannedInput(RefreshParameter Def, string Source, string? Value, string? ResolverHash, string? Origin = null)
{
    public string Name => Def.Name;
    public string Type => Def.Type;

    /// <summary>Whether the value is computed (a resolver found it), which is what can move between a plan and its apply.</summary>
    public bool IsComputed => Source.StartsWith("resolver:", StringComparison.Ordinal) || Source == "initial";
}

/// <summary>What resolving the inputs of one load at plan time came to: the inputs, or why the load cannot be planned (<see cref="Failure"/>), or that an input found NULL and says `skip_load`.</summary>
public sealed record PlannedLoadInputs(IReadOnlyList<PlannedInput> Inputs, string? Failure, bool SkipLoad);

/// <summary>The rows a `transfer` step moves (<see cref="Command"/>: the read is a native command, a call that returns rows, not a SELECT; apply runs it in a transaction it rolls back, and matches its result columns to the declared ones by name): read on <see cref="Origin"/> with <see cref="ReadText"/> (a single SELECT, hashed like any statement), converted by the declared types of <see cref="Columns"/>, and written to <see cref="Staging"/> on the plan's connection.</summary>
/// <param name="Staging">`schema_name.table_name` of the staging table; the step's text creates it.</param>
/// <param name="Dialect">For a Flight SQL origin: the name of the dialect the read was written in, and <paramref name="DialectHash"/> the hash of its profile (`dialects.lock.yml` or the connection's `engine`), so a refresh can tell that the server now reports another. Null for the other drivers.</param>
/// <param name="ParameterOrder">For an origin that takes its parameters by position (`?`, `$1`): the names of the values in the order they are bound, a name used twice in the text appearing twice for `?`. Null when the read binds by name.</param>
public sealed record TransferSpec(string Origin, string ReadText, string Staging, IReadOnlyList<PlanColumn> Columns, PlanSlice? Slice = null, PlanWatermark? Watermark = null, IReadOnlyList<PlanParameter>? Parameters = null, bool Command = false, IReadOnlyList<string>? ParameterOrder = null, string? Dialect = null, string? DialectHash = null);

/// <summary>The lower bound an incremental copy reads from: rows whose <see cref="Column"/> is at or after <see cref="Value"/> (invariant text of the column's <see cref="Type"/>), bound to `@watermark` in the read.</summary>
public sealed record PlanWatermark(string Column, string Type, string Value);

/// <summary>How a transfer keeps its origin's rows apart: the rows carry <see cref="Value"/> in <see cref="Column"/>. When <see cref="Added"/> the origin has no such column and apply writes the value into every row; otherwise every row the origin returns must already hold it.</summary>
public sealed record PlanSlice(string Column, string Value, bool Added);

/// <summary>A column of a transfer: its name and declared logical type.</summary>
public sealed record PlanColumn(string Name, string Type);

/// <summary>One thing apply will do. <see cref="Text"/> is the full statement or script, executed exactly as written.</summary>
/// <param name="Reasons">The reason chain: a decision-table row, a model change, an answer.</param>
/// <param name="HashAfter">For DDL on tables: the shape hash the object must have afterwards. Null when it cannot be predicted (views).</param>
/// <param name="ResolverText">The committed resolver of a load, so apply can re-run it and refuse a stale value.</param>
/// <param name="ResolverResult">What the resolver returned at plan time (null for SQL NULL). Compared again at apply.</param>
/// <param name="FileHash">Hash of the committed rendered file a load step runs.</param>
/// <param name="Expect">For an index step: what the catalog must report afterwards, `index:&lt;name&gt;=&lt;canonical definition&gt;` (index steps leave the shape hash alone, so this is their post-check).</param>
/// <param name="Hook">For a hook step: the hook's name (`group.name` for a group member). The event is in <see cref="Operation"/>.</param>
/// <param name="Effect">For a hook step: `ddl` or `data`, which decides which command may run it.</param>
/// <param name="Transfer">For a transfer step: where the rows come from and which columns they are. <see cref="Text"/> is the DDL that (re)creates the staging table they are written to.</param>
/// <param name="DefinitionHash">For a load: the hash of the model query it was planned from, recorded in `run_log` so a later plan can see the query changed.</param>
/// <param name="Inputs">For a load whose model declares `inputs:`: every input with the value the plan was made with and where it came from, and what is needed to compute it again at apply (a clock-based input moves, and apply says so).</param>
public sealed record PlanStep(
    string Id, StepType Type, string Object, string Description, string Text, RiskClass Risk, IReadOnlyList<string> Reasons,
    string? HashAfter, IReadOnlyList<PlanParameter> Parameters, string? ResolverText = null, string? ResolverResult = null, bool HasResolver = false,
    string? FileHash = null, string? Operation = null, string? ShapeSource = null, string? DefinitionHash = null, string? Expect = null, string? Hook = null, string? Effect = null, TransferSpec? Transfer = null,
    IReadOnlyList<PlannedInput>? Inputs = null);

/// <summary>What the plan assumed about an object, verified against the live target at apply (a mismatch is a stale plan).</summary>
public sealed record ObjectBase(string Object, ObjectState State, string? LiveShapeHash, string? RecordedShapeHash);

public sealed record Plan(
    string Id, string Connection, string? GitCommit, bool GitDirty, string ToolVersion,
    IReadOnlyList<ObjectBase> Bases, IReadOnlyList<ResolvedAnswer> Answers, IReadOnlyList<PlanStep> Steps, IReadOnlyList<string> Noticed, string? ProjectHash = null);
