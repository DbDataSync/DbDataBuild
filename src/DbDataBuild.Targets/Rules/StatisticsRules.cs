using System.Text.Json.Nodes;

namespace DbDataBuild.Targets.Rules;

/// <summary>
/// PostgreSQL's covar_pop, covar_samp and regr_sxy accumulate in double precision with an update that is unstable when one value is far from the rest: over a 1e10 outlier the covariance of two independent columns
/// comes out as -334 (and regr_sxy as -16384) where it is 0, which is what DuckDB gives and what exact arithmetic gives. The sums of x*y, x and y in NUMERIC are exact, so sum(xy) - sum(x) * sum(y) / n is the
/// value to the digits of the inputs; it is written out for those three (docs/research/function-matrix.md finding 1).
/// </summary>
public static partial class TargetRules
{
    public const string CovarianceExact = "covariance-exact";

    private static JsonNode? StatisticsRewrite(string kind, JsonObject body, HashSet<string> rules, SortedSet<string> fired)
    {
        if (kind != "aggregate_function" || !rules.Contains(CovarianceExact) || body["name"]?.GetValue<string>()?.ToLowerInvariant() is not { } name || name is not ("covar_pop" or "covar_samp" or "regr_sxy")) return null;
        if (body["args"] is not JsonArray { Count: 2 } args || body["distinct"]?.GetValue<bool>() == true || body["order_by"] is JsonArray { Count: > 0 } || IsWindowed(body)) return null;
        var present = body["filter"] is { } filter ? "(__f) AND __x IS NOT NULL AND __y IS NOT NULL" : "__x IS NOT NULL AND __y IS NOT NULL";
        var n = $"count(*) FILTER (WHERE {present})";
        var sx = $"sum(ddb_numeric(__x)) FILTER (WHERE {present})";
        var sy = $"sum(ddb_numeric(__y)) FILTER (WHERE {present})";
        var sxy = $"sum(ddb_numeric(__x) * ddb_numeric(__y)) FILTER (WHERE {present})";
        var centered = $"({sxy} - {sx} * {sy} / NULLIF({n}, 0))";
        var sql = name switch
        {
            "covar_pop" => $"CAST({centered} / NULLIF({n}, 0) AS DOUBLE)",
            "covar_samp" => $"CAST({centered} / NULLIF({n} - 1, 0) AS DOUBLE)",
            _ => $"CAST({centered} AS DOUBLE)",
        };
        fired.Add(CovarianceExact);
        return Template(sql, [("__x", args[0]!), ("__y", args[1]!), .. (body["filter"] is { } f ? new[] { ("__f", f) } : [])]);
    }

    /// <summary>True when the aggregate is the function of a window (OVER ...): a FILTER of sums would not be a window.</summary>
    private static bool IsWindowed(JsonNode node)
    {
        var p = node.Parent;
        for (var i = 0; i < 5 && p != null; i++, p = p.Parent)
            if (p is JsonObject o && o.ContainsKey("window_function")) return true;
        return false;
    }
}
