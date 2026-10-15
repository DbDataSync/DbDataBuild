using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.State;
using DbDataBuild.Targets.DuckDb;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The model kind beside each model document, and what each model depends on as rows (tracking layout 8): computed from the project, the same rows in the tracking table and in the in-memory views.</summary>
public class MetadataDependencyTests
{
    [Fact]
    public void The_closure_has_the_direct_edges_at_depth_one_and_the_rest_at_the_shortest_path()
    {
        var rows = MetadataDependencies.Closure(
        [
            ("m.c", "m.b", "model", "view"), ("m.c", "s.x", "model", "mapped"), ("m.b", "m.a", "model", "full"), ("m.a", "s.x", "model", "mapped"), ("m.a", "n.f", "model", "native"), ("n.f", "s.y", "model", "mapped"),
            ("m.d", "mac", "macro", null), ("m.d", "ghost", "unknown", null), ("m.cy", "m.cy2", "model", "copy"), ("m.cy2", "m.cy", "model", "view"),
        ]);
        string Of(string subject) => string.Join(";", rows.Where(r => r.Subject == subject).Select(r => $"{r.DependsOn}|{r.DependsOnCategory}|{r.DependsOnKind ?? "-"}|{r.Depth}"));
        Assert.Equal("m.b|model|view|1;s.x|model|mapped|1;m.a|model|full|2;n.f|model|native|3;s.y|model|mapped|4", Of("m.c"));           // s.x is direct, so depth 1, not 3; a model's kind is its own, not "model"
        Assert.Equal("ghost|unknown|-|1;mac|macro|-|1", Of("m.d"));                                                                         // a macro and an unknown table have a category and no kind
        Assert.Equal("m.cy2|model|copy|1", Of("m.cy"));                                                                                     // a cycle ends where it started
        Assert.Equal("s.y|model|mapped|1", Of("n.f"));
    }

    private static string Project()
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "connections:\n  wh: { engine: sqlserver }\ndefaults:\n  connections: [wh]\n");
        File.WriteAllText(Path.Combine(dir, "models/staging/t.yml"), "name: staging.t\nkind: mapped\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/staging/ext.yml"), "name: staging.ext\nkind: {native: {query: \"SELECT 1 AS id\"}}\nreads: [staging.t]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/v1.yml"), "name: marts.v1\nkind: view\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/v1.sql"), "SELECT id FROM staging.ext\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/v2.yml"), "name: marts.v2\nkind: full\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/v2.sql"), "SELECT v1.id FROM marts.v1 v1 JOIN staging.t t ON t.id = v1.id\n");
        return dir;
    }

    [Fact]
    public void The_project_gives_every_model_and_native_model_its_whole_ancestry_with_the_kind_of_each_thing()
    {
        var ctx = ProjectContext.Load(Project());
        Assert.False(ctx.Project.HasErrors, string.Join("\n", ctx.Project.Diagnostics.Select(DiagnosticFormatter.Format)));
        var rows = MetadataBuilder.Dependencies(ctx).Select(r => $"{r.Subject}>{r.DependsOn}|{r.DependsOnCategory}|{r.DependsOnKind}|{r.Depth}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
        [
            "marts.v1>staging.ext|model|native|1", "marts.v1>staging.t|model|mapped|2",
            "marts.v2>marts.v1|model|view|1", "marts.v2>staging.ext|model|native|2", "marts.v2>staging.t|model|mapped|1",
            "staging.ext>staging.t|model|mapped|1",
        ], rows);
    }

    [Fact]
    public void The_model_documents_say_the_kind_as_a_string_with_its_settings_beside_it_and_the_view_reads_the_same_rows()
    {
        var ctx = ProjectContext.Load(Project());
        var docs = MetadataPublisher.Collect(ctx, null, null);
        Assert.Equal("full", MetadataDependencies.ModelKindOf("model", docs.Single(d => d.Subject == "marts.v2").Json));
        Assert.Equal("mapped", MetadataDependencies.ModelKindOf("model", docs.Single(d => d.Subject == "staging.t").Json));
        Assert.Equal("native", MetadataDependencies.ModelKindOf("model", docs.Single(d => d.Subject == "staging.ext").Json));
        Assert.Contains("\"kind\":\"native\"", docs.Single(d => d.Subject == "staging.ext").Json);
        using (var ext = System.Text.Json.JsonDocument.Parse(docs.Single(d => d.Subject == "staging.ext").Json))
        {
            Assert.Equal("dbdatabuild.model/3", ext.RootElement.GetProperty("schema").GetString());
            Assert.False(ext.RootElement.TryGetProperty("native", out _));                                                           // the native model's settings are kind_settings, as a model's are
            Assert.Equal("select", ext.RootElement.GetProperty("kind_settings").GetProperty("access").GetString());
            Assert.Equal(["staging.t"], ext.RootElement.GetProperty("kind_settings").GetProperty("reads").EnumerateArray().Select(r => r.GetString()));
            Assert.Equal(["staging.t"], ext.RootElement.GetProperty("upstream").EnumerateArray().Select(u => u.GetProperty("name").GetString()));         // what it reads is its upstream, as a built model's query is
        }
        Assert.Contains("\"kind\":\"mapped\"", docs.Single(d => d.Subject == "staging.t").Json);
        Assert.Contains("\"kind_settings\":{}", docs.Single(d => d.Subject == "staging.t").Json);
        Assert.Null(MetadataDependencies.ModelKindOf("source", "{\"kind\":\"mapped\"}"));                                                                  // there is no subject kind `source` any more
        Assert.Null(MetadataDependencies.ModelKindOf("project", "{}"));
        Assert.Contains("\"kind\":\"view\"", docs.Single(d => d.Subject == "marts.v1").Json);
        Assert.Contains("\"kind_settings\":{}", docs.Single(d => d.Subject == "marts.v1").Json);
        var fromProject = MetadataBuilder.Dependencies(ctx).Where(r => r.Subject == "marts.v2" && r.DependsOn != "staging.ext").Select(r => $"{r.Subject}>{r.DependsOn}|{r.DependsOnCategory}|{r.DependsOnKind}|{r.Depth}").Order(StringComparer.Ordinal).ToList();
        var fromDocuments = MetadataDependencies.FromDocuments(docs.Select(d => (d.SubjectKind, d.Subject, d.Json))).Where(r => r.Subject == "marts.v2" && r.DependsOn != "staging.ext").Select(r => $"{r.Subject}>{r.DependsOn}|{r.DependsOnCategory}|{r.DependsOnKind}|{r.Depth}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(fromProject.Where(r => !r.Contains("staging.ext")), fromDocuments.Where(r => !r.Contains("staging.ext")));       // the documents do not include the native model, so what it reads is "unknown" there
        using var db = MetadataDatabase.Open(docs.Select(d => new MetadataDocument(d.SubjectKind, d.Subject, d.Json, d.Hash)), ProductInfo.Version, MetadataBuilder.Dependencies(ctx));
        var run = db.Run("SELECT m.model, m.kind FROM metadata_models m ORDER BY m.model", 10);
        Assert.Equal(["marts.v1|view", "marts.v2|full", "staging.ext|native", "staging.t|mapped"], run.Rows.Select(r => $"{r["model"]}|{r["kind"]}"));      // one view of all seven kinds
        var kinds = db.Run("SELECT subject, model_kind FROM metadata_current WHERE model_kind IS NOT NULL ORDER BY subject", 10);
        Assert.Equal(["marts.v1|view", "marts.v2|full", "staging.ext|native", "staging.t|mapped"], kinds.Rows.Select(r => $"{r["subject"]}|{r["model_kind"]}"));      // no model has a null kind
        var declared = db.Run("SELECT model, kind, kind_settings->>'access' AS access FROM metadata_models WHERE kind IN ('mapped', 'native') ORDER BY model", 10);
        Assert.Equal(["staging.ext|native|select", "staging.t|mapped|"], declared.Rows.Select(r => $"{r["model"]}|{r["kind"]}|{r["access"]}"));
        var up = db.Run("SELECT upstream, upstream_category, upstream_kind FROM metadata_upstream WHERE model = 'marts.v2' ORDER BY upstream", 10);
        Assert.Equal(["marts.v1|model|view", "staging.t|model|mapped"], up.Rows.Select(r => $"{r["upstream"]}|{r["upstream_category"]}|{r["upstream_kind"]}"));
        var anc = db.Run("SELECT ancestor, depth, ancestor_kind FROM metadata_ancestors WHERE model = 'marts.v2' ORDER BY depth, ancestor", 10);
        Assert.Equal(["marts.v1|1|view", "staging.t|1|mapped", "staging.ext|2|native"], anc.Rows.Select(r => $"{r["ancestor"]}|{r["depth"]}|{r["ancestor_kind"]}"));
        Assert.Equal(1, db.Run("SELECT * FROM metadata_current WHERE model_kind = 'full'", 5).Count);
        Assert.Equal(1, db.Run("SELECT * FROM metadata_dependency WHERE depends_on = 'staging.t' AND subject = 'marts.v2'", 5).Count);
    }

    [Fact]
    public void A_sync_writes_only_the_subjects_whose_rows_differ_from_the_stored_ones()
    {
        DependencyRow R(string s, string d, int depth) => new(s, d, "model", "view", depth);
        var computed = new[] { R("a", "b", 1), R("a", "c", 2), R("x", "y", 1) };
        var set = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(["a", "x"], MetadataStore.DependencySubjectsToWrite(computed, [], set, all: true));
        Assert.Empty(MetadataStore.DependencySubjectsToWrite(computed, computed, set, all: true));
        Assert.Equal(["a"], MetadataStore.DependencySubjectsToWrite(computed, [R("a", "b", 1), R("x", "y", 1)], set, all: true));        // a's closure changed
        Assert.Equal(["gone", "x"], MetadataStore.DependencySubjectsToWrite([R("a", "b", 1), R("a", "c", 2)], [R("a", "b", 1), R("a", "c", 2), R("x", "y", 1), R("gone", "z", 1)], set, all: true));
        Assert.Empty(MetadataStore.DependencySubjectsToWrite(computed, [], new HashSet<string> { "q" }, all: false));                      // a partial publish leaves the others alone
        Assert.Equal(["x"], MetadataStore.DependencySubjectsToWrite(computed, [R("a", "b", 1), R("a", "c", 2)], new HashSet<string> { "x" }, all: false));
    }

    [Fact]
    public void Layout_nine_has_the_subject_kind_column_and_the_dependency_table_and_the_init_script_keeps_the_old_view_until_the_column_is_there()
    {
        Assert.Equal(9, TrackingSchema.Version);
        Assert.Contains(TrackingSchema.Table("metadata_document").Columns, c => c.Name == "model_kind" && c.Nullable);
        Assert.Contains(TrackingSchema.Table("metadata_document").Columns, c => c.Name == "subject_kind" && !c.Nullable);
        Assert.DoesNotContain(TrackingSchema.Table("metadata_document").Columns, c => c.Name == "kind");
        Assert.Equal(["connection", "subject", "depends_on", "depends_on_category", "depends_on_kind", "depth"], TrackingSchema.Table("metadata_dependency").Columns.Select(c => c.Name));
        Assert.True(TrackingSchema.Table("metadata_dependency").Columns.Single(c => c.Name == "depends_on_kind").Nullable);        // a macro or an unknown table is no model, so it has no model kind
        Assert.False(TrackingSchema.Table("metadata_dependency").Columns.Single(c => c.Name == "depends_on_category").Nullable);
        foreach (var target in new[] { "sqlserver", "postgres" })
        {
            var script = TrackingDdl.Render(TrackingDdl.For(target).InitScript("dbdatabuild", "x"));
            Assert.Contains("metadata_dependency", script);
            Assert.Contains("model_kind", script);
            var upgrade = TrackingDdl.Render(TrackingDdl.For(target).UpgradeScript("dbdatabuild", "c"));
            Assert.Contains("(layout 8)", upgrade);
            Assert.Contains("(layout 9)", upgrade);
            Assert.Contains("subject_kind", upgrade);
        }
    }
}
