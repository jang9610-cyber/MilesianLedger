using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace MabinogiBarter
{
    public sealed class MarketCategoryNode
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Path { get; private set; }
        public bool IsGroup { get; private set; }
        public IList<MarketCategoryNode> Children { get; private set; }

        internal MarketCategoryNode(string id, string name, string path, bool isGroup, IEnumerable<MarketCategoryNode> children)
        {
            Id = id; Name = name; Path = path; IsGroup = isGroup;
            Children = new ReadOnlyCollection<MarketCategoryNode>((children ?? Enumerable.Empty<MarketCategoryNode>()).ToList());
        }
    }

    // Display ordering follows the in-game auction categories shown at mabi.zip/auction-live.
    // API category values stay unchanged; selection uses exact names after whitespace normalization.
    public static class MarketCategories
    {
        public const string AllId = "all";
        public const string UnknownGroupId = "group:unclassified";
        public const string UnknownName = "분류 미확인";
        static readonly string[][] Definition = {
            new[] { "근거리 장비", "한손 장비", "양손 장비", "검", "도끼", "둔기", "랜스", "핸들", "너클", "체인 블레이드" },
            new[] { "원거리 장비", "활", "석궁", "듀얼건", "수리검", "아틀라틀", "원거리 소모품" },
            new[] { "마법 장비", "실린더", "스태프", "원드", "마도서", "힐링 원드" },
            new[] { "점성술 장비", "대형 낫", "오브" },
            new[] { "갑옷 장비", "중갑옷", "경갑옷", "천옷" },
            new[] { "방어 장비", "장갑", "신발", "모자/가발", "방패", "로브" },
            new[] { "액세서리", "얼굴 장식", "액세서리", "날개", "꼬리" },
            new[] { "특수 장비", "악기", "생활 도구", "마리오네트", "에코스톤", "에이도스", "유물", "기타 장비" },
            new[] { "설치물", "의자/사물", "낭만농장/달빛섬" },
            new[] { "인챈트 용품", "인챈트 스크롤", "마법가루" },
            new[] { "스크롤", "도면", "옷본", "마족 스크롤", "기타 스크롤" },
            new[] { "마기그래프 용품", "마기그래프", "마기그래프 도안", "기타 재료" },
            new[] { "서적", "책", "마비노벨", "페이지" },
            new[] { "소모품", "포션", "음식", "허브", "던전 통행증", "알반 훈련석", "개조석", "보석", "변신 메달", "염색 앰플", "스케치", "핀즈비즈", "기타 소모품" },
            new[] { "토템", "애뮬릿", "토템" },
            new[] { "생활 재료", "주머니", "천옷/방직", "제련/블랙스미스", "힐웬 공학", "매직 크래프트" },
            new[] { "기타", "제스처", "말풍선 스티커", "피니 펫", "불타래", "퍼퓸", "분양 메달", "뷰티 쿠폰", "기타" }
        };
        static readonly Dictionary<string, HashSet<string>> GroupValues = CreateGroups();
        static readonly HashSet<string> KnownValues = new HashSet<string>(Definition.SelectMany(group => group).Select(Normalize), StringComparer.Ordinal);
        static readonly IList<MarketCategoryNode> StandardNodes = CreateStandardNodes();
        static readonly Dictionary<string, string> StandardPaths = StandardNodes.SelectMany(node => new[] { node }.Concat(node.Children))
            .ToDictionary(node => node.Id, node => node.Path, StringComparer.Ordinal);

        public static string Normalize(string category)
        {
            if (String.IsNullOrEmpty(category)) return "";
            var value = new StringBuilder(category.Length);
            foreach (char letter in category) if (!Char.IsWhiteSpace(letter)) value.Append(letter);
            return value.ToString();
        }

        public static string GroupId(string name)
        {
            return Normalize(name) == Normalize(UnknownName) ? UnknownGroupId : "group:" + Normalize(name);
        }

        public static string LeafId(string name) { return "leaf:" + Normalize(name); }

        static Dictionary<string, HashSet<string>> CreateGroups()
        {
            return Definition.ToDictionary(group => GroupId(group[0]), group => new HashSet<string>(group.Select(Normalize), StringComparer.Ordinal), StringComparer.Ordinal);
        }

        static IList<MarketCategoryNode> CreateStandardNodes()
        {
            var result = new List<MarketCategoryNode> { new MarketCategoryNode(AllId, "전체 분류", "전체 분류", false, null) };
            foreach (var group in Definition) {
                string parent = group[0];
                result.Add(new MarketCategoryNode(GroupId(parent), parent, parent, true,
                    group.Skip(1).Select(name => new MarketCategoryNode(LeafId(name), name, parent + " › " + name, false, null))));
            }
            return new ReadOnlyCollection<MarketCategoryNode>(result);
        }

        public static IList<MarketCategoryNode> Build(IEnumerable<string> observedCategories)
        {
            var unknown = (observedCategories ?? Enumerable.Empty<string>())
                .Where(name => !KnownValues.Contains(Normalize(name)))
                .GroupBy(Normalize, StringComparer.Ordinal)
                .Select(group => String.IsNullOrEmpty(group.Key) ? "" : group.Select(name => name.Trim()).OrderBy(name => name, StringComparer.Ordinal).First())
                .OrderBy(name => name, StringComparer.Ordinal).ToList();
            if (unknown.Count == 0) return StandardNodes;
            var result = StandardNodes.ToList();
            result.Add(new MarketCategoryNode(UnknownGroupId, UnknownName, UnknownName, true,
                unknown.Select(name => new MarketCategoryNode(LeafId(name), String.IsNullOrEmpty(name) ? UnknownName : name,
                    String.IsNullOrEmpty(name) ? UnknownName : UnknownName + " › " + name, false, null))));
            return new ReadOnlyCollection<MarketCategoryNode>(result);
        }

        public static bool Matches(string selectionId, string rawCategory)
        {
            if (String.IsNullOrEmpty(selectionId) || selectionId == AllId) return true;
            string value = Normalize(rawCategory);
            if (selectionId == UnknownGroupId) return !KnownValues.Contains(value);
            HashSet<string> categories;
            if (GroupValues.TryGetValue(selectionId, out categories)) return categories.Contains(value);
            return selectionId.StartsWith("leaf:", StringComparison.Ordinal) && selectionId.Substring(5) == value;
        }

        public static string PathFor(string selectionId) { return PathFor(selectionId, null); }

        public static string PathFor(string selectionId, IEnumerable<MarketCategoryNode> nodes)
        {
            if (String.IsNullOrEmpty(selectionId)) return "전체 분류";
            string path;
            if (StandardPaths.TryGetValue(selectionId, out path)) return path;
            if (selectionId == UnknownGroupId) return UnknownName;
            if (nodes != null) {
                var node = nodes.SelectMany(root => new[] { root }.Concat(root.Children)).FirstOrDefault(item => item.Id == selectionId);
                if (node != null) return node.Path;
            }
            return selectionId.StartsWith("leaf:", StringComparison.Ordinal) && selectionId.Length > 5
                ? UnknownName + " › " + selectionId.Substring(5) : UnknownName;
        }
    }
}
