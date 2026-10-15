using System.Data;
using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Sample;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection seed` (effect: target writes; DESIGN.md 15.6). Runs the project's seeds in DuckDB, then creates each seeded table (a mapped model) on a target and fills it. It is how a sandbox database gets the
/// data of a template (or of any project that keeps the rows of its mapped models as DuckDB queries). Without `--apply` it prints what it would do and connects to nothing. It writes only the tables the mapped models describe, and
/// stops at the first table that already exists unless `--replace` says to drop and recreate it, so it cannot touch a table the project does not name. Rows travel as bound parameters in batches through the mutation gate;
/// the statement log has the statements and the number of values, not the values.
/// </summary>
internal static class LoadSeedsCommand
{
    /// <summary>Both engines take far more, but SQL Server stops at 2,100 parameters and 1,000 rows in one VALUES list.</summary>
    internal const int MaxParametersPerStatement = 2000;
    internal const int MaxRowsPerStatement = 1000;

    public static int Run(CommandSpec spec, string root, string? targetArg, int seed, int? scale, bool replace, bool apply, TextWriter output, TextWriter error, Func<string, string?> environment)
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, diags);
        foreach (var d in diags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (diags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;
        var connection = CommandTargets.Resolve(config, targetArg, error);
        if (connection == null) return CliApp.ExitUsage;
        var target = connection.Name; var engine = connection.Engine;

        LoginSettings? write = null;
        if (apply)
        {
            var (settings, missing) = LoginSettings.FromEnvironment(connection, Login.Write, environment);
            if (missing != null)
            {
                output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {target}  |  login: none");
                error.Diag(missing);
                return CliApp.ExitFindings;
            }
            write = settings;
        }
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {target}  |  login: {(write?.Describe() ?? "none (not applying)")}");

        var ctx = ProjectContext.Load(root);
        var seeds = SeedLoader.Load(root);
        if (seeds.Seeds.Count == 0) { error.WriteLine($"The project has no seeds: put a DuckDB query per mapped model in {SeedLoader.Directory}/<schema name>/<table>.sql."); return CliApp.ExitUsage; }

        var ddl = TargetRegistry.Get(engine).CreateDdl(config.ForConnection(target));
        SeededData data;
        try { data = SeededData.Run(ctx.Project.MappedModels, seeds, seed, scale); }
        catch (SampleException ex) { error.WriteLine(ex.Message); return CliApp.ExitFindings; }

        using (data)
        {
            var bySource = ctx.Project.MappedModels.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
            var tables = new List<(SeededTable Seeded, DeclaredModel Source, IReadOnlyList<NativeColumn> Columns)>();
            try
            {
                foreach (var t in data.Tables)
                {
                    var source = bySource[t.Name];
                    tables.Add((t, source, source.Columns.Select(c => ddl.Map(source.Name, c)).ToList()));
                }
            }
            catch (DdlUnsupportedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }

            var summary = tables.Select(t => new { name = t.Seeded.Name, rows = t.Seeded.Rows, statements = Statements(t.Seeded.Rows, t.Columns.Count) + (replace ? 3 : 2) }).ToList();
            output.Payload("connection", target);
            output.Payload("seed", seed);
            output.Payload("scale", scale);
            output.Payload("replace", replace);
            output.Payload("applied", apply);
            output.Payload("tables", summary);
            output.WriteLine($"{tables.Count} table(s), {tables.Sum(t => t.Seeded.Rows)} row(s) (seed {seed}{(scale != null ? $", scale {scale}" : "")}):");
            foreach (var t in tables) output.WriteLine($"  {t.Seeded.Name,-32} {t.Seeded.Rows,10} row(s)");
            if (!apply)
            {
                output.WriteLine();
                foreach (var t in tables)
                {
                    var (schema, name) = DdlGenerator.Split(t.Source.Name);
                    output.WriteLine(ddl.CreateSchema(schema));
                    if (replace) output.WriteLine(ddl.DropTableIfExists(schema, name));
                    output.WriteLine(ddl.CreateTable(schema, name, t.Columns));
                    output.WriteLine($"-- then {Statements(t.Seeded.Rows, t.Columns.Count)} INSERT statement(s) of up to {RowsPerStatement(t.Columns.Count)} rows");
                }
                output.WriteLine();
                output.WriteLine($"Nothing was executed. Run `{ProductInfo.Cli} {spec.Name} {target} --apply` with the write login configured to create and fill these tables{(replace ? "" : " (it stops at a table that already exists; --replace drops and recreates)")}.");
                output.Next($"connection seed {target} --apply");
                return CliApp.ExitOk;
            }

            var runId = Guid.NewGuid();
            try
            {
                using var log = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), spec.Name, runId);
                output.WriteLine($"Statement log: {Path.GetRelativePath(root, log.Path)}");
                var loaded = Task.Run(() => LoadAsync(write!, spec.Name, ddl, data, tables, replace, log, runId)).GetAwaiter().GetResult();
                output.WriteLine($"Loaded {loaded.Count} table(s), {loaded.Sum(t => t.Rows)} row(s).");
                output.Next("connection deploy");
            }
            catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }
            catch (LoadFailedException ex)
            {
                error.WriteLine($"Stopped at {ex.Table}: {ex.Reason}. The tables before it are loaded; tables from this one on are not (the statement log has the details).");
                return CliApp.ExitFindings;
            }
            return CliApp.ExitOk;
        }
    }

    private sealed class LoadFailedException(string table, string reason) : Exception(reason)
    {
        public string Table { get; } = table;
        public string Reason { get; } = reason;
    }

    private static async Task<List<SeededTable>> LoadAsync(LoginSettings write, string command, DdlGenerator ddl, SeededData data,
        List<(SeededTable Seeded, DeclaredModel Source, IReadOnlyList<NativeColumn> Columns)> tables, bool replace, IStatementLog log, Guid runId)
    {
        await using var gate = await MutationGate.OpenAsync(write, command, StatementKind.Ddl | StatementKind.Data, log, runId);
        var done = new List<SeededTable>();
        foreach (var (seeded, source, columns) in tables)
        {
            var (schema, name) = DdlGenerator.Split(source.Name);
            var id = "load:" + source.Name;
            try
            {
                CommandContext.Hooks?.Progress?.Invoke($"Loading {source.Name}");
                await gate.ExecuteAsync(GateStatement.FromPlanStep(id + ":schema", StatementKind.Ddl, ddl.CreateSchema(schema)));
                if (replace) await gate.ExecuteAsync(GateStatement.FromPlanStep(id + ":drop", StatementKind.Ddl, ddl.DropTableIfExists(schema, name)));
                await gate.ExecuteAsync(GateStatement.FromPlanStep(id + ":create", StatementKind.Ddl, ddl.CreateTable(schema, name, columns)));
                var batchNo = 0;
                foreach (var batch in data.Batches(seeded.Name, columns.Select(c => c.Name).ToList(), RowsPerStatement(columns.Count)))
                    await gate.ExecuteAsync(Insert($"{id}:rows:{++batchNo}", ddl, schema, name, source, columns, batch));
                done.Add(seeded);
            }
            catch (Exception ex) when (ex is not GateRefusedException)
            {
                // type and driver error number only: the same rule as everywhere (DESIGN.md 14.2)
                var number = ex is System.Data.Common.DbException { ErrorCode: var code } && code != 0 ? $" (error {code})" : "";
                throw new LoadFailedException(source.Name, ex.GetType().Name + number);
            }
        }
        return done;
    }

    internal static int RowsPerStatement(int columns) => Math.Max(1, Math.Min(MaxRowsPerStatement, MaxParametersPerStatement / Math.Max(1, columns)));

    internal static int Statements(long rows, int columns) => (int)((rows + RowsPerStatement(columns) - 1) / RowsPerStatement(columns));

    /// <summary>One INSERT of a batch of rows, a bound parameter for every value.</summary>
    internal static GateStatement Insert(string stepId, DdlGenerator ddl, string schema, string name, DeclaredModel source, IReadOnlyList<NativeColumn> columns, IReadOnlyList<object?[]> rows)
    {
        var types = source.Columns.Select(c => DbTypeOf(c.Type)).ToList();
        var parameters = new List<GateParameter>(rows.Count * columns.Count);
        var tuples = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var names = new List<string>(columns.Count);
            for (var i = 0; i < columns.Count; i++)
            {
                var p = "p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
                names.Add("@" + p);
                parameters.Add(new GateParameter(p, types[i], Convert(row[i], types[i])));
            }
            tuples.Add("(" + string.Join(", ", names) + ")");
        }
        var text = $"INSERT INTO {ddl.Qualified(schema, name)} ({string.Join(", ", columns.Select(c => ddl.QuoteIdentifier(c.Name)))}) VALUES\n{string.Join(",\n", tuples)};";
        return GateStatement.BulkInsert(stepId, text, parameters);
    }

    /// <summary>The driver type a value of a logical column type travels as. Chosen from the declared type, not the value, so a NULL still has the right type.</summary>
    internal static DbType DbTypeOf(string logicalType)
    {
        var t = LogicalTypes.Canonical(logicalType);
        if (t.StartsWith("VARCHAR", StringComparison.Ordinal)) return DbType.String;
        if (t.StartsWith("DECIMAL", StringComparison.Ordinal)) return DbType.Decimal;
        return t switch
        {
            "BIGINT" => DbType.Int64,
            "INTEGER" => DbType.Int32,
            "SMALLINT" or "TINYINT" => DbType.Int16,
            "DOUBLE" => DbType.Double,
            "FLOAT" => DbType.Single,
            "BOOLEAN" => DbType.Boolean,
            "DATE" => DbType.Date,
            "TIMESTAMP" => DbType.DateTime2,
            "TIME" => DbType.Time,
            "TIMESTAMP WITH TIME ZONE" => DbType.DateTimeOffset,
            "UUID" => DbType.Guid,
            "BLOB" => DbType.Binary,
            _ => DbType.String,
        };
    }

    internal static object? Convert(object? value, DbType type)
    {
        if (value == null) return null;
        return (type, value) switch
        {
            (DbType.Date, DateOnly d) => d.ToDateTime(TimeOnly.MinValue),
            (DbType.Time, TimeOnly t) => t.ToTimeSpan(),
            (DbType.Date or DbType.DateTime2, DateTime dt) => dt,
            (DbType.DateTimeOffset, DateTime dt) => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            (DbType.Int16, _) => System.Convert.ToInt16(value, CultureInfo.InvariantCulture),
            (DbType.Int32, _) => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
            (DbType.Int64, _) => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
            (DbType.Decimal, _) => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            (DbType.Double, _) => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
            (DbType.Single, _) => System.Convert.ToSingle(value, CultureInfo.InvariantCulture),
            (DbType.Boolean, _) => System.Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            (DbType.String, _) => System.Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => value,
        };
    }
}
