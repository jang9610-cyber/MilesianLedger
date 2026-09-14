import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import worker, { AuctionCoordinator, MarketCollector } from './worker.mjs';
import { MarketStore } from './market-store.mjs';
import { validateMarketPage, parseMarketQuery } from './market-core.mjs';

const NOW = Date.parse('2026-09-12T04:00:00Z');
const sale = (id = 'a', extra = {}) => ({ auction_buy_id: id, item_name: '거미줄', auction_item_category: '천옷/방직',
  item_count: 10, auction_price_per_unit: 200, date_auction_buy: new Date(NOW - 60_000).toISOString(), item_option: [], ...extra });
const page = (kind, items, cursor = null) => validateMarketPage({ [kind === 'history' ? 'auction_history' : 'auction_item']: items, next_cursor: cursor }, kind, NOW);
const query = extra => parseMarketQuery(new URL('https://test/v1/market/rankings?' + new URLSearchParams(extra)));
function storage() {
  const db = new DatabaseSync(':memory:'); const values = new Map();
  return {
    db, alarm: null,
    sql: { exec(sql, ...args) {
      // Schema initialization is the only multi-statement invocation.
      if (sql.includes('CREATE TABLE')) { db.exec(sql); return []; }
      const stmt = db.prepare(sql); return stmt.all(...args);
    } },
    transactionSync(fn) { db.exec('BEGIN'); try { const result = fn(); db.exec('COMMIT'); return result; } catch(e) { db.exec('ROLLBACK'); throw e; } },
    async get(key) { return structuredClone(values.get(key)); },
    async put(key, value) { values.set(key, structuredClone(value)); },
    async setAlarm(t) { this.alarm = t; }, async deleteAlarm() { this.alarm = null; },
  };
}
function storeFixture(fn) { const state = storage(); try { return fn(new MarketStore(state), state); } finally { state.db.close(); } }

test('production two-hour listing interval retains twenty-minute history and resumes after restart', async () => {
  const config = JSON.parse(readFileSync(new URL('./wrangler.market.jsonc', import.meta.url), 'utf8'));
  assert.equal(config.vars.MARKET_LIST_INTERVAL_MINUTES, '120');
  const state = storage(), savedNow = Date.now; let now = NOW;
  Date.now = () => now;
  try {
    const env = { ...config.vars };
    let c = new MarketCollector({ storage: state }, env);
    const tick = () => c.fetch(new Request('https://market.internal/tick', { method: 'POST' }));
    const complete = kind => c.store.commitPage(c.store.active(kind), { rows: [], next_cursor: null }, now);
    await tick(); complete('history'); complete('list');
    for (let minutes = 20; minutes < 120; minutes += 20) {
      now = NOW + minutes * 60_000;
      c = new MarketCollector({ storage: state }, env);
      await tick();
      assert(c.store.active('history'), 'history remains eligible every twenty minutes');
      assert.equal(c.store.active('list'), null, 'no premature full-list scan');
      complete('history');
    }
    now = NOW + 120 * 60_000; await tick();
    const listing = c.store.active('list');
    assert(listing); assert(c.store.active('history'));
    await tick(); assert.equal(c.store.active('list').id, listing.id, 'repeat tick cannot duplicate a running scan');
    const budget = c.store.reserveCollectionPage(now);
    assert.equal(budget.write_limit, 60_000, 'keep the collection write safety cap');
    assert.equal(budget.read_limit, 3_000_000);
  } finally { Date.now = savedNow; state.db.close(); }
});

test('daily storage guard stops collection before any upstream request', async () => {
  const state = storage(), now = Date.now(); let calls = 0;
  const env = { MARKET_ENABLED: 'true', AUCTION_COORDINATOR: { idFromName: x => x, get: () => ({ fetch: async () => { calls++; throw Error('must not fetch'); } }) } };
  try {
    const collector = new MarketCollector({ storage: state }, env);
    collector.store.start('history', now);
    collector.store.setMeta('sql_budget:' + new Date(now).toISOString().slice(0, 10), JSON.stringify({ charged_writes: 60000, charged_reads: 0 }));
    await collector.alarm();
    assert.equal(calls, 0); assert.equal(collector.store.latest('history').error, 'MARKET_STORAGE_BUDGET');
    const restarted = new MarketCollector({ storage: state }, env);
    restarted.store.start('list', now + 1); await restarted.alarm(); assert.equal(calls, 0);
  } finally { state.db.close(); }
});

test('overlapping history and redelivered pages count a trade only once', () => storeFixture(s => {
  const r1 = s.start('history', NOW); const p = page('history', [sale()]);
  s.commitPage(r1, p, NOW); s.commitPage(r1, p, NOW);
  const r2 = s.start('history', NOW + 20 * 60_000);
  s.commitPage(r2, page('history', [sale(), sale('b', { item_count: 2, auction_price_per_unit: 400 })]), NOW + 20 * 60_000);
  const result = s.rankings(query({}), NOW + 20 * 60_000).items[0];
  assert.equal(result.sold_quantity, 12); assert.equal(result.trade_count, 2);
  assert.equal(result.traded_gold, 2800); assert.equal(result.average_sale_price, 2800 / 12);
  assert.equal(result.listed_quantity, null); assert.equal(result.image_url, null);
}));
test('partial or failed listing scans keep the previous completed snapshot', () => storeFixture(s => {
  const old = s.start('list', NOW); s.commitPage(old, page('list', [sale()]), NOW);
  const next = s.start('list', NOW + 3600_000);
  s.commitPage(next, page('list', [sale('x', { item_count: 90 })], 'next'), NOW + 3600_000);
  assert.equal(s.rankings(query({}), NOW).items[0].listed_quantity, 10);
  s.fail(s.active('list'), 'MARKET_TEST_FAILURE', NOW + 3601_000);
  assert.equal(s.rankings(query({}), NOW).items[0].listed_quantity, 10);
}));
test('listing pages add once and publish only when all pages finish', () => storeFixture(s => {
  const run = s.start('list', NOW), p = page('list', [sale()], 'next');
  s.commitPage(run, p, NOW); s.commitPage(run, p, NOW);
  assert.equal(s.meta('list_published'), null);
  s.commitPage(s.active('list'), page('list', [sale('b', { auction_price_per_unit: 100 })]), NOW);
  const r = s.rankings(query({}), NOW).items[0];
  assert.equal(r.listed_quantity, 20); assert.equal(r.listing_count, 2); assert.equal(r.lowest_listing_price, 100);
}));
test('repeating cursor rolls back the entire bad page', () => storeFixture(s => {
  const run = s.start('history', NOW); s.commitPage(run, page('history', [sale()], 'x'), NOW);
  assert.throws(() => s.commitPage(s.active('history'), page('history', [sale('b')], 'x'), NOW), /REPEAT_CURSOR/);
  assert.equal(s.rankings(query({}), NOW).items.length, 0);
}));
test('equipment suppresses averages while material option metadata does not', () => storeFixture(s => {
  const run = s.start('history', NOW);
  s.commitPage(run, page('history', [sale('a', { item_option: [{ option_value: '특수' }] }), sale('b', { item_name: '검', auction_item_category: '검' })]), NOW);
  const items = s.rankings(query({}), NOW).items;
  const equipment = items.find(item => item.name === '검'), material = items.find(item => item.name === '거미줄');
  assert.equal(equipment.price_comparable, false); assert.equal(equipment.average_sale_price, null);
  assert.equal(material.price_comparable, true); assert.equal(material.average_sale_price, 200);
}));
test('empty successful snapshot means zero supply; expired data is marked stale', () => storeFixture(s => {
  s.commitPage(s.start('history', NOW), page('history', [sale()]), NOW);
  s.commitPage(s.start('list', NOW), page('list', []), NOW);
  assert.equal(s.rankings(query({}), NOW).items[0].listed_quantity, 0);
  assert.equal(s.status(NOW + 2 * 3600_000).listings.stale, true);
}));
test('query inputs are bounded, parameterized, and rankings support paging and filters', () => storeFixture(s => {
  s.commitPage(s.start('history', NOW), page('history', [sale('a'), sale('b', { item_name: "' OR 1=1 --", item_count: 20 })]), NOW);
  const r = s.rankings(query({ limit: 1 }), NOW); assert.equal(r.has_more, true); assert.equal(r.items.length, 1);
  assert.equal(s.rankings(query({ q: "' OR 1=1 --" }), NOW).items.length, 1);
  for (const invalid of [{sort:'name;DROP TABLE'}, {limit:501}, {window:'all'}, {offset:-1}]) assert.throws(() => query(invalid));
}));
test('upstream schema rejects malformed pages, IDs, cursors, quantities and future trades', () => {
  for (const p of [{ item_count: -1 }, { auction_buy_id: '' }, { auction_price_per_unit: 1.5 }, { date_auction_buy: 'bad' }, { date_auction_buy: new Date(NOW + 86400_000).toISOString() }]) assert.throws(() => page('history', [sale('a', p)]));
  assert.throws(() => page('history', [sale()], 'bad cursor'));
  assert.throws(() => page('history', Array.from({ length: 501 }, () => sale())));
});
test('public reads cannot initiate collection or access private upstream routes', async () => {
  let calls = 0;
  const env = { MARKET_ENABLED: 'true', MARKET_COLLECTOR: { idFromName: n => n, get: () => ({ fetch: async () => { calls++; return new Response('{}'); } }) } };
  assert.equal((await worker.fetch(new Request('https://test/internal/market?kind=history'), env)).status, 404);
  assert.equal((await worker.fetch(new Request('https://test/v1/market/tick', { method: 'POST' }), env)).status, 405);
  assert.equal(calls, 0);
  assert.equal((await worker.fetch(new Request('https://test/v1/market/status'), env)).status, 200);
  assert.equal(calls, 1);
  assert.equal((await worker.fetch(new Request('https://test/v1/market/status'), { ...env, MARKET_ENABLED: 'false' })).status, 503);
});
test('scheduler resumes persisted cursors after recreation and respects stop switch', async () => {
  const state = storage(); const savedNow = Date.now; let time = NOW; Date.now = () => time;
  const calls = [];
  const env = { MARKET_ENABLED:'true', MARKET_PAGES_PER_ALARM:'1', MARKET_SCHEDULE_ENABLED:'true', AUCTION_COORDINATOR: { idFromName:n=>n, get:()=>({fetch:async req=>{
    const u = new URL(req.url); calls.push(u.search);
    const kind = u.searchParams.get('kind');
    return Response.json(page(kind, [sale()], kind==='history' && !u.searchParams.has('cursor') ? 'next' : null));
  }}) } };
  try {
    let c = new MarketCollector({storage:state},env);
    await c.fetch(new Request('https://test/tick', {method:'POST'})); await c.alarm();
    assert.equal(c.store.active('history').cursor,'next');
    c = new MarketCollector({storage:state},env); time+=2000; await c.alarm();
    assert.equal(c.store.active('history'),null);
    time+=2000; await c.alarm(); assert.equal(calls.length,3);
    assert.equal(c.store.rankings(query({}),time).items[0].trade_count,1);
    time+=20*60_000; await c.fetch(new Request('https://test/tick', {method:'POST'}));
    env.MARKET_ENABLED='false'; await c.alarm(); assert.equal(calls.length,3); assert.equal(state.alarm,null);
  } finally {Date.now=savedNow;state.db.close();}
});
test('429 retry persists backoff; authentication failure terminates run', async () => {
  const state = storage(); const savedNow = Date.now; Date.now=()=>NOW;
  let code='PROXY_UPSTREAM_LIMIT', calls=0;
  const env={MARKET_ENABLED:'true',AUCTION_COORDINATOR:{idFromName:n=>n,get:()=>({fetch:async()=>{calls++;return Response.json({error:{code}},{status:429,headers:{'Retry-After':'60'}});}})}};
  try {
    const c=new MarketCollector({storage:state},env); c.store.start('history',NOW); await c.alarm();
    assert.equal(c.store.active('history').next_attempt,NOW+60_000); await c.alarm(); assert.equal(calls,1);
    c.store.sql.exec('UPDATE market_v2_runs SET next_attempt=0'); code='PROXY_UPSTREAM_AUTH'; await c.alarm();
    assert.equal(c.store.latest('history').state,'failed'); assert.equal(c.store.meta('history_published'),null);
  } finally {Date.now=savedNow;state.db.close();}
});
test('background uses shared quota and preserves the final 20 percent for barter', async () => {
  const savedFetch=globalThis.fetch,savedNow=Date.now; const state=storage(); let calls=0;
  Date.now=()=>NOW;
  globalThis.fetch=async()=>{calls++;return Response.json({auction_item:[{item_name:'거미줄',auction_price_per_unit:100,item_count:1}],next_cursor:null});};
  const env={AUCTION_ENABLED:'true',MARKET_ENABLED:'true',NEXON_API_KEY:'test-secret-long',UPSTREAM_REQUESTS_PER_24H:'5'};
  try {
    await state.put('upstream-budget-v1',{version:1,timestamps:[NOW-4000,NOW-3000,NOW-2000,NOW-1000]});
    const c=new AuctionCoordinator({storage:state},env);
    const market=await c.fetch(new Request('https://internal/internal/market?kind=history'));
    assert.equal(market.status,429);assert.equal(calls,0);
    const barter=await c.fetch(new Request('https://internal/auction?item_name='+encodeURIComponent('거미줄')));
    assert.equal(barter.status,200);assert.equal(calls,1);
  } finally {globalThis.fetch=savedFetch;Date.now=savedNow;state.db.close();}
});
test('shared coordinator fetches all-category history, projects schema and omits private fields', async () => {
  const state=storage(), savedFetch=globalThis.fetch, savedNow=Date.now; Date.now=()=>NOW;
  let target;
  globalThis.fetch=async url=>{target=new URL(url);return Response.json({auction_history:[sale('one',{private_field:'secret-field'})],next_cursor:'cursor-2'});};
  const env={AUCTION_ENABLED:'true',MARKET_ENABLED:'true',NEXON_API_KEY:'offline-key-value',UPSTREAM_REQUESTS_PER_24H:'10000'};
  try {
    const c=new AuctionCoordinator({storage:state},env);
    const r=await c.fetch(new Request('https://internal/internal/market?kind=history'));
    assert.equal(r.status,200);assert.equal(target.pathname,'/mabinogi/v1/auction/history');assert.equal(target.search,'');
    const body=await r.json();assert.equal(body.rows[0].id,'one');assert.equal(body.next_cursor,'cursor-2');
    assert.equal(JSON.stringify(body).includes('secret-field'),false);
    assert.equal((await state.get('upstream-budget-v1')).timestamps.length,1);
  } finally {globalThis.fetch=savedFetch;Date.now=savedNow;state.db.close();}
});
test('page storage failure rolls back both rows and cursor', () => storeFixture((s,state) => {
  const run=s.start('history',NOW);
  state.db.exec("CREATE TRIGGER fail_second BEFORE INSERT ON market_v2_pages BEGIN SELECT RAISE(ABORT,'disk failure fixture'); END");
  assert.throws(()=>s.commitPage(run,page('history',[sale('a'),sale('b')],'next'),NOW),/disk failure/);
  assert.equal(s.active('history').pages,0);assert.equal(s.rankings(query({}),NOW).items.length,0);
  assert.equal(s.rows('SELECT * FROM market_v2_pages').length,0);
}));
test('admin pilot requires secret authentication; public reads and cron cannot start disabled scheduling', async () => {
  let calls=0;
  const env={MARKET_ENABLED:'true',MARKET_ADMIN_TOKEN:'a'.repeat(64),MARKET_COLLECTOR:{idFromName:n=>n,get:()=>({fetch:async()=>{calls++;return Response.json({accepted:true});}})}};
  const url='https://test/v1/market/admin/pilot?id=offline-test&pages=4';
  for (const authorization of ['', 'Bearer '+ 'b'.repeat(64), 'a'.repeat(64)]) {
    const response=await worker.fetch(new Request(url,{method:'POST',headers:{Authorization:authorization}}),env);
    assert.equal(response.status,401);
  }
  await worker.scheduled({},env);assert.equal(calls,0);
  const response=await worker.fetch(new Request(url,{method:'POST',headers:{Authorization:'Bearer '+env.MARKET_ADMIN_TOKEN}}),env);
  assert.equal(response.status,200);assert.equal(calls,1);
  const excessive=await worker.fetch(new Request(url.replace('pages=4','pages=2001'),{method:'POST',headers:{Authorization:'Bearer '+env.MARKET_ADMIN_TOKEN}}),env);
  assert.equal(excessive.status,400);assert.equal(calls,1);
});
test('pilot page cap and request id survive restarts without publishing a partial listing', async () => {
  const state=storage(),savedNow=Date.now;let time=NOW,calls=0;Date.now=()=>time;
  const env={MARKET_ENABLED:'true',MARKET_SCHEDULE_ENABLED:'false',AUCTION_COORDINATOR:{idFromName:n=>n,get:()=>({fetch:async req=>{
    calls++; const kind=new URL(req.url).searchParams.get('kind');return Response.json(page(kind,[sale()],String(calls)));
  }})}};
  try {
    let c=new MarketCollector({storage:state},env);
    assert.equal((await (await c.fetch(new Request('https://internal/tick',{method:'POST'}))).json()).accepted,false);
    const pilot=()=>new Request('https://internal/pilot?id=test-1&pages=1',{method:'POST'});
    await c.fetch(pilot());await c.alarm();time+=2000;
    c=new MarketCollector({storage:state},env);await c.alarm();
    assert.equal(calls,2);assert.equal(c.store.latest('list').error,'MARKET_PILOT_LIMIT');assert.equal(c.store.meta('list_published'),null);
    assert.equal(c.store.latest('list').state,'limited');assert.equal(c.store.status(time).failed_runs_7d,0);assert.equal(c.store.status(time).limited_runs_7d,2);
    assert.equal((await (await c.fetch(pilot())).json()).duplicate,true);await c.alarm();assert.equal(calls,2);
  } finally {Date.now=savedNow;state.db.close();}
});
