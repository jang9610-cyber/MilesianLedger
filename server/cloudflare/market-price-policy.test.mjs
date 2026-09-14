import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { gunzipSync } from 'node:zlib';
import { isPriceComparable, PRICE_COMPARISON_POLICY_VERSION } from './market-price-policy.mjs';
import { ENCHANT_SCROLL_NAMES } from './market-enchant.mjs';
import { validateMarketPage } from './market-core.mjs';
import { MarketStore } from './market-store.mjs';
import { MarketSnapshots } from './market-snapshot.mjs';

const NOW = Date.parse('2026-09-14T04:00:00Z');
const excluded = ['근거리 장비', '한손 장비', '양손 장비', '검', '도끼', '둔기', '랜스', '핸들', '너클', '체인 블레이드',
  '원거리 장비', '활', '석궁', '듀얼건', '수리검', '아틀라틀', '마법 장비', '실린더', '스태프', '원드', '마도서', '힐링 원드',
  '점성술 장비', '대형 낫', '오브', '갑옷 장비', '중갑옷', '경갑옷', '천옷', '방어 장비', '장갑', '신발', '모자/가발', '방패', '로브',
  '액세서리', '얼굴 장식', '날개', '꼬리', '특수 장비', '악기', '생활 도구', '마리오네트', '에코스톤', '에이도스', '유물', '기타 장비',
  '토템', '애뮬릿', '펫 토템', '마기그래프', '마기그래프 도안'];
const included = ['원거리 소모품', '의자/사물', '낭만농장/달빛섬', '마법가루', '인챈트 스크롤', '도면', '옷본', '마족 스크롤',
  '기타 스크롤', '기타 재료', '책', '마비노벨', '페이지', '포션', '음식', '허브', '던전 통행증', '알반 훈련석', '개조석',
  '보석', '변신 메달', '염색 앰플', '스케치', '핀즈비즈', '기타 소모품', '주머니', '천옷/방직', '제련/블랙스미스',
  '힐웬 공학', '매직 크래프트', '제스처', '말풍선 스티커', '피니 펫', '불타래', '퍼퓸', '분양 메달', '뷰티 쿠폰', '기타',
  '인챈트 용품', '마기그래프 용품', '생활 재료', '대미지 스킨', '새 공개 분류'];
const raw = (name, category, options = []) => ({ item_name: name, auction_item_category: category, item_count: 3,
  auction_price_per_unit: 100, item_option: options, auction_buy_id: name + '-trade', date_auction_buy: new Date(NOW - 60_000).toISOString() });
const parsed = (kind, items) => validateMarketPage({ [kind === 'history' ? 'auction_history' : 'auction_item']: items,
  next_cursor: null }, kind, NOW);
const row = (name, category, extra = {}) => ({ name, category, quantity: 3, price: 100,
  comparable: 0, id: name + '-trade', time: NOW - 60_000, ...extra });
const commit = (store, kind, rows) => store.commitPage(store.start(kind, NOW), { rows, next_cursor: null }, NOW);
async function fixture(fn) {
  const db = new DatabaseSync(':memory:');
  const storage = {
    sql: { exec(sql, ...args) {
      if (sql.includes('CREATE TABLE')) { db.exec(sql); return []; }
      return db.prepare(sql).all(...args);
    } },
    transactionSync(fn) {
      db.exec('BEGIN');
      try { const value = fn(); db.exec('COMMIT'); return value; }
      catch (error) { db.exec('ROLLBACK'); throw error; }
    },
  };
  try { return await fn(new MarketStore(storage), storage); } finally { db.close(); }
}

test('only reviewed variable-option categories suppress average comparison', () => {
  for (const category of excluded) {
    assert.equal(isPriceComparable('동일 품목', category), false, category);
    assert.equal(isPriceComparable('동일 품목', ' \t' + category.replace(/ /g, '\t') + '\n'), false, 'normalized ' + category);
  }
  for (const category of included) assert.equal(isPriceComparable('동일 품목', category), true, category);
  for (const missing of ['', ' \t\n', null, undefined]) assert.equal(isPriceComparable('미분류 품목', missing), false);
  for (const name of ENCHANT_SCROLL_NAMES) {
    assert.equal(isPriceComparable(name, '인챈트 스크롤'), false, 'unidentified ' + name);
    assert.equal(isPriceComparable(name, '기타 소모품'), false, 'legacy category ' + name);
    assert.equal(isPriceComparable('템포 (접두 / 랭크 6) · ' + name, '인챈트 스크롤'), true, 'identified ' + name);
  }
});

test('history and listings apply category policy regardless of option metadata', () => {
  const options = [{ option_type: '거래 횟수', option_value: '1' }, { option_type: '설명', option_value: '일반 정보' }];
  for (const kind of ['history', 'list']) {
    for (const category of [...excluded, ...included]) {
      const plain = parsed(kind, [raw('일반 품목', category)]).rows[0];
      const withMetadata = parsed(kind, [raw('일반 품목', category, options)]).rows[0];
      assert.equal(plain.comparable, excluded.includes(category) ? 0 : 1, kind + ': ' + category);
      assert.equal(withMetadata.comparable, plain.comparable, kind + ' metadata: ' + category);
      assert.equal(withMetadata.name, '일반 품목'); assert.equal(withMetadata.price, 100);
    }
    const scroll = raw('전용 인챈트 스크롤', '인챈트 스크롤', [
      { option_type: '인챈트 종류', option_sub_type: '접두', option_value: '템포 (랭크 6)' }, ...options]);
    const named = parsed(kind, [scroll]).rows[0];
    assert.equal(named.name, '템포 (접두 / 랭크 6) · 전용 인챈트 스크롤'); assert.equal(named.comparable, 1);
    assert.equal(parsed(kind, [raw('전용 인챈트 스크롤', '인챈트 스크롤', options)]).rows[0].comparable, 0);
  }
});

test('new equipment snapshots expose their lowest listing but never a mixed-option average', async () => fixture(store => {
  const names = ['검', '애뮬릿', '마기그래프 도안', '기타 재료', '원거리 소모품', '개조석'];
  const trades = names.map(category => raw(category + ' 품목', category, [{ option_type: '옵션', option_value: 'A' }]));
  commit(store, 'history', parsed('history', trades).rows);
  const listings = trades.map(item => ({ ...item, item_count: 8, auction_price_per_unit: 75 }));
  commit(store, 'list', parsed('list', listings).rows);
  const snapshot = store.buildSnapshot(NOW);
  for (const period of ['items_24h', 'items_7d']) for (const item of snapshot[period]) {
    assert.equal(item.lowest_listing_price, 75, item.name);
    assert.equal(item.price_comparable, !excluded.includes(item.category));
    assert.equal(item.average_sale_price, item.price_comparable ? 100 : null);
    assert.equal(item.trade_count, 1); assert.equal(item.sold_quantity, 3); assert.equal(item.traded_gold, 300);
    assert.equal(item.listing_count, 1); assert.equal(item.listed_quantity, 8);
  }
  assert.ok(snapshot.quotes.every(item => item.unit_price === 75));
}));

test('retained chunks are reinterpreted without rewriting data or rescan and keep exact-ID dedup', async () => fixture((store, storage) => {
  const material = row('메모가 있는 개조석', '개조석');
  const bundle = row('도면 묶음', '도면');
  const equipment = row('옵션 검', '검', { comparable: 1 });
  const generic = row('전용 인챈트 스크롤', '인챈트 스크롤', { comparable: 1 });
  const named = row('템포 (접두 / 랭크 6) · 전용 인챈트 스크롤', '인챈트 스크롤');
  const history = [material, { ...material, id: 'higher-price', quantity: 1, price: 900, comparable: 1 }, bundle, equipment, generic, named];
  commit(store, 'history', history);
  commit(store, 'list', [material, bundle, equipment, generic, named].map(item => ({ ...item, price: 25 })));
  const before = store.rows('SELECT * FROM market_v2_chunks ORDER BY run_id,page,part');
  const restarted = new MarketStore(storage), snapshot = restarted.buildSnapshot(NOW);
  for (const period of ['items_24h', 'items_7d']) {
    const byName = new Map(snapshot[period].map(item => [item.name, item]));
    assert.equal(byName.get(material.name).average_sale_price, 300, 'weighted (3×100 + 1×900)/4 from old chunks');
    assert.equal(byName.get(material.name).trade_count, 2);
    for (const item of [material, bundle, named]) {
      assert.equal(byName.get(item.name).price_comparable, true, item.name);
      assert.equal(byName.get(item.name).lowest_listing_price, 25, item.name);
    }
    assert.equal(byName.get(equipment.name).price_comparable, false);
    assert.equal(byName.get(equipment.name).average_sale_price, null);
    assert.equal(byName.get(equipment.name).lowest_listing_price, 25);
    assert.equal(byName.get(generic.name).price_comparable, false);
    assert.equal(byName.get(generic.name).average_sale_price, null);
    assert.equal(byName.get(generic.name).lowest_listing_price, null);
  }
  assert.deepEqual(restarted.rows('SELECT * FROM market_v2_chunks ORDER BY run_id,page,part'), before, 'publication does not migrate retained chunks');
  restarted.commitPage(restarted.start('history', NOW + 1), { rows: history.map(item => ({ ...item, comparable: 1 })), next_cursor: null }, NOW + 1);
  assert.equal(restarted.buildSnapshot(NOW + 1).items_24h.reduce((total, item) => total + item.trade_count, 0), 6);
}));

test('policy version republishes an existing run once, with compatible gzip and released-client quotes', async () => fixture(async store => {
  commit(store, 'history', [row('일반 도면', '도면')]); commit(store, 'list', [row('일반 도면', '도면', { price: 30 })]);
  const snapshots = new MarketSnapshots(store), old = store.buildSnapshot(NOW);
  for (const period of ['items_24h', 'items_7d']) {
    old[period][0].price_comparable = false; old[period][0].average_sale_price = null; old[period][0].lowest_listing_price = null;
  }
  const oldSignature = 'enchant-names-v1:' + store.meta('history_published') + '|' + store.meta('list_published');
  await snapshots.publish(old, oldSignature, NOW);
  const oldVersion = snapshots.manifest().version;
  assert.ok(store.publicationSignature().startsWith(PRICE_COMPARISON_POLICY_VERSION + ':'));
  assert.equal(await snapshots.publish(store.buildSnapshot(NOW), store.publicationSignature(), NOW), true);
  assert.notEqual(snapshots.manifest().version, oldVersion);
  const response = snapshots.artifactResponse(snapshots.manifest().version);
  const decoded = JSON.parse(gunzipSync(Buffer.from(await response.arrayBuffer())).toString('utf8'));
  assert.equal(decoded.schema_version, 1); assert.equal(decoded.items_24h[0].average_sale_price, 100);
  assert.equal(decoded.items_24h[0].lowest_listing_price, 30);
  assert.equal((await (await snapshots.quoteResponse('일반 도면', null)).json()).auction_item[0].auction_price_per_unit, 30);
  assert.equal(await snapshots.publish(store.buildSnapshot(NOW + 1000), store.publicationSignature(), NOW + 1000), false);
}));
