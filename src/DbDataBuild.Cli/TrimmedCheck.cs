using System.Data.Common;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `check` for the columns a project declares `trimmed: true` (DDB-237): each one is counted on the connection, the rows whose value ends in a space. The answer is a count, never a value; a table that does not
/// exist yet is not checked. SQL Server compares trailing spaces as equal, so the count is of byte lengths (`DATALENGTH`), not `=`.
/// </summary>
internal static class TrimmedCheck
{
    public static IReadOnlyList<Diagnostic> Run(ProjectContext ctx, string connection, LoginSettings login)
    {
        var engine = ctx.Config.EngineOf(connection) ?? connection;
        var quote = TrackingDdl.For(engine);
        var work = new List<(string Table, string File, ColumnDefinition Column)>();
        foreach (var m in ctx.Project.BuiltModels.Where(s => ctx.TargetsOf(s.Definition).Contains(connection) && !s.Definition.IsCopy))
            work.AddRange(m.Definition.Columns.Where(c => c.Trimmed).Select(c => (m.Definition.Name, m.DefinitionFile, c)));
        foreach (var d in ctx.Project.MappedModels.Where(d => (d.Connections ?? ctx.Config.DefaultConnections).Contains(connection)))
            work.AddRange(d.Columns.Where(c => c.Trimmed).Select(c => (d.Name, d.File, c)));
        if (work.Count == 0) return [];
        return Task.Run(async () =>
        {
            var found = new List<Diagnostic>();
            await using var read = await ReadSession.OpenAsync(login);
            foreach (var (table, file, column) in work.OrderBy(w => w.Table, StringComparer.Ordinal).ThenBy(w => w.Column.Name, StringComparer.Ordinal))
            {
                var (schema, name) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(table);
                var c = quote.Quote(column.Name);
                var text = engine == "postgres"
                    ? $"SELECT COUNT(*) FROM {quote.Quote(schema)}.{quote.Quote(name)} WHERE length({c}) <> length(rtrim({c}))"
                    : $"SELECT COUNT(*) FROM {quote.Quote(schema)}.{quote.Quote(name)} WHERE DATALENGTH({c}) <> DATALENGTH(RTRIM({c}))";
                long count;
                try { count = Convert.ToInt64((await read.QueryAsync(text, null, timeoutSeconds: 1800)).Single()[0], System.Globalization.CultureInfo.InvariantCulture); }
                catch (DbException) { continue; }             // no such table here (yet): nothing to count
                if (count > 0)
                    found.Add(new Diagnostic(DiagnosticCatalog.TrimmedColumnHasTrailingSpaces, new(file, column.Line, 0), $"`{table}.{column.Name}` is declared `trimmed: true` on `{connection}`, and {count} row(s) hold a value that ends in a space."));
            }
            return (IReadOnlyList<Diagnostic>)found;
        }).GetAwaiter().GetResult();
    }
}
