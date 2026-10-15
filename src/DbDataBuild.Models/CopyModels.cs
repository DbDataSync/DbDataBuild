using System.Security.Cryptography;
using System.Text;
using DbDataBuild.Core;

namespace DbDataBuild.Models;

/// <summary>
/// What a copy (<see cref="ModelKinds.Copy"/>) is made of. The rows of the origin are written to a **staging table** on the destination, and the copy itself is an ordinary load from that table (the
/// strategy, indexes, drift and acknowledgements are the existing ones). The staging table is declared to the project as a generated mapped table, so the copy's query binds like any other.
/// </summary>
public static class CopyModels
{
    /// <summary>Where the staging table lives: the tracking schema name, which exists on every connection the tool writes to, so it never adds a schema name to the data's own.</summary>
    public static string StagingSchema(ProjectConfig config) => config.TrackingSchemaName;

    /// <summary>`stg_<schema name>__<table>`, shortened with a hash of the name when it would pass the shortest identifier limit of the engines (63 bytes on PostgreSQL). Deterministic, so an interrupted run is run again on the same table.</summary>
    public static string StagingTable(string modelName)
    {
        var plain = "stg_" + modelName.Replace(".", "__");
        if (Encoding.UTF8.GetByteCount(plain) <= 60) return plain;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelName)))[..8].ToLowerInvariant();
        return plain[..Math.Min(plain.Length, 51)] + "_" + hash;
    }

    public static string StagingName(ProjectConfig config, string modelName) => $"{StagingSchema(config)}.{StagingTable(modelName)}";

    /// <summary>The staging table as a mapped table with the copy's columns, for the queries that bind against it.</summary>
    public static DeclaredModel StagingDescriptor(ProjectConfig config, ModelDefinition copy, IReadOnlyList<ColumnDefinition> columns) =>
        new(StagingName(config, copy.Name), columns.Select(c => c with { Line = 0, CollationLine = 0 }).ToList(), [], DeclaredConnections: copy.Targets ?? config.DefaultConnections, Generated: true);

    /// <summary>The query of a copy in DuckDB dialect: every column of the origin from the staging table, in the origin's order.</summary>
    public static string Query(ProjectConfig config, ModelDefinition copy) =>
        $"SELECT {string.Join(", ", copy.Columns.Select(c => Quote(c.Name)))} FROM {Quote(StagingSchema(config))}.{Quote(StagingTable(copy.Name))}\n";

    /// <summary>The query of a **local** copy (the origin is on the copy's own connection): every column of the origin, read by name. No staging table, no transfer: an ordinary load.</summary>
    public static string LocalQuery(ModelDefinition copy)
    {
        var dot = copy.From!.IndexOf('.');
        return $"SELECT {string.Join(", ", copy.Columns.Select(c => Quote(c.Name)))} FROM {Quote(copy.From[..dot])}.{Quote(copy.From[(dot + 1)..])}\n";
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
