-- Fan-out: one query, built on both the SQL Server warehouse and the PostgreSQL reporting database. Each reads its own warehouse.orders.
SELECT store_id,
       ordered_on,
       channel,
       count(*) AS orders,
       sum(amount) AS amount
FROM warehouse.orders
GROUP BY store_id, ordered_on, channel
