-- The table macro given the live table and NULL for the day column: every row is as of the macro's `live_as_of`.
SELECT * FROM status_totals('ledger.orders', NULL)
