-- The same macro given the history table and its day column, both by name from dbdatabuild.yml (parameters of type NAME).
-- Compare rendered/sqlserver/marts.status_totals_history/load.default.sql with the live one: no CASE, no UNION, only this table.
SELECT * FROM status_totals(${project.history_table}, ${project.snapshot_column})
