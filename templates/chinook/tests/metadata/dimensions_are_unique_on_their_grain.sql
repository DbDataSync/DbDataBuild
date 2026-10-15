-- description: A dimension has a declared grain, and its first column is part of it (the key the facts join on)
-- tags: design
SELECT m.model
FROM metadata_models m
WHERE m.model LIKE 'marts.dim\_%' ESCAPE '\'
  AND NOT EXISTS (SELECT 1 FROM metadata_columns c WHERE c.model = m.model AND c.model_kind NOT IN ('mapped', 'native') AND list_contains(m.grain, c.column_name))
