using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MabinogiBarter
{
    // Display families, based on acquisition/recipe evidence in item-acquisition.json.
    // These are not auction categories. Shared ingredients have one stable family,
    // independent of the chosen recipe, readiness, price or current parent item.
    public static class ItemCategories
    {
        static readonly string[] labels = {
            "방직·천·가죽", "목공·장작", "제련·금속", "실리엔·매직 크래프트",
            "힐웬 공학", "허브", "포션·조제", "핸디크래프트", "필기구",
            "펫·핀즈 크래프트", "요리·식재료", "기타 재료"
        };
        static readonly Dictionary<string, int> families = BuildFamilies();
        static readonly StringComparer koreanNames = StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), false);

        static Dictionary<string, int> BuildFamilies()
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            Add(result, 0, "거미줄|양털|고급 양털|가는 실뭉치|굵은 실뭉치|매듭끈|질긴 실|질긴 끈|고급 실크|최고급 실크|고급 옷감|최고급 옷감|저가형 가죽|일반 가죽|고급 가죽|최고급 가죽|고급 가죽끈|최고급 가죽끈|쿠션용 솜|튼튼한 고리");
            // Enchanted firewood stays with logs, even though Magic Craft makes it.
            Add(result, 1, "나무장작|중급 나무장작|고급 나무장작|최고급 나무장작|특급 나무장작|마력이 깃든 나무장작|나무판|순도 높은 강화제");
            Add(result, 2, "철광석|철광석 조각|철괴|철봉|동광석|동광석 조각|동괴|동판|은광석|은광석 조각|은괴|은판|금광석|금광석 조각|금괴|금판|미스릴 광석|미스릴 광석 조각|미스릴괴|미스릴판|미스릴 대못|대못");
            Add(result, 3, "실리엔 결정|실리엔|신비한 허브 가루|돌연변이 토끼의 발|돌연변이 식물의 점액질|사스콰치의 심장|뮤턴트|정화된 토끼의 발|끈끈이 풀");
            Add(result, 4, "힐웬 광석 조각|무른 힐웬 광석 조각|힐웬|힐웬 합금|육각 볼트|육각 너트|스핀 기어|에메랄드 코어|에메랄드 퓨즈|에너지 컨버터|에너지 증폭 장치");
            // Herb identity wins over the many recipes/locations that use each herb.
            Add(result, 5, "베이스 허브|블러디 허브|마나 허브|선라이트 허브|골드 허브|화이트 허브|포이즌 허브");
            Add(result, 6, "생명력 500 포션|마나 500 포션|스태미나 500 포션|마리오네트 500 포션|포이즌 포션|축복의 포션|정령의 리큐르|고대 정령의 화석 조각|엘레멘탈 리무버|빈 병|물이 든 병");
            Add(result, 7, "건초 더미|못쓰게 된 밀 이파리|마법가루|작은 녹색구슬|작은 빨간구슬|작은 은색구슬|작은 파란구슬|미니 바닐라 향초|고급 바닐라 향초|최고급 바닐라 향초|밀랍|정제된 촉매제|인조 잔디|싱싱한 풀|꽃뭉치|발리스타용 독 묻은 와이번 볼트|와이번의 발톱|종이|빤짝이 종이");
            Add(result, 8, "마법의 양피지|부드러운 양피지|마법의 깃털펜|생기 있는 깃털");
            Add(result, 9, "펫 놀이세트|펫이 좋아하는 잡동사니|조화의 코스모스 퍼퓸|코스모스 추출액");
            Add(result, 10, "새우 조련 미끼|새우|설탕|마늘|밀|밀가루|보리|보릿가루");
            Add(result, 11, "아라트의 결정|어둠이 깃든 칼날 조각");
            return result;
        }

        static void Add(Dictionary<string, int> destination, int family, string names)
        {
            foreach (string name in names.Split('|')) destination.Add(name, family);
        }

        public static int GetOrder(string name)
        {
            int family;
            return name != null && families.TryGetValue(name, out family) ? family : labels.Length - 1;
        }
        public static string GetGroup(string name) { return labels[GetOrder(name)]; }
        public static bool IsKnown(string name) { return name != null && families.ContainsKey(name); }

        public static IOrderedEnumerable<T> OrderByCategory<T>(IEnumerable<T> items, Func<T, string> name)
        {
            return items.OrderBy(item => GetOrder(name(item))).ThenBy(name, koreanNames).ThenBy(name, StringComparer.Ordinal);
        }

        public static List<ProcurementStep> OrderSteps(IEnumerable<ProcurementStep> steps)
        {
            return steps.OrderBy(s => s.IsFinal ? 2 : s.Kind == "craft" ? 1 : 0)
                // Terminal exchange items have no later consumers; keep their families together.
                // Intermediate recipes must still follow all of their ingredient steps.
                .ThenBy(s => !s.IsFinal && s.Kind == "craft" ? s.Level : 0)
                .ThenBy(s => GetOrder(s.Name)).ThenBy(s => s.Name, koreanNames)
                .ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Kind, StringComparer.Ordinal)
                .ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
        }
    }
}
