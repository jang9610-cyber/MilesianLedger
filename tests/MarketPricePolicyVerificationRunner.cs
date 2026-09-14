using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MabinogiBarter;

public static class MarketPricePolicyVerificationRunner
{
    static int checks;
    static readonly DateTime Stamp = DateTime.UtcNow.AddHours(-1);
    static string Time(DateTime value) { return value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"); }
    static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }

    static void VerifyCategories()
    {
        var equipmentRoots = new HashSet<string>(new[] { "근거리 장비", "원거리 장비", "마법 장비", "점성술 장비", "갑옷 장비", "방어 장비", "액세서리", "특수 장비" });
        foreach (var group in MarketCategories.Build(null)) {
            if (group.Id == MarketCategories.AllId) continue;
            foreach (var node in new[] { group }.Concat(group.Children)) {
                bool excluded = (equipmentRoots.Contains(group.Name) && node.Name != "원거리 소모품")
                    || new[] { "토템", "애뮬릿", "마기그래프", "마기그래프 도안" }.Contains(node.Name);
                Check(MarketPricePolicy.IsComparable("비교 품목", node.Name) == !excluded, "taxonomy policy " + node.Path);
                Check(MarketPricePolicy.IsComparable("비교 품목", "\t" + node.Name.Replace(" ", "\t ") + "\n") == !excluded, "whitespace category " + node.Path);
            }
        }
        foreach (string category in new[] { "펫 토템", "펫토템", "마기그래프도안" })
            Check(!MarketPricePolicy.IsComparable("품목", category), "additional variable category " + category);
        foreach (string category in new[] { "원거리 소모품", "기타 재료", "피니 펫", "마기그래프 용품", "도면", "옷본", "대미지 스킨", "새로운 공개 분류" })
            Check(MarketPricePolicy.IsComparable("품목", category), "non-variable category " + category);
        foreach (string category in new[] { null, "", " \r\n\t" }) {
            Check(!MarketPricePolicy.IsComparable("품목", category), "unknown category not comparable");
            Check(MarketPricePolicy.ExclusionReason("품목", category).Contains("분류"), "unknown category explanation");
        }
        Check(!MarketPricePolicy.IsComparable("", "음식"), "missing identity not comparable");
        Check(MarketPricePolicy.ExclusionReason("품목", "애뮬릿").Contains("유동 옵션"), "totem explanation");
        foreach (string name in new[] { "인챈트 스크롤", "전용 인챈트 스크롤", "개방된 전용 인챈트 스크롤", "인챈트 스크롤(판매 불가)", "8주년 전용 인챈트 스크롤", "시양양 한정 인챈트 스크롤" }) {
            Check(!MarketPricePolicy.IsComparable(name, "인챈트 스크롤"), "unidentified scroll " + name);
            Check(!MarketPricePolicy.IsComparable(name, "기타 소모품"), "unidentified identity overrides category " + name);
            Check(MarketPricePolicy.ExclusionReason(name, "인챈트 스크롤").Contains("인챈트 이름"), "identity explanation " + name);
        }
        foreach (string name in new[] { "템포 (접미 / 랭크 6) · 인챈트 스크롤", "템포 (접미 / 랭크 6) · 전용 인챈트 스크롤", "각성한 · 인챈트 스크롤", "인챈트 스크롤 묶음", "인챈트 스크롤(5등급)" })
            Check(MarketPricePolicy.IsComparable(name, "인챈트 스크롤"), "distinct scroll identity comparable " + name);
    }

    static void VerifyServerParity(string repo)
    {
        string source = File.ReadAllText(Path.Combine(repo, "server", "cloudflare", "market-price-policy.mjs"), Encoding.UTF8);
        var block = Regex.Match(source, @"EXCLUDED_PRICE_CATEGORIES\s*=\s*new Set\(\[(.*?)\]\.map", RegexOptions.Singleline);
        Check(block.Success, "server category policy is available for cross-runtime parity");
        var serverNames = new HashSet<string>(Regex.Matches(block.Groups[1].Value, "'([^']+)'").Cast<Match>()
            .Select(match => new string(match.Groups[1].Value.Where(letter => !Char.IsWhiteSpace(letter)).ToArray())), StringComparer.Ordinal);
        var clientNames = (HashSet<string>)typeof(MarketPricePolicy).GetField("VariableCategories", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Check(serverNames.SetEquals(clientNames), "server and desktop exclusion categories agree exactly");
    }

    static Dictionary<string, object> Row(string name, string category, bool comparable, decimal? average, decimal? gold = 2500m, long? sold = 10, long? trades = 2, long? listed = 5, long? listings = 1)
    {
        return new Dictionary<string, object> {
            {"name",name}, {"category",category}, {"price_comparable",comparable}, {"average_sale_price",average},
            {"traded_gold",gold}, {"sold_quantity",sold}, {"trade_count",trades}, {"lowest_listing_price",null},
            {"listed_quantity",listed}, {"listing_count",listings}
        };
    }
    static Dictionary<string, object> Quote(string name, decimal price, DateTime stamp)
    {
        return new Dictionary<string, object> { {"name",name}, {"unit_price",price}, {"listing_count",1}, {"quantity",5}, {"fetched_at",Time(stamp)} };
    }
    sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; throw new Exception("Cache-only verification must not request HTTP.");
        }
    }
    static void VerifyLegacyCache(string folder)
    {
        var rows = new[] {
            Row("저장된 음식", "음식", false, null),
            Row("기존 평균", "천옷/방직", true, 123.456m),
            Row("새 분류 재료", "기타 재료", false, null),
            Row("화살", "원거리 소모품", false, null),
            Row("도안", "마기그래프 도안", true, 1000m),
            Row("무기", "검", true, 1000m),
            Row("애뮬릿", "애뮬릿", true, 1000m),
            Row("토템", "펫 토템", true, 1000m),
            Row("미확인 분류", "", false, null),
            Row("인챈트 스크롤", "인챈트 스크롤", true, 1000m),
            Row("템포 (접미 / 랭크 6) · 인챈트 스크롤", "인챈트 스크롤", false, null),
            Row("판매 없음", "음식", false, null, 0, 0, 0),
            Row("금액 미확인", "음식", false, null, null),
            Row("건수 미확인", "음식", false, null, 2500m, 10, null),
            Row("재고 없음", "음식", false, null, 2500m, 10, 2, 0, 0),
            Row("재고 미확인", "음식", false, null, 2500m, 10, 2, null, null),
            Row("지난 시세", "음식", false, null),
            Row("시세 누락", "음식", false, null),
            Row("새 평균 누락", "음식", true, null)
        };
        var quotes = rows.Where(row => (string)row["name"] != "시세 누락")
            .Select(row => Quote((string)row["name"], 50m, (string)row["name"] == "지난 시세" ? Stamp.AddHours(-1) : Stamp)).ToArray();
        var serializer = new JavaScriptSerializer { MaxJsonLength = 2000000 };
        string plainText = serializer.Serialize(new { schema_version = 1, generated_at = Time(Stamp), listings_fetched_at = Time(Stamp),
            items_24h = rows, items_7d = rows, quotes = quotes,
            status = new { listings = new { published = new { state = "complete", finished_at = Time(Stamp) } } } });
        byte[] plain = Encoding.UTF8.GetBytes(plainText), compressed;
        using (var memory = new MemoryStream()) {
            using (var gzip = new GZipStream(memory, CompressionMode.Compress, true)) gzip.Write(plain, 0, plain.Length);
            compressed = memory.ToArray();
        }
        string hash = MarketSnapshotClient.Hash(compressed);
        var manifest = new { schema_version = 1, version = hash, sha256 = hash, generated_at = Time(Stamp), snapshot_url = "/v1/market/snapshots/" + hash + ".json.gz",
            compressed_bytes = compressed.Length, uncompressed_bytes = plain.Length };
        string path = Path.Combine(folder, "legacy-cache.json");
        File.WriteAllText(path, serializer.Serialize(new { manifest = manifest, compressed_base64 = Convert.ToBase64String(compressed) }), new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(path);
        var handler = new NoNetwork();
        var client = new MarketSnapshotClient(new Uri("https://offline.example/"), path, handler);
        var data = client.ReadCachedData();
        Check(data != null && data.Version == hash && handler.Calls == 0, "verified legacy cache restores without network");
        foreach (var items in new[] { data.Items24h, data.Items7d }) {
            var byName = items.ToDictionary(row => row.Name);
            foreach (string name in new[] { "저장된 음식", "새 분류 재료", "화살", "템포 (접미 / 랭크 6) · 인챈트 스크롤", "새 평균 누락" }) {
                var item = byName[name];
                Check(item.PriceComparable && item.AverageSalePrice == 250m && item.LowestListingPrice == 50m, "legacy average and quote restore " + name);
                Check(MarketInsights.IsPriceRisk(item), "restored exact five-times ratio excludes " + name);
            }
            Check(byName["기존 평균"].AverageSalePrice == 123.456m, "existing comparable average precision preserved");
            foreach (string name in new[] { "도안", "무기", "애뮬릿", "토템", "미확인 분류", "인챈트 스크롤" }) {
                var item = byName[name];
                Check(!item.PriceComparable && !item.AverageSalePrice.HasValue && !MarketInsights.IsPriceRisk(item), "excluded or unidentified average masked " + name);
                Check(item.TradedGold == 2500m && item.SoldQuantity == 10 && item.TradeCount == 2, "excluded raw counts retained " + name);
                Check(MarketPrices.LowestFor(item, data) == (name == "인챈트 스크롤" ? (decimal?)null : 50m), "equipment minimum remains available but unidentified enchant has no single price " + name);
            }
            foreach (string name in new[] { "판매 없음", "금액 미확인", "건수 미확인" })
                Check(byName[name].PriceComparable && !byName[name].AverageSalePrice.HasValue && !MarketInsights.IsPriceRisk(byName[name]), "unknown average is not invented " + name);
            foreach (string name in new[] { "재고 없음", "재고 미확인", "지난 시세", "시세 누락" })
                Check(!byName[name].LowestListingPrice.HasValue && !MarketInsights.IsPriceRisk(byName[name]), "unavailable minimum not invented " + name);
        }
        var search = new MarketSearchIndex(data);
        Check(search.Search("저장된 음식", 10).Single().PriceComparable, "PIP/settlement search receives restored comparable metadata");
        Check(!search.Search("무기", 10).Single().PriceComparable, "PIP/settlement search still marks equipment noncomparable");
        Check(search.Search("템포", 10).Single().EnchantNameKnown, "named enchant identity survives policy normalization");
        Check(!search.Search("인챈트 스크롤", 100).Single(row => row.Name == "인챈트 스크롤").UnitPrice.HasValue,
            "unidentified mixed enchant minimum remains hidden in search");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "parsed compatibility does not rewrite verified raw cache");
        Check(Object.ReferenceEquals(client.ReadCachedData(), data) && handler.Calls == 0, "repeated local reads reuse prepared data without HTTP");
        MarketPricePolicy.Apply(data);
        Check(data.Items24h[0].AverageSalePrice == 250m && data.Items24h[1].AverageSalePrice == 123.456m, "policy is idempotent");
    }

    public static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(args[0]); VerifyCategories(); VerifyServerParity(args[1]); VerifyLegacyCache(args[0]);
            Console.WriteLine("PASS " + checks + " category, legacy-cache, risk and search policy checks."); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
