// Synthetic passwords are generated per test process and never printed or saved.
import { randomBytes } from 'node:crypto';

const password = () => 'Test9' + randomBytes(24).toString('hex');
export const customer = { email: 'presentation-customer@example.test', password: password() };
export const admin = { email: 'presentation-admin@example.test', password: password() };
export const owner = { email: 'presentation-owner@example.test', password: password() };
process.env.DemoAccounts__Presentation = 'true';
process.env.GLAM_DEMO_CUSTOMER_PASSWORD = customer.password;
process.env.GLAM_DEMO_ADMIN_PASSWORD = admin.password;
process.env.GLAM_DEMO_OWNER_PASSWORD = owner.password;
