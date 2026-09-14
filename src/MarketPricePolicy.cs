using System;
using System.Collections.Generic;
using System.Text;

namespace MabinogiBarter
{
    // Keep this exact category policy aligned with server/cloudflare/market-price-policy.mjs.
    // A nonempty category is comparable unless it is an equipment/totem/magigraph
    // category whose variable options make an aggregate item-name average misleading.
    public static class MarketPricePolicy
    {
        static readonly HashSet<string> VariableCategories = new HashSet<string>(StringComparer.Ordinal) {
            "근거리장비", "한손장비", "양손장비", "검", "도끼", "둔기", "랜스", "핸들", "너클", "체인블레이드",
            "원거리장비", "활", "석궁", "듀얼건", "수리검", "아틀라틀",
            "마법장비", "실린더", "스태프", "원드", "마도서", "힐링원드",
            "점성술장비", "대형낫", "오브", "갑옷장비", "중갑옷", "경갑옷", "천옷",
            "방어장비", "장갑", "신발", "모자/가발", "방패", "로브",
            "액세서리", "얼굴장식", "날개", "꼬리",
            "특수장비", "악기", "생활도구", "마리오네트", "에코스톤", "에이도스", "유물", "기타장비",
            "토템", "애뮬릿", "펫토템", "마기그래프", "마기그래프도안"
        };
        static readonly HashSet<string> UnidentifiedEnchantNames = new HashSet<string>(StringComparer.Ordinal) {
            "인챈트 스크롤", "전용 인챈트 스크롤", "개방된 전용 인챈트 스크롤",
            "인챈트 스크롤(판매 불가)", "8주년 전용 인챈트 스크롤", "시양양 한정 인챈트 스크롤"
        };

        static string NormalizeCategory(string category)
        {
            if (String.IsNullOrEmpty(category)) return "";
            var result = new StringBuilder(category.Length);
            foreach (char letter in category) if (!Char.IsWhiteSpace(letter)) result.Append(letter);
            return result.ToString();
        }

        public static bool IsVariableOptionCategory(string category)
        {
            return VariableCategories.Contains(NormalizeCategory(category));
        }

        public static bool IsUnidentifiedEnchant(string name)
        {
            return name != null && UnidentifiedEnchantNames.Contains(name);
        }

        public static bool IsComparable(string name, string category)
        {
            return !String.IsNullOrWhiteSpace(name) && !String.IsNullOrWhiteSpace(category)
                && !IsVariableOptionCategory(category) && !IsUnidentifiedEnchant(name);
        }

        public static string ExclusionReason(string name, string category)
        {
            if (IsUnidentifiedEnchant(name)) return "인챈트 이름을 확인하지 못해 서로 다른 인챈트의 평균을 표시하지 않습니다.";
            if (IsVariableOptionCategory(category)) return "유동 옵션에 따라 가격 차이가 큰 품목으로 평균 단가를 표시하지 않습니다.";
            if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(category)) return "품목 또는 분류를 확인하지 못해 평균 단가를 표시하지 않습니다.";
            return "";
        }

        public static void Apply(MarketSnapshotData data)
        {
            if (data == null) return;
            // Legacy generic scroll rows mix several enchant identities. Even their
            // minimum is not the price of one identified enchant, unlike equipment.
            if (data.Quotes != null) foreach (var pair in data.Quotes) {
                if (pair.Value != null && (IsUnidentifiedEnchant(pair.Key) || IsUnidentifiedEnchant(pair.Value.Name)))
                    pair.Value.UnitPrice = null;
            }
            Apply(data.Items24h, data);
            Apply(data.Items7d, data);
        }

        static void Apply(IEnumerable<MarketSnapshotItem> items, MarketSnapshotData data)
        {
            if (items == null) return;
            foreach (var item in items) {
                if (item == null) continue;
                bool wasComparable = item.PriceComparable;
                item.PriceComparable = IsComparable(item.Name, item.Category);
                if (!item.PriceComparable) {
                    item.AverageSalePrice = null;
                    if (IsUnidentifiedEnchant(item.Name)) item.LowestListingPrice = null;
                    continue;
                }
                // The old server allowlist hid averages outside a handful of material
                // categories. Reconstruct only from observed counts and their gold sum;
                // never treat missing/overflowed totals or no sales as a zero price.
                if (!wasComparable || !item.AverageSalePrice.HasValue) {
                    item.AverageSalePrice = item.SoldQuantity > 0 && item.TradeCount > 0 && item.TradedGold >= 0
                        ? item.TradedGold.Value / item.SoldQuantity.Value : (decimal?)null;
                }
                // The same old snapshots retained these prices in their quote table.
                // Use only an observed, matching, current quote; keep empty supply unknown.
                if (!(item.LowestListingPrice > 0) && item.ListedQuantity > 0 && item.ListingCount > 0)
                    item.LowestListingPrice = MarketPrices.LowestFor(item, data);
            }
        }
    }
}
