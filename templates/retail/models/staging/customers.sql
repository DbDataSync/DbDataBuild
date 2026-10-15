-- Staging splits the one text column a mapped model gave us into the two the rest of the project wants.
-- The first name is everything before the first space, the last name everything after it ("van Dijk" stays whole).
SELECT customer_id,
       substr(full_name, 1, strpos(full_name, ' ') - 1) AS first_name,
       substr(full_name, strpos(full_name, ' ') + 1) AS last_name,
       email,
       substr(email, strpos(email, '@') + 1) AS email_domain,
       country_code,
       signup_date,
       marketing_opt_in
FROM shop.customers
