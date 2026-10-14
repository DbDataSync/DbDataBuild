# AI agents and dbdatabuild

How an AI coding agent (Claude Code or another) should work with the tool, and why.

## Use the CLI

An agent should operate dbdatabuild through its **command line with `--format json`**. That interface already exists and is the same one the terminal interface uses:

- every command prints exactly one JSON document whose shape is a closed schema (`schemas/output.schema.json`), with structured diagnostics that each carry a code, a location, what was found, what is supported and a fix;
- every command declares an effect class (offline, repo files, database read-only, tracking tables, data writes), so what an agent may run without asking is decided by the command, not by trust;
- the plan file is the review gate: an agent can plan (`connection deploy --write-plan`), a person reads the plan, and applying it runs exactly what was recorded and refuses an edited plan; the write login is a separate environment variable that an agent's shell should not have;
- the agent's tool permissions can follow the same line (below), and nothing needs to be running in the background.

**Not an HTTP service.** A server would add a long-lived process, authentication, and a network surface to a tool whose design is explicit, offline-first and plan-then-apply, and it would offer nothing the CLI cannot do.

**MCP.** `dbdatabuild ui mcp` is a stdio MCP server over the same command layer: typed tools, and the schemas, the support matrix and the explanation of each code as resources. It offers the commands that only read, `project compile` and the rest of the `project` group, and `connection deploy` to plan; it leaves applying a plan, `connection refresh`, `connection init --apply`, `connection publish` and `--ack` off unless an operator starts it with `--allow-writes`, and then each run is put to the person through the host. `docs/interfaces.md` has the details.

## Give the agent the knowledge

The tool has rules a model will not guess: the SQL is DuckDB's dialect and the engine SQL is generated; `grain` and `unique_key` mean specific things; indexes are never inferred; some constructs are refused with a reason; a plan must not be edited; questions about data belong to a person. `dbdatabuild project agent-kit --write` installs a **skill** (`.claude/skills/dbdatabuild/SKILL.md`) and the JSON Schemas next to it. They are embedded in the executable, so they always match the version of the tool; `agent-kit --check` in CI fails when an installed copy is stale, and tests keep every command, option, diagnostic code and schema the skill names real.

In a project's own `CLAUDE.md` (or equivalent), a pointer is enough:

```
This is a dbdatabuild project. Read .claude/skills/dbdatabuild/SKILL.md before writing models or running dbdatabuild.
Run dbdatabuild with --format json. Never apply, run, ack or publish without my say-so.
```

## Permissions that match the effect classes

For Claude Code, allow what changes nothing and make a person approve the rest. For example, in `.claude/settings.json` (check the current Claude Code documentation for the exact syntax of your version):

```json
{
  "permissions": {
    "allow": [
      "Bash(dbdatabuild project compile:*)", "Bash(dbdatabuild project sample:*)", "Bash(dbdatabuild project show metadata:*)", "Bash(dbdatabuild project show loads:*)",
      "Bash(dbdatabuild help matrix:*)", "Bash(dbdatabuild help dialects:*)", "Bash(dbdatabuild help code:*)", "Bash(dbdatabuild project compile:*)", "Bash(dbdatabuild project model update:*)",
      "Bash(dbdatabuild connection status:*)", "Bash(dbdatabuild connection deploy:*)", "Bash(dbdatabuild connection monitor:*)", "Bash(dbdatabuild project agent-kit:*)"
    ],
    "ask": [
      "Bash(dbdatabuild connection deploy --apply-plan:*)", "Bash(dbdatabuild connection refresh:*)", "Bash(dbdatabuild ack:*)", "Bash(dbdatabuild connection init:*)", "Bash(dbdatabuild connection publish:*)"
    ]
  }
}
```

Give the agent's shell the **read** login only (`DBDATABUILD_<TARGET>_READ`). With no write login in its environment the commands that change a database cannot succeed from the agent's session, whatever it is asked to do; a person applies a plan from a shell that has it (or from `dbdatabuild ui terminal`).

## What an agent is good for here

Writing and changing models and sources; reading `project compile` and plan output and explaining it; running `project sample` to check logic on generated data; keeping `rendered/` and definitions in sync; summarizing a plan's risky steps for a reviewer. What it should hand back to a person: the answers to data questions, the decision to apply, and any `ack`.
