import test from 'node:test';
import assert from 'node:assert/strict';
import { createProxy } from '../src/proxy.mjs';
import { configFromEnv, createHttpServer } from '../src/server.mjs';

const SECRET = 'unit-test-server-secret';
const ITEM = '가는 실뭉치';
const SECOND = '거미줄';
const query = (name = ITEM, cursor) => '/v1/auction/list?' + new URLSearchParams({ item_name: name, ...(cursor ? { cursor } : {}) });
const page = (name = ITEM, extra = {}) => ({ auction_item: [{ item_name: name, auction_price_per_unit: 12.5, item_count: 3, extra: SECRET }], next_cursor: null, ...extra });
const response = value => new Response(JSON.stringify(value), { status: 200 });

function fixture(options = {}) {
  let time = Date.parse('2026-09-11T00:00:00Z');
  let spent = 0;
  const calls = [];
  const handler = createProxy({
    apiKey: SECRET, allowedNames: [ITEM, SECOND], now: () => time,
    budget: { reserve() { spent++; return { allowed: true }; } },
    fetchImpl: async (url, init) => { calls.push({ url, init }); return response(page()); },
    ...options,
  });
  return { handler, calls, spent: () => spent, advance(ms) { time += ms; }, time: () => time,
    get(url = query(), clientAddress = 'client-1', extra = {}) { return handler({ url, clientAddress, ...extra }); } };
}

test('no startup fetching; fixed upstream, key only in outgoing header, projected response', async () => {
  const f = fixture();
  assert.equal(f.calls.length, 0);
  const result = await f.get();
  assert.equal(result.status, 200);
  const { url, init } = f.calls[0];
  assert.equal(url.origin, 'https://open.api.nexon.com');
  assert.equal(url.pathname, '/mabinogi/v1/auction/list');
  assert.equal(url.searchParams.get('item_name'), ITEM);
  assert.equal(init.headers['x-nxopen-api-key'], SECRET);
  assert.equal(init.redirect, 'error');
  assert.equal(init.method, 'GET');
  assert.equal(init.headers.Cookie, undefined);
  assert.equal(result.body.fetched_at, '2026-09-11T00:00:00.000Z');
  assert.equal(result.body.auction_item[0].extra, undefined);
  assert.equal(JSON.stringify(result).includes(SECRET), false);
  assert.equal(url.toString().includes(SECRET), false);
});

test('cache reuses original fetched_at without upstream quota; expiry is request driven', async () => {
  const f = fixture();
  const first = await f.get();
  f.advance(30000);
  assert.deepEqual(await f.get(), first);
  assert.equal(f.spent(), 1);
  f.advance(30001);
  assert.equal(f.calls.length, 1);
  const fresh = await f.get();
  assert.notEqual(fresh.body.fetched_at, first.body.fetched_at);
  assert.equal(f.calls.length, 2);
  assert.equal(f.spent(), 2);
});

test('simultaneous identical page requests share one upstream reservation', async () => {
  let release;
  let calls = 0;
  const f = fixture({ fetchImpl: async () => { calls++; return new Promise(resolve => { release = resolve; }); } });
  const first = f.get();
  const second = f.get(query(), 'client-2');
  assert.equal(calls, 1);
  assert.equal(f.spent(), 1);
  release(response(page()));
  assert.deepEqual(await first, await second);
});

test('each opaque cursor gets its own cache entry and cannot change query or upstream host', async () => {
  const f = fixture();
  const cursor = 'abc&item_name=other&url=https://evil.invalid/?x=#part';
  await f.get(query(ITEM, cursor));
  const outgoing = f.calls[0].url;
  assert.equal(outgoing.searchParams.get('cursor'), cursor);
  assert.equal(outgoing.searchParams.getAll('item_name').length, 1);
  assert.equal(outgoing.searchParams.has('url'), false);
  await f.get(query());
  assert.equal(f.calls.length, 2);
});

test('missing key, empty allowlist and missing budget all fail closed without upstream calls', async () => {
  for (const options of [{ apiKey: '' }, { apiKey: 'bad\nkey' }, { allowedNames: [] }, { budget: null }]) {
    const f = fixture(options);
    assert.equal((await f.get()).body.error.code, 'PROXY_NOT_CONFIGURED');
    assert.equal(f.calls.length, 0);
  }
});

test('health only reports liveness and consumes no upstream quota', async () => {
  const f = fixture({ apiKey: '' });
  assert.deepEqual((await f.get('/health')).body, { status: 'ok' });
  assert.equal(f.calls.length, 0);
  assert.equal(f.spent(), 0);
});

test('unsupported methods, paths, names, duplicate and extra parameters fail before fetch', async () => {
  const f = fixture();
  const invalid = [
    '/v1/auction/list', query('설탕'), query('가는실뭉치'), query() + '&url=https://evil.invalid',
    query() + '&item_name=other', query() + '&cursor=x&cursor=y', query() + '&cursor=%00',
    query() + '&cursor=' + 'x'.repeat(2049), query() + '&cursor=%xx', query() + '#fragment',
    'https://evil.invalid' + query(), '//evil.invalid' + query(), '/v1/auction/other',
  ];
  for (const url of invalid) assert.ok((await f.get(url)).status >= 400, url);
  assert.equal((await f.get(query(), 'client-1', { method: 'POST' })).status, 405);
  assert.equal((await f.get(query(), 'client-1', { hasBody: true })).status, 400);
  assert.equal(f.calls.length, 0);
  assert.equal(f.spent(), 0);
});

test('per-client throttle also covers cached and invalid requests', async () => {
  const f = fixture({ clientRequestsPerMinute: 2 });
  await f.get();
  await f.get();
  const blocked = await f.get();
  assert.equal(blocked.status, 429);
  assert.equal(blocked.headers['Retry-After'], '60');
  assert.equal(f.calls.length, 1);
  assert.equal((await f.get(query(), 'other-client')).status, 200);
  f.advance(60000);
  assert.equal((await f.get()).status, 200);
});

test('shared upstream throttle is enforced across different clients and cursors', async () => {
  const f = fixture({ upstreamRequestsPerSecond: 1 });
  await f.get();
  assert.equal((await f.get(query(ITEM, 'next'), 'other-client')).status, 429);
  assert.equal(f.spent(), 1);
  f.advance(1000);
  assert.equal((await f.get(query(ITEM, 'next'), 'other-client')).status, 200);
});

test('a full or unavailable shared budget fails closed without fetching', async () => {
  for (const [reserve, code] of [
    [() => ({ allowed: false, retryAfter: 99 }), 'PROXY_QUOTA_EXCEEDED'],
    [() => { throw new Error(SECRET); }, 'PROXY_QUOTA_UNAVAILABLE'],
  ]) {
    const f = fixture({ budget: { reserve } });
    const result = await f.get();
    assert.equal(result.body.error.code, code);
    assert.equal(f.calls.length, 0);
    assert.equal(JSON.stringify(result).includes(SECRET), false);
  }
});

test('concurrency bound rejects new work while identical in-flight requests still join', async () => {
  let release;
  const f = fixture({ maxConcurrent: 1, fetchImpl: () => new Promise(resolve => { release = resolve; }) });
  const first = f.get();
  const joined = f.get();
  const blocked = await f.get(query(ITEM, 'another'));
  assert.equal(blocked.body.error.code, 'PROXY_BUSY');
  release(response(page()));
  assert.equal((await first).status, 200);
  assert.equal((await joined).status, 200);
});

test('upstream errors and exception text never reach the caller; failures are not cached', async () => {
  for (const [status, code] of [[401, 'PROXY_UPSTREAM_AUTH'], [403, 'PROXY_UPSTREAM_AUTH'], [429, 'PROXY_UPSTREAM_LIMIT'], [500, 'PROXY_UPSTREAM_ERROR'], [302, 'PROXY_UPSTREAM_ERROR']]) {
    const f = fixture({ fetchImpl: async () => new Response(SECRET, { status }) });
    const result = await f.get();
    assert.equal(result.body.error.code, code);
    assert.equal(JSON.stringify(result).includes(SECRET), false);
    await f.get();
    assert.equal(f.spent(), 2);
  }
  const f = fixture({ fetchImpl: async () => { throw new Error(SECRET); } });
  assert.equal(JSON.stringify(await f.get()).includes(SECRET), false);
});

test('invalid or oversized response is rejected, including streamed bodies without a length', async () => {
  const cases = [
    () => new Response('not-json ' + SECRET),
    () => response({ auction_item: 'wrong' }),
    () => response({ auction_item: [{ item_name: null }] }),
    () => response(page(ITEM, { next_cursor: '\r\n' })),
    () => response(page(ITEM, { next_cursor: SECRET })),
    () => response({ auction_item: [{ item_name: ITEM, auction_price_per_unit: '12', item_count: 1 }] }),
    () => new Response('a'.repeat(600), { headers: { 'content-length': '600' } }),
    () => new Response('a'.repeat(600)),
  ];
  for (const make of cases) {
    const f = fixture({ fetchImpl: async () => make(), maxResponseBytes: 512 });
    const result = await f.get();
    assert.equal(result.body.error.code, 'PROXY_INVALID_RESPONSE');
    assert.equal(JSON.stringify(result).includes(SECRET), false);
  }
});

test('related-item rows are filtered without discarding valid rows or the next cursor', async () => {
  const f = fixture({ fetchImpl: async () => response({
    auction_item: [...page().auction_item, ...page(SECOND).auction_item], next_cursor: 'next-page',
  }) });
  const result = await f.get();
  assert.equal(result.status, 200);
  assert.equal(result.body.auction_item.length, 1);
  assert.equal(result.body.auction_item[0].item_name, ITEM);
  assert.equal(result.body.next_cursor, 'next-page');
  const other = fixture({ fetchImpl: async () => response(page(SECOND, { next_cursor: 'next-page' })) });
  const empty = await other.get();
  assert.deepEqual(empty.body.auction_item, []);
  assert.equal(empty.body.next_cursor, 'next-page');
});

test('upstream timeout aborts the request and exposes only the fixed timeout message', async () => {
  const f = fixture({ timeoutMs: 10, fetchImpl: (_url, init) => new Promise((_resolve, reject) => {
    init.signal.addEventListener('abort', () => reject(new Error(SECRET)), { once: true });
  }) });
  const result = await f.get();
  assert.equal(result.status, 504);
  assert.equal(result.body.error.code, 'PROXY_TIMEOUT');
  assert.equal(JSON.stringify(result).includes(SECRET), false);
});

test('cache has a size bound and evicted entries fetch only on next explicit request', async () => {
  const f = fixture({ maxCacheEntries: 1 });
  await f.get();
  await f.get(query(ITEM, 'next'));
  assert.equal(f.spent(), 2);
  await f.get();
  assert.equal(f.spent(), 3);
});

test('safe environment defaults and explicit bounds; HTTP server starts no listener by construction', () => {
  const config = configFromEnv({});
  assert.equal(config.apiKey, '');
  assert.equal(config.host, '127.0.0.1');
  assert.equal(config.dailyBudget, 500);
  assert.equal(config.clientRequestsPerMinute, 600);
  assert.match(config.runtimeDirectory, /\.runtime$/);
  for (const env of [{ PORT: '0' }, { PORT: '65536' }, { UPSTREAM_REQUESTS_PER_24H: 'NaN' }, { HOST: 'evil.invalid' }, { CACHE_TTL_SECONDS: '301' }]) assert.throws(() => configFromEnv(env));
  const http = createHttpServer(async () => ({ status: 200, headers: {}, body: {} }));
  assert.equal(http.listening, false);
  assert.equal(http.maxConnections, 128);
  http.close();
});
