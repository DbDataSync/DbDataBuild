using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Planning;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>A copy as a load of a refresh (docs/research/refresh-plans.md, section 15): its load plan carries the staging table and one read per origin, it is a member of the plans it declares (and of `default` when it declares none), and it comes before what reads it.</summary>
public class RefreshCopyTests
{
    private const string Config = "connections:\n  east: { engine: postgres, parameters: { shop: E } }\n  west: { engine: sqlserver, parameters: { shop: W } }\n  wh: { engine: sqlserver }\ndefaults:\n  connections: [wh]\nrefresh:\n  plans:\n    hourly: {}\n";
    private const string Orders = "name: pos.orders\nkind: mapped\nconnections=: [east, west]\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: ordered_on, type: DATE, nullable: false}\n  - {name: amount, type: \"DECIMAL(12, 2)\"}\n";

    private static void Write(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private static string Project(string copyYaml)
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", Config);
        Write(dir, "models/pos/orders.yml", Orders);
        Write(dir, "models/warehouse/orders.yml", copyYaml);
        Write(dir, "models/marts/daily.yml", "name: marts.daily\nkind: full\ncolumns:\n  - {name: d, type: DATE, nullable: false}\n  - {name: n, type: BIGINT}\nrefresh: { hourly: default }\n");
        Write(dir, "models/marts/daily.sql", "SELECT ordered_on AS d, COUNT(*) AS n FROM warehouse.orders GROUP BY ordered_on\n");
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        return dir;
    }

    private static LoadsFile Loads(string dir)
    {
        var diags = new List<Diagnostic>();
        var file = LoadsDocument.Parse(File.ReadAllText(Path.Combine(dir, "rendered", "wh", LoadsDocument.FileName)), "loads.yml", diags);
        Assert.True(file != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return file!;
    }

    private static RefreshPlanFile Plan(string dir, string name)
    {
        var diags = new List<Diagnostic>();
        var plan = RefreshPlanDocument.Parse(File.ReadAllText(Path.Combine(dir, "rendered", "wh", "refresh", name + ".yml")), name + ".yml", diags);
        Assert.True(plan != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return plan!;
    }

    [Fact]
    public void A_copy_from_two_shops_is_a_load_plan_with_the_staging_table_and_a_read_per_origin_in_the_origins_own_dialect()
    {
        var dir = Project("name: warehouse.orders\nkind:\n  copy:\n    from: pos.orders\n    slice: {column: shop, value: \"${origin.shop}\", type: \"VARCHAR(4)\"}\n    unique_key: [order_id]\n    watermark: {column: ordered_on, lookback: 7 days}\nrefresh: { hourly: default }\n");
        var file = Loads(dir);
        Assert.Empty(file.Excluded);
        var copy = file.Loads.Single(l => l.Model == "warehouse.orders").Copy!;
        Assert.Equal("dbdatabuild.stg_warehouse__orders", copy.Staging);
        Assert.Contains("CREATE TABLE", copy.CreateStaging, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stg_warehouse__orders", copy.DropStaging);
        Assert.Equal(["order_id", "ordered_on", "amount", "shop"], copy.Columns.Select(c => c.Name));
        Assert.Equal(("ordered_on", "DATE", "7 days"), (copy.Watermark!.Column, copy.Watermark.Type, copy.Watermark.Lookback));
        Assert.Equal(["east", "west"], copy.Origins.Select(o => o.Connection));
        var (east, west) = (copy.Origins[0], copy.Origins[1]);
        Assert.Equal("postgres", east.Engine);
        Assert.Equal("sqlserver", west.Engine);
        Assert.Equal("SELECT \"order_id\", \"ordered_on\", \"amount\" FROM \"pos\".\"orders\"", east.ReadText);        // quoted as PostgreSQL does; the added slice column is written by the tool, not read
        Assert.Equal("SELECT [order_id], [ordered_on], [amount] FROM [pos].[orders]", west.ReadText);
        Assert.EndsWith("WHERE \"ordered_on\" >= @watermark", east.ReadWithWatermark);
        Assert.EndsWith("WHERE [ordered_on] >= @watermark", west.ReadWithWatermark);
        Assert.Equal((("shop", "E", true), ("shop", "W", true)), ((east.SliceColumn!, east.SliceValue!, east.SliceAdded), (west.SliceColumn!, west.SliceValue!, west.SliceAdded)));
        // the same text twice is the same file: the copy's part of the load plan is deterministic
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "rendered", "wh", LoadsDocument.FileName)), LoadsDocument.Serialize(file));
    }

    [Fact]
    public void A_copy_is_in_the_plans_it_declares_before_the_model_that_reads_it_and_in_default_when_it_declares_none()
    {
        var dir = Project("name: warehouse.orders\nkind: {copy: {from: pos.orders, slice: {column: shop, value: \"${origin.shop}\", type: \"VARCHAR(4)\"}}}\nrefresh: { hourly: default }\n");
        Assert.Equal(["warehouse.orders.default", "marts.daily.default"], Plan(dir, "hourly").Members.Select(m => m.FullName));
        Assert.Empty(Plan(dir, "hourly").Excluded);

        var implicitDir = Project("name: warehouse.orders\nkind: {copy: {from: pos.orders, slice: {column: shop, value: \"${origin.shop}\", type: \"VARCHAR(4)\"}}}\n");
        var loads = Loads(implicitDir);
        Assert.NotNull(loads.Find("warehouse.orders", "default"));
        Assert.Contains("warehouse.orders.default", Plan(implicitDir, "default").Members.Select(m => m.FullName));        // a default load and no `refresh:` is in `default`, copy or not
    }

    [Fact]
    public void A_damaged_copy_part_of_a_load_plan_is_refused_with_a_diagnostic_never_an_exception()
    {
        var dir = Project("name: warehouse.orders\nkind: {copy: {from: pos.orders, slice: {column: shop, value: \"${origin.shop}\", type: \"VARCHAR(4)\"}}}\nrefresh: { hourly: default }\n");
        var text = LoadsDocument.Serialize(Loads(dir));
        var diags = new List<Diagnostic>();
        Assert.NotNull(LoadsDocument.Parse(text, "loads.yml", diags));
        Assert.Empty(diags);
        foreach (var bad in new[] { text.Replace("create_staging:", "create_stagingx:"), text.Replace("      origins:", "      origins: 3\n      x:"), text.Replace("engine: \"postgres\"", "engine: \"mysql\"") })
        {
            var d = new List<Diagnostic>();
            Assert.Null(LoadsDocument.Parse(bad, "loads.yml", d));
            Assert.NotEmpty(d);
        }
        var random = new Random(11);
        var at = text.IndexOf("copy:", StringComparison.Ordinal);
        for (var i = 0; i < 300; i++)
        {
            var chars = text.ToCharArray();
            chars[at + random.Next(chars.Length - at)] = "x:-\n\"'#{ ["[random.Next(10)];
            var parsed = LoadsDocument.Parse(new string(chars), "loads.yml", []);
            if (parsed != null) Assert.Equal(text, LoadsDocument.Serialize(parsed));
        }
    }
}
