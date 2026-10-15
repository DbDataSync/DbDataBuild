using DbDataBuild.Core;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Tui.Model;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>The models of the project, all seven kinds, read from the `metadata` document: what each one is, and the things to do with it (sample data, render, plan; a mapped or native model is declared, not built, so those say so).</summary>
public sealed class ModelsWindow : Ui.Modal
{
    private readonly TuiSession session;
    private readonly JsonArray models;
    private readonly ListView list;
    private readonly TextView detail;

    public static void Open(TuiSession s)
    {
        var result = s.Run(["metadata", "--project", s.ProjectRoot]);
        if (result.Data["models"] is not JsonArray models || models.Count == 0)
        {
            Flow.Show(s, result);
            return;
        }
        s.App.Run(new ModelsWindow(s, models));
    }

    private ModelsWindow(TuiSession s, JsonArray models) : base($"Models  ({models.Count}: {Breakdown(models)})", 96, 94)
    {
        session = s;
        this.models = models;
        list = new ListView { X = 0, Y = 0, Width = 36, Height = Dim.Fill(2) };
        list.SetSource(new ObservableCollection<string>(models.Select(m => (string?)m!["name"] ?? "?")));
        detail = new TextView { X = Pos.Right(list) + 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(2), ReadOnly = true };
        list.ValueChanged += (_, _) => ShowModel();

        var close = Ui.Button("Close", () => Close(s.App), isDefault: true);
        close.X = 0; close.Y = Pos.AnchorEnd(1);
        View previous = close;
        foreach (var (text, command) in new[] { ("Sample data…", "project sample"), ("Compile…", "project compile"), ("Deploy…", "connection deploy"), ("Status…", "connection status") })
        {
            var b = Ui.Button(text, () => Run(command));
            b.X = Pos.Right(previous) + 2; b.Y = Pos.AnchorEnd(1);
            Add(b);
            previous = b;
        }
        Add(list, detail, close);
        Ui.EscapeCloses(this, s.App);
        ShowModel();
    }

    private string? Selected() => (list.SelectedItem ?? 0) is var i && i >= 0 && i < models.Count ? (string?)models[i]!["name"] : null;

    private void Run(string command)
    {
        var name = Selected();
        if (name == null) return;
        var info = session.Host.Commands.First(c => c.Name == command);
        Flow.Command(session, info, f => { if (f.Fields.FirstOrDefault(x => x.IsArgument && x.Label == "models") is { } models) models.Value = name; });
    }

    private void ShowModel()
    {
        var name = Selected();
        detail.Text = name == null ? "" : Describe(models.First(m => (string?)m!["name"] == name)!);
    }

    private static string Describe(JsonNode m)
    {
        var sb = new StringBuilder();
        var settings = m["kind_settings"] as JsonObject ?? [];
        sb.AppendLineLf($"{(string?)m["name"]}   {(string?)m["kind"]}");
        sb.AppendLineLf($"connections: {Join(m["connections"])}    grain: {Join(m["grain"])}");
        if (settings["unique_key"] is JsonArray { Count: > 0 } uk) sb.AppendLineLf($"unique key: {Join(uk)}");
        if ((string?)settings["time_column"] is { } tc) sb.AppendLineLf($"time column: {tc}{((string?)settings["lookback"] is { } lb ? ", lookback " + lb : "")}");
        if ((string?)settings["from"] is { } origin) sb.AppendLineLf($"copies: {origin}");
        if ((string?)settings["access"] is { } access) sb.AppendLineLf($"native access: {access}{(settings["reads"] is JsonArray { Count: > 0 } reads ? ", reads " + Join(reads) : "")}");
        sb.AppendLineLf($"files: {(string?)m["files"]!["query"] ?? (string?)m["files"]!["definition"]}");
        if (m["upstream"] is JsonArray { Count: > 0 } up) sb.AppendLineLf("reads: " + string.Join(", ", up.Select(u => $"{(string?)u!["name"]} ({(string?)u["kind"]})")));
        sb.AppendLineLf().AppendLineLf("Columns");
        foreach (var c in m["columns"]!.AsArray())
        {
            var native = c!["native"] is JsonObject n ? string.Join("; ", n.Select(p => $"{p.Key}: {(string?)p.Value!["type"] ?? (string?)p.Value["error"]}")) : "";
            sb.AppendLineLf($"  {(string?)c["name"],-22} {(string?)c["logical_type"],-18} {((bool?)c["nullable"] ?? true ? "null" : "not null"),-9} {native}");
        }
        if (m["loads"] is JsonArray { Count: > 0 } loads)
        {
            sb.AppendLineLf().AppendLineLf("Loads");
            foreach (var l in loads) sb.AppendLineLf($"  {(string?)l!["connection"],-10} {(string?)l["operation"],-14} {(string?)l["strategy"],-24} {(string?)l["matrix_status"]}{((bool?)l["is_default"] == true ? "  (default)" : "")}");
        }
        if (m["indexes"] is JsonArray { Count: > 0 } ix)
        {
            sb.AppendLineLf().AppendLineLf("Indexes");
            foreach (var i in ix) sb.AppendLineLf($"  {(string?)i!["name"]}  ({Join(i["columns"])}){((bool?)i["unique"] == true ? " unique" : "")}");
        }
        if (m["foreign_keys"] is JsonArray { Count: > 0 } fk)
        {
            sb.AppendLineLf().AppendLineLf("Foreign keys");
            foreach (var f in fk) sb.AppendLineLf($"  {(string?)f!["name"]}  ({Join(f["columns"])}) -> {(string?)f["references"]!["table"]} ({Join(f["references"]!["columns"])})");
        }
        if (m["index_advice"] is JsonArray { Count: > 0 } adv)
        {
            sb.AppendLineLf().AppendLineLf("Index advice");
            foreach (var a in adv) sb.AppendLineLf($"  {(string?)a!["code"]} {(string?)a["severity"]}: {Join(a["columns"])} ({(string?)a["reason"]}) -> {(string?)a["suggested"]}{((bool?)a["silenced"] == true ? "  [silenced]" : "")}");
        }
        if (m["lowered"] is JsonObject lo) sb.AppendLineLf().AppendLineLf($"Lowered query: {(string?)lo["file"]}   rules: {Join(lo["rules"])}");
        return sb.ToString();
    }

    /// <summary>The models counted by kind: `3 view, 1 mapped`.</summary>
    private static string Breakdown(JsonArray models) =>
        string.Join(", ", models.GroupBy(m => (string?)m!["kind"] ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"));

    private static string Join(JsonNode? n) => n is JsonArray a ? string.Join(", ", a.Select(x => (string?)x)) : "";
}
