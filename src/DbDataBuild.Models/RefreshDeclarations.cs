using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>What a refresh plan does when one of its loads fails (docs/research/refresh-plans.md, section 6). Stored in the compiled plan file; the rules act in a later step.</summary>
public static class OnFailureModes
{
    public const string Stop = "stop";
    public const string Continue = "continue";
    public const string Ignore = "ignore";
    public static readonly IReadOnlyList<string> All = [Stop, Continue, Ignore];
}

/// <summary>What a run of a plan does with a load that another run holds (section 6). Stored in the plan file; it acts in a later step.</summary>
public static class OnBusyModes
{
    public const string Skip = "skip";
    public const string Wait = "wait";
    public const string Fail = "fail";
    public static readonly IReadOnlyList<string> All = [Skip, Wait, Fail];
}

/// <summary>A span such as `15m`, `1h`, `2d` or `1w`, as a plan says how often it is meant to run (`expected_every`). Kept as written; the minutes are what compares.</summary>
public sealed record EveryDuration(string Text, long Minutes)
{
    private static readonly Regex Form = new(@"^([1-9][0-9]{0,5})(m|h|d|w)$", RegexOptions.CultureInvariant);

    public static EveryDuration? TryParse(string text)
    {
        var m = Form.Match(text.Trim());
        if (!m.Success) return null;
        var n = long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return new EveryDuration(m.Value, m.Groups[2].Value switch { "m" => n, "h" => n * 60, "d" => n * 1440, _ => n * 10080 });
    }
}

/// <summary>
/// One refresh plan as the project declares it (`refresh.plans.<name>` in dbdatabuild.yml): a name, the words about it, and the rules for running it. Everything here is stored in the compiled plan file; in the first
/// step of the build only the check and its failure rule act, the others (`on_failure`, `on_busy`, `expected_every`, `parallel`) are validated and kept.
/// </summary>
/// <param name="Check">Overrides the connection's and the project's `refresh.check` for this plan.</param>
/// <param name="OnFail">Overrides `refresh.on_fail` for this plan.</param>
public sealed record RefreshPlanSettings(string Name, ObjectInfo Info, string? OnFailure, string? OnBusy, EveryDuration? ExpectedEvery, int? Parallel, RefreshCheck? Check, OnFail? OnFail, int Line = 0)
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
