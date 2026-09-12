import { MarketStore } from './market-store.mjs';
import { HISTORY_INTERVAL, LIST_INTERVAL, json, marketError, parseMarketQuery, validateMarketPage } from './market-core.mjs';

export async function marketFetch(request, env) {
  const url = new URL(request.url);
  if (!url.pathname.startsWith('/v1/market/')) return null;
  if (request.method !== 'GET') return marketError(405, 'MARKET_READ_ONLY');
  if (!['/v1/market/status', '/v1/market/rankings'].includes(url.pathname)) return marketError(404, 'MARKET_NOT_FOUND');
  if (env.MARKET_ENABLED !== 'true' || !env.MARKET_COLLECTOR) return marketError(503, 'MARKET_NOT_ENABLED');
  if (url.toString().length > 4096) return marketError(400, 'MARKET_INVALID_QUERY');
  try { if (url.pathname.endsWith('/status') && url.search) throw Error(); parseMarketQuery(url); }
  catch { return marketError(400, 'MARKET_INVALID_QUERY'); }
  return env.MARKET_COLLECTOR.get(env.MARKET_COLLECTOR.idFromName('market-v1')).fetch(new Request('https://market.internal' + url.pathname + url.search));
}
export async function marketScheduled(event, env) {
  if (env.MARKET_ENABLED !== 'true' || !env.MARKET_COLLECTOR) return;
  const response = await env.MARKET_COLLECTOR.get(env.MARKET_COLLECTOR.idFromName('market-v1'))
    .fetch(new Request('https://market.internal/tick', { method: 'POST' }));
  if (!response.ok) throw Error('Market scheduler failed');
}

// Alarm-driven durable work queue. One page per stream per invocation; checkpoints survive restarts.
export class MarketCollector {
  constructor(state, env) { this.state = state; this.env = env; this.store = new MarketStore(state.storage); this.queue = Promise.resolve(); this.cache = new Map(); }
  serial(fn) { const job = this.queue.then(fn); this.queue = job.catch(() => {}); return job; }
  fetch(request) { return this.serial(() => this.handle(request)); }
  async handle(request) {
    if (this.env.MARKET_ENABLED !== 'true') return marketError(503, 'MARKET_NOT_ENABLED');
    const url = new URL(request.url), now = Date.now();
    if (url.pathname === '/tick' && request.method === 'POST') {
      for (const kind of ['history', 'list']) {
        const latest = this.store.latest(kind);
        if (!this.store.active(kind) && (!latest || now - latest.started >= (kind === 'history' ? HISTORY_INTERVAL : LIST_INTERVAL))) this.store.start(kind, now);
      }
      await this.state.storage.setAlarm(now + 1000);
      return json({ accepted: true });
    }
    if (request.method !== 'GET') return marketError(405, 'MARKET_READ_ONLY');
    if (url.pathname === '/v1/market/status') return json(this.store.status(now));
    if (url.pathname === '/v1/market/rankings') {
      let query;
      try { query = parseMarketQuery(url); } catch { return marketError(400, 'MARKET_INVALID_QUERY'); }
      const key = JSON.stringify(query), cached = this.cache.get(key);
      if (cached && now - cached.time < 30_000) return json(cached.body);
      const body = this.store.rankings(query, now);
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
    for (const kind of ['history', 'list']) {
      const run = this.store.active(kind);
      if (!run) continue;
      const now = Date.now();
      if (now - run.started > (kind === 'history' ? 15 : 50) * 60_000 || run.pages >= 10000) {
        this.store.fail(run, 'MARKET_INCOMPLETE_SCAN', now); continue;
      }
      if (now < run.next_attempt) continue;
      try {
        const params = new URLSearchParams({ kind });
        if (run.cursor) params.set('cursor', run.cursor);
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
      }
    }
    this.store.cleanup(Date.now());
    const active = ['history', 'list'].map(k => this.store.active(k)).filter(Boolean);
    if (active.length) await this.state.storage.setAlarm(Math.max(Date.now() + 1000, Math.min(...active.map(r => r.next_attempt))));
    else await this.state.storage.deleteAlarm();
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
