// Verified against the official API's scroll category: 인챈트 종류 / 접두|접미 /
// option_value such as "템포 (랭크 6)". Equipment options never enter this path.
export const ENCHANT_SCROLL_NAMES = new Set([
  '인챈트 스크롤', '전용 인챈트 스크롤', '개방된 전용 인챈트 스크롤',
  '인챈트 스크롤(판매 불가)', '8주년 전용 인챈트 스크롤', '시양양 한정 인챈트 스크롤',
]);
export const isUnnamedEnchantScroll = name => ENCHANT_SCROLL_NAMES.has(name);

export function namedEnchantScroll(item) {
  if (item.auction_item_category !== '인챈트 스크롤' || !isUnnamedEnchantScroll(item.item_name) ||
      !Array.isArray(item.item_option) || item.item_option.length > 64) return null;
  const names = new Set();
  for (const option of item.item_option) {
    if (option?.option_type !== '인챈트 종류') continue;
    if (!['접두', '접미'].includes(option.option_sub_type) || typeof option.option_value !== 'string' ||
        option.option_value.length > 180 || /[\x00-\x1f\x7f]/.test(option.option_value)) return null;
    const match = /^(.+?)\s+\(랭크\s+([1-9a-fA-F])\)$/.exec(option.option_value.trim());
    if (!match || !match[1].trim() || match[1].includes('(랭크')) return null;
    const name = match[1].trim().replace(/\s+/g, ' ');
    const identity = `${name} (${option.option_sub_type} / 랭크 ${match[2].toUpperCase()}) · ${item.item_name}`;
    if (identity.length > 200) return null;
    names.add(identity);
  }
  // Conflicting option entries must never choose one arbitrary enchant's price.
  return names.size === 1 ? [...names][0] : null;
}
