using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MabinogiBarter;

public static class MarketSearchVerificationRunner
{
    static readonly List<string> reports = new List<string>();
    static readonly DateTime Stamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Pass(string message) { reports.Add("PASS " + message); Console.WriteLine(reports[reports.Count - 1]); }
    static MarketSnapshotData Data() {
        return new MarketSnapshotData { Items24h = new List<MarketSnapshotItem>(), Items7d = new List<MarketSnapshotItem>(),
            Quotes = new Dictionary<string, MarketSnapshotQuote>(StringComparer.Ordinal), ListingsFetchedUtc = Stamp };
    }
    static void Quote(MarketSnapshotData data, string name, decimal? price, int count, long quantity) {
        data.Quotes.Add(name, new MarketSnapshotQuote { Name = name, UnitPrice = price, ListingCount = count, Quantity = quantity, FetchedUtc = Stamp.AddMinutes(1) });
    }
    static MarketSnapshotItem Item(string name, string category, bool comparable) {
        return new MarketSnapshotItem { Name = name, Category = category, PriceComparable = comparable, LowestListingPrice = 1m,
            AverageSalePrice = 2m, ListedQuantity = 999, ListingCount = 999 };
    }
    static MarketSearchEntry One(MarketSearchIndex index, string query) {
        var matches = index.Search(query, 1);
        Check(matches.Count == 1, "expected search result: " + query);
        return matches[0];
    }
    static void Run() {
        var empty = new MarketSearchIndex(null);
        Check(empty.Count == 0 && empty.Search("재료", 100).Count == 0, "null snapshot");
        Check(new MarketSearchIndex(new MarketSnapshotData()).Count == 0, "nullable collections");
        Pass("empty and nullable snapshots are searchable without I/O");

        var data = Data();
        Quote(data, "축복의 포션", 750.5m, 3, 22);
        Quote(data, "옵션 검", 120000m, 2, 2);
        Quote(data, "판매 전용", 31m, 1, 1);
        data.Items24h.Add(Item("축복의 포션", "소모품", true));
        data.Items24h.Add(Item("옵션 검", "무기", false));
        data.Items24h.Add(Item("거래 이력만", "재료", true));
        data.Items7d.Add(Item("축복의 포션", "오래된 분류", true));
        data.Items7d.Add(Item("칠일 이력", "기타", true));
        var index = new MarketSearchIndex(data);
        Check(index.Count == 5, "merge sources without duplicate names");
        var potion = One(index, "축복의포션");
        Check(potion.UnitPrice == 750.5m && potion.ListingCount == 3 && potion.Quantity == 22, "quote fields are authoritative");
        Check(potion.HasListing && potion.QuantityKnown && potion.PriceComparable && potion.Category == "소모품", "metadata enrichment");
        Check(potion.FetchedUtc == Stamp.AddMinutes(1), "quote timestamp wins");
        var equipment = One(index, "옵션검");
        Check(!equipment.PriceComparable && equipment.UnitPrice == 120000m && equipment.HasListing, "equipment retains observed price");
        Check(!One(index, "판매전용").PriceComparable, "quote without metadata conservatively not comparable");
        var history = One(index, "거래이력만");
        Check(!history.HasListing && !history.UnitPrice.HasValue && history.ListingCount == 0 && history.Quantity == 0, "no history price/count as current listing");
        Check(history.QuantityKnown && history.FetchedUtc == Stamp, "completed listing collection establishes no-listing time");
        Check(One(index, "칠일이력").Category == "기타", "7d-only history included");
        Pass("quote authority, history-only names, categories, and equipment caveat metadata");

        data.ListingsFetchedUtc = null;
        var unknown = One(new MarketSearchIndex(data), "거래이력만");
        Check(!unknown.HasListing && !unknown.UnitPrice.HasValue && !unknown.QuantityKnown && !unknown.FetchedUtc.HasValue, "expired/unknown listing coverage");
        var unknownQuantity = data.Quotes["축복의 포션"];
        unknownQuantity.Quantity = 0; unknownQuantity.QuantityKnown = false;
        var unknownQuantityEntry = One(new MarketSearchIndex(data), "축복의포션");
        Check(unknownQuantityEntry.HasListing && unknownQuantityEntry.UnitPrice == 750.5m && !unknownQuantityEntry.QuantityKnown, "unknown quantity does not erase a valid price");
        Pass("unknown listing coverage and unknown quantity never become fabricated availability");

        data = Data();
        Quote(data, "빈 매물", null, 0, 0);
        Quote(data, "잘못된 영가", 0m, 1, 1);
        Quote(data, "잘못된 음가", -1m, 1, 1);
        Quote(data, "개수 없는 가격", 30m, 0, 0);
        data.Quotes["빈 매물"].FetchedUtc = DateTime.MinValue;
        index = new MarketSearchIndex(data);
        foreach (string name in new[] { "빈 매물", "잘못된 영가", "잘못된 음가", "개수 없는 가격" }) {
            var entry = One(index, name);
            Check(!entry.HasListing && !entry.UnitPrice.HasValue, "no phantom current price for " + name);
        }
        Check(One(index, "빈매물").FetchedUtc == Stamp, "default quote time falls back to listing time");
        data.ListingsFetchedUtc = null;
        Check(!One(new MarketSearchIndex(data), "빈매물").QuantityKnown, "untimed empty quote is unknown");
        Pass("null/zero/negative or count-less prices do not create listings; timestamp fallback works");

        data = Data();
        Quote(data, "축복의 포션", 20m, 2, 4);
        Quote(data, "축복의포션", 10m, 1, 2);
        data.Items24h.Add(Item("축복의 포션", "", true));
        data.Items7d.Add(Item("축복의 포션", "소모품", false));
        data.Items24h.Add(null);
        data.Items24h.Add(Item(" \t", "ignored", true));
        index = new MarketSearchIndex(data);
        var distinct = index.Search(" \t축복의\u3000포션\r\n", 100);
        Check(index.Count == 2 && distinct.Count == 2, "normalized matching must preserve distinct source names");
        var spaced = One(index, "축복의 포션");
        Check(spaced.UnitPrice == 20m && spaced.ListingCount == 2 && spaced.Quantity == 4, "spaced source quote retains its own price and counts");
        Check(spaced.Category == "소모품" && !spaced.PriceComparable, "same-name metadata fills category with conservative comparability");
        Check(One(index, "축복의포션").UnitPrice == 10m, "raw-name exact beats other normalized exact names");
        data = Data();
        Quote(data, "A B", 50m, 2, 4); Quote(data, "AB", 70m, 3, 6); Quote(data, "ab", 90m, 4, 8);
        Quote(data, "  C\tD  ", 100m, 1, 1);
        index = new MarketSearchIndex(data);
        Check(index.Count == 4 && index.Search("a b", 100).Count == 3, "case/space variants remain distinct but all match");
        Check(index.Search("AB", 100)[0].UnitPrice == 70m && index.Search("A B", 100)[0].UnitPrice == 50m
            && index.Search("ab", 1)[0].UnitPrice == 90m, "raw-name exact precedes normalized ties, including limited search");
        Check(One(index, "CD").Name == "  C\tD  ", "display name preserves exact published source spelling");
        Pass("distinct case/space names retain identity and quotes; only same-name metadata merges; raw exact ranks first");

        data = Data();
        foreach (string name in new[] { "큰 포션", "포션 나", "가 포션", "포션", "포션 가", "ABC 포션" })
            data.Items24h.Add(Item(name, "소모품", true));
        index = new MarketSearchIndex(data);
        var ranked = index.Search("포 션", 100);
        string[] expected = { "포션", "포션 가", "포션 나", "ABC 포션", "가 포션", "큰 포션" };
        Check(ranked.Count == expected.Length, "ranked result count");
        for (int i = 0; i < expected.Length; i++) Check(ranked[i].Name == expected[i], "exact/prefix/contains ordinal rank " + i);
        Check(One(index, "abc").Name == "ABC 포션", "Latin case-insensitive matching");
        Check(index.Search("포션", 2)[1].Name == "포션 가", "limited search retains ranking");
        foreach (string query in new[] { null, "", " \t\r\n\u3000" }) Check(index.Search(query, 100).Count == 0, "empty query");
        Check(index.Search("없음", 100).Count == 0 && index.Search("포션", 0).Count == 0 && index.Search("포션", -1).Count == 0, "empty results and nonpositive limits");
        Pass("Korean whitespace and Latin case matching with exact/prefix/contains ordinal order");

        MarketSearchEntry detached = One(index, "포션");
        detached.Name = "changed"; detached.UnitPrice = 99m;
        data.Items24h.Clear();
        Check(One(index, "포션").Name == "포션" && !One(index, "포션").UnitPrice.HasValue, "index is independent of result/source edits");
        Pass("results and source list edits cannot mutate the built snapshot index");

        data = Data();
        for (int i = 19999; i >= 0; i--) data.Items24h.Add(Item("재료 " + i.ToString("D5", CultureInfo.InvariantCulture), "재료", true));
        var timer = Stopwatch.StartNew();
        index = new MarketSearchIndex(data);
        Check(index.Count == 20000, "large index count");
        Check(index.Search("재료", Int32.MaxValue).Count == 100, "100 result cap");
        Check(index.Search("재료", 101).Count == 100, "cap just above limit");
        var exactLast = index.Search("재료19999", 100);
        Check(exactLast.Count == 1 && exactLast[0].Name == "재료 19999", "lookup after full large sorted index");
        for (int i = 0; i < 30; i++) Check(index.Search("199", 30).Count == 30, "repeated large contains query");
        timer.Stop();
        Pass("20,000 names, repeated lookups, and hard 100-result limit (" + timer.ElapsedMilliseconds + " ms)");
    }

    public static int Main(string[] args) {
        try {
            Run();
            string summary = String.Join(Environment.NewLine, reports.ToArray()) + Environment.NewLine + "Passed " + reports.Count + " checks." + Environment.NewLine;
            if (args.Length != 0) File.WriteAllText(Path.Combine(args[0], "summary.txt"), summary, new UTF8Encoding(false));
            Console.WriteLine("Passed " + reports.Count + " checks.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
