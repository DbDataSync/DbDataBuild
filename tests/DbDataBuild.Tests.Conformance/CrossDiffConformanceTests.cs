using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// `diff --against-connection` between two real engines (DESIGN.md 9.10.1): the same logical rows are loaded into SQL Server and PostgreSQL with different native types, and the comparison, which cannot be a
/// join, goes through digests each engine computes of a canonical text form. Every kind of value the canonical form covers is in the table, and the rows that differ differ in a way the digests must see.
/// </summary>
[Trait("Group", "apply")]
public class CrossDiffConformanceTests
{
    public static TheoryData<string, string> Directions => new() { { "sqlserver", "postgres" }, { "postgres", "sqlserver" } };

    private static string Create(string engine) => engine == "postgres"
        ? "CREATE TABLE xd.items (id BIGINT NOT NULL, n INTEGER, price NUMERIC(12,2), code VARCHAR(40), flag BOOLEAN, born DATE, seen TIMESTAMP(6), at TIMESTAMPTZ, guid UUID, payload BYTEA, tm TIME(6), fixed CHAR(5))"
        : "CREATE TABLE xd.items (id BIGINT NOT NULL, n INT, price DECIMAL(18,3), code NVARCHAR(40), flag BIT, born DATE, seen DATETIME2(6), at DATETIMEOFFSET(3), guid UNIQUEIDENTIFIER, payload VARBINARY(20), tm TIME(6), fixed CHAR(8))";

    private static string Rows(string engine, bool left)
    {
        var pg = engine == "postgres";
        // row 1: everything set; 2: price differs on the right; 3: NULL on the left and '' on the right (code); 4: NULL everywhere; 5 left only; 6 right only
        var price2 = left ? "20.500" : "21.50";
        var code3 = left ? "NULL" : "N''";
        if (pg) code3 = code3.Replace("N''", "''");
        string Row(string id, string n, string price, string code, string flag, string born, string seen, string at, string guid, string payload, string tm, string fixedText) =>
            $"({id}, {n}, {price}, {code}, {flag}, {born}, {seen}, {at}, {guid}, {payload}, {tm}, {fixedText})";
        var bit1 = pg ? "TRUE" : "1"; var bit0 = pg ? "FALSE" : "0";
        string Str(string s) => pg ? $"'{s}'" : $"N'{s}'";
        string Hex(string h) => pg ? $"'\\x{h}'::bytea" : $"0x{h}";
        var at = pg ? "'2024-03-10 08:30:00.123+02'" : "'2024-03-10 08:30:00.123 +02:00'";
        var rows = new List<string>
        {
            Row("1", "100", "12345.60", Str("café ☕"), bit1, "'2024-02-29'", "'2024-02-29 13:14:15.123456'", at, "'0e984725-c51c-4bf4-9960-e1c80e27aba0'", Hex("00FF10"), "'13:14:15.250000'", "'ab'"),
            Row("2", "-2147483648", price2, Str("Straße"), bit0, "'0001-01-01'", "'1999-12-31 23:59:59.999999'", at, "'0e984725-c51c-4bf4-9960-e1c80e27aba1'", Hex("DEADBEEF"), "'00:00:00'", "'cd'"),
            Row("3", "7", "0", code3, bit1, "'9999-12-31'", "'2000-01-01 00:00:00'", at, "'0e984725-c51c-4bf4-9960-e1c80e27aba2'", Hex("01"), "'23:59:59.999999'", "'ef'"),
            Row("4", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL", "NULL"),
        };
        rows.Add(left ? Row("5", "5", "5", Str("only left"), bit1, "'2020-01-01'", "'2020-01-01 01:02:03'", at, "'0e984725-c51c-4bf4-9960-e1c80e27aba5'", Hex("05"), "'01:02:03'", "'l'")
                      : Row("6", "6", "6", Str("only right"), bit0, "'2020-01-02'", "'2020-01-02 01:02:03'", at, "'0e984725-c51c-4bf4-9960-e1c80e27aba6'", Hex("06"), "'01:02:03'", "'r'"));
        return "INSERT INTO xd.items VALUES " + string.Join(", ", rows);
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task A_table_is_compared_with_the_same_table_on_another_engine_by_digests(string leftName, string rightName)
    {
        var left = EngineEnv.Require(leftName);
        var right = EngineEnv.Require(rightName);
        await left.StartAsync();
        await right.StartAsync();
        await using var _l = left; await using var _r = right;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-xdiff-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models/xd"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{leftName}]}}\ntracking: {{ connection: {leftName} }}\n");
            File.WriteAllText(Path.Combine(dir, "models/xd/items.yml"), $"name: xd.items\nkind: mapped\nconnections=: [{leftName}, {rightName}]\ngrain: [id]\ncolumns:\n  - {{name: id, type: BIGINT, nullable: false}}\n");
            foreach (var (engine, isLeft) in new[] { (left, true), (right, false) })
            {
                await engine.ExecAsync(engine.Name == "postgres" ? "CREATE SCHEMA xd" : "EXEC('CREATE SCHEMA xd')");
                await engine.ExecAsync(Create(engine.Name));
                await engine.ExecAsync(Rows(engine.Name, isLeft));
            }
            Func<string, string?> env = v =>
                v == LoginSettings.VariableName(left.Name, Login.Read) ? left.ConnectionString
                : v == LoginSettings.VariableName(right.Name, Login.Read) ? right.ConnectionString : null;
            (int Exit, string Out, string Err) Cli(params string[] args)
            {
                var o = new StringWriter(); var e = new StringWriter();
                var exit = CliApp.Run([.. args, "--project", dir], o, e, environment: env);
                return (exit, o.ToString(), e.ToString());
            }

            var counts = Cli("connection", "compare", leftName, "xd.items", "--against-connection", rightName, "--exclude-columns", "fixed", "--format", "json");
            Assert.True(counts.Exit == 1, counts.Out + counts.Err);
            var data = JsonNode.Parse(counts.Out)!["data"]!;
            Assert.Equal(rightName, (string)data["against_connection"]!);
            Assert.Equal((5, 5), ((int)data["left"]!["rows"]!, (int)data["right"]!["rows"]!));
            Assert.Equal((1, 1, 4, 2), ((int)data["rows"]!["only_left"]!, (int)data["rows"]!["only_right"]!, (int)data["rows"]!["matched"]!, (int)data["rows"]!["differing"]!));
            var byColumn = data["rows"]!["differing_by_column"]!.AsObject();
            Assert.Equal(["code", "price"], byColumn.Select(c => c.Key).Order());
            Assert.Empty(data["schema"]!["not_compared"]!.AsArray());
            Assert.DoesNotContain(data["schema"]!["type_differences"]!.AsArray(), d => (string?)d!["column"] is not ("price" or "code"));   // `int` and `integer` are no difference; the price scales and text lengths are
            Assert.Null(data["samples"]);
            Assert.DoesNotContain("café", counts.Out);
            Assert.DoesNotContain("Straße", counts.Out);

            var shown = Cli("connection", "compare", leftName, "xd.items", "--against-connection", rightName, "--exclude-columns", "fixed", "--show-values", "--limit", "5", "--format", "json");
            var samples = JsonNode.Parse(shown.Out)!["data"]!["samples"]!;
            Assert.Equal("5", (string)samples["only_left"]![0]!["key"]!["id"]!);
            Assert.Equal("only left", (string)samples["only_left"]![0]!["values"]!["code"]!);
            Assert.Equal("6", (string)samples["only_right"]![0]!["key"]!["id"]!);
            var differing = samples["differing"]!.AsArray().OrderBy(d => (string)d!["key"]!["id"]!).ToList();
            Assert.Equal(["2", "3"], differing.Select(d => (string)d!["key"]!["id"]!));
            Assert.Equal(("20.500", "21.500"), ((string)differing[0]!["columns"]!["price"]!["left"]!, (string)differing[0]!["columns"]!["price"]!["right"]!));
            Assert.Null((string?)differing[1]!["columns"]!["code"]!["left"]);
            Assert.Equal("", (string)differing[1]!["columns"]!["code"]!["right"]!);

            // the same rows on both sides are identical: restrict the key to the rows that match, by making the right equal the left
            await right.ExecAsync("DELETE FROM xd.items WHERE id = 6");
            await left.ExecAsync("DELETE FROM xd.items WHERE id = 5");
            await right.ExecAsync("UPDATE xd.items SET price = 20.50 WHERE id = 2");
            await right.ExecAsync("UPDATE xd.items SET code = NULL WHERE id = 3");
            await left.ExecAsync("UPDATE xd.items SET price = 20.50 WHERE id = 2");
            var same = Cli("connection", "compare", leftName, "xd.items", "--against-connection", rightName);
            Assert.True(same.Exit == 0, same.Out + same.Err);
            Assert.Contains("The tables are identical", same.Out);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [SkippableFact]
    public async Task When_too_many_rows_differ_to_list_the_result_stops_at_the_buckets_and_names_the_columns()
    {
        var left = EngineEnv.Require("sqlserver");
        var right = EngineEnv.Require("postgres");
        await left.StartAsync();
        await right.StartAsync();
        await using var _l = left; await using var _r = right;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-xdiff-partial-" + Guid.NewGuid().ToString("N"));
        var saved = CrossDiffer.MaxDrillRows;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "models/xd"));
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\n");
            File.WriteAllText(Path.Combine(dir, "models/xd/t.yml"), "name: xd.t\nkind: mapped\nconnections=: [sqlserver, postgres]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
            await left.ExecAsync("EXEC('CREATE SCHEMA xd')");
            await left.ExecAsync("CREATE TABLE xd.t (id BIGINT NOT NULL, a INT, code NVARCHAR(20))");
            await left.ExecAsync("INSERT INTO xd.t SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 1, N'same' FROM sys.all_objects a CROSS JOIN sys.all_objects b");
            await right.ExecAsync("CREATE SCHEMA xd");
            await right.ExecAsync("CREATE TABLE xd.t (id BIGINT NOT NULL, a INT, code VARCHAR(20))");
            await right.ExecAsync("INSERT INTO xd.t SELECT g, 2, 'same' FROM generate_series(1, 5000) g");         // every row differs in `a`
            Func<string, string?> env = v => v == LoginSettings.VariableName("sqlserver", Login.Read) ? left.ConnectionString : v == LoginSettings.VariableName("postgres", Login.Read) ? right.ConnectionString : null;
            (int Exit, string Out) Run(params string[] extra)
            {
                var o = new StringWriter(); var e = new StringWriter();
                var exit = CliApp.Run(["connection", "compare", "sqlserver", "xd.t", "--project", dir, "--against-connection", "postgres", "--format", "json", .. extra], o, e, environment: env);
                return (exit, o.ToString());
            }
            CrossDiffer.MaxDrillRows = 1000;
            var partial = Run();
            Assert.Equal(1, partial.Exit);
            var rows = JsonNode.Parse(partial.Out)!["data"]!["rows"]!;
            Assert.Equal(["a"], rows["partial"]!["columns_differing"]!.AsArray().Select(x => (string)x!));
            Assert.Equal(5000, (long)rows["partial"]!["rows_in_differing_buckets"]!);
            Assert.True((int)rows["partial"]!["buckets_differing"]! > 0);
            Assert.False((bool)JsonNode.Parse(partial.Out)!["data"]!["identical"]!);

            // leaving the differing column out compares the rest, row by row
            var narrowed = Run("--exclude-columns", "a");
            Assert.Equal(0, narrowed.Exit);
            Assert.Null(JsonNode.Parse(narrowed.Out)!["data"]!["rows"]!["partial"]);
        }
        finally { CrossDiffer.MaxDrillRows = saved; if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
