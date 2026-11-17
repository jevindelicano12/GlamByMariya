import { createHash } from 'node:crypto';

// Pure source-byte check; says nothing about objects installed in PostgreSQL.
export function validatePinnedHashes(entries) {
  if (!Array.isArray(entries) || entries.length === 0) return false;
  const names = new Set();
  for (const entry of entries) {
    if (!entry || typeof entry.name !== 'string' || !entry.name || names.has(entry.name) ||
        !/^[0-9A-F]{64}$/.test(entry.expected) || !Buffer.isBuffer(entry.bytes)) return false;
    names.add(entry.name);
    if (createHash('sha256').update(entry.bytes).digest('hex').toUpperCase() !== entry.expected) return false;
  }
  return true;
}
