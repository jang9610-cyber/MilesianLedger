import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { MarketCollector } from './market-worker.mjs';

const NOW = Date.parse('2026-09-13T10:00:00Z');
async function fixture(fn, extraEnv = {}) {
  const db = new DatabaseSync(':memory:');
  const storage = {
    sql: { exec(sql, ...args) {
      if (sql.includes('CREATE TABLE')) { db.exec(sql); return Object.assign([], { rowsRead: 0, rowsWritten: 0 }); }
      const rows = db.prepare(sql).all(...args);
      const writes = /^(INSERT|UPDATE|DELETE)\b/.test(sql.trim()) ? db.prepare('SELECT changes() AS n').get().n : 0;
      return Object.assign(rows, { rowsRead: rows.length, rowsWritten: writes });
    } },
    transactionSync(fn) { db.exec('BEGIN'); try { const r = fn(); db.exec('COMMIT'); return r; } catch (error) { db.exec('ROLLBACK'); throw error; } },
    alarm: null, async setAlarm(time) { this.alarm = time; }, async deleteAlarm() { this.alarm = null; },
  };
  const oldNow = Date.now, clock = { time: NOW }; Date.now = () => clock.time;
  let handler = () => Response.json({ rows: [], next_cursor: 'unused' });
  const calls = [];
  const env = { MARKET_ENABLED: 'true', ...extraEnv, AUCTION_COORDINATOR: {
    idFromName: name => name, get: () => ({ fetch: async request => {
      const params = new URL(request.url).searchParams;
      const call = { kind: params.get('kind'), cursor: params.get('cursor') };
      calls.push(call); return handler(call, calls.length);
    } }),
  } };
  const create = () => new MarketCollector({ storage }, env);
  const c = create(); c.store.start('history', NOW); c.store.start('list', NOW);
  try { await fn({ c, storage, clock, calls, env, create, setHandler(fn) { handler = fn; } }); }
  finally { Date.now = oldNow; db.close(); }
}
const next = call => Response.json({ rows: [], next_cursor: `${call.kind}-${Number(call.cursor?.split('-').at(-1) || 0) + 1}` });

test('bounded alarm defaults to40 calls, enforces hard40 ceiling and alternates streams', async () => {
  for (const settings of [{}, { MARKET_PAGES_PER_ALARM: '999' }, { MARKET_PAGES_PER_ALARM: 'invalid' }]) {
    await fixture(async ({ c, calls, setHandler }) => {
      setHandler(next); await c.alarm();
      assert.equal(calls.length, 40);
      assert.deepEqual(calls.map(r => r.kind), Array.from({ length: 40 }, (_, i) => i % 2 ? 'list' : 'history'));
      assert.equal(c.store.active('history').pages, 20); assert.equal(c.store.active('list').pages, 20);
      assert.equal(c.store.diagnostics(NOW).sql_budget.reservations, 40, 'every upstream page reserves separately');
    }, settings);
  }
});

test('batch cursor checkpoints survive recreation after configurable3-call cap', async () => fixture(async ({ c, create, calls, setHandler }) => {
  setHandler(next); await c.alarm();
  assert.equal(calls.length, 3);
  assert.equal(c.store.active('history').cursor, 'history-2'); assert.equal(c.store.active('list').cursor, 'list-1');
  const restarted = create(); await restarted.alarm();
  assert.equal(calls[3].cursor, 'history-2'); assert.equal(calls[4].cursor, 'list-1');
  assert.equal(restarted.store.active('history').pages, 4); assert.equal(restarted.store.active('list').pages, 2);
}, { MARKET_PAGES_PER_ALARM: '3' }));

test('both streams can complete within one batch and publish exactly once', async () => fixture(async ({ c, calls, storage, setHandler }) => {
  const seen = { history: 0, list: 0 };
  setHandler(call => {
    const count = ++seen[call.kind], done = count === (call.kind === 'history' ? 2 : 3);
    return Response.json({ rows: [], next_cursor: done ? null : `${call.kind}-${count}` });
  });
  let publishes = 0; const original = c.snapshots.publish.bind(c.snapshots);
  c.snapshots.publish = (...args) => { publishes++; return original(...args); };
  await c.alarm();
  assert.equal(calls.length, 5); assert.equal(publishes, 1);
  assert.equal(c.store.latest('history').state, 'complete'); assert.equal(c.store.latest('list').state, 'complete');
  assert.equal(storage.alarm, null);
  assert.equal(c.snapshots.manifest().status.history.published.pages, 2);
  assert.equal(c.snapshots.manifest().status.listings.published.pages, 3);
}));

test('429 puts one stream in backoff without hot-looping or blocking the other stream', async () => fixture(async ({ c, calls, storage, clock, setHandler }) => {
  let historyAttempts = 0, lists = 0;
  setHandler(call => {
    if (call.kind === 'history' && historyAttempts++ === 0) return Response.json({ error: { code: 'PROXY_UPSTREAM_LIMIT' } }, { status: 429, headers: { 'Retry-After': '60' } });
    if (call.kind === 'history') return Response.json({ rows: [], next_cursor: null });
    return Response.json({ rows: [], next_cursor: ++lists === 3 ? null : `list-${lists}` });
  });
  await c.alarm();
  assert.deepEqual(calls.map(c => c.kind), ['history', 'list', 'list', 'list']);
  assert.equal(c.store.active('history').next_attempt, NOW + 60_000);
  assert.equal(storage.alarm, NOW + 60_000);
  await c.alarm(); assert.equal(calls.length, 4, 'backoff alarm performs no upstream calls');
  clock.time += 61_000; await c.alarm();
  assert.equal(calls.length, 5); assert.equal(c.store.latest('history').state, 'complete');
}));

test('8second batch deadline stops starting pages and keeps the latest committed cursors', async () => fixture(async ({ c, calls, clock, storage, setHandler }) => {
  setHandler(call => { clock.time += 3100; return next(call); });
  await c.alarm();
  assert.equal(calls.length, 3, 'starts at0,3.1,6.2seconds; no start after8seconds');
  assert.equal(c.store.active('history').pages, 2); assert.equal(c.store.active('list').pages, 1);
  assert.equal(storage.alarm, clock.time + 1000);
}));

test('storage budget is checked before every page, including the middle of a batch', async () => fixture(async ({ c, calls, setHandler }) => {
  setHandler(next);
  const original = c.store.reserveCollectionPage.bind(c.store); let attempts = 0;
  c.store.reserveCollectionPage = (...args) => ++attempts <= 2 ? original(...args) : { allowed: false };
  await c.alarm();
  assert.equal(calls.length, 2);
  assert.equal(c.store.latest('history').error, 'MARKET_STORAGE_BUDGET');
  assert.equal(c.store.latest('list').error, 'MARKET_STORAGE_BUDGET');
}));
