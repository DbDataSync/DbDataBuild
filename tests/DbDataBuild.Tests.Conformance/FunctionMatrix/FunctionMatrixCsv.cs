using System.Text;

namespace DbDataBuild.Tests.Conformance.FunctionMatrix;

/// <summary>Writes the results as CSV (RFC 4180 quoting, LF line ends, UTF-8 without a BOM, rows in a fixed order, no timestamps) so the files diff cleanly between runs.</summary>
public static class FunctionMatrixCsv
{
    private static readonly string[] Engines = ["sqlserver", "postgres"];
    private static readonly string[] Severity = ["lowerer error", "differs", "engine error", "refused", "works", "arg type unsupported", "not probed"];

    public static string Quote(string? s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static void Row(StringBuilder sb, params string?[] cells) => sb.Append(string.Join(",", cells.Select(Quote))).Append('\n');

    private static string Detail(OverloadResult r, string engine, string outcome)
    {
        if (!r.Targets.TryGetValue(engine, out var o)) return r.NotProbed ?? "";
        return outcome switch
        {
            "lowerer error" or "refused" => o.StaticDetail,
            "differs" or "engine error" => o.DynamicDetail,
            "not probed" => o.StaticDetail,
            _ => o.DynamicDetail,
        };
    }

    /// <summary>The combined outcome of an overload on an engine: the worst of its variants.</summary>
    public static string Outcome(OverloadResult r, string engine)
    {
        if (r.Counts[engine].Count == 0) return "not probed";
        return Severity.First(s => r.Counts[engine].ContainsKey(s));
    }

    public static string CountsText(IEnumerable<KeyValuePair<string, int>> counts) =>
        string.Join("; ", counts.OrderBy(c => Array.IndexOf(Severity, c.Key)).Select(c => $"{c.Key} {c.Value}"));

    public static void Write(string dir, string duckdbVersion, List<OverloadResult> results, List<TableFunction> tables, IReadOnlyDictionary<string, string>? meta = null)
    {
        var enc = new UTF8Encoding(false);

        // ---- one row per overload ----
        var sb = new StringBuilder();
        Row(sb, "function", "canonical", "kind", "schema", "signature", "variants", "duckdb",
            "sqlserver_outcome", "sqlserver_static", "sqlserver_static_detail", "sqlserver_lint", "sqlserver_validate", "sqlserver_dynamic", "sqlserver_dynamic_detail",
            "postgres_outcome", "postgres_static", "postgres_static_detail", "postgres_lint", "postgres_validate", "postgres_dynamic", "postgres_dynamic_detail",
            "values_compared", "fabric", "probe_sql", "lowered_sql", "type_rules", "sqlserver_sql", "postgres_sql");
        foreach (var r in results.OrderBy(r => r.Function.Name, StringComparer.Ordinal).ThenBy(r => r.Function.Signature, StringComparer.Ordinal))
        {
            var f = r.Function;
            string T(string e, Func<TargetOutcome, string> pick) => r.Targets.TryGetValue(e, out var o) ? pick(o) : "";
            Row(sb, f.Name, f.Canonical, f.Kind, f.Schema, f.Signature, r.Variants.ToString(), r.NotProbed != null && r.Targets.Count == 0 ? "not probed: " + r.NotProbed : r.DuckStatus,
                Outcome(r, "sqlserver"), T("sqlserver", o => o.Static), T("sqlserver", o => o.StaticDetail), T("sqlserver", o => o.Lint), T("sqlserver", o => o.Validate), T("sqlserver", o => o.Dynamic), T("sqlserver", o => o.DynamicDetail),
                Outcome(r, "postgres"), T("postgres", o => o.Static), T("postgres", o => o.StaticDetail), T("postgres", o => o.Lint), T("postgres", o => o.Validate), T("postgres", o => o.Dynamic), T("postgres", o => o.DynamicDetail),
                r.RunOnly ? "run only" : "values", FabricText(r), r.ProbeSql, r.Lowered, r.TypeRules, T("sqlserver", o => o.Sql ?? ""), T("postgres", o => o.Sql ?? ""));
        }
        File.WriteAllText(Path.Combine(dir, "function-matrix-overloads.csv"), sb.ToString(), enc);

        // ---- one row per function (aliases together) ----
        sb.Clear();
        Row(sb, "function", "kind", "aliases", "overloads", "signatures", "values_compared",
            "sqlserver_outcome", "sqlserver_counts", "sqlserver_static", "sqlserver_detail", "sqlserver_lint",
            "postgres_outcome", "postgres_counts", "postgres_static", "postgres_detail", "postgres_lint", "fabric", "probe_notes");
        foreach (var g in results.GroupBy(r => r.Function.Canonical).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = g.ToList();
            var names = list.Select(r => r.Function.Name).Distinct().Order(StringComparer.Ordinal).Where(n => n != g.Key).ToList();
            var signatures = list.Where(r => r.Function.Name == g.Key).Select(r => r.Function.Signature).Distinct().ToList();
            if (signatures.Count == 0) signatures = list.Select(r => r.Function.Signature).Distinct().ToList();
            var cells = new List<string?>
            {
                g.Key, string.Join("/", list.Select(r => r.Function.Kind).Distinct().Order(StringComparer.Ordinal)), string.Join(" ", names), list.Count.ToString(),
                string.Join(" ; ", signatures.Take(6)) + (signatures.Count > 6 ? $" ; ... (+{signatures.Count - 6} more)" : ""),
                list.All(r => r.RunOnly) ? "run only" : "values",
            };
            foreach (var e in Engines)
            {
                var counts = new Dictionary<string, int>();
                foreach (var r in list) foreach (var (k, n) in r.Counts[e]) counts[k] = counts.GetValueOrDefault(k) + n;
                foreach (var r in list.Where(r => r.Counts[e].Count == 0)) counts["not probed"] = counts.GetValueOrDefault("not probed") + 1;
                var outcome = Severity.First(s => counts.ContainsKey(s));
                var worstOverload = list.FirstOrDefault(r => Outcome(r, e) == outcome) ?? list[0];
                var staticLabel = list.Where(r => r.Targets.ContainsKey(e)).Select(r => r.Targets[e].Static).OrderBy(s => Array.IndexOf(new[] { "error", "refused", "not-probed", "rewritten", "same" }, s)).FirstOrDefault() ?? "";
                var detail = outcome == "not probed" ? (worstOverload.NotProbed ?? worstOverload.Targets.GetValueOrDefault(e)?.StaticDetail ?? "") : Detail(worstOverload, e, outcome);
                if (outcome != "works") detail = $"{worstOverload.Function.Signature}: {detail}";
                cells.AddRange([outcome, CountsText(counts), staticLabel, detail, string.Join(" ", list.SelectMany(r => r.Targets.TryGetValue(e, out var o) ? o.Lint.Split(' ', StringSplitOptions.RemoveEmptyEntries) : []).Distinct().Order(StringComparer.Ordinal))]);
            }
            var compared = list.Where(r => r.FabricCompared).ToList();
            cells.Add(compared.Count == 0 ? "untested (no text)" : compared.All(r => r.FabricSameText) ? "untested; same text as SQL Server" : "untested; text differs from SQL Server on " + compared.Count(r => !r.FabricSameText) + " of " + compared.Count + " overloads");
            cells.Add(string.Join(" | ", list.Select(r => r.NotProbed).Where(n => n != null).Distinct().Take(2)!));
            Row(sb, cells.ToArray());
        }
        File.WriteAllText(Path.Combine(dir, "function-matrix.csv"), sb.ToString(), enc);

        // ---- table functions, macros-as-tables and pragmas: listed, not probed ----
        sb.Clear();
        Row(sb, "name", "kind", "schema", "alias_of", "parameters", "description");
        foreach (var t in tables.OrderBy(t => t.Kind, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Parameters, StringComparer.Ordinal))
            Row(sb, t.Name, t.Kind, t.Schema, t.AliasOf, t.Parameters, t.Description.Length > 160 ? t.Description[..160] + "..." : t.Description);
        File.WriteAllText(Path.Combine(dir, "function-matrix-table-functions.csv"), sb.ToString(), enc);

        // ---- what ran against what ----
        var m = new StringBuilder();
        m.Append("duckdb=").Append(duckdbVersion).Append('\n');
        if (meta != null) foreach (var (k, v) in meta.OrderBy(k => k.Key, StringComparer.Ordinal)) m.Append(k).Append('=').Append(v).Append('\n');
        File.WriteAllText(Path.Combine(dir, "function-matrix-meta.txt"), m.ToString(), enc);
    }

    private static string FabricText(OverloadResult r) =>
        !r.FabricCompared ? "untested (no text)" : r.FabricSameText ? "untested; same text as SQL Server" : "untested; text differs from SQL Server";
}
