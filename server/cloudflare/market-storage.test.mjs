import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { MarketStore } from './market-store.mjs';
import { encodeChunks, decodeChunk, MAX_CHUNK_BYTES } from './market-chunks.mjs';
import { RETENTION } from './market-core.mjs';

const NOW = Date.parse('2026-09-13T12:00:00Z'), DAY = 86400_000;
const trade = (id, time = NOW - 60_000, extra = {}) => ({ id, time, name: '거미줄', category: '천옷/방직', quantity: 10, price: 20, comparable: 1, ...extra });
function fixture(fn) {
  const db = new DatabaseSync(':memory:');
  const stats = { physical_row_changes: 0, conservative_indexed_writes: 0, statements: 0 };
  const storage = {
    db, stats,
    sql: { exec(sql, ...args) {
      stats.statements++;
      if (sql.includes('CREATE TABLE')) { db.exec(sql); return Object.assign([], { rowsRead: 0, rowsWritten: 0 }); }
      const statement = db.prepare(sql), rows = statement.all(...args);
      const change = /^(INSERT|UPDATE|DELETE)\b/.test(sql.trim()) ? db.prepare('SELECT changes() AS n').get().n : 0;
      // SQLite's changes() excludes index entries. Use a conservative multiplier
      // for this offline write-amplification bound, not a claim of CF billing.
      const indexed = /market_v2_(runs|chunks)/.test(sql) ? 3 : 2;
      stats.physical_row_changes += change; stats.conservative_indexed_writes += change * indexed;
      return Object.assign(rows, { rowsRead: rows.length, rowsWritten: change * indexed });
    } },
    transactionSync(fn) {
      db.exec('BEGIN'); try { const value = fn(); db.exec('COMMIT'); return value; }
      catch (error) { db.exec('ROLLBACK'); throw error; }
    },
  };
  try { return fn(new MarketStore(storage), storage); } finally { db.close(); }
}
const commit = (store, kind, rows, now = NOW, cursor = null) => store.commitPage(store.active(kind) || store.start(kind, now), { rows, next_cursor: cursor }, now);
const count = (snapshot, window = '24h') => snapshot[`items_${window}`].reduce((n, item) => n + item.trade_count, 0);

test('dictionary blobs split by UTF8 bytes and preserve large names and IDs', () => {
  const rows = Array.from({ length: 500 }, (_, i) => trade('ID' + i + '한'.repeat(200), NOW, { name: '물'.repeat(190) + i, category: '분'.repeat(90) }));
  const chunks = [...encodeChunks(rows, 'history', 40_000)];
  assert(chunks.length > 1);
  assert(chunks.every(c => Buffer.byteLength(c.payload) <= 40_000 && Buffer.byteLength(c.payload) < MAX_CHUNK_BYTES));
  assert.equal(chunks.reduce((n, c) => n + decodeChunk(c.payload).ids.length, 0), 500);
  assert.equal(chunks.reduce((n, c) => n + c.records, 0), 500);
});

test('exact ID dedup persists across pages, overlapping scans, restart and redelivery', () => fixture((s, storage) => {
  const run = s.start('history', NOW);
  s.commitPage(run, { rows: [trade('a'), trade('a'), trade('b')], next_cursor: 'p2' }, NOW);
  s.commitPage(run, { rows: [trade('a'), trade('a'), trade('b')], next_cursor: 'p2' }, NOW);
  s = new MarketStore(storage);
  assert.equal(count(s.buildSnapshot(NOW)), 0, 'incomplete history is not public');
  commit(s, 'history', [trade('b'), trade('c')]);
  assert.equal(count(s.buildSnapshot(NOW)), 3);
  commit(s, 'history', [trade('a'), trade('c'), trade('d')], NOW + 20 * 60_000);
  assert.equal(count(new MarketStore(storage).buildSnapshot(NOW + 20 * 60_000)), 4);
  assert.equal(s.diagnostics(NOW).stored_trades, 4);
}));

test('failed partial history never leaks and cannot poison a later complete scan', () => fixture(s => {
  commit(s, 'history', [trade('old')]);
  const signature = s.publicationSignature();
  commit(s, 'history', [trade('old'), trade('new')], NOW + 1, 'more');
  assert.equal(count(s.buildSnapshot(NOW + 1)), 1);
  assert.equal(s.publicationSignature(), signature);
  s.fail(s.active('history'), 'FAILURE', NOW + 2);
  commit(s, 'history', [trade('new')], NOW + 3);
  assert.equal(count(s.buildSnapshot(NOW + 3)), 2);
}));

test('SQLite failure rolls back chunk, page, cursor and publication as one commit', () => fixture((s, storage) => {
  const run = s.start('history', NOW), signature = s.publicationSignature();
  storage.db.exec("CREATE TRIGGER reject_page BEFORE INSERT ON market_v2_pages BEGIN SELECT RAISE(ABORT,'disk fixture'); END");
  assert.throws(() => s.commitPage(run, { rows: [trade('a')], next_cursor: null }, NOW), /disk fixture/);
  assert.equal(s.one('SELECT COUNT(*) AS n FROM market_v2_chunks').n, 0);
  assert.equal(s.one('SELECT COUNT(*) AS n FROM market_v2_pages').n, 0);
  assert.equal(s.active('history').pages, 0);
  assert.equal(s.publicationSignature(), signature);
  storage.db.exec('DROP TRIGGER reject_page');
  s.commitPage(run, { rows: [trade('a')], next_cursor: null }, NOW);
  assert.equal(count(s.buildSnapshot(NOW)), 1);
}));

test('only complete listing scans replace supply, empty completed scan means zero', () => fixture(s => {
  commit(s, 'history', [trade('a')]);
  commit(s, 'list', [trade('a')]);
  commit(s, 'list', [trade('a', NOW, { quantity: 100 })], NOW + 1, 'p2');
  assert.equal(s.buildSnapshot(NOW).quotes[0].quantity, 10);
  s.fail(s.active('list'), 'FAILURE', NOW + 2);
  assert.equal(s.buildSnapshot(NOW).quotes[0].quantity, 10);
  commit(s, 'list', [], NOW + 3);
  const snapshot = s.buildSnapshot(NOW + 3);
  assert.equal(snapshot.quotes.length, 0);
  assert.equal(snapshot.items_24h[0].listed_quantity, 0);
  assert.equal(snapshot.items_24h[0].lowest_listing_price, null);
}));

test('quotes preserve all-option lowest price but rankings avoid unsafe comparison', () => fixture(s => {
  commit(s, 'list', [trade('a', NOW, { price: 30 }), trade('b', NOW, { price: 5, comparable: 0 }), trade('c', NOW, { category: '다른 분류', price: 15 })]);
  const snapshot = s.buildSnapshot(NOW);
  assert.equal(snapshot.quotes.length, 1);
  assert.equal(snapshot.quotes[0].unit_price, 5);
  assert.equal(snapshot.quotes[0].listing_count, 3);
  assert.equal(snapshot.quotes[0].quantity, 30);
  assert.equal(snapshot.items_24h.find(i => i.category === '천옷/방직').lowest_listing_price, null);
}));

test('rolling boundaries use exact trade times and do not count future records', () => fixture(s => {
  const rows = [trade('expired', NOW - 7 * DAY - 1), trade('week-edge', NOW - 7 * DAY),
    trade('old-day', NOW - DAY - 1), trade('day-edge', NOW - DAY), trade('recent', NOW), trade('future', NOW + 1)];
  commit(s, 'history', rows);
  const snapshot = s.buildSnapshot(NOW);
  assert.equal(count(snapshot, '24h'), 2);
  assert.equal(count(snapshot, '7d'), 4);
  assert.equal(count(s.buildSnapshot(NOW + 1), '24h'), 2);
}));

test('bounded cleanup removes expired and aborted chunks while keeping legacy pilot data', () => fixture(s => {
  s.exec("INSERT INTO market_sales VALUES ('legacy','old','old',1,1,?,1)", NOW);
  commit(s, 'history', [trade('old', NOW - RETENTION - 1)], NOW - RETENTION - 1);
  commit(s, 'history', [trade('new')]);
  commit(s, 'list', [trade('old')], NOW - RETENTION - 1);
  commit(s, 'list', [trade('new')], NOW);
  commit(s, 'history', [trade('aborted')], NOW + 1, 'more');
  s.fail(s.active('history'), 'FAILURE', NOW + 2);
  s.cleanup(NOW + 3);
  assert.equal(s.one('SELECT COUNT(*) AS n FROM market_v2_chunks').n, 2);
  assert.equal(s.one('SELECT COUNT(*) AS n FROM market_sales').n, 1);
  assert.equal(count(s.buildSnapshot(NOW + 3)), 1);
  assert.equal(s.buildSnapshot(NOW + 3).quotes[0].quantity, 10);
}));

test('expired listing data becomes unknown rather than a current zero or stale quote', () => fixture(s => {
  commit(s, 'list', [trade('a')], NOW - RETENTION - 1);
  commit(s, 'history', [trade('new')]);
  const snapshot = s.buildSnapshot(NOW);
  assert.equal(snapshot.quotes.length, 0);
  assert.equal(snapshot.items_24h[0].listed_quantity, null);
  assert.equal(snapshot.status.listings.stale, true);
}));

test('daily SQL reservation persists on restart, settles once and stops before cap', () => fixture((s, storage) => {
  const reservation = s.reserveCollectionPage(NOW, { writeLimit: 40, reserveWrites: 20 });
  assert.equal(reservation.allowed, true);
  s = new MarketStore(storage);
  assert.equal(s.reserveCollectionPage(NOW, { writeLimit: 40, reserveWrites: 20 }).allowed, true);
  const denied = s.reserveCollectionPage(NOW, { writeLimit: 40, reserveWrites: 20 });
  assert.equal(denied.allowed, false);
  assert(denied.retry_after_seconds > 0);
  const next = s.reserveCollectionPage(NOW + DAY, { writeLimit: 40, reserveWrites: 20 });
  assert.equal(next.allowed, true);
  s.settleCollectionPage(next, NOW + DAY);
  const before = s.diagnostics(NOW + DAY).sql_budget.actual_writes;
  s.settleCollectionPage(next, NOW + DAY);
  assert.equal(s.diagnostics(NOW + DAY).sql_budget.actual_writes, before);
  assert(s.accounting.counters_available);
}));

test('safe integer overflow is unknown and never silently becomes zero', () => fixture(s => {
  commit(s, 'history', [trade('a', NOW, { quantity: 1, price: Number.MAX_SAFE_INTEGER }), trade('b', NOW, { quantity: 1, price: Number.MAX_SAFE_INTEGER })]);
  const item = s.buildSnapshot(NOW).items_24h[0];
  assert.equal(item.traded_gold, null); assert.equal(item.average_sale_price, null);
}));

test('500-record pages replace per-trade SQL writes with bounded blob writes at representative load', () => fixture((s, storage) => {
  // 48 scans x 10 pages x 500 rows = 240,000 observed rows with 50% overlap.
  // Unique records are immutable and dedup is exact, including process restarts.
  const before = storage.stats.conservative_indexed_writes;
  let unique = 0;
  for (let run = 0; run < 48; run++) {
    const now = NOW + run * 20 * 60_000;
    s.start('history', now);
    for (let page = 0; page < 10; page++) {
      const batch = run === 0 || page >= 5 ? run * 5 + page : (run - 1) * 5 + page + 5;
      // Preserve official IDs' immutable event timestamp across overlap scans.
      const time = NOW - 60_000 + batch * 1000;
      const rows = Array.from({ length: 500 }, (_, i) => trade(`${batch}-${i}`, time, { name: `재료 ${i % 100}` }));
      s.commitPage(s.active('history'), { rows, next_cursor: page === 9 ? null : `page-${page + 1}` }, now);
    }
    if (run % 8 === 0) s = new MarketStore(storage);
  }
  const snapshot = s.buildSnapshot(NOW + 48 * 20 * 60_000);
  assert.equal(count(snapshot), 122500);
  assert.equal(s.diagnostics().stored_trades, 122500);
  const writes = storage.stats.conservative_indexed_writes - before;
  assert(writes < 480 * 20, `Conservative indexed writes ${writes} exceeded 20/page`);
  assert.equal(s.one('SELECT COUNT(*) AS n FROM market_sales').n, 0, 'legacy row-per-trade table stays untouched');
  assert(s.one('SELECT MAX(LENGTH(CAST(payload AS BLOB))) AS n FROM market_v2_chunks').n < MAX_CHUNK_BYTES);
}));


test('retention maintenance is gated across alarms and restarts', () => fixture((s, storage) => {
  assert.equal(s.cleanup(NOW), true);
  s = new MarketStore(storage);
  const before = storage.stats.statements;
  assert.equal(s.cleanup(NOW + 1000), false);
  assert.equal(storage.stats.statements - before, 1, 'only the indexed metadata lookup runs');
  assert.equal(s.cleanup(NOW + 60_000), true);
}));


test('native accounting refunds unused reservation but crash retains the next reservation', () => fixture((s, storage) => {
  const limits = { writeLimit: 350, readLimit: 5000, reserveWrites: 300, reserveReads: 1000 };
  const reservation = s.reserveCollectionPage(NOW, limits);
  assert.equal(reservation.allowed, true);
  commit(s, 'history', [trade('measured')]);
  s.settleCollectionPage(reservation, NOW);
  const settled = s.diagnostics(NOW).sql_budget;
  assert(settled.charged_writes > 0 && settled.charged_writes < 300);
  assert(settled.charged_reads > 0 && settled.charged_reads < 1000);
  assert.equal(settled.charged_writes, settled.actual_writes);
  assert.equal(settled.charged_reads, settled.actual_reads);
  const pending = s.reserveCollectionPage(NOW, limits);
  assert.equal(pending.allowed, true, 'refund allows a second reservation');
  s = new MarketStore(storage); // Crash before settling pending.
  const restarted = s.diagnostics(NOW).sql_budget;
  assert.equal(restarted.charged_writes, settled.charged_writes + 300);
  assert.equal(restarted.charged_reads, settled.charged_reads + 1000);
  assert.equal(s.reserveCollectionPage(NOW, limits).allowed, false);
}));

test('approximate offline accounting never refunds a reservation', () => fixture((s, storage) => {
  const nativeExec = storage.sql.exec.bind(storage.sql);
  storage.sql.exec = (sql, ...args) => Array.from(nativeExec(sql, ...args));
  s = new MarketStore(storage);
  const reservation = s.reserveCollectionPage(NOW, { reserveWrites: 500, reserveReads: 50_000 });
  s.settleCollectionPage(reservation, NOW);
  const budget = s.diagnostics(NOW).sql_budget;
  assert.equal(budget.counters_available, false);
  assert.equal(budget.charged_writes, 500);
  assert.equal(budget.charged_reads, 50_000);
}));
