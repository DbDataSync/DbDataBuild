-- Table macros (AS TABLE): a macro that returns a query. A table's name arrives as an argument, as text, and `query_table` turns it
-- into that table; a column's name arrives the same way and `COLUMNS(lambda c: c = col)` turns it into that column. Only constants
-- can be names, so DuckDB can see which table and column a call means and the project's graph shows them as dependencies.

-- The rows of a table as of a day. `col` names the column that holds the day when the table is a history that keeps one set of rows
-- per day; NULL says the table is live and every row is as of `live_as_of`. One macro serves both shapes (here a live orders table
-- and its history): DuckDB folds the CASE on `col IS NULL` while it expands the call, so the query an engine gets has no CASE and
-- no UNION, only what is true of the table it was given.
CREATE MACRO snapshot_at(tbl, col, live_as_of := DATE '2024-06-30') AS TABLE
    SELECT * EXCLUDE (__none),
           CASE WHEN col IS NULL THEN live_as_of ELSE COLUMNS(lambda c: c = coalesce(col, '__none')) END AS as_of_date
    FROM (SELECT *, NULL::DATE AS __none FROM query_table(tbl));

-- A macro may call another, scalar or table. Totals per day and status for any table of the order shape.
CREATE MACRO status_totals(tbl, col) AS TABLE
    SELECT as_of_date, status, count(*) AS orders, sum(dollars(amount_cents)) AS total
    FROM snapshot_at(tbl, col)
    GROUP BY as_of_date, status;
