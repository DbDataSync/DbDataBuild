using System.Globalization;
using System.Text.RegularExpressions;

namespace DbDataBuild.Execution;

/// <summary>A column of the rows being copied: its name and its declared logical type, which decides what the values are made into on the way.</summary>
public sealed record TransferColumn(string Name, string LogicalType);

/// <summary>A value could not be copied. The message names the column, never the value.</summary>
public sealed class TransferException(string message) : Exception(message);

/// <summary>
/// The values of a copy are converted by the column's **declared logical type**, not by whatever the origin's driver happened to return, so every route between engines gives the destination the same
/// kind of value (DuckDB's types are the common language, as everywhere else in the tool). A value that does not fit the type is an error that names the column.
/// </summary>
public static partial class TransferValues
{
    [GeneratedRegex(@"^(?:VARCHAR|TEXT|CHAR)(?:\(\d+\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Text();

    [GeneratedRegex(@"^DECIMAL\(\d+,\s*\d+\)$", RegexOptions.IgnoreCase)]
    private static partial Regex Decimal();

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>An ODBC driver can hand a boolean over as text (psqlodbc does unless `BoolsAsChar=0`): `t`/`f`, `true`/`false`, `1`/`0`, `y`/`n`, `yes`/`no`. Anything else is not a boolean.</summary>
    private static bool BooleanOfText(string text) => text.Trim().ToLowerInvariant() switch
    {
        "t" or "true" or "1" or "y" or "yes" => true,
        "f" or "false" or "0" or "n" or "no" => false,
        _ => throw new FormatException(),
    };

    /// <summary>Converts one value read from the origin to the CLR type the destination's bulk writer takes for <paramref name="column"/>. A null stays null.</summary>
    public static object? Convert(object? value, TransferColumn column)
    {
        if (value == null || value is DBNull) return null;
        var t = column.LogicalType.Trim().ToUpperInvariant();
        try
        {
            return t switch
            {
                "BIGINT" => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
                "INTEGER" or "INT" => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
                "SMALLINT" or "TINYINT" => System.Convert.ToInt16(value, CultureInfo.InvariantCulture),
                "DOUBLE" => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
                "FLOAT" or "REAL" => System.Convert.ToSingle(value, CultureInfo.InvariantCulture),
                "BOOLEAN" => value is string flag ? BooleanOfText(flag) : System.Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                "DATE" => value switch { DateOnly d => d.ToDateTime(TimeOnly.MinValue), DateTime dt => dt.Date, _ => System.Convert.ToDateTime(value, CultureInfo.InvariantCulture).Date },
                "TIMESTAMP" => value switch { DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), DateTimeOffset o => DateTime.SpecifyKind(o.UtcDateTime, DateTimeKind.Unspecified), _ => System.Convert.ToDateTime(value, CultureInfo.InvariantCulture) },
                "TIME" => value switch { TimeSpan s => s, TimeOnly o => o.ToTimeSpan(), _ => TimeSpan.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture) },
                "TIMESTAMP WITH TIME ZONE" => value switch { DateTimeOffset o => o.ToUniversalTime(),   // the instant is what is kept: PostgreSQL takes only an offset of zero, and SQL Server keeps whichever it is given
                                                          DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()), _ => throw new InvalidCastException() },
                "UUID" => value is Guid g ? g : Guid.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture)!),
                "BLOB" => value switch { byte[] b => b, Stream s => ReadAll(s), _ => throw new InvalidCastException() },
                _ when Decimal().IsMatch(t) => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                _ when Text().IsMatch(t) => value as string ?? System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                _ => throw new TransferException($"column `{column.Name}` has type {column.LogicalType}, which a copy cannot carry."),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new TransferException($"column `{column.Name}`: a value does not fit {column.LogicalType} ({ex.GetType().Name}).");
        }
    }
}
