-- description: Staging models are views, and they read only mapped models
-- tags: layers
SELECT m.model, m.kind, u.upstream
FROM metadata_models m
JOIN metadata_upstream u ON u.model = m.model
WHERE m.model LIKE 'staging.%' AND (m.kind <> 'view' OR u.upstream_kind IS DISTINCT FROM 'mapped')
