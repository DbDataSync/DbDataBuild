using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Models whose query files start with a head (`CREATE TABLE schema_name.object_name WITH (...) AS`, `CREATE VIEW`) built and reloaded on the real engines.</summary>
[Trait("Group", "apply")]
public class QueryHeadConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private static string Config(string name) =>
        $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name} }}\n" + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "");

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_table_a_view_and_an_incremental_table_declared_by_their_heads_build_and_reload(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-head-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        try
        {
            await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA IF NOT EXISTS src" : "IF SCHEMA_ID('src') IS NULL EXEC('CREATE SCHEMA src')");
            await engine.ExecAsync("DROP TABLE IF EXISTS src.orders");
            await engine.ExecAsync("CREATE TABLE src.orders (order_id bigint NOT NULL, amount decimal(18,2) NOT NULL)");
            await engine.ExecAsync("INSERT INTO src.orders VALUES (1, 10.00), (2, 20.00)");

            Write("dbdatabuild.yml", Config(name));
            Write("models/src/orders.yml", "name: src.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n");
            const string cols = "grain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n";
            // no `name:` and no `kind:` in the definitions: the heads say them
            Write("models/marts/plain.yml", cols);
            Write("models/marts/plain.sql", "CREATE TABLE marts.plain AS\nSELECT order_id, amount FROM src.orders\n");
            Write("models/marts/big.yml", cols);
            Write("models/marts/big.sql", "CREATE VIEW marts.big AS\nSELECT order_id, amount FROM src.orders WHERE amount > 15\n");
            Write("models/marts/inc.yml", cols);
            Write("models/marts/inc.sql", "CREATE TABLE marts.inc\nWITH (kind = 'incremental_by_unique_key', unique_key = (order_id))\nAS\nSELECT order_id, amount FROM src.orders\n");

            Ok(Cli("connection", "init", name, "--apply"), "init");
            Ok(Cli("project", "compile"), "render");
            var first = Cli("connection", "deploy", name, "--write-plan");
            Ok(first, "plan");
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(first.Out)), "apply");
            string Sql(string table) => $"SELECT CAST(order_id AS VARCHAR(10)) + '|' + CAST(amount AS VARCHAR(20)) FROM {table}".Replace(" + '|' + ", name == "postgres" ? " || '|' || " : " + '|' + ");
            Assert.Equal(["1|10.00", "2|20.00"], (await engine.RowsAsync(Sql("marts.plain") + " ORDER BY 1")));
            Assert.Equal(["2|20.00"], await engine.RowsAsync(Sql("marts.big")));
            Assert.Equal(["1|10.00", "2|20.00"], (await engine.RowsAsync(Sql("marts.inc") + " ORDER BY 1")));

            // the source changes: the plain table is replaced, the incremental one merges by the key the head gave
            await engine.ExecAsync("UPDATE src.orders SET amount = 25.00 WHERE order_id = 1");
            await engine.ExecAsync("INSERT INTO src.orders VALUES (3, 30.00)");
            var second = Cli("connection", "deploy", name, "--write-plan");
            Ok(second, "second plan");
            Ok(Cli("connection", "deploy", "--apply-plan", PlanOf(second.Out)), "second apply");
            Assert.Equal(["1|25.00", "2|20.00", "3|30.00"], (await engine.RowsAsync(Sql("marts.plain") + " ORDER BY 1")));
            Assert.Equal(["1|25.00", "2|20.00", "3|30.00"], (await engine.RowsAsync(Sql("marts.inc") + " ORDER BY 1")));
            Assert.Equal(["1|25.00", "2|20.00", "3|30.00"], await engine.RowsAsync(Sql("marts.big") + " ORDER BY 1"));                  // the view reads the source as it is now
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
