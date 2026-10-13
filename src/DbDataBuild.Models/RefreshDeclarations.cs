using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>What a refresh plan does when one of its loads fails (docs/research/refresh-plans.md, section 6). Stored in the compiled plan file and acting in a run (step 2).</summary>
public static class OnFailureModes
{
    public const string Stop = "stop";
    public const string Continue = "continue";
    public const string Ignore = "ignore";
    public static readonly IReadOnlyList<string> All = [Stop, Continue, Ignore];
}

/// <summary>What a run of a plan does with a load that another run holds (section 6). Stored in the plan file and acting in a run (step 2).</summary>
public static class OnBusyModes
{
    public const string Skip = "skip";
    public const string Wait = "wait";
    public const string Fail = "fail";
    public static readonly IReadOnlyList<string> All = [Skip, Wait, Fail];
}

/// <summary>A span such as `30s`, `15m`, `1h`, `2d` or `1w`, as a plan says how often it is meant to run (`expected_every`), how long a load may run (`max_runtime`) or how long to wait for a busy load (`max_wait`). Kept as written; the seconds are what compares.</summary>
public sealed record EveryDuration(string Text, long Seconds)
{
    private static readonly Regex Form = new(@"^([1-9][0-9]{0,5})(s|m|h|d|w)$", RegexOptions.CultureInvariant);

    public long Minutes => Seconds / 60;

    public TimeSpan Span => TimeSpan.FromSeconds(Seconds);

    public static EveryDuration? TryParse(string text)
    {
        var m = Form.Match(text.Trim());
        if (!m.Success) return null;
        var n = long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return new EveryDuration(m.Value, m.Groups[2].Value switch { "s" => n, "m" => n * 60, "h" => n * 3600, "d" => n * 86400, _ => n * 604800 });
    }
}

/// <summary>
/// One refresh plan as the project declares it (`refresh.plans.<name>` in dbdatabuild.yml): a name, the words about it, and the rules for running it. Everything here is stored in the compiled plan file. `check`, `on_fail`, `on_failure`, `on_busy`, `max_wait`,
/// `max_runtime` and `expected_every` act (steps 1 and 2); `parallel` is validated and kept.
/// </summary>
/// <param name="Check">Overrides the connection's and the project's `refresh.check` for this plan.</param>
/// <param name="OnFail">Overrides `refresh.on_fail` for this plan.</param>
/// <param name="MaxWait">With `on_busy: wait`, how long to wait for a busy load (none: until it is free). Past it the load fails.</param>
/// <param name="MaxRuntime">Opt-in: how long one load of the plan may run before its statement is cancelled and the load fails (a load's own `max_runtime` wins).</param>
public sealed record RefreshPlanSettings(string Name, ObjectInfo Info, string? OnFailure, string? OnBusy, EveryDuration? ExpectedEvery, int? Parallel, RefreshCheck? Check, OnFail? OnFail, int Line = 0, EveryDuration? MaxWait = null, EveryDuration? MaxRuntime = null)
{
    /// <summary>The plan every model with a default load and no `refresh:` is in. It exists whether or not the project declares it.</summary>
    public const string DefaultName = "default";

    public static readonly Regex NameForm = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
}

/// <summary>
/// One place where a load of a model is in a refresh plan (`refresh:` in the model, or in a folder or the project beneath `defaults:`). <see cref="Name"/> is what the membership is called inside the plan: the load's name
/// unless `as:` says another, so one load can be in a plan twice with different inputs. <see cref="Inputs"/> are values for the load's inputs for this plan, as the text written.
/// </summary>
/// <param name="OnFailure">Overrides the plan's `on_failure` for this membership.</param>
public sealed record RefreshMembership(string Plan, string Load, string Name, IReadOnlyList<KeyValuePair<string, string>> Inputs, string? OnFailure, int Line = 0, string? File = null)
{
    /// <summary>The membership as a person wrote it, for messages.</summary>
    public string Describe() => Name == Load ? Load : $"{Load} as {Name}";
}
