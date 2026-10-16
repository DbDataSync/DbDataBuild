using System.Globalization;
using DbDataBuild.Lowering;
using DbDataBuild.Sql;
using DbDataBuild.Targets;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rules;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Runs a DuckDB-dialect query three ways over the same seeded rows: on DuckDB as written (the answer), and on an engine as the tool would send it (lowered from DuckDB's plan, rewritten by the target
/// rules, transpiled). The seed is built to hit the edges: negatives, zero, NULL, a half, non-BMP and accented text, empty and padded strings, the largest INT.
/// </summary>
public sealed class EngineProbe : IDisposable
{
    public sealed record Case(string Id, string Sql, string? Note = null);
    public sealed record Outcome(string Rows, string? Error);

    // the connection that plans is the one that answers (DuckDB's rows are what the engine's are compared to): on DuckDB 2.0 its replacements keep the clock calls in the plan and return the real time
    private readonly DuckDBConnection duck = PlanningConnection.Open(lockDown: false);
    private readonly IProbeEngine engine;

    // id, i, j, d, n, s, dt, ts
    public static readonly IReadOnlyList<ProbeRow> Seed =
    [
        new(1, "7", "2", "2.5", "2.50", "abc", "2024-01-15", "2024-01-15 10:30:45"),
        new(2, "-7", "2", "-2.5", "-2.50", "ABC", "2024-02-29", "2024-02-29 23:59:59"),
        new(3, "7", "-2", "3.5", "3.50", " abc ", "2024-03-31", "2024-03-31 00:00:00"),
        new(4, "0", "0", "0.5", "0.00", "", "2023-12-31", "2023-12-31 12:00:00"),
        new(5, "NULL", "3", "NULL", "NULL", null, null, null),
        new(6, "5", "0", "1.005", "1.25", "😀a", "2024-06-30", "2024-06-30 08:15:30"),
        new(7, "2147483647", "1", "-0.5", "-0.50", "héllo", "2024-01-31", "2024-01-31 18:45:00"),
        new(8, "10", "3", "10000000000", "12345.67", "a,b,c", "2024-02-01", "2024-02-01 00:00:01"),
    ];

    public EngineProbe(IProbeEngine engine)
    {
        this.engine = engine;
        Duck("CREATE TABLE probe (id INTEGER NOT NULL, i INTEGER, j INTEGER, d DOUBLE, n DECIMAL(10, 2), s VARCHAR, dt DATE, ts TIMESTAMP)");
        Duck(Insert(sqlServer: false));
    }

    public Task CreateTableAsync() => engine.CreateProbeTableAsync(Seed);

    private static string Insert(bool sqlServer)
    {
        string Str(string? v) => v == null ? "NULL" : "'" + v.Replace("'", "''") + "'";
        string Dt(string? v) => v == null ? "NULL" : $"CAST('{v}' AS DATE)";
        string Ts(string? v) => v == null ? "NULL" : $"CAST('{v}' AS TIMESTAMP)";
        return "INSERT INTO probe VALUES " + string.Join(", ", Seed.Select(r => $"({r.Id}, {r.I}, {r.J}, {r.D}, {r.N}, {Str(r.S)}, {Dt(r.Dt)}, {Ts(r.Ts)})"));
    }

    private void Duck(string sql) { using var cmd = duck.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    /// <summary>DuckDB's answer, as the sorted rows of the first output column(s).</summary>
    public Outcome OnDuckDb(string sql)
    {
        try
        {
            using var cmd = duck.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var types = Enumerable.Range(0, r.FieldCount).Select(i => r.GetDataTypeName(i)).ToList();
            var rows = new List<object?[]>();
            while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToArray());
            return new(Rows(new ProbeRows(rows, types)), null);
        }
        catch (DuckDBException ex) { return new(string.Empty, FirstLine(ex.Message)); }
    }

    /// <summary>DuckDB's name and type for each output column of the query (DESCRIBE).</summary>
    public IReadOnlyList<(string Name, string Type)> DuckColumns(string sql)
    {
        using var cmd = duck.CreateCommand();
        cmd.CommandText = "DESCRIBE " + sql;
        using var r = cmd.ExecuteReader();
        var columns = new List<(string, string)>();
        while (r.Read()) columns.Add((r.GetString(0), r.GetString(1)));
        return columns;
    }

    /// <summary>What the tool would send the engine for this query, or the lowering's refusal.</summary>
    public (string? Sql, string? Refused) Render(string sql, DbDataBuild.Core.RewritePolicy? rewrites = null)
    {
        try
        {
            string plan;
            using (var cmd = duck.CreateCommand()) { cmd.CommandText = QueryDescriber.PlanStatement(sql); plan = (string)cmd.ExecuteScalar()!; }
            var names = new List<string>();
            using (var cmd = duck.CreateCommand()) { cmd.CommandText = "DESCRIBE " + sql; using var r = cmd.ExecuteReader(); while (r.Read()) names.Add(r.GetString(0)); }
            var lowered = PlanLowerer.Lower(plan, names, null, rewrites).Sql;
            var ruled = TargetRules.Apply(lowered, engine.Name, rewrites, engine.Version).Sql;
            var (outcome, text) = TargetRules.Transpile(ruled, Dialects.Canonical, Dialects.ForTarget(engine.Name));
            return outcome.Ok && text != null ? (TargetRules.Finish(text, engine.Name), null) : (null, "transpile: " + (outcome.Error ?? "failed"));
        }
        catch (LoweringException ex) { return (null, ex.Message); }
        catch (DuckDBException ex) { return (null, "duckdb: " + FirstLine(ex.Message)); }
        catch (Exception ex) { return (null, "CRASH " + ex.GetType().Name + ": " + FirstLine(ex.Message)); }   // a lowering that throws anything but a refusal is a tool bug
    }

    public string Lowered(string sql)
    {
        try
        {
            string plan;
            using (var cmd = duck.CreateCommand()) { cmd.CommandText = QueryDescriber.PlanStatement(sql); plan = (string)cmd.ExecuteScalar()!; }
            var names = new List<string>();
            using (var cmd = duck.CreateCommand()) { cmd.CommandText = "DESCRIBE " + sql; using var r = cmd.ExecuteReader(); while (r.Read()) names.Add(r.GetString(0)); }
            return PlanLowerer.Lower(plan, names, null, null).Sql;
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    public string LoweredAst(string sql) { var p = Polyglot.Parse(Lowered(sql), Dialects.Canonical); return p.Ok ? p.Data! : "PARSE " + p.Error; }

    public async Task<Outcome> OnEngineAsync(string rendered)
    {
        try
        {
            var r = await engine.QueryAsync(rendered);
            return new(Rows(r), null);
        }
        catch (EngineQueryException ex) { return new(string.Empty, ex.Message); }
        catch (InvalidCastException ex) { return new(string.Empty, "the engine returned a type the probe cannot read: " + FirstLine(ex.Message)); }      // PostgreSQL's interval with months
    }

    private static string Rows(ProbeRows result)
    {
        var rows = result.Rows.Select(row => string.Join("|", row.Select((v, i) => Cell(v, result.TypeNames[i])))).ToList();
        rows.Sort(StringComparer.Ordinal);
        return string.Join("; ", rows);
    }

    /// <summary>Numbers are compared as numbers (2.5 and 2.50 are the same answer); text is compared as text.</summary>
    private static string Cell(object? v, string typeName) => v switch
    {
        null => "∅",
        bool b => b ? "true" : "false",
        double d when double.IsNaN(d) || double.IsInfinity(d) => d.ToString(CultureInfo.InvariantCulture),
        double d => Number(Math.Round(d, 9)),
        float f => Number((decimal)Math.Round(f, 6)),
        decimal m => Number(Math.Round(m, 9)),
        DateTime dt when typeName.Equals("date", StringComparison.OrdinalIgnoreCase) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),      // microseconds, as DuckDB keeps them (the engines' seventh digit is always 0 here)
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        System.Collections.IEnumerable and not string => "[list]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Number(double d) => d == 0 ? "0" : d.ToString("0.#########", CultureInfo.InvariantCulture);     // -0 and 0 are the same number
    private static string Number(decimal d) => d == 0 ? "0" : d.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    public void Dispose() => duck.Dispose();
}
