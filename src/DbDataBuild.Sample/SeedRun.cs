using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Models;
using DuckDB.NET.Data;

namespace DbDataBuild.Sample;

public sealed record SeededTable(string Name, string File, long Rows);

/// <summary>
/// Runs a project's seeds (DESIGN.md 15.6) on a DuckDB connection: the variables `seed` and `scale`, then `seeds/macros.sql`, then each seed in the order of what reads what, each as
/// `CREATE TABLE` from the mapped model and `INSERT ... SELECT` of the seed's query cast to the declared types. A seed has to return exactly the declared columns. The data is a function of
/// the seed and the scale: the same two numbers give the same rows.
/// </summary>
public static class SeedRun
{
    /// <param name="only">Load these sources and the seeds they read; all seeds when null.</param>
    /// <param name="scale">Set as the variable `scale`; null leaves the default the project's macros.sql sets.</param>
    public static IReadOnlyList<SeededTable> Load(DuckDBConnection db, IReadOnlyList<DeclaredModel> sources, SeedSet seeds, IReadOnlyCollection<string>? only, int seed, int? scale)
    {
        Exec(db, $"SET VARIABLE seed = {seed.ToString(CultureInfo.InvariantCulture)}");
        if (scale != null) Exec(db, $"SET VARIABLE scale = {scale.Value.ToString(CultureInfo.InvariantCulture)}");
        if (seeds.Macros != null)
            try { Exec(db, seeds.Macros); }
            catch (DuckDBException ex) { throw new SampleException($"{seeds.MacrosFile}: {FirstLine(ex.Message)}"); }

        var bySource = sources.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var s in seeds.Seeds.Where(s => !bySource.ContainsKey(s.Name)))
            throw new SampleException($"{s.File} is a seed for `{s.Name}`, which is not a mapped model of the project (there is no models/{s.Name.Replace('.', '/')}.yml with kind: mapped).");

        var order = Order(seeds, only);
        var loaded = new List<SeededTable>();
        foreach (var file in order)
        {
            var source = bySource[file.Name];
            var (schema, table) = Split(file.Name);
            try
            {
                var described = Describe(db, file.Sql);
                var declared = source.Columns.Select(c => c.Name).ToList();
                var missing = declared.Where(d => !described.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
                var extra = described.Where(d => !declared.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0 || extra.Count > 0)
                    throw new SampleException($"{file.File} returns {(missing.Count > 0 ? "no column " + string.Join(", ", missing) : "")}{(missing.Count > 0 && extra.Count > 0 ? " and " : "")}{(extra.Count > 0 ? "the undeclared column " + string.Join(", ", extra) : "")}; a seed returns exactly the columns of the source.");
                Exec(db, $"CREATE SCHEMA IF NOT EXISTS {Q(schema)}");
                Exec(db, $"CREATE TABLE {Q(schema)}.{Q(table)} ({string.Join(", ", source.Columns.Select(c => $"{Q(c.Name)} {c.Type.Trim()}{(c.Nullable ? "" : " NOT NULL")}"))})");
                Exec(db, $"INSERT INTO {Q(schema)}.{Q(table)} SELECT {string.Join(", ", source.Columns.Select(c => $"CAST({Q(c.Name)} AS {c.Type.Trim()})"))} FROM ({file.Sql.Trim().TrimEnd(';').TrimEnd()}) AS seed_query");
            }
            catch (DuckDBException ex) { throw new SampleException($"{file.File}: {FirstLine(ex.Message)}"); }
            using var count = db.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {Q(schema)}.{Q(table)}";
            loaded.Add(new SeededTable(file.Name, file.File, Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture)));
        }
        return loaded;
    }

    /// <summary>Creates a fresh DuckDB file at <paramref name="path"/> and runs the seeds into it. External access is switched off once the file is open, so no seed can read files or the network.</summary>
    public static IReadOnlyList<SeededTable> WriteFile(string path, IReadOnlyList<DeclaredModel> sources, SeedSet seeds, int seed, int? scale)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var f in new[] { path, path + ".wal" }) if (File.Exists(f)) File.Delete(f);
        try
        {
            using var db = new DuckDBConnection($"DataSource={path}");
            db.Open();
            foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false" }) Exec(db, setting);
            return Load(db, sources, seeds, null, seed, scale);
        }
        catch { foreach (var f in new[] { path, path + ".wal" }) if (File.Exists(f)) File.Delete(f); throw; }
    }

    /// <summary>The seeds to run, each after the seeds whose tables its query names. A cycle is an error.</summary>
    public static IReadOnlyList<SeedFile> Order(SeedSet seeds, IReadOnlyCollection<string>? only)
    {
        var all = seeds.Seeds.ToList();
        var dependsOn = all.ToDictionary(s => s.Name, s => all.Where(o => o != s && Mentions(s.Sql, o.Name)).Select(o => o.Name).ToList(), StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Want(string name) { if (wanted.Add(name)) foreach (var d in dependsOn[name]) Want(d); }
        foreach (var s in all.Where(s => only == null || only.Contains(s.Name, StringComparer.OrdinalIgnoreCase))) Want(s.Name);

        var ordered = new List<SeedFile>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);       // 1 visiting, 2 done
        void Visit(SeedFile s)
        {
            if (state.GetValueOrDefault(s.Name) == 2) return;
            if (state.GetValueOrDefault(s.Name) == 1) throw new SampleException($"the seeds read each other in a cycle through `{s.Name}`.");
            state[s.Name] = 1;
            foreach (var d in dependsOn[s.Name]) Visit(all.First(x => string.Equals(x.Name, d, StringComparison.OrdinalIgnoreCase)));
            state[s.Name] = 2;
            ordered.Add(s);
        }
        foreach (var s in all.Where(s => wanted.Contains(s.Name))) Visit(s);
        return ordered;
    }

    private static bool Mentions(string sql, string table)
    {
        var stripped = Regex.Replace(Regex.Replace(sql, @"--[^\n]*", " "), @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.IsMatch(stripped, @"(?<![A-Za-z0-9_.""])" + Regex.Escape(table) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
    }

    private static List<string> Describe(DuckDBConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DESCRIBE " + sql.Trim().TrimEnd(';').TrimEnd();
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private static (string SchemaName, string Table) Split(string name)
    {
        var i = name.LastIndexOf('.');
        return i < 0 ? ("main", name) : (name[..i], name[(i + 1)..]);
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    private static void Exec(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
