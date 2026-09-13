// Each SQLite value stays below the free Durable Object's 2 MB row/value limit.
// Compact dictionaries also avoid repeating long item names for every trade.
export const MAX_CHUNK_BYTES = 900_000;
const encoder = new TextEncoder();
export const itemKey = (name, category) => JSON.stringify([name, category]);
export function addSafe(a, b) {
  if (a === null || b === null) return null;
  const sum = a + b;
  return Number.isSafeInteger(sum) ? sum : null;
}
function encode(rows, kind) {
  const names = [], indexes = new Map(), groups = new Map();
  const index = row => {
    const key = itemKey(row.name, row.category);
    if (!indexes.has(key)) { indexes.set(key, names.length); names.push([row.name, row.category]); }
    return indexes.get(key);
  };
  if (kind === 'history') {
    for (const r of rows) {
      const i = index(r), key = `${i}:${r.time}`;
      if (!groups.has(key)) groups.set(key, [i, r.time, 0, 0, 0, 1]);
      const g = groups.get(key);
      g[2] = addSafe(g[2], r.quantity); g[3]++;
      g[4] = addSafe(g[4], r.quantity * r.price); g[5] = Math.min(g[5], r.comparable);
    }
    return JSON.stringify({ v: 2, names, ids: rows.map(r => [r.id, r.time]), sales: [...groups.values()] });
  }
  for (const r of rows) {
    const i = index(r);
    if (!groups.has(i)) groups.set(i, [i, 0, 0, r.price, 1]);
    const g = groups.get(i);
    g[1]++; g[2] = addSafe(g[2], r.quantity); g[3] = Math.min(g[3], r.price); g[4] = Math.min(g[4], r.comparable);
  }
  return JSON.stringify({ v: 2, names, listings: [...groups.values()] });
}
export function* encodeChunks(rows, kind, maxBytes = MAX_CHUNK_BYTES) {
  if (!rows.length) return;
  const payload = encode(rows, kind);
  if (encoder.encode(payload).byteLength <= maxBytes) {
    yield { payload, records: rows.length,
      min_time: kind === 'history' ? Math.min(...rows.map(r => r.time)) : null,
      max_time: kind === 'history' ? Math.max(...rows.map(r => r.time)) : null };
    return;
  }
  if (rows.length === 1) throw Error('MARKET_CHUNK_TOO_LARGE');
  const middle = Math.ceil(rows.length / 2);
  yield* encodeChunks(rows.slice(0, middle), kind, maxBytes);
  yield* encodeChunks(rows.slice(middle), kind, maxBytes);
}
export function decodeChunk(payload) {
  const value = JSON.parse(payload);
  if (value.v !== 2 || !Array.isArray(value.names)) throw Error('MARKET_CHUNK_VERSION');
  return value;
}
