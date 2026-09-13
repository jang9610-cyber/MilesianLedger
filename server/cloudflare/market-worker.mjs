import { MarketStore } from './market-store.mjs';
import { MarketSnapshots, snapshotRankings } from './market-snapshot.mjs';
import { HISTORY_INTERVAL, LIST_INTERVAL, json, marketError, parseMarketQuery, validateMarketPage } from './market-core.mjs';

async function adminAuthorized(request, env) {
  const token = /^Bearer ([a-f0-9]{64})$/.exec(request.headers.get('Authorization') || '')?.[1] || '';
  if (!/^[a-f0-9]{64}$/.test(env.MARKET_ADMIN_TOKEN || '') || !/^[a-f0-9]{64}$/.test(token)) return false;
  const encoder = new TextEncoder(), algorithm = { name: 'HMAC', hash: 'SHA-256' };
  const key = await crypto.subtle.importKey('raw', encoder.encode(env.MARKET_ADMIN_TOKEN), algorithm, false, ['sign']);
  const candidate = await crypto.subtle.importKey('raw', encoder.encode(token), algorithm, false, ['verify']);
  const message = encoder.encode('MilesianLedger market administration');
  return crypto.subtle.verify('HMAC', candidate, await crypto.subtle.sign('HMAC', key, message), message);
}

export async function marketFetch(request, env, context) {
  const url = new URL(request.url);
  if (!url.pathname.startsWith('/v1/market/')) return null;
  if (url.pathname.startsWith('/v1/market/admin/')) {
    if (!await adminAuthorized(request, env)) return marketError(401, 'MARKET_ADMIN_UNAUTHORIZED');
    if (env.MARKET_ENABLED !== 'true' || !env.MARKET_COLLECTOR) return marketError(503, 'MARKET_NOT_ENABLED');
    const stub = env.MARKET_COLLECTOR.get(env.MARKET_COLLECTOR.idFromName('market-v1'));
    if (url.pathname === '/v1/market/admin/metrics' && request.method === 'GET' && !url.search) return stub.fetch(new Request('https://market.internal/metrics'));
    if (url.pathname !== '/v1/market/admin/pilot' || request.method !== 'POST') return marketError(405, 'MARKET_INVALID_ADMIN_REQUEST');
    const id = url.searchParams.get('id'), pages = url.searchParams.get('pages');
    if (!/^[a-zA-Z0-9-]{1,64}$/.test(id || '') || !/^[0-9]{1,4}$/.test(pages || '') || +pages < 1 || +pages > 2000 ||
        [...url.searchParams.keys()].some(k => !['id', 'pages'].includes(k) || url.searchParams.getAll(k).length !== 1)) return marketError(400, 'MARKET_INVALID_QUERY');
    return stub.fetch(new Request('https://market.internal/pilot?' + new URLSearchParams({ id, pages }), { method: 'POST' }));
  }
  if (request.method !== 'GET') return marketError(405, 'MARKET_READ_ONLY');
  const artifact = /^\/v1\/market\/snapshots\/[a-f0-9]{64}\.json\.gz$/.test(url.pathname);
  const manifest = url.pathname === '/v1/market/manifest';
  if (!artifact && !manifest && !['/v1/market/status', '/v1/market/rankings'].includes(url.pathname)) return marketError(404, 'MARKET_NOT_FOUND');
  if (env.MARKET_ENABLED !== 'true' || !env.MARKET_COLLECTOR) return marketError(503, 'MARKET_NOT_ENABLED');
  if (url.toString().length > 4096) return marketError(400, 'MARKET_INVALID_QUERY');
  try { if ((artifact || manifest || url.pathname.endsWith('/status')) && url.search) throw Error(); if (!artifact && !manifest) parseMarketQuery(url); }
  catch { return marketError(400, 'MARKET_INVALID_QUERY'); }
  const cache = globalThis.caches?.default;
  const cacheKey = new Request(url.origin + url.pathname + url.search);
  if ((artifact || manifest) && cache) {
    const cached = await cache.match(cacheKey).catch(() => null);
    if (cached) return request.headers.get('If-None-Match') === cached.headers.get('ETag')
      ? new Response(null, { status: 304, headers: cached.headers }) : cached;
  }
  const response = await env.MARKET_COLLECTOR.get(env.MARKET_COLLECTOR.idFromName('market-v1')).fetch(new Request('https://market.internal' + url.pathname + url.search,
    { headers: request.headers.has('If-None-Match') ? { 'If-None-Match': request.headers.get('If-None-Match') } : {} }));
  if ((artifact || manifest) && cache && response.status === 200) {
    const put = cache.put(cacheKey, response.clone()).catch(() => {});
    if (context?.waitUntil) context.waitUntil(put); else await put;
  }
  return response;
}
export async function marketScheduled(event, env) {
  if (env.MARKET_ENABLED !== 'true' || env.MARKET_SCHEDULE_ENABLED !== 'true' || !env.MARKET_COLLECTOR) return;
  const response = await env.MARKET_COLLECTOR.get(env.MARKET_COLLECTOR.idFromName('market-v1'))
    .fetch(new Request('https://market.internal/tick', { method: 'POST' }));
  if (!response.ok) throw Error('Market scheduler failed');
}

// Alarm-driven durable work queue. Bounded batches amortize delayed alarm delivery; every page is checkpointed.
export class MarketCollector {
  constructor(state, env) { this.state = state; this.env = env; this.store = new MarketStore(state.storage); this.snapshots = new MarketSnapshots(this.store); this.queue = Promise.resolve(); this.cache = new Map(); }
  serial(fn) { const job = this.queue.then(fn); this.queue = job.catch(() => {}); return job; }
  fetch(request) { return this.serial(() => this.handle(request)); }
  async handle(request) {
    if (this.env.MARKET_ENABLED !== 'true') return marketError(503, 'MARKET_NOT_ENABLED');
    const url = new URL(request.url), now = Date.now();
    if (url.pathname === '/pilot' && request.method === 'POST') {
      const id = url.searchParams.get('id'), limit = Number(url.searchParams.get('pages'));
      if (!/^[a-zA-Z0-9-]{1,64}$/.test(id || '') || !Number.isInteger(limit) || limit < 1 || limit > 2000) return marketError(400, 'MARKET_INVALID_QUERY');
      if (this.store.meta('pilot-request:' + id)) {
        if (this.store.active('history') || this.store.active('list')) await this.state.storage.setAlarm(now + 1000);
        return json({ accepted: true, duplicate: true });
      }
      if (this.store.active('history') || this.store.active('list')) return marketError(409, 'MARKET_ALREADY_RUNNING');
      this.state.storage.transactionSync(() => {
        for (const kind of ['history', 'list']) { const run = this.store.start(kind, now); this.store.setMeta('pilot-limit:' + run.id, limit); }
        this.store.setMeta('pilot-request:' + id, now);
      });
      await this.state.storage.setAlarm(now + 1000);
      return json({ accepted: true, duplicate: false, max_pages_per_stream: limit });
    }
    if (url.pathname === '/metrics' && request.method === 'GET') return json({
      database_bytes: this.state.storage.sql.databaseSize ?? null,
      alarm_at: typeof this.state.storage.getAlarm === 'function' ? await this.state.storage.getAlarm() : null,
      server_time: new Date(now).toISOString(),
      ...this.store.diagnostics(), snapshot: this.snapshots.manifest(),
      snapshot_error: this.store.meta('snapshot_error'),
      upstream_usage: this.env.AUCTION_COORDINATOR ? await this.env.AUCTION_COORDINATOR.get(this.env.AUCTION_COORDINATOR.idFromName('global-v1'))
        .fetch(new Request('https://coordinator.internal/internal/usage')).then(r => r.ok ? r.json() : null).catch(() => null) : null,
      status: this.store.status(now), schedule_enabled: this.env.MARKET_SCHEDULE_ENABLED === 'true',
      shared_quotes_enabled: this.env.SHARED_MARKET_QUOTES_ENABLED === 'true',
      listing_interval_minutes: Number(this.env.MARKET_LIST_INTERVAL_MINUTES || 60),
    });
    if (url.pathname === '/tick' && request.method === 'POST') {
      if (this.env.MARKET_SCHEDULE_ENABLED !== 'true') return json({ accepted: false, reason: 'schedule_disabled' });
      for (const kind of ['history', 'list']) {
        const latest = this.store.latest(kind);
        const interval = kind === 'history' ? HISTORY_INTERVAL : Math.max(LIST_INTERVAL, Number(this.env.MARKET_LIST_INTERVAL_MINUTES || 60) * 60_000);
        if (!this.store.active(kind) && (!latest || now - latest.started >= interval)) this.store.start(kind, now);
      }
      await this.state.storage.setAlarm(now + 1000);
      return json({ accepted: true });
    }
    if (request.method !== 'GET') return marketError(405, 'MARKET_READ_ONLY');
    if (url.pathname === '/v1/market/manifest') return this.snapshots.manifestResponse(request);
    const artifact = /^\/v1\/market\/snapshots\/([a-f0-9]{64})\.json\.gz$/.exec(url.pathname);
    if (artifact) return this.snapshots.artifactResponse(artifact[1]);
    if (url.pathname === '/quote') return this.snapshots.quoteResponse(url.searchParams.get('item_name'), url.searchParams.get('cursor'), url.searchParams.get('canonical_name'));
    if (url.pathname === '/v1/market/status') return json({ ...this.store.status(now), schedule_enabled: this.env.MARKET_SCHEDULE_ENABLED === 'true' });
    if (url.pathname === '/v1/market/rankings') {
      let query;
      try { query = parseMarketQuery(url); } catch { return marketError(400, 'MARKET_INVALID_QUERY'); }
      const key = JSON.stringify(query), cached = this.cache.get(key);
      if (cached && now - cached.time < 30_000) return json(cached.body);
      const body = snapshotRankings(await this.snapshots.data(), query);
      if (!body) return marketError(503, 'MARKET_SNAPSHOT_NOT_READY');
      if (this.cache.size >= 32) this.cache.delete(this.cache.keys().next().value);
      this.cache.set(key, { time: now, body });
      return json(body);
    }
    return marketError(404, 'MARKET_NOT_FOUND');
  }
  alarm() { return this.serial(() => this.collect()); }
  async collect() {
    if (this.env.MARKET_ENABLED !== 'true') { await this.state.storage.deleteAlarm(); return; }
    // Persist a watchdog first; unexpected storage/runtime failure cannot strand a cursor forever.
    await this.state.storage.setAlarm(Date.now() + 60_000);
    const configuredPages = Number(this.env.MARKET_PAGES_PER_ALARM || 40);
    const maxPages = Number.isInteger(configuredPages) && configuredPages > 0 ? Math.min(40, configuredPages) : 40;
    const deadline = Date.now() + 8000;
    const kinds = ['history', 'list'];
    let cleaned = false, calls = 0, nextKind = 0;
    while (calls < maxPages && Date.now() < deadline) {
      if (this.env.MARKET_ENABLED !== 'true') break;
      const now = Date.now();
      let run = null, kind = null;
      // Alternate streams while both are ready, and stop immediately if every
      // remaining stream is in backoff. A completed stream cannot be restarted
      // by this alarm; only the scheduler or protected pilot creates runs.
      for (let offset = 0; offset < kinds.length; offset++) {
        const index = (nextKind + offset) % kinds.length;
        const candidate = this.store.active(kinds[index]);
        if (candidate && now >= candidate.next_attempt) {
          run = candidate; kind = kinds[index]; nextKind = (index + 1) % kinds.length; break;
        }
      }
      if (!run) break;
      const pilotLimit = Number(this.store.meta('pilot-limit:' + run.id));
      if (pilotLimit && run.pages >= pilotLimit) { this.store.fail(run, 'MARKET_PILOT_LIMIT', now); continue; }
      if (now - run.started > (kind === 'history' ? 15 : 50) * 60_000 || run.pages >= 10000) {
        this.store.fail(run, 'MARKET_INCOMPLETE_SCAN', now); continue;
      }
      if ((this.state.storage.sql.databaseSize || 0) >= 750 * 1024 * 1024) {
        this.store.fail(run, 'MARKET_STORAGE_CAPACITY', now); continue;
      }
      const reservation = this.store.reserveCollectionPage(now);
      if (!reservation.allowed) { this.store.fail(run, 'MARKET_STORAGE_BUDGET', now); continue; }
      try {
        const params = new URLSearchParams({ kind });
        if (run.cursor) params.set('cursor', run.cursor);
        calls++;
        const response = await this.env.AUCTION_COORDINATOR.get(this.env.AUCTION_COORDINATOR.idFromName('global-v1'))
          .fetch(new Request('https://coordinator.internal/internal/market?' + params));
        if (!response.ok) {
          const problem = await response.json().catch(() => ({}));
          const rawCode = problem?.error?.code;
          const code = typeof rawCode === 'string' && /^PROXY_[A-Z_]+$/.test(rawCode) ? rawCode : 'MARKET_UPSTREAM_ERROR';
          if (run.attempts >= 3 || ['PROXY_UPSTREAM_AUTH', 'PROXY_NOT_CONFIGURED', 'PROXY_QUOTA_EXCEEDED'].includes(code)) this.store.fail(run, code, now);
          else {
            const retry = Number(response.headers.get('Retry-After'));
            this.store.retry(run, code, now, Math.max(30_000 * 2 ** run.attempts, Math.min(300_000, (retry || 0) * 1000)));
          }
          continue;
        }
        // Upstream is already schema-projected by the shared coordinator.
        const page = await response.json();
        this.store.commitPage(run, page, Date.now());
        this.cache.clear();
      } catch (err) {
        const code = err?.message === 'MARKET_REPEAT_CURSOR' ? err.message : 'MARKET_STORAGE_OR_REQUEST_ERROR';
        if (code === 'MARKET_REPEAT_CURSOR' || run.attempts >= 3) this.store.fail(run, code, now);
        else this.store.retry(run, code, now, 30_000 * 2 ** run.attempts);
      } finally {
        if (!cleaned) { this.store.cleanup(Date.now()); cleaned = true; }
        this.store.settleCollectionPage(reservation, Date.now());
      }
    }
    await this.publishIfChanged();
    const active = ['history', 'list'].map(k => this.store.active(k)).filter(Boolean);
    if (active.length) await this.state.storage.setAlarm(Math.max(Date.now() + 1000, Math.min(...active.map(r => r.next_attempt))));
    else if (Number(this.store.meta('snapshot_retry_at')) > 0) await this.state.storage.setAlarm(Math.max(Date.now() + 60_000, Number(this.store.meta('snapshot_retry_at'))));
    else await this.state.storage.deleteAlarm();
  }
  async publishIfChanged() {
    const signature = this.store.publicationSignature();
    if (signature === '|' || signature === this.store.meta('snapshot_signature')) return;
    if (Date.now() < Number(this.store.meta('snapshot_retry_at'))) return;
    const previous = this.snapshots.manifest();
    const reservation = this.store.reserveCollectionPage(Date.now(), {
      reserveWrites: Math.max(40, Math.ceil((previous?.compressed_bytes || 0) / (256 * 1024)) * 4 + 20),
      reserveReads: Math.max(1000, this.store.chunkCount() * 3) });
    if (!reservation.allowed) { this.store.setMeta('snapshot_retry_at', Date.now() + reservation.retry_after_seconds * 1000); return; }
    try {
      const data = this.store.buildSnapshot(Date.now());
      data.status.schedule_enabled = this.env.MARKET_SCHEDULE_ENABLED === 'true';
      await this.snapshots.publish(data, signature);
      this.store.setMeta('snapshot_retry_at', null); this.cache.clear();
    } catch (err) {
      this.store.setMeta('snapshot_error', /^MARKET_[A-Z_]+$/.test(err?.message || '') ? err.message : 'MARKET_PUBLICATION_FAILED');
      this.store.setMeta('snapshot_retry_at', Date.now() + 5 * 60_000);
    } finally { this.store.settleCollectionPage(reservation, Date.now()); }
  }
}

// Called only by AuctionCoordinator, behind its private DO binding and shared rate/budget reservation.
export function parseInternalMarket(request, env) {
  const url = new URL(request.url);
  if (url.pathname !== '/internal/market') return null;
  if (env.MARKET_ENABLED !== 'true' || request.method !== 'GET' || url.toString().length > 4096) return { response: marketError(503, 'MARKET_NOT_ENABLED') };
  const kind = url.searchParams.get('kind'), cursor = url.searchParams.get('cursor') || '';
  if (!['list', 'history'].includes(kind) || cursor.length > 2048 || /\s|[\x00-\x1f\x7f]/.test(cursor) ||
      [...url.searchParams.keys()].some(k => !['kind', 'cursor'].includes(k) || url.searchParams.getAll(k).length > 1)) return { response: marketError(400, 'MARKET_INVALID_QUERY') };
  return { market: true, kind, cursor };
}
export { validateMarketPage };
