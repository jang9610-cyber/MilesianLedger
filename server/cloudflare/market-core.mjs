// Official auction schema: https://openapi.nexon.com/static/api/mabinogi/36_ko_script20250410023004.yaml
import { namedEnchantScroll } from './market-enchant.mjs';
export const HISTORY_INTERVAL = 20 * 60_000;
export const LIST_INTERVAL = 60 * 60_000;
export const RETENTION = 8 * 24 * 60 * 60_000;
export const COMPARABLE = new Set(['허브', '천옷/방직', '제련/블랙스미스', '힐웬 공학', '매직 크래프트', '포션', '음식', '기타 재료', '개조석', '퍼퓸']);
export function json(value, status = 200, headers = {}) {
  return new Response(JSON.stringify(value), { status, headers: {
    'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff', ...headers,
  } });
}
export function marketError(status, code) { return json({ error: { code } }, status); }
const string = (v, max) => typeof v === 'string' && v.length > 0 && v.length <= max && !/[\x00-\x1f\x7f]/.test(v);
export function validateMarketPage(value, kind, now = Date.now()) {
  const field = kind === 'history' ? 'auction_history' : 'auction_item';
  if (!['history', 'list'].includes(kind) || !value || !Array.isArray(value[field]) || value[field].length > 500) throw Error('MARKET_INVALID_PAGE');
  const cursor = value.next_cursor ?? null;
  if (cursor !== null && (!string(cursor, 2048) || /\s/.test(cursor))) throw Error('MARKET_INVALID_CURSOR');
  const rows = value[field].map(item => {
    if (!item || !string(item.item_name, 200) || !string(item.auction_item_category, 100) ||
        !Number.isSafeInteger(item.item_count) || item.item_count < 1 || item.item_count > 1e9 ||
        !Number.isSafeInteger(item.auction_price_per_unit) || item.auction_price_per_unit < 0 ||
        !Number.isSafeInteger(item.item_count * item.auction_price_per_unit)) throw Error('MARKET_INVALID_ITEM');
    if (!Array.isArray(item.item_option ?? [])) throw Error('MARKET_INVALID_OPTIONS');
    // Only identified scrolls get an option-derived name; equipment remains name-level.
    const scrollName = namedEnchantScroll(item);
    const comparable = !!scrollName || COMPARABLE.has(item.auction_item_category) && !(item.item_option?.length);
    const row = { name: scrollName || item.item_name, category: item.auction_item_category,
      quantity: item.item_count, price: item.auction_price_per_unit, comparable: comparable ? 1 : 0 };
    if (kind === 'history') {
      const time = Date.parse(item.date_auction_buy);
      if (!string(item.auction_buy_id, 256) || !Number.isFinite(time) || time < now - RETENTION || time > now + 5 * 60_000) throw Error('MARKET_INVALID_TRADE');
      return { ...row, id: item.auction_buy_id, time };
    }
    return row;
  });
  return { rows, next_cursor: cursor };
}
export function parseMarketQuery(url) {
  const q = url.searchParams;
  if ([...q.keys()].some(k => !['window', 'sort', 'category', 'q', 'offset', 'limit'].includes(k)) ||
      [...q.keys()].some(k => q.getAll(k).length !== 1)) throw Error('MARKET_INVALID_QUERY');
  const window = q.get('window') || '24h';
  const sort = q.get('sort') || 'quantity';
  const category = q.get('category') || '';
  const search = q.get('q') || '';
  const limit = q.get('limit') || '50', offset = q.get('offset') || '0';
  if (!['24h', '7d'].includes(window) || !['quantity', 'trades', 'gold', 'supply'].includes(sort) ||
      category.length > 100 || search.length > 200 || !/^\d+$/.test(limit) || !/^\d+$/.test(offset) ||
      +limit < 1 || +limit > 100 || +offset > 10000) throw Error('MARKET_INVALID_QUERY');
  return { window, sort, category, search, limit: +limit, offset: +offset };
}
