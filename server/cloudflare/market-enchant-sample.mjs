// Admin-only schema diagnostic. Keep the first page bounded and omit all private fields and cursors.
const text = (value, maximum) => typeof value === 'string' ? value.slice(0, maximum) : null;
export function projectEnchantSample(value) {
  if (!value || !Array.isArray(value.auction_item) || value.auction_item.length > 500) throw Error('MARKET_INVALID_PAGE');
  const auction_item = value.auction_item.slice(0, 12).map(item => {
    if (!item || typeof item.item_name !== 'string' || typeof item.auction_item_category !== 'string' ||
        !Number.isSafeInteger(item.item_count) || item.item_count < 1 || item.item_count > 1e9 ||
        !Number.isSafeInteger(item.auction_price_per_unit) || item.auction_price_per_unit < 0 ||
        !Array.isArray(item.item_option ?? [])) throw Error('MARKET_INVALID_ITEM');
    return {
      item_name: text(item.item_name, 200), auction_item_category: text(item.auction_item_category, 100),
      item_count: item.item_count, auction_price_per_unit: item.auction_price_per_unit,
      item_option: (item.item_option ?? []).slice(0, 32).map(option => ({
        option_type: text(option?.option_type, 128), option_sub_type: text(option?.option_sub_type, 128),
        option_value: text(option?.option_value, 2048), option_value2: text(option?.option_value2, 2048),
      })),
    };
  });
  return { sample: 'enchant-scroll', auction_item };
}
