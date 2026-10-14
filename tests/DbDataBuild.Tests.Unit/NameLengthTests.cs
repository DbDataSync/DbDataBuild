using DbDataBuild.Cli;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>DDB-241: a name the engine would cut (PostgreSQL, 63 bytes) or refuse (SQL Server, 128 characters).</summary>
public class NameLengthTests
{
    private static string Project(string connections, string column)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{connections}]}}\n");
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        Directory.CreateDirectory(Path.Combine(dir, "models/marts"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/m.yml"), $"name: marts.m\nkind: view\ncolumns:\n  - {{name: order_id, type: BIGINT, nullable: false}}\n  - {{name: \"{column}\", type: INTEGER}}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/m.sql"), $"SELECT order_id, 1 AS \"{column}\" FROM staging.orders\n");
        return dir;
    }

    private static string Validate(string dir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        CliApp.Run(["project", "compile", "--project", dir], o, e);
        return e.ToString();
    }

    [Fact]
    public void Sixty_four_bytes_are_too_long_for_PostgreSQL_and_fine_for_SQL_Server()
    {
        var name = new string('c', 64);
        Assert.Contains("error DDB-241", Validate(Project("postgres", name)));
        Assert.DoesNotContain("DDB-241", Validate(Project("sqlserver", name)));
        Assert.DoesNotContain("DDB-241", Validate(Project("postgres", new string('c', 63))));
    }

    [Fact]
    public void The_length_is_counted_in_bytes_on_PostgreSQL_and_in_characters_on_SQL_Server()
    {
        var accented = new string('é', 40);                      // 40 characters, 80 bytes
        Assert.Contains("80 bytes, and PostgreSQL keeps 63", Validate(Project("postgres", accented)));
        Assert.DoesNotContain("DDB-241", Validate(Project("sqlserver", accented)));
        Assert.Contains("129 characters, and SQL Server accepts 128", Validate(Project("sqlserver", new string('c', 129))));
        Assert.DoesNotContain("DDB-241", Validate(Project("sqlserver", new string('c', 128))));
    }

    [Fact]
    public void A_model_built_on_both_engines_is_checked_against_each_of_them()
    {
        var err = Validate(Project("sqlserver, postgres", new string('c', 70)));
        Assert.Contains("(connection `postgres`)", err);
        Assert.DoesNotContain("(connection `sqlserver`)", err);
    }
}
