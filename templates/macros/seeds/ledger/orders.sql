-- Orders placed in the first half of 2024, in the state they are in on 2024-06-30.
SELECT i AS order_id,
       1 + pick(i, 2, 25) AS customer_id,
       status_after(i, date_diff('month', date_trunc('month', DATE '2024-01-01' + pick(i, 1, 181)), DATE '2024-06-01')) AS status,
       CAST(500 + pick(i, 3, 20000) AS BIGINT) AS amount_cents,
       DATE '2024-01-01' + pick(i, 1, 181) AS ordered_on
FROM range(1, 1 + getvariable('scale')) AS t(i)
