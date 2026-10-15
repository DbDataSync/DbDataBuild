using System.Text.Json.Nodes;

namespace DbDataBuild.Cli.Mcp;

/// <summary>
/// The workflows of the skill as prompts a person can pick in the host (a slash command in Claude Desktop, for instance): each is one user message that tells the model what to do with the tools of this server.
/// They carry no rules of their own: the rules are in the skill (`dbdatabuild://skill`), which every prompt tells the model to read first.
/// </summary>
internal static class Prompts
{
    private sealed record Argument(string Name, string Description, bool Required = true);
    private sealed record Prompt(string Name, string Title, string Description, Argument[] Arguments, Func<IReadOnlyDictionary<string, string>, string> Text);

    private const string Preface = "First read the resource dbdatabuild://skill and follow it. Work only through the tools of this server; it offers no command that changes a database, so a person runs those. ";

    private static readonly Prompt[] All =
    [
        new("explore-project", "Explore the project", "Orient in a project: what it holds, how it is layered, what is wrong with it",
            [],
            _ => Preface + "Give me a picture of this project. Call `project_compile` and `project_tests_run`, then `project_show_graph` and `project_show_metadata`. Tell me: how many models of each kind, the layers and what each is for, the largest dependency chains, " +
                 "which models read which mapped and native models, and every error or warning (look up each code with `help_code`). Say what you would look at first. Change nothing."),

        new("add-model", "Add a model", "Write a new model and its definition, check it on sample data, render it and plan it",
            [new("name", "The model's name, as schema_name.table_name (marts.fct_orders)"), new("purpose", "What one row is, and what the model should compute")],
            a => Preface + $"Add the model `{a["name"]}`: {a["purpose"]}\n\nFollow the loop for a model change in the skill. Look at the models it should read (`project_show_metadata`, `project_show_graph`) before writing. " +
                 "Write the query in DuckDB's dialect and the definition with its columns and grain, run `project_compile` until it is clean, check the logic with `project_sample` and add a model test for the logic that is easy to get wrong, run `project_model_update` with `check`, " +
                 "then `project_compile` again and `project_tests_run`. Finish with `connection_deploy` with `write_plan` if a connection is configured for reading, and show me the steps. Ask me about anything the plan asks; do not answer questions about data yourself."),

        new("change-model", "Change a model", "Change an existing model and show what the change reaches",
            [new("model", "The model to change (marts.fct_orders)"), new("change", "What should be different")],
            a => Preface + $"Change `{a["model"]}`: {a["change"]}\n\nStart with `project_show_graph` for `{a["model"]}+` and for the columns involved, and tell me what the change reaches before you edit anything. " +
                 "Then follow the loop for a model change: edit, `project_compile`, `project_sample`, `project_model_update` with `check`, `project_tests_run`, and a plan (`connection_deploy` with `write_plan`) of `changed:HEAD+` if a target can be read. List every model downstream whose result could change."),

        new("fix-findings", "Fix the findings", "Work through the errors and warnings of compile and the failing tests",
            [new("scope", "A selector or model name to limit it to (default: the whole project)", false)],
            a => Preface + $"Run `project_compile` and `project_tests_run`{(a.TryGetValue("scope", out var s) && s.Length > 0 ? $" for `{s}`" : "")} and fix what they report, errors first. For each code call `help_code` and read its fix before changing anything. " +
                 "Do not work around a refusal by changing what it protects, and do not silence advice (DDB-225, DDB-302) yourself: tell me and let me choose. Re-run until clean and say what is left and why."),

        new("review-plan", "Review a plan", "Read a plan file and summarize it for a decision: what changes, what is risky, what it asks",
            [new("plan", "Path of the plan file (default: the newest under plans/)", false)],
            a => Preface + $"Review {(a.TryGetValue("plan", out var p) && p.Length > 0 ? $"the plan `{p}`" : "the newest plan: call `project_show_plan` with no plan to list them")} with `project_show_plan`. Check that it is intact. Summarize it as the skill says: what is created, altered or dropped, what loads, and every risky or destructive step with its reason, " +
                 "and every open question with its options and the tool's proposal. Do not edit the plan and do not apply it: tell me what I would be agreeing to."),
    ];

    public static IEnumerable<JsonNode> Listed() => All.Select(p => (JsonNode)new JsonObject
    {
        ["name"] = p.Name, ["title"] = p.Title, ["description"] = p.Description,
        ["arguments"] = new JsonArray(p.Arguments.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["description"] = a.Description, ["required"] = a.Required }).ToArray()),
    });

    public static JsonObject Get(string? name, JsonObject? arguments)
    {
        var prompt = All.FirstOrDefault(p => p.Name == name) ?? throw new McpException(-32602, $"Unknown prompt: {name}");
        var given = new Dictionary<string, string>();
        foreach (var a in prompt.Arguments)
        {
            var value = arguments?[a.Name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
            if (string.IsNullOrEmpty(value)) { if (a.Required) throw new McpException(-32602, $"The prompt `{prompt.Name}` needs `{a.Name}`: {a.Description}."); continue; }
            given[a.Name] = value;
        }
        return new JsonObject
        {
            ["description"] = prompt.Description,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt.Text(given) } }),
        };
    }
}
