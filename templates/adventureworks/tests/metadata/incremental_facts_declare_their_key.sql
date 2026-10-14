-- description: A model loaded by unique key says which columns are the key, and they are its grain
-- tags: design
SELECT model FROM metadata_models WHERE kind = 'incremental_by_unique_key' AND len(grain) = 0
