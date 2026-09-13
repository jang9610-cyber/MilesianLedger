import { json, marketError } from './market-core.mjs';

const RAW_LIMIT = 16 * 1024 * 1024, GZIP_LIMIT = 8 * 1024 * 1024, CHUNK = 256 * 1024;
const encoder = new TextEncoder();
const VERSION = /^[a-f0-9]{64}$/;
function base64(bytes) {
  let text = '';
  for (let offset = 0; offset < bytes.length; offset += 8192) text += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
  return btoa(text);
}
const fromBase64 = value => Uint8Array.from(atob(value), ch => ch.charCodeAt(0));

// The manifest pointer changes in the same transaction that stores every immutable chunk.
// Readers therefore see the previous complete publication or the next complete publication.
export class MarketSnapshots {
  constructor(store) {
    this.store = store; this.loaded = null;
    store.rows(`CREATE TABLE IF NOT EXISTS market_snapshot_versions (
      version TEXT PRIMARY KEY, created INTEGER NOT NULL, manifest TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS market_snapshot_chunks (
      version TEXT NOT NULL, part INTEGER NOT NULL, payload TEXT NOT NULL,
      PRIMARY KEY(version, part)
    ) WITHOUT ROWID;`);
  }
  manifest() {
    const version = this.store.meta('snapshot_current');
    if (!version) return null;
    const row = this.store.one('SELECT manifest FROM market_snapshot_versions WHERE version=?', version);
    return row ? JSON.parse(row.manifest) : null;
  }
  async publish(data, signature, now = Date.now()) {
    if (this.store.meta('snapshot_signature') === signature) return false;
    const raw = encoder.encode(JSON.stringify(data));
    if (raw.length > RAW_LIMIT) throw Error('MARKET_SNAPSHOT_TOO_LARGE');
    const compressed = new Uint8Array(await new Response(new Blob([raw]).stream().pipeThrough(new CompressionStream('gzip'))).arrayBuffer());
    if (compressed.length > GZIP_LIMIT) throw Error('MARKET_SNAPSHOT_TOO_LARGE');
    if ((this.store.storage.sql.databaseSize || 0) + compressed.length * 2 > 750 * 1024 * 1024) throw Error('MARKET_STORAGE_CAPACITY');
    const version = [...new Uint8Array(await crypto.subtle.digest('SHA-256', compressed))].map(x => x.toString(16).padStart(2, '0')).join('');
    const manifest = { schema_version: 1, version, generated_at: data.generated_at,
      snapshot_url: '/v1/market/snapshots/' + version + '.json.gz', compressed_bytes: compressed.length,
      uncompressed_bytes: raw.length, sha256: version, status: data.status };
    const parts = [];
    for (let offset = 0; offset < compressed.length; offset += CHUNK) parts.push(base64(compressed.subarray(offset, offset + CHUNK)));
    this.store.storage.transactionSync(() => {
      this.store.rows('INSERT OR IGNORE INTO market_snapshot_versions(version,created,manifest) VALUES(?,?,?)', version, now, JSON.stringify(manifest));
      parts.forEach((part, index) => this.store.rows('INSERT OR IGNORE INTO market_snapshot_chunks(version,part,payload) VALUES(?,?,?)', version, index, part));
      this.store.setMeta('snapshot_current', version);
      this.store.setMeta('snapshot_signature', signature);
      this.store.setMeta('snapshot_error', null);
    });
    this.loaded = { version, data };
    // Retain at least 24 hours for clients that obtained a previous manifest.
    for (const old of this.store.rows('SELECT version FROM market_snapshot_versions WHERE created<? AND version<>?', now - 86400_000, version)) {
      this.store.rows('DELETE FROM market_snapshot_chunks WHERE version=?', old.version);
      this.store.rows('DELETE FROM market_snapshot_versions WHERE version=?', old.version);
    }
    return true;
  }
  bytes(version) {
    if (!VERSION.test(version)) return null;
    const rows = this.store.rows('SELECT payload FROM market_snapshot_chunks WHERE version=? ORDER BY part', version);
    if (!rows.length) return null;
    const parts = rows.map(r => fromBase64(r.payload)), length = parts.reduce((n, p) => n + p.length, 0);
    if (length > GZIP_LIMIT) throw Error('MARKET_SNAPSHOT_TOO_LARGE');
    const bytes = new Uint8Array(length); let offset = 0;
    for (const part of parts) { bytes.set(part, offset); offset += part.length; }
    return bytes;
  }
  async data() {
    const manifest = this.manifest(); if (!manifest) return null;
    if (this.loaded?.version === manifest.version) return this.loaded.data;
    const bytes = this.bytes(manifest.version); if (!bytes) return null;
    const data = await new Response(new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'))).json();
    this.loaded = { version: manifest.version, data }; return data;
  }
  manifestResponse(request) {
    const body = this.manifest(); if (!body) return marketError(503, 'MARKET_SNAPSHOT_NOT_READY');
    const headers = { ETag: '"' + body.version + '"', 'Cache-Control': 'public, max-age=15, must-revalidate' };
    if (request.headers.get('If-None-Match') === headers.ETag) return new Response(null, { status: 304, headers });
    return json(body, 200, headers);
  }
  artifactResponse(version) {
    const bytes = this.bytes(version); if (!bytes) return marketError(410, 'MARKET_SNAPSHOT_EXPIRED');
    return new Response(bytes, { headers: { 'Content-Type': 'application/gzip',
      'Cache-Control': 'public, max-age=86400, immutable', ETag: '"' + version + '"',
      'Content-Length': String(bytes.length), 'X-Content-Type-Options': 'nosniff' } });
  }
  async quoteResponse(name, cursor, canonicalName = name) {
    const data = await this.data();
    if (!data?.listings_fetched_at) return marketError(503, 'PROXY_NOT_CONFIGURED');
    const quote = data.quotes.find(q => q.name === (canonicalName || name));
    // Compatibility for released clients: a single aggregated listing, no upstream pagination.
    const items = !cursor && quote?.unit_price != null ? [{ item_name: name,
      auction_price_per_unit: quote.unit_price, item_count: quote.quantity }] : [];
    return json({ auction_item: items, next_cursor: null, fetched_at: quote?.fetched_at || data.listings_fetched_at,
      listing_count: quote?.listing_count || 0, source: 'shared_snapshot' }, 200, { 'Cache-Control': 'public, max-age=30' });
  }
}

export function snapshotRankings(data, query) {
  if (!data) return null;
  const field = { quantity: 'sold_quantity', trades: 'trade_count', gold: 'traded_gold', supply: 'listed_quantity' }[query.sort];
  const matches = (query.window === '7d' ? data.items_7d : data.items_24h).filter(item =>
    (!query.category || item.category === query.category) && (!query.search || item.name.includes(query.search)));
  matches.sort((a, b) => (b[field] || 0) - (a[field] || 0) || a.name.localeCompare(b.name) || a.category.localeCompare(b.category));
  return { window: query.window, sort: query.sort, generated_at: data.generated_at, status: data.status,
    items: matches.slice(query.offset, query.offset + query.limit), total: matches.length,
    has_more: query.offset + query.limit < matches.length, offset: query.offset, limit: query.limit };
}
