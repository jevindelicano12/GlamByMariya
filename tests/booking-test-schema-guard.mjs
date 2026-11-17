// Offline test-harness contract only. Not an application startup check and not
// proof that the database, role, tables, constraints, or connection are safe.
const required = ['fixture_runs', 'artists', 'conflict_reservations'];

export function validateFixtureMetadata(metadata) {
  if (!metadata || metadata.host !== '127.0.0.1' || metadata.port !== 5432 ||
      metadata.database !== 'glam_auth_mvc_workflow_test' || metadata.user !== 'postgres' ||
      metadata.schema !== 'booking_test' || metadata.sentinel !== true)
    throw new Error('Unexpected isolated booking-test configuration.');
  if (!Array.isArray(metadata.tables) || metadata.tables.length !== required.length ||
      new Set(metadata.tables).size !== required.length || required.some(table => !metadata.tables.includes(table)))
    throw new Error('Booking-test fixture tables are missing, duplicated or unexpected.');
  return true;
}
