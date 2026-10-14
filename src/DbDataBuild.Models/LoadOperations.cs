using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>The closed library of load strategies (DESIGN.md 6.6).</summary>
public static class LoadStrategies
{
    public const string WatermarkAppend = "watermark_append";
    public const string DeleteInsertByRange = "delete_insert_by_range";
    public const string DeleteInsertByKey = "delete_insert_by_key";
    public const string MergeByKey = "merge_by_key";
    public const string FullReplace = "full_replace";
    public static readonly IReadOnlyList<string> All = [WatermarkAppend, DeleteInsertByRange, DeleteInsertByKey, MergeByKey, FullReplace];
}

public enum DurationUnit { Minute, Hour, Day, Week, Month }

/// <summary>A span such as "3 days" (lookback, max_span). Months are calendar months, so they are only valid on date columns.</summary>
public sealed record LoadDuration(int Amount, DurationUnit Unit)
{
    private static readonly Regex Pattern = new(@"^(\d+)\s+(minute|hour|day|week|month)s?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static LoadDuration? TryParse(string text)
    {
        var m = Pattern.Match(text.Trim());
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n) || n <= 0) return null;
        return new LoadDuration(n, Enum.Parse<DurationUnit>(m.Groups[2].Value, ignoreCase: true));
    }

    /// <summary>The moment this long before <paramref name="value"/> (a lookback applied to a watermark).</summary>
    public DateTime Before(DateTime value) => Unit switch
    {
        DurationUnit.Minute => value.AddMinutes(-Amount),
        DurationUnit.Hour => value.AddHours(-Amount),
        DurationUnit.Day => value.AddDays(-Amount),
        DurationUnit.Week => value.AddDays(-7 * Amount),
        _ => value.AddMonths(-Amount),
    };

    /// <summary>Canonical text, for example "3 days".</summary>
    public override string ToString() => $"{Amount} {Unit.ToString().ToLowerInvariant()}{(Amount == 1 ? "" : "s")}";

    /// <summary>Whether the unit makes sense on a column of this logical type: sub-day units need a timestamp, and a date takes days, weeks or months.</summary>
    public bool FitsColumnType(string logicalType)
    {
        var t = logicalType.Trim().ToUpperInvariant();
        if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal)) return true;
        if (t == "DATE") return Unit is DurationUnit.Day or DurationUnit.Week or DurationUnit.Month;
        return false;
    }
}

/// <summary>How a watermark is determined: written down and committed, never implied (DESIGN.md 6.6).</summary>
/// <param name="OnNull">require_param, or initial (a declared, committed literal in <paramref name="Initial"/>).</param>
public sealed record WatermarkSpec(string Column, LoadDuration? Lookback, string OnNull, string? Initial, bool Overridable)
{
    public const string RequireParam = "require_param";
    public const string InitialLiteral = "initial";
}

public sealed record LoadParameter(string Name, string Type);

/// <param name="Key">Key columns for the key-based strategies (defaults to the kind's unique_key).</param>
/// <param name="Column">Range column for delete_insert_by_range (defaults to the kind's time_column).</param>
/// <param name="Targets">Restricts the operation to these targets; null means every target the model declares.</param>
/// <param name="Declared">False for the operation a kind supplies implicitly.</param>
/// <param name="MaxRuntime">Opt-in: how long a refresh lets this load run before it cancels the statement and counts the load as failed (wins over the plan's).</param>
/// <param name="Inputs">The load's `inputs:` (section 8 of docs/research/refresh-plans.md): defaults and resolvers for the values it needs.</param>
/// <param name="ReadsAll">A load of a copy that replaces everything of an origin (declared `full_replace`): the copy reads the origin in full for it, with no watermark bound.</param>
public sealed record LoadOperation(
    string Name, bool IsDefault, string Strategy, IReadOnlyList<string> Key, string? Column, WatermarkSpec? Watermark,
    IReadOnlyList<LoadParameter> Params, LoadDuration? MaxSpan, IReadOnlyList<string>? Targets, bool Declared = true, int Line = 0, EveryDuration? MaxRuntime = null, IReadOnlyList<LoadInput>? Inputs = null, bool ReadsAll = false)
{
    public IReadOnlyList<LoadInput> InputList => Inputs ?? [];

    public bool AppliesTo(string target) => Targets == null || Targets.Contains(target);
}

public static class LoadPlan
{
    public const string ImplicitName = "default";

    /// <summary>
    /// The operations of a model for one target (DESIGN.md 6.6): the declared ones that apply to it, or, if none are declared, the single
    /// operation its kind supplies. A view has none (DDL only). Declared operations replace the implicit one.
    /// </summary>
    public static IReadOnlyList<LoadOperation> For(ModelDefinition model, string target)
    {
        if (model.Kind == ModelKinds.View) return [];
        if (model.Loads.Count > 0) return model.Loads.Where(o => o.AppliesTo(target)).Select(o => ForCopy(model, o)).ToList();
        return [Implicit(model)];
    }

    /// <summary>
    /// `full_replace` on a copy means everything of the origin being loaded: the whole table for a copy without a slice, and for a copy with one the rows of that origin only (delete by the slice column, then insert,
    /// as the implicit load of a sliced copy does), so a fan-in's other origins keep their rows. The copy reads the origin in full for it, whatever its watermark says (<see cref="LoadOperation.ReadsAll"/>).
    /// </summary>
    private static LoadOperation ForCopy(ModelDefinition model, LoadOperation op)
    {
        if (!model.IsCopy || op.Strategy != LoadStrategies.FullReplace) return op;
        return model.Slice == null ? op with { ReadsAll = true } : op with { Strategy = LoadStrategies.DeleteInsertByKey, Key = [model.Slice.Column], ReadsAll = true };
    }

    /// <summary>Every operation of a model across all its declared loads, or the implicit one.</summary>
    public static IReadOnlyList<LoadOperation> All(ModelDefinition model) =>
        model.Kind == ModelKinds.View ? [] : model.Loads.Count > 0 ? model.Loads.Select(o => ForCopy(model, o)).ToList() : [Implicit(model)];

    private static LoadOperation Implicit(ModelDefinition m) => m.Kind switch
    {
        // a copy from several connections replaces only the rows of the origin it is loading: delete by the slice column, then insert
        ModelKinds.Copy when m.Slice != null || m.UniqueKey.Count > 0 => new(ImplicitName, true, LoadStrategies.DeleteInsertByKey,
            m.Slice != null && !m.UniqueKey.Contains(m.Slice.Column, StringComparer.OrdinalIgnoreCase) ? [.. m.UniqueKey, m.Slice.Column] : m.UniqueKey, null, null, [], null, null, Declared: false),
        ModelKinds.Full or ModelKinds.Copy => new(ImplicitName, true, LoadStrategies.FullReplace, [], null, null, [], null, null, Declared: false),
        ModelKinds.IncrementalByUniqueKey => new(ImplicitName, true, LoadStrategies.DeleteInsertByKey, m.UniqueKey, null, null, [], null, null, Declared: false),
        // the range comes from MAX(time_column) in the target minus the lookback, never from a state table; an empty target needs a value
        ModelKinds.IncrementalByTimeRange => new(ImplicitName, true, LoadStrategies.WatermarkAppend, [], null,
            new WatermarkSpec(m.TimeColumn!, m.Lookback == null ? null : LoadDuration.TryParse(m.Lookback), WatermarkSpec.RequireParam, null, Overridable: false),
            [], null, null, Declared: false),
        _ => throw new InvalidOperationException($"No implicit load operation for kind {m.Kind}."),
    };
}
