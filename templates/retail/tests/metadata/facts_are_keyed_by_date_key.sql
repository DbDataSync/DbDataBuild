-- description: A fact table that is keyed by date joins to dim_date through a column called date_key (or shipped_date_key)
-- severity: warning
-- tags: design
SELECT model FROM metadata_models
WHERE model LIKE 'marts.fct\_%' ESCAPE '\'
  AND model NOT IN (SELECT model FROM metadata_columns WHERE model_kind NOT IN ('mapped', 'native') AND column_name IN ('date_key', 'shipped_date_key', 'budget_month'))
