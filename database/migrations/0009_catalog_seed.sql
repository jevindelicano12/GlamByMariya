-- Starting catalog for a new database: categories and the three services.
-- 0002 already adds the add-ons, service areas, payment methods, business settings
-- and the 'Signature' category. No artist, no accounts, no bookings: the owner and the
-- single makeup artist come from --database-create-superadmin. The owner edits the
-- catalog later from the Catalog page.
INSERT INTO public.catalog_categories(id, name) VALUES
('30000000-0000-0000-0000-000000000001', 'The everyday edit'),
('30000000-0000-0000-0000-000000000002', 'The occasion edit'),
('30000000-0000-0000-0000-000000000003', 'The bridal edit')
ON CONFLICT (id) DO NOTHING;

INSERT INTO public.services(app_id, category_id, name, description, price, duration_minutes, group_price, group_minimum_people, visual_word)
SELECT v.app_id, v.category_id, v.name, v.description, v.price, v.minutes, v.group_price, 4, v.visual_word
FROM (VALUES
  ('40000000-0000-0000-0000-000000000001'::uuid, '30000000-0000-0000-0000-000000000001'::uuid, 'Soft Glam',
   'Fresh, luminous skin and softly defined eyes. Your everyday beauty, elevated.', 600.00, 60, 525.00, 'soft.'),
  ('40000000-0000-0000-0000-000000000002'::uuid, '30000000-0000-0000-0000-000000000002'::uuid, 'Glamour Makeup',
   'Sculpted definition, statement eyes and a flawless finish that lasts all night.', 750.00, 90, NULL::numeric, 'bold.'),
  ('40000000-0000-0000-0000-000000000003'::uuid, '30000000-0000-0000-0000-000000000003'::uuid, 'Bridal Makeup',
   'An exquisite, camera-ready look made for your most unforgettable day. Priced for one bride.', 1500.00, 120, NULL::numeric, 'forever.')
) AS v(app_id, category_id, name, description, price, minutes, group_price, visual_word)
WHERE NOT EXISTS (SELECT 1 FROM public.services s WHERE s.app_id = v.app_id);
