import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import worker, { AuctionCoordinator } from './worker.mjs';

// Offline-only harness: every upstream fetch is replaced, and Durable Object storage is in memory.
const ITEM = '가는 실뭉치';
const SECOND = '거미줄';
const SECRET = 'offline-only-dummy-secret-' + 'a'.repeat(80);
const START = Date.parse('2026-09-11T01:00:00.000Z');
const savedFetch = globalThis.fetch;
const savedNow = Date.now;
const savedSetTimeout = globalThis.setTimeout;
const savedClearTimeout = globalThis.clearTimeout;
const page = (itemName = ITEM, extra = {}) => ({
  auction_item: [{ item_name: itemName, auction_price_per_unit: 12.5, item_count: 3, private_field: SECRET }],
  next_cursor: null, ...extra,
});
const jsonResponse = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const path = (name = ITEM, cursor, route = '/v1/auction/list') => route + '?' + new URLSearchParams({ item_name: name, ...(cursor === undefined ? {} : { cursor }) });

async function fixture(action, overrides = {}) {
  let time = START;
  let coordinator;
  const data = new Map();
  const calls = [];
  const bindingCalls = [];
  const reservations = [];
  const waits = [];
  let failRead = false, failWrite = false;
  let upstream = async () => jsonResponse(page());
  const storage = {
    async get(key) { if (failRead) throw new Error(SECRET); return structuredClone(data.get(key)); },
    async put(key, value) { if (failWrite) throw new Error(SECRET); data.set(key, structuredClone(value)); reservations.push(structuredClone(value)); },
  };
  const env = {
    AUCTION_ENABLED: 'true', NEXON_API_KEY: SECRET,
    UPSTREAM_REQUESTS_PER_24H: '500', UPSTREAM_REQUESTS_PER_SECOND: '5',
    AUCTION_COORDINATOR: {
      idFromName(name) { bindingCalls.push(name); return name; },
      get(id) {
        assert.equal(id, 'global-v1');
        if (!coordinator) coordinator = new AuctionCoordinator({ storage }, env);
        return { fetch(request, init) { return coordinator.fetch(request instanceof Request ? request : new Request(request, init)); } };
      },
    },
    ...overrides,
  };
  globalThis.fetch = async (url, init = {}) => {
    const call = { url: new URL(url instanceof Request ? url.url : url), init, headers: new Headers(init.headers), time: Date.now() };
    calls.push(call);
    return upstream(call);
  };
  Date.now = () => time;
  globalThis.setTimeout = (callback, milliseconds, ...args) => {
    waits.push(milliseconds);
    // Keep rate-spacing deterministic; accelerate only this fixture's fixed network timeout.
    return savedSetTimeout(() => { time += milliseconds; callback(...args); }, milliseconds >= 12000 ? 10 : 0);
  };
  globalThis.clearTimeout = savedClearTimeout;
  const f = {
    env, calls, bindingCalls, reservations, data, waits,
    advance(milliseconds) { time += milliseconds; },
    now: () => time,
    restart() { coordinator = undefined; },
    failRead() { failRead = true; },
    failWrite() { failWrite = true; },
    upstream(handler) { upstream = handler; },
    async get(route = path(), options = {}) {
      const response = await worker.fetch(new Request('https://ledger.example.test' + route, options), env);
      const text = await response.text();
      let body;
      try { body = JSON.parse(text); } catch { body = null; }
      return { status: response.status, headers: response.headers, body, text };
    },
  };
  try { await action(f); }
  finally { globalThis.fetch = savedFetch; Date.now = savedNow; globalThis.setTimeout = savedSetTimeout; globalThis.clearTimeout = savedClearTimeout; }
}

test('disabled and missing configuration never spend quota or contact the provider', async () => {
  for (const overrides of [
    { AUCTION_ENABLED: 'false', NEXON_API_KEY: '123' },
    { AUCTION_ENABLED: '', NEXON_API_KEY: '123' },
    { AUCTION_ENABLED: 'TRUE' },
    { NEXON_API_KEY: '' },
    { NEXON_API_KEY: 'bad\r\nkey' },
    { AUCTION_COORDINATOR: undefined },
    { UPSTREAM_REQUESTS_PER_24H: '10001' },
  ]) await fixture(async f => {
    assert.equal(f.calls.length, 0);
    const response = await f.get();
    assert.equal(response.status, 503);
    assert.equal(f.calls.length, 0);
    assert.equal(f.reservations.length, 0);
    assert.equal(response.text.includes(SECRET), false);
  }, overrides);
});

test('both desktop and short route use one fixed provider and only the server secret header', async () => {
  await fixture(async f => {
    assert.equal(f.calls.length, 0);
    const response = await f.get(path(ITEM, '', '/auction'), { headers: {
      Cookie: 'private-cookie', Authorization: 'private-auth', 'x-nxopen-api-key': 'caller-key', 'X-Forwarded-For': 'fake-client',
    } });
    assert.equal(response.status, 200);
    const call = f.calls[0];
    assert.equal(call.url.origin, 'https://open.api.nexon.com');
    assert.equal(call.url.pathname, '/mabinogi/v1/auction/list');
    assert.equal(call.url.searchParams.get('item_name'), ITEM);
    assert.equal(call.init.method, 'GET');
    assert.ok(['error', 'manual'].includes(call.init.redirect), 'Provider redirects must never be followed.');
    assert.equal(call.headers.get('x-nxopen-api-key'), SECRET);
    assert.equal(call.headers.get('cookie'), null);
    assert.equal(call.headers.get('authorization'), null);
    assert.equal(call.headers.get('x-forwarded-for'), null);
    assert.equal(call.url.toString().includes(SECRET), false);
    assert.equal(response.text.includes(SECRET), false);
    assert.equal(response.body.auction_item[0].private_field, undefined);
    assert.equal(response.body.auction_item[0].auction_price_per_unit, 12.5);
    assert.equal(response.body.auction_item[0].item_count, 3);
    assert.equal(response.body.fetched_at, new Date(START).toISOString());
    assert.match(response.headers.get('content-type'), /application\/json/);
    assert.equal(response.headers.get('cache-control'), 'no-store');
    assert.equal((await f.get(path())).status, 200);
    assert.equal(f.calls.length, 1);
    assert.ok(f.bindingCalls.every(name => name === 'global-v1'));
  });
});

test('invalid method, path, item, duplicate or extra query never reaches upstream', async () => {
  await fixture(async f => {
    for (const route of [
      '/', '/not-supported', '/v1/auction/list', path('설탕'), path('없는 품목'),
      path() + '&item_name=other', path() + '&cursor=x&cursor=y', path() + '&page=2',
      path() + '&url=https://evil.invalid', path() + '&api_key=caller-secret',
      path(ITEM, '\r\n'), path(ITEM, 'x'.repeat(2049)),
    ]) {
      const result = await f.get(route);
      assert.ok(result.status >= 400, route);
      assert.equal(f.calls.length, 0);
    }
    assert.equal((await f.get(path(), { method: 'POST', body: 'private-input' })).status, 405);
    assert.equal((await f.get(path(), { method: 'DELETE' })).status, 405);
    assert.equal(f.calls.length, 0);
    assert.equal(f.reservations.length, 0);
  });
});

test('root route mismatch exposes the desktop-recognized stop code', async () => {
  await fixture(async f => {
    const result = await f.get('/wrong-route');
    assert.equal(result.status, 404);
    assert.equal(result.body.error.code, 'PROXY_NOT_FOUND');
  });
});

test('cache preserves source time and expiry fetches only on the next explicit request', async () => {
  await fixture(async f => {
    const first = await f.get();
    f.advance(30000);
    const cached = await f.get();
    assert.deepEqual(cached.body, first.body);
    assert.equal(f.calls.length, 1);
    assert.equal(f.reservations.length, 1);
    f.advance(30001);
    assert.equal(f.calls.length, 1);
    const second = await f.get();
    assert.notEqual(second.body.fetched_at, first.body.fetched_at);
    assert.equal(f.calls.length, 2);
    assert.equal(f.reservations.length, 2);
  });
});

test('opaque cursor, exact matching and numeric response stay compatible with the desktop', async () => {
  await fixture(async f => {
    const cursor = 'abc&item_name=other&url=https://evil.invalid/?a=#part';
    f.upstream(async () => jsonResponse({
      auction_item: [...page().auction_item, ...page(SECOND).auction_item], next_cursor: cursor,
    }));
    const first = await f.get();
    assert.equal(first.status, 200);
    assert.equal(first.body.auction_item.length, 1);
    assert.equal(first.body.auction_item[0].item_name, ITEM);
    assert.equal(first.body.next_cursor, cursor);
    await f.get(path(ITEM, cursor));
    assert.equal(f.calls[1].url.searchParams.get('cursor'), cursor);
    assert.equal(f.calls[1].url.searchParams.getAll('item_name').length, 1);
    assert.equal(f.calls[1].url.searchParams.has('url'), false);
    f.upstream(async () => jsonResponse(page(SECOND, { next_cursor: 'next-page' })));
    const empty = await f.get(path(ITEM, 'second'));
    assert.deepEqual(empty.body.auction_item, []);
    assert.equal(empty.body.next_cursor, 'next-page');
  });
});

test('legacy mithril requests use canonical searches and preserve each request name in its own cache', async () => {
  for (const [legacy, canonical] of [['미스릴 광석', '미스릴광석'], ['미스릴 광석 조각', '미스릴광석 조각']]) await fixture(async f => {
    const cursor = 'next&item_name=other';
    f.upstream(async () => jsonResponse({
      auction_item: [...page(canonical).auction_item, ...page(legacy).auction_item, ...page(SECOND).auction_item],
      next_cursor: cursor,
    }));
    const old = await f.get(path(legacy));
    const current = await f.get(path(canonical));
    for (const [result, expectedName] of [[old, legacy], [current, canonical]]) {
      assert.equal(result.status, 200);
      assert.equal(result.body.auction_item.length, 1, 'Filter upstream rows by the canonical exact name.');
      assert.equal(result.body.auction_item[0].item_name, expectedName);
      assert.equal(result.body.next_cursor, cursor);
    }
    assert.equal(f.calls.length, 2, 'Different requested names must keep separate response caches.');
    assert.deepEqual((await f.get(path(legacy))).body, old.body);
    assert.deepEqual((await f.get(path(canonical))).body, current.body);
    assert.equal(f.calls.length, 2);
    assert.equal((await f.get(path(legacy, cursor))).body.auction_item[0].item_name, legacy);
    assert.equal((await f.get(path(canonical, cursor))).body.auction_item[0].item_name, canonical);
    assert.equal(f.calls.length, 4);
    assert.ok(f.calls.every(call => call.url.searchParams.get('item_name') === canonical));
    assert.equal(f.calls[2].url.searchParams.get('cursor'), cursor);
    assert.equal(f.calls[3].url.searchParams.get('cursor'), cursor);
    assert.equal(f.reservations.length, 4);
  });
});

test('aliases cannot bypass the canonical allowlist or accept loosely matching names', async () => {
  // Load a fixture with both canonical names removed; production exports need no test-only hooks.
  const source = await readFile(new URL('./worker.mjs', import.meta.url), 'utf8');
  const withoutCanonicalNames = source.replace(/const ITEM_NAMES = new Set\((\[[\s\S]*?\])\);/, (_match, list) =>
    'const ITEM_NAMES = new Set(' + JSON.stringify(JSON.parse(list).filter(name => !['미스릴광석', '미스릴광석 조각'].includes(name))) + ');');
  assert.notEqual(withoutCanonicalNames, source);
  const { default: restrictedWorker } = await import('data:text/javascript;base64,' + Buffer.from(withoutCanonicalNames.replaceAll("'./market-worker.mjs'", JSON.stringify(new URL('./market-worker.mjs', import.meta.url).href))).toString('base64'));
  await fixture(async f => {
    for (const name of ['미스릴 광석', '미스릴 광석 조각', '미스릴광석', '미스릴광석 조각']) {
      const result = await restrictedWorker.fetch(new Request('https://ledger.example.test' + path(name)), f.env);
      assert.equal(result.status, 400);
      assert.equal((await result.json()).error.code, 'PROXY_ITEM_NOT_ALLOWED');
    }
    for (const name of [' 미스릴 광석', '미스릴 광석 ', '미스릴  광석', '미스릴광석조각']) {
      assert.equal((await f.get(path(name))).body.error.code, 'PROXY_ITEM_NOT_ALLOWED');
    }
    assert.equal(f.calls.length, 0);
    assert.equal(f.reservations.length, 0);
    assert.equal(f.bindingCalls.length, 0);
  });
});

test('simultaneous identical pages share one fetch and one durable reservation', async () => {
  await fixture(async f => {
    let release, entered;
    const started = new Promise(resolve => { entered = resolve; });
    f.upstream(() => { entered(); return new Promise(resolve => { release = resolve; }); });
    const first = f.get();
    await started;
    const second = f.get();
    release(jsonResponse(page()));
    assert.deepEqual((await first).body, (await second).body);
    assert.equal(f.calls.length, 1);
    assert.equal(f.reservations.length, 1);
  });
});

test('shared quota survives object reconstruction and opens only as old reservations expire', async () => {
  await fixture(async f => {
    await f.get();
    await f.get(path(ITEM, 'next'));
    assert.equal(f.calls.length, 2);
    const blocked = await f.get(path(ITEM, 'third'));
    assert.equal(blocked.status, 429);
    assert.equal(blocked.body.error.code, 'PROXY_QUOTA_EXCEEDED');
    assert.ok(Number(blocked.headers.get('retry-after')) > 0);
    assert.equal(f.calls.length, 2);
    const state = f.data.get('upstream-budget-v1');
    assert.equal(state.version, 1);
    assert.equal(state.timestamps.length, 2);
    assert.equal(JSON.stringify(state).includes(SECRET), false);
    f.restart();
    assert.equal((await f.get(path(ITEM, 'fourth'))).status, 429);
    assert.equal(f.calls.length, 2);
    f.advance(24 * 60 * 60 * 1000 + 1);
    assert.equal((await f.get(path(ITEM, 'fourth'))).status, 200);
    assert.equal(f.calls.length, 3);
  }, { UPSTREAM_REQUESTS_PER_24H: '2' });
});

test('a 10000-request budget accepts its last reservation, blocks the next, and reopens on expiry', async () => {
  await fixture(async f => {
    const timestamps = Array.from({ length: 9999 }, (_, index) => START - (9999 - index) * 200);
    f.data.set('upstream-budget-v1', { version: 1, timestamps });
    const last = await f.get();
    assert.equal(last.status, 200);
    const full = f.data.get('upstream-budget-v1');
    assert.equal(full.timestamps.length, 10000);
    assert.deepEqual(full.timestamps.slice(0, -1), timestamps);
    assert.equal(full.timestamps.at(-1), START);
    f.restart();
    const blocked = await f.get(path(ITEM, 'over-budget'));
    assert.equal(blocked.status, 429);
    assert.equal(blocked.body.error.code, 'PROXY_QUOTA_EXCEEDED');
    assert.ok(Number(blocked.headers.get('retry-after')) > 0);
    assert.equal(f.calls.length, 1);
    assert.deepEqual(f.data.get('upstream-budget-v1'), full);
    f.advance(24 * 60 * 60 * 1000 - 9999 * 200);
    assert.equal((await f.get(path(ITEM, 'after-expiry'))).status, 200);
    const reopened = f.data.get('upstream-budget-v1');
    assert.equal(reopened.timestamps.length, 10000);
    assert.equal(reopened.timestamps[0], timestamps[1]);
    assert.equal(reopened.timestamps.at(-1), f.now());
    assert.equal(f.calls.length, 2);
    assert.equal(f.reservations.length, 2);
  }, { UPSTREAM_REQUESTS_PER_24H: '10000' });
});

test('raising the budget to 10000 across object recreation preserves all prior reservations', async () => {
  await fixture(async f => {
    await f.get();
    await f.get(path(ITEM, 'second'));
    assert.equal((await f.get(path(ITEM, 'third'))).body.error.code, 'PROXY_QUOTA_EXCEEDED');
    const previous = structuredClone(f.data.get('upstream-budget-v1'));
    f.env.UPSTREAM_REQUESTS_PER_24H = '10000';
    f.restart();
    assert.equal((await f.get(path(ITEM, 'third'))).status, 200);
    const updated = f.data.get('upstream-budget-v1');
    assert.equal(updated.version, previous.version);
    assert.deepEqual(updated.timestamps.slice(0, -1), previous.timestamps);
    assert.equal(updated.timestamps.length, 3);
    assert.ok(updated.timestamps.at(-1) - previous.timestamps.at(-1) >= 200);
    assert.equal(f.calls.length, 3);
    assert.equal(f.reservations.length, 3);
  }, { UPSTREAM_REQUESTS_PER_24H: '2' });
});

test('storage read/write failure and corrupt quota fail closed before the provider call', async () => {
  for (const failure of ['read', 'write', 'corrupt']) await fixture(async f => {
    if (failure === 'read') f.failRead();
    if (failure === 'write') f.failWrite();
    if (failure === 'corrupt') f.data.set('upstream-budget-v1', { version: 1, timestamps: ['not-a-time'] });
    const result = await f.get();
    assert.equal(result.status, 503, failure);
    assert.equal(result.body.error.code, 'PROXY_QUOTA_UNAVAILABLE');
    assert.equal(f.calls.length, 0);
    assert.equal(result.text.includes(SECRET), false);
  });
});

test('failed requests still reserve shared budget and never populate success cache', async () => {
  await fixture(async f => {
    f.upstream(async () => new Response(SECRET, { status: 500 }));
    assert.equal((await f.get()).status, 502);
    assert.equal((await f.get()).status, 502);
    assert.equal(f.calls.length, 2);
    assert.equal(f.reservations.length, 2);
    assert.equal((await f.get()).body.error.code, 'PROXY_QUOTA_EXCEEDED');
    assert.equal(f.calls.length, 2);
  }, { UPSTREAM_REQUESTS_PER_24H: '2' });
});

test('known item query rejections do not block later items or populate success cache', async () => {
  for (const name of ['OPENAPI00003', 'OPENAPI00004']) await fixture(async f => {
    f.upstream(async call => call.url.searchParams.get('item_name') === ITEM
      ? jsonResponse({ error: { name, message: SECRET }, private_field: SECRET }, 400)
      : jsonResponse(page(SECOND)));
    const [rejected, next] = await Promise.all([f.get(), f.get(path(SECOND))]);
    assert.equal(rejected.status, 400, name);
    assert.deepEqual(rejected.body, { error: {
      code: 'PROXY_ITEM_QUERY_REJECTED', message: '공식 경매장에서 이 품목의 검색 조건을 확인하지 못했습니다.',
    } });
    assert.equal(rejected.text.includes(SECRET), false);
    assert.equal(rejected.headers.get('retry-after'), null);
    assert.equal(next.status, 200);
    assert.equal(next.body.auction_item[0].item_name, SECOND);
    assert.equal((await f.get()).status, 400, 'A rejected query must not become a successful cached page.');
    assert.equal((await f.get(path(SECOND))).status, 200);
    assert.equal(f.calls.length, 3);
    assert.equal(f.reservations.length, 3, 'Rejected upstream queries still consume the shared budget.');
  });
});

test('upstream errors including HTTP400 authentication and maintenance remain terminal without raw text', async () => {
  for (const [status, payload, code] of [
    [400, { error: { name: 'OPENAPI00005', message: SECRET } }, 'PROXY_UPSTREAM_AUTH'],
    [400, { error: { name: 'OPENAPI00006', message: SECRET } }, 'PROXY_UPSTREAM_ERROR'],
    [400, { error: { name: 'OPENAPI00009', message: SECRET } }, 'PROXY_UPSTREAM_ERROR'],
    [400, { error: { name: 'OPENAPI00010', message: SECRET } }, 'PROXY_UPSTREAM_ERROR'],
    [400, { error: { name: 'UNKNOWN', message: 'OPENAPI00004 ' + SECRET } }, 'PROXY_UPSTREAM_ERROR'],
    [400, { error: { name: 'OPENAPI00004-extra', message: SECRET } }, 'PROXY_UPSTREAM_ERROR'],
    [400, { error: { code: 'OPENAPI00004', message: SECRET } }, 'PROXY_UPSTREAM_ERROR'],
    [400, SECRET, 'PROXY_UPSTREAM_ERROR'],
    [401, SECRET, 'PROXY_UPSTREAM_AUTH'], [403, SECRET, 'PROXY_UPSTREAM_AUTH'],
    [429, SECRET, 'PROXY_UPSTREAM_LIMIT'], [500, SECRET, 'PROXY_UPSTREAM_ERROR'],
    [302, SECRET, 'PROXY_UPSTREAM_ERROR'],
  ]) await fixture(async f => {
    f.upstream(async () => typeof payload === 'string' ? new Response(payload, { status }) : jsonResponse(payload, status));
    const result = await f.get();
    assert.equal(result.body.error.code, code);
    assert.equal(result.status, code === 'PROXY_UPSTREAM_AUTH' ? 503 : code === 'PROXY_UPSTREAM_LIMIT' ? 429 : 502);
    assert.equal(result.text.includes(SECRET), false);
    assert.equal(f.calls.length, 1);
    if (result.status === 429) assert.ok(Number(result.headers.get('retry-after')) > 0);
  });
  await fixture(async f => {
    f.upstream(async () => { throw new Error(SECRET); });
    const result = await f.get();
    assert.equal(result.status, 502);
    assert.equal(result.text.includes(SECRET), false);
  });
});

test('oversized error bodies cannot bypass the body limit with a known query code', async () => {
  await fixture(async f => {
    f.upstream(async () => jsonResponse({ error: { name: 'OPENAPI00004', message: SECRET }, padding: 'x'.repeat(16 * 1024) }, 400));
    const result = await f.get();
    assert.equal(result.status, 502);
    assert.equal(result.body.error.code, 'PROXY_UPSTREAM_ERROR');
    assert.equal(result.text.includes(SECRET), false);
    assert.equal(f.calls.length, 1);
  });
});

test('timeout while reading an error body remains a timeout rather than an item rejection', async () => {
  await fixture(async f => {
    f.upstream(async call => new Response(new ReadableStream({
      start(controller) {
        call.init.signal.addEventListener('abort', () => controller.error(new Error(SECRET)), { once: true });
      },
    }), { status: 400 }));
    const result = await f.get();
    assert.equal(result.status, 504);
    assert.equal(result.body.error.code, 'PROXY_TIMEOUT');
    assert.equal(result.text.includes(SECRET), false);
    assert.equal(f.calls.length, 1);
  });
});

test('malformed and oversized success bodies are sanitized and rejected', async () => {
  for (const make of [
    () => new Response('invalid JSON ' + SECRET),
    () => jsonResponse({ auction_item: {} }),
    () => jsonResponse({ auction_item: [{ item_name: ITEM, auction_price_per_unit: '12', item_count: 1 }] }),
    () => jsonResponse({ auction_item: [{ item_name: ITEM, auction_price_per_unit: 12, item_count: 1.5 }] }),
    () => jsonResponse(page(ITEM, { next_cursor: '\r\n' })),
    () => jsonResponse(page(ITEM, { next_cursor: SECRET })),
    () => new Response('a'.repeat(1024 * 1024 + 1)),
  ]) await fixture(async f => {
    f.upstream(async () => make());
    const result = await f.get();
    assert.equal(result.status, 502);
    assert.equal(result.body.error.code, 'PROXY_INVALID_RESPONSE');
    assert.equal(result.text.includes(SECRET), false);
    assert.equal(f.calls.length, 1);
  });
});

test('timeout aborts the single provider request and returns only the fixed failure', async () => {
  await fixture(async f => {
    let aborted = false;
    f.upstream(call => new Promise((_resolve, reject) => {
      call.init.signal.addEventListener('abort', () => { aborted = true; reject(new Error(SECRET)); }, { once: true });
    }));
    const result = await f.get();
    assert.equal(result.status, 504);
    assert.equal(result.body.error.code, 'PROXY_TIMEOUT');
    assert.equal(aborted, true);
    assert.equal(f.calls.length, 1);
    assert.equal(result.text.includes(SECRET), false);
  });
});

test('queued distinct requests serialize and preserve the global request spacing', async () => {
  await fixture(async f => {
    const responses = await Promise.all([f.get(path()), f.get(path(ITEM, 'two')), f.get(path(ITEM, 'three'))]);
    assert.ok(responses.every(result => result.status === 200));
    assert.equal(f.calls.length, 3);
    const stamps = f.calls.map(call => call.time);
    assert.ok(stamps[1] - stamps[0] >= 200);
    assert.ok(stamps[2] - stamps[1] >= 200);
    assert.ok(f.waits.filter(ms => ms < 12000).length >= 2);
  });
});

test('queue overload is rejected before it can spend another reservation', async () => {
  await fixture(async f => {
    let release, entered;
    const started = new Promise(resolve => { entered = resolve; });
    f.upstream(() => {
      if (f.calls.length === 1) { entered(); return new Promise(resolve => { release = resolve; }); }
      return Promise.resolve(jsonResponse(page()));
    });
    const pending = [f.get()];
    await started;
    for (let i = 1; i < 32; i++) pending.push(f.get(path(ITEM, 'queued-' + i)));
    const overflow = await f.get(path(ITEM, 'over-capacity'));
    assert.equal(overflow.status, 503);
    assert.equal(overflow.body.error.code, 'PROXY_BUSY');
    assert.equal(f.calls.length, 1);
    assert.equal(f.reservations.length, 1);
    release(jsonResponse(page()));
    const results = await Promise.all(pending);
    assert.ok(results.every(result => result.status === 200));
    assert.equal(f.calls.length, 32);
  });
});

test('cancelled caller starts no new provider work and aborts only its active request', async () => {
  await fixture(async f => {
    const before = new AbortController();
    before.abort();
    assert.equal((await f.get(path(), { signal: before.signal })).status, 503);
    assert.equal(f.calls.length, 0);
    assert.equal(f.reservations.length, 0);
    let entered, aborted = false;
    const started = new Promise(resolve => { entered = resolve; });
    f.upstream(call => new Promise((_resolve, reject) => {
      call.init.signal.addEventListener('abort', () => { aborted = true; reject(new Error(SECRET)); }, { once: true });
      entered();
    }));
    const during = new AbortController();
    const pending = f.get(path(), { signal: during.signal });
    await started;
    during.abort();
    const result = await pending;
    assert.equal(result.status, 503);
    assert.equal(aborted, true);
    assert.equal(f.calls.length, 1);
    assert.equal(f.reservations.length, 1);
    assert.equal(result.text.includes(SECRET), false);
  });
});

test('object recreation preserves global spacing and backwards clocks fail closed', async () => {
  await fixture(async f => {
    await f.get();
    f.restart();
    assert.equal((await f.get(path(ITEM, 'after-restart'))).status, 200);
    assert.ok(f.calls[1].time - f.calls[0].time >= 200);
    f.advance(-1000);
    f.restart();
    const backwards = await f.get(path(ITEM, 'clock-backwards'));
    assert.equal(backwards.status, 503);
    assert.equal(backwards.body.error.code, 'PROXY_QUOTA_UNAVAILABLE');
    assert.equal(f.calls.length, 2);
  });
});
