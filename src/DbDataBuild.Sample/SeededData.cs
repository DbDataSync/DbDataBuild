using System.Globalization;
using DbDataBuild.Models;
using DuckDB.NET.Data;

namespace DbDataBuild.Sample;

/// <summary>
/// The result of running a project's seeds in an in-memory DuckDB database, kept open so the rows can be read in batches and sent somewhere else (a SQL Server or PostgreSQL source table).
/// The tables are in the order the seeds ran, which is the order they can be created and loaded in (a table is after the tables its query read).
/// </summary>
public sealed class SeededData : IDisposable
{
    private readonly DuckDBConnection db;

    public IReadOnlyList<SeededTable> Tables { get; }

    private SeededData(DuckDBConnection db, IReadOnlyList<SeededTable> tables) { this.db = db; Tables = tables; }

    public static SeededData Run(IReadOnlyList<DeclaredModel> sources, SeedSet seeds, int seed, int? scale)
    {
        var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        try
        {
            foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false" })
            {
                using var c = db.CreateCommand();
                c.CommandText = setting;
                c.ExecuteNonQuery();
            }
            return new SeededData(db, SeedRun.Load(db, sources, seeds, null, seed, scale));
        }
        catch { db.Dispose(); throw; }
    }

    /// <summary>The rows of a seeded table, in a fixed order (every column), as batches of at most <paramref name="rowsPerBatch"/> rows; each row has a value per column, null for NULL.</summary>
    public IEnumerable<IReadOnlyList<object?[]>> Batches(string table, IReadOnlyList<string> columns, int rowsPerBatch)
    {
        var dot = table.IndexOf('.');
        var (schema, name) = dot < 0 ? ("main", table) : (table[..dot], table[(dot + 1)..]);
        static string Q(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(Q))} FROM {Q(schema)}.{Q(name)} ORDER BY {string.Join(", ", columns.Select(Q))}";
        using var reader = cmd.ExecuteReader();
        var batch = new List<object?[]>(rowsPerBatch);
        while (reader.Read())
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            batch.Add(row);
            if (batch.Count == rowsPerBatch) { yield return batch; batch = new List<object?[]>(rowsPerBatch); }
        }
        if (batch.Count > 0) yield return batch;
    }

    public void Dispose() => db.Dispose();
}
