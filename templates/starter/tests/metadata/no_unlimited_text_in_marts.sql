-- description: Mart columns declare a length (a rule you can drop or tighten: it is only a sample)
-- severity: warning
-- tags: design
SELECT model, column_name FROM metadata_columns WHERE model_kind NOT IN ('mapped', 'native') AND model LIKE 'marts.%' AND logical_type = 'VARCHAR'
