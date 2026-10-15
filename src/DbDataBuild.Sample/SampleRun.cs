using System.Globalization;
using DbDataBuild.Models;
using DuckDB.NET.Data;

namespace DbDataBuild.Sample;

/// <summary>A model to run: its declared columns, its DuckDB-dialect query, and the tables that query reads (declared models or other models).</summary>
/// <param name="Collation">DuckDB's `default_collation` for the query (the model's connection profile); null for none.</param>
/// <param name="Notes">What was done to make DuckDB compare strings as the model's connection does, shown with the result.</param>
public sealed record SampleModel(string Name, IReadOnlyList<ColumnDefinition> Columns, string Sql, IReadOnlyList<string> Upstream, string? Collation = null, IReadOnlyList<string>? Notes = null)
{
    /// <summary>The model kind, for the table the result shows.</summary>
    public string Kind { get; init; } = ModelKinds.View;
}

/// <param name="Rows">Rows generated per declared model.</param>
/// <param name="Limit">Rows of each result that are returned (the row count is always complete).</param>
/// <param name="DataDir">A directory of CSV files named after declared models (`staging.orders.csv`); a declared model with a file uses it instead of generated rows.</param>
/// <param name="Seeds">The project's seeds (DESIGN.md 15.6): a declared model with a seed is filled by running it, with the variables `seed` and `scale`, instead of generated at random.</param>
/// <param name="Scale">The variable `scale` the seeds read; null keeps the project's default.</param>
public sealed record SampleOptions(int Rows = 50, int Seed = 1, int Limit = 20, string? DataDir = null, SeedSet? Seeds = null, int? Scale = null, DbDataBuild.Core.DuckPrelude? Macros = null);

public sealed record SampleColumn(string Name, string Type);

/// <param name="Kind">The model kind of the table: a built kind for a result, `mapped` or `native` for a declared model that was filled.</param>
/// <param name="Origin">For a declared model: generated or the CSV file's name.</param>
public sealed record SampleTable(string Name, string Kind, IReadOnlyList<SampleColumn> Columns, long RowCount, IReadOnlyList<IReadOnlyList<string?>> Rows, string? Error, string? Origin, IReadOnlyList<string> Warnings);

public sealed record SampleResult(IReadOnlyList<SampleTable> Tables);

/// <summary>
/// Runs models on sample data, offline, in an in-memory DuckDB (DESIGN.md 15.2): declared models are filled with generated or supplied rows, then the selected models and the
/// models they depend on run in dependency order, each as `CREATE TABLE ... AS <query>`, so a downstream model reads what its upstream model produced. Nothing is
/// connected to and nothing is written: this answers "what would this query return on data like this", not "what is in the warehouse". External access is off, so a
/// query cannot read files or the network.
/// </summary>
public static class SampleRun
{
    public static SampleResult Run(IReadOnlyList<DeclaredModel> sources, IReadOnlyList<SampleModel> models, IReadOnlyList<string> selected, SampleOptions options)
    {
        var byModel = models.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var bySource = sources.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        // the selected models, everything they read, in dependency order
        var order = new List<SampleModel>();
        var neededSources = new List<DeclaredModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string name)
        {
            if (!seen.Add(name)) return;
            if (byModel.TryGetValue(name, out var m)) { foreach (var u in m.Upstream.Order(StringComparer.Ordinal)) Visit(u); order.Add(m); }
            else if (bySource.TryGetValue(name, out var s)) neededSources.Add(s);
        }
        foreach (var name in selected.Order(StringComparer.Ordinal)) Visit(name);

        using var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        Exec(db, "SET autoinstall_known_extensions = false");
        Exec(db, "SET autoload_known_extensions = false");
        Exec(db, "SET enable_external_access = false");
        // the project's types, before any table (a column may have one); its macros once the sources exist (DuckDB binds the names in a macro when it creates it)
        foreach (var statement in (options.Macros?.Schemas ?? []).Concat(options.Macros?.Types ?? [])) Exec(db, statement);

        var tables = new List<SampleTable>();
        // sources with a seed (and no CSV of their own) are filled by their seeds, together with the seeds those read
        var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.Seeds is { Seeds.Count: > 0 } seedSet)
        {
            bool HasCsv(DeclaredModel s) => options.DataDir != null && File.Exists(Path.Combine(options.DataDir, s.Name + ".csv"));
            var wanted = neededSources.Where(s => seedSet.Find(s.Name) != null && !HasCsv(s)).Select(s => s.Name).ToList();
            if (wanted.Count > 0)
            {
                try
                {
                    var loaded = SeedRun.Load(db, sources, seedSet, wanted, options.Seed, options.Scale);
                    foreach (var l in loaded)
                    {
                        seeded.Add(l.Name);
                        var s = bySource[l.Name];
                        tables.Add(Read(db, s.Name, s.Kind, s.Columns.Select(c => new SampleColumn(c.Name, c.Type)).ToList(), options, null, $"seeded by {l.File} (seed {options.Seed}{(options.Scale != null ? $", scale {options.Scale}" : "")})", []));
                    }
                }
                catch (SampleException ex) { foreach (var name in wanted) { seeded.Add(name); tables.Add(new SampleTable(name, bySource[name].Kind, Columns(bySource[name].Columns), 0, [], ex.Message, null, [])); } }
            }
        }
        foreach (var s in neededSources.Where(s => !seeded.Contains(s.Name)))
        {
            try { tables.Add(LoadSource(db, s, options)); }
            catch (SampleException ex) { tables.Add(new SampleTable(s.Name, s.Kind, Columns(s.Columns), 0, [], ex.Message, null, [])); }
        }

        foreach (var statement in options.Macros?.Macros ?? [])
        {
            try { Exec(db, statement); }
            catch (DuckDBException ex) { throw new SampleException($"a macro of the project could not be created ({statement.Trim().Split('\n', 2)[0].Trim()}): {ex.Message.Split('\n', 2)[0]}"); }
        }

        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in tables.Where(t => t.Error != null)) failed[s.Name] = s.Error!;
        foreach (var m in order)
        {
            var broken = m.Upstream.FirstOrDefault(failed.ContainsKey);
            if (broken != null)
            {
                failed[m.Name] = $"not run: it reads `{broken}`, which failed";
                tables.Add(new SampleTable(m.Name, m.Kind, Columns(m.Columns), 0, [], failed[m.Name], null, []));
                continue;
            }
            tables.Add(RunModel(db, m, options, failed));
        }

        var shown = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        return new SampleResult(tables.Where(t => ModelKinds.Declared.Contains(t.Kind) || shown.Contains(t.Name) || t.Error != null).ToList());
    }

    private static SampleTable LoadSource(DuckDBConnection db, DeclaredModel s, SampleOptions o)
    {
        var (schema, table) = Split(s.Name);
        Exec(db, $"CREATE SCHEMA IF NOT EXISTS {Q(schema)}");
        Exec(db, $"CREATE TABLE {Q(schema)}.{Q(table)} ({string.Join(", ", s.Columns.Select(c => $"{Q(c.Name)} {c.Type.Trim()}{(c.Nullable ? "" : " NOT NULL")}"))})");
        string origin;
        var file = o.DataDir == null ? null : Path.Combine(o.DataDir, s.Name + ".csv");
        if (file != null && File.Exists(file))
        {
            LoadCsv(db, schema, table, s, file);
            origin = Path.GetFileName(file);
        }
        else
        {
            var rows = SampleGenerator.Rows(s, o.Rows, o.Seed);
            var names = string.Join(", ", s.Columns.Select(c => Q(c.Name)));
            for (var i = 0; i < rows.Count; i += 200)
                Exec(db, $"INSERT INTO {Q(schema)}.{Q(table)} ({names}) VALUES {string.Join(", ", rows.Skip(i).Take(200).Select(r => "(" + string.Join(", ", r) + ")"))}");
            origin = $"generated ({rows.Count} rows, seed {o.Seed})";
        }
        return Read(db, s.Name, s.Kind, s.Columns.Select(c => new SampleColumn(c.Name, c.Type)).ToList(), o, null, origin, []);
    }

    /// <summary>The CSV is read on a separate connection (the one that runs the models cannot read files) and its rows are inserted into the sample database.</summary>
    private static void LoadCsv(DuckDBConnection db, string schema, string table, DeclaredModel s, string file)
    {
        using var reader = new DuckDBConnection("DataSource=:memory:");
        reader.Open();
        var types = string.Join(", ", s.Columns.Select(c => $"{SqlString(c.Name)}: {SqlString(c.Type.Trim())}"));
        using var cmd = reader.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", s.Columns.Select(c => Q(c.Name)))} FROM read_csv({SqlString(file)}, header = true, columns = {{{types}}})";
        try
        {
            using var r = cmd.ExecuteReader();
            var batch = new List<string>();
            void Flush()
            {
                if (batch.Count == 0) return;
                Exec(db, $"INSERT INTO {Q(schema)}.{Q(table)} VALUES {string.Join(", ", batch)}");
                batch.Clear();
            }
            while (r.Read())
            {
                batch.Add("(" + string.Join(", ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : Literal(r.GetValue(i), r.GetDataTypeName(i)))) + ")");
                if (batch.Count == 200) Flush();
            }
            Flush();
        }
        catch (DuckDBException ex) { throw new SampleException($"`{Path.GetFileName(file)}` could not be read as `{s.Name}`: {FirstLine(ex.Message)}"); }
    }

    private static string Literal(object v, string typeName) => v switch
    {
        string str => SqlString(str),
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => $"TIMESTAMP '{dt:yyyy-MM-dd HH:mm:ss.ffffff}'",
        DateOnly d => $"DATE '{d:yyyy-MM-dd}'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => SqlString(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""),
    };

    private static SampleTable RunModel(DuckDBConnection db, SampleModel m, SampleOptions o, Dictionary<string, string> failed)
    {
        var (schema, table) = Split(m.Name);
        try
        {
            Exec(db, $"CREATE SCHEMA IF NOT EXISTS {Q(schema)}");
            if (m.Collation != null) Exec(db, $"SET default_collation = '{m.Collation}'");           // for this query only: the table it makes keeps plain VARCHAR columns
            try { Exec(db, $"CREATE TABLE {Q(schema)}.{Q(table)} AS {m.Sql.Trim().TrimEnd(';').TrimEnd()}"); }
            finally { if (m.Collation != null) Exec(db, "RESET default_collation"); }
            using var d = db.CreateCommand();
            d.CommandText = $"DESCRIBE {Q(schema)}.{Q(table)}";
            var columns = new List<SampleColumn>();
            using (var r = d.ExecuteReader()) while (r.Read()) columns.Add(new SampleColumn(r.GetString(0), r.GetString(1)));
            var declared = m.Columns.Select(c => c.Name).ToList();
            var warnings = new List<string>(m.Notes ?? []);
            var missing = declared.Where(n => !columns.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            var extra = columns.Where(c => !declared.Any(n => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).Select(c => c.Name).ToList();
            if (missing.Count > 0) warnings.Add($"the query does not return the declared column(s) {string.Join(", ", missing)}");
            if (extra.Count > 0) warnings.Add($"the query returns column(s) {string.Join(", ", extra)} that the definition does not declare");
            return Read(db, m.Name, m.Kind, columns, o, null, null, warnings);
        }
        catch (DuckDBException ex)
        {
            failed[m.Name] = FirstLine(ex.Message);
            return new SampleTable(m.Name, m.Kind, Columns(m.Columns), 0, [], FirstLine(ex.Message), null, []);
        }
    }

    private static SampleTable Read(DuckDBConnection db, string name, string kind, IReadOnlyList<SampleColumn> columns, SampleOptions o, string? error, string? origin, IReadOnlyList<string> warnings)
    {
        var (schema, table) = Split(name);
        using var count = db.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {Q(schema)}.{Q(table)}";
        var total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Q(schema)}.{Q(table)} LIMIT {Math.Max(0, o.Limit)}";
        var rows = new List<IReadOnlyList<string?>>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : Format(r.GetValue(i))).ToList());
        return new SampleTable(name, kind, columns, total, rows, error, origin, warnings);
    }

    private static string Format(object v) => v switch
    {
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00" : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static IReadOnlyList<SampleColumn> Columns(IReadOnlyList<ColumnDefinition> cols) => cols.Select(c => new SampleColumn(c.Name, c.Type)).ToList();

    private static (string SchemaName, string Table) Split(string name)
    {
        var i = name.LastIndexOf('.');
        return i < 0 ? ("main", name) : (name[..i], name[(i + 1)..]);
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static string SqlString(string s) => "'" + s.Replace("'", "''") + "'";
    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    private static void Exec(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
