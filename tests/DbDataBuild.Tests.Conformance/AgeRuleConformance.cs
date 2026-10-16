using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// age() through the tool's own pipeline (lowering, the target rules age-parts, normalized-interval and age-second, transpile) on both engines against DuckDB, over every ordered pair of edge timestamps (month ends,
/// leap days, the century years, the microsecond before midnight, both directions, equal pairs). docs/research/age-function.md section 5 and 8; the exploratory form is FunctionMatrix/AgeProbe.cs.
/// </summary>
public class AgeRuleConformance
{
    private static readonly string[] Stamps =
    [
        "1900-02-28 00:00:00", "1900-03-01 10:30:45.5", "1999-12-31 23:59:59.999999", "2000-02-28 08:15:30", "2000-02-29 00:00:00.000001", "2000-03-01 12:00:00", "2023-01-31 00:00:00", "2023-02-28 23:59:59.999999",
        "2023-03-31 10:30:45.5", "2023-04-30 08:15:30", "2023-12-31 12:00:00", "2024-01-30 00:00:00", "2024-01-31 08:15:30", "2024-02-01 00:00:00.000001", "2024-02-28 23:59:59.999999", "2024-02-29 00:00:00",
        "2024-02-29 10:30:45.5", "2024-03-01 00:00:00", "2024-03-01 12:00:00", "2024-03-30 08:15:30", "2024-03-31 00:00:00", "2024-03-31 23:59:59.999999", "2024-04-30 10:30:45.5", "2024-05-31 12:00:00",
        "2024-12-31 00:00:00", "2025-01-31 08:15:30", "2025-02-28 00:00:00.000001", "2025-03-01 10:30:45.5", "2025-03-31 23:59:59.999999", "2100-02-28 12:00:00", "2100-03-01 00:00:00",
    ];

    private static readonly string[] Shapes =
    [
        "date_part('year', age(x.s, y.s))", "date_part('month', age(x.s, y.s))", "date_part('day', age(x.s, y.s))", "date_part('hour', age(x.s, y.s))", "date_part('minute', age(x.s, y.s))", "date_part('second', age(x.s, y.s))",
        "age(x.s, y.s) > INTERVAL '1 year'", "age(x.s, y.s) >= INTERVAL '30 days'", "age(x.s, y.s) = INTERVAL '1 month'", "age(x.s, y.s) < INTERVAL '0 days'", "age(x.s, y.s) < age(y.s, TIMESTAMP '2024-01-01 00:00:00')",
        "age(x.s, y.s) <> INTERVAL '1 month 2 days 03:04:05'",
    ];

    /// <summary>The query that covers every ordered pair: the stamps as one VALUES list on each side of a cross join.</summary>
    private static IEnumerable<string> Pairs(string expression)
    {
        string List() => string.Join(", ", Stamps.Select(t => $"(TIMESTAMP '{t}')"));
        yield return $"SELECT date_diff('second', TIMESTAMP '1900-01-01', x.s) AS a, date_diff('second', TIMESTAMP '1900-01-01', y.s) AS b, {expression} AS v FROM (VALUES {List()}) AS x(s) CROSS JOIN (VALUES {List()}) AS y(s)";
    }

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task Age_shapes_give_DuckDBs_value_on_every_pair(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        var problems = new List<string>();
        foreach (var shape in Shapes)
        {
            var differing = 0; string? first = null;
            foreach (var sql in Pairs(shape))
            {
                var expected = probe.OnDuckDb(sql);
                var (rendered, refused) = probe.Render(sql);
                if (rendered == null) { problems.Add($"{shape}: refused ({refused})"); break; }
                var actual = await probe.OnEngineAsync(rendered);
                if (actual.Error != null) { problems.Add($"{shape}: {actual.Error}"); break; }
                if (expected.Error != null || actual.Rows != expected.Rows)
                {
                    var (e, a) = (expected.Rows.Split("; "), actual.Rows.Split("; "));
                    differing += e.Zip(a).Count(p => p.First != p.Second);
                    first ??= e.Zip(a).Where(p => p.First != p.Second).Select(p => $"duckdb [{p.First}] engine [{p.Second}]").FirstOrDefault() ?? expected.Error;
                }
            }
            if (differing > 0) problems.Add($"{shape}: {differing} pairs differ, first {first}");
        }
        Assert.True(problems.Count == 0, name + ":\n" + string.Join("\n", problems));
    }

    [SkippableTheory]
    [InlineData("sqlserver")]
    public async Task Age_as_a_value_and_the_other_parts_are_refused_before_the_engine_is_asked(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        var linter = new DbDataBuild.Sql.Matrix.MatrixLinter(DbDataBuild.Sql.Matrix.MatrixLoader.LoadEmbedded([]));
        foreach (var shape in new[] { "age(x.s, y.s)", "date_part('epoch', age(x.s, y.s))" })
        {
            var (rendered, _) = probe.Render($"SELECT {shape} AS v FROM (SELECT TIMESTAMP '2024-01-01 00:00:00' AS s) x CROSS JOIN (SELECT TIMESTAMP '2023-01-01 00:00:00' AS s) y");
            Assert.NotNull(rendered);
            Assert.Contains("AGE(", rendered!.ToUpperInvariant());                              // not rewritten: the lint refuses it (unit test), the engine would fail
        }
        Assert.NotNull(linter);
    }
}
