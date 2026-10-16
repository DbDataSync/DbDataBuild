using System.Text;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The real-engine leg of the function rules (docs/research/function-matrix.md, entry 177 onward): each case is a DuckDB query over the probe table (and literal edge values) that the tool lowers, rewrites for the
/// engine and sends; the rows must be DuckDB's. A case in <see cref="Engines"/> only runs on the engines it names. A case that DuckDB itself rejects (a domain error) must be rejected by the engine too: the
/// engines' messages differ, the fact that nothing wrong is returned does not.
/// </summary>
public class FunctionRuleProbes
{
    /// <param name="Id">Names the case in a failure.</param>
    /// <param name="Sql">A DuckDB query over the probe table.</param>
    /// <param name="Engines">The engines the case runs on; null for both.</param>
    private sealed record Case(string Id, string Sql, string[]? Engines = null);

    private static Case V(string id, string expr, string? from = null, string[]? engines = null) => new(id, $"SELECT id, {expr} AS v FROM {from ?? "probe"}", engines);
    private static Case S(string id, string expr, string[]? engines = null) => new(id, $"SELECT {expr} AS v", engines);

    private static readonly string[] SqlServerOnly = ["sqlserver"];
    private static readonly string[] PostgresOnly = ["postgres"];

    private static readonly Case[] Cases =
    [
        // log(x) is base 10; log(b, x) has a base
        V("log_x", "log(abs(d) + 1)"), V("log_two_args", "log(2, abs(d) + 1)"), V("log_ten_args", "log(10, abs(d) + 1)"), V("log_int_col", "log(abs(i) + 1)"), V("log_decimal", "log(abs(n) + 1)"),
        S("log_one", "log(1.0)"), S("log_hundred", "log(CAST(100 AS DOUBLE))"), S("log_small", "log(CAST(0.001 AS DOUBLE))"), S("log_null", "log(CAST(NULL AS DOUBLE))"),
        S("log_zero_errors", "log(CAST(0 AS DOUBLE))"), S("log_negative_errors", "log(CAST(-1 AS DOUBLE))"),
    ];

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task The_engine_gives_DuckDBs_rows_for_every_case(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        await probe.CreateTableAsync();

        var problems = new List<string>();
        foreach (var c in Cases.Where(c => c.Engines == null || c.Engines.Contains(name)))
        {
            var expected = probe.OnDuckDb(c.Sql);
            var (rendered, refused) = probe.Render(c.Sql);
            if (rendered == null) { problems.Add($"{c.Id}: refused ({refused})"); continue; }
            var actual = await probe.OnEngineAsync(rendered);
            if (expected.Error != null)
            {
                if (actual.Error == null) problems.Add($"{c.Id}: DuckDB raises ({expected.Error}) and the engine returns [{actual.Rows}]  {rendered.Replace("\n", " ")}");
            }
            else if (actual.Error != null) problems.Add($"{c.Id}: engine error {actual.Error}  {rendered.Replace("\n", " ")}");
            else if (actual.Rows != expected.Rows) problems.Add($"{c.Id}: duckdb [{expected.Rows}] engine [{actual.Rows}]  {rendered.Replace("\n", " ")}");
        }
        Assert.True(problems.Count == 0, name + ":\n" + string.Join("\n", problems));
    }

    /// <summary>Queries the tool must refuse at compile (the lowering or the target step says so before an engine is asked): the id, the SQL and the text the message holds.</summary>
    private static readonly (string Id, string Sql, string Contains)[] Refusals = [];

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task What_cannot_be_exact_is_refused_with_a_reason(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        var problems = new StringBuilder();
        foreach (var (id, sql, contains) in Refusals)
        {
            var (rendered, refused) = probe.Render(sql);
            if (rendered != null || refused == null || !refused.Contains(contains, StringComparison.OrdinalIgnoreCase)) problems.AppendLine($"{id}: expected a refusal naming '{contains}', got {refused ?? rendered}");
        }
        Assert.True(problems.Length == 0, problems.ToString());
    }
}
