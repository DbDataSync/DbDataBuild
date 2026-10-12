-- The orders one shop system holds. Order numbers start at 1 in every system, so the same number is a different order in each:
-- that is why the copy into the warehouse carries a store_id and why the store_id is part of its key.
SELECT i AS order_id,
       DATE '2024-03-01' + CAST(pick(i, 1, 14) AS INTEGER) AS ordered_on,
       CASE WHEN rnd(i, 2) < 0.6 THEN 'web' WHEN rnd(i, 2) < 0.85 THEN 'store' ELSE 'phone' END AS channel,
       CAST(round(5 + rnd(i, 3) * 195, 2) AS DECIMAL(12, 2)) AS amount
FROM range(1, 1 + getvariable('scale')) AS t(i)
