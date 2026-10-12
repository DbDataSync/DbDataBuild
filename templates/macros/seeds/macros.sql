-- Helpers every seed shares. (These are DuckDB helpers for generating sample data; they are not the project's macros, which are in macros/.)
-- A seed is a SELECT that returns the rows of one source; `seed` and `scale` are variables:
-- `dbdatabuild project seed --seed 7 --scale 200` sets them, and the same two numbers always give the same rows.
SET VARIABLE scale = coalesce(getvariable('scale'), 80);       -- how many orders

-- A number in [0, 1) that depends only on the row, a salt (to make different columns independent) and the seed.
CREATE OR REPLACE MACRO rnd(i, salt) AS (hash(i, salt, getvariable('seed')) % 1000000) / 1000000.0;

-- A whole number in [0, n) with the same property.
CREATE OR REPLACE MACRO pick(i, salt, n) AS CAST(floor(rnd(i, salt) * n) AS INTEGER);

-- The status of order i this many months after it was placed: the history and the live table tell the same story.
CREATE OR REPLACE MACRO status_after(i, months) AS
    CASE WHEN months = 0 THEN 'placed'
         WHEN months = 1 THEN 'shipped'
         WHEN rnd(i, 9) < 0.15 THEN 'returned'
         ELSE 'completed' END;
