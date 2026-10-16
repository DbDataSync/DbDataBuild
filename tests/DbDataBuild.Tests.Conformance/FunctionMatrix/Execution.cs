using System.Data.Common;
using System.Globalization;
using DbDataBuild.Targets.DuckDb;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Conformance.FunctionMatrix;

/// <summary>The rows of one result: every cell as normalized text (the last column is the value under test), and the engine's name for that column's type.</summary>
public sealed record ResultRows(IReadOnlyList<string[]> Rows, string ValueType);

/// <summary>The answer to a query: rows, or the error (exception type and first line, never more).</summary>
public sealed record Answer(ResultRows? Rows, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>Runs queries on DuckDB and on the two engines and normalizes what comes back so that two answers can be compared as text.</summary>
public sealed class Execution : IAsyncDisposable
{
    public readonly DuckDBConnection Duck = PlanningConnection.Open(lockDown: false);
    public readonly Engine SqlServer = new SqlServerEngine(version: 16);
    public readonly Engine Postgres = new PostgresEngine();
    private readonly Dictionary<string, Answer> duckCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), Answer> engineCache = [];

    public async Task StartAsync()
    {
        foreach (var e in new[] { SqlServer, Postgres }) { await e.StartAsync(); await e.CreateProbeTableAsync(EngineProbe.Seed); }
        Run("CREATE TABLE probe (id INTEGER NOT NULL, i INTEGER, j INTEGER, d DOUBLE, n DECIMAL(10, 2), s VARCHAR, dt DATE, ts TIMESTAMP)");
        string Str(string? v) => v == null ? "NULL" : "'" + v.Replace("'", "''") + "'";
        string Dt(string? v) => v == null ? "NULL" : $"CAST('{v}' AS DATE)";
        string Ts(string? v) => v == null ? "NULL" : $"CAST('{v}' AS TIMESTAMP)";
        Run("INSERT INTO probe VALUES " + string.Join(", ", EngineProbe.Seed.Select(r => $"({r.Id}, {r.I}, {r.J}, {r.D}, {r.N}, {Str(r.S)}, {Dt(r.Dt)}, {Ts(r.Ts)})")));
        Run("SET TimeZone = 'UTC'");
    }

    private void Run(string sql) { using var cmd = Duck.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    public Engine EngineFor(string target) => target == "sqlserver" ? SqlServer : Postgres;

    public Answer OnDuck(string sql)
    {
        if (duckCache.TryGetValue(sql, out var hit)) return hit;
        return duckCache[sql] = ReadDuck(sql, wrap: false);
    }

    private Answer ReadDuck(string sql, bool wrap)
    {
        try
        {
            var originalType = "";
            if (wrap) { using var d = Duck.CreateCommand(); d.CommandText = "DESCRIBE " + sql; using var dr = d.ExecuteReader(); while (dr.Read()) originalType = dr.GetString(1); }
            using var cmd = Duck.CreateCommand();
            cmd.CommandText = wrap ? $"SELECT * REPLACE (CAST(v AS VARCHAR) AS v) FROM ({sql})" : sql;
            using var r = cmd.ExecuteReader();
            var rows = new List<string[]>();
            var type = "";
            while (r.Read())
            {
                var cells = new string[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++) cells[i] = Cell(r.IsDBNull(i) ? null : r.GetValue(i), r.GetDataTypeName(i));
                rows.Add(cells);
            }
            if (r.FieldCount > 0) type = wrap ? originalType : r.GetDataTypeName(r.FieldCount - 1);
            if (wrap && type == "INTERVAL") foreach (var row in rows) row[^1] = IntervalText(row[^1]);
            return new(new(rows, type), null);
        }
        catch (DuckDBException ex) { return new(null, Short(ex.Message)); }
        catch (Exception ex) when (!wrap && ex is ArgumentOutOfRangeException or InvalidCastException or NotSupportedException or InvalidOperationException) { return ReadDuck(sql, wrap: true); }
        catch (Exception ex) { return new(null, ex.GetType().Name + ": " + Short(ex.Message)); }
    }

    public async Task<Answer> OnEngineAsync(string target, string sql)
    {
        if (engineCache.TryGetValue((target, sql), out var hit)) return hit;
        return engineCache[(target, sql)] = await ReadEngineAsync(EngineFor(target), sql);
    }

    private static async Task<Answer> ReadEngineAsync(Engine engine, string sql)
    {
        try
        {
            using var cmd = engine.Conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 60;
            using var r = await cmd.ExecuteReaderAsync();
            var rows = new List<string[]>();
            while (await r.ReadAsync())
            {
                var cells = new string[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++) cells[i] = ReadCell(r, i);
                rows.Add(cells);
            }
            return new(new(rows, r.FieldCount > 0 ? r.GetDataTypeName(r.FieldCount - 1) : ""), null);
        }
        catch (DbException ex) { return new(null, ex.GetType().Name + ": " + Short(ex.Message)); }
        catch (Exception ex) { return new(null, ex.GetType().Name + ": " + Short(ex.Message)); }
    }

    private static string ReadCell(DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return "∅";
        var typeName = r.GetDataTypeName(i);
        try
        {
            if (r is Npgsql.NpgsqlDataReader pgInterval && typeName.Equals("interval", StringComparison.OrdinalIgnoreCase)) { var iv = pgInterval.GetFieldValue<NpgsqlTypes.NpgsqlInterval>(i); return $"{iv.Months}m {iv.Days}d {iv.Time}us"; }
            return Cell(r.GetValue(i), typeName);
        }
        catch (Exception)
        {
            try
            {
                // an interval the driver cannot turn into a TimeSpan
                if (r is Npgsql.NpgsqlDataReader pg) { var iv = pg.GetFieldValue<NpgsqlTypes.NpgsqlInterval>(i); return $"{iv.Months}m {iv.Days}d {iv.Time}us"; }
                return Cell(r.GetProviderSpecificValue(i)?.ToString(), typeName);
            }
            catch (Exception ex) { return "<unreadable " + ex.GetType().Name + ">"; }
        }
    }

    private static string Short(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 220 ? line[..220] + "..." : line;
    }

    // ---- normalization ----

    public static string Cell(object? v, string typeName) => v switch
    {
        null => "∅",
        bool b => b ? "true" : "false",
        double d when double.IsNaN(d) || double.IsInfinity(d) => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        decimal m => TrimZeros(m.ToString(CultureInfo.InvariantCulture)),
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        System.Numerics.BigInteger big => big.ToString(CultureInfo.InvariantCulture),
        DateTime dt when typeName.Equals("date", StringComparison.OrdinalIgnoreCase) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero && typeName.Equals("date", StringComparison.OrdinalIgnoreCase) ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Timestamp(dt),
        DateTimeOffset dto => Timestamp(dto.UtcDateTime) + "+00",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.Ticks % TimeSpan.TicksPerSecond == 0 ? t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture).TrimEnd('0'),
        TimeSpan t when t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) && !typeName.Contains("interval", StringComparison.OrdinalIgnoreCase) => Cell(new TimeOnly(t.Ticks), typeName),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        Stream stream => "0x" + Convert.ToHexString(ReadAll(stream)).ToLowerInvariant(),
        byte[] bytes => "0x" + Convert.ToHexString(bytes).ToLowerInvariant(),
        Guid g => g.ToString(),
        string s => s,
        System.Collections.IEnumerable => "[list]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Timestamp(DateTime dt)
    {
        var s = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = dt.Ticks % TimeSpan.TicksPerSecond;
        return fraction == 0 ? s : s + "." + (fraction / 10).ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    /// <summary>DuckDB's text for an interval ("1 year 2 months 3 days 04:05:06.5") as months, days and microseconds, the way PostgreSQL's driver gives one.</summary>
    private static string IntervalText(string text)
    {
        long months = 0, days = 0, micros = 0;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var k = 0; k < tokens.Length; k++)
        {
            if (tokens[k].Contains(':'))
            {
                var negative = tokens[k].StartsWith('-');
                var parts = tokens[k].TrimStart('-').Split(':');
                var seconds = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
                var us = (long)(((long.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + long.Parse(parts[1], CultureInfo.InvariantCulture)) * 60 + seconds) * 1_000_000m);
                micros += negative ? -us : us;
            }
            else if (k + 1 < tokens.Length && long.TryParse(tokens[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                var unit = tokens[++k];
                if (unit.StartsWith("year", StringComparison.Ordinal)) months += 12 * n;
                else if (unit.StartsWith("mon", StringComparison.Ordinal)) months += n;
                else if (unit.StartsWith("day", StringComparison.Ordinal)) days += n;
                else return text;
            }
            else return text;
        }
        return $"{months}m {days}d {micros}us";
    }

    private static byte[] ReadAll(Stream s) { using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }

    private static string TrimZeros(string s) => s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : s;

    /// <summary>Two cells are the same answer when their text is, or when both are numbers within a relative 1e-9 (the engines' floating point differs in the last digits).</summary>
    public static bool SameCell(string a, string b)
    {
        if (a == b) return true;
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
        return false;
    }

    /// <summary>The kind of value a column holds, from the engine's type name: exact numbers (integer and decimal count as one), floating point, text, boolean, date, timestamp, time and a few others.</summary>
    public static string Family(string typeName)
    {
        var t = typeName.ToLowerInvariant();
        if (t.Contains("interval")) return "interval";
        if (t.Contains('[') || t.Contains("list") || t.Contains("array") || t.StartsWith('_')) return "list";
        if (t.Contains("struct") || t == "record" || t.Contains("row")) return "struct";
        if (t.Contains("timestamp") || t.Contains("datetime")) return "timestamp";
        if (t == "date") return "date";
        if (t.StartsWith("time", StringComparison.Ordinal)) return "time";
        if (t.Contains("float") || t.Contains("double") || t == "real") return "floating";
        if (t.Contains("decimal") || t.Contains("numeric") || t.Contains("money") || t.Contains("int") && !t.Contains("point")) return "exact";
        if (t.Contains("bool") || t == "bit") return "boolean";
        if (t.Contains("char") || t.Contains("text") || t.Contains("string") || t.Contains("json")) return "text";
        if (t.Contains("blob") || t.Contains("binary") || t.Contains("bytea")) return "binary";
        if (t.Contains("uuid") || t.Contains("uniqueidentifier")) return "uuid";
        return t;
    }

    /// <summary>
    /// Compares DuckDB's rows with an engine's, keyed by every column but the last (the ids of the sample rows). Returns null when they agree, else one example. With
    /// <paramref name="compareValues"/> false (time, randomness, order-dependent aggregates) only the shape is compared.
    /// </summary>
    public static string? Difference(ResultRows duck, ResultRows engine, bool compareValues)
    {
        if (duck.Rows.Count != engine.Rows.Count) return $"{duck.Rows.Count} rows on DuckDB, {engine.Rows.Count} on the engine";
        if (!compareValues) return null;
        var theirs = engine.Rows.ToDictionary(r => string.Join("|", r[..^1]), r => r[^1]);
        foreach (var row in duck.Rows)
        {
            var key = string.Join("|", row[..^1]);
            if (!theirs.TryGetValue(key, out var other)) return $"row {key} missing on the engine";
            if (!SameCell(row[^1], other)) return $"{(key.Length > 0 ? "at row " + key + ": " : "")}DuckDB {Clip(row[^1])}, engine {Clip(other)}";
        }
        var fd = Family(duck.ValueType);
        var fe = Family(engine.ValueType);
        var nonNull = duck.Rows.Any(r => r[^1] != "∅");
        if (nonNull && fd != fe && !(fd == "exact" && fe == "exact")) return $"same values, different type: DuckDB {duck.ValueType}, engine {engine.ValueType}";
        return null;
    }

    private static string Clip(string s) => s.Length > 60 ? s[..60] + "..." : s;

    public async ValueTask DisposeAsync()
    {
        Duck.Dispose();
        await SqlServer.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}
