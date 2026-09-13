import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { createHash, randomBytes } from 'node:crypto';
import { gunzipSync } from 'node:zlib';
import worker, { MarketCollector } from './worker.mjs';
import { MarketStore } from './market-store.mjs';
import { MarketSnapshots } from './market-snapshot.mjs';
import { validateMarketPage } from './market-core.mjs';

const NOW = Date.parse('2026-09-13T10:00:00Z');
function sqliteStorage() {
  const db = new DatabaseSync(':memory:');
  return {
    db, alarm: null,
    sql: { exec(sql, ...args) {
      if (sql.includes('CREATE TABLE')) { db.exec(sql); return []; }
      return db.prepare(sql).all(...args);
    } },
    transactionSync(fn) {
      db.exec('BEGIN');
      try { const result = fn(); db.exec('COMMIT'); return result; }
      catch (error) { db.exec('ROLLBACK'); throw error; }
    },
    async setAlarm(time) { assert.ok(Number.isFinite(time), 'alarm time must be finite'); this.alarm = time; },
    async deleteAlarm() { this.alarm = null; },
  };
}
async function fixture(fn) {
  const state = sqliteStorage(), store = new MarketStore(state), snapshots = new MarketSnapshots(store);
  try { return await fn({ state, store, snapshots }); } finally { state.db.close(); }
}
function page(kind, extra = {}, cursor = null, now = NOW) {
  const item = { auction_buy_id: `sale-${now}`, item_name: '거미줄', auction_item_category: '천옷/방직',
    item_count: 10, auction_price_per_unit: 200, date_auction_buy: new Date(now - 60_000).toISOString(), item_option: [], ...extra };
  return validateMarketPage({ [kind === 'history' ? 'auction_history' : 'auction_item']: [item], next_cursor: cursor }, kind, now);
}
function complete(store, kind, now = NOW, extra = {}) {
  store.commitPage(store.start(kind, now), page(kind, extra, null, now), now);
}
async function publish(store, snapshots, now = NOW) {
  return snapshots.publish(store.buildSnapshot(now), store.publicationSignature(), now);
}
function collectorEnvironment(collector) {
  const counts = { coordinator: 0, market: 0 };
  return { counts, env: { MARKET_ENABLED: 'true', SHARED_MARKET_QUOTES_ENABLED: 'true',
    MARKET_COLLECTOR: { idFromName: name => name, get: () => ({ fetch: request => { counts.market++; return collector.fetch(request); } }) },
    AUCTION_COORDINATOR: { idFromName: name => name, get: () => ({ fetch: () => { counts.coordinator++; throw Error('public reads must never fetch Nexon'); } }) },
  } };
}

test('snapshot publication rolls back all chunks and pointers when a later chunk cannot be stored', async () => fixture(async ({ state, store, snapshots }) => {
  complete(store, 'list'); await publish(store, snapshots);
  const previous = snapshots.manifest(), previousBytes = snapshots.bytes(previous.version);
  const beforeChunks = store.one('SELECT COUNT(*) AS count FROM market_snapshot_chunks').count;
  const data = store.buildSnapshot(NOW + 1000);
  // More than one compressed chunk ensures the failure occurs after the first insert.
  data.test_padding = randomBytes(400_000).toString('base64');
  state.db.exec("CREATE TRIGGER fail_snapshot_chunk BEFORE INSERT ON market_snapshot_chunks WHEN NEW.part=1 BEGIN SELECT RAISE(ABORT,'snapshot disk failure'); END");
  await assert.rejects(snapshots.publish(data, 'next-complete-version', NOW + 1000), /snapshot disk failure/);
  assert.deepEqual(snapshots.manifest(), previous);
  assert.deepEqual(snapshots.bytes(previous.version), previousBytes);
  assert.equal(store.meta('snapshot_signature'), store.publicationSignature());
  assert.equal(store.one('SELECT COUNT(*) AS count FROM market_snapshot_versions').count, 1);
  assert.equal(store.one('SELECT COUNT(*) AS count FROM market_snapshot_chunks').count, beforeChunks);
  const restarted = new MarketSnapshots(new MarketStore(state));
  assert.equal((await restarted.data()).generated_at, previous.generated_at);
}));

test('published gzip bytes match manifest sizes and SHA-256 and unchanged manifest returns 304', async () => fixture(async ({ store, snapshots }) => {
  complete(store, 'history'); complete(store, 'list');
  const expected = store.buildSnapshot(NOW);
  await snapshots.publish(expected, store.publicationSignature(), NOW);
  const manifest = snapshots.manifest(), response = snapshots.artifactResponse(manifest.version);
  const compressed = Buffer.from(await response.arrayBuffer()), raw = gunzipSync(compressed);
  assert.equal(response.headers.get('Content-Type'), 'application/gzip');
  assert.equal(response.headers.has('Content-Encoding'), false, 'gzip is an opaque download, not implicit transport decoding');
  assert.equal(compressed.length, manifest.compressed_bytes);
  assert.equal(raw.length, manifest.uncompressed_bytes);
  assert.equal(createHash('sha256').update(compressed).digest('hex'), manifest.sha256);
  assert.equal(manifest.sha256, manifest.version);
  assert.deepEqual(JSON.parse(raw.toString('utf8')), expected);
  const conditional = snapshots.manifestResponse(new Request('https://test/v1/market/manifest', { headers: { 'If-None-Match': `"${manifest.version}"` } }));
  assert.equal(conditional.status, 304); assert.equal(await conditional.text(), '');
  assert.equal(conditional.headers.get('ETag'), `"${manifest.version}"`);
  assert.equal(snapshots.manifestResponse(new Request('https://test/v1/market/manifest', { headers: { 'If-None-Match': '"other"' } })).status, 200);
}));

test('same collection signature avoids storing or replacing the published download', async () => fixture(async ({ store, snapshots }) => {
  complete(store, 'list'); await publish(store, snapshots);
  const before = snapshots.manifest(), beforeBytes = snapshots.bytes(before.version);
  const changedClock = store.buildSnapshot(NOW + 5000);
  assert.equal(await snapshots.publish(changedClock, store.publicationSignature(), NOW + 5000), false);
  assert.deepEqual(snapshots.manifest(), before);
  assert.deepEqual(snapshots.bytes(before.version), beforeBytes);
  assert.equal(store.one('SELECT COUNT(*) AS count FROM market_snapshot_versions').count, 1);
}));

test('previous manifest version remains downloadable after a new publication and restart', async () => fixture(async ({ state, store, snapshots }) => {
  complete(store, 'list'); await publish(store, snapshots);
  const first = snapshots.manifest();
  complete(store, 'list', NOW + 3600_000, { auction_price_per_unit: 50 });
  await publish(store, snapshots, NOW + 3600_000);
  const second = snapshots.manifest(), restarted = new MarketSnapshots(new MarketStore(state));
  assert.notEqual(first.version, second.version);
  const old = JSON.parse(gunzipSync(Buffer.from(await restarted.artifactResponse(first.version).arrayBuffer())).toString('utf8'));
  assert.equal(old.quotes[0].unit_price, 200);
  assert.equal((await restarted.data()).quotes[0].unit_price, 50);
  assert.equal(restarted.artifactResponse('0'.repeat(64)).status, 410);
}));

test('an incomplete listing run cannot replace quotes even when a new history version is published', async () => fixture(async ({ store, snapshots }) => {
  complete(store, 'list'); complete(store, 'history'); await publish(store, snapshots);
  const nextTime = NOW + 20 * 60_000;
  store.commitPage(store.start('list', nextTime), page('list', { item_count: 900, auction_price_per_unit: 1 }, 'more', nextTime), nextTime);
  complete(store, 'history', nextTime, { auction_buy_id: 'new-sale' });
  await publish(store, snapshots, nextTime);
  const body = await (await snapshots.quoteResponse('거미줄', null)).json();
  assert.equal(body.auction_item[0].auction_price_per_unit, 200);
  assert.equal(body.auction_item[0].item_count, 10);
  assert.equal(body.fetched_at, new Date(NOW).toISOString());
  assert.equal((await snapshots.data()).status.listings.latest_run.state, 'running');
  const version = snapshots.manifest().version;
  store.fail(store.active('list'), 'MARKET_INCOMPLETE_SCAN', nextTime + 1000);
  assert.equal(await publish(store, snapshots, nextTime + 1000), false);
  assert.equal(snapshots.manifest().version, version);
}));

test('released-client quote routes use only the shared snapshot including absent data and pagination', async () => {
  const state = sqliteStorage();
  try {
    const collector = new MarketCollector({ storage: state }, { MARKET_ENABLED: 'true' });
    const { env, counts } = collectorEnvironment(collector);
    const request = (name = '거미줄', cursor = '') => new Request('https://test/auction?' + new URLSearchParams({ item_name: name, ...(cursor ? { cursor } : {}) }));
    assert.equal((await worker.fetch(request(), env)).status, 503);
    assert.equal(counts.coordinator, 0);
    complete(collector.store, 'list'); await publish(collector.store, collector.snapshots);
    const quote = await (await worker.fetch(request(), env)).json();
    assert.equal(quote.source, 'shared_snapshot'); assert.equal(quote.auction_item[0].auction_price_per_unit, 200);
    assert.deepEqual((await (await worker.fetch(request('거미줄', 'old-client-cursor'), env)).json()).auction_item, []);
    assert.deepEqual((await (await worker.fetch(request('양털'), env)).json()).auction_item, []);
    assert.equal((await worker.fetch(request(), { ...env, MARKET_COLLECTOR: undefined })).status, 503);
    assert.equal((await worker.fetch(request(), { ...env, MARKET_ENABLED: 'false' })).status, 503);
    assert.equal(counts.coordinator, 0);
  } finally { state.db.close(); }
});

test('shared quotes retain released-client aliases without another upstream request', async () => {
  const state = sqliteStorage();
  try {
    const collector = new MarketCollector({ storage: state }, { MARKET_ENABLED: 'true' });
    const { env, counts } = collectorEnvironment(collector);
    complete(collector.store, 'list', NOW, { item_name: '미스릴광석', auction_item_category: '제련/블랙스미스' });
    await publish(collector.store, collector.snapshots);
    const result = await worker.fetch(new Request('https://test/v1/auction/list?' + new URLSearchParams({ item_name: '미스릴 광석' })), env);
    assert.equal(result.status, 200);
    const quote = (await result.json()).auction_item[0];
    assert.ok(quote, 'canonical snapshot item must resolve the legacy alias');
    assert.equal(quote.item_name, '미스릴 광석'); assert.equal(quote.auction_price_per_unit, 200);
    assert.equal(counts.coordinator, 0);
  } finally { state.db.close(); }
});

test('public common manifest and gzip cache hits skip the collector and support conditional reads', async () => {
  const state = sqliteStorage(), previousCaches = Object.getOwnPropertyDescriptor(globalThis, 'caches');
  const cache = new Map();
  Object.defineProperty(globalThis, 'caches', { configurable: true, value: { default: {
    async match(request) { return cache.get(request.url)?.clone(); },
    async put(request, response) { cache.set(request.url, response.clone()); },
  } } });
  try {
    const collector = new MarketCollector({ storage: state }, { MARKET_ENABLED: 'true' });
    complete(collector.store, 'list'); await publish(collector.store, collector.snapshots);
    const { env, counts } = collectorEnvironment(collector);
    const manifestUrl = 'https://test/v1/market/manifest';
    const manifest = await (await worker.fetch(new Request(manifestUrl), env)).json();
    assert.equal(counts.market, 1);
    const hit = await worker.fetch(new Request(manifestUrl, { headers: { 'If-None-Match': `"${manifest.version}"` } }), env);
    assert.equal(hit.status, 304); assert.equal(await hit.text(), ''); assert.equal(counts.market, 1);
    const artifactUrl = 'https://test' + manifest.snapshot_url;
    const first = await worker.fetch(new Request(artifactUrl), env);
    const firstBytes = Buffer.from(await first.arrayBuffer());
    const second = await worker.fetch(new Request(artifactUrl), env);
    assert.deepEqual(Buffer.from(await second.arrayBuffer()), firstBytes);
    assert.equal(counts.market, 2); assert.equal(counts.coordinator, 0);
    const artifact304 = await worker.fetch(new Request(artifactUrl, { headers: { 'If-None-Match': `"${manifest.version}"` } }), env);
    assert.equal(artifact304.status, 304); assert.equal(counts.market, 2);
  } finally {
    if (previousCaches) Object.defineProperty(globalThis, 'caches', previousCaches); else delete globalThis.caches;
    state.db.close();
  }
});
