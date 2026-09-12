const UPSTREAM = 'https://open.api.nexon.com/mabinogi/v1/auction/list';
const CONTROL = /[\x00-\x20\x7f]/;
const ITEM_ALIASES = new Map([
  ['미스릴 광석', '미스릴광석'],
  ['미스릴 광석 조각', '미스릴광석 조각'],
]);
const MESSAGES = {
  PROXY_NOT_CONFIGURED: '경매장 연결이 아직 설정되지 않았습니다.',
  PROXY_INVALID_REQUEST: '경매장 요청 형식이 올바르지 않습니다.',
  PROXY_ITEM_NOT_ALLOWED: '조회할 수 없는 품목입니다.',
  PROXY_ITEM_QUERY_REJECTED: '공식 경매장에서 이 품목의 검색 조건을 확인하지 못했습니다.',
  PROXY_NOT_FOUND: '요청한 경로가 없습니다.',
  PROXY_METHOD_NOT_ALLOWED: 'GET 요청만 사용할 수 있습니다.',
  PROXY_RATE_LIMIT: '요청이 많습니다. 잠시 후 다시 갱신해 주세요.',
  PROXY_QUOTA_EXCEEDED: '서버의 경매장 조회 예산을 모두 사용했습니다.',
  PROXY_BUSY: '서버가 혼잡합니다. 잠시 후 다시 갱신해 주세요.',
  PROXY_QUOTA_UNAVAILABLE: '서버의 조회 예산을 확인할 수 없습니다.',
  PROXY_UPSTREAM_AUTH: '서버의 경매장 연결 인증을 확인해야 합니다.',
  PROXY_UPSTREAM_LIMIT: '공식 경매장 서비스의 요청 한도에 도달했습니다.',
  PROXY_UPSTREAM_ERROR: '공식 경매장 서비스에 연결하지 못했습니다.',
  PROXY_TIMEOUT: '경매장 응답 시간이 초과되었습니다.',
  PROXY_INVALID_RESPONSE: '경매장 응답을 확인하지 못했습니다.',
};

function failure(status, code, retryAfter) {
  return { status, headers: retryAfter ? { 'Retry-After': String(retryAfter) } : {}, body: { error: { code, message: MESSAGES[code] } } };
}

class WindowLimiter {
  constructor(limit, period, maxClients = 4096) {
    this.limit = limit;
    this.period = period;
    this.maxClients = maxClients;
    this.clients = new Map();
  }
  take(key, now) {
    let slot = this.clients.get(key);
    if (slot && now < slot.start + this.period) {
      if (slot.count >= this.limit) return Math.max(1, Math.ceil((slot.start + this.period - now) / 1000));
      slot.count++;
      return 0;
    }
    if (!slot && this.clients.size >= this.maxClients) {
      for (const [id, entry] of this.clients) if (now >= entry.start + this.period) this.clients.delete(id);
      if (this.clients.size >= this.maxClients) return Math.ceil(this.period / 1000);
    }
    this.clients.set(key, { start: now, count: 1 });
    return 0;
  }
}

function validatePage(value, itemName, responseName = itemName) {
  if (!value || !Array.isArray(value.auction_item) || value.auction_item.length > 1000 ||
      (value.next_cursor != null && (typeof value.next_cursor !== 'string' || value.next_cursor.length > 2048 || CONTROL.test(value.next_cursor)))) {
    throw new Error('Invalid response');
  }
  const items = value.auction_item.filter(item => {
    if (!item || typeof item.item_name !== 'string' || !item.item_name || item.item_name.length > 100) throw new Error('Invalid response');
    // Searches may include related items: keep only the exact item the planner asked for.
    return item.item_name === itemName;
  }).map(item => {
    if (typeof item.auction_price_per_unit !== 'number' ||
        !Number.isFinite(item.auction_price_per_unit) || item.auction_price_per_unit < 0 || item.auction_price_per_unit > 1e15 ||
        !Number.isSafeInteger(item.item_count) || item.item_count < 1 || item.item_count > 1e9) throw new Error('Invalid response');
    return { item_name: responseName, auction_price_per_unit: item.auction_price_per_unit, item_count: item.item_count };
  });
  return { auction_item: items, next_cursor: value.next_cursor || null };
}

async function readLimitedJson(response, limit) {
  const length = response.headers.get('content-length');
  if (length && (!/^\d+$/.test(length) || Number(length) > limit)) throw new Error('Response too large');
  const reader = response.body?.getReader();
  if (!reader) throw new Error('Missing response body');
  let size = 0;
  const chunks = [];
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > limit) throw new Error('Response too large');
      chunks.push(value);
    }
  } catch (error) {
    await reader.cancel().catch(() => {});
    throw error;
  } finally { reader.releaseLock(); }
  return JSON.parse(Buffer.concat(chunks, size).toString('utf8'));
}

export function createProxy({ apiKey = '', allowedNames = [], budget, fetchImpl = globalThis.fetch, now = Date.now,
  clientRequestsPerMinute = 600, upstreamRequestsPerSecond = 5, cacheTtlMs = 60000,
  maxConcurrent = 8, timeoutMs = 12000, maxResponseBytes = 1024 * 1024,
  maxCacheEntries = 256, maxCacheBytes = 8 * 1024 * 1024 } = {}) {
  const names = new Set(allowedNames);
  const configured = typeof apiKey === 'string' && apiKey.length > 0 && apiKey.length <= 2048 && !CONTROL.test(apiKey) &&
    names.size > 0 && names.size <= 1000 && [...names].every(n => typeof n === 'string' && n.length > 0 && n.length <= 100 && !/[\x00-\x1f\x7f]/.test(n)) &&
    budget && typeof budget.reserve === 'function';
  const clients = new WindowLimiter(clientRequestsPerMinute, 60000);
  const upstreamLimit = new WindowLimiter(upstreamRequestsPerSecond, 1000, 1);
  const cache = new Map();
  const running = new Map();
  let cacheBytes = 0;

  function cachePut(key, body) {
    const bytes = Buffer.byteLength(JSON.stringify(body), 'utf8');
    if (bytes > maxCacheBytes || maxCacheEntries < 1 || cacheTtlMs < 1) return;
    for (const [id, entry] of cache) if (now() >= entry.expires) { cache.delete(id); cacheBytes -= entry.bytes; }
    while (cache.size && (cache.size >= maxCacheEntries || cacheBytes + bytes > maxCacheBytes)) {
      const first = cache.keys().next().value;
      cacheBytes -= cache.get(first).bytes;
      cache.delete(first);
    }
    cache.set(key, { body, bytes, expires: now() + cacheTtlMs });
    cacheBytes += bytes;
  }

  async function requestUpstream(itemName, canonicalName, cursor, key) {
    const url = new URL(UPSTREAM);
    url.searchParams.set('item_name', canonicalName);
    if (cursor) url.searchParams.set('cursor', cursor);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    let response;
    try {
      response = await fetchImpl(url, { method: 'GET', headers: { 'x-nxopen-api-key': apiKey, Accept: 'application/json' },
        signal: controller.signal, redirect: 'error' });
      if (response.status === 401 || response.status === 403) return failure(503, 'PROXY_UPSTREAM_AUTH');
      if (response.status === 429) return failure(429, 'PROXY_UPSTREAM_LIMIT', 60);
      if (response.status === 400) {
        let errorName;
        try {
          errorName = (await readLimitedJson(response, Math.min(maxResponseBytes, 16384)))?.error?.name;
        } catch {
          return failure(controller.signal.aborted ? 504 : 502, controller.signal.aborted ? 'PROXY_TIMEOUT' : 'PROXY_UPSTREAM_ERROR');
        }
        // Only documented item/query failures are recoverable; never forward the upstream error body.
        if (controller.signal.aborted) return failure(504, 'PROXY_TIMEOUT');
        if (errorName === 'OPENAPI00003' || errorName === 'OPENAPI00004') return failure(400, 'PROXY_ITEM_QUERY_REJECTED');
        if (errorName === 'OPENAPI00005') return failure(503, 'PROXY_UPSTREAM_AUTH');
        return failure(502, 'PROXY_UPSTREAM_ERROR');
      }
      if (!response.ok) return failure(502, 'PROXY_UPSTREAM_ERROR');
      let body;
      try {
        body = validatePage(await readLimitedJson(response, maxResponseBytes), canonicalName, itemName);
        if (body.next_cursor && body.next_cursor.includes(apiKey)) throw new Error('Invalid response');
      } catch {
        return failure(controller.signal.aborted ? 504 : 502, controller.signal.aborted ? 'PROXY_TIMEOUT' : 'PROXY_INVALID_RESPONSE');
      }
      body.fetched_at = new Date(now()).toISOString();
      cachePut(key, body);
      return { status: 200, headers: {}, body };
    } catch {
      return failure(controller.signal.aborted ? 504 : 502, controller.signal.aborted ? 'PROXY_TIMEOUT' : 'PROXY_UPSTREAM_ERROR');
    } finally {
      clearTimeout(timer);
      if (response?.body && !response.bodyUsed) await response.body.cancel().catch(() => {});
    }
  }

  return async function handle({ method = 'GET', url = '/', clientAddress = 'unknown', hasBody = false } = {}) {
    // No HTTP headers, key, cookie, host, or upstream URL from the caller are forwarded.
    const limited = clients.take(String(clientAddress).slice(0, 128), now());
    if (limited) return failure(429, 'PROXY_RATE_LIMIT', limited);
    if (method !== 'GET') return { ...failure(405, 'PROXY_METHOD_NOT_ALLOWED'), headers: { Allow: 'GET' } };
    if (typeof url !== 'string' || url.length > 8192 || !url.startsWith('/') || url.startsWith('//') || url.includes('#') ||
        /%(?![a-f\d]{2})/i.test(url) || hasBody) return failure(400, 'PROXY_INVALID_REQUEST');
    let parsed;
    try { parsed = new URL(url, 'http://proxy.invalid'); } catch { return failure(400, 'PROXY_INVALID_REQUEST'); }
    if (parsed.pathname === '/health' && !parsed.search) return { status: 200, headers: {}, body: { status: 'ok' } };
    if (parsed.pathname !== '/v1/auction/list') return failure(404, 'PROXY_NOT_FOUND');
    const query = parsed.searchParams;
    if ([...query.keys()].some(k => k !== 'item_name' && k !== 'cursor') || query.getAll('item_name').length !== 1 || query.getAll('cursor').length > 1) return failure(400, 'PROXY_INVALID_REQUEST');
    const itemName = query.get('item_name');
    const cursor = query.get('cursor') || '';
    if (!itemName || itemName.length > 100 || cursor.length > 2048 || CONTROL.test(cursor)) return failure(400, 'PROXY_INVALID_REQUEST');
    if (!configured) return failure(503, 'PROXY_NOT_CONFIGURED');
    const canonicalName = ITEM_ALIASES.get(itemName) || itemName;
    if (!names.has(canonicalName)) return failure(400, 'PROXY_ITEM_NOT_ALLOWED');
    const key = JSON.stringify([itemName, cursor]);
    const cached = cache.get(key);
    if (cached && now() < cached.expires) return { status: 200, headers: {}, body: cached.body };
    if (cached) { cache.delete(key); cacheBytes -= cached.bytes; }
    if (running.has(key)) return running.get(key);
    if (running.size >= maxConcurrent) return failure(503, 'PROXY_BUSY', 2);
    const upstreamWait = upstreamLimit.take('all', now());
    if (upstreamWait) return failure(429, 'PROXY_RATE_LIMIT', upstreamWait);
    let reservation;
    try { reservation = budget.reserve(); } catch { return failure(503, 'PROXY_QUOTA_UNAVAILABLE'); }
    if (!reservation?.allowed) return failure(429, 'PROXY_QUOTA_EXCEEDED', reservation?.retryAfter || 86400);
    const pending = requestUpstream(itemName, canonicalName, cursor, key);
    running.set(key, pending);
    try { return await pending; } finally { running.delete(key); }
  };
}
