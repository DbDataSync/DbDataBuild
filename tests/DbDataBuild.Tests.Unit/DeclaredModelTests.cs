using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.SchemaConformanceTests;

namespace DbDataBuild.Tests.Unit;

public class DeclaredModelTests
{
    public sealed record Case(string Name, string Yaml, bool SchemaValid, params string[] Codes)
    {
        public override string ToString() => Name;
    }

    private const string Cols = "columns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";

    public static readonly Case[] Cases =
    [
        new("minimal", "name: staging.orders\nkind: mapped\n" + Cols, true),
        new("with grain", "name: staging.orders\nkind: mapped\ngrain: [order_id]\n" + Cols, true),
        new("composite grain", "name: staging.orders\nkind: mapped\ngrain: [order_id, amount]\n" + Cols, true),
        new("missing name", Cols, false, "DDB-105"),
        new("missing columns", "name: staging.orders\nkind: mapped\n", false, "DDB-105"),
        new("empty columns", "name: staging.orders\nkind: mapped\ncolumns: []\n", false, "DDB-106"),
        new("unknown key", "name: staging.orders\nkind: mapped\nsurprise: 1\n" + Cols, false, "DDB-104"),
        new("missing kind", "name: staging.orders\n" + Cols, false, "DDB-105"),
        new("a kind that builds", "name: staging.orders\nkind: full\n" + Cols, false, "DDB-106"),
        new("kind with other keys", "name: staging.orders\nkind: {mapped: {unique_key: [a]}}\n" + Cols, false, "DDB-106"),
        new("connections", "name: staging.orders\nkind: mapped\nconnections: [sqlserver, postgres]\n" + Cols, true),
        new("connections that replace what is inherited", "name: staging.orders\nkind: mapped\nconnections=: [postgres]\n" + Cols, true),
        new("an unknown connection", "name: staging.orders\nkind: mapped\nconnections: [nowhere]\n" + Cols, true, "DDB-106"),
        new("empty connections", "name: staging.orders\nkind: mapped\nconnections: []\n" + Cols, false, "DDB-106"),
        new("empty grain", "name: staging.orders\nkind: mapped\ngrain: []\n" + Cols, false, "DDB-106"),
        new("duplicate grain column", "name: staging.orders\nkind: mapped\ngrain: [order_id, order_id]\n" + Cols, false, "DDB-106"),
        new("column without type", "name: staging.orders\nkind: mapped\ncolumns:\n  - name: a\n", false, "DDB-105"),
        new("nullable maybe", "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: a, type: INT, nullable: maybe}\n", false, "DDB-106"),
        new("name does not match path", "name: staging.other\nkind: mapped\n" + Cols, true, "DDB-107"),
        new("grain names an undeclared column", "name: staging.orders\nkind: mapped\ngrain: [ghost]\n" + Cols, true, "DDB-217"),
        new("duplicate column names", "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: a, type: INT}\n  - {name: A, type: INT}\n", true, "DDB-102"),
        new("indexes", "name: staging.orders\nkind: mapped\n" + Cols + "indexes:\n  - {name: IX_orders-amount, columns: [amount], include: [order_id]}\n  - {name: ux_orders, columns: [order_id, amount], unique: true}\n", true),
        new("index on an undeclared column", "name: staging.orders\nkind: mapped\n" + Cols + "indexes:\n  - {name: ix, columns: [ghost]}\n", true, "DDB-217"),
        new("index without columns", "name: staging.orders\nkind: mapped\n" + Cols + "indexes:\n  - {name: ix}\n", false, "DDB-105"),
        new("index with an unknown key", "name: staging.orders\nkind: mapped\n" + Cols + "indexes:\n  - {name: ix, columns: [amount], connections: [sqlserver]}\n", false, "DDB-104"),
        new("duplicate index names", "name: staging.orders\nkind: mapped\n" + Cols + "indexes:\n  - {name: ix, columns: [amount]}\n  - {name: IX, columns: [order_id]}\n", true, "DDB-102"),
        new("foreign keys", "name: staging.orders\nkind: mapped\n" + Cols + "foreign_keys:\n  - {name: fk_orders_customer, columns: [order_id], references: {table: staging.customers, columns: [customer_id]}}\n", true),
        new("foreign key without references", "name: staging.orders\nkind: mapped\n" + Cols + "foreign_keys:\n  - {name: fk, columns: [order_id]}\n", false, "DDB-105"),
        new("foreign key with mismatched column counts", "name: staging.orders\nkind: mapped\n" + Cols + "foreign_keys:\n  - {name: fk, columns: [order_id, amount], references: {table: staging.c, columns: [id]}}\n", true, "DDB-106"),
        new("foreign key on an undeclared column", "name: staging.orders\nkind: mapped\n" + Cols + "foreign_keys:\n  - {name: fk, columns: [ghost], references: {table: staging.c, columns: [id]}}\n", true, "DDB-217"),
    ];

    public static TheoryData<Case> Data
    {
        get { var d = new TheoryData<Case>(); foreach (var c in Cases) d.Add(c); return d; }
    }

    [Theory, MemberData(nameof(Data))]
    public void Declared_model_schema_and_loader_agree(Case c)
    {
        Assert.Equal(c.SchemaValid, SchemaAccepts(LoadSchema("declared-model"), c.Yaml));
        var diags = new List<Diagnostic>();
        var d = DeclaredModelLoader.Load(c.Yaml, "models/staging/orders.yml", "staging.orders", diags);
        if (c.Codes.Length == 0)
        {
            Assert.Empty(diags.Select(DiagnosticFormatter.Format));
            Assert.NotNull(d);
        }
        else
        {
            foreach (var code in c.Codes) Assert.Contains(diags, x => x.Code == code);
            Assert.Null(d);
        }
    }

    [Fact]
    public void Descriptor_is_read_completely()
    {
        var d = DeclaredModelLoader.Load("name: staging.orders\nkind: mapped\ngrain: [order_id]\n" + Cols, "f.yml", null, [])!;
        Assert.Equal("staging.orders", d.Name);
        Assert.Equal(["order_id"], d.Grain);
        Assert.Equal(["order_id", "amount"], d.Columns.Select(c => c.Name));
        Assert.False(d.Columns[0].Nullable);
        Assert.Equal("DECIMAL(14, 2)", d.Columns[1].Type);
    }

    [Fact]
    public void Project_validator_loads_sources_by_path_and_reports_problems()
    {
        var dir = TestSupport.NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models", "staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\n" + Cols);
        File.WriteAllText(Path.Combine(dir, "models/staging/bad.yml"), "name: staging.nope\nkind: mapped\n" + Cols);
        var result = ProjectValidator.Validate(dir);
        Assert.Equal("staging.orders", Assert.Single(result.MappedModels).Name);
        Assert.Contains(result.Diagnostics, d => d.Code == "DDB-107" && d.Location.File == "models/staging/bad.yml");
    }

    [Fact]
    public void A_mapped_model_has_no_query_and_a_query_beside_one_is_refused()
    {
        var dir = TestSupport.NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models", "staging"));
        File.WriteAllText(Path.Combine(dir, "models/staging/orders.yml"), "name: staging.orders\nkind: mapped\n" + Cols);
        var result = ProjectValidator.Validate(dir);
        Assert.Equal("staging.orders", Assert.Single(result.MappedModels).Name);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "DDB-108");              // nothing is missing: there is no query to have

        File.WriteAllText(Path.Combine(dir, "models/staging/orders.sql"), "SELECT 1");
        Assert.Contains(ProjectValidator.Validate(dir).Diagnostics, d => d.Code == "DDB-108" && d.Found.Contains("has no query of its own"));
    }

    [Fact]
    public void A_folder_can_make_everything_beneath_it_mapped_and_say_where_it_exists()
    {
        var dir = TestSupport.NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "connections:\n  crm: { engine: postgres }\n");
        Directory.CreateDirectory(Path.Combine(dir, "models", "crm"));
        File.WriteAllText(Path.Combine(dir, "models/crm/_dbdatabuild.yml"), "defaults:\n  kind: mapped\n  connections: [crm]\n");
        File.WriteAllText(Path.Combine(dir, "models/crm/customers.yml"), "name: crm.customers\n" + Cols);
        var result = ProjectValidator.Validate(dir);
        Assert.Empty(result.Diagnostics);
        var d = Assert.Single(result.MappedModels);
        Assert.Equal(("crm.customers", "crm"), (d.Name, string.Join(",", d.Connections!)));
    }

    [Fact]
    public void The_retired_sources_folder_is_an_error_that_says_where_its_files_go()
    {
        var dir = TestSupport.NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "sources", "staging"));
        var d = Assert.Single(ProjectValidator.Validate(dir).Diagnostics, x => x.Location.File == "sources");
        Assert.Contains("models/<schema name>/<table>.yml", d.Fix);
        Assert.Contains("kind: mapped", d.Fix);
    }

    [Fact]
    public void Sources_are_optional()
    {
        var dir = TestSupport.NewProjectDir();
        Assert.Empty(ProjectValidator.Validate(dir).MappedModels);
    }
}
