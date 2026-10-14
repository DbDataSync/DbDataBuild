# Model kind: one word, one structure (decided 2026-10-14; not built)

Status: decided by the owner; queued to run after `project update` is merged. Touches the model loader, so it must not run beside that work.

## The problem

The docs and files say both "kind" and "type" for the same thing (`kind: {type: copy, ...}`, "kind.type"). "Type" also means a column's SQL type (`columns[].type`, `slice.type`), so the word collides.

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
* Every template, test fixture, golden, the agent-kit skill, `DESIGN.md` (6.1, 6.5.x), `docs/models.md` (drop "kind.type", say "kind"), `docs/concepts.md`, help text, the metadata documents and `metadata_models` views (check what names the kind), the TUI and the web page if they show it.
* Docs say "model kind" everywhere; a grep for "kind.type" and "model type" finds nothing in `docs/` and `DESIGN.md` afterwards (history entries in `docs/progress/` keep their words).
* Tests: the loader accepts the new forms and the shorthand, refuses two kinds, no kind, a foreign setting and an empty mapping; the old form still loads with the warning and produces byte-identical rendered output; fuzz coverage for the new shape; templates build on both engines as before.
