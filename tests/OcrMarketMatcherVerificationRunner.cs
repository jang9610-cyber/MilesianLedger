using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MabinogiBarter;

public static class OcrMarketMatcherVerificationRunner
{
    static int checks;
    public static int Main(string[] args)
    {
        try {
            var data = Data();
            Add(data, "거미줄", 149); Add(data, "고급 가죽", 1234); Add(data, "최고급 가죽", 2345);
            Add(data, "가는 실뭉치", 200); Add(data, "마나 500 포션", 500); Add(data, "마나 300 포션", 300);
            Add(data, "체험 꾸러미 (3개)", 5000); Add(data, "나무장작", null);
            Add(data, "대지의 (접미 / 랭크 C) · 인챈트 스크롤", 1000, true);
            Add(data, "대지의 (접미 / 랭크 C) · 전용 인챈트 스크롤", 2000, true);
            Add(data, "인챈트 스크롤", 10, true);
            Add(data, "동일 이름", 10); Add(data, "동일이름", 20);
            var matcher = new OcrMarketMatcher(data);
            var rows = matcher.Resolve(new[] { " • 거미줄 x3", "거미줄 × 5", "거 미 줄", "고급 가죽 (3개)", "고급 가죽 [9개]" });
            Equal(2, rows.Count, "quantity cleanup and normalized duplicate merge");
            Exact(rows[0], "거미줄", 149); Exact(rows[1], "고급 가죽", 1234);
            rows = matcher.Resolve(new[] { "마나 500 포션 12개", "마나 300 포션", "체험 꾸러미 (3개)" });
            Equal(3, rows.Count, "meaningful numbers preserved"); Exact(rows[0], "마나 500 포션", 500);
            Exact(rows[2], "체험 꾸러미 (3개)", 5000);
            rows = matcher.Resolve(new[] { "가는실 뭉치", "나무장작", "현재 없는 드랍 아이템" });
            Exact(rows[0], "가는 실뭉치", 200); Check(rows[1].Exact, "known item without current listing still resolved");
            Check(!rows[1].Candidates[0].HasListing && !rows[1].Candidates[0].UnitPrice.HasValue, "no listing is not an invented price");
            Check(!rows[2].Exact && rows[2].Candidates.Count == 0 && rows[2].Text == "현재 없는 드랍 아이템", "unknown row retained for editing");
            rows = matcher.Resolve(new[] { "대지의", "대지의 전용 인챈트 스크롤", "인챈트 스크롤" });
            Check(!rows[0].Exact && rows[0].Candidates.Count == 2, "enchant shorthand requires variant choice");
            Check(rows[0].Candidates[0].Name != rows[0].Candidates[1].Name, "enchant variants never merge minima");
            Check(!rows[1].Exact && rows[1].Candidates.Count == 1, "even one shorthand suggestion stays unconfirmed");
            Check(!rows[2].Candidates[0].UnitPrice.HasValue, "unidentified enchant minimum is hidden");
            rows = matcher.Resolve(new[] { "고급", "거미쥴", "동일이름" });
            Check(!rows[0].Exact && rows[0].Candidates.Count > 0, "partial names are suggestions");
            Check(!rows[1].Exact && rows[1].Candidates[0].Name == "거미줄", "OCR typo gets unconfirmed spelling suggestion");
            Check(!rows[2].Exact && rows[2].Candidates.Count == 2, "normalized-name collision is ambiguous");
            rows = matcher.Resolve(new[] { "거미줄   고급 가죽", "최고급 가죽 | 마나 500 포션 3개", "거미줄 x3  고급 가죽 ×5" });
            Equal(4, rows.Count, "multiple item names per line resolve and dedupe");
            Exact(rows[0], "거미줄", 149); Exact(rows[1], "고급 가죽", 1234);
            Exact(rows[2], "최고급 가죽", 2345); Exact(rows[3], "마나 500 포션", 500);
            rows = matcher.Resolve(new[] { "거미줄x3 고급 가죽x5", "거미줄x3주머니 고급 가죽" });
            Equal(3, rows.Count, "attached counts recognized only at token boundaries");
            Exact(rows[0], "거미줄", 149); Exact(rows[1], "고급 가죽", 1234);
            Check(!rows[2].Exact, "an x3 fragment inside a longer item is not a count suffix");
            rows = matcher.Resolve(new[] { "거미줄주머니", "거미줄 주머니", "최고급 가죽 조각", "지금 거미줄을 받았습니다", "고급 가죽 거미줄주머니" });
            Equal(4, rows.Count, "unknown longer names and UI text retained; whitespace duplicates merged");
            foreach (var row in rows) Check(!row.Exact, "contained item fragment must not acquire confirmed price");
            rows = matcher.Resolve(new[] { "고급 가죽   거미줄", "고급 가죽" });
            Exact(rows[0], "고급 가죽", 1234); Exact(rows[1], "거미줄", 149);
            rows[0].Candidates[0].UnitPrice = 999;
            Exact(matcher.Resolve(new[] { "고급 가죽" })[0], "고급 가죽", 1234);
            Check(matcher.Resolve(null).Count == 0, "null input supported");
            Check(new OcrMarketMatcher(null).Resolve(new[] { "거미줄" })[0].Candidates.Count == 0, "no snapshot retains editable unknown");
            var longRows = new List<string>(); for (int i = 0; i < 1000; i++) longRows.Add("없는 드랍 이름 " + i);
            Equal(100, matcher.Resolve(longRows).Count, "output capped at 100 rows");
            Equal(512, matcher.Resolve(new[] { new string('가', 50000) })[0].Text.Length, "oversized lines bounded");

            var large = Data();
            for (int i = 0; i < 17000; i++) Add(large, "아이템" + i.ToString("D5") + " 종류", i + 1);
            var timer = Stopwatch.StartNew(); var largeMatcher = new OcrMarketMatcher(large);
            var inputs = new List<string>();
            for (int i = 0; i < 50; i++) inputs.Add("아이템" + (i * 211).ToString("D5") + "종류 x3");
            for (int i = 0; i < 50; i++) inputs.Add("아이템" + (i * 211).ToString("D5") + "종뤼");
            var largeResult = largeMatcher.Resolve(inputs); timer.Stop();
            Equal(100, largeResult.Count, "17k catalog resolves 100 mixed exact/typo rows");
            for (int i = 0; i < 50; i++) Check(largeResult[i].Exact, "large snapshot exact resolution");
            foreach (var row in largeResult) Check(row.Candidates.Count <= 5, "candidate cap");
            Check(timer.Elapsed < TimeSpan.FromSeconds(15), "bounded 17k-catalog batch time");
            string summary = checks + " OCR matcher checks passed; 17k catalog + 100 rows: " + timer.ElapsedMilliseconds + " ms. No HTTP, clipboard, or game access.";
            Console.WriteLine(summary);
            if (args.Length > 0) File.WriteAllText(Path.Combine(args[0], "report.txt"), summary);
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static MarketSnapshotData Data()
    { return new MarketSnapshotData { Quotes = new Dictionary<string, MarketSnapshotQuote>(), Items24h = new List<MarketSnapshotItem>(), Items7d = new List<MarketSnapshotItem>(), ListingsFetchedUtc = DateTime.UtcNow }; }
    static void Add(MarketSnapshotData data, string name, decimal? price, bool enchant = false)
    {
        data.Quotes[name] = new MarketSnapshotQuote { Name = name, UnitPrice = price, ListingCount = price.HasValue ? 1 : 0, Quantity = price.HasValue ? 3 : 0, FetchedUtc = DateTime.UtcNow };
        data.Items24h.Add(new MarketSnapshotItem { Name = name, Category = enchant ? "인챈트 스크롤" : "기타 재료", PriceComparable = true });
    }
    static void Exact(OcrMarketMatch row, string name, decimal price)
    { Check(row.Exact && row.Text == name && row.Candidates.Count == 1 && row.Candidates[0].Name == name && row.Candidates[0].UnitPrice == price, "exact price identity: " + name); }
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Equal(int expected, int actual, string message) { Check(expected == actual, message + ": expected " + expected + ", got " + actual); }
}
