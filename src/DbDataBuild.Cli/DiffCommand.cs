using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection compare` (effect: target read-only; DESIGN.md 9.10). Compares the data of two tables or views of one target, for example a model's table against a copy under a development schema name: the columns and types, the row
/// counts, and a full outer join on a key inside the engine that counts the rows on one side only and the rows whose values differ, column by column. By default no value is read back (counts only), so an operator can
/// work without seeing real data; `--show-values` asks for the smallest and largest value of each column and up to --limit sample rows of each kind of difference, which then appear in the output you asked for
/// and nowhere else (not in the statement log, not in a plan).
/// </summary>
internal static class DiffCommand
{
    public static int Run(CommandSpec spec, string root, string table, string? against, string? againstSchema, string? againstConnection, string? targetArg, string[] keyArg, string[] only, string[] except, bool showValues, int limit,
        TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (limit < 0) { error.WriteLine("--limit must not be negative."); return CliApp.ExitUsage; }
        if (against != null && againstSchema != null || against == null && againstSchema == null && againstConnection == null)
        { error.WriteLine("Name the other table with --against <schema_name.table_name>, or --against-schema <schema name> (the same table name under another schema name), or --against-connection <connection> (the same table on another connection)."); return CliApp.ExitUsage; }
        if (Split(table) is not { } left) { error.WriteLine($"`{table}` is not `schema_name.table_name`."); return CliApp.ExitUsage; }
        (string SchemaName, string Name) right;
        if (against != null)
        {
            if (Split(against) is not { } r) { error.WriteLine($"`{against}` is not `schema_name.table_name`."); return CliApp.ExitUsage; }
            right = r;
        }
        else right = (againstSchema ?? left.SchemaName, left.Name);

        var ctx = ProjectContext.Load(root);
        var connection = CommandTargets.Resolve(ctx.Config, targetArg, error, readOnlyOk: true);
        if (connection == null) return CliApp.ExitUsage;
        var target = connection.Name; var engine = connection.Engine;
        var otherConnection = connection;
        if (againstConnection != null)
        {
            if (!ctx.Config.Connections.TryGetValue(againstConnection, out otherConnection)) { error.WriteLine($"Unknown connection `{againstConnection}`. One of: {string.Join(", ", ctx.Config.Connections.Keys.Order(StringComparer.Ordinal))}."); return CliApp.ExitUsage; }
        }
        var across = !string.Equals(otherConnection.Name, connection.Name, StringComparison.Ordinal);
        if (!connection.Kind.IsBuildTarget || !otherConnection.Kind.IsBuildTarget)
        {
            error.WriteLine($"`connection compare` compares tables of SQL Server and PostgreSQL connections; `{(connection.Kind.IsBuildTarget ? otherConnection : connection).Name}` is {(connection.Kind.IsBuildTarget ? otherConnection : connection).Kind.Describe()}.");
            return CliApp.ExitUsage;
        }
        if (!across && string.Equals(left.SchemaName, right.SchemaName, StringComparison.OrdinalIgnoreCase) && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)) { error.WriteLine("Both sides name the same table."); return CliApp.ExitUsage; }

        // the key: given, or the table's own grain (a model's grain or unique key, a mapped model's grain)
        var key = keyArg.SelectMany(k => k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        var keySource = "option";
        if (key.Count == 0)
        {
            var qualified = $"{left.SchemaName}.{left.Name}";
            var model = ctx.Project.BuiltModels.FirstOrDefault(s => string.Equals(s.Definition.Name, qualified, StringComparison.OrdinalIgnoreCase))?.Definition;
            var descriptor = ctx.Project.MappedModels.FirstOrDefault(d => string.Equals(d.Name, qualified, StringComparison.OrdinalIgnoreCase));
            if (model is { Grain.Count: > 0 }) (key, keySource) = (model.Grain.ToList(), "grain");
            else if (model is { UniqueKey.Count: > 0 }) (key, keySource) = (model.UniqueKey.ToList(), "unique_key");
            else if (descriptor is { Grain.Count: > 0 }) (key, keySource) = (descriptor.Grain.ToList(), "grain");
            else { error.WriteLine($"{qualified} has no grain or unique key in the project, so there is nothing to match rows on. Give the columns with --key a,b."); return CliApp.ExitUsage; }
        }

        var (login, missing) = LoginSettings.FromEnvironment(connection, Login.Read, env);
        var (otherLogin, otherMissing) = across ? LoginSettings.FromEnvironment(otherConnection, Login.Read, env) : (login, null);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {target}{(across ? $" against {otherConnection.Name}" : "")}  |  login: {login?.Describe() ?? "none"}{(across ? $", {otherLogin?.Describe() ?? "none"}" : "")}  |  values: {(showValues ? "shown (requested)" : "not read")}");
        if (missing != null) { error.Diag(missing); return CliApp.ExitFindings; }
        if (otherMissing != null) { error.Diag(otherMissing); return CliApp.ExitFindings; }

        DiffPlan? plan = null;
        DiffOutcome? outcome = null;
        string? problem = null;
        try
        {
            Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login!);
                await using var otherRead = across ? await ReadSession.OpenAsync(otherLogin!) : read;
                read.CommandTimeoutSeconds = 0; otherRead.CommandTimeoutSeconds = 0;       // a comparison scans whole tables: no limit (the person can interrupt it)
                async Task<DiffTable?> Find(ReadSession session, string sessionEngine, (string SchemaName, string Name) t)
                {
                    var shapes = await CatalogReader.ReadObjectsAsync(session, sessionEngine, t.SchemaName);
                    var shape = shapes.Values.FirstOrDefault(s => string.Equals(s.Name, t.Name, StringComparison.OrdinalIgnoreCase));
                    return shape == null ? null : new DiffTable(shape.SchemaName, shape.Name, shape.Columns);
                }
                var l = await Find(read, engine, left);
                if (l == null) { problem = $"{left.SchemaName}.{left.Name} is not a table or view of {target} (or the read login cannot see it)."; return; }
                var r = await Find(otherRead, otherConnection.Engine, right);
                if (r == null) { problem = $"{right.SchemaName}.{right.Name} is not a table or view of {otherConnection.Name} (or the read login cannot see it)."; return; }
                if (across)
                {
                    (plan, problem) = CrossDiffer.Plan(engine, otherConnection.Engine, l, r, key, only, except);
                    if (plan == null) return;
                    outcome = await CrossDiffer.RunAsync(read, otherRead, plan, showValues, limit);
                    return;
                }
                (plan, problem) = TableDiffer.Plan(engine, l, r, key, only, except);
                if (plan == null) return;
                outcome = await TableDiffer.RunAsync(read, plan, showValues, limit);
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }
        if (plan == null || outcome == null) { error.WriteLine(problem); return CliApp.ExitUsage; }

        Report(output, plan, outcome, keySource, showValues, limit, target, across ? otherConnection : null);
        return outcome.Identical ? CliApp.ExitOk : CliApp.ExitFindings;
    }

    private static (string SchemaName, string Name)? Split(string text)
    {
        var i = text.IndexOf('.');
        return i <= 0 || i == text.Length - 1 ? null : (text[..i], text[(i + 1)..]);
    }

    private static void Report(TextWriter output, DiffPlan p, DiffOutcome o, string keySource, bool showValues, int limit, string target, ConnectionConfig? other)
    {
        var typeDiffers = (DiffColumn c) => other != null ? !CrossDiffer.SameType(p, other.Engine, c) : !string.Equals(Type(c.Left), Type(c.Right), StringComparison.OrdinalIgnoreCase);
        var others = p.Compared.Where(c => !p.Key.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        output.Payload("connection", target);
        output.Payload("against_connection", other?.Name);
        output.Payload("left", new { table = p.Left.Qualified, rows = o.LeftRows, columns = p.Left.Columns.Count });
        output.Payload("right", new { table = p.Right.Qualified, rows = o.RightRows, columns = p.Right.Columns.Count });
        output.Payload("key", new { columns = p.Key, source = keySource });
        output.Payload("schema", new
        {
            compared_columns = p.Compared.Select(c => c.Name).ToList(),
            only_left = p.OnlyLeft, only_right = p.OnlyRight,
            not_compared = p.Skipped.Select(s => new { column = s.Column, left_type = s.Left, right_type = s.Right, reason = s.Reason }).ToList(),
            type_differences = p.Compared.Where(typeDiffers).Select(c => new { column = c.Name, left_type = Type(c.Left), right_type = Type(c.Right) }).ToList(),
        });
        output.Payload("duplicate_keys", new { left = o.LeftDuplicateKeys, right = o.RightDuplicateKeys });
        output.Payload("rows", new
        {
            compared = o.RowsCompared, only_left = o.OnlyLeft, only_right = o.OnlyRight, matched = o.Matched, differing = o.Differing,
            differing_by_column = others.Where(c => o.DifferingByColumn.GetValueOrDefault(c.Name) > 0).ToDictionary(c => c.Name, c => o.DifferingByColumn[c.Name]),
            partial = o.Partial == null ? null : new { buckets_differing = o.Partial.BucketsDiffering, buckets = o.Partial.Buckets, rows_in_differing_buckets = o.Partial.RowsInDifferingBuckets, columns_differing = o.Partial.ColumnsDiffering },
        });
        output.Payload("column_stats", o.Stats.Select(s => new { column = s.Column, left_non_null = s.LeftNonNull, right_non_null = s.RightNonNull, left_min = s.LeftMin, left_max = s.LeftMax, right_min = s.RightMin, right_max = s.RightMax }).ToList());
        output.Payload("samples", showValues ? new
        {
            only_left = o.OnlyLeftSamples.Select(s => new { key = s.Key, values = s.Values }).ToList(),
            only_right = o.OnlyRightSamples.Select(s => new { key = s.Key, values = s.Values }).ToList(),
            differing = o.DifferingSamples.Select(s => new { key = s.Key, columns = s.Columns.ToDictionary(c => c.Key, c => new { left = c.Value.Left, right = c.Value.Right }) }).ToList(),
        } : null);
        output.Payload("identical", o.Identical);

        output.WriteLine();
        output.WriteLine($"{p.Left.Qualified}  ({o.LeftRows} row(s), {p.Left.Columns.Count} column(s))   against   {p.Right.Qualified}  ({o.RightRows} row(s), {p.Right.Columns.Count} column(s))");
        output.WriteLine($"matched on {string.Join(", ", p.Key)} ({keySource}){(other != null ? $"; {target} against {other.Name}: each engine computed a digest of every value, and only digests were compared" : "")}");
        output.WriteLine();
        output.WriteLine("Columns");
        output.WriteLine($"  compared: {p.Compared.Count}{(others.Count < p.Compared.Count ? $" ({p.Key.Count} of them the key)" : "")}");
        if (p.OnlyLeft.Count > 0) output.WriteLine($"  only in {p.Left.Qualified}: {string.Join(", ", p.OnlyLeft)}");
        if (p.OnlyRight.Count > 0) output.WriteLine($"  only in {p.Right.Qualified}: {string.Join(", ", p.OnlyRight)}");
        foreach (var c in p.Compared.Where(typeDiffers)) output.WriteLine($"  type differs: {c.Name} is {Type(c.Left)} and {Type(c.Right)}");
        foreach (var s in p.Skipped) output.WriteLine($"  not compared: {s.Column} ({s.Reason})");

        output.WriteLine();
        if (!o.RowsCompared)
        {
            output.WriteLine($"Rows were not compared: the key {string.Join(", ", p.Key)} is not unique ({o.LeftDuplicateKeys} duplicated key(s) on the left, {o.RightDuplicateKeys} on the right). Choose columns that identify one row with --key.");
            return;
        }
        if (o.Partial is { } part)
        {
            output.WriteLine("Rows");
            output.WriteLine($"  The tables differ in {part.BucketsDiffering} of {part.Buckets} buckets of the key, which hold {part.RowsInDifferingBuckets:N0} rows. Listing them row by row would need more than {CrossDiffer.MaxDrillRows:N0} rows' digests in memory, so the comparison stops here.");
            output.WriteLine($"  Columns whose values differ: {(part.ColumnsDiffering.Count == 0 ? "none (the rows differ only by their keys)" : string.Join(", ", part.ColumnsDiffering))}.");
            output.WriteLine("  Compare fewer columns (--columns a,b) or leave the differing ones out (--exclude-columns) to see the rows that differ in the others.");
            output.WriteLine();
            output.WriteLine("The tables differ.");
            return;
        }
        output.WriteLine("Rows");
        output.WriteLine($"  matched on the key: {o.Matched}   only in {p.Left.Qualified}: {o.OnlyLeft}   only in {p.Right.Qualified}: {o.OnlyRight}");
        output.WriteLine($"  matched rows whose values differ: {o.Differing}");
        foreach (var c in others.Where(c => o.DifferingByColumn.GetValueOrDefault(c.Name) > 0))
            output.WriteLine($"    {c.Name}: {o.DifferingByColumn[c.Name]}");
        var nulls = o.Stats.Where(s => s.LeftNonNull != s.RightNonNull).ToList();
        foreach (var s in nulls) output.WriteLine($"  non-NULL values of {s.Column}: {s.LeftNonNull} against {s.RightNonNull}");

        if (showValues)
        {
            foreach (var s in o.Stats.Where(s => s.LeftMin != null || s.RightMin != null))
                output.WriteLine($"  {s.Column}: {s.LeftMin} .. {s.LeftMax}   against   {s.RightMin} .. {s.RightMax}");
            string Show(IReadOnlyDictionary<string, string?> d) => string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
            if (o.OnlyLeftSamples.Count > 0) { output.WriteLine($"\nOnly in {p.Left.Qualified} (first {o.OnlyLeftSamples.Count} of {o.OnlyLeft}):"); foreach (var s in o.OnlyLeftSamples) output.WriteLine($"  {Show(s.Key)} | {Show(s.Values)}"); }
            if (o.OnlyRightSamples.Count > 0) { output.WriteLine($"\nOnly in {p.Right.Qualified} (first {o.OnlyRightSamples.Count} of {o.OnlyRight}):"); foreach (var s in o.OnlyRightSamples) output.WriteLine($"  {Show(s.Key)} | {Show(s.Values)}"); }
            if (o.DifferingSamples.Count > 0)
            {
                output.WriteLine($"\nDifferent values (first {o.DifferingSamples.Count} of {o.Differing}):");
                foreach (var s in o.DifferingSamples) output.WriteLine($"  {Show(s.Key)}: {string.Join("; ", s.Columns.Select(c => $"{c.Key} {c.Value.Left ?? "NULL"} -> {c.Value.Right ?? "NULL"}"))}");
            }
        }
        else if (o.OnlyLeft + o.OnlyRight + o.Differing > 0) output.WriteLine($"\nNo value was read. Add --show-values (and --limit {Math.Max(limit, 10)}) to see the keys and values behind these counts.");

        output.WriteLine();
        output.WriteLine(o.Identical ? "The tables are identical on the compared columns." : "The tables differ.");
    }

    private static string Type(ColumnShape c) =>
        c.Type + (c.Precision != null ? $"({c.Precision}{(c.Scale != null ? $",{c.Scale}" : "")})" : c.Length != null ? $"({(c.Length == -1 ? "max" : c.Length.ToString())})" : "");
}
