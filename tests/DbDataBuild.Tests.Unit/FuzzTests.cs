using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Hardening (DESIGN.md 15.5): every file a person edits is read by code that must answer a damaged file with a diagnostic, never with an unhandled exception.
/// Each test takes a valid file, applies seeded mutations (cut, delete a line, duplicate a line, replace a character, insert a token, swap two lines) and requires that
/// the tool still finishes with a normal exit code and, for the CLI, never reports an internal error (DDB-900). The seed is fixed so a failure is reproducible.
/// </summary>
public class FuzzTests
{
    private const int Seed = 20261002;
    private const int Rounds = 40;

    private static readonly string[] Tokens =
        ["", " ", "\t", ":", "- ", "[", "]", "{", "}", "\"", "'", "#", "&a", "*a", "!!str", "? ", "|", ">", "---", "...", "null", "~", "true", "-1", "1e999", "0x10", "\u0000", "é", "\u202e", "\r", "\\", "%", "@", "`", "$$", "{{x}}", "<<: *a"];

    internal static IEnumerable<string> Mutations(string text, int seed, int count)
    {
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var lines = text.Split('\n').ToList();
            var pick = rng.Next(7);
            switch (pick)
            {
                case 0: yield return text[..rng.Next(text.Length + 1)]; break;                                                           // truncated
                case 1: if (lines.Count > 0) lines.RemoveAt(rng.Next(lines.Count)); yield return string.Join('\n', lines); break;       // line deleted
                case 2: { var k = rng.Next(lines.Count); lines.Insert(k, lines[k]); yield return string.Join('\n', lines); break; }      // line duplicated
                case 3: { var chars = text.ToCharArray(); if (chars.Length > 0) chars[rng.Next(chars.Length)] = Tokens[rng.Next(Tokens.Length)].FirstOrDefault('?'); yield return new string(chars); break; }
                case 4: { var at = rng.Next(text.Length + 1); yield return text.Insert(at, Tokens[rng.Next(Tokens.Length)]); break; }   // token inserted
                case 5: { var a = rng.Next(lines.Count); var b = rng.Next(lines.Count); (lines[a], lines[b]) = (lines[b], lines[a]); yield return string.Join('\n', lines); break; }
                default: { var k = rng.Next(lines.Count); lines[k] = new string(' ', rng.Next(8)) + lines[k].TrimStart(); yield return string.Join('\n', lines); break; }   // indentation changed
            }
        }
    }

    private const string Config = """
        title: Fuzz project
        description: "A project for the fuzz tests"
        tags: [fuzz, "layer:one"]
        properties: { owner: data, tier: 2, live: true, ratio: 0.5, code: "007" }
        defaults: {connections: [sqlserver, postgres], tags: [base], properties: { source: fuzz }}
        connections:
          sqlserver: { version: 17, title: Warehouse, description: "The main one", tags: [prod], properties: { region: eu } }
        string_semantics:
          case: insensitive
          accent: sensitive
          trailing_space: ignored
        metadata: { store_on_apply: false }
        lowering: { enabled: true }
        hook_groups:
          standard:
            - { name: grant, event: post_create, script: hooks/grant.sql }
        """;

    private const string Source = "name: staging.events\ntitle: Events\ndescription: \"Raw events\"\ntags: [raw]\nproperties: {system: crm}\nkind: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false, title: \"Event id\", description: \"The key\", tags: [key], properties: {pk: true}}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: seq, type: BIGINT, nullable: false}\n  - {name: payload, type: \"VARCHAR(100)\"}\n";

    /// <summary>The words about the model, its columns' neighbours and its index: appended to the model's definition so the mutations reach them.</summary>
    private const string ModelExtras = "\ntitle: Events\ndescription: \"Facts about events\"\ntags: [facts]\nproperties:\n  owner: data\n  tier: 1\n  reviewed: false\nindexes:\n  - {name: ix_seq, columns: [seq], title: \"By sequence\", description: \"Lookups by seq\", tags: [lookup], properties: {rank: 1}}\nhooks:\n  - {use: standard}\n";

    private const string Answers = "answers:\n  - {id: Q-history-marts.fct_events.payload, answer: not_backfilled, note: \"no history\"}\n";

    private const string FolderFile = "defaults:\n  connections: [sqlserver]\n  lint_ignore: [DDB-223]\n  tags: [folder]\n  properties: {zone: marts, tier: 3}\n  hooks:\n    - {name: audit, event: post_load, script: hooks/grant.sql}\n";

    private static string Project(string? config = null, string? source = null, string? model = null, string? sql = null, string? folder = null)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config ?? Config);
        Directory.CreateDirectory(Path.Combine(dir, "models", "staging"));
        Directory.CreateDirectory(Path.Combine(dir, "hooks"));
        File.WriteAllText(Path.Combine(dir, "hooks", "grant.sql"), "GRANT SELECT ON marts.fct_events TO reader;\n");
        File.WriteAllText(Path.Combine(dir, "models", "staging", "events.yml"), source ?? Source);
        File.WriteAllText(Path.Combine(dir, "models", "marts", "fct_events.yml"), model ?? (LoadRendererTests.AllOpsYaml + ModelExtras));
        File.WriteAllText(Path.Combine(dir, "models", "marts", "fct_events.sql"), sql ?? LoadRendererTests.AllOpsSql);
        File.WriteAllText(Path.Combine(dir, "answers.yml"), Answers);
        File.WriteAllText(Path.Combine(dir, "models", "marts", "_dbdatabuild.yml"), folder ?? FolderFile);
        return dir;
    }

    private static void RunAll(string dir, string what)
    {
        foreach (var args in new[] { new[] { "project", "compile" }, new[] { "project", "compile", "--check" }, new[] { "project", "show", "loads" }, new[] { "project", "model", "update", "--check" }, new[] { "project", "show", "metadata" } })
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir, "--format", "json"], o, e);
            Assert.True(exit != CliApp.ExitInternal && !o.ToString().Contains("DDB-900") && !e.ToString().Contains("DDB-900"), $"`{string.Join(' ', args)}` failed internally on {what}:\n{o}\n{e}");
            if (args[1] == "compile") System.Text.Json.JsonDocument.Parse(o.ToString());      // the JSON document stays well formed whatever the input
        }
    }

    [Fact]
    public void The_unmutated_project_is_a_valid_starting_point()
    {
        var dir = Project();
        try
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run(["project", "compile", "--project", dir], o, e);
            Assert.True(exit is CliApp.ExitOk or CliApp.ExitFindings, o + "\n" + e);
            Assert.DoesNotContain("error DDB-1", e.ToString());      // no config, definition or YAML error in the base files
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("config")]
    [InlineData("source")]
    [InlineData("model")]
    [InlineData("sql")]
    [InlineData("folder")]
    public void A_damaged_project_file_is_answered_with_diagnostics_not_an_exception(string which)
    {
        var baseText = which switch { "folder" => FolderFile, "config" => Config, "source" => Source, "model" => LoadRendererTests.AllOpsYaml + ModelExtras, _ => LoadRendererTests.AllOpsSql };
        var n = 0;
        foreach (var mutated in Mutations(baseText, Seed + which.Length, Rounds))
        {
            var dir = which switch { "config" => Project(config: mutated), "source" => Project(source: mutated), "model" => Project(model: mutated), "folder" => Project(folder: mutated), _ => Project(sql: mutated) };
            try { RunAll(dir, $"{which} mutation {n++}: {mutated.Replace("\n", "\\n")}"); }
            finally { Directory.Delete(dir, true); }
        }
    }

    private static readonly string[] KindTexts =
    [
        "name: marts.k\nkind: {incremental_by_unique_key: {unique_key: [event_id]}}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT}\n",
        "name: marts.k\nkind:\n  incremental_by_time_range:\n    time_column: event_ts   # the clock\n    lookback: 3 days\ngrain: [event_ts]\ncolumns:\n  - {name: event_ts, type: TIMESTAMP}\n",
        "name: marts.k\nkind: {copy: {from: staging.events, slice: {column: s, value: \"${origin.s}\", type: \"VARCHAR(5)\"}, unique_key: [event_id], watermark: {column: event_ts, lookback: 1 days}}}\n",
        "name: staging.k\nkind:\n  native:\n    access: select\n    query: SELECT 1 AS n\ncolumns:\n  - {name: n, type: INTEGER}\n",
        "name: marts.k\nkind: view\ncolumns:\n  - {name: n, type: INTEGER}\n",
        // the earlier form, which is an error that names the new one
        "name: marts.k\nkind:\n  type: incremental_by_unique_key   # keyed\n  unique_key: [event_id]\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT}\n",
        "name: marts.k\nkind: {type: copy, from: staging.events, unique_key: [event_id]}\n",
    ];

    [Fact]
    public void A_damaged_kind_is_answered_with_diagnostics_not_an_exception()
    {
        var n = 0;
        foreach (var text in KindTexts)
            foreach (var mutated in Mutations(text, Seed + text.Length, Rounds))
            {
                var label = $"kind mutation {n++}: {mutated.Replace("\n", "\\n")}";
                var diags = new List<Diagnostic>();
                var def = ModelDefinitionLoader.Load(mutated, "models/marts/k.yml", null, diags);
                Assert.True(def == null || diags.All(d => d.Severity != Severity.Error), label);
                Assert.DoesNotContain(diags, d => d.Code == "DDB-900");
                var source = new List<Diagnostic>();
                DeclaredModelLoader.Load(mutated, "models/staging/k.yml", null, source);
            }
    }

    private static readonly (string Name, string Path, string Text)[] Auxiliary =
    [
        ("hook", "hooks/grant.sql", "-- grants\nGRANT SELECT ON marts.fct_events TO reader;\nGO\nEXEC sp_addrolemember 'r', 'u';\n"),
        ("macros", "macros/m.sql", "CREATE MACRO twice(x) AS x * 2;\nCREATE MACRO pick(t, c) AS TABLE SELECT c FROM query_table(t);\nCREATE TYPE mood AS ENUM ('a', 'b');\n"),
        ("native", "models/staging/ext.native.sql", "SELECT 1 AS n, 'x' AS s\n"),
        ("native_yaml", "models/staging/ext.yml", "name: staging.ext\nkind: {native: {access: select}}\nreads: [staging.events]\ntrack_definition: [dbo.fn_x, public.f(date)]\nparameters:\n  p: {type: NAME, value: events}\ncolumns:\n  - {name: n, type: INTEGER}\n  - {name: s, type: VARCHAR}\n"),
        ("metadata_test", "tests/metadata/rule.sql", "-- tags: naming\n-- severity: warn\nSELECT name FROM metadata_models WHERE name LIKE 'x%'\n"),
        ("model_test", "tests/models/marts/fct_events.yml", "tests:\n  - name: one row\n    given:\n      staging.events:\n        - {event_id: 1, event_ts: '2024-01-01 00:00:00', seq: 1}\n    expect:\n      - {event_id: 1}\n"),
        ("head", "models/marts/head.sql", "CREATE TABLE marts.head WITH (kind = full_replace, unique_key = (event_id), time_column = event_ts, lookback = 3) AS\nSELECT event_id FROM staging.events\n"),
    ];

    [Theory]
    [InlineData("hook")]
    [InlineData("macros")]
    [InlineData("native")]
    [InlineData("native_yaml")]
    [InlineData("metadata_test")]
    [InlineData("model_test")]
    [InlineData("head")]
    public void A_damaged_hook_macro_native_text_test_or_query_head_is_answered_with_diagnostics_not_an_exception(string which)
    {
        var (_, path, text) = Auxiliary.Single(a => a.Name == which);
        var n = 0;
        foreach (var mutated in Mutations(text, Seed + 7 * which.Length, 25))
        {
            var dir = Project();
            try
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, mutated);
                foreach (var args in new[] { new[] { "project", "compile" }, new[] { "project", "tests", "run" }, new[] { "project", "show", "loads" }, new[] { "project", "show", "metadata" }, new[] { "project", "model", "update", "--check" } })
                {
                    var o = new StringWriter(); var e = new StringWriter();
                    var exit = CliApp.Run([.. args, "--project", dir, "--format", "json"], o, e);
                    Assert.True(exit != CliApp.ExitInternal && !o.ToString().Contains("DDB-900") && !e.ToString().Contains("DDB-900"), $"`{string.Join(' ', args)}` failed internally on {which} mutation {n}: {mutated.Replace("\n", "\\n")}\n{o}\n{e}");
                    System.Text.Json.JsonDocument.Parse(o.ToString());
                }
                n++;
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public void A_damaged_answers_file_does_not_crash_define_or_plan()
    {
        var n = 0;
        foreach (var mutated in Mutations(Answers, Seed, Rounds))
        {
            var dir = Project();
            File.WriteAllText(Path.Combine(dir, "answers.yml"), mutated);
            try
            {
                var o = new StringWriter(); var e = new StringWriter();
                var exit = CliApp.Run(["project", "model", "update", "--check", "--answers", Path.Combine(dir, "answers.yml"), "--project", dir], o, e);
                Assert.True(exit != CliApp.ExitInternal, $"answers mutation {n++} failed internally: {mutated.Replace("\n", "\\n")}\n{e}");
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public void A_damaged_dialects_lock_file_is_answered_with_a_diagnostic_never_an_exception()
    {
        var facts = new ServerFacts("PostgreSQL", "16.4", "\"", "case_insensitive", "case_insensitive", true);
        var chosen = DialectDiscovery.Choose(facts, new Dictionary<string, DialectProfile>());
        var text = DialectLockFile.Render([DialectLockEntry.From("proxy_erp", facts, chosen), DialectLockEntry.From("proxy_two", ServerFacts.Nothing, DialectDiscovery.Choose(ServerFacts.Nothing, new Dictionary<string, DialectProfile>()))]);
        var clean = new List<Diagnostic>();
        Assert.Equal(2, DialectLockFile.Parse(text, ProductInfo.DialectLockFile, clean, new Dictionary<string, DialectProfile>())!.Count);
        Assert.Empty(clean);
        var n = 0;
        foreach (var mutated in Mutations(text, Seed + 31, Rounds * 3))
        {
            var diags = new List<Diagnostic>();
            var parsed = DialectLockFile.Parse(mutated, ProductInfo.DialectLockFile, diags, new Dictionary<string, DialectProfile>());
            Assert.True(parsed != null || diags.Count > 0, $"dialect lock mutation {n} was refused with no diagnostic: {mutated.Replace("\n", "\\n")}");
            Assert.True(parsed == null || diags.Count == 0, $"dialect lock mutation {n} was accepted with diagnostics");
            // the project reads it too: compile and update answer with a normal exit code
            var dir = Project(config: Config.Replace("properties: { region: eu } }\n", "properties: { region: eu } }\n  proxy_erp: { engine: readonly flightsql }\n"));
            try
            {
                File.WriteAllText(Path.Combine(dir, ProductInfo.DialectLockFile), mutated);
                foreach (var args in new[] { new[] { "project", "compile" }, new[] { "project", "update", "--check" }, new[] { "project", "update", "--offline", "--check" } })
                {
                    var o = new StringWriter(); var e = new StringWriter();
                    var exit = CliApp.Run([.. args, "--project", dir, "--format", "json"], o, e);
                    Assert.True(exit != CliApp.ExitInternal && !o.ToString().Contains("DDB-900"), $"`{string.Join(' ', args)}` failed internally on dialect lock mutation {n}: {mutated.Replace("\n", "\\n")}");
                    System.Text.Json.JsonDocument.Parse(o.ToString());
                }
            }
            finally { Directory.Delete(dir, true); }
            n++;
        }
    }

    [Fact]
    public void Every_prefix_of_a_yaml_document_is_read_without_an_exception()
    {
        // found by the plan-file fuzz: a file cut off mid-structure made YamlDotNet throw EndOfStreamException, which the reader did not catch
        const string doc = "a:\n  - b: [1, 2]\n    c: {d: e, f: \"g\"}\nh: |\n  text\n  more\ni: 'q'\n";
        for (var n = 0; n <= doc.Length; n++)
            DbDataBuild.Models.Yaml.StrictYamlReader.Read(doc[..n], "x.yml", new List<Diagnostic>());
    }

    [Fact]
    public void A_damaged_plan_file_is_refused_with_a_diagnostic()
    {
        var plan = new Plan("2026-10-12-abcd1234", "sqlserver", "4af31c2", true, "0.1.0",
            [new ObjectBase("marts.fct", ObjectState.InSync, new string('a', 64), new string('a', 64))],
            [new ResolvedAnswer("Q-history-marts.fct.x", "not_backfilled", null, "n", AnswerSource.File)],
            [new PlanStep("1", StepType.Ddl, "marts.fct", "add column", "ALTER TABLE t ADD x int;", RiskClass.Safe, ["col.added"], new string('b', 64), []),
             new PlanStep("2", StepType.Load, "marts.fct", "load", "SELECT 'é'\r\nGO", RiskClass.Safe, ["load.routine"], null, [new PlanParameter("w", "TIMESTAMP", "resolver", "2024-03-01")], "SELECT 1", "x", true, new string('c', 64), "default")],
            ["note"]);
        var text = PlanDocument.Serialize(plan);
        var parsedClean = new List<Diagnostic>();
        Assert.NotNull(PlanDocument.Parse(text, "plan.yml", parsedClean));
        var n = 0;
        foreach (var mutated in Mutations(text, Seed, Rounds * 6))
        {
            var diags = new List<Diagnostic>();
            var parsed = PlanDocument.Parse(mutated, "plan.yml", diags);       // must not throw
            // an edited plan is either rejected or, if the edit left the content unchanged, equal to the original: it never parses to something different
            if (parsed != null && diags.All(d => d.Severity != Severity.Error))
                Assert.True(PlanDocument.Serialize(parsed) == text, $"mutation {n} parsed to a different plan without an error: {mutated.Replace("\n", "\\n")}");
            n++;
        }
    }
}
