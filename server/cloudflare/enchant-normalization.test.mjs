import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { gunzipSync } from 'node:zlib';
import { namedEnchantScroll, ENCHANT_SCROLL_NAMES } from './market-enchant.mjs';
import { validateMarketPage } from './market-core.mjs';
import { MarketStore } from './market-store.mjs';
import { MarketSnapshots, snapshotRankings } from './market-snapshot.mjs';

const NOW = Date.parse('2026-09-13T12:00:00Z');
// Minimal verified option shapes; all prices, quantities and IDs below are synthetic.
const TEMPO = { option_type: '인챈트 종류', option_sub_type: '접미', option_value: '템포 (랭크 6)', option_value2: null };
const TRAJECTORY = { option_type: '인챈트 종류', option_sub_type: '접미', option_value: '궤적 (랭크 6)', option_value2: null };
const raw = (extra = {}) => ({ item_name: '전용 인챈트 스크롤', auction_item_category: '인챈트 스크롤',
  item_count: 2, auction_price_per_unit: 100, item_option: [TEMPO],
  auction_buy_id: 'synthetic-trade', date_auction_buy: new Date(NOW - 60_000).toISOString(), ...extra });
const page = (kind, items, cursor = null) => validateMarketPage({
  [kind === 'history' ? 'auction_history' : 'auction_item']: items, next_cursor: cursor,
}, kind, NOW);
const parsed = item => page('list', [item]).rows[0];
const name = (enchant = '템포', position = '접미', rank = '6', base = '전용 인챈트 스크롤') => `${enchant} (${position} / 랭크 ${rank}) · ${base}`;
async function fixture(action) {
  const db = new DatabaseSync(':memory:');
  const storage = {
    sql: { exec(sql, ...args) {
      if (sql.includes('CREATE TABLE')) { db.exec(sql); return []; }
      return db.prepare(sql).all(...args);
    } },
    transactionSync(fn) {
      db.exec('BEGIN');
      try { const result = fn(); db.exec('COMMIT'); return result; }
      catch (error) { db.exec('ROLLBACK'); throw error; }
    },
  };
  try { return await action(new MarketStore(storage), storage); } finally { db.close(); }
}
const commit = (store, kind, rows, now = NOW, cursor = null) => store.commitPage(
  store.active(kind) || store.start(kind, now), { rows, next_cursor: cursor }, now);

test('verified scroll option shape becomes a named price-comparable item in list and history', () => {
  for (const kind of ['list', 'history']) {
    const rows = page(kind, [raw({ item_option: [{ option_type: '내구도', option_value: '100%' }, TEMPO] }),
      raw({ auction_buy_id: 'other-synthetic', item_option: [TRAJECTORY] })]).rows;
    assert.deepEqual(rows.map(row => row.name), [name(), name('궤적')]);
    assert.ok(rows.every(row => row.comparable === 1 && row.category === '인챈트 스크롤'));
    if (kind === 'history') assert.equal(rows[0].id, 'synthetic-trade');
  }
  assert.equal(namedEnchantScroll(raw({ item_option: [TEMPO, { ...TEMPO }] })), name(), 'identical duplicate options agree');
  assert.equal(namedEnchantScroll(raw({ item_option: [{ ...TEMPO, option_value: '  템포   (랭크 a)  ' }] })), name('템포', '접미', 'A'));
  assert.equal(namedEnchantScroll(raw({ item_name: '개방된 전용 인챈트 스크롤',
    item_option: [{ ...TEMPO, option_value: '가'.repeat(172) + ' (랭크 6)' }] })), null, 'decorated names retain the client 200-character bound');
});

test('enchant, prefix/suffix, rank and scroll base distinguish items while identical variants aggregate', async () => fixture(store => {
  const inputs = [raw(), raw({ item_count: 3, auction_price_per_unit: 80 }), raw({ item_option: [TRAJECTORY] }),
    raw({ item_option: [{ ...TEMPO, option_sub_type: '접두' }] }), raw({ item_option: [{ ...TEMPO, option_value: '템포 (랭크 7)' }] }),
    raw({ item_name: '인챈트 스크롤' })];
  commit(store, 'list', page('list', inputs).rows);
  commit(store, 'history', page('history', inputs.map((item, i) => ({ ...item, auction_buy_id: 'variant-' + i }))).rows);
  const snapshot = store.buildSnapshot(NOW);
  assert.equal(snapshot.items_24h.length, 5); assert.equal(snapshot.quotes.length, 5);
  const same = snapshot.items_24h.find(item => item.name === name());
  assert.equal(same.listed_quantity, 5); assert.equal(same.sold_quantity, 5);
  assert.equal(same.listing_count, 2); assert.equal(same.trade_count, 2);
  assert.equal(same.lowest_listing_price, 80); assert.equal(same.average_sale_price, 88);
  assert.ok(snapshot.items_24h.every(item => item.price_comparable));
  assert.equal(snapshot.quotes.find(item => item.name === name()).unit_price, 80);
}));

test('missing, malformed, conflicting and oversized enchant options remain unidentified without public unit prices', async () => fixture(store => {
  const options = [undefined, null, [], [null], [{ ...TEMPO, option_type: '인챈트' }],
    [{ ...TEMPO, option_type: 1 }], [{ ...TEMPO, option_type: 'x'.repeat(500) }],
    [{ ...TEMPO, option_sub_type: '앞' }], [{ ...TEMPO, option_sub_type: null }],
    [{ ...TEMPO, option_value: 123 }], [{ ...TEMPO, option_value: null }],
    [{ ...TEMPO, option_value: '템포' }], [{ ...TEMPO, option_value: ' (랭크 6)' }],
    [{ ...TEMPO, option_value: '템포 (랭크 6) 뒤' }], [{ ...TEMPO, option_value: '템포\n(랭크 6)' }],
    [{ ...TEMPO, option_value: '템포 (랭크 6) (랭크 6)' }],
    [{ ...TEMPO, option_value: '가'.repeat(181) + ' (랭크 6)' }],
    [TEMPO, TRAJECTORY], [TEMPO, { ...TEMPO, option_sub_type: '접두' }], [TEMPO, { ...TEMPO, option_value: null }],
    [TEMPO, { ...TEMPO, option_value: '템포 (랭크 7)' }], Array(65).fill(TEMPO)];
  const rows = options.map(item_option => parsed(raw({ item_option })));
  for (const row of rows) { assert.equal(row.name, '전용 인챈트 스크롤'); assert.equal(row.comparable, 0); }
  commit(store, 'list', rows);
  commit(store, 'history', rows.map((row, i) => ({ ...row, id: 'unidentified-' + i, time: NOW - 60_000 })));
  const snapshot = store.buildSnapshot(NOW), item = snapshot.items_24h[0];
  assert.equal(item.sold_quantity, options.length * 2); assert.equal(item.listed_quantity, options.length * 2);
  assert.equal(item.trade_count, options.length); assert.equal(item.listing_count, options.length);
  assert.equal(item.average_sale_price, null); assert.equal(item.lowest_listing_price, null);
  assert.equal(item.price_comparable, false); assert.equal(snapshot.quotes[0].unit_price, null);
}));

test('malformed rank tokens cannot make unidentified scrolls price comparable', () => {
  for (const rank of ['BADRANK', '99999999', 'A6', '0', 'G']) {
    assert.equal(namedEnchantScroll(raw({ item_option: [{ ...TEMPO, option_value: `템포 (랭크 ${rank})` }] })), null, rank);
  }
});

test('equipment stays excluded while separately named scroll bundles retain their own prices', () => {
  const equipment = raw({ item_name: '테스트 검', auction_item_category: '한손 장비' });
  assert.equal(parsed(equipment).name, equipment.item_name); assert.equal(parsed(equipment).comparable, 0);
  for (const item of [raw({ item_name: '랜덤 인챈트 스크롤' }), raw({ item_name: '인챈트 스크롤 꾸러미' }),
    raw({ item_name: '인챈트 스크롤 선택 상자' })]) {
    const row = parsed(item); assert.equal(row.name, item.item_name); assert.equal(row.comparable, 1);
  }
  const material = parsed(raw({ item_name: '거미줄', auction_item_category: '천옷/방직', item_option: [] }));
  assert.equal(material.name, '거미줄'); assert.equal(material.comparable, 1);
});

test('old stored generic scroll chunks suppress price even if their comparable flag was true', async () => fixture((store, storage) => {
  const rows = [...ENCHANT_SCROLL_NAMES].map((name, i) => ({ name, category: '인챈트 스크롤', quantity: i + 1,
    price: 100 + i, comparable: 1, id: 'legacy-' + i, time: NOW - 60_000 }));
  commit(store, 'list', rows); commit(store, 'history', rows);
  const snapshot = new MarketStore(storage).buildSnapshot(NOW);
  for (const item of snapshot.items_24h) {
    const original = rows.find(row => row.name === item.name);
    assert.equal(item.price_comparable, false); assert.equal(item.lowest_listing_price, null);
    assert.equal(item.average_sale_price, null); assert.equal(item.sold_quantity, original.quantity);
    assert.equal(item.listed_quantity, original.quantity); assert.equal(item.trade_count, 1);
    assert.equal(item.traded_gold, original.quantity * original.price);
  }
  assert.ok(snapshot.quotes.every(quote => quote.unit_price === null && quote.quantity > 0));
}));

test('old history ID remains exactly deduplicated when a repeated trade acquires a named variant', async () => fixture((store, storage) => {
  const identified = page('history', [raw()]).rows[0];
  commit(store, 'history', [{ ...identified, name: '전용 인챈트 스크롤', comparable: 0 }]);
  store = new MarketStore(storage);
  commit(store, 'history', [identified, { ...identified, id: 'new-named-trade' }], NOW + 1);
  const snapshot = store.buildSnapshot(NOW + 1);
  assert.equal(snapshot.items_24h.reduce((sum, item) => sum + item.trade_count, 0), 2);
  assert.equal(snapshot.items_24h.reduce((sum, item) => sum + item.sold_quantity, 0), 4);
  assert.equal(snapshot.items_24h.find(item => item.name === name()).trade_count, 1);
  assert.equal(snapshot.items_24h.find(item => item.name === '전용 인챈트 스크롤').trade_count, 1);
  assert.equal(store.diagnostics(NOW + 1).stored_trades, 2);
}));

test('complete named listing scan replaces old generic supply while incomplete and failed scans preserve it', async () => fixture(store => {
  const identified = parsed(raw()), legacy = { ...identified, name: '전용 인챈트 스크롤', comparable: 0, quantity: 9 };
  commit(store, 'list', [legacy]); const oldSignature = store.publicationSignature();
  commit(store, 'list', [identified], NOW + 1, 'more');
  assert.equal(store.publicationSignature(), oldSignature);
  assert.equal(store.buildSnapshot(NOW + 1).quotes[0].name, legacy.name);
  assert.equal(store.buildSnapshot(NOW + 1).quotes[0].quantity, 9);
  assert.equal(store.buildSnapshot(NOW + 1).quotes[0].unit_price, null);
  store.fail(store.active('list'), 'SYNTHETIC_FAILURE', NOW + 2);
  assert.equal(store.buildSnapshot(NOW + 2).quotes[0].name, legacy.name);
  commit(store, 'list', [identified], NOW + 3);
  const snapshot = store.buildSnapshot(NOW + 3);
  assert.equal(snapshot.quotes.length, 1); assert.equal(snapshot.quotes[0].name, name());
  assert.equal(snapshot.quotes[0].quantity, 2); assert.equal(snapshot.quotes[0].unit_price, 100);
  assert.ok(snapshot.items_24h.every(item => item.name !== legacy.name));
}));

test('gzip snapshot keeps schema and name properties compatible and supports named enchant search', async () => fixture(async store => {
  commit(store, 'list', page('list', [raw(), raw({ item_option: [TRAJECTORY] })]).rows);
  const snapshots = new MarketSnapshots(store), expected = store.buildSnapshot(NOW);
  await snapshots.publish(expected, store.publicationSignature(), NOW);
  const response = snapshots.artifactResponse(snapshots.manifest().version);
  const decoded = JSON.parse(gunzipSync(Buffer.from(await response.arrayBuffer())).toString('utf8'));
  assert.equal(decoded.schema_version, 1); assert.deepEqual(decoded, expected);
  const rankings = snapshotRankings(decoded, { window: '24h', sort: 'supply', search: '템포', category: '', offset: 0, limit: 50 });
  assert.equal(rankings.items.length, 1); assert.equal(rankings.items[0].name, name());
  const quote = await (await snapshots.quoteResponse(name(), null)).json();
  assert.equal(quote.auction_item[0].item_name, name()); assert.equal(quote.auction_item[0].auction_price_per_unit, 100);
}));

test('new publication signature republishes generic price suppression without requiring new collection runs', async () => fixture(async store => {
  commit(store, 'list', [{ ...parsed(raw()), name: '전용 인챈트 스크롤', comparable: 1 }]);
  const snapshots = new MarketSnapshots(store), oldData = store.buildSnapshot(NOW);
  oldData.items_24h[0].lowest_listing_price = 100; oldData.items_24h[0].price_comparable = true;
  oldData.items_7d[0].lowest_listing_price = 100; oldData.items_7d[0].price_comparable = true;
  oldData.quotes[0].unit_price = 100;
  const oldSignature = `${store.meta('history_published') || ''}|${store.meta('list_published') || ''}`;
  await snapshots.publish(oldData, oldSignature, NOW);
  const oldVersion = snapshots.manifest().version;
  assert.notEqual(store.publicationSignature(), oldSignature);
  assert.equal(await snapshots.publish(store.buildSnapshot(NOW), store.publicationSignature(), NOW), true);
  assert.notEqual(snapshots.manifest().version, oldVersion);
  assert.equal((await snapshots.data()).quotes[0].unit_price, null);
  const quote = await (await snapshots.quoteResponse('전용 인챈트 스크롤', null)).json();
  assert.deepEqual(quote.auction_item, []); assert.equal(quote.listing_count, 1);
}));
