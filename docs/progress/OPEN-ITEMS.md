# Open items and status

Written 2026-10-02, brought up to date 2026-10-07. Unit tests 1817, real-engine tests 184 (179 run and 5 skipped, among them the scale tests that need DDB_SCALE=1; SQL Server 2022 and 2025, PostgreSQL 17, and the dialect probes for Oracle, Spark SQL and the BigQuery emulator). CI runs the unit suite on Linux and Windows and the conformance groups on every push (section H). Fabric has never been run against a real engine.

## Where the milestones stand (DESIGN.md section 16)

| # | Milestone | State |
|---|---|---|
| 1-3 | Spike, foundations, test infrastructure | Done |
| 4 | State and safety core | Done (gate, logins, statement log, lock, invariant test) |
| 5-6 | Planning, apply | Done, with risk classes, resume, hooks, indexes, backfill, `ack`, `report` |
| 7 | Incremental kinds, loads, `run` | Done |
| 8 | PostgreSQL target | Done and verified. **Fabric target: written, unverified.** Operations guidance for SQL Server Audit: not written |
| 9 | Hardening | **Mostly done** (entry 25): seeded fuzzing of config, source, model, SQL, answers and plan files; the real-engine error-scrub test; linux-x64 single-file publish; operations guide (`docs/operations.md`). The Windows executable is built and published by the release workflow, and the unit suite runs on Windows in CI. Open: fuzzing of the interactive question flow, the pre-1.0 polyglot upgrade policy (the command reference is generated, entry 93) |
| extra | Plan lowering | Built (section 7.6) |

## A. Lowering: open items (the ones you asked to have written down)

1. **Target-specific rules: built** (DESIGN.md 7.6.1, entries 24, 40 and 126): about thirty rules (`rewrites:` lists them, `dbdatabuild matrix --rewrites`), and about 200 expressions probed on both engines against DuckDB. What is known to differ and has no rule is in `docs/research/engine-differences` and the matrix (`approximated`, `unsupported`); do not copy that list here, it goes stale. Not yet probed: window functions with frames, `list`/`struct` results, string functions beyond the probe list.
2. **`sum` widening: built** (lowering rule `sum-widen`). `sum` of DECIMAL needs no rule on SQL Server (both give DECIMAL(38, s)); PostgreSQL gives NUMERIC without a scale limit, which a declared column then constrains. Not yet probed: `avg` and `sum` over very large DECIMALs (and, with the aggregates above, a column that holds more than 28 digits).
3. **`x op ANY/ALL (subquery)` and row-value `IN`: built as filter conditions** (DESIGN.md 7.6, entry 39), verified against DuckDB on data with NULLs and on both engines. Used as a *value* (select list, `CASE`, `IS NULL`, `ORDER BY`, `GROUP BY`) they are a `CASE` over the two predicates (entry 95, verified on both engines); a mark in a join condition is still refused; a correlated row-value `IN` is not supported by DuckDB itself. **On the DuckDB 2.0 alpha these plans have a different shape (a count-based CASE) and 9 of the new tests fail there, one with wrong rows: they have to be reworked when 2.0 is adopted** (the preview job is on hold).
4. **Correlated subquery over `UNION`/`INTERSECT`/`EXCEPT`, a window partitioned by a correlated value, a correlated `LIMIT` with an offset.** Refused; no plan to build unless a real model needs them.
5. **Author table aliases: built** (DESIGN.md 7.6; entry 89): the aliases written for tables come back in the lowered query. Not for a query with a CTE or one that reaches a macro (they keep the table names); derived-table and subquery aliases the author wrote (`FROM (SELECT ...) x`) are still generated names (`s1`).
6. **Date/timestamp series, `UNNEST`, list/struct constructors, `USING SAMPLE`, `LIMIT ... PERCENT`.** Refused by decision. A date series could be an integer series plus `DATEADD`/interval arithmetic; it is left out because every interval unit needs its own differential check.
7. **Aggregate over a subquery: built** (entry 96). SQL Server rejects `sum((SELECT ...))` and a `GROUP BY` on a subquery; the lowerer computes the subquery in a derived table first and the aggregate reads its column.
8. **Plan JSON is DuckDB-internal.** A DuckDB upgrade can change plans, which shows as a stale lowered artifact in `render --check`. The header records the DuckDB version; there is no tooling to explain *why* an artifact changed.
9. **Evidence gaps**: none known. The `fn.generate_series` spike case was run on 2026-10-07 (SQL Server 2022: polyglot's raw text is refused because of the column list, which the renderer drops; PostgreSQL: MATCH); the matrix note says so.

## B. Features designed or reserved but not built

- **Hook events for drops and other reserved kinds** (`HookEvents` marks them `Fires = false`; the model loader rejects them with a message). Only the events the planner actually performs are accepted.
- **`define` asking about extra loads** (section 6.5): not built.
- **Index lint and generation: built** (entry 26). Not built: adding indexes when `define` updates an existing definition (the surgical editor has no block insertion), a `drop`/exclusive setting for undeclared indexes (the planner still never drops one), and lint for the partial or filtered indexes the model syntax cannot express yet.
- **JSON Schemas for each command's `data` and the metadata documents: built** (entry 27). Not done: marking keys as required per outcome (the schemas only close the key sets and fix types), and a published JSON Schema for the plan YAML's embedded JSON beyond `plan.schema.json`.
- **History consistency as a blocking condition: built** (DDB-240, `policy.severity.history_inconsistency`, entry 100): the warning and, with the policy at `error`, a block for the models downstream of an unacknowledged inconsistency, by column lineage.
- **Redaction** of logged parameter values and resolver results (decision 6 says it can be added later).

## C. Unverified or risky areas

- **Fabric**: every Fabric matrix row is `unverified`. Needs a real Fabric Warehouse to confirm MERGE/ALTER/TRUNCATE/rename, `nvarchar(max)` and constraints in the tracking tables, `sp_describe_first_result_set`, trailing-space and `LEN` behavior, collations, and the `GENERATE_SERIES` form. Draft upstream issue notes exist; the owner has not decided to submit them.
- **Lock, resume and failure paths** are tested on SQL Server and PostgreSQL, on single local containers: a second `apply` started while a first one runs is refused and both leave the target consistent, and an `apply` whose connection is killed mid-step fails with DDB-440 and resumes (entry 102). Not tested: many concurrent applies under load, a network partition that does not close the session at once (a half-open TCP connection), a server restart mid-step.
- **Native dependency**: polyglot-sql is pinned and fetched prebuilt from the `native-<pin>` release (`scripts/fetch-native.sh`; `scripts/build-polyglot.sh` and the `native` workflow build it); pre-1.0 API churn and an upgrade policy are open (section 17).
- **Windows**: the unit suite runs natively on `windows-latest` on every push (CI), the release workflow builds the win-x64 single-file executable, and the owner has run the tool, the terminal interface, the skill and the MCP server on Windows (2026-10-07: they work at some level; not exercised thoroughly). Not done: PostgreSQL and the real-engine suite on Windows (PostgreSQL cannot log in under Wine; the conformance groups run on Linux), code signing and an installer, and a win-arm64 build.
- **Licenses**: audited 2026-10-02 (all permissive; see `THIRD-PARTY-NOTICES.md`); re-check when dependencies change.
- **Collation**: chained collations under `GROUP BY`/`DISTINCT`/joins/windows on DuckDB and the engines are only partly verified (section 17). Live collation checks exist for SQL Server and PostgreSQL.
- **Error scrubbing and fuzzing**: done for the file inputs (entry 25). Seeded mutation also covers hook scripts, macro files, native query text and its definition, metadata and model test files and a query file's head (entry 94). Not covered: fuzzing the interactive answer flow and the resolver query results.

## D. Documentation debt and housekeeping

- Operations guide written (`docs/operations.md`; Fabric parts say "not checked"). Written 2026-10-07: `docs/getting-started.md` and `docs/commands.md` (generated from the command tree; `CommandReferenceTests` fails when it is out of date). Not written: per-engine setup guides (logins and permissions are in `operations.md`).
- REVIEW.md was rewritten as one document on 2026-10-06 and extended on 2026-10-07; keep it a summary (the log is `state-and-apply.md`).
- **Housekeeping the tool does not do** (was D0): plan files and statement logs from a scheduled `run` pile up (about 2.3 MB for a run of 100 loads, entry 118). The tool deletes nothing; `docs/operations.md` section 4 says how to prune them. Open for the owner: a retention setting (`run` keeping the newest N plans, and a log age), or writing the plans of `run` somewhere that is not the project tree.

## E. Suggested order (for you to change; refreshed 2026-10-07)

0. **Deploy and refresh as two lanes, one event record, consistent verbs and hints** (design: `docs/research/lifecycle-model.md`; plan and status: `docs/research/lifecycle-implementation.md`; for users: `docs/concepts.md`; entries 130 and 131). Built so far (entries 132 to 138): the command tree, events for refresh, the rows of a load, headers and `Next:` hints, the settings, the compiled refresh plan with its checks, layout 5. Left: the deploy event as its own id, `monitor --plan`, more interface hints (terminal tabs), the positional connection name, `--backfill` for a refresh.
1. **Decisions only the owner can make** (section R): `copy_to` and offline tracking (section M), retention of files from `run`, the test gate's recording in the plan, MCP `outputSchema`.
2. **Real-host checks** (sections F and K): the skill and the MCP server have been tried from Windows and work at some level; they have not been exercised thoroughly. The MCP app has not been seen in a real host (see section F: the host has to advertise MCP Apps), and the page's apply and the elicitation have not been checked against one.
3. Lowering gaps as real models need them (section A), probing with scale and edge data (it found most of the defects of 2026-10-07), and the documentation debt (section D).
4. Fabric verification (moved to the back by the owner, 2026-10-02): needs a real Fabric instance; otherwise Fabric stays unverified for the first release.
5. **Parked**: `connection tests run` (its meaning was never settled; parked 2026-10-12), string comparison until it is designed as a whole (section P), change feeds (section L), and more options in a query file's head (section N). The DuckDB 2.0 CI (section H) waits for 2.0.

## F. Terminal interface and agents (added 2026-10-02)

- **TUI gaps**: progress and stop-between-steps are built (entry 33); a stop cannot interrupt a long single statement (by design: a started statement is never abandoned), and a `plan` or `sample` that takes minutes cannot be cancelled; forms do not scroll on a terminal shorter than the longest form (`plan`, 9 fields); no menu bar or mouse testing; the terminal interface runs on Windows (checked by the owner); view code is covered only by the pty walk-through (`scripts/tui_drive.py`), not by unit tests; the target chosen in the TUI is not shown in the title until the next screen change; `define` is reachable but its interactive prompts are answered through dialogs only for open questions, not for the accept/inferred flow.
- **Sample data gaps**: string comparison follows the model's connection profile (collation and `rtrim`, DESIGN.md 7.4), other target behaviors are not emulated (it runs in DuckDB only); a source whose type has no generator needs a CSV; no PIVOT/seed values yet; generated values do not respect CHECK-like rules that are not declared.
- **Agents**: the MCP server exists (`dbdatabuild mcp`, section K: typed tools and schema resources, writers off by default); the owner has tried the skill and the MCP server from Windows (2026-10-07) and they work at some level, but not thoroughly: the skill still needs a real task and revision from what an agent gets wrong (the tests keep it true, not useful); no per-engine variants of the skill (it says Fabric is unverified).
- **The MCP app is only offered to a host that says it supports MCP Apps** (the `io.modelcontextprotocol/ui` capability when it connects). On 2026-10-07 the owner could not make Claude Code in the Claude Desktop app on Windows use it, which is what that rule predicts if the host does not advertise the extension: the server then offers no app and no `show` tool, and says nothing in the conversation. `dbdatabuild mcp` now writes one line to its standard error at connect (client name, whether MCP Apps and confirmations are supported), and `show` is offered to every host: where the app is not advertised it returns a one-time link to the page in a browser (entry 129; whoever opens the link first gets the session). Which hosts advertise MCP Apps is not known here.

## G. After the slice and parameter work (2026-10-02)

- Predicate pushdown and runtime parameters are no longer open questions; what remains: a first-class **custom slicing operation** (author-written filter with a declared parameter; allowed today by writing it in the query and using `full_replace` or a key load, but not modelled), and **seeded parameter values for `sample`** so the strategies can be previewed. (The type of a parameter is checked by the loader: VARCHAR, BIGINT, INTEGER, SMALLINT, DATE, TIMESTAMP or NAME.)
- The slice lint follows plain column lineage; it does not judge sargability of an expression slice column (`CAST(ts AS DATE)`); whether the slice column is indexed on the source is DDB-239 (entry 98), for a source that declares indexes or a grain.

## H. DuckDB 2.0 and the repository (2026-10-02)

- **DuckDB 2.0 adoption** (see `docs/research/duckdb-2.0/README.md`): lowering work is done (all unit and real-engine tests pass on the alpha). Open: it depends on the deprecated `delim_join_as_cte` setting (if it is removed, write the inverse decorrelation: 16 forms); wait for a DuckDB.NET release built for 2.0; then regenerate committed lowered artifacts (their headers carry the DuckDB version) and make 2.0 the default. A CI job running `scripts/test-duckdb-preview.sh` weekly would show convergence.
- **GitHub**: the repository is public. `ci.yml` runs on every push to main and every pull request: the unit suite on Linux and Windows, and the conformance groups against real engines (`quick`, `apply`, `templates`, SQL Server 2025, and the Oracle, Spark SQL and BigQuery probes); `conformance-full.yml` runs everything weekly and on demand; `release.yml` runs on a `vX.Y.Z` tag, gates on the full conformance, builds the linux-x64 and win-x64 executables and publishes the release (and the Scoop manifest). Open: branch protection, issue templates, a pull-request template. Commits made with this repository's git configuration carry `dan.shryock@gmail.com` from 2026-10-06; earlier ones carry the old identity (rewriting them would need a force-push, which was not asked for).


## I. Sources and metadata (2026-10-02)

- **`import`** (was `import-sources`, entry 36) is built and verified on SQL Server 2022 and PostgreSQL 17; Fabric is unverified (the catalog queries are the SQL Server ones). Open: collation is not exported (a native collation has no honest logical name: the logical names are the project's own profile); a refresh rewrites a descriptor without its comments; a source's declared indexes are read by the slice lint (DDB-239, entry 98), not yet by anything else; `--check` needs a database login in CI.
- **Metadata** now covers the project, sources and models. Not yet in metadata: the diagnostics catalog and the support matrix (both are printed by `explain` and `matrix`, and the matrix hash is in the project document), observed live shapes of sources (`import --format json` has them), and the tool's own tracking tables.

## J. Project tests (2026-10-02)

- **Built**: metadata rules (`test`, entry 37) and model tests (entry 38). **Gating `plan`/`check`/`run` on tests by tag group is built** (`tests: { gate: { tags } }`, entry 99); not built: a severity floor for the gate, the result in the plan file and its hash, gating `apply`; the `metadata_*` views in the target (only `metadata_current` and `metadata_columns` exist there); `#` comment settings for model YAML (only test files have them); floating-point tolerance, parameterized loads, multi-run incremental behaviour, and array or struct values in model tests; running model tests on a real target.

## K. Graph and diff (2026-10-03)

- **Built**: selectors (including `tag:` and `changed:`, which follows folder files, native text, hook scripts, macro files and rendered files, entries 97 and 101) and `graph` (entry 42); `diff` in one connection (entry 43) and across connections and engines (entries 92 and 114). **Not built**: a floating-point comparison and a row filter for `diff`, comparing a table with a DuckDB run of its model; `graph` does not draw load strategies or hooks; change impact is a list, not yet a classification (breaking or not) that would drive a plan.
- **Loading from a source query**: `load-seeds` loads seeds (DuckDB queries) into SQL Server and PostgreSQL (entry 45). Not built: loading from a query on another engine (SQL Server or PostgreSQL to DuckDB or to each other), CSV through DuckDB `read_csv` (seeds run with external access off).
- **Templates**: `starter`, `retail`, `chinook` and `adventureworks` are built (`dbdatabuild new`). Backlog: none named.
- **Refused constructs the templates found** (docs/research/template-findings.md): `split_part` on SQL Server, a lateral or date series (`generate_series` bounds from another table, or dates), `UNNEST` of list columns, a quantile with a list of fractions or a DISTINCT or FILTER, `json_extract`/`json_valid`/`json_type`/`json_keys`, `json_array_length` and every regular expression on SQL Server, `nth_value` on SQL Server.
- **Recursion on SQL Server**: a hierarchy deeper than 100 levels fails (error 530); `OPTION (MAXRECURSION n)` cannot be put in a view, so it needs the load statement of a table.
- **Decimal products wider than 38 digits** round at scale 6 on SQL Server (matrix `type.decimal_product_wide`); no rewrite makes SQL Server exact.
- **Oracle, Spark SQL, BigQuery emulator** (entries 48 and 49): probed only, with first rules (Spark 253 of 267 agree, BigQuery 175, Oracle 119). Needed to make them targets: target rules for what the scoreboard shows, a matrix column each (the rows say `unverified` for nothing today: the loader requires the three existing targets), a DDL type table, tracking tables, load strategies, a driver behind `MutationGate`/`ReadSession`.
- **How BigQuery is tested** (decision, entry 49): the emulator stays for analysis-level checks (does the rendered SQL parse and resolve, are function and part names valid). Its row answers are not authoritative: do not write rules for differences that only the emulator shows (it runs GoogleSQL analysis over a SQLite executor). Alternatives for row-level truth, to look at later: real BigQuery on the free sandbox as an opt-in release check, GoogleSQL's `execute_query` built from `google/googlesql`. Also open: the probe run time (33 s to 9 min), a per-query timeout.
- **MCP server** (entry 50): built for tools, resources and prompts; `scripts/pack-mcpb.sh` builds an `.mcpb` bundle. Open: per-tool `outputSchema` (section R), a no-values test over results, and thorough use in real hosts (tried from Windows, entry above in section F).
- **Web interface** (entry 51): read-only screens built; open: accessibility, and a VS Code shell. (Plan review, apply with confirmation, answers, diff and sample data are built, entries 53 to 56.)
- **Page and MCP writes** (entry 54): built with approval; open: check elicitation against a real host, `apply --resume` from the page, the questions form against a real model change.
- **MCP app** (entry 56): built and driven with a stand-in host and the extension's reference host; never seen in a real host. It is offered only to a host that advertises MCP Apps (section F). Open: a host that does (checklist in `docs/interfaces.md`), attaching the `.mcpb` bundle to releases once a host has installed it.
- **SQL Server version** (entry 58): `version` means the T-SQL level to generate for (16: 2022 and 2025@160; 17: 2025@170) and from 17 the regular expressions are written; open: the `i`/`s` regex options, other 2025 features (a native JSON type, vectors) are not used. The SQL Server 2025 group runs in CI.
- **Oracle empty string** (entry 59): measured and designed (DESIGN.md 7.4), not built: needs Oracle as a target and a matrix column.
- **`agent-kit --mcp`** (entry 60): written from Claude Code's documentation; the owner has used the MCP server from Windows (2026-10-07, works at some level); still to check: `${VAR:-}` with an empty default for an unset login, and the approval prompt.
- **Cross-server and domains** (entries 62 to 82): built; what is left is in section M.



## L. Backlog: change feeds (2026-10-06)

- **Change feeds** (native models over `CHANGETABLE`, PostgreSQL change capture and the like, with deletes applied through `deleted_when`) are **parked**: the design is incomplete. Known from `docs/research/native-queries.md`: a feed is a native model used as a copy origin; slot-consuming feeds (logical decoding, where reading changes engine state) are refused in a first version, perhaps later `access: command` with an explicit `consumes: true`; today an incremental copy never sees origin deletes. Undecided: the `deleted_when` syntax and meaning, where a feed's own position (a sync version, an LSN) is kept in place of a column watermark, and ordering and replay safety. Nothing is built.

## M. Cross-connection work: what is built and what is not (2026-10-06)

- **Built** (entries 66 to 82): connections and engines, layered project files, mapped and native models, copies (remote and local, fan-in with slices, incremental with a watermark, `--full-refresh`, the origin shape check for mapped, native and built-model origins, `on_mismatch`), central or per-connection tracking and its upgrade from layout 3 (`init --upgrade`), parameters (values and names), native selects and commands (`reads:`, plan-time describe, `track_definition`), macros and types, `report` per copy origin.
- **String semantics across engines**: parked until the owner designs it as a whole (section P). Nothing more is added to it before that.
- **Not built**: `copy_to` (records replicated to further connections); offline tracking and catching up; a copy that selects columns or filters rows (decided: do it at the origin with a model); deletes at the origin in an incremental copy (a plain copy reconciles; change feeds are section L); the lowering of a macro's enum-typed expressions; Fabric for any of it.
- **Not verified on a real engine**: Fabric (never run); large objects in copies (and time zones: the copy and cross-connection `diff` tests pass with the host in America/Los_Angeles and Asia/Kolkata for `TIMESTAMP` and timestamp-with-zone columns, entry 103; not varied: the server's own time zone).

## N. Schema name and object name out of the folder path (2026-10-06)

- **Built**: the name is the definition's `name:` (`model_layout: folder | dotted | object | none`, `DESIGN.md` 6.5.6), and a query file may start with a head, `CREATE TABLE schema_name.object_name WITH (kind = ..., unique_key = (...)) AS` or `CREATE VIEW schema_name.object_name AS`, which says the name, table or view and the reload options (`DESIGN.md` 6.5.7; the options and what is open are in `docs/research/model-naming.md`).
- **Parked (owner, 2026-10-06)**: more options in the head (`grain`, `connections`, `loads:`), heads for native models and copies, `define` writing a head into a `.sql` that has none. The head keeps its four reload options (`kind`, `unique_key`, `time_column`, `lookback`) until the owner picks this up. **Parked (owner, 2026-10-06)**: a per-connection **schema name** (a dev/prod split as a setting, not a name). Terminology: *schema name* is the namespace (`marts`), *schema* is only the defined shape of a table or view (`DESIGN.md` 6.5.6).

## O. Limits recorded in the progress log, collected here (2026-10-06)

- **Macros** (entries 81, 87): folder-scoped macros; a `NAME` parameter reaches a query only as a macro argument; enum-typed expressions are not mapped to engine types.
- **Native models** (entries 77 to 80): the plan-time describe does not compare nullability and does not describe commands; `reads:` is not checked against the text; a command copied across engines is verified in both directions (entry 104).
- **Parameters** (entry 76): there are no boolean, decimal or double parameter types (a boolean has two values, so it cannot be marked uniquely in the query; the others need markers that survive the lowering); a view cannot use value parameters.
- **Tracking** (entries 74, 82): `copy_to`, offline tracking; `init --upgrade` handles layouts 2 and 3 and not Fabric.
- **Copies** (entries 71 to 75, 83): `--full-refresh` does not see rows deleted at the origin; author aliases are not recovered for derived tables and subqueries (section A5).
- **Support matrix**: a `current_date` depends on each engine's time zone (`fn.current_date`).

## P. String comparison: parked until it is designed as a whole (2026-10-06)

- **Decision (owner)**: the string profile was built by patches (a project setting, a per-connection override, a lint, a `trimmed` declaration and its check, a DuckDB-side emulation), each answering a question that a whole design would have answered together. Continuing to patch it would leave more code and configuration than the design needs. It is **frozen**: no further string-profile settings, lints, rewrites or emulation are added until the owner has time to design it. Bugs in what exists are fixed.
- **What exists today** (so the design starts from facts, not from the history): `string_semantics` at the project (case, accent, trailing_space, collations per logical name and engine) and `connections.<name>.string_semantics`; collation checks offline and against the live catalog (DDB-310 to 312); explicit collations in all DDL; the `LEN` rule and matrix rows `str.*` (mostly `approximated` on the engines); DDB-236 (a model comparing strings on connections that disagree; `lint_ignore`); `trimmed: true` on columns and its count in `check` (DDB-237); `sample` and `test` run under the model's first connection's profile (a `default_collation` and `rtrim()` for trailing spaces; not set operations, `count(DISTINCT)` or `LIKE`). Nothing rewrites a comparison on an engine. The review is `docs/research/string-semantics-across-engines.md`.
- **What a design has to decide** (a list of questions, not proposals): what "string semantics" covers (case, accent, trailing space, collation, ordering, `LIKE`, length, concatenation with NULL, the empty string, Unicode normalization, identifier case); who owns it (project, connection, folder, model, column, expression) and in how many places it may be said; what is promised (a declaration that is checked, a behavior the tool imposes by rewriting, or a result that is tested against DuckDB), and what a model on connections that disagree gets; where emulation lives (DuckDB runs, the engines, both) and what it costs (index use); how data (`trimmed`) and query (rewrites) share the work; how it relates to the lowerer, the matrix and `rewrites:`; and how one place in the configuration says all of it.

## Q. Names that still say "schema" for a schema name (decided 2026-10-06: leave)

The owner decided to keep these. A short name is fine where it is plainly a key or a variable; what must say "schema name" is prose: error messages, documentation, conversation.

- Config: `tracking: { connection, schema }` and `connections.<name>.tracking.schema`.
- JSON documents: `tracking_schema` (the `init` output and the project metadata document).
- CLI: `diff --against-schema`.
- Code: local variables and parameters called `schema` or `schemas`.
- Correct under the terms: `schema_version` and `TrackingSchema` (the defined shape of the tracking tables), `diff`'s `schema` payload (column shapes), JSON Schema.

## R. Left from the 2026-10-07 work: decisions and limits (entries 91 to 129)

For the owner to decide:

- **Retention of files from a scheduled `run`** (section D, entry 118): plan files and statement logs pile up; the tool deletes nothing.
- **The test gate** (entry 99): it runs before `plan`, `check` and `run` read the target; the result is printed, not recorded in the plan file or its hash (that changes the plan format), there is no severity floor, and `apply` is not gated. The history policy `history_inconsistency` (entry 100) defaults to a warning.
- **MCP `outputSchema`** (section K): each tool could declare the schema of its `data`, but a tool that declares one must return conforming `structuredContent` every time, and a truncated or failed document does not. Not done; it needs a decision on what a truncated result looks like.
- **Names too long** (DDB-241, entry 110) are an error for PostgreSQL's 63 bytes and SQL Server's 128 characters; the tracking schema name and parameter names are not checked.

Limits, written down:

- **Decimals of more than 28 digits cannot be copied** between connections (the drivers' `decimal` holds 28; the transfer stops with a message, it does not round).
- **Fabric** has none of the work done in this stretch checked: the `N'...'` literals (entry 120), the `now()` rule (126) and the cross-connection digests (92, which also need `HASHBYTES` and a UTF-8 collation there) are applied to SQL Server only or unverified.
- **Cross-connection `diff`**: floating-point columns and a PostgreSQL `numeric` without precision are not compared; a comparison that differs in most rows stops at the buckets (114); about 25 s a million rows.
- **Probed and found sound** (no entry needed beyond the log): names with keywords, spaces, accents and quotes (110), text edge values in copies (123), CRLF and paths with spaces and accents (124), a case-sensitive SQL Server database (124), a 1,500-view chain, a 600-column model, 300 models (107, 109), a server at -07:00 and one at +05:30, a German SQL Server session (127).

## S. Samples and templates: what the 2026-10-08 lifecycle work did not reach (added 2026-10-08; done 2026-10-12, entry 144)

Done (details and what was verified on which engine: entry 144):

- **Lifecycle settings** are a commented `plans:`/`refresh:`/`retention:` block with non-default, illustrative values in `retail` (`keep: database`, `check: live`), `chinook` (`keep: ephemeral`, `check: project`, `on_fail: warn`) and `adventureworks` (`audit: full`), as in `starter`. Each was uncommented and run once on SQL Server.
- **Walk-throughs** are in the READMEs: `retail` has the long one (inspect, seed, init, status, deploy, refresh, status, monitor with `--event`, `--lane`, `--attention`, the four `refresh.check` levels with the message each gave, the settings), `chinook` and `adventureworks` the short one with their own counts, `stores` the multi-connection one, `macros` the expansion. Only output of real runs is quoted.
- **`retail`, `chinook`, `adventureworks`, `stores` and `macros` were walked through the new cycle by hand** on SQL Server 2022 and PostgreSQL 17 (`stores` across four databases). What it found is in `docs/research/template-findings.md` (M1 to M7) and was fixed or documented.
- **Multi-connection sample**: a new template, `stores`, not additions to `retail` (retail is one connection with a SQL Server profile, and every conformance and unit case over it assumes that). It has a fan-in copy with a slice from two origins into one table (on both engines), a fan-out model on SQL Server and PostgreSQL, central tracking, per-connection string semantics shown (`connections.postgres.string_semantics`, `trimmed`, `lint_ignore: [DDB-236]`; nothing added), and refresh plans with the copy under `excluded`.
- **Dynamic macros**: a new template, `macros`: scalar macros, table macros (`AS TABLE`, one calling another and a scalar one, a default parameter), a type alias, one macro for a live table and its history, `NAME` parameters at project scope; the README says a macro is never run by an engine. Tests: a unit test over the rendered files, and the `templates` conformance group builds it on both engines and compares every mart with DuckDB.
- **Template tests**: `Group=templates` covers `stores` (`StoresTemplateConformanceTests`) and `macros` (in `TemplateConformanceTests`); the unit test over all templates now also runs `model update --check`.

Left:

- **A `NAME` parameter per connection** (one macro, two connections of one engine, each with its own table; DESIGN.md 6.5.5) is covered by `MacroConformanceTests` but is not in a template: the `macros` template uses the project scope. A template needs two databases of one engine for it.
- **An incremental copy** (`unique_key` and `watermark`) and `on_mismatch: skip` are covered by `CopyConformanceTests` only; `stores` mentions them.
- **`connection inspect` and mapped models**: "schema names" lists the schemas of models the tool builds only, so a project whose models are all mapped on a connection (a shop system) says `no model declares this connection`, and a missing source schema is not counted as a problem. It should list the schemas of mapped models too ("missing: the tool does not create it", counted).
- **Wording found on the way, not changed** (`inspect` advised `init --connection <the shop system>` when the tracking tables are on another connection: fixed, it now names the tracking connection): `status` after a deploy says `Structure  pending: a deploy would run 3 step(s)` for a project with a copy (the steps are the copy's transfers and load, which every deploy repeats: data, not structure); `connection deploy` asks "what about the rows already loaded?" for a column added to a full-replace table (every load rebuilds it); `compile` and `status` print the same DDB-302 note several times when one model has the construct in several places (62 notes for `retail`).
- **Fabric** has not been run for any template or sample (as before).
- **DuckDB's `ENUM`**: a cast to an enum is now refused (DDB-324). Mapping an enum to a `VARCHAR` plus a check is possible but is a design question (what the engine does with a value outside the list); decide before building it.
