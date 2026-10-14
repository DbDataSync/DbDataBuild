using DbDataBuild.Cli;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>A file saved by an editor on Windows starts with a UTF-8 byte order mark. Every command must read it as the same file.</summary>
public class ByteOrderMarkTests
{
    private const string Bom = "﻿";

    private static string Project(bool bom)
    {
        var dir = NewProjectDir();
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: bom)); }
        Write("dbdatabuild.yml", "defaults: {connections: [sqlserver]}\n");
        Write("models/staging/orders.yml", "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        Write("models/marts/plain.yml", "name: marts.plain\nkind: view\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        Write("models/marts/plain.sql", "SELECT order_id FROM staging.orders\n");
        // a query file with a head: the head must still be seen as one
        Write("models/marts/headed.yml", "columns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        Write("models/marts/headed.sql", "CREATE VIEW marts.headed AS\nSELECT order_id FROM staging.orders\n");
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--project", dir], o, e);
        return (exit, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData("project compile")]
    [InlineData("project model update --check")]
    [InlineData("project compile --check")]
    public void A_project_whose_files_start_with_a_byte_order_mark_is_read_like_the_same_project_without_one(string command)
    {
        var without = Project(bom: false);
        Cli(without, "project", "compile");
        var withBom = Project(bom: true);
        Cli(withBom, "project", "compile");
        var a = Cli(without, command.Split(' '));
        var b = Cli(withBom, command.Split(' '));
        Assert.True(a.Exit == 0, $"the project without a mark is not valid: {a.Out}{a.Err}");
        Assert.True(b.Exit == 0, $"with a mark: {b.Out}{b.Err}");
        Assert.DoesNotContain("DDB-104", b.Err);                         // an unknown key `﻿name`
        Assert.DoesNotContain("DDB-105", b.Err);                         // a missing `name`
        Assert.DoesNotContain("headed", b.Err.Replace("marts.headed", ""));
    }

    [Fact]
    public void The_head_of_a_query_file_is_seen_when_the_file_has_a_mark()
    {
        var dir = Project(bom: true);
        var r = Cli(dir, "project", "model", "update", "--check");
        Assert.Equal(0, r.Exit);
        Assert.Contains("definition(s) in sync", r.Out);
    }
}
