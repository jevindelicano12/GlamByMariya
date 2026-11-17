-- Included by --database-init inside the schema's open transaction.
-- No demonstration users, bookings, payment references, refunds or image assets.
SET LOCAL search_path=makeup,public;
INSERT INTO roles(id,name) VALUES
('10000000-0000-0000-0000-000000000001','Admin'),
('10000000-0000-0000-0000-000000000002','Customer'),
('10000000-0000-0000-0000-000000000003','SuperAdmin');
INSERT INTO catalog_categories(id,name) VALUES
('30000000-0000-0000-0000-000000000001','The everyday edit'),
('30000000-0000-0000-0000-000000000002','The occasion edit'),
('30000000-0000-0000-0000-000000000003','The bridal edit'),
('30000000-0000-0000-0000-000000000004','Signature');
INSERT INTO makeup_services(id,category_id,name,description,price,minutes,group_price,visual_word) VALUES
('40000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000001','Soft Glam','Fresh, luminous skin and softly defined eyes. Your everyday beauty, elevated.',600,60,525,'soft.'),
('40000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000002','Glamour Makeup','Sculpted definition, statement eyes and a flawless finish that lasts all night.',750,90,NULL,'bold.'),
('40000000-0000-0000-0000-000000000003','30000000-0000-0000-0000-000000000003','Bridal Makeup','An exquisite, camera-ready look made for your most unforgettable day. Priced for one bride.',1500,120,NULL,'forever.');
INSERT INTO makeup_addons(id,category_id,name,description,price,minutes) VALUES
('50000000-0000-0000-0000-000000000001','30000000-0000-0000-0000-000000000004','Lash application','A little extra definition',250,15),
('50000000-0000-0000-0000-000000000002','30000000-0000-0000-0000-000000000004','Hair styling','Complete your look',800,45),
('50000000-0000-0000-0000-000000000003','30000000-0000-0000-0000-000000000004','Touch-up kit','Stay fresh throughout the day',350,0);
INSERT INTO business_settings(payment_instructions) VALUES
('Contact the artist for verified payment account details. Verify the recipient before sending your deposit.');
INSERT INTO service_areas(id,name) VALUES
('60000000-0000-0000-0000-000000000001','Quezon City'),
('60000000-0000-0000-0000-000000000002','Makati'),
('60000000-0000-0000-0000-000000000003','Manila'),
('60000000-0000-0000-0000-000000000004','Pasig'),
('60000000-0000-0000-0000-000000000005','Taguig'),
('60000000-0000-0000-0000-000000000006','Mandaluyong');
INSERT INTO payment_methods(id,name,accepts_deposits) VALUES
('70000000-0000-0000-0000-000000000001','GCash',true),
('70000000-0000-0000-0000-000000000002','Maya',true),
('70000000-0000-0000-0000-000000000003','Bank Transfer',true),
('70000000-0000-0000-0000-000000000004','Cash',false);
