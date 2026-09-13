using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Text;
using System.Web.Script.Serialization;
using MabinogiBarter;

public static class MarketSnapshotLiveVerificationRunner
{
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
            var report = new { verified_at = DateTime.UtcNow.ToString("o"), first_requests = first.Requests, whole_item_requests = batchRequests,
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
