using DbDataBuild.Tui.Model;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>
/// The options of one command as a form. Every option and argument of the command is a field (generated from its definition); the line a person could type to do the same
/// thing is shown and updated as the fields change.
/// </summary>
public sealed class FormWindow : Ui.Modal
{
    private readonly TuiSession session;
    private readonly Label problems;
    private readonly TextView preview;
    private readonly Label help;

    public FormModel Form { get; }

    public FormWindow(TuiSession session, CommandInfo command, Action<FormModel>? prefill = null) : base($"{command.Name}  |  {command.Effect}", 92, 92)
    {
        this.session = session;
        Form = new FormModel(command, session.ProjectRoot);
        if (Form.Fields.FirstOrDefault(f => f.IsArgument && f.Label == "connection") is { } t && session.EnsureTarget() is { } target) t.Value = target;
        prefill?.Invoke(Form);

        var y = 0;
        foreach (var field in Form.Fields)
        {
            var label = new Ui.PlainLabel { X = 0, Y = y, Width = 26, Text = field.IsArgument ? $"{field.Label}{(field.Required ? " *" : "")}" : field.Label };
            View control;
            if (field.Kind == OptionKind.Flag)
            {
                var box = new CheckBox { X = 27, Y = y, Text = "", Value = field.Checked ? CheckState.Checked : CheckState.UnChecked };
                box.ValueChanged += (_, _) => { field.Checked = box.Value == CheckState.Checked; Refresh(); };
                control = box;
            }
            else
            {
                var text = new TextField { X = 27, Y = y, Width = Dim.Fill(1), Text = field.Value };
                text.ValueChanged += (_, _) => { field.Value = text.Text ?? ""; Refresh(); };
                control = text;
            }
            control.HasFocusChanged += (_, e) => { if (e.NewValue) help!.Text = Hint(field); };
            Add(label, control);
            y++;
        }

        help = new Ui.PlainLabel { X = 0, Y = y + 1, Width = Dim.Fill(1), Height = 2, Text = "" };
        var line = new Ui.PlainLabel { X = 0, Y = y + 3, Text = "Same thing on the command line:" };
        preview = new TextView { X = 0, Y = y + 4, Width = Dim.Fill(1), Height = 3, ReadOnly = true, WordWrap = true };
        problems = new Ui.PlainLabel { X = 0, Y = y + 7, Width = Dim.Fill(1), Text = "" };
        var run = Ui.Button("Run", Submit, isDefault: true);
        run.X = 0; run.Y = Pos.AnchorEnd(1);
        var cancel = Ui.Button("Cancel", () => Close(session.App));
        cancel.X = Pos.Right(run) + 2; cancel.Y = Pos.AnchorEnd(1);
        Add(help, line, preview, problems, run, cancel);
        Ui.EscapeCloses(this, session.App);
        Refresh();
    }

    private static string Hint(FormField f)
    {
        var parts = new List<string> { f.Description };
        if (f.Choices.Count > 0) parts.Add("One of: " + string.Join(", ", f.Choices) + ".");
        if (f.Kind == OptionKind.List) parts.Add("Several values: separate them with commas.");
        if (f.Default is { Length: > 0 } d && f.Kind != OptionKind.Flag) parts.Add($"Default: {d}.");
        return string.Join("  ", parts);
    }

    private void Refresh()
    {
        preview.Text = Form.CommandLine();
        var p = Form.Problems();
        problems.Text = p.Count == 0 ? "" : string.Join("  ", p);
    }

    private void Submit()
    {
        if (Form.Problems().Count > 0) { Refresh(); return; }
        Close(session.App, confirmed: true);
    }
}
