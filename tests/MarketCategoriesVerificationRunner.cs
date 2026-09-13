using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MabinogiBarter;

public static class MarketCategoriesVerificationRunner
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }

    static void Run()
    {
        var standard = MarketCategories.Build(null);
        Check(standard.Count == 18 && standard[0].Id == MarketCategories.AllId, "all selection precedes seventeen groups");
        Check(standard.Skip(1).All(node => node.IsGroup), "group node metadata");
        Check(standard.Skip(1).Sum(node => node.Children.Count) == 82, "reference hierarchy contains 82 exact leaf names");
        Check(standard.Select(node => node.Name).SequenceEqual(new[] { "전체 분류", "근거리 장비", "원거리 장비", "마법 장비", "점성술 장비", "갑옷 장비", "방어 장비", "액세서리", "특수 장비", "설치물", "인챈트 용품", "스크롤", "마기그래프 용품", "서적", "소모품", "토템", "생활 재료", "기타" }), "game group ordering");
        var leaves = standard.SelectMany(node => node.Children).ToList();
        Check(leaves.Select(node => node.Id).Distinct().Count() == leaves.Count, "leaf identifiers unique");
        foreach (var group in standard.Skip(1)) {
            Check(MarketCategories.Matches(group.Id, group.Name), "direct parent API category remains reachable: " + group.Name);
            foreach (var leaf in group.Children) {
                Check(MarketCategories.Matches(group.Id, leaf.Name), "parent contains descendant: " + leaf.Name);
                Check(MarketCategories.Matches(leaf.Id, leaf.Name), "leaf exact match: " + leaf.Name);
                Check(MarketCategories.Matches(MarketCategories.AllId, leaf.Name), "all contains category: " + leaf.Name);
                Check(MarketCategories.PathFor(leaf.Id) == group.Name + " › " + leaf.Name, "leaf path: " + leaf.Name);
            }
        }
        foreach (string name in new[] { "액세서리", "토템", "기타" }) {
            var group = standard.Single(node => node.Name == name);
            var sameNamedLeaf = group.Children.Single(node => node.Name == name);
            Check(group.Id != sameNamedLeaf.Id, "parent and same-named leaf must not collide: " + name);
            Check(MarketCategories.Matches(group.Id, group.Children.First().Name), "parent should include sibling categories");
            Check(!MarketCategories.Matches(sameNamedLeaf.Id, group.Children.First().Name), "same-named leaf must not include siblings");
        }
        Check(MarketCategories.Matches(MarketCategories.GroupId("생활 재료"), " 천옷 / 방직\t"), "known category whitespace normalization");
        Check(MarketCategories.LeafId("체인 블레이드") == MarketCategories.LeafId(" 체인\t블레이드 "), "stable whitespace normalized leaf identifier");
        Check(!MarketCategories.Matches(MarketCategories.LeafId("검"), "검은 재료"), "no substring matching");
        Check(!MarketCategories.Matches(MarketCategories.GroupId("생활 재료"), "천옷"), "clothing not confused with sewing materials");
        Check(!MarketCategories.Matches("group:missing", "검"), "invalid group must not fall back to all");
        Check(MarketCategories.Matches(MarketCategories.AllId, null), "all preserves missing categories");
        Check(MarketCategories.Matches(null, "검"), "empty selection represents all");
        Check(MarketCategories.Build(new[] { "근거리 장비", "천옷 / 방직", "검", "검" }).Count == 18, "known parent and whitespace variants do not become unknown");

        string[] observed = { "펫 토템", "대미지 스킨", "새 분류", "새\t분류", "새 분류", "검", "", null, "  " };
        var expanded = MarketCategories.Build(observed);
        var extra = expanded.Last();
        Check(expanded.Count == 19 && extra.Id == MarketCategories.UnknownGroupId && extra.Children.Count == 4, "observed unknown categories deduplicate and remain visible");
        Check(extra.Children.Any(node => node.Name == "펫 토템") && extra.Children.Any(node => node.Name == "대미지 스킨"), "real API categories absent from reference are retained");
        foreach (var value in observed) {
            Check(MarketCategories.Matches(MarketCategories.AllId, value), "all must keep every raw category");
            Check(expanded.Skip(1).Any(node => MarketCategories.Matches(node.Id, value)), "every raw category has a reachable group");
        }
        Check(MarketCategories.Matches(MarketCategories.UnknownGroupId, "펫 토템"), "unknown group selection");
        Check(!MarketCategories.Matches(MarketCategories.GroupId("토템"), "펫 토템"), "unverified membership must not be inferred by substring");
        Check(!MarketCategories.Matches(MarketCategories.UnknownGroupId, "천옷 / 방직"), "known whitespace variant not unknown");
        Check(MarketCategories.Matches(MarketCategories.LeafId(""), null), "missing category leaf matches missing observation");
        Check(MarketCategories.PathFor(MarketCategories.LeafId("펫 토템"), expanded) == "분류 미확인 › 펫 토템", "unknown path preserves display spaces");
        Check(MarketCategories.PathFor(MarketCategories.UnknownGroupId) == "분류 미확인", "unknown group path");
        Check(MarketCategories.Build(null).Count == 18, "building unknown nodes never mutates shared standard definitions");
        bool blocked = false;
        try { expanded.Add(standard[0]); } catch (NotSupportedException) { blocked = true; }
        Check(blocked, "root list is read only");
        blocked = false;
        try { standard[1].Children.Clear(); } catch (NotSupportedException) { blocked = true; }
        Check(blocked, "children are read only");
        Check(observed[3] == "새\t분류" && observed[7] == null, "normalization does not alter raw input");
    }

    public static int Main(string[] args)
    {
        try {
            Run();
            string result = "PASS " + checks + " category checks: game ordering, exact parent/leaf scope, whitespace, duplicate labels, unknown observations and immutable definitions.";
            Console.WriteLine(result);
            if (args.Length > 0) File.WriteAllText(Path.Combine(args[0], "report.txt"), result + Environment.NewLine, new UTF8Encoding(false));
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
