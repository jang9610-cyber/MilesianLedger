import { marketFetch, marketScheduled, parseInternalMarket, validateMarketPage, projectEnchantSample } from './market-worker.mjs';
export { MarketCollector } from './market-worker.mjs';
// Milesian Ledger auction proxy for Cloudflare Workers + one Durable Object.
// Deploy the module bundle with Wrangler; market modules are included.
// Bind AUCTION_COORDINATOR to the exported AuctionCoordinator Durable Object.
// Keep AUCTION_ENABLED="false" until the real NEXON_API_KEY secret is configured.

const UPSTREAM_URL = 'https://open.api.nexon.com/mabinogi/v1/auction/list';
const COORDINATOR_NAME = 'global-v1';
const BUDGET_KEY = 'upstream-budget-v1';
const DAY_MS = 24 * 60 * 60 * 1000;
const CACHE_TTL_MS = 60 * 1000;
const MAX_CACHE_ENTRIES = 256;
const MAX_CACHE_BYTES = 8 * 1024 * 1024;
const MAX_RESPONSE_BYTES = 1024 * 1024;
const MAX_ERROR_RESPONSE_BYTES = 16 * 1024;
const MAX_QUEUE_LENGTH = 32;
const REQUEST_TIMEOUT_MS = 12000;
const MAX_QUEUE_WAIT_MS = 8000;
const INVALID_CURSOR = /[\x00-\x20\x7f]/;
const encoder = new TextEncoder();

// Public exact search names, generated from server/item-names.json (117 items).
// NPC-only purchases are intentionally absent. Keep this in sync with the app catalog.
const ITEM_NAMES = new Set([
    "가는 실뭉치",
    "거미줄",
    "건초 더미",
    "고급 가죽",
    "고급 가죽끈",
    "고급 나무장작",
    "고급 바닐라 향초",
    "고급 실크",
    "고급 양털",
    "고급 옷감",
    "고대 정령의 화석 조각",
    "골드 허브",
    "굵은 실뭉치",
    "금광석",
    "금광석 조각",
    "금괴",
    "금판",
    "꽃뭉치",
    "끈끈이 풀",
    "나무장작",
    "나무판",
    "돌연변이 식물의 점액질",
    "돌연변이 토끼의 발",
    "동광석",
    "동광석 조각",
    "동괴",
    "동판",
    "마나 500 포션",
    "마나 허브",
    "마력이 깃든 나무장작",
    "마리오네트 500 포션",
    "마법가루",
    "마법의 깃털펜",
    "마법의 양피지",
    "매듭끈",
    "못쓰게 된 밀 이파리",
    "무른 힐웬 광석 조각",
    "물이 든 병",
    "뮤턴트",
    "미니 바닐라 향초",
    "미스릴 대못",
    "미스릴광석",
    "미스릴광석 조각",
    "미스릴괴",
    "미스릴판",
    "밀",
    "밀가루",
    "밀랍",
    "발리스타용 독 묻은 와이번 볼트",
    "베이스 허브",
    "보리",
    "보릿가루",
    "부드러운 양피지",
    "블러디 허브",
    "빈 병",
    "빤짝이 종이",
    "사스콰치의 심장",
    "새우 조련 미끼",
    "생기 있는 깃털",
    "생명력 500 포션",
    "선라이트 허브",
    "순도 높은 강화제",
    "스태미나 500 포션",
    "스핀 기어",
    "신비한 허브 가루",
    "실리엔",
    "실리엔 결정",
    "싱싱한 풀",
    "아라트의 결정",
    "양털",
    "어둠이 깃든 칼날 조각",
    "에너지 증폭 장치",
    "에너지 컨버터",
    "에메랄드 코어",
    "에메랄드 퓨즈",
    "엘레멘탈 리무버",
    "와이번의 발톱",
    "육각 너트",
    "육각 볼트",
    "은광석",
    "은광석 조각",
    "은괴",
    "은판",
    "인조 잔디",
    "일반 가죽",
    "작은 녹색구슬",
    "작은 빨간구슬",
    "작은 은색구슬",
    "작은 파란구슬",
    "저가형 가죽",
    "정령의 리큐르",
    "정제된 촉매제",
    "정화된 토끼의 발",
    "조화의 코스모스 퍼퓸",
    "종이",
    "중급 나무장작",
    "질긴 끈",
    "질긴 실",
    "최고급 가죽",
    "최고급 가죽끈",
    "최고급 나무장작",
    "최고급 바닐라 향초",
    "최고급 실크",
    "최고급 옷감",
    "축복의 포션",
    "코스모스 추출액",
    "쿠션용 솜",
    "특급 나무장작",
    "튼튼한 고리",
    "펫 놀이세트",
    "펫이 좋아하는 잡동사니",
    "포이즌 포션",
    "포이즌 허브",
    "화이트 허브",
    "힐웬",
    "힐웬 광석 조각",
    "힐웬 합금"
]);

// Retain beta.1 request names while searching the exact names used by Nexon.
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
  PROXY_QUOTA_EXCEEDED: '서버의 경매장 조회 예산을 모두 사용했습니다.',
  PROXY_QUOTA_UNAVAILABLE: '서버의 조회 예산을 확인할 수 없습니다.',
  PROXY_BUSY: '서버가 혼잡합니다. 잠시 후 다시 갱신해 주세요.',
  PROXY_UPSTREAM_AUTH: '서버의 경매장 연결 인증을 확인해야 합니다.',
  PROXY_UPSTREAM_LIMIT: '공식 경매장 서비스의 요청 한도에 도달했습니다.',
  PROXY_UPSTREAM_ERROR: '공식 경매장 서비스에 연결하지 못했습니다.',
  PROXY_TIMEOUT: '경매장 응답 시간이 초과되었습니다.',
  PROXY_INVALID_RESPONSE: '경매장 응답을 확인하지 못했습니다.',
};

function json(body, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff', ...extraHeaders },
  });
}

function error(status, code, retryAfter) {
  return json({ error: { code, message: MESSAGES[code] } }, status,
    retryAfter ? { 'Retry-After': String(Math.max(1, Math.ceil(retryAfter))) } : {});
}

function validateRequest(request) {
  if (request.method !== 'GET') return { response: json({ error: {
    code: 'PROXY_METHOD_NOT_ALLOWED', message: MESSAGES.PROXY_METHOD_NOT_ALLOWED,
  } }, 405, { Allow: 'GET' }) };
  if (request.url.length > 8192 || /%(?![a-f\d]{2})/i.test(request.url) || request.url.includes('#') ||
      request.body || request.headers.has('transfer-encoding') ||
      (request.headers.has('content-length') && request.headers.get('content-length') !== '0')) {
    return { response: error(400, 'PROXY_INVALID_REQUEST') };
  }
  let url;
  try { url = new URL(request.url); } catch { return { response: error(400, 'PROXY_INVALID_REQUEST') }; }
  if (url.pathname === '/health' && !url.search) return { response: json({ status: 'ok' }) };
  if (url.pathname !== '/v1/auction/list' && url.pathname !== '/auction') return { response: error(404, 'PROXY_NOT_FOUND') };
  const query = url.searchParams;
  if ([...query.keys()].some(key => key !== 'item_name' && key !== 'cursor') ||
      query.getAll('item_name').length !== 1 || query.getAll('cursor').length > 1) {
    return { response: error(400, 'PROXY_INVALID_REQUEST') };
  }
  const itemName = query.get('item_name');
  const cursor = query.get('cursor') || '';
  if (!itemName || itemName.length > 100 || cursor.length > 2048 || INVALID_CURSOR.test(cursor)) {
    return { response: error(400, 'PROXY_INVALID_REQUEST') };
  }
  const canonicalName = ITEM_ALIASES.get(itemName) || itemName;
  if (!ITEM_NAMES.has(canonicalName)) return { response: error(400, 'PROXY_ITEM_NOT_ALLOWED') };
  return { itemName, canonicalName, cursor };
}

function configuration(env) {
  if (!env || env.AUCTION_ENABLED !== 'true' || typeof env.NEXON_API_KEY !== 'string' ||
      !env.NEXON_API_KEY || env.NEXON_API_KEY.length > 2048 || INVALID_CURSOR.test(env.NEXON_API_KEY)) return null;
  const integer = (value, fallback, min, max) => {
    if (value === undefined || value === '') return fallback;
    if (typeof value !== 'string' || !/^\d+$/.test(value)) return null;
    const number = Number(value);
    return Number.isSafeInteger(number) && number >= min && number <= max ? number : null;
  };
  const budget = integer(env.UPSTREAM_REQUESTS_PER_24H, 500, 1, 10000);
  const rate = integer(env.UPSTREAM_REQUESTS_PER_SECOND, 5, 1, 5);
  if (budget === null || rate === null) return null;
  return { apiKey: env.NEXON_API_KEY, budget, spacingMs: Math.ceil(1000 / rate) };
}

// The public Worker accepts no client key, shared client secret, or upstream URL.
export default {
  scheduled: marketScheduled,
  async fetch(request, env, context) {
    try {
      const market = await marketFetch(request, env, context);
      if (market) return market;
      const parsed = validateRequest(request);
      if (parsed.response) return parsed.response;
      if (env.SHARED_MARKET_QUOTES_ENABLED === 'true') {
        if (!env.MARKET_COLLECTOR || env.MARKET_ENABLED !== 'true') return error(503, 'PROXY_NOT_CONFIGURED');
        const query = new URLSearchParams({ item_name: parsed.itemName, canonical_name: parsed.canonicalName });
        if (parsed.cursor) query.set('cursor', parsed.cursor);
        return env.MARKET_COLLECTOR.get(env.MARKET_COLLECTOR.idFromName('market-v1')).fetch(new Request('https://market.internal/quote?' + query));
      }
      if (!configuration(env) || !env.AUCTION_COORDINATOR ||
          typeof env.AUCTION_COORDINATOR.idFromName !== 'function' || typeof env.AUCTION_COORDINATOR.get !== 'function') {
        return error(503, 'PROXY_NOT_CONFIGURED');
      }
      if (request.signal.aborted) return error(503, 'PROXY_BUSY');
      const query = new URLSearchParams({ item_name: parsed.itemName });
      if (parsed.cursor) query.set('cursor', parsed.cursor);
      const stub = env.AUCTION_COORDINATOR.get(env.AUCTION_COORDINATOR.idFromName(COORDINATOR_NAME));
      // A clean internal request carries no caller Authorization, cookies, or other headers.
      return await stub.fetch(new Request('https://coordinator.internal/v1/auction/list?' + query, {
        method: 'GET', signal: request.signal,
      }));
    } catch {
      return error(503, 'PROXY_BUSY');
    }
  },
};

function abortableDelay(milliseconds, signal) {
  if (signal.aborted) return Promise.resolve(false);
  return new Promise(resolve => {
    let timer;
    const finish = allowed => {
      clearTimeout(timer);
      signal.removeEventListener('abort', abort);
      resolve(allowed);
    };
    const abort = () => finish(false);
    signal.addEventListener('abort', abort, { once: true });
    timer = setTimeout(() => finish(true), milliseconds);
  });
}

async function limitedJson(response, limit = MAX_RESPONSE_BYTES) {
  const length = response.headers.get('content-length');
  if (length && (!/^\d+$/.test(length) || Number(length) > limit)) throw new Error('Invalid response');
  const reader = response.body?.getReader();
  if (!reader) throw new Error('Missing response');
  const chunks = [];
  let bytes = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      bytes += value.byteLength;
      if (bytes > limit) throw new Error('Response too large');
      chunks.push(value);
    }
  } catch (cause) {
    await reader.cancel().catch(() => {});
    throw cause;
  } finally { reader.releaseLock(); }
  const all = new Uint8Array(bytes);
  let offset = 0;
  for (const chunk of chunks) { all.set(chunk, offset); offset += chunk.byteLength; }
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(all));
}

function projectPage(value, itemName, apiKey, responseName = itemName) {
  if (!value || !Array.isArray(value.auction_item) || value.auction_item.length > 1000 ||
      (value.next_cursor != null && (typeof value.next_cursor !== 'string' || value.next_cursor.length > 2048 ||
        INVALID_CURSOR.test(value.next_cursor) || value.next_cursor.includes(apiKey)))) throw new Error('Invalid response');
  const items = [];
  for (const item of value.auction_item) {
    if (!item || typeof item.item_name !== 'string' || !item.item_name || item.item_name.length > 100) throw new Error('Invalid response');
    if (item.item_name !== itemName) continue;
    if (typeof item.auction_price_per_unit !== 'number' || !Number.isFinite(item.auction_price_per_unit) ||
        item.auction_price_per_unit < 0 || item.auction_price_per_unit > 1e15 ||
        !Number.isSafeInteger(item.item_count) || item.item_count < 1 || item.item_count > 1e9) throw new Error('Invalid response');
    items.push({ item_name: responseName, auction_price_per_unit: item.auction_price_per_unit, item_count: item.item_count });
  }
  return { auction_item: items, next_cursor: value.next_cursor || null, fetched_at: new Date(Date.now()).toISOString() };
}

// All users and Worker locations call this one named object. FIFO serialization keeps
// the storage reservation, API request, and successful cache publication in one order.
// Barter stays button-driven; the private market collector shares this queue and budget.
export class AuctionCoordinator {
  constructor(state, env) {
    this.state = state;
    this.env = env;
    this.queue = Promise.resolve();
    this.pending = 0;
    this.cache = new Map();
    this.cacheBytes = 0;
  }

  async fetch(request) {
    try {
      const url = new URL(request.url);
      if (url.pathname === '/internal/usage' && request.method === 'GET' && !url.search) {
        const budget = await this.state.storage.get(BUDGET_KEY);
        const timestamps = Array.isArray(budget?.timestamps) ? budget.timestamps.filter(t => t > Date.now() - DAY_MS) : [];
        return json({ requests_24h: timestamps.length, configured_limit: Number(this.env.UPSTREAM_REQUESTS_PER_24H || 500),
          oldest_request_at: timestamps.length ? new Date(timestamps[0]).toISOString() : null });
      }
      const parsed = parseInternalMarket(request, this.env) || validateRequest(request);
      if (parsed.response) return parsed.response;
      const config = configuration(this.env);
      if (!config) return error(503, 'PROXY_NOT_CONFIGURED');
      if (request.signal.aborted) return error(503, 'PROXY_BUSY');
      if (this.pending >= MAX_QUEUE_LENGTH) return error(503, 'PROXY_BUSY', 2);
      this.pending++;
      const deadline = Date.now() + MAX_QUEUE_WAIT_MS;
      const operation = this.queue.then(() => this.handle(parsed, request.signal, config, deadline));
      // A failed request cannot break later queued work. Return only sanitized failures.
      this.queue = operation.catch(() => {});
      try { return await operation; }
      finally { this.pending--; }
    } catch {
      return error(503, 'PROXY_BUSY');
    }
  }

  cacheGet(key) {
    const entry = this.cache.get(key);
    if (!entry) return null;
    const now = Date.now();
    if (now >= entry.expires || now < entry.created) {
      this.cache.delete(key);
      this.cacheBytes -= entry.bytes;
      return null;
    }
    // Store immutable serialized JSON, then create a separate response per caller.
    return new Response(entry.text, { headers: { 'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' } });
  }

  cachePut(key, body) {
    const text = JSON.stringify(body);
    const bytes = encoder.encode(text).byteLength;
    if (bytes > MAX_CACHE_BYTES) return;
    const now = Date.now();
    for (const [id, entry] of this.cache) {
      if (now >= entry.expires || id === key) { this.cache.delete(id); this.cacheBytes -= entry.bytes; }
    }
    while (this.cache.size && (this.cache.size >= MAX_CACHE_ENTRIES || this.cacheBytes + bytes > MAX_CACHE_BYTES)) {
      const first = this.cache.keys().next().value;
      this.cacheBytes -= this.cache.get(first).bytes;
      this.cache.delete(first);
    }
    this.cache.set(key, { text, bytes, created: now, expires: now + CACHE_TTL_MS });
    this.cacheBytes += bytes;
  }

  async reserve(signal, config, deadline) {
    try {
      const stored = await this.state.storage.get(BUDGET_KEY);
      if (signal.aborted || Date.now() >= deadline) return { response: error(503, 'PROXY_BUSY') };
      let timestamps = [];
      if (stored !== undefined) {
        if (!stored || stored.version !== 1 || !Array.isArray(stored.timestamps) || stored.timestamps.length > 10000 ||
            stored.timestamps.some((time, index) => !Number.isSafeInteger(time) || time < 0 ||
              (index > 0 && time < stored.timestamps[index - 1]))) return { response: error(503, 'PROXY_QUOTA_UNAVAILABLE') };
        timestamps = stored.timestamps;
      }
      let now = Date.now();
      const latest = timestamps.length ? timestamps[timestamps.length - 1] : null;
      if (latest !== null && latest > now) return { response: error(503, 'PROXY_QUOTA_UNAVAILABLE') };
      timestamps = timestamps.filter(time => time > now - DAY_MS);
      // Background scans leave 20% of the configured budget available to barter requests.
      const effectiveBudget = config.background ? Math.floor(config.budget * 0.8) : config.budget;
      if (timestamps.length >= effectiveBudget) {
        return { response: error(429, 'PROXY_QUOTA_EXCEEDED', ((timestamps[0] ?? now) + DAY_MS - now) / 1000) };
      }
      // The previous reservation is durable, so object recreation cannot reset spacing.
      const wait = latest === null ? 0 : latest + config.spacingMs - now;
      if (wait > 0 && !await abortableDelay(wait, signal)) return { response: error(503, 'PROXY_BUSY') };
      if (signal.aborted || Date.now() >= deadline) return { response: error(503, 'PROXY_BUSY') };
      now = Date.now();
      if (latest !== null && now < latest + config.spacingMs) return { response: error(503, 'PROXY_QUOTA_UNAVAILABLE') };
      timestamps = timestamps.filter(time => time > now - DAY_MS);
      timestamps.push(now);
      // Do not start fetch unless the shared quota reservation has been persisted.
      await this.state.storage.put(BUDGET_KEY, { version: 1, timestamps });
      return { allowed: true };
    } catch {
      return { response: error(503, 'PROXY_QUOTA_UNAVAILABLE') };
    }
  }

  async handle(parsed, signal, config, deadline) {
    if (signal.aborted || Date.now() >= deadline) return error(503, 'PROXY_BUSY');
    const key = JSON.stringify([parsed.itemName, parsed.cursor]);
    // Recheck at the head of the queue: simultaneous identical requests reuse success.
    const cached = parsed.market ? null : this.cacheGet(key);
    if (cached) return cached;
    const reservation = await this.reserve(signal, { ...config, background: Boolean(parsed.market) }, deadline);
    if (reservation.response) return reservation.response;
    if (signal.aborted || Date.now() >= deadline) return error(503, 'PROXY_BUSY');
    const url = new URL(parsed.market ? 'https://open.api.nexon.com/mabinogi/v1/auction/' + parsed.kind : UPSTREAM_URL);
    if (!parsed.market) url.searchParams.set('item_name', parsed.canonicalName);
    if (parsed.sample === 'enchant-scroll') url.searchParams.set('auction_item_category', '인챈트 스크롤');
    if (parsed.cursor) url.searchParams.set('cursor', parsed.cursor);
    const controller = new AbortController();
    let timedOut = false;
    const abort = () => controller.abort();
    signal.addEventListener('abort', abort, { once: true });
    const timer = setTimeout(() => { timedOut = true; controller.abort(); }, REQUEST_TIMEOUT_MS);
    let response;
    try {
      response = await fetch(url.toString(), {
        method: 'GET', headers: { 'x-nxopen-api-key': config.apiKey, Accept: 'application/json' },
        redirect: 'manual', signal: controller.signal,
      });
      if (signal.aborted) return error(503, 'PROXY_BUSY');
      if (response.status === 401 || response.status === 403) return error(503, 'PROXY_UPSTREAM_AUTH');
      if (response.status === 429) return error(429, 'PROXY_UPSTREAM_LIMIT', 60);
      // This also rejects all redirects without sending the key to their destination.
      if (!response.ok) {
        if (response.status === 400) {
          try {
            const problem = await limitedJson(response, MAX_ERROR_RESPONSE_BYTES);
            // Nexon uses HTTP 400 for both query and service errors. Only known codes
            // are classified; raw messages and extra fields always remain private.
            if (signal.aborted) return error(503, 'PROXY_BUSY');
            if (timedOut) return error(504, 'PROXY_TIMEOUT');
            const name = problem?.error?.name;
            if (name === 'OPENAPI00005') return error(503, 'PROXY_UPSTREAM_AUTH');
            if (name === 'OPENAPI00003' || name === 'OPENAPI00004') return error(400, 'PROXY_ITEM_QUERY_REJECTED');
          } catch {
            if (signal.aborted) return error(503, 'PROXY_BUSY');
            if (timedOut) return error(504, 'PROXY_TIMEOUT');
          }
        }
        return error(502, 'PROXY_UPSTREAM_ERROR');
      }
      let body;
      try {
        body = parsed.market
          ? parsed.sample === 'enchant-scroll'
            ? projectEnchantSample(await limitedJson(response, 8 * 1024 * 1024))
            : validateMarketPage(await limitedJson(response, 8 * 1024 * 1024), parsed.kind)
          : projectPage(await limitedJson(response), parsed.canonicalName, config.apiKey, parsed.itemName);
        if (parsed.market && JSON.stringify(body).includes(config.apiKey)) throw Error('Invalid response');
      } catch {
        if (signal.aborted) return error(503, 'PROXY_BUSY');
        return error(timedOut ? 504 : 502, timedOut ? 'PROXY_TIMEOUT' : 'PROXY_INVALID_RESPONSE');
      }
      if (signal.aborted) return error(503, 'PROXY_BUSY');
      if (timedOut) return error(504, 'PROXY_TIMEOUT');
      if (!parsed.market) this.cachePut(key, body);
      return json(body);
    } catch {
      if (signal.aborted) return error(503, 'PROXY_BUSY');
      return error(timedOut ? 504 : 502, timedOut ? 'PROXY_TIMEOUT' : 'PROXY_UPSTREAM_ERROR');
    } finally {
      clearTimeout(timer);
      signal.removeEventListener('abort', abort);
      if (response?.body && !response.bodyUsed) await response.body.cancel().catch(() => {});
    }
  }
}
