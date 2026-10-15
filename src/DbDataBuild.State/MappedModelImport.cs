using DbDataBuild.Models;

namespace DbDataBuild.State;

/// <summary>One column of a live table as the importer saw it: the native type, the logical type it maps to (if any), and how faithful that is.</summary>
public sealed record ImportedColumn(string Name, string NativeType, string? LogicalType, TypeFit Fit, string Reason, bool Nullable);

/// <summary>A live table or view, described for a mapped model. <c>File</c> is null when the object's name cannot be a path.</summary>
/// <summary>A foreign key as the catalog reports it.</summary>
public sealed record ForeignKeyShape(string Name, IReadOnlyList<string> Columns, string ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns);

/// <param name="Notes">What was left out of the descriptor, and why (an expression index, an index on a column that has no logical type).</param>
public sealed record ImportedObject(string SchemaName, string Name, ObjectKind Kind, string? File, IReadOnlyList<ImportedColumn> Columns, IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<IndexDefinition>? Indexes = null, IReadOnlyList<ForeignKeyDefinition>? ForeignKeys = null, IReadOnlyList<string>? Notes = null)
{
    public string QualifiedName => $"{SchemaName}.{Name}";
}

public enum MappedModelChangeKind { New, ColumnAdded, ColumnRemoved, TypeChanged, NullabilityChanged, GrainAdded, IndexAdded, IndexRemoved, IndexChanged, ForeignKeyAdded, ForeignKeyRemoved, ForeignKeyChanged }

/// <summary>One difference between a committed descriptor and the live table.</summary>
public sealed record MappedModelChange(MappedModelChangeKind Kind, string? Column, string Detail);

/// <summary>
/// Source descriptors from live tables (DESIGN.md 6.5.1). Pure: the catalog is read elsewhere. A descriptor is an export, so the live table wins for columns, types and
/// nullability, with two exceptions that are human knowledge: a committed grain is kept (a primary key only seeds a new descriptor), and a committed type that means the same as the live one (INT for
/// INTEGER, or a length a person put on unlimited text) and a committed column whose native type has no logical type are kept as written, because a person supplied them.
/// </summary>
public static class MappedModelImport
{
    public static ImportedObject Describe(string target, ObjectShape shape, IReadOnlyList<string>? primaryKey, IReadOnlyList<ForeignKeyShape>? foreignKeys = null, ModelLayout layout = ModelLayout.Folder, DialectProfile? dialect = null)
    {
        var columns = shape.Columns.Select(c =>
        {
            var t = ImportedTypes.From(target, c, dialect);
            return new ImportedColumn(c.Name, NativeText(c), t.LogicalType, t.Fit, t.Reason, c.Nullable);
        }).ToList();
        // an index or foreign key is exported only if every column it names is a column of the descriptor; the primary key's own index is the grain
        var typed = columns.Where(c => c.LogicalType != null).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var indexes = new List<IndexDefinition>();
        foreach (var p in shape.Physical.Where(p => p.Kind is "index" or "constraint_index").OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var (unique, rawKeys, include) = IndexText.Parse(p.Definition);
            var keys = rawKeys.Select(k => k.EndsWith(" desc", StringComparison.Ordinal) ? k[..^5] : k).ToList();
            if (keys.Any(k => k == "<expression>")) { notes.Add($"index {p.Name} is on an expression and was not exported"); continue; }
            if (primaryKey is { Count: > 0 } pk && keys.SequenceEqual(pk, StringComparer.OrdinalIgnoreCase) && unique) continue;
            if (keys.Concat(include).Any(c => !typed.Contains(c))) { notes.Add($"index {p.Name} names a column that has no logical type and was not exported"); continue; }
            indexes.Add(new IndexDefinition(p.Name, keys, unique, include, null));
        }
        var fks = new List<ForeignKeyDefinition>();
        foreach (var f in (foreignKeys ?? []).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (f.Columns.Any(c => !typed.Contains(c))) { notes.Add($"foreign key {f.Name} names a column that has no logical type and was not exported"); continue; }
            fks.Add(new ForeignKeyDefinition(f.Name, f.Columns, $"{f.ReferencedSchema}.{f.ReferencedTable}", f.ReferencedColumns));
        }
        return new ImportedObject(shape.SchemaName, shape.Name, shape.Kind, DeclaredModelWriter.PathFor(shape.SchemaName, shape.Name, layout), columns, primaryKey ?? [], indexes, fks, notes);
    }

    /// <summary>The descriptor for a live object, merged with the committed one (null for a new descriptor). Columns with no logical type and no committed declaration are left out.</summary>
    public static DeclaredModel ToDescriptor(ImportedObject live, DeclaredModel? committed)
    {
        var columns = new List<ColumnDefinition>();
        foreach (var c in live.Columns)
        {
            var declared = committed?.Columns.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            // a committed type that means the same as the live one is kept as written (INT for INTEGER, or a length a person put on unlimited text), so a refresh does not churn it
            if (declared != null && (c.LogicalType == null || Define.LogicalTypes.Equivalent(declared.Type, c.LogicalType)))
                columns.Add(declared with { Nullable = c.Nullable, Line = 0, CollationLine = 0 });
            else if (c.LogicalType != null)
                columns.Add(new ColumnDefinition(c.Name, c.LogicalType, c.Nullable));
        }
        var kept2 = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> grain = committed != null && committed.Grain.Count > 0 ? committed.Grain
            : live.PrimaryKey.Count > 0 && live.PrimaryKey.All(kept2.Contains) ? live.PrimaryKey : [];
        // what a person said about the table and its indexes is kept: the catalog knows none of it
        var keptIndexes = (live.Indexes ?? []).Select(i => committed?.Indexes.FirstOrDefault(x => string.Equals(x.Name, i.Name, StringComparison.OrdinalIgnoreCase)) is { Info: { } info } ? i with { Info = info } : i).ToList();
        return new DeclaredModel(live.QualifiedName, columns, grain, keptIndexes, live.ForeignKeys ?? [], DeclaredInfo: committed?.DeclaredInfo);
    }

    /// <summary>What differs between a committed descriptor (null: none yet) and the one the live object produces. Empty means in sync. Column order is not compared.</summary>
    public static IReadOnlyList<MappedModelChange> Compare(DeclaredModel? committed, DeclaredModel live)
    {
        if (committed == null) return [new(MappedModelChangeKind.New, null, $"{live.Columns.Count} column(s)")];
        var changes = new List<MappedModelChange>();
        var old = committed.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var c in live.Columns)
        {
            if (!old.TryGetValue(c.Name, out var o)) { changes.Add(new(MappedModelChangeKind.ColumnAdded, c.Name, c.Type)); continue; }
            if (!Define.LogicalTypes.Equivalent(o.Type, c.Type)) changes.Add(new(MappedModelChangeKind.TypeChanged, c.Name, $"{o.Type} to {c.Type}"));
            if (o.Nullable != c.Nullable) changes.Add(new(MappedModelChangeKind.NullabilityChanged, c.Name, c.Nullable ? "now nullable" : "now NOT NULL"));
        }
        var now = live.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.AddRange(committed.Columns.Where(c => !now.Contains(c.Name)).Select(c => new MappedModelChange(MappedModelChangeKind.ColumnRemoved, c.Name, c.Type)));
        if (committed.Grain.Count == 0 && live.Grain.Count > 0) changes.Add(new(MappedModelChangeKind.GrainAdded, null, string.Join(", ", live.Grain)));

        static string Text(IndexDefinition i) => $"({string.Join(", ", i.Columns)}){(i.Unique ? " unique" : "")}{(i.Include.Count > 0 ? $" include ({string.Join(", ", i.Include)})" : "")}";
        var oldIndexes = committed.Indexes.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var i in live.Indexes)
        {
            if (!oldIndexes.TryGetValue(i.Name, out var o)) changes.Add(new(MappedModelChangeKind.IndexAdded, i.Name, Text(i)));
            else if (Text(o).ToLowerInvariant() != Text(i).ToLowerInvariant()) changes.Add(new(MappedModelChangeKind.IndexChanged, i.Name, $"{Text(o)} to {Text(i)}"));
        }
        var nowIndexes = live.Indexes.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.AddRange(committed.Indexes.Where(i => !nowIndexes.Contains(i.Name)).Select(i => new MappedModelChange(MappedModelChangeKind.IndexRemoved, i.Name, Text(i))));

        static string FkText(ForeignKeyDefinition f) => $"({string.Join(", ", f.Columns)}) to {f.Table} ({string.Join(", ", f.ReferencedColumns)})";
        var oldKeys = committed.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var f in live.ForeignKeys)
        {
            if (!oldKeys.TryGetValue(f.Name, out var o)) changes.Add(new(MappedModelChangeKind.ForeignKeyAdded, f.Name, FkText(f)));
            else if (FkText(o).ToLowerInvariant() != FkText(f).ToLowerInvariant()) changes.Add(new(MappedModelChangeKind.ForeignKeyChanged, f.Name, $"{FkText(o)} to {FkText(f)}"));
        }
        var nowKeys = live.ForeignKeys.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.AddRange(committed.ForeignKeys.Where(f => !nowKeys.Contains(f.Name)).Select(f => new MappedModelChange(MappedModelChangeKind.ForeignKeyRemoved, f.Name, FkText(f))));
        return changes;
    }

    private static string NativeText(ColumnShape c)
    {
        var p = c.Precision != null ? $"({c.Precision}{(c.Scale != null ? $", {c.Scale}" : "")})"
            : c.Length != null ? $"({(c.Length == -1 ? "max" : c.Length.ToString())})"
            : c.Scale != null ? $"({c.Scale})" : "";
        return c.Type + p;
    }
}
