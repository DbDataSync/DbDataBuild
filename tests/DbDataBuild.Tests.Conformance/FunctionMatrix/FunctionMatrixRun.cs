using System.Globalization;
using System.Text;
using Xunit;

namespace DbDataBuild.Tests.Conformance.FunctionMatrix;

/// <summary>What happened to one overload (or one variant of it) on one target.</summary>
public sealed record TargetOutcome(string Static, string StaticDetail, string Lint, string Validate, string Dynamic, string DynamicDetail, bool ValuesCompared, string? Sql);

/// <summary>An overload with the outcome of each of its variants, merged.</summary>
public sealed class OverloadResult(FunctionOverload function)
{
    public FunctionOverload Function { get; } = function;
    public string? NotProbed { get; set; }
    public int Variants { get; set; }
    public string DuckStatus { get; set; } = "";
    public string ProbeSql { get; set; } = "";
    public string Lowered { get; set; } = "";
    public string TypeRules { get; set; } = "";
    public Dictionary<string, TargetOutcome> Targets { get; } = [];
    public bool FabricSameText { get; set; } = true;
    public bool FabricCompared { get; set; }
    public bool RunOnly { get; set; }
    /// <summary>Per target: how many variants ended in each combined outcome.</summary>
    public Dictionary<string, Dictionary<string, int>> Counts { get; } = new() { ["sqlserver"] = [], ["postgres"] = [] };
}

/// <summary>
/// Runs every function DuckDB lists through the tool's pipeline and, for what lowers, on the engines (docs/research/function-matrix.md). The test is skipped unless DDB_FUNCTION_MATRIX_OUT
/// names a directory; scripts/function-matrix.sh sets it. It asserts nothing about the results: they are data, written as CSV, and the report is made from them.
/// </summary>
public class FunctionMatrixRun
{
    private static readonly string[] Engines = ["sqlserver", "postgres"];

    /// <summary>Called by name: the answer depends on the clock, the session, randomness, or on the order of rows, so the values are not compared (the call must still run).</summary>
    private static readonly HashSet<string> RunOnlyFunctions = new(StringComparer.Ordinal)
    {
        "current_date", "current_time", "current_timestamp", "current_localtime", "current_localtimestamp", "get_current_time", "get_current_timestamp", "now", "today", "transaction_timestamp",
        "localtime", "localtimestamp", "current_connection_id", "current_database", "current_query", "current_query_id", "current_schema", "current_schemas", "current_setting",
        "current_transaction_id", "txid_current", "version", "random", "setseed", "uuid", "gen_random_uuid", "uuidv4", "uuidv7", "getenv", "error", "pg_backend_pid",
        "pg_conf_load_time", "pg_postmaster_start_time", "first", "last", "any_value", "arbitrary", "list", "array_agg", "group_concat", "string_agg", "listagg", "histogram", "histogram_exact", "approx_count_distinct", "approx_quantile",
        "approx_top_k", "reservoir_quantile",
    };

    [SkippableFact]
    public async Task Function_matrix_over_every_function_DuckDB_lists()
    {
        var outDir = Environment.GetEnvironmentVariable("DDB_FUNCTION_MATRIX_OUT");
        Skip.If(string.IsNullOrEmpty(outDir), "Set DDB_FUNCTION_MATRIX_OUT to a directory (scripts/function-matrix.sh does, with the engines running).");
        Skip.If(EngineEnv.Get(EngineEnv.SqlServer) == null || EngineEnv.Get(EngineEnv.Postgres) == null, "Set DBDATABUILD_TEST_MSSQL and DBDATABUILD_TEST_PG (scripts/test-engines.sh up; eval \"$(scripts/test-engines.sh env)\").");
        var only = Environment.GetEnvironmentVariable("DDB_FUNCTION_MATRIX_ONLY");          // a comma list of names, for a quick look
        Directory.CreateDirectory(outDir!);

        await using var x = new Execution();
        await x.StartAsync();
        var pipeline = new StaticPipeline(x.Duck);
        var (functions, tables, version) = FunctionCatalog.Enumerate(x.Duck);
        if (only != null) { var names = only.Split(',').ToHashSet(); functions = functions.Where(f => names.Contains(f.Name)).ToList(); }

        bool Accepts(string sql) { var a = x.OnDuck(sql); return a.Ok || !StaticPipeline.IsQueryError(a.Error!); }
        var results = new List<OverloadResult>();
        var started = DateTime.UtcNow;
        var done = 0;
        foreach (var f in functions)
        {
            if (++done % 200 == 0) Console.Error.WriteLine($"function-matrix: {done}/{functions.Count} overloads, {(DateTime.UtcNow - started).TotalSeconds:0}s");
            if (Environment.GetEnvironmentVariable("DDB_FUNCTION_MATRIX_TRACE") is { Length: > 0 } trace) File.WriteAllText(trace, f.Signature);
            results.Add(await ProbeOverload(f, x, pipeline, Accepts));
        }

        var meta = new Dictionary<string, string>
        {
            ["sqlserver"] = ((string?)await x.SqlServer.ScalarAsync("SELECT @@VERSION"))?.Split('\n')[0].Trim() ?? "",
            ["sqlserver_tool_version_setting"] = "16 (the T-SQL of SQL Server 2022)",
            ["postgres"] = (string?)await x.Postgres.ScalarAsync("SELECT version()") ?? "",
            ["fabric"] = "not run",
        };
        FunctionMatrixCsv.Write(outDir!, version, results, tables, meta);
    }

    private static IReadOnlyList<Variant> VariantsOf(FunctionOverload f, Func<string, bool> accepts) =>
        f.Kind == "macro" ? MacroVariants(f, accepts) : ProbeBuilder.Variants(f, accepts);

    /// <summary>A macro has no parameter types, so the argument types are found by trying: the first combination of integer, text, date and double that DuckDB accepts, and the first with a list.</summary>
    private static IReadOnlyList<Variant> MacroVariants(FunctionOverload f, Func<string, bool> accepts)
    {
        var n = f.Types.Count;
        var choices = new (string Label, Func<int, string> Sql, bool Column, bool List)[]
        {
            ("INTEGER", k => k == 0 ? "{x}.i" : "{x}.j", true, false), ("VARCHAR", _ => "{x}.s", true, false), ("DATE", _ => "{x}.dt", true, false), ("DOUBLE", _ => "{x}.d", true, false),
            ("LIST", _ => "[1, 2, 3]", false, true),
        };
        var combos = new List<int[]> { new int[n] };
        for (var k = 0; k < n; k++) combos = combos.SelectMany(c => Enumerable.Range(0, n > 3 ? 1 : choices.Length).Select(ch => { var d = (int[])c.Clone(); d[k] = n > 3 ? -1 : ch; return d; })).ToList();
        if (n > 3) combos = Enumerable.Range(0, choices.Length).Select(ch => Enumerable.Repeat(ch, n).ToArray()).ToList();
        Variant? scalar = null, list = null;
        foreach (var combo in combos.OrderBy(c => c.Any(i => choices[i].List) ? 1 : 0))
        {
            var isList = combo.Any(i => choices[i].List);
            if ((isList ? list : scalar) != null) continue;
            var column = 0;
            var args = new List<string>();
            var aliases = new List<char>();
            for (var k = 0; k < n; k++)
            {
                var c = choices[combo[k]];
                if (c.Column) { var alias = "abc"[column++ % 3]; aliases.Add(alias); args.Add(c.Sql(k).Replace("{x}", alias.ToString())); }
                else args.Add(c.Sql(k));
            }
            var v = ProbeBuilder.Make(f, "arguments " + string.Join(", ", combo.Select(i => choices[i].Label)), args, aliases, accepts);
            if (accepts(v.Build(null))) { if (isList) list = v; else scalar = v; }
            if (scalar != null && list != null) break;
        }
        var found = new[] { scalar, list }.Where(v => v != null).Select(v => v!).ToList();
        if (found.Count == 0) found.Add(new Variant("", _ => "", [], false, "not probed: DuckDB rejects every combination of integer, text, date, double and list arguments tried for this macro"));
        return found;
    }

    private async Task<OverloadResult> ProbeOverload(FunctionOverload f, Execution x, StaticPipeline pipeline, Func<string, bool> accepts)
    {
        var result = new OverloadResult(f);
        result.RunOnly = RunOnlyFunctions.Contains(f.Name) || RunOnlyFunctions.Contains(f.Canonical);
        if (FunctionCatalog.NotProbedReason(f) is { } reason) { result.NotProbed = reason; return result; }

        var variants = VariantsOf(f, accepts);
        result.Variants = variants.Count;
        var perVariant = new List<(Variant V, string Sql, Dictionary<string, TargetOutcome> T, string Duck)>();
        foreach (var v in variants)
        {
            if (v.NotProbed != null) { result.NotProbed ??= v.NotProbed; continue; }
            var sql = v.Build(null);
            var duck = x.OnDuck(sql);
            var st = pipeline.Run(sql);
            var duckStatus = st.DuckRejected != null ? "rejected: " + st.DuckRejected : !duck.Ok && StaticPipeline.IsQueryError(duck.Error!) ? "rejected: " + duck.Error : duck.Ok ? "ok" : "data error: " + duck.Error;
            var outcomes = new Dictionary<string, TargetOutcome>();
            foreach (var t in Engines) outcomes[t] = await OutcomeFor(f, v, sql, t, st, duck, x, pipeline, result);
            perVariant.Add((v, sql, outcomes, duckStatus));
            if (st.Targets.TryGetValue("fabric", out var fabric) && st.Targets.TryGetValue("sqlserver", out var mssql) && fabric.Final != null && mssql.Final != null)
            {
                result.FabricCompared = true;
                if (fabric.Final != mssql.Final) result.FabricSameText = false;
            }
        }
        if (perVariant.Count == 0) return result;

        // the overload's own row: the first variant that DuckDB accepts shows the texts; the outcomes are merged over all variants
        var shown = perVariant.FirstOrDefault(p => p.Duck == "ok" || p.Duck.StartsWith("data error", StringComparison.Ordinal));
        if (shown.V == null) shown = perVariant[0];
        result.ProbeSql = shown.Sql;
        result.DuckStatus = perVariant.Select(p => p.Duck).Distinct().Count() == 1 ? shown.Duck : string.Join(" | ", perVariant.Select(p => p.Duck).Distinct().Take(3));
        var shownStatic = pipeline.Run(shown.Sql);
        result.Lowered = shownStatic.Lowered ?? "";
        result.TypeRules = string.Join(",", shownStatic.TypeRules);
        foreach (var t in Engines)
        {
            var all = perVariant.Select(p => (p.V.Label, O: p.T[t])).ToList();
            foreach (var (label, o) in all)
            {
                var combined = Combine(o);
                result.Counts[t][combined] = result.Counts[t].GetValueOrDefault(combined) + 1;
            }
            result.Targets[t] = Merge(all);
        }
        return result;
    }

    /// <summary>The single word for what a variant came to on a target: works, differs, engine error, refused, lowerer error, or not probed.</summary>
    public static string Combine(TargetOutcome o) =>
        o.Static == "error" ? "lowerer error"
        : o.Static == "refused" ? "refused"
        : o.Static == "not-probed" || o.Dynamic == "n/a" ? "not probed"
        : o.Dynamic == "arg type" ? "arg type unsupported"
        : o.Dynamic;

    private static readonly string[] Severity = ["lowerer error", "differs", "engine error", "refused", "works", "arg type unsupported", "not probed"];

    private static TargetOutcome Merge(List<(string Label, TargetOutcome O)> all)
    {
        var worst = all.OrderBy(a => Array.IndexOf(Severity, Combine(a.O))).First();
        var staticWorst = all.OrderBy(a => Array.IndexOf(new[] { "error", "refused", "not-probed", "rewritten", "same" }, a.O.Static)).First().O;
        var dynamicWorst = all.Select(a => a.O).OrderBy(o => Array.IndexOf(new[] { "differs", "engine error", "works", "arg type", "n/a" }, o.Dynamic)).First();
        var detail = worst.O.Dynamic is "differs" or "engine error" ? worst.O.DynamicDetail : "";
        var staticDetail = staticWorst.StaticDetail;
        var failing = all.Where(a => Combine(a.O) != "works" && a.Label.Length > 0).Select(a => a.Label).Distinct().ToList();
        if (all.Count > 1 && failing.Count > 0 && failing.Count < all.Count) detail = $"{detail} [{failing.Count}/{all.Count} variants: {string.Join("; ", failing.Take(6))}{(failing.Count > 6 ? "; ..." : "")}]".Trim();
        return new(staticWorst.Static, staticDetail, string.Join(" ", all.SelectMany(a => a.O.Lint.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().Order(StringComparer.Ordinal)),
            string.Join(" ", all.SelectMany(a => a.O.Validate.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().Order(StringComparer.Ordinal)),
            dynamicWorst.Dynamic, detail, all.Any(a => a.O.ValuesCompared), worst.O.Sql);
    }

    private async Task<TargetOutcome> OutcomeFor(FunctionOverload f, Variant v, string sql, string target, StaticResult st, Answer duck, Execution x, StaticPipeline pipeline, OverloadResult result)
    {
        if (st.DuckRejected != null || st.Crash != null)
        {
            if (st.Crash != null) return new("error", "CRASH " + st.Crash, "", "", "n/a", "", false, null);
            return new("not-probed", "DuckDB rejected the generated query: " + st.DuckRejected, "", "", "n/a", "", false, null);
        }
        if (st.Refused != null) return new("refused", "DDB-324: " + st.Refused, "", "", "n/a", "", false, null);
        var t = StaticPipeline.Classify(st.Targets[target], f);
        if (t.Status is "error") return new("error", t.Detail, t.Lint, t.Validate, "n/a", "", false, t.Final);
        if (t.Status is "refused") return new("refused", t.Detail, t.Lint, t.Validate, "n/a", "", false, t.Final);
        var compare = !result.RunOnly;
        var (dyn, detail) = await Dynamic(f, v, sql, target, t, duck, x, pipeline, compare);
        return new(t.Status, t.Detail, t.Lint, t.Validate, dyn, detail, compare && dyn is "works" or "differs", t.Final);
    }

    private async Task<(string Status, string Detail)> Dynamic(FunctionOverload f, Variant v, string sql, string target, StaticTarget t, Answer duck, Execution x, StaticPipeline pipeline, bool compare)
    {
        if (!duck.Ok && StaticPipeline.IsQueryError(duck.Error!)) return ("n/a", "DuckDB rejected the generated query: " + duck.Error);
        var engine = await x.OnEngineAsync(target, t.Final!);
        if (duck.Ok && engine.Ok)
        {
            var diff = Execution.Difference(duck.Rows!, engine.Rows!, compare);
            return diff == null ? ("works", "") : ("differs", diff);
        }
        if (!v.CanIsolate)
        {
            if (!duck.Ok) return ("n/a", "DuckDB raises on the sample rows: " + duck.Error);
            return (EngineErrorStatus(engine.Error!), engine.Error!);
        }

        // the whole set failed on one side: run the sample rows one at a time to see which rows, and what each side says
        var ids = SampleCombinations(v.Aliases);
        var duckOk = 0;
        var engineErrors = new List<string>();
        var consecutiveErrors = 0;
        string? mismatch = null;
        var lenient = 0;
        foreach (var combo in ids)
        {
            var rowSql = v.Build(combo);
            var rowDuck = x.OnDuck(rowSql);
            var rowStatic = pipeline.Run(rowSql);
            if (rowStatic.Targets.Count == 0) continue;
            var rowText = StaticPipeline.Classify(rowStatic.Targets[target], f);
            if (rowText.Final == null || rowText.Status is "refused" or "error") continue;
            var rowEngine = await x.OnEngineAsync(target, rowText.Final);
            var key = string.Join(",", combo.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
            if (!rowDuck.Ok) { if (rowEngine.Ok) lenient++; continue; }
            duckOk++;
            if (!rowEngine.Ok)
            {
                engineErrors.Add($"{key}: {rowEngine.Error}");
                if (++consecutiveErrors >= 4 && duckOk == engineErrors.Count) break;
                continue;
            }
            consecutiveErrors = 0;
            var diff = Execution.Difference(rowDuck.Rows!, rowEngine.Rows!, compare);
            if (diff != null) mismatch ??= $"{key}: {diff}";
        }
        if (duckOk == 0) return ("n/a", $"DuckDB raises on every sampled row ({duck.Error})");
        if (engineErrors.Count == duckOk) return (EngineErrorStatus(engineErrors[0]), engineErrors[0]);
        if (engineErrors.Count > 0) return ("differs", $"the engine raises where DuckDB returns a value ({engineErrors.Count} of {duckOk} rows): {engineErrors[0]}");
        if (mismatch != null) return ("differs", mismatch);
        return ("works", lenient > 0 ? $"engine returns a value on {lenient} rows where DuckDB raises" : "");
    }

    /// <summary>An error that says the engine has no such type (HUGEINT, the unsigned integers, ...) is about an argument's type, not about the function: it is kept apart so that `+` is not called broken because it has an overload over UBIGINT.</summary>
    private static string EngineErrorStatus(string message) =>
        System.Text.RegularExpressions.Regex.IsMatch(message, @"is not a defined system type|type ""[a-z_0-9]+"" does not exist|overflow error for data type tinyint") ? "arg type" : "engine error";

    private static List<Dictionary<char, int>> SampleCombinations(IReadOnlyList<char> aliases)
    {
        var combos = new List<Dictionary<char, int>>();
        for (var k = 1; k <= 8; k++)
        {
            if (aliases.Count <= 1) { combos.Add(new() { [aliases.Count == 1 ? aliases[0] : 'a'] = k }); continue; }
            foreach (var m in new[] { k, k % 8 + 1 })
            {
                var d = new Dictionary<char, int> { [aliases[0]] = k };
                for (var a = 1; a < aliases.Count; a++) d[aliases[a]] = m;
                combos.Add(d);
            }
        }
        return combos;
    }
}
