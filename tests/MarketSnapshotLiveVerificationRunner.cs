using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MabinogiBarter;

public static class MarketSnapshotLiveVerificationRunner
{
    static readonly HashSet<string> ScrollBases = new HashSet<string>(StringComparer.Ordinal) {
        "인챈트 스크롤", "전용 인챈트 스크롤", "개방된 전용 인챈트 스크롤",
        "인챈트 스크롤(판매 불가)", "8주년 전용 인챈트 스크롤", "시양양 한정 인챈트 스크롤"
    };
    static readonly Regex NamedScroll = new Regex(@"^(.+) \((접두|접미) / 랭크 ([1-9A-F])\) · (.+)$", RegexOptions.CultureInvariant);
    sealed class EnchantVerification
    {
        public int NamedCount, VerifiedCount, PricedQuotes, PartialSearchVerified, GenericCount;
        public readonly List<object> Examples = new List<object>();
    }
    static MarketSearchEntry Exact(MarketSearchIndex search, string name)
    {
        var found = search.Search(name, 1);
        if (found.Count != 1 || found[0].Name != name) throw new Exception("Exact enchant identity not found: " + name);
        return found[0];
    }
    static void SameQuote(MarketSearchEntry found, MarketSnapshotQuote quote)
    {
        if (found.Name != quote.Name || found.UnitPrice != quote.UnitPrice || found.ListingCount != quote.ListingCount
            || found.Quantity != quote.Quantity || found.QuantityKnown != quote.QuantityKnown || found.FetchedUtc != quote.FetchedUtc
            || found.HasListing != (quote.UnitPrice.HasValue && quote.UnitPrice.Value > 0 && quote.ListingCount > 0))
            throw new Exception("Named enchant search differs from source price, count or timestamp: " + quote.Name);
    }
    static EnchantVerification VerifyEnchantScrolls(MarketSnapshotData data, MarketSearchIndex search)
    {
        var report = new EnchantVerification();
        var metadata = data.Items24h.Concat(data.Items7d).ToLookup(item => item.Name, StringComparer.Ordinal);
        var sourceNames = metadata.Select(group => group.Key).Concat(data.Quotes.Keys).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal);
        var priced = new List<MarketSnapshotQuote>();
        foreach (string name in sourceNames) {
            int separator = name.LastIndexOf(" · ", StringComparison.Ordinal);
            bool decoratedScroll = separator > 0 && ScrollBases.Contains(name.Substring(separator + 3));
            if (decoratedScroll) {
                var identity = NamedScroll.Match(name);
                if (!identity.Success || !ScrollBases.Contains(identity.Groups[4].Value) || String.IsNullOrWhiteSpace(identity.Groups[1].Value))
                    throw new Exception("Named enchant does not preserve enchant, position, rank and scroll base: " + name);
                report.NamedCount++;
                if (!metadata[name].Any() || metadata[name].Any(item => item.Category != "인챈트 스크롤" || !item.PriceComparable))
                    throw new Exception("Named enchant lacks trusted scroll category/comparability metadata: " + name);
                var found = Exact(search, name);
                if (!found.IsEnchantScroll || !found.EnchantNameKnown || !found.PriceComparable || found.Category != "인챈트 스크롤")
                    throw new Exception("Named enchant search lost its trusted scroll classification: " + name);
                MarketSnapshotQuote quote;
                if (data.Quotes.TryGetValue(name, out quote)) {
                    SameQuote(found, quote);
                    foreach (var item in metadata[name]) {
                        if (item.ListingCount != quote.ListingCount || item.ListedQuantity.HasValue != quote.QuantityKnown
                            || (quote.QuantityKnown && item.ListedQuantity != quote.Quantity) || item.LowestListingPrice != quote.UnitPrice)
                            throw new Exception("Named enchant ranking and quote disagree on current listing data: " + name);
                    }
                    if (quote.UnitPrice.HasValue && quote.ListingCount > 0) priced.Add(quote);
                } else if (found.UnitPrice.HasValue || found.HasListing || found.ListingCount != 0 || found.Quantity != 0)
                    throw new Exception("History-only named enchant acquired a current quote: " + name);
                report.VerifiedCount++;
            } else if (ScrollBases.Contains(name)) {
                report.GenericCount++;
                if (metadata[name].Any(item => item.PriceComparable || item.LowestListingPrice.HasValue || item.AverageSalePrice.HasValue))
                    throw new Exception("Generic scroll metadata exposes an individual enchant price: " + name);
                var found = Exact(search, name);
                if (!found.IsEnchantScroll || found.EnchantNameKnown || found.PriceComparable || found.UnitPrice.HasValue || found.HasListing)
                    throw new Exception("Generic scroll search exposes an individual enchant price: " + name);
                MarketSnapshotQuote quote;
                if (data.Quotes.TryGetValue(name, out quote)) {
                    if (quote.UnitPrice.HasValue || found.ListingCount != quote.ListingCount || found.Quantity != quote.Quantity
                        || found.QuantityKnown != quote.QuantityKnown || found.FetchedUtc != quote.FetchedUtc)
                        throw new Exception("Generic scroll quote exposed a price or lost observed stock counts: " + name);
                }
            }
        }
        report.PricedQuotes = priced.Count;
        if (report.NamedCount == 0 || report.PricedQuotes == 0)
            throw new Exception("Live snapshot has no identified enchant scrolls with current listings; wait for a complete named collection.");
        // Sample actual distinct enchant names so the check does not depend on any particular live listing.
        foreach (var quote in priced.GroupBy(value => NamedScroll.Match(value.Name).Groups[1].Value, StringComparer.Ordinal).Select(group => group.First()).Take(3)) {
            var identity = NamedScroll.Match(quote.Name);
            string query = identity.Groups[1].Value;
            var found = search.Search(query, 100).FirstOrDefault(entry => entry.Name == quote.Name);
            if (found == null || !found.IsEnchantScroll || !found.EnchantNameKnown || !found.PriceComparable)
                throw new Exception("Actual enchant-name search failed to return its trusted scroll: " + query);
            SameQuote(found, quote);
            report.PartialSearchVerified++;
            report.Examples.Add(new { query = query, name = found.Name, position = identity.Groups[2].Value, rank = identity.Groups[3].Value,
                scroll_base = identity.Groups[4].Value, unit_price = found.UnitPrice, listing_count = found.ListingCount,
                quantity = found.Quantity, quantity_known = found.QuantityKnown, price_comparable = found.PriceComparable,
                enchant_name_known = found.EnchantNameKnown, fetched_utc = found.FetchedUtc.Value.ToString("o") });
        }
        Console.WriteLine("PASS live enchant identities: named=" + report.NamedCount + ", verified=" + report.VerifiedCount
            + ", priced_quotes=" + report.PricedQuotes + ", actual_name_searches=" + report.PartialSearchVerified + ", generic_prices_suppressed=" + report.GenericCount);
        return report;
    }

    public static int Main(string[] args)
    {
        try {
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
                UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None };
            var client = new MarketSnapshotClient(new Uri("https://restless-bread-9002milesianledger-api.jang9610.workers.dev/"), Path.Combine(output, "live-snapshot-cache.json"), handler);
            var first = client.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (first.Data == null || first.ErrorMessage != null || !first.Downloaded || first.Requests != 2)
                throw new Exception("First public snapshot did not validate: " + first.ErrorMessage + "; requests=" + first.Requests);
            Console.WriteLine("PASS first public manifest + verified gzip: requests=" + first.Requests);
            string[] names = new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(args[1], Encoding.UTF8));
            int listed = 0, empty = 0, batchRequests;
            var settings = new AuctionSettings();
            using (var service = new AuctionService(Path.Combine(output, "auction"), settings, client)) {
                var refreshed = service.RefreshAsync(names, null, CancellationToken.None).GetAwaiter().GetResult();
                batchRequests = refreshed.Requests;
                if (refreshed.UpdatedMaterials != names.Length || refreshed.FailedMaterials != 0 || refreshed.Cancelled)
                    throw new Exception("Whole supported quote batch failed: updated=" + refreshed.UpdatedMaterials + ", failed=" + refreshed.FailedMaterials + ", reason=" + refreshed.StoppedReason);
                if (client.CachedData.Version == first.Data.Version && batchRequests != 1)
                    throw new Exception("Unchanged common snapshot was downloaded again during whole-item refresh.");
                foreach (string name in names) {
                    var actual = service.GetQuote(name); MarketSnapshotQuote expected;
                    if (actual == null) throw new Exception("Refreshed quote missing for supported item.");
                    if (client.CachedData.Quotes.TryGetValue(settings.ResolveName(name), out expected)) {
                        if (actual.UnitPrice != expected.UnitPrice || actual.PriceUtc != expected.FetchedUtc || actual.ListingCount != expected.ListingCount
                            || actual.AvailableQuantity != expected.Quantity || !actual.Complete) throw new Exception("Snapshot/source quote mismatch.");
                        listed++;
                    } else {
                        if (!client.CachedData.ListingsFetchedUtc.HasValue || actual.UnitPrice.HasValue || actual.Status != "empty"
                            || actual.AvailableQuantity != 0 || actual.ListingCount != 0 || actual.PriceUtc != client.CachedData.ListingsFetchedUtc.Value)
                            throw new Exception("Absent item is not represented as verified empty from completed listings.");
                        empty++;
                    }
                }
                Console.WriteLine("PASS whole-item AuctionService refresh: items=" + names.Length + ", updated=" + refreshed.UpdatedMaterials + ", failed=" + refreshed.FailedMaterials + ", requests=" + batchRequests);
            }
            Console.WriteLine("PASS snapshot price/source-time/count comparison: listed=" + listed + ", verified_empty=" + empty);
            var search = new MarketSearchIndex(client.CachedData);
            int searchedQuotes = 0;
            foreach (var quote in client.CachedData.Quotes.Values.Where(q => q.UnitPrice.HasValue && q.ListingCount > 0)) {
                var found = search.Search(quote.Name, 1);
                if (found.Count != 1 || found[0].Name != quote.Name || found[0].UnitPrice != quote.UnitPrice || found[0].ListingCount != quote.ListingCount
                    || found[0].Quantity != quote.Quantity || found[0].QuantityKnown != quote.QuantityKnown || found[0].FetchedUtc != quote.FetchedUtc)
                    throw new Exception("PIP exact-name search differs from the shared quote: " + quote.Name);
                searchedQuotes++;
            }
            if (searchedQuotes <= names.Length) throw new Exception("PIP live fixture does not contain items beyond barter materials.");
            Console.WriteLine("PASS PIP search against every priced market quote: " + searchedQuotes + " names; zero extra HTTP requests");
            var enchants = VerifyEnchantScrolls(client.CachedData, search);
            foreach (var item in client.CachedData.Items24h.Concat(client.CachedData.Items7d)) {
                bool comparable = MarketPricePolicy.IsComparable(item.Name, item.Category);
                if (item.PriceComparable != comparable || (!comparable && item.AverageSalePrice.HasValue))
                    throw new Exception("Live item did not apply the variable-category average policy: " + item.Name);
                if (comparable && item.SoldQuantity > 0 && item.TradeCount > 0 && item.TradedGold >= 0 && !item.AverageSalePrice.HasValue)
                    throw new Exception("Live fixed-category observed average is still hidden: " + item.Name);
            }
            int averageCount = client.CachedData.Items24h.Count(item => item.AverageSalePrice.HasValue);
            int variableCount = client.CachedData.Items24h.Count(item => MarketPricePolicy.IsVariableOptionCategory(item.Category));
            Console.WriteLine("PASS live category-average policy: 24h visible averages=" + averageCount + ", variable-option items=" + variableCount);
            var report = new { verified_at = DateTime.UtcNow.ToString("o"), first_requests = first.Requests, whole_item_requests = batchRequests,
                comparable_average_items24h = averageCount, variable_option_items24h = variableCount,
                pip_search_names = search.Count, pip_verified_quotes = searchedQuotes,
                named_enchant_count = enchants.NamedCount, named_enchant_verified_count = enchants.VerifiedCount,
                named_enchant_priced_quotes = enchants.PricedQuotes, named_enchant_partial_search_verified = enchants.PartialSearchVerified,
                generic_scroll_verified_count = enchants.GenericCount, named_enchant_examples = enchants.Examples,
                names = names.Length, listed = listed, verified_empty = empty, version = client.CachedData.Version,
                snapshot_generated_utc = client.CachedData.GeneratedUtc.ToString("o"), items24h = client.CachedData.Items24h.Count,
                items7d = client.CachedData.Items7d.Count, quotes = client.CachedData.Quotes.Count };
            File.WriteAllText(Path.Combine(output, "report.json"), new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
            Console.WriteLine("items24h=" + first.Data.Items24h.Count + ", items7d=" + first.Data.Items7d.Count + ", quotes=" + first.Data.Quotes.Count);
            Console.WriteLine("unknown_listing_items=" + first.Data.Items24h.Count(item => !item.ListedQuantity.HasValue) + ", completed_listings=" + first.Data.ListingsFetchedUtc.HasValue);
            Console.WriteLine("snapshot_generated_utc=" + first.Data.GeneratedUtc.ToString("o"));
            Console.WriteLine("version=" + first.Data.Version);
            Console.WriteLine("cache_bytes=" + new FileInfo(Path.Combine(output, "live-snapshot-cache.json")).Length);
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
