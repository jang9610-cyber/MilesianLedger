import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { MarketStore } from './market-store.mjs';

const NOW = Date.parse('2026-09-13T12:00:00Z');
const row = { name: '거미줄', category: '천옷/방직', quantity: 2, price: 100, comparable: 1 };
function fixture(action) {
  const db = new DatabaseSync(':memory:');
  const updates = [];
  const storage = { sql: { exec(sql, ...args) {
    if (sql.includes('CREATE TABLE')) { db.exec(sql); return []; }
    if (/^UPDATE market_v2_runs SET pages=/.test(sql)) updates.push(sql);
    return db.prepare(sql).all(...args);
  } }, transactionSync(fn) {
    db.exec('BEGIN');
    try { const result = fn(); db.exec('COMMIT'); return result; }
    catch (error) { db.exec('ROLLBACK'); throw error; }
  } };
  try { return action(new MarketStore(storage), storage, db, updates); } finally { db.close(); }
}

test('intermediate checkpoint keeps running state and clears retry fields without assigning indexed state', () => fixture((store, storage, db, updates) => {
  store.start('list', NOW);
  store.retry(store.active('list'), 'RETRY', NOW + 1, 10_000);
  assert.equal(store.commitPage(store.active('list'), { rows: [row], next_cursor: 'page-2' }, NOW + 2), true);
  const restarted = new MarketStore(storage), run = restarted.active('list');
  assert.equal(run.state, 'running'); assert.equal(run.finished, null); assert.equal(run.pages, 1);
  assert.equal(run.rows_seen, 1); assert.equal(run.cursor, 'page-2');
  assert.equal(run.attempts, 0); assert.equal(run.error, null); assert.equal(run.next_attempt, 0);
  assert.equal(restarted.published('list'), null);
  assert.doesNotMatch(updates[0], /\bstate\s*=/); assert.doesNotMatch(updates[0], /\bfinished\s*=/);
  assert.equal(restarted.commitPage(run, { rows: [row], next_cursor: null }, NOW + 3), true);
  const completed = restarted.published('list');
  assert.equal(restarted.active('list'), null); assert.equal(completed.state, 'complete');
  assert.equal(completed.finished, NOW + 3); assert.equal(completed.pages, 2); assert.equal(completed.rows_seen, 2);
  assert.equal(completed.cursor, null); assert.match(updates[1], /state='complete'/);
  assert.equal(restarted.buildSnapshot(NOW + 3).quotes[0].quantity, 4);
}));

test('failed checkpoint rolls back page, chunks, cursor and completion publication atomically', () => fixture((store, storage, db) => {
  store.commitPage(store.start('list', NOW), { rows: [row], next_cursor: 'page-2' }, NOW);
  const run = store.active('list'), chunks = store.chunkCount();
  db.exec("CREATE TRIGGER reject_checkpoint BEFORE UPDATE OF pages ON market_v2_runs BEGIN SELECT RAISE(ABORT,'checkpoint failure'); END");
  for (const next_cursor of ['page-3', null]) {
    assert.throws(() => store.commitPage(run, { rows: [row], next_cursor }, NOW + 1), /checkpoint failure/);
    assert.equal(store.chunkCount(), chunks); assert.equal(store.active('list').cursor, 'page-2');
    assert.equal(store.active('list').pages, 1); assert.equal(store.published('list'), null);
    assert.equal(store.one('SELECT COUNT(*) AS n FROM market_v2_pages').n, 1);
  }
  db.exec('DROP TRIGGER reject_checkpoint');
  store.fail(run, 'PERMANENT_FAILURE', NOW + 2);
  assert.equal(store.commitPage(run, { rows: [row], next_cursor: null }, NOW + 3), false);
  assert.equal(store.latest('list').state, 'failed'); assert.equal(store.published('list'), null);
}));
