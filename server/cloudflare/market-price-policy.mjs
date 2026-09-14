import { isUnnamedEnchantScroll } from './market-enchant.mjs';

export const PRICE_COMPARISON_POLICY_VERSION = 'category-prices-v2';
const normalizeCategory = value => typeof value === 'string' ? value.replace(/\s/g, '') : '';

// Only categories with equipment-like variable options suppress name-level averages.
// Keep this exact taxonomy aligned with the desktop MarketPricePolicy. Ammunition
// and 기타 재료 (under 마기그래프 용품) intentionally remain comparable.
export const EXCLUDED_PRICE_CATEGORIES = new Set([
  '근거리 장비', '한손 장비', '양손 장비', '검', '도끼', '둔기', '랜스', '핸들', '너클', '체인 블레이드',
  '원거리 장비', '활', '석궁', '듀얼건', '수리검', '아틀라틀',
  '마법 장비', '실린더', '스태프', '원드', '마도서', '힐링 원드',
  '점성술 장비', '대형 낫', '오브',
  '갑옷 장비', '중갑옷', '경갑옷', '천옷',
  '방어 장비', '장갑', '신발', '모자/가발', '방패', '로브',
  '액세서리', '얼굴 장식', '날개', '꼬리',
  '특수 장비', '악기', '생활 도구', '마리오네트', '에코스톤', '에이도스', '유물', '기타 장비',
  '토템', '애뮬릿', '펫 토템', '마기그래프', '마기그래프 도안',
].map(normalizeCategory));

export function isPriceComparable(name, category) {
  const normalized = normalizeCategory(category);
  return !!normalized && !isUnnamedEnchantScroll(name) && !EXCLUDED_PRICE_CATEGORIES.has(normalized);
}
