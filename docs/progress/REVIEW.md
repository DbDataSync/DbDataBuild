# Review summary

Rewritten 2026-10-06 as one document (it had grown into a dated log of updates; the log with the detail and evidence is `docs/progress/state-and-apply.md`, entries 1 to 125, and what is unfinished is `docs/progress/OPEN-ITEMS.md`). The design is `DESIGN.md`; sections marked "as built" describe what exists. Earlier versions of this file are in git history.

## Where things stand

dbdatabuild is a .NET 10 CLI that builds analytics tables and views on **SQL Server, PostgreSQL and Fabric** from **DuckDB-dialect SQL**: DuckDB binds each model offline, the tool lowers it to one explicit query, transpiles it per engine, and `plan` then `apply` run exactly the statements the plan recorded. Fabric has never been run. **1737 unit tests and 149 real-engine tests** (147 run, 2 skipped) pass; the real engines are SQL Server 2022 and 2025 and PostgreSQL 17 in containers, plus dialect probes for Oracle, Spark SQL and the BigQuery emulator. CI runs the unit suite on Linux and Windows and the conformance groups on every push; a weekly job and the release workflow run everything.

| Area | What exists | Where |
|---|---|---|
| Safety core | read and write logins (no fallback), one mutation gate for every write, one read session for every read, statement log, application lock, risk classes, `--allow-risky` / `--allow-destructive`, resume | DESIGN 10, entries 1 to 9 |
| Plan and apply | pure planner and decision table, plan files with a content hash, `check`, `plan`, `apply`, `run`, `ack`, `report`, backfills, indexes, hooks | DESIGN 11, entries 10 to 27 |
| Lowering and rules | DuckDB's bound plan lowered to explicit SQL (committed under `rendered/lowered/`), support matrix, target rules, subqueries, series, string rewrites | DESIGN 7, entries 20 to 24 |
| Connections | named connections over engines, layered project files (`defaults:`, `=`/`-`/`+`), mapped models (`import`), central or per-connection tracking, `init --upgrade` from older layouts | DESIGN 6.5, 12, entries 62 to 74, 82 |
| Copies | rows between connections (bulk route), fan-in with slices, incremental copies with a watermark, a `full` load that re-reads (`--full-refresh` was removed in entry 156), origin shape checks at each origin's turn, `report` per origin, local copies | DESIGN 6.5.1 to 6.5.4, entries 71 to 77, 83 |
| Parameters | values (project, connection, origin, model; typed; bound at apply), names (`NAME`, for macro arguments) | DESIGN 6.5.3, entries 76, 82 |
| Native models | `select` (inlined as a derived table) and `command` (run, copied; risky), `reads:`, plan-time describe, `track_definition` | DESIGN 6.5.4, entries 77 to 80 |
| Macros | `macros/*.sql` (DuckDB macros and types), loaded on demand, expanded by DuckDB in the lowering; defaults and named arguments work; dependencies and lineage follow | DESIGN 6.5.5, entries 81, 87 |
| Names and strings | a model's name is its definition's `name:`, `model_layout` checks file names; `string_semantics` per connection, DDB-236 and `trimmed` | DESIGN 6.5.6, 7.4, entries 84 to 86 |
| Interfaces | `--format json` on every command with closed schemas, the terminal interface, the MCP server and app, the read-only web page, `agent-kit` | DESIGN 9, entries 27 to 61 |
| Tests and samples | `sample`, `test` (metadata rules and model tests, a gate by tag before `plan`), `seed`/`load-seeds`, `diff` (one connection, or across connections and engines by digests), `graph`, `tag:` selectors, templates | DESIGN 15, entries 37 to 47, 92, 99, 101 |
| Model kind and metadata | one word ("model kind"); `kind` keyed by the kind's name with its settings beneath it (`kind: view` when it has none), the old `{type: ...}` is refused (DDB-248, entry 169); **one model surface** (entry 167): a mapped and a native model are models like the others, one projection (`ProjectModel`), one selector grammar, one document `dbdatabuild.model/3`, one array `models[]`, one subject kind `model`; tracking layout 9 (`subject_kind`, `model_kind` for all seven kinds, the table `metadata_dependency`: direct and transitive dependencies as rows) | DESIGN 6.1, 9.4.1, 9.8, 9.9, 12, entries 163, 164, 165, 167 |
| Documentation | `docs/getting-started.md`, `docs/commands.md` (generated from the command tree; a test keeps it current), `docs/operations.md` | entry 93 |

The command list is `dbdatabuild --help` (every command declares an effect class, printed in its header).

To try it by hand: `scripts/fetch-native.sh`, `dotnet build`, `scripts/test-engines.sh up` and `eval "$(scripts/test-engines.sh env)"` (see `CLAUDE.md`); a project needs `DBDATABUILD_<CONNECTION>_READ` and `_WRITE` connection strings, then `init --apply`, `render --write`, `plan`, `apply <plan> --dry-run`, `apply <plan>`.

## Decisions to look at

**The owner has answered** (in conversation, not recorded one by one in the log): lowering is a committed artifact and a hard error when it cannot be done; the cross-server terms (connection, engine, mapped, native, copy; one word per concept); tracking is off by default with a warning and `tracking: none` is the silent choice; native models come as `select` and `command` with `allow_native_commands`; `track_definition` is an explicit list with its record in the tracking tables and a warning on change; per-connection `string_semantics` over the project's; a model's name is the definition's `name:` (option A), with `model_layout`; change feeds are a backlog item; DuckDB 2.0 CI is held until 2.0 is final.

**Made by me and not yet individually confirmed:**

1. The tracking tables differ from the illustrative DDL in DESIGN 12 (tool-generated GUID ids, a `tracking_version` table, a `connection` column in every key since layout 4).
2. The logical-to-native type table (TINYINT is `smallint`, `VARCHAR(n)` is `nvarchar(n)` on SQL Server, and so on; entry 4).
3. Text columns always carry the profile's collation, never the database default.
4. A plan with blocks is still written for the models that are not blocked (exit code 1).
5. Acknowledgements are keyed by the exact hash of what they accept.
6. Views are tracked by the hash of the applied `CREATE OR ALTER VIEW` text.
7. `run` cannot ask questions; a load with an unanswered runtime parameter is not routine.
8. The planner never drops an index that is not declared.
9. The read guard is conservative (one `SELECT` or `WITH`, no data-changing keyword): it can refuse a harmless query; the read login's permissions are the real enforcement.
10. `model_layout` defaults to `folder` only because every existing project is laid out that way.
11. An unused macro is not created in a binding, and `validate` only warns about one DuckDB refuses.
12. A change to a routine under `track_definition` is a warning (`policy.severity.native_definition_changed` makes it an error).
13. The string profile is a checked declaration, not an imposed behavior. **The owner has parked the whole of string comparison until it is designed as one thing** (`OPEN-ITEMS.md` section P); the pieces built so far came from patches, and nothing is added meanwhile.

## Added since 2026-10-06 (entries 91 to 125)

- **Terminology** (91): *schema name* for the namespace and *schema* for a shape, in documents, diagnostics, descriptions and the C# members; the config key `tracking.schema`, the JSON key `tracking_schema`, `--against-schema` and local variables keep the short name (your decision).
- **Built**: `diff --against-connection` across connections and engines by digests (92); a getting-started guide and a generated command reference (93); fuzzing of hook scripts, macros, native text, tests and the query head (94, no defect found); `ANY`/`ALL`/row-value `IN` as values and aggregates over subqueries on SQL Server (95, 96); `changed:` follows folder files, hooks, macros, native text and rendered files (97); DDB-239 (a slice column no source index leads); a test gate (`tests: { gate: { tags } }`, 99); DDB-240 and `policy.severity.history_inconsistency` (100); model `tags:` and `tag:` (101); DDB-241 (a name too long for an engine, 110).
- **Found by probing and fixed** (the part to read): every statement of `apply` had the drivers' 30-second timeout (106); `apply` re-read the schemas of every object under the schema name after each step, 199 s for 401 steps and 9 s now (108); validation grew with the square of the models, 59 s for 300 and 8.8 s now (107, 109); an unhandled driver exception printed the driver's own text and a stack trace (105); `report` listed a resumed step as unfinished for ever (102); DDB-505 sent a person to `init` when the real cause was a denied catalog permission (103); a 70-character column name on PostgreSQL passed `validate` and failed after it ran (110).
- **More found by probing, all fixed with tests** (112 to 125): SQL Server lost every non-ASCII character of a text literal (`'日本語'` became `???`; the tool now writes `N'...'`, entry 120); a decimal constant of more than 18 digits could not be lowered (121); a zoned timestamp copied from SQL Server to PostgreSQL stopped the step (122); `define --check` rejected a file with a byte order mark (124); `plan` timed out once the tracking log tables held a few hundred thousand rows on PostgreSQL (116); a refused login was reported as an internal tool error (113); the first Ctrl-C killed an apply in the middle of a statement (125); `report` called an object whose model had left the project "in sync" (112); a cross-connection `diff` where most rows differ would have needed gigabytes (114).
- **Verified on real engines**: concurrent applies and a killed connection (102), a read login without VIEW DEFINITION and copies under other time zones (103), a native command copied across engines (104), cross-connection `diff` at 5 million rows (105), an incremental load of 2 million rows (111), names with keywords and quotes (110).

## Things that went wrong or surprised me (all fixed, all with tests)

- The hash canonical text collided for a null and the literal text `~`; my first read guard hid a statement behind a backtick on PostgreSQL; a failed PostgreSQL transaction stays aborted until rolled back; Npgsql refuses UTC-kind timestamps for zone-less columns and turns the extreme dates into infinity (`DriverSettings.Apply`).
- T-SQL refuses `GROUP BY` an expression that names no column (found by the first macro test on SQL Server; the lowerer drops such a key).
- A DuckDB macro binds the names in it when it is created (so macros are created on demand), and a callee must exist before its caller.
- The read login's PostgreSQL session is read-only, so a native command that writes is stopped by the engine; on SQL Server the rollback is what leaves nothing behind.
- Two engines in one project could not satisfy one string profile (SQL Server ignores trailing spaces in a comparison, PostgreSQL keeps them): per-connection `string_semantics` came from that.

## Known gaps and risks

The full, current list is `docs/progress/OPEN-ITEMS.md`. The ones that matter most: Fabric has never run; declaring a model's object in its query file (to revisit); `copy_to` and offline tracking are not built; change feeds are parked; the interfaces have not been checked against real MCP hosts; `apply` is sequential on one connection (a second one is refused by the lock, entry 102).
