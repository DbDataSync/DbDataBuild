-- Scalar macros: a name for an expression. DuckDB replaces the call by the expression while it binds the query, so what the
-- engine receives (see rendered/<connection>/marts.orders/load.default.sql) is the expression, never the macro.

-- Cents to dollars, as the `money` type of types.sql. An integer times 0.01 stays exact.
CREATE MACRO dollars(cents) AS CAST(cents * 0.01 AS money);

-- A band for an amount in dollars. The limits live here once; every model that bands an amount agrees.
CREATE MACRO size_band(amount) AS
    CASE WHEN amount < 25 THEN 'small' WHEN amount < 100 THEN 'medium' ELSE 'large' END;

-- Which statuses count as what.
CREATE MACRO status_group(status) AS
    CASE WHEN status IN ('shipped', 'completed') THEN 'fulfilled'
         WHEN status = 'returned' THEN 'returned'
         ELSE 'open' END;
