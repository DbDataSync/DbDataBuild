-- description: Every model says what one of its rows is
-- tags: design
SELECT model FROM metadata_models WHERE kind NOT IN ('mapped', 'native') AND len(grain) = 0
