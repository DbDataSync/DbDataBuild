-- description: Marts read staging and other marts, never a mapped model directly
-- tags: layers
SELECT model, upstream FROM metadata_upstream WHERE model LIKE 'marts.%' AND upstream_kind = 'mapped'
