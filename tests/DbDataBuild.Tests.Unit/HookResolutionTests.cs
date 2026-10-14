using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Tests.Unit;

public class HookResolutionTests
{
    private static ProjectConfig Config(string groups) =>
        ProjectConfigLoader.Load("hook_groups:\n" + groups, "dbdatabuild.yml", new List<Diagnostic>())!;

    private static ModelDefinition Model(string hooks)
    {
        var diags = new List<Diagnostic>();
        var m = ModelDefinitionLoader.Load("name: marts.fct\nkind: full\ncolumns:\n  - {name: a, type: INT}\nhooks:\n" + hooks, "models/marts/fct.yml", "marts.fct", diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return m!;
    }

    private const string Groups = "  standard:\n    - {name: grant, event: post_create, script: hooks/grant.sql}\n    - {name: stats, event: post_load, script: {sqlserver: hooks/s.sql, postgres: hooks/p.sql}}\n    - {name: lock, event: pre_alter, script: hooks/lock.sql, connections: [postgres]}\n";

    private static IReadOnlyList<ResolvedHook> Resolve(ModelDefinition m, ProjectConfig c, string target, List<Diagnostic>? diags = null) =>
        HookReader.Resolve(m, c, target, "models/marts/fct.yml", diags ?? []);

    [Fact]
    public void Groups_expand_in_place_in_the_order_written_and_names_are_prefixed()
    {
        var m = Model("  - {name: first, event: pre_load, script: hooks/first.sql}\n  - {use: standard}\n  - {name: last, event: post_load, script: hooks/last.sql}\n");
        var r = Resolve(m, Config(Groups), "postgres");
        Assert.Equal(["first", "standard.grant", "standard.stats", "standard.lock", "last"], r.Select(h => h.Name));
        Assert.Equal([null, "standard", "standard", "standard", null], r.Select(h => h.Group));
        Assert.Equal("hooks/p.sql", r.Single(h => h.Name == "standard.stats").ScriptPath);
    }

    [Fact]
    public void Hooks_for_other_targets_are_dropped_and_a_per_target_script_picks_its_own_file()
    {
        var m = Model("  - {use: standard}\n");
        var sql = Resolve(m, Config(Groups), "sqlserver");
        Assert.Equal(["standard.grant", "standard.stats"], sql.Select(h => h.Name));       // `lock` is for postgres only
        Assert.Equal("hooks/s.sql", sql[1].ScriptPath);
        Assert.Equal(["standard.grant"], Resolve(m, Config(Groups), "fabric").Select(h => h.Name));   // `stats` has no fabric script
    }

    [Fact]
    public void An_unknown_group_is_reported_with_the_fix()
    {
        var diags = new List<Diagnostic>();
        var r = Resolve(Model("  - {use: nope}\n  - {name: x, event: post_load, script: hooks/x.sql}\n"), Config(Groups), "sqlserver", diags);
        var d = Assert.Single(diags);
        Assert.Contains("`nope`", d.Found);
        Assert.Contains("hook_groups", d.Fix);
        Assert.Equal(["x"], r.Select(h => h.Name));                    // the rest still resolve
    }

    [Fact]
    public void A_name_clash_after_expansion_is_reported()
    {
        var diags = new List<Diagnostic>();
        Resolve(Model("  - {use: standard}\n  - {use: standard}\n"), Config(Groups), "sqlserver", diags);
        Assert.Contains(diags, d => d.Code == "DDB-102" && d.Found.Contains("standard.grant"));
    }

    [Fact]
    public void Effect_and_risk_are_carried_and_default_to_ddl_and_safe()
    {
        var r = Resolve(Model("  - {name: a, event: post_load, script: hooks/a.sql}\n  - {name: b, event: post_load, script: hooks/b.sql, effect: data, risk: risky}\n"), Config(Groups), "sqlserver");
        Assert.Equal([("ddl", "safe"), ("data", "risky")], r.Select(h => (h.Effect, h.Risk)));
    }

    [Fact]
    public void The_event_registry_pairs_each_phase_with_each_action_and_marks_what_the_planner_fires()
    {
        Assert.Equal(10, HookEvents.All.Count);
        Assert.Equal(["pre_drop", "post_drop"], HookEvents.All.Where(e => !e.Fires).Select(e => e.Name));
        Assert.Equal(HookEvents.Names.Distinct().Count(), HookEvents.Names.Count);
        Assert.All(HookEvents.All, e => Assert.Equal(e.Name, $"{e.Phase}_{e.Action}"));
    }

    [Theory]
    [InlineData("hooks/x.sql", true)]
    [InlineData("a/b/c.SQL", true)]
    [InlineData("../x.sql", false)]
    [InlineData("a/../x.sql", false)]
    [InlineData("/x.sql", false)]
    [InlineData("C:/x.sql", false)]
    [InlineData("a\\b.sql", false)]
    [InlineData("a//b.sql", false)]
    [InlineData("x.sh", false)]
    [InlineData("./x.sql", false)]
    public void Script_paths_stay_inside_the_project(string path, bool ok) => Assert.Equal(ok, HookReader.IsSafePath(path));
}
