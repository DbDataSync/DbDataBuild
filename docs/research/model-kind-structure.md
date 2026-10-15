# Model kind: one word, one structure (decided 2026-10-14; built in entries 163 and 164)

Status: decided by the owner; the migration described below (old form read with a warning, rewritten by `project model update`) was built in entry 163 and **removed in entry 169** (the old form is DDB-248 now); built after `project update` was merged (entry 163: the structure, the old form read with a warning and rewritten by `project model update`; entry 164: the metadata exposure). The words of the old name for the key (a dotted path ending in "type") are kept out of the docs on purpose: a search of `docs/` and `DESIGN.md` for them finds only history.

## The problem

The docs and files say both "kind" and "type" for the same thing (`kind: {type: copy, ...}`, and its dotted path). "Type" also means a column's SQL type (`columns[].type`, `slice.type`), so the word collides.

## Decision

1. **The term is "model kind".** "Type" is not used for models in docs, help, diagnostics, JSON documents or code comments. The seven kinds: `view`, `full`, `incremental_by_unique_key`, `incremental_by_time_range`, `copy`, `mapped`, `native` (`docs/models.md`).
2. **The structure is `kind` keyed by the kind's name**, with that kind's settings beneath it:

   ```yaml
   kind:
     copy:
       from: pos.orders
       unique_key: [order_id]
       watermark: {column: ordered_on, lookback: 7 days}
   ```
   ```yaml
   kind: {copy: {from: pos.orders, unique_key: [order_id]}}      # flow form
   kind: {incremental_by_time_range: {time_column: ordered_on, lookback: 3 days}}
   kind: view                                                     # shorthand: a kind with no settings may be a plain value
   kind: {native: {access: select, query: "..."}}
   ```
   Exactly one key under `kind`. A setting that does not belong to the kind is an error by the shape of the schema (a one-of with closed keys per branch), not by a conditional check.
3. **No dotted-key sugar** (`kind.copy:` is an ordinary key named `kind.copy` in YAML; it does not nest). Not supported.

## Pros and cons recorded

Pros: the word "type" is gone; each kind's settings are scoped; exactly one kind is enforced by the shape; the smallest migration from today (`kind: {type: copy, from: x}` becomes `kind: {copy: {from: x}}`); consistent with name-keyed blocks elsewhere (`loads:`, `refresh:`, `connections:`); room for user-defined kinds later.
Cons: one more level of nesting; a scalar shorthand makes two accepted forms for settings-free kinds; changing a model's kind rewrites a block; settings shared by two kinds repeat in the schema; layering needs care (a folder's `_dbdatabuild.yml` that sets `kind` must be replaced whole by the model's, not merged key by key: check whether folders set `kind` today).

## Work to do

* Loader (`ModelDefinitionLoader`, `SourceDescriptorLoader` for mapped and native), `schemas/model.schema.json` (one-of keyed by kind), the shorthand, layering, diagnostics (new codes; never reuse) with fixtures.
* Migration: the loader accepts the old form (`kind: {type: ...}`) for one release with a warning (new DDB code) that names the rewrite; `project model update` (and `project update`) rewrites old files mechanically; `--check` reports them. No old form is written by the tool from now on.
* Every template, test fixture, golden, the agent-kit skill, `DESIGN.md` (6.1, 6.5.x), `docs/models.md` (say "model kind", no dotted path), `docs/concepts.md`, help text, the metadata documents and `metadata_models` views (check what names the kind), the TUI and the web page if they show it.
* Docs say "model kind" everywhere; a search for the dotted path of the old key, or for the words "model" and "type" together, finds nothing in `docs/` and `DESIGN.md` afterwards (history entries in `docs/progress/` keep their words).
* Tests: the loader accepts the new forms and the shorthand, refuses two kinds, no kind, a foreign setting and an empty mapping; the old form still loads with the warning and produces byte-identical rendered output; fuzz coverage for the new shape; templates build on both engines as before.

## Metadata exposure (added 2026-10-14, same run)

Found by reading the code before this run:

* The model metadata document (`dbdatabuild.model/1`, `schemas/metadata.schema.json`) has `kind` as an **object** (`{type: ..., unique_key: ...}`). With the new structure it would be a one-key object named by the kind, which a tool would have to unpick. The project document lists models with `kind` as a plain string already.
* The stored metadata is `metadata_current` (one JSON `document` per subject; its `kind` column is the **subject** kind: project, model or source) and `object_info` (flattened title, description, tags, properties). The flattened views (`metadata_models`, `metadata_upstream`, `metadata_ancestors`, ...) are built in an in-memory DuckDB from the documents and **are not stored in the database**.
* Dependencies: each model document carries its **direct** `upstream` (name and kind: model, source, native, macro, unknown); a source document carries `consumers` and a native's `reads`. The transitive tree (`metadata_ancestors`) is computed by a view from those, not stored. `project show graph` and selectors compute the graph from the project files.
* The metadata schema's model kind enum (under the old key) lists four values (it may lack `copy`, `mapped`, `native`): check for drift.

Decided (to build in the same run, after `project update`):

1. **`kind` is a plain string in every serialized model document and output** (`"kind": "copy"`), and the kind's settings go in a separate property (`"kind_settings": {...}`; empty object when none). A tool never has to find the first or only key. The metadata schema version moves to `dbdatabuild.model/2`. The same holds for the JSON documents of every command that shows a model (`project show`, `status`, `compile` output, MCP, web), and the TUI.
2. **A column for it in the stored tables**: `metadata_current` gains `model_kind` (null for project and source subjects; the existing `kind` column stays the subject kind and is not renamed). This is a tracking layout change (layout 8: `init --upgrade --apply`; `init` still alters no table). `publish` writes it. `metadata_models.kind` (the view) is the same string.
3. **Dependencies stored as rows**: a new tracking table `metadata_dependency` (`connection`, `subject`, `depends_on`, `depends_on_kind`, `depth`), written by `publish` beside `object_info`: depth 1 is a direct edge, greater is transitive (the closure, computed at publish from the committed project). A tool answers "what does this depend on" and "what depends on this" with one query and no JSON. The in-memory views read the same rows, so a rule means the same thing against published metadata. Native `reads` are edges; unknown/macro upstreams are recorded with their kind.
4. Docs/tests: the metadata schema and goldens, `metadata_*` view definitions, `publish`/`status` tests on SQL Server and PostgreSQL, the migration test for layout 7 to 8, `docs/concepts.md` and DESIGN.md 12 (tracking tables) and 9.x.
