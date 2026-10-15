using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The head of a query file: `CREATE TABLE schema_name.object_name WITH (...) AS query` says what the file builds and how it reloads.</summary>
public class QueryHeadTests
{
    private static QueryHead Head(string sql)
    {
        var r = QueryHeadParser.Parse("m.sql", sql);
        Assert.Empty(r.Problems);
        return r.Head!;
    }

    private static string Problem(string sql) => string.Join("\n", QueryHeadParser.Parse("models/m.sql", sql).Problems.Select(p => $"{p.Code} {p.Location.Line}:{p.Location.Column} {p.Found}"));

    [Fact]
    public void A_head_names_the_object_whether_it_is_a_table_or_a_view_and_its_options()
    {
        var head = Head("-- the orders\nCREATE TABLE marts.fct_orders\nWITH (kind = 'incremental_by_unique_key', unique_key = (order_id, \"line no\"))\nAS\nSELECT 1\n");
        Assert.Equal(("marts.fct_orders", false, "incremental_by_unique_key", true), (head.Name, head.IsView, head.Kind, head.KindIsExplicit));
        Assert.Equal(["order_id", "line no"], head.Property("unique_key")!.Value.Items);
        Assert.Equal(2, head.Line);
        var timed = Head("create table marts.daily with (KIND = incremental_by_time_range, time_column = day, lookback = '3 days') as select 1");
        Assert.Equal(("incremental_by_time_range", "day", "3 days"), (timed.Kind, timed.Property("time_column")!.Value.Text, timed.Property("lookback")!.Value.Text));
        var view = Head("CREATE VIEW marts.v AS SELECT 1");
        Assert.Equal((true, "view", true), (view.IsView, view.Kind, view.KindIsExplicit));
        var plain = Head("CREATE TABLE \"my schema\".t AS SELECT 1");
        Assert.Equal(("my schema.t", "full", false), (plain.Name, plain.Kind, plain.KindIsExplicit));          // a plain table says "a table", not a kind
    }

    [Fact]
    public void The_query_after_as_is_passed_on_as_written_and_a_file_with_no_head_is_only_a_query()
    {
        var r = QueryHeadParser.Parse("m.sql", "CREATE TABLE a.b AS\n  SELECT  'x AS y' AS c -- AS\n  FROM t;\n");
        Assert.Equal("SELECT  'x AS y' AS c -- AS\n  FROM t;\n", r.Body);
        var bare = "-- comment\nSELECT 1 AS n\n";
        Assert.Equal((null, bare), (QueryHeadParser.Parse("m.sql", bare).Head, QueryHeadParser.Parse("m.sql", bare).Body));
        Assert.Null(QueryHeadParser.Parse("m.sql", "WITH x AS (SELECT 1) SELECT * FROM x").Head);
    }

    [Theory]
    [InlineData("CREATE OR REPLACE TABLE a.b AS SELECT 1", "DDB-238 1:10", "`CREATE OR ...` is not used")]
    [InlineData("CREATE TABLE b AS SELECT 1", "DDB-238 1:14", "the name `b` has no schema")]
    [InlineData("CREATE INDEX a.b AS SELECT 1", "DDB-238 1:13", "a head starts `CREATE TABLE` or `CREATE VIEW`")]
    [InlineData("CREATE TABLE a.b WITH kind = 'full' AS SELECT 1", "DDB-238 1:23", "`WITH` is followed by a list in parentheses")]
    [InlineData("CREATE TABLE a.b WITH (kind 'full') AS SELECT 1", "DDB-238 1:29", "`kind` is followed by `=`")]
    [InlineData("CREATE TABLE a.b WITH (kind = ) AS SELECT 1", "DDB-238 1:31", "the value of `kind`")]
    [InlineData("CREATE TABLE a.b WITH (kind = 'full' strategy) AS SELECT 1", "DDB-238 1:38", "`,` or `)` is expected")]
    [InlineData("CREATE TABLE a.b SELECT 1", "DDB-238 1:18", "`AS` and the query are expected")]
    [InlineData("CREATE TABLE a.b AS\n  ", "DDB-238 1:20", "the query after `AS` is missing")]
    [InlineData("CREATE TABLE a.b WITH (colour = 'red') AS SELECT 1", "DDB-238 1:24", "`colour` is not an option of a head")]
    [InlineData("CREATE TABLE a.b WITH (kind = 'view') AS SELECT 1", "DDB-238 1:24", "`kind` is `view`")]
    [InlineData("CREATE TABLE a.b WITH (kind = 'full', kind = 'full') AS SELECT 1", "DDB-238 1:39", "`kind` is given twice")]
    [InlineData("CREATE VIEW a.b WITH (lookback = '1 day') AS SELECT 1", "DDB-238 1:23", "a view has no options")]
    [InlineData("CREATE TABLE a.b WITH (time_column = (a, b)) AS SELECT 1", "DDB-238 1:24", "`time_column` is a single value")]
    public void A_head_that_cannot_be_read_says_where_and_why(string sql, string where, string why)
    {
        var problem = Problem(sql);
        Assert.StartsWith(where, problem);
        Assert.Contains(why, problem);
    }

    // ---- in a project ----

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private const string Orders = "name: staging.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n";

    private static string Project(string sql, string yml, string config = "defaults: {connections: [sqlserver]}\ntracking: none\n")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config);
        Write(dir, "models/staging/orders.yml", Orders);
        Write(dir, "models/marts/fct.yml", yml);
        Write(dir, "models/marts/fct.sql", sql);
        return dir;
    }

    private const string Columns = "grain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n";

    private static string Validate(string dir) => string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Location.File}:{d.Location.Line} {d.Found}"));

    [Fact]
    public void The_head_gives_the_model_its_name_and_kind_and_the_definition_file_keeps_the_rest()
    {
        var dir = Project("CREATE TABLE marts.fct\nWITH (kind = 'incremental_by_unique_key', unique_key = (order_id))\nAS\nSELECT order_id, amount FROM staging.orders\n", Columns);
        Assert.Equal("", Validate(dir));
        var source = ProjectValidator.Validate(dir).BuiltModels.Single();
        Assert.Equal(("marts.fct", ModelKinds.IncrementalByUniqueKey, "order_id"), (source.Definition.Name, source.Definition.Kind, string.Join(",", source.Definition.UniqueKey)));
        Assert.Equal("SELECT order_id, amount FROM staging.orders\n", source.ReadQuery(dir, ProjectConfigLoader.LoadFromProject(dir, new List<Diagnostic>())));    // the query, without the head
        var render = new StringWriter(); var err = new StringWriter();
        Assert.Equal(0, CliApp.Run(["project", "compile", "--project", dir], render, err, environment: _ => null));
        Assert.Contains("FROM staging.orders", File.ReadAllText(Path.Combine(dir, "rendered", "lowered", "marts.fct", "lowered.sql")));
        var script = File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "marts.fct", "load.default.sql"));
        Assert.Contains("order_id", script);                                                     // the load is by the unique key the head gave
        Assert.Contains("delete_insert_by_key", script);
    }

    [Fact]
    public void A_view_head_makes_a_view_and_a_plain_table_is_a_full_replace_wherever_a_folder_default_says_otherwise()
    {
        var view = Project("CREATE VIEW marts.fct AS SELECT order_id, amount FROM staging.orders", Columns);
        Assert.Equal("", Validate(view));
        Assert.Equal(ModelKinds.View, ProjectValidator.Validate(view).BuiltModels.Single().Definition.Kind);

        var dir = Project("CREATE TABLE marts.fct AS SELECT order_id, amount FROM staging.orders", Columns);
        Write(dir, "models/marts/_dbdatabuild.yml", "defaults:\n  kind: {incremental_by_unique_key: {unique_key: [order_id]}}\n");
        Assert.Equal("", Validate(dir));
        var source = ProjectValidator.Validate(dir).BuiltModels.Single();
        Assert.Equal((ModelKinds.Full, 0), (source.Definition.Kind, source.Definition.UniqueKey.Count));              // the head wins over the folder's kind, and nothing of it is left
    }

    [Fact]
    public void The_kind_is_said_in_one_place_and_the_name_is_the_same_in_both()
    {
        var both = Project("CREATE VIEW marts.fct AS SELECT order_id, amount FROM staging.orders", "name: marts.fct\nkind: view\n" + Columns);
        Assert.Contains("DDB-238 models/marts/fct.sql:1 the kind is said in the head (CREATE VIEW) and again in `models/marts/fct.yml`", Validate(both));

        var named = Project("CREATE TABLE marts.fct AS SELECT order_id, amount FROM staging.orders", "name: marts.other\n" + Columns);
        Assert.Contains("DDB-107 models/marts/fct.yml:1 name is `marts.other`, but the head of `models/marts/fct.sql` says `marts.fct`", Validate(named));

        var same = Project("CREATE TABLE marts.fct AS SELECT order_id, amount FROM staging.orders", "name: marts.fct\nkind: {incremental_by_unique_key: {unique_key: [order_id]}}\n" + Columns);
        Assert.Equal("", Validate(same));                                                                                   // a plain CREATE TABLE leaves the kind to the definition
        Assert.Equal(ModelKinds.IncrementalByUniqueKey, ProjectValidator.Validate(same).BuiltModels.Single().Definition.Kind);

        var contradiction = Project("CREATE TABLE marts.fct AS SELECT order_id, amount FROM staging.orders", "kind: view\n" + Columns);
        Assert.Contains("the head says `CREATE TABLE`, and `models/marts/fct.yml` says the kind is `view`", Validate(contradiction));
    }

    [Fact]
    public void A_wrong_option_for_the_kind_is_reported_at_the_head_and_a_bad_head_stops_only_its_model()
    {
        var dir = Project("CREATE TABLE marts.fct\nWITH (kind = 'full', unique_key = (order_id))\nAS SELECT order_id, amount FROM staging.orders", Columns);
        var text = Validate(dir);
        Assert.Contains("models/marts/fct.sql:2", text);                                                                    // the line of `unique_key` in the head, not in a YAML file
        Assert.Contains("unique_key", text);

        var broken = Project("CREATE TABLE fct AS SELECT 1", Columns);
        Assert.Contains("DDB-238 models/marts/fct.sql:1", Validate(broken));
    }

    [Fact]
    public void The_layout_check_applies_to_the_name_the_head_gives()
    {
        var dir = Project("CREATE TABLE reports.fct AS SELECT order_id, amount FROM staging.orders", Columns);
        Assert.Contains("DDB-107", Validate(dir));                                                                          // the default layout: the path is the name
        var free = Project("CREATE TABLE reports.fct AS SELECT order_id, amount FROM staging.orders", Columns, "defaults: {connections: [sqlserver]}\ntracking: none\nmodel_layout: none\n");
        Assert.Equal("", Validate(free));
        Assert.Equal("reports.fct", ProjectValidator.Validate(free).BuiltModels.Single().Definition.Name);
    }
}
