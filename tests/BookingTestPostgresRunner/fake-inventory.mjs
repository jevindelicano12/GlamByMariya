// Offline model only. Not a PostgreSQL inspector or authorization decision.
const expected = Object.freeze({
  roles: ['booking_test_customer_a', 'booking_test_customer_b', 'booking_test_runtime'],
  objects: ['booking_addons', 'bookings'],
  grants: []
});

export function validateFakeInventory(facts) {
  if (!facts || typeof facts !== 'object' || Array.isArray(facts) ||
      Object.keys(facts).sort().join(',') !== 'grants,objects,roles') return false;
  return Object.entries(expected).every(([key, values]) => {
    const actual = facts[key];
    return Array.isArray(actual) && actual.every(x => typeof x === 'string') &&
      new Set(actual).size === actual.length && actual.length === values.length &&
      values.every(x => actual.includes(x));
  });
}
