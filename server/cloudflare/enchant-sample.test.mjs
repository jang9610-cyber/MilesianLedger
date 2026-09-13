import test from 'node:test';
import assert from 'node:assert/strict';
import worker, { AuctionCoordinator } from './worker.mjs';
import { parseInternalMarket } from './market-worker.mjs';
import { projectEnchantSample } from './market-enchant-sample.mjs';

const ROUTE = '/v1/market/admin/enchant-sample';
const TOKEN = 'b'.repeat(64);
const SECRET = 'offline-enchant-sample-test-key';
const row = (extra = {}) => ({ item_name: '인챈트 스크롤', auction_item_category: '인챈트 스크롤',
  item_count: 1, auction_price_per_unit: 100, private_field: 'private',
  item_option: [{ option_type: '인챈트', option_sub_type: '접두', option_value: '테스트', option_value2: null, private_field: 'private' }], ...extra });
const page = () => ({ auction_item: [row()], next_cursor: 'must-not-be-followed', private_field: 'private' });

async function fixture(action, overrides = {}) {
  const savedFetch = globalThis.fetch, calls = [], internal = [], data = new Map();
  let upstream = () => new Response(JSON.stringify(page())), reservations = 0;
  const env = { AUCTION_ENABLED: 'true', MARKET_ENABLED: 'true', NEXON_API_KEY: SECRET, MARKET_ADMIN_TOKEN: TOKEN,
    UPSTREAM_REQUESTS_PER_24H: '500', UPSTREAM_REQUESTS_PER_SECOND: '5', ...overrides };
  const coordinator = new AuctionCoordinator({ storage: {
    async get(key) { return structuredClone(data.get(key)); },
    async put(key, value) { reservations++; data.set(key, structuredClone(value)); },
  } }, env);
  env.AUCTION_COORDINATOR = { idFromName(name) { assert.equal(name, 'global-v1'); return name; },
    get() { return { fetch(request) { internal.push(request); return coordinator.fetch(request); } }; } };
  globalThis.fetch = async (url, init) => { calls.push({ url: new URL(url), init }); return upstream(); };
  try {
    await action({ calls, internal, data, env, coordinator, get reservations() { return reservations; },
      upstream(handler) { upstream = handler; },
      async request(path = ROUTE, init = {}) {
        const response = await worker.fetch(new Request('https://ledger.example.test' + path,
          { headers: { Authorization: 'Bearer ' + TOKEN }, ...init }), env);
        return { status: response.status, headers: response.headers, body: await response.json() };
      },
    });
  } finally { globalThis.fetch = savedFetch; }
}

test('enchant sample requires existing administrator authentication before binding or upstream access', async () => {
  await fixture(async f => {
    for (const headers of [{}, { Authorization: 'Bearer ' + 'a'.repeat(64) }, { Authorization: 'Bearer invalid' }]) {
      assert.equal((await f.request(ROUTE, { headers })).status, 401);
    }
    assert.equal(f.calls.length, 0); assert.equal(f.internal.length, 0); assert.equal(f.reservations, 0);
  });
  await fixture(async f => {
    assert.equal((await f.request()).status, 401); assert.equal(f.calls.length, 0);
  }, { MARKET_ADMIN_TOKEN: '' });
});

test('enchant sample rejects all queries, methods, fragments and request body headers before upstream access', async () => {
  await fixture(async f => {
    for (const suffix of ['?', '?cursor=', '?cursor=other', '?sample=enchant-scroll', '?auction_item_category=other', '?url=https://evil.test', '#fragment']) {
      assert.equal((await f.request(ROUTE + suffix)).status, 400, suffix);
    }
    for (const method of ['POST', 'PUT', 'DELETE', 'HEAD']) assert.equal((await f.request(ROUTE, { method })).status, 405);
    assert.equal((await f.request(ROUTE, { headers: { Authorization: 'Bearer ' + TOKEN, 'content-length': '1' } })).status, 400);
    assert.equal(f.calls.length, 0); assert.equal(f.internal.length, 0); assert.equal(f.reservations, 0);
  });
});

test('internal sample accepts only list with exact sample name and no cursor parameter', () => {
  const parse = (query, init) => parseInternalMarket(new Request('https://coordinator.internal/internal/market?' + query, init), { MARKET_ENABLED: 'true' });
  assert.deepEqual(parse('kind=list&sample=enchant-scroll'), { market: true, kind: 'list', cursor: '', sample: 'enchant-scroll' });
  for (const query of ['kind=history&sample=enchant-scroll', 'kind=list&sample=', 'kind=list&sample=other',
    'kind=list&sample=enchant-scroll&cursor=', 'kind=list&sample=enchant-scroll&cursor=x',
    'kind=list&sample=enchant-scroll&sample=enchant-scroll', 'kind=list&sample=enchant-scroll&kind=list',
    'kind=list&sample=enchant-scroll&auction_item_category=other', 'kind=list&sample=enchant-scroll&url=https://evil.test',
    'kind=list&sample=enchant-scroll#fragment']) assert.equal(parse(query).response.status, 400, query);
  assert.ok(parse('kind=list&sample=enchant-scroll', { method: 'POST' }).response.status >= 400);
  assert.deepEqual(parse('kind=list&cursor=next'), { market: true, kind: 'list', cursor: 'next' });
});

test('admin sample makes one fixed category request through private binding and shared durable quota', async () => {
  await fixture(async f => {
    const response = await f.request(ROUTE, { headers: { Authorization: 'Bearer ' + TOKEN, Cookie: 'private-cookie' } });
    assert.equal(response.status, 200); assert.equal(f.calls.length, 1); assert.equal(f.reservations, 1);
    assert.equal(f.internal.length, 1); assert.equal(f.internal[0].headers.get('authorization'), null);
    assert.equal(f.internal[0].headers.get('cookie'), null);
    const { url, init } = f.calls[0];
    assert.equal(url.origin, 'https://open.api.nexon.com'); assert.equal(url.pathname, '/mabinogi/v1/auction/list');
    assert.deepEqual([...url.searchParams], [['auction_item_category', '인챈트 스크롤']]);
    assert.equal(init.method, 'GET'); assert.equal(init.redirect, 'manual');
    assert.equal(new Headers(init.headers).get('authorization'), null);
    assert.equal(new Headers(init.headers).get('x-nxopen-api-key'), SECRET);
    assert.equal(response.body.sample, 'enchant-scroll'); assert.equal(response.body.auction_item.length, 1);
    assert.deepEqual(Object.keys(response.body).sort(), ['auction_item', 'sample']);
    assert.equal(JSON.stringify(response.body).includes('private'), false);
    assert.equal(response.headers.get('cache-control'), 'no-store');
    assert.equal(f.data.get('upstream-budget-v1').timestamps.length, 1);
  });
});

test('sample projection bounds names, rows, options and strings and excludes non-string option values', () => {
  const huge = 'x'.repeat(3000);
  const projected = projectEnchantSample({ auction_item: Array.from({ length: 30 }, () => row({ item_name: huge,
    auction_item_category: huge, item_option: Array.from({ length: 60 }, () => ({ option_type: huge,
      option_sub_type: huge, option_value: huge, option_value2: { private: true }, other: 'private' })) })), next_cursor: 'private' });
  assert.equal(projected.auction_item.length, 12);
  for (const item of projected.auction_item) {
    assert.equal(item.item_name.length, 200); assert.equal(item.auction_item_category.length, 100);
    assert.equal(item.item_option.length, 32);
    for (const option of item.item_option) {
      assert.equal(option.option_type.length, 128); assert.equal(option.option_sub_type.length, 128);
      assert.equal(option.option_value.length, 2048); assert.equal(option.option_value2, null);
      assert.deepEqual(Object.keys(option).sort(), ['option_sub_type', 'option_type', 'option_value', 'option_value2']);
    }
  }
  assert.throws(() => projectEnchantSample({ auction_item: Array(501).fill(row()) }));
});

test('sample respects existing disabled mode and background quota without provider requests', async () => {
  await fixture(async f => { assert.equal((await f.request()).status, 503); assert.equal(f.calls.length, 0); }, { MARKET_ENABLED: 'false' });
  await fixture(async f => {
    f.data.set('upstream-budget-v1', { version: 1, timestamps: Array(400).fill(Date.now() - 1000) });
    assert.equal((await f.request()).status, 429); assert.equal(f.calls.length, 0); assert.equal(f.reservations, 0);
  });
});

test('sample never exposes reflected API keys or raw upstream errors', async () => {
  await fixture(async f => {
    f.upstream(() => new Response(JSON.stringify({ auction_item: [row({ item_option: [{ option_value: SECRET }] })] })));
    const response = await f.request(); assert.equal(response.status, 502);
    assert.equal(response.body.error.code, 'PROXY_INVALID_RESPONSE'); assert.equal(JSON.stringify(response).includes(SECRET), false);
  });
  await fixture(async f => {
    f.upstream(() => new Response(JSON.stringify({ error: { message: SECRET }, private: TOKEN }), { status: 500 }));
    const response = await f.request(); assert.equal(response.status, 502);
    assert.equal(response.body.error.code, 'PROXY_UPSTREAM_ERROR'); assert.equal(JSON.stringify(response).includes(SECRET), false);
    assert.equal(JSON.stringify(response).includes(TOKEN), false);
  });
});
