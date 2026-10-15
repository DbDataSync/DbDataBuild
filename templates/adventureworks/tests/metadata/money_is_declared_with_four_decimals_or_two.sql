-- description: Money columns (names ending in _usd, _cost, _price, _amount, revenue, spend) carry a decimal type, never a float
-- severity: warning
-- tags: design
SELECT model, column_name, logical_type
FROM metadata_columns
WHERE model_kind NOT IN ('mapped', 'native') AND model LIKE 'marts.%'
  AND regexp_matches(column_name, '(_usd|_cost|_price|_amount|^revenue|^spend)$')
  AND logical_type NOT LIKE 'DECIMAL%'
