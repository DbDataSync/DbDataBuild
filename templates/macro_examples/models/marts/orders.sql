-- Scalar macros in a query: dollars(), size_band() and status_group() are replaced by their expressions before anything
-- reaches an engine. Open rendered/sqlserver/marts.orders/load.default.sql to see the expansion.
SELECT order_id,
       customer_id,
       status,
       status_group(status) AS status_group,
       dollars(amount_cents) AS amount,
       size_band(dollars(amount_cents)) AS size_band,
       ordered_on
FROM ledger.orders
