using System.Globalization;
using System.Numerics;
using DbDataBuild.Models;

namespace DbDataBuild.Sample;

/// <summary>A type no sample values can be generated for. The message names the column and says what to do.</summary>
public sealed class SampleException(string message) : Exception(message);

/// <summary>
/// Deterministic sample rows for a source table, from its declared columns (DESIGN.md 15.2). The same name, columns, row count and seed always give the same rows.
/// The values are chosen to be awkward on purpose: NULLs in nullable columns, strings that differ only in case, accents or trailing spaces, empty strings, decimals that
/// sit on a rounding boundary, doubles whose decimal text is not exact, dates on month ends. Grain columns are unique, so a source is a valid input for keyed loads and
/// joins; all other integers come from a small range so that joins and GROUP BYs find matches.
/// </summary>
public static class SampleGenerator
{
    private static readonly string[] Words =
    [
        "alpha", "Alpha", "ALPHA", "alpha ", "beta", "Beta", "gamma", "delta", "été", "ete", "", " ", "a b", "x_y", "Zoë", "zoe", "O'Brien", "100%", "naïve", "naive",
    ];

    private static readonly double[] Doubles = [0, 0.285, 1.005, 2.675, -2.5, 2.5, 0.125, 1e-3, 123456.789, -0.4];
    private static readonly decimal[] Decimals = [0m, 0.005m, 0.015m, 1.005m, 2.675m, -2.5m, 99.995m, 1234.5m];

    /// <summary>Rows of text literals ready for an INSERT: SQL literal strings, one per column, in column order.</summary>
    public static IReadOnlyList<string[]> Rows(DeclaredModel source, int count, int seed)
    {
        var rng = new Random(HashCode(source.Name, seed));
        var grain = source.Grain.Select(g => g.ToLowerInvariant()).ToList();
        var rows = new List<string[]>(count);
        for (var i = 0; i < count; i++)
        {
            var row = new string[source.Columns.Count];
            for (var c = 0; c < source.Columns.Count; c++)
            {
                var col = source.Columns[c];
                var grainIndex = grain.IndexOf(col.Name.ToLowerInvariant());
                row[c] = Value(col, i, count, rng, grainIndex, grain.Count);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static int HashCode(string name, int seed)
    {
        // string.GetHashCode is randomized per process; this is not
        var h = 17;
        foreach (var ch in name) h = unchecked(h * 31 + ch);
        return unchecked(h * 31 + seed);
    }

    private static string Value(ColumnDefinition col, int i, int count, Random rng, int grainIndex, int grainCount)
    {
        var type = col.Type.Trim().ToUpperInvariant();
        var baseType = type.Split('(', '[')[0].Trim();

        // a grain column: the last one is the row number (so the combination is unique), earlier ones repeat in blocks
        if (grainIndex >= 0 && baseType is "TINYINT" or "SMALLINT" or "INTEGER" or "INT" or "BIGINT" or "HUGEINT")
            return (grainIndex == grainCount - 1 ? i + 1 : i % Math.Max(2, count / 5) + 1).ToString(CultureInfo.InvariantCulture);
        if (grainIndex >= 0 && baseType is "VARCHAR" or "CHAR" or "TEXT" or "STRING")
            return Text($"{(grainIndex == grainCount - 1 ? "k" : "g")}{(grainIndex == grainCount - 1 ? i + 1 : i % Math.Max(2, count / 5) + 1):D4}", type);
        if (grainIndex >= 0 && baseType is "DATE" && grainIndex == grainCount - 1)
            return $"DATE '{new DateTime(2024, 1, 1).AddDays(i):yyyy-MM-dd}'";

        if (col.Nullable && rng.Next(100) < 12) return "NULL";

        switch (baseType)
        {
            case "TINYINT": return rng.Next(-5, 100).ToString(CultureInfo.InvariantCulture);
            case "SMALLINT": return Pick(rng, [0, -1, 1, 7, 100, 1000, 32000]).ToString(CultureInfo.InvariantCulture);
            case "INTEGER" or "INT": return Pick(rng, [0, 1, 2, 3, 5, 8, 13, -1, 2147483647]).ToString(CultureInfo.InvariantCulture);
            case "BIGINT": return rng.Next(100) < 8 ? "9000000000000000000" : rng.Next(0, 20).ToString(CultureInfo.InvariantCulture);
            case "HUGEINT": return rng.Next(0, 20).ToString(CultureInfo.InvariantCulture);
            case "DECIMAL" or "NUMERIC": return DecimalLiteral(type, rng);
            case "DOUBLE" or "FLOAT" or "REAL": return rng.Next(100) < 40 ? Doubles[rng.Next(Doubles.Length)].ToString("R", CultureInfo.InvariantCulture) : Math.Round(rng.NextDouble() * 1000 - 100, 4).ToString("R", CultureInfo.InvariantCulture);
            case "VARCHAR" or "CHAR" or "TEXT" or "STRING": return Text(Words[rng.Next(Words.Length)], type);
            case "BOOLEAN" or "BOOL": return rng.Next(2) == 0 ? "TRUE" : "FALSE";
            case "DATE": return $"DATE '{Day(rng):yyyy-MM-dd}'";
            case "TIMESTAMP" or "DATETIME" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMPTZ":
                return $"TIMESTAMP '{Day(rng).AddSeconds(rng.Next(0, 86400)):yyyy-MM-dd HH:mm:ss}'";
            default:
                throw new SampleException($"Sample values cannot be generated for column `{col.Name}` of type {col.Type}. Supply the rows yourself with --data <directory> (a CSV file named after the table).");
        }
    }

    private static T Pick<T>(Random rng, T[] values) => values[rng.Next(values.Length)];

    private static DateTime Day(Random rng) => rng.Next(100) < 15 ? new DateTime(2024, 1, 31).AddMonths(rng.Next(0, 4)) : new DateTime(2024, 1, 1).AddDays(rng.Next(0, 120));

    private static string Text(string word, string type)
    {
        var n = TypeLength(type);
        var w = n is > 0 and var len && word.Length > len ? word[..len] : word;
        return "'" + w.Replace("'", "''") + "'";
    }

    private static int? TypeLength(string type)
    {
        var open = type.IndexOf('(');
        return open > 0 && int.TryParse(type[(open + 1)..type.IndexOf(')')].Split(',')[0].Trim(), out var n) ? n : null;
    }

    private static string DecimalLiteral(string type, Random rng)
    {
        var open = type.IndexOf('(');
        var parts = open > 0 ? type[(open + 1)..type.IndexOf(')')].Split(',').Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray() : [18, 3];
        var (p, s) = (parts[0], parts.Length > 1 ? parts[1] : 0);
        var max = BigInteger.Pow(10, p - s) - 1;
        decimal v;
        if (rng.Next(100) < 25) v = Decimals[rng.Next(Decimals.Length)];
        else v = Math.Round((decimal)(rng.NextDouble() * 2000 - 200), Math.Min(s, 4));
        v = Math.Round(v, s, MidpointRounding.AwayFromZero);
        if (new BigInteger(Math.Abs(v)) > max) v = 0;
        return v.ToString(CultureInfo.InvariantCulture);
    }
}
