using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MabinogiBarter;

public static class MarketInsightsVerificationRunner
{
    static readonly List<string> reports = new List<string>();
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Pass(string message) { reports.Add("PASS " + message); Console.WriteLine(reports[reports.Count - 1]); }
    static MarketSnapshotItem Item(long? sold, long? listed, long? trades)
    {
        return new MarketSnapshotItem { Name = "거미줄", Category = "재료", SoldQuantity = sold, ListedQuantity = listed,
            TradeCount = trades, PriceComparable = true, AverageSalePrice = 100, LowestListingPrice = 70 };
    }
    static void Run(string directory)
    {
        var item = Item(100, 20, 5);
        Check(!MarketInsights.Matches(null, MarketOpportunity.All), "null item is not a market row");
        Check(MarketInsights.Matches(item, MarketOpportunity.All), "all accepts observed item");
        Check(!MarketInsights.Rank(item, MarketOpportunity.All).HasValue, "all has no invented score");
        Check(MarketInsights.Matches(item, MarketOpportunity.ActiveTrading), "positive trades eligible");
        Check(MarketInsights.Rank(item, MarketOpportunity.ActiveTrading) == 5, "active rank uses transactions");
        foreach (long? value in new long?[] { null, 0, -1 }) {
            Check(!MarketInsights.Matches(Item(100, 20, value), MarketOpportunity.ActiveTrading), "unknown/nonpositive trades excluded");
        }
        Pass("activity uses known positive completed transaction counts");

        Check(MarketInsights.Rank(item, MarketOpportunity.LowSupply) == 5, "supply ratio matches units sold / units listed");
        Check(MarketInsights.Matches(Item(100, 0, 5), MarketOpportunity.LowSupply), "known no-listings opportunity retained");
        Check(MarketInsights.Rank(Item(100, 0, 5), MarketOpportunity.LowSupply) > MarketInsights.Rank(Item(Int64.MaxValue, 1, 1), MarketOpportunity.LowSupply), "no listings first even at largest quantity");
        Check(MarketInsights.Detail(Item(100, 0, 5), MarketOpportunity.LowSupply).Contains("현재 매물 없음"), "zero listing sentinel not displayed as a number");
        foreach (long? value in new long?[] { null, -1, 100, 101 })
            Check(!MarketInsights.Matches(Item(100, value, 5), MarketOpportunity.LowSupply), "unknown/equal/oversupply excluded");
        foreach (long? value in new long?[] { null, 0, -1 })
            Check(!MarketInsights.Matches(Item(value, 0, 5), MarketOpportunity.LowSupply), "no demand excluded");
        Check(MarketInsights.Detail(item, MarketOpportunity.LowSupply).Contains("5배"), "ratio explained in human text");
        Pass("low supply excludes unknown availability and orders known zero listings explicitly");

        Check(MarketInsights.Rank(item, MarketOpportunity.BelowAverage) == 30, "discount measured against observed mean");
        Check(MarketInsights.Detail(item, MarketOpportunity.BelowAverage).Contains("30% 낮음"), "price difference labeled without a profit prediction");
        item.PriceComparable = false;
        Check(!MarketInsights.Matches(item, MarketOpportunity.BelowAverage), "option-sensitive equipment excluded");
        item.PriceComparable = true;
        foreach (decimal? value in new decimal?[] { null, 0, -1, 100, 101 }) {
            item.LowestListingPrice = value;
            Check(!MarketInsights.Matches(item, MarketOpportunity.BelowAverage), "unknown/nonpositive/not discounted price excluded");
        }
        item.LowestListingPrice = 1;
        foreach (decimal? value in new decimal?[] { null, 0, -1 }) {
            item.AverageSalePrice = value;
            Check(!MarketInsights.Matches(item, MarketOpportunity.BelowAverage), "invalid average excluded");
        }
        foreach (long? value in new long?[] { null, 0, -1 }) {
            Check(!MarketInsights.Matches(Item(100, value, 5), MarketOpportunity.BelowAverage), "requires observed listing units");
            Check(!MarketInsights.Matches(Item(100, 20, value), MarketOpportunity.BelowAverage), "requires completed transaction");
        }
        item = Item(100, 20, 5); item.AverageSalePrice = Decimal.MaxValue; item.LowestListingPrice = 1;
        Check(MarketInsights.Rank(item, MarketOpportunity.BelowAverage) <= 100, "large price math cannot overflow");
        Pass("below-average view rejects unknown prices, option-sensitive items, and absent trades/listings");

        item = Item(19, 10, 5);
        Check(MarketInsights.Rank(item, MarketOpportunity.LowSupply) == 1.9m, "supply ratio keeps calculation precision");
        Check(MarketInsights.Detail(item, MarketOpportunity.LowSupply).Contains("1배"), "supply ratio display truncates instead of rounding");
        item.AverageSalePrice = 100; item.LowestListingPrice = 0.001m;
        Check(MarketInsights.Rank(item, MarketOpportunity.BelowAverage) == 99.999m, "price gap keeps calculation precision");
        Check(MarketInsights.Detail(item, MarketOpportunity.BelowAverage).Contains("99% 낮음"), "positive listing price must not round to a 100 percent discount");
        item.LowestListingPrice = 99.9m;
        Check(MarketInsights.Detail(item, MarketOpportunity.BelowAverage).Contains("1% 미만 낮음"), "small gap is not presented as zero");
        var preciseGap = Item(100, 20, 5); preciseGap.LowestListingPrice = 11.17m;
        var smallerGap = Item(100, 20, 5); smallerGap.LowestListingPrice = 11.99m;
        Check(MarketInsights.Rank(preciseGap, MarketOpportunity.BelowAverage) == 88.83m
            && MarketInsights.Rank(smallerGap, MarketOpportunity.BelowAverage) == 88.01m,
            "display precision must not alter comparison ranks");
        Check(MarketInsights.Rank(preciseGap, MarketOpportunity.BelowAverage) > MarketInsights.Rank(smallerGap, MarketOpportunity.BelowAverage)
            && MarketInsights.PercentageText(MarketInsights.Rank(preciseGap, MarketOpportunity.BelowAverage).Value) == "88%"
            && MarketInsights.PercentageText(MarketInsights.Rank(smallerGap, MarketOpportunity.BelowAverage).Value) == "88%",
            "identically displayed integer percentages retain precise ranking");
        Pass("ratios and percentages truncate for display while eligibility and ranks keep precision");

        Check(MarketInsights.PriceRiskThreshold == 5m, "risk threshold is the confirmed five-times minimum");
        Check(!MarketInsights.IsPriceRisk(null) && !MarketInsights.RiskMultiple(null).HasValue
            && MarketInsights.RiskReason(null) == "", "missing row has no risk classification");
        var boundary = Item(2, 10, 2); boundary.LowestListingPrice = 100;
        boundary.AverageSalePrice = 499.9m;
        Check(MarketInsights.RiskMultiple(boundary) == 4.999m && !MarketInsights.IsPriceRisk(boundary),
            "4.999 times stays below the threshold without display rounding");
        boundary.AverageSalePrice = 500;
        Check(MarketInsights.RiskMultiple(boundary) == 5 && MarketInsights.IsPriceRisk(boundary),
            "exactly five times is excluded");
        Check(MarketInsights.RiskReason(boundary).Contains("5배 이상")
            && MarketInsights.RiskReason(boundary).Contains("일반 목록에서 제외"), "reason exposes the exact rule and scope");
        boundary.AverageSalePrice = 1055;
        Check(MarketInsights.RiskMultiple(boundary) == 10.55m && MarketInsights.IsPriceRisk(boundary),
            "more than ten times is excluded without altering its ratio");
        Check(MarketInsights.RiskReason(boundary).Contains("10배 이상")
            && !MarketInsights.RiskReason(boundary).Contains("10.55"), "risk ratio text drops decimals");
        Check(MarketInsights.Matches(boundary, MarketOpportunity.BelowAverage)
            && MarketInsights.Matches(boundary, MarketOpportunity.ActiveTrading),
            "risk classification stays separate from opportunity criteria for inspecting exclusions");
        boundary.AverageSalePrice = 50;
        Check(!MarketInsights.IsPriceRisk(boundary) && MarketInsights.RiskMultiple(boundary) == 0.5m
            && MarketInsights.RiskReason(boundary) == "", "higher listing price does not imply a high-average anomaly");
        Pass("five-times exclusion is exact at ordinary price boundaries and independent of opportunity filters");

        foreach (decimal? value in new decimal?[] { null, 0, -1 }) {
            item = Item(1, 1, 1); item.AverageSalePrice = value; item.LowestListingPrice = 1;
            Check(!MarketInsights.IsPriceRisk(item) && !MarketInsights.RiskMultiple(item).HasValue,
                "unknown or nonpositive average cannot classify risk");
            item = Item(1, 1, 1); item.AverageSalePrice = 1000; item.LowestListingPrice = value;
            Check(!MarketInsights.IsPriceRisk(item) && !MarketInsights.RiskMultiple(item).HasValue,
                "unknown or nonpositive minimum cannot classify risk");
        }
        foreach (long? value in new long?[] { null, 0, -1 }) {
            var observations = new[] { Item(value, 1, 1), Item(1, value, 1), Item(1, 1, value) };
            foreach (var observation in observations) {
                observation.AverageSalePrice = 1000; observation.LowestListingPrice = 1;
                Check(!MarketInsights.IsPriceRisk(observation) && !MarketInsights.RiskMultiple(observation).HasValue
                    && MarketInsights.RiskReason(observation) == "", "requires known positive sales, listings, and trades");
            }
        }
        item = Item(1, 1, 1); item.AverageSalePrice = 100000; item.LowestListingPrice = 1;
        foreach (long value in new long[] { 0, -1 }) {
            item.ListingCount = value;
            Check(!MarketInsights.IsPriceRisk(item) && !MarketInsights.RiskMultiple(item).HasValue,
                "explicit absent or invalid listing count overrides stale positive quantity and price");
        }
        item.ListingCount = null;
        Check(MarketInsights.IsPriceRisk(item), "unknown listing count allows positive observed quantity and price");
        item.ListingCount = 1;
        Check(MarketInsights.IsPriceRisk(item), "positive listing count allows risk classification");
        item.PriceComparable = false;
        Check(!MarketInsights.IsPriceRisk(item) && !MarketInsights.RiskMultiple(item).HasValue,
            "option-sensitive equipment is unclassified rather than compared by incompatible prices");
        Pass("unknown prices, absent observations, and option-sensitive equipment have no risk determination");

        item = Item(1, 1, 1); item.AverageSalePrice = Decimal.MaxValue; item.LowestListingPrice = 0.0000000000000000000000000001m;
        Check(MarketInsights.IsPriceRisk(item) && MarketInsights.RiskMultiple(item) == Decimal.MaxValue,
            "extreme ratio overflow saturates the display but remains classifiable");
        Check(!String.IsNullOrEmpty(MarketInsights.RiskReason(item)), "extreme ratio reason cannot throw");
        item.LowestListingPrice = Decimal.MaxValue;
        Check(!MarketInsights.IsPriceRisk(item) && MarketInsights.RiskMultiple(item) == 1,
            "largest minimum does not overflow threshold comparison");
        var anomalous = Item(2, 349, 2); anomalous.Name = "아몬드";
        anomalous.AverageSalePrice = 22350150; anomalous.LowestListingPrice = 190; anomalous.TradedGold = 44700300;
        var normal = Item(10, 20, 4);
        var snapshot = new MarketSnapshotData { Items24h = new List<MarketSnapshotItem> { anomalous, normal },
            Items7d = new List<MarketSnapshotItem> { anomalous, normal } };
        var selectedRows = snapshot.Items24h.Where(x => !MarketInsights.IsPriceRisk(x)).ToList();
        var excludedRows = snapshot.Items24h.Where(MarketInsights.IsPriceRisk).ToList();
        Check(selectedRows.Count == 1 && Object.ReferenceEquals(selectedRows[0], normal)
            && excludedRows.Count == 1 && Object.ReferenceEquals(excludedRows[0], anomalous),
            "normal and excluded views partition existing observations without rewriting them");
        Check(snapshot.Items24h.Count == 2 && snapshot.Items7d.Count == 2
            && anomalous.AverageSalePrice == 22350150 && anomalous.LowestListingPrice == 190
            && anomalous.TradedGold == 44700300 && anomalous.SoldQuantity == 2,
            "source snapshot and raw aggregates remain unchanged for inspection");
        Pass("risk inspection handles extreme values and preserves the source snapshot and aggregates");

        string path = Path.Combine(directory, "personal", "market-watchlist.json"), error;
        var store = new WatchlistStore(path);
        Check(store.Count == 0 && store.Notice == null, "missing file starts clean");
        Check(store.TrySet("템포 인챈트 스크롤", true, out error) && error == null, "create first saved name");
        Check(store.TrySet("템포 전용 인챈트 스크롤", true, out error), "enchant form has separate exact identity");
        Check(store.TrySet("관측되지 않는 품목", true, out error), "no current market row needed to retain favorite");
        var reloaded = new WatchlistStore(path);
        Check(reloaded.Count == 3 && reloaded.Contains("템포 인챈트 스크롤") && reloaded.Contains("템포 전용 인챈트 스크롤"), "restart roundtrip preserves exact names");
        string[] copy = reloaded.Entries; copy[0] = "외부 수정";
        Check(!reloaded.Contains("외부 수정") && reloaded.Count == 3, "Entries is not mutable store state");
        Check(reloaded.TrySet("템포 인챈트 스크롤", false, out error), "atomic update removes one name");
        Check(new WatchlistStore(path).Count == 2 && new WatchlistStore(path).Contains("템포 전용 인챈트 스크롤"), "remove preserves different enchant form");
        Check(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "no temporary leftovers");
        Pass("watchlist survives restart and changes atomically without depending on current observations");

        string corruptPath = Path.Combine(directory, "corrupt.json");
        foreach (string raw in new[] { "broken-json", "{\"version\":2,\"items\":[\"기존 품목\"]}", "{\"version\":1,\"items\":[42]}", "{\"version\":1,\"items\":null}" }) {
            File.WriteAllText(corruptPath, raw, new UTF8Encoding(false));
            var corrupt = new WatchlistStore(corruptPath);
            Check(!String.IsNullOrEmpty(corrupt.Notice), "malformed or forward schema reports load failure");
            Check(!corrupt.TrySet("새 품목", true, out error) && !String.IsNullOrEmpty(error), "malformed file is not overwritten");
            Check(File.ReadAllText(corruptPath) == raw && corrupt.Count == 0, "failed load preserves original file byte content");
        }
        Pass("corrupt and future-version files stay unchanged and refuse destructive saves");

        string blocked = Path.Combine(directory, "directory-instead-of-file.json");
        Directory.CreateDirectory(blocked);
        var failure = new WatchlistStore(blocked);
        Check(!failure.TrySet("저장 실패", true, out error), "invalid destination save fails");
        Check(!failure.Contains("저장 실패") && !String.IsNullOrEmpty(error), "failed write rolls back in-memory membership");
        Check(Directory.GetFiles(directory, ".market-watchlist-*.tmp").Length == 0, "failed write cleans temporary file");
        string original = File.ReadAllText(path);
        var lockedStore = new WatchlistStore(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
            Check(!lockedStore.TrySet("템포 전용 인챈트 스크롤", false, out error), "locked existing file rejects atomic replacement");
            Check(lockedStore.Contains("템포 전용 인챈트 스크롤"), "failed removal retains previous in-memory favorite");
        }
        Check(File.ReadAllText(path) == original, "locked-file write failure preserves existing bytes");
        Check(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "failed replacement cleans temporary file");
        Pass("failed persistence leaves in-memory selections unchanged");

        var memory = new WatchlistStore(null);
        foreach (string name in new[] { null, "", "  ", "이름\n주입", new string('가', 513) })
            Check(!memory.TrySet(name, true, out error), "invalid/oversized names rejected");
        Check(memory.TrySet("ABC", true, out error) && memory.TrySet("abc", true, out error), "ordinal names do not merge");
        for (int i = memory.Count; i < WatchlistStore.MaximumEntries; i++)
            Check(memory.TrySet("관심 품목 " + i, true, out error), "bounded in-memory list accepts valid entry");
        Check(!memory.TrySet("한도 초과", true, out error) && memory.Count == WatchlistStore.MaximumEntries, "entry bound enforced");
        Check(memory.TrySet("ABC", true, out error), "existing selection is idempotent even at capacity");
        Check(memory.TrySet("ABC", false, out error) && memory.TrySet("빈 자리", true, out error), "removing frees capacity");
        Pass("memory-only operation, input limits, capacity, and idempotent toggles");
    }
    public static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try {
            Run(output);
            reports.Add("PASS " + checks + " checks");
            File.WriteAllLines(Path.Combine(output, "report.txt"), reports, new UTF8Encoding(false));
            Console.WriteLine(reports[reports.Count - 1]);
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
