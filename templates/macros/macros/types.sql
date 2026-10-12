-- A type is DuckDB's. An alias gives a type a name: the name is replaced by the type it stands for while the query is bound, so an engine
-- receives DECIMAL(12, 2), never `money`. (An ENUM type does not lower: an engine has no such type, and a cast to one is refused, DDB-324.)
CREATE TYPE money AS DECIMAL(12, 2);
