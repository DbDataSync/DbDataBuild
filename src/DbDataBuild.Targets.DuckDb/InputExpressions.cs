using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;

namespace DbDataBuild.Targets.DuckDb;

/// <summary>A value an expression may name: an input of the load, a parameter or a run variable. <see cref="Value"/> is invariant-culture text of <see cref="Type"/> (null is SQL NULL).</summary>
/// <param name="Type">DATE, TIMESTAMP, TIMESTAMPTZ, INTEGER, BIGINT, SMALLINT or VARCHAR.</param>
public sealed record ExpressionVariable(string Name, string Type, string? Value);

/// <param name="Names">The names the text refers to as columns (the clock words DuckDB reads as columns are left out): they must be inputs, parameters or run variables.</param>
public sealed record ExpressionCheck(string? Error, IReadOnlyList<string> Names);

/// <summary>One column of the single row an expression returned, as DuckDB's own value and the name of its DuckDB type.</summary>
public sealed record ExpressionColumn(string Name, string DuckType, object? Value);

public sealed record ExpressionResult(string? Error, IReadOnlyList<ExpressionColumn> Columns);

/// <summary>
/// Evaluates the `expression` resolvers of a load's inputs (docs/research/refresh-plans.md, section 8): a DuckDB expression, or a SELECT with no FROM, in a fresh in-memory database that has no external access and a locked
/// configuration, so the text can compute and nothing else. Every DuckDB function is allowed; a table reference is not (there is nothing to read). The clock is frozen: `now()`, `current_date` and their relatives answer
/// the instant of the run, in the zone the project says (UTC by default), never the machine's clock or zone. The names a text may use are the variables given to <see cref="Evaluate"/>.
/// </summary>
public static class InputExpressions
{
    /// <summary>Words DuckDB reads as column references (`SELECT current_date` serializes as a column) that are in fact the clock.</summary>
    private static readonly HashSet<string> ClockWords = new(StringComparer.OrdinalIgnoreCase) { "current_date", "current_time", "current_timestamp", "localtime", "localtimestamp" };

    private const string VariableTable = "ddb_v";
    private const int TimeoutMilliseconds = 10_000;

    private static readonly Regex StartsWithSelect = new(@"^\s*select\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The statement for a text: a text that starts with SELECT as written, anything else as `SELECT (text)`.</summary>
    public static string Statement(string text)
    {
        var trimmed = text.Trim().TrimEnd(';').Trim();
        return StartsWithSelect.IsMatch(trimmed) ? trimmed : "SELECT " + trimmed;
    }

    private static DuckDBConnection OpenSandbox()
    {
        var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        try
        {
            foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false", "SET memory_limit = '256MB'", "SET threads = 1" }) Exec(db, setting);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static void Exec(DuckDBConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Whether DuckDB reads the text as one SELECT that reads no table, and the names it refers to. A text that names a table, a table function (`range(3)`), a subquery over a table, or is more than one statement, is
    /// an error here, before anything is evaluated.
    /// </summary>
    public static ExpressionCheck Check(string text)
    {
        var statement = Statement(text);
        using var db = OpenSandbox();
        var (node, error) = Serialize(db, statement);
        if (node == null) return new(error, []);
        if (UsesTable(node)) return new("it reads a table, a table function or a CTE: an expression computes from its own inputs and reads nothing", []);
        var lambdaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var qualified = false;
        Walk(node, lambdaNames, names, ref qualified);
        if (qualified) return new("it uses a qualified name (`a.b`); name an input as it is declared", []);
        return new(null, names.Where(n => !lambdaNames.Contains(n) && !ClockWords.Contains(n)).Distinct(StringComparer.Ordinal).ToList());
    }

    private static (JsonNode? Node, string? Error) Serialize(DuckDBConnection db, string statement)
    {
        string? json;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT json_serialize_sql(" + Quote(statement) + ")";
            json = cmd.ExecuteScalar() as string;
        }
        if (json == null) return (null, "DuckDB could not read the text");
        var root = JsonNode.Parse(json)!;
        if (root["error"]?.GetValue<bool>() == true) return (null, Trim(root["error_message"]?.GetValue<string>() ?? "DuckDB could not read the text"));
        var statements = root["statements"] as JsonArray;
        if (statements == null || statements.Count != 1) return (null, "it must be one expression or one SELECT");
        return (root, null);
    }

    private static string Trim(string message)
    {
        var first = message.Split('\n')[0].Trim();
        return first.Length > 240 ? first[..240] + "..." : first;
    }

    private static bool UsesTable(JsonNode root)
    {
        var top = root["statements"]![0]!["node"];
        if (top?["type"]?.GetValue<string>() != "SELECT_NODE") return true;                  // a set operation, a recursive CTE ...
        var found = false;
        void Visit(JsonNode? n, bool isTop)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k == "from_table" && v is JsonObject from && from["type"]?.GetValue<string>() != "EMPTY") found = true;
                        else if (k == "cte_map" && v?["map"] is JsonArray { Count: > 0 }) found = true;
                        Visit(v, false);
                    }
                    break;
                case JsonArray a:
                    foreach (var v in a) Visit(v, false);
                    break;
            }
        }
        Visit(top, true);
        return found;
    }

    private static void Walk(JsonNode? n, HashSet<string> lambdaNames, List<string> names, ref bool qualified)
    {
        switch (n)
        {
            case JsonObject o:
                if (o["class"]?.GetValue<string>() == "LAMBDA")
                {
                    var lhs = new List<string>(); var q = false;
                    Walk(o["lhs"], new HashSet<string>(), lhs, ref q);
                    foreach (var l in lhs) lambdaNames.Add(l);
                }
                if (o["class"]?.GetValue<string>() == "COLUMN_REF" && o["column_names"] is JsonArray cn)
                {
                    if (cn.Count == 1) names.Add(cn[0]!.GetValue<string>());
                    else qualified = true;
                }
                foreach (var (_, v) in o) Walk(v, lambdaNames, names, ref qualified);
                break;
            case JsonArray a:
                foreach (var v in a) Walk(v, lambdaNames, names, ref qualified);
                break;
        }
    }

    /// <summary>
    /// Evaluates the text with the variables as the columns of a one-row table and the clock frozen at <paramref name="instant"/> in <paramref name="timezone"/>. One row comes back: its columns, named as the text names them (a
    /// bare expression has the column DuckDB gives it). More than one row, or none, is an error.
    /// </summary>
    public static ExpressionResult Evaluate(string text, IReadOnlyList<ExpressionVariable> variables, DateTimeOffset instant, string timezone)
    {
        var zone = ZoneOf(timezone);
        if (zone == null) return new($"the time zone `{timezone}` is not known on this machine", []);
        var statement = Statement(text);
        using var db = OpenSandbox();
        try
        {
            var (root, error) = Serialize(db, statement);
            if (root == null) return new(error, []);
            if (UsesTable(root)) return new("it reads a table, a table function or a CTE: an expression computes from its own inputs and reads nothing", []);

            // the clock: the same words DuckDB answers with its own clock, answered with the run's instant
            var utc = instant.UtcDateTime;
            var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
            string Ts(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            var asTimestamptz = $"TIMESTAMPTZ '{Ts(utc)}+00'";
            foreach (var name in new[] { "now", "current_timestamp", "get_current_timestamp", "transaction_timestamp", "statement_timestamp" }) Exec(db, $"CREATE OR REPLACE MACRO {name}() AS {asTimestamptz}");
            foreach (var name in new[] { "current_date", "today" }) Exec(db, $"CREATE OR REPLACE MACRO {name}() AS DATE '{local:yyyy-MM-dd}'");
            foreach (var name in new[] { "localtimestamp", "current_localtimestamp" }) Exec(db, $"CREATE OR REPLACE MACRO {name}() AS TIMESTAMP '{Ts(local)}'");
            foreach (var name in new[] { "current_time", "localtime", "current_localtime", "get_current_time" }) Exec(db, $"CREATE OR REPLACE MACRO {name}() AS TIME '{local:HH:mm:ss.ffffff}'");

            var columns = new List<string> { $"{asTimestamptz} AS \"run_started\"" };
            foreach (var v in variables.Where(v => v.Name != "run_started")) columns.Add($"{Literal(v)} AS \"{v.Name.Replace("\"", "\"\"")}\"");
            Exec(db, $"CREATE TABLE {VariableTable} AS SELECT {string.Join(", ", columns)}");
            Exec(db, $"SET TimeZone = {Quote(timezone)}");                      // the zone the project says, for the functions that read a TIMESTAMPTZ (date_trunc, strftime, extract)
            Exec(db, "SET lock_configuration = true");

            // the variables are the row the text selects from: the serialized SELECT gets that FROM and is turned back into text
            root["statements"]![0]!["node"]!["from_table"] = JsonNode.Parse($$"""{"type":"BASE_TABLE","alias":"","sample":null,"query_location":0,"schema_name":"","table_name":"{{VariableTable}}","column_name_alias":[],"catalog_name":"","at_clause":null}""");
            string? runnable;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT json_deserialize_sql(" + Quote(root.ToJsonString()) + "::JSON)";
                runnable = cmd.ExecuteScalar() as string;
            }
            if (runnable == null) return new("DuckDB could not run the text", []);

            using var run = db.CreateCommand();
            run.CommandText = runnable;
            using var timer = new Timer(_ => { try { run.Cancel(); } catch (Exception) { /* the statement ended */ } }, null, TimeoutMilliseconds, Timeout.Infinite);
            using var reader = run.ExecuteReader();
            if (!reader.Read()) return new("it returned no row", []);
            var result = new List<ExpressionColumn>();
            for (var i = 0; i < reader.FieldCount; i++) result.Add(new(reader.GetName(i), reader.GetDataTypeName(i), reader.IsDBNull(i) ? null : reader.GetValue(i)));
            if (reader.Read()) return new("it returned more than one row", []);
            return new(null, result);
        }
        catch (DuckDBException ex) { return new(Trim(ex.Message), []); }
    }

    private static TimeZoneInfo? ZoneOf(string timezone)
    {
        if (timezone == "UTC") return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    private static string Literal(ExpressionVariable v)
    {
        var type = v.Type.ToUpperInvariant();
        var duck = type switch { "DATE" => "DATE", "TIMESTAMP" => "TIMESTAMP", "TIMESTAMPTZ" => "TIMESTAMPTZ", "INTEGER" => "INTEGER", "BIGINT" => "BIGINT", "SMALLINT" => "SMALLINT", _ => "VARCHAR" };
        if (v.Value == null) return $"CAST(NULL AS {duck})";
        // the value was validated against its type by whoever produced it; a value that does not parse is a text, and DuckDB's cast says so
        return $"CAST({Quote(v.Value)} AS {duck})";
    }

    /// <summary>
    /// A DuckDB value as the invariant text of the declared type, or an error that says why it does not fit. Nothing is coerced silently: a time of day does not become a DATE, a fraction does not become an integer, a
    /// number or a date does not become text. A TIMESTAMPTZ becomes the wall time in <paramref name="timezone"/>.
    /// </summary>
    public static (string? Text, string? Error) ToText(string declaredType, ExpressionColumn column, string timezone)
    {
        var type = declaredType.Trim().ToUpperInvariant();
        var value = column.Value;
        if (value == null) return (null, null);
        var zone = ZoneOf(timezone) ?? TimeZoneInfo.Utc;
        string No(string why) => $"the value {Show(value)} ({column.DuckType}) {why}";
        DateTime? wall = value switch
        {
            DateTime d when column.DuckType.StartsWith("TimestampTz", StringComparison.OrdinalIgnoreCase) => TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(d, DateTimeKind.Utc), zone),
            DateTime d => d,
            DateTimeOffset o => TimeZoneInfo.ConvertTime(o, zone).DateTime,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            _ => null,
        };
        switch (type)
        {
            case "DATE":
                if (wall is { } w)
                {
                    if (value is not DateOnly && w.TimeOfDay != TimeSpan.Zero) return (null, No("has a time of day, which a DATE would lose"));
                    return (w.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
                }
                if (value is string s) return DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? (parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null) : (null, No("is not a date (yyyy-MM-dd)"));
                return (null, No("is not a date"));
            case "TIMESTAMP":
                if (wall is { } t) return (t.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture), null);
                if (value is string ts) return DateTime.TryParseExact(ts.Trim(), ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFF", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var tp) ? (tp.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture), null) : (null, No("is not a timestamp"));
                return (null, No("is not a timestamp"));
            case "INTEGER" or "BIGINT" or "SMALLINT":
            {
                BigInteger whole;
                switch (value)
                {
                    case sbyte or byte or short or ushort or int or uint or long or ulong: whole = new BigInteger(Convert.ToDecimal(value, CultureInfo.InvariantCulture)); break;
                    case BigInteger b: whole = b; break;
                    case decimal m when m == decimal.Truncate(m): whole = new BigInteger(m); break;
                    case double dbl when Math.Abs(dbl) < 1e18 && dbl == Math.Floor(dbl): whole = new BigInteger(dbl); break;
                    case float f when Math.Abs(f) < 1e18 && f == Math.Floor(f): whole = new BigInteger(f); break;
                    case string str when BigInteger.TryParse(str.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p): whole = p; break;
                    case decimal or double or float: return (null, No($"has a fraction, which a {type} would lose"));
                    default: return (null, No($"is not a whole number"));
                }
                var (lo, hi) = type switch { "SMALLINT" => (short.MinValue, short.MaxValue), "INTEGER" => (int.MinValue, int.MaxValue), _ => (long.MinValue, (BigInteger)long.MaxValue) };
                return whole < lo || whole > hi ? (null, No($"does not fit a {type}")) : (whole.ToString(CultureInfo.InvariantCulture), null);
            }
            default:
                return value is string text ? (text, null) : (null, No("is not text"));
        }
    }

    private static string Show(object value) => value switch
    {
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
