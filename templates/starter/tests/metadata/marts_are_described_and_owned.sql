-- description: Every mart says what it is (a title and a description) and who owns it (an owner property)
-- tags: documentation
SELECT m.model FROM metadata_models m
WHERE m.model LIKE 'marts.%' AND (m.title IS NULL OR m.description IS NULL
  OR NOT EXISTS (SELECT 1 FROM metadata_info i WHERE i.subject_kind = 'model' AND i.subject = m.model AND i.element = '' AND i.item = 'property' AND i.item_key = 'owner'))
