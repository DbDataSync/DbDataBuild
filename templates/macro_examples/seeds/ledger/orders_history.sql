-- Every order at each month end from the month it was placed, with the status it had then.
SELECT m.snap_date,
       o.order_id,
       o.customer_id,
       status_after(o.order_id, date_diff('month', date_trunc('month', o.ordered_on), date_trunc('month', m.snap_date))) AS status,
       o.amount_cents,
       o.ordered_on
FROM (SELECT * FROM ledger.orders) o
JOIN (VALUES (DATE '2024-01-31'), (DATE '2024-02-29'), (DATE '2024-03-31'), (DATE '2024-04-30'), (DATE '2024-05-31'), (DATE '2024-06-30')) AS m(snap_date)
  ON m.snap_date >= o.ordered_on
