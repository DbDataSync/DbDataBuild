using System.Data;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Tests.Unit;

public class LoadSeedsTests
{
    private static (int Exit, string Out, string Err) Run(Func<string, string?>? env, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: env ?? (_ => null)), o.ToString(), e.ToString());
    }

    private static string NewStarter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-load-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, Run(null, "project", "create", "starter", dir).Exit);
        return dir;
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(6, 333)]
    [InlineData(12, 166)]
    [InlineData(2500, 1)]
    public void A_batch_never_passes_the_parameter_or_row_limit_of_SQL_Server(int columns, int rows)
    {
        Assert.Equal(rows, LoadSeedsCommand.RowsPerStatement(columns));
        Assert.True(LoadSeedsCommand.RowsPerStatement(columns) * columns <= Math.Max(columns, LoadSeedsCommand.MaxParametersPerStatement));
    }

    [Theory]
    [InlineData("INTEGER", DbType.Int32)]
    [InlineData("BIGINT", DbType.Int64)]
    [InlineData("DECIMAL(10, 2)", DbType.Decimal)]
    [InlineData("VARCHAR(20)", DbType.String)]
    [InlineData("VARCHAR", DbType.String)]
    [InlineData("DATE", DbType.Date)]
    [InlineData("TIMESTAMP", DbType.DateTime2)]
    [InlineData("BOOLEAN", DbType.Boolean)]
    public void A_value_travels_as_the_type_its_column_declares(string logical, DbType expected) => Assert.Equal(expected, LoadSeedsCommand.DbTypeOf(logical));

    [Fact]
    public void Values_are_converted_to_what_the_driver_takes_and_null_stays_null()
    {
        Assert.Equal(new DateTime(2024, 3, 5), LoadSeedsCommand.Convert(new DateOnly(2024, 3, 5), DbType.Date));
        Assert.Equal(12.5m, LoadSeedsCommand.Convert(12.5m, DbType.Decimal));
        Assert.Equal((short)7, LoadSeedsCommand.Convert(7, DbType.Int16));
        Assert.Null(LoadSeedsCommand.Convert(null, DbType.Int32));
    }

    [Theory]
    [InlineData("sqlserver", "[raw].[customers]", "[id], [name]")]
    [InlineData("postgres", "\"raw\".\"customers\"", "\"id\", \"name\"")]
    public void An_insert_binds_every_value_and_puts_none_in_the_text(string target, string table, string columns)
    {
        var config = ProjectConfigLoader.Load("defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS, postgres: C }\n", "dbdatabuild.yml", []);
        var ddl = TargetRegistry.Get(target).CreateDdl(config!);
        var source = new DeclaredModel("raw.customers", [new ColumnDefinition("id", "INTEGER", false), new ColumnDefinition("name", "VARCHAR(20)")], []);
        var native = source.Columns.Select(c => ddl.Map(source.Name, c)).ToList();
        var statement = LoadSeedsCommand.Insert("s", ddl, "raw", "customers", source, native, [[1, "Ann'; DROP TABLE x;--"], [2, null]]);
        Assert.Equal($"INSERT INTO {table} ({columns}) VALUES\n(@p0, @p1),\n(@p2, @p3);", statement.Text);
        Assert.DoesNotContain("DROP", statement.Text);
        Assert.Equal(4, statement.Parameters.Count);
        Assert.Equal(DbType.String, statement.Parameters[3].Type);
        Assert.Null(statement.Parameters[3].Value);
        Assert.True(statement.BulkValues);
    }

    [Fact]
    public void Without_apply_it_prints_what_it_would_do_and_connects_to_nothing()
    {
        var dir = NewStarter();
        try
        {
            var (exit, output, err) = Run(null, "connection", "seed", "--project", dir);
            Assert.True(exit == 0, output + err);
            Assert.Contains("login: none (not applying)", output);
            Assert.Contains("CREATE TABLE [raw].[customers]", output);
            Assert.Contains("Nothing was executed", output);
            Assert.False(Directory.Exists(Path.Combine(dir, ".dbdatabuild", "statement-log")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_needs_the_write_login_and_does_not_fall_back_to_the_read_login()
    {
        var dir = NewStarter();
        try
        {
            var (exit, _, err) = Run(v => v == "DBDATABUILD_SQLSERVER_READ" ? "Server=x" : null, "connection", "seed", "--project", dir, "--apply");
            Assert.NotEqual(0, exit);
            Assert.Contains("DBDATABUILD_SQLSERVER_WRITE", err);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_replace_shows_the_drop_in_the_preview()
    {
        var dir = NewStarter();
        try
        {
            var (_, output, _) = Run(null, "connection", "seed", "--project", dir, "--replace");
            Assert.Contains("DROP TABLE IF EXISTS [raw].[customers];", output);
            var (_, without, _) = Run(null, "connection", "seed", "--project", dir);
            Assert.DoesNotContain("DROP TABLE", without);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
