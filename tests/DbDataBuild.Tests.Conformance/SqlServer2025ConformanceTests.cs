using DbDataBuild.Core;
using DuckDB.NET.Data;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

public partial class ApplyConformanceTests
{
    private const string NamesYaml = "name: staging.names\nkind: mapped\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: s, type: VARCHAR(50)}\n";
    private const string RegexYaml = "name: marts.regex_probe\nkind: full\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: replaced_first, type: VARCHAR(50)}\n  - {name: replaced_all, type: VARCHAR(50)}\n  - {name: group_one, type: VARCHAR(50)}\n  - {name: no_match, type: VARCHAR(50)}\n  - {name: whole, type: BOOLEAN}\n  - {name: starts_a, type: BOOLEAN}\n";
    private const string RegexSql = "SELECT id, regexp_replace(s, '[a-c]', 'x') AS replaced_first, regexp_replace(s, '[a-c]', 'x', 'g') AS replaced_all, regexp_extract(s, 'a(b)', 1) AS group_one, regexp_extract(s, 'zzz') AS no_match,\n       regexp_full_match(s, 'a.c') AS whole, regexp_matches(s, '^a') AS starts_a\nFROM staging.names\n";
    private static readonly string[] Names = ["abc", "ABC", "a,b,c", "", "xabbc"];

    /// <summary>
    /// SQL Server 2025 at its own compatibility level (170), told to the tool as `connections: sqlserver: {version: 17}`: the whole loop (render, plan, apply) writes the regular expression functions and the table
    /// holds what DuckDB computes. The same project at version 16 is the matrix's to refuse (unit tests: CliTests).
    /// </summary>
    [SkippableFact]
    public async Task A_project_at_version_17_loads_regular_expressions_on_SQL_Server_2025_and_gets_DuckDBs_rows()
    {
        Skip.If(EngineEnv.Get(EngineEnv.SqlServer2025) == null, $"Set {EngineEnv.SqlServer2025} (scripts/test-engines.sh up mssql2025).");
        var run = await SetUp("sqlserver", () => new SqlServerEngine(EngineEnv.SqlServer2025, 170, "sqlserver2025", 17), "connections:\n  sqlserver: { version: 17 }\n");
        await using var engine = run.Engine;
        try
        {
            Assert.Equal(170, int.Parse((await run.Engine.RowsAsync("SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()")).Single()));
            run.Write("models/staging/names.yml", NamesYaml);
            run.Write("models/marts/regex_probe.yml", RegexYaml);
            run.Write("models/marts/regex_probe.sql", RegexSql);
            await engine.ExecAsync("CREATE TABLE staging.names (id BIGINT NOT NULL, s NVARCHAR(50) COLLATE Latin1_General_100_BIN2)");
            await engine.ExecAsync("INSERT INTO staging.names VALUES " + string.Join(", ", Names.Select((n, i) => $"({i + 1}, N'{n}')")) + ", (6, NULL)");

            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "render --write");
            var rendered = File.ReadAllText(Path.Combine(run.Dir, "rendered", "sqlserver", "marts.regex_probe", "load.default.sql"));
            Assert.Contains("REGEXP_SUBSTR(", rendered);
            Assert.Contains("REGEXP_REPLACE(", rendered);
            Assert.DoesNotContain("ddb_regexp", rendered, StringComparison.OrdinalIgnoreCase);
            Ok(run.Cli("connection", "status"), "check");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");

            // DuckDB's answer to the same query over the same rows
            using var duck = new DuckDBConnection("DataSource=:memory:");
            duck.Open();
            void Exec(string sql) { using var c = duck.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
            Exec("CREATE SCHEMA staging");
            Exec("CREATE TABLE staging.names (id BIGINT, s VARCHAR)");
            Exec("INSERT INTO staging.names VALUES " + string.Join(", ", Names.Select((n, i) => $"({i + 1}, '{n}')")) + ", (6, NULL)");
            var expected = new List<string>();
            using (var c = duck.CreateCommand())
            {
                c.CommandText = RegexSql;
                using var r = c.ExecuteReader();
                while (r.Read()) expected.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : r.GetValue(i) is bool b ? (b ? "true" : "false") : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            }
            expected.Sort(StringComparer.Ordinal);
            var actual = (await run.Engine.RowsAsync("SELECT id, replaced_first, replaced_all, group_one, no_match, whole, starts_a FROM marts.regex_probe")).Select(x => x.Replace("True", "true").Replace("False", "false")).ToList();
            Assert.Equal(expected, actual);
        }
        finally { try { Directory.Delete(run.Dir, true); } catch (IOException) { } }
    }
}
