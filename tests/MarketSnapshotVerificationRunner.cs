using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MabinogiBarter;

public static class MarketSnapshotVerificationRunner
{
    static int passed;
    static string root;
    static readonly Uri Origin = new Uri("https://market.example/");
    sealed class Fixture {
        public byte[] Bytes; public string Manifest, Version, Stamp;
        public Fixture(int quantity, bool complete) {
            Stamp = DateTime.UtcNow.AddMinutes(-20).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var rows = Enumerable.Range(0, 120).Select(i => new { name = "재료" + i, category = "재료", sold_quantity = quantity,
                trade_count = 2, traded_gold = complete ? (int?)200 : null, listed_quantity = complete ? (int?)4 : null, listing_count = complete ? (int?)2 : null,
                average_sale_price = (decimal?)50, lowest_listing_price = (decimal?)40, price_comparable = true, image_url = (string)null }).ToArray();
            var quotes = Enumerable.Range(0, 120).Select(i => new { name = "재료" + i, unit_price = 40, listing_count = 2, quantity = 4, fetched_at = Stamp }).ToArray();
            string text = new JavaScriptSerializer { MaxJsonLength = 2000000 }.Serialize(new {
                schema_version = 1, generated_at = Stamp, items_24h = rows, items_7d = rows, quotes = quotes,
                status = new { listings = new { published = complete ? new { state = "complete", finished_at = Stamp } : null }, history = new { published = new { state = "complete", finished_at = Stamp } } }
            });
            SetBytes(Encoding.UTF8.GetBytes(text));
        }
        public void ExpireListings() {
            Dictionary<string, object> data;
            using (var stream = new MemoryStream(Bytes))
            using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8))
                data = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 2000000 }.DeserializeObject(reader.ReadToEnd());
            data["listings_fetched_at"] = null;
            data["quotes"] = new object[0];
            var status = (Dictionary<string, object>)data["status"];
            var listings = (Dictionary<string, object>)status["listings"];
            listings["published"] = new { state = "complete", finished_at = DateTime.UtcNow.AddDays(-9).ToString("yyyy-MM-ddTHH:mm:ss.fffZ") };
            foreach (string key in new[] { "items_24h", "items_7d" }) foreach (var raw in (object[])data[key]) {
                var item = (Dictionary<string, object>)raw; item["listed_quantity"] = null; item["listing_count"] = null; item["lowest_listing_price"] = null;
            }
            SetBytes(Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = 2000000 }.Serialize(data)));
        }
        public void SetBytes(byte[] raw) {
            using (var output = new MemoryStream()) {
                using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(raw, 0, raw.Length);
                Bytes = output.ToArray();
            }
            Version = MarketSnapshotClient.Hash(Bytes);
            Manifest = new JavaScriptSerializer().Serialize(new { schema_version = 1, version = Version, generated_at = Stamp,
                snapshot_url = "/v1/market/snapshots/" + Version + ".json.gz", compressed_bytes = Bytes.Length,
                uncompressed_bytes = raw.Length, sha256 = Version, status = new { } });
        }
    }
    sealed class Handler : HttpMessageHandler {
        public Fixture Fixture; public int Calls, Artifacts; public bool Offline, Corrupt, InvalidUrl;
        public TaskCompletionSource<bool> Block;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Interlocked.Increment(ref Calls);
            if (Block != null) {
                var cancelled = new TaskCompletionSource<bool>();
                using (token.Register(() => cancelled.TrySetCanceled())) await await Task.WhenAny(Block.Task, cancelled.Task);
            }
            token.ThrowIfCancellationRequested();
            Check(request.RequestUri.Host == "market.example", "same origin");
            if (Offline) return Response(503, Encoding.UTF8.GetBytes("{}"));
            if (request.RequestUri.AbsolutePath == "/v1/market/manifest") {
                string etag = "\"" + Fixture.Version + "\"";
                if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == etag)) return Response(304, new byte[0]);
                string body = InvalidUrl ? Fixture.Manifest.Replace("/v1/market/snapshots/", "https://evil.example/v1/market/snapshots/") : Fixture.Manifest;
                return Response(200, Encoding.UTF8.GetBytes(body));
            }
            Check(request.RequestUri.AbsolutePath == "/v1/market/snapshots/" + Fixture.Version + ".json.gz", "exact immutable path");
            Interlocked.Increment(ref Artifacts);
            var bytes = (byte[])Fixture.Bytes.Clone(); if (Corrupt) bytes[0] ^= 1;
            return Response(200, bytes);
        }
        static HttpResponseMessage Response(int code, byte[] body) { return new HttpResponseMessage((HttpStatusCode)code) { Content = new ByteArrayContent(body) }; }
    }
    static MarketSnapshotClient Client(string name, Handler handler) { return new MarketSnapshotClient(Origin, Path.Combine(root, name + ".json"), handler); }
    static void Check(bool value, string what) { if (!value) throw new Exception(what); }
    static void Pass(string what) { passed++; Console.WriteLine("PASS " + what); }
    static async Task Run() {
        var first = new Fixture(10, true); var h = new Handler { Fixture = first }; var client = Client("main", h);
        var data = await client.RefreshAsync(CancellationToken.None);
        Check(data.Downloaded && data.Requests == 2 && data.Data.Items24h.Count == 120 && data.Data.Quotes.Count == 120, "initial verified download");
        Pass("manifest + one gzip snapshot, parsed 120 quotes");
        var next = await client.RefreshAsync(CancellationToken.None);
        Check(next.UsedCached && !next.Downloaded && next.Requests == 1 && h.Artifacts == 1, "304 no download");
        Pass("conditional manifest 304 uses cached snapshot");
        var saved = File.ReadAllBytes(Path.Combine(root, "main.json"));
        var diskHandler = new Handler { Fixture = first, Offline = true };
        var diskClient = Client("main", diskHandler);
        Check(diskClient.CachedData == null, "fresh client begins without in-memory data");
        var diskReads = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => diskClient.ReadCachedData())));
        Check(diskReads.All(value => value != null && value.Version == first.Version && Object.ReferenceEquals(value, diskReads[0]))
            && Object.ReferenceEquals(diskClient.CachedData, diskReads[0]) && diskHandler.Calls == 0, "local concurrent cache restore made HTTP or lost data");
        Pass("fresh client restores verified disk snapshot with concurrent local reads and zero HTTP requests");
        File.WriteAllText(Path.Combine(root, "corrupt-disk.json"), "{ invalid persisted snapshot", Encoding.UTF8);
        var corruptDiskHandler = new Handler { Fixture = first, Offline = true };
        var corruptDiskClient = Client("corrupt-disk", corruptDiskHandler);
        Check(corruptDiskClient.ReadCachedData() == null && corruptDiskClient.ReadCachedData() == null && corruptDiskHandler.Calls == 0,
            "corrupt local cache caused HTTP or fabricated data");
        var missingDiskHandler = new Handler { Fixture = first, Offline = true };
        Check(Client("missing-disk", missingDiskHandler).ReadCachedData() == null && missingDiskHandler.Calls == 0, "missing local cache caused HTTP");
        Pass("corrupt and missing disk snapshots remain unavailable with zero HTTP requests");
        h.Fixture = new Fixture(20, true); h.Corrupt = true;
        var bad = await client.RefreshAsync(CancellationToken.None);
        Check(bad.Data.Version == first.Version && bad.ErrorMessage != null && saved.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "main.json"))), "corrupt preserved disk");
        Pass("corrupt gzip hash cannot replace prior in-memory/disk snapshot");
        h.Corrupt = false; h.InvalidUrl = true; int artifactCount = h.Artifacts;
        bad = await client.RefreshAsync(CancellationToken.None);
        Check(bad.ErrorMessage != null && h.Artifacts == artifactCount, "URL not followed");
        Pass("cross-origin manifest rejected before artifact request");
        var restarted = Client("main", new Handler { Fixture = first, Offline = true });
        var offline = await restarted.RefreshAsync(CancellationToken.None);
        Check(offline.UsedCached && offline.Data.Version == first.Version && offline.ErrorMessage != null, "restart fallback");
        Pass("offline restart validates and restores persisted snapshot");
        var empty = await Client("empty", new Handler { Fixture = first, Offline = true }).RefreshAsync(CancellationToken.None);
        Check(empty.Data == null && empty.ErrorMessage.Contains("아직"), "unavailable distinction");
        Pass("unpublished server gives explicit waiting state");
        var blocking = new Handler { Fixture = first, Block = new TaskCompletionSource<bool>() }; var shared = Client("shared", blocking);
        var one = shared.RefreshAsync(CancellationToken.None); var two = shared.RefreshAsync(CancellationToken.None);
        await Task.Delay(40); Check(blocking.Calls == 1, "single flight"); blocking.Block.TrySetResult(true);
        await Task.WhenAll(one, two); Check(blocking.Calls == 2 && blocking.Artifacts == 1 && Object.ReferenceEquals(one.Result.Data, two.Result.Data), "joined artifact");
        Pass("concurrent consumers share one manifest + artifact operation");
        var cancelHandler = new Handler { Fixture = first, Block = new TaskCompletionSource<bool>() }; var cancelClient = Client("cancel", cancelHandler);
        using (var cts = new CancellationTokenSource()) {
            var task = cancelClient.RefreshAsync(cts.Token); await Task.Delay(30); cts.Cancel();
            try { await task; throw new Exception("did not cancel"); } catch (OperationCanceledException) { }
            await Task.Delay(30); Check(!File.Exists(Path.Combine(root, "cancel.json")), "cancel saved data");
        }
        Pass("cancelled last consumer aborts request without cache publication");
        var isolationHandler = new Handler { Fixture = first, Block = new TaskCompletionSource<bool>() }; var isolation = Client("isolation", isolationHandler);
        using (var cts = new CancellationTokenSource()) {
            var leaving = isolation.RefreshAsync(cts.Token); var staying = isolation.RefreshAsync(CancellationToken.None); await Task.Delay(30); cts.Cancel();
            try { await leaving; } catch (OperationCanceledException) { }
            isolationHandler.Block.TrySetResult(true); Check((await staying).Downloaded && isolationHandler.Calls == 2, "one cancel killed other");
        }
        Pass("cancelling one consumer does not cancel another consumer");
        var huge = new Fixture(1, true); huge.SetBytes(new byte[16 * 1024 * 1024 + 1]);
        var bounded = new Handler { Fixture = huge }; var large = await Client("large", bounded).RefreshAsync(CancellationToken.None);
        Check(large.Data == null && bounded.Artifacts == 0, "oversized advertised payload");
        Pass("oversized declared decompressed data rejected before download");
        // Lie about the uncompressed size to exercise the streaming decompression bound.
        huge.Manifest = huge.Manifest.Replace("16777217", "100");
        large = await Client("bomb", bounded).RefreshAsync(CancellationToken.None);
        Check(large.Data == null && bounded.Artifacts == 1, "gzip bomb");
        Pass("bounded gzip rejects compressed expansion beyond 16 MiB");
        var batchHandler = new Handler { Fixture = first }; var batch = Client("batch", batchHandler);
        var settings = new AuctionSettings(); settings.NameMappings["별칭"] = "재료0";
        using (var service = new AuctionService(Path.Combine(root, "auction"), settings, batch)) {
            var all = await service.RefreshAsync(Enumerable.Range(0, 120).Select(i => "재료" + i).Concat(new[] { "별칭", "없는 재료" }), null, CancellationToken.None);
            Check(all.Requests == 2 && all.UpdatedMaterials == 122 && batchHandler.Artifacts == 1, "whole batch used per-item fetch");
            Check(service.GetQuote("별칭").UnitPrice == 40 && service.GetQuote("없는 재료").Status == "empty", "alias or complete absent");
            Check(service.GetQuote("재료0").PriceUtc == DateTime.Parse(first.Stamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal), "source timestamp lost");
            var purchase = await service.RefreshAsync(new[] { "재료3", "재료8" }, null, CancellationToken.None);
            Check(purchase.Requests == 1 && purchase.UpdatedMaterials == 2 && batchHandler.Artifacts == 1, "purchase redownloaded");
        }
        Pass("122-name whole/purchase batches share snapshot, preserve timestamps, resolve aliases and complete absent items");
        var incompleteHandler = new Handler { Fixture = new Fixture(1, false) }; var incomplete = Client("incomplete", incompleteHandler);
        var unknown = await incomplete.RefreshAsync(CancellationToken.None);
        Check(unknown.Data != null && unknown.Data.Items24h[0].ListedQuantity == null && unknown.Data.Items24h[0].TradedGold == null, "unknown values became zero or rejected snapshot");
        Pass("history-only and overflow-null statistics stay unknown, not zero");
        using (var service = new AuctionService(Path.Combine(root, "incomplete-auction"), new AuctionSettings(), incomplete)) {
            var result = await service.RefreshAsync(new[] { "없는 재료" }, null, CancellationToken.None);
            Check(result.UpdatedMaterials == 0 && result.FailedMaterials == 1 && service.GetQuote("없는 재료") == null, "incomplete inferred empty");
        }
        Pass("missing item is not zero supply without completed listing snapshot");
        var expiryHandler = new Handler { Fixture = first }; var expiryClient = Client("expiry", expiryHandler);
        using (var service = new AuctionService(Path.Combine(root, "expiry-auction"), new AuctionSettings(), expiryClient)) {
            var initial = await service.RefreshAsync(new[] { "재료0" }, null, CancellationToken.None);
            Check(initial.UpdatedMaterials == 1 && service.GetQuote("재료0").UnitPrice == 40, "expiry setup");
            var previous = service.GetQuote("재료0");
            var expiredFixture = new Fixture(20, true); expiredFixture.ExpireListings(); expiryHandler.Fixture = expiredFixture;
            var expiredResult = await service.RefreshAsync(new[] { "재료0" }, null, CancellationToken.None);
            Check(expiredResult.UpdatedMaterials == 0 && expiredResult.FailedMaterials == 1, "expired snapshot inferred zero supply");
            Check(!expiryClient.CachedData.ListingsFetchedUtc.HasValue && expiryClient.CachedData.Items24h[0].ListedQuantity == null, "published status overrode explicit null");
            Check(service.GetQuote("재료0").UnitPrice == previous.UnitPrice && service.GetQuote("재료0").PriceUtc == previous.PriceUtc
                && service.GetQuote("재료0").Status != "empty", "expired listing erased prior price");
        }
        Pass("explicit expired listing null overrides retained published status and preserves prior prices");
    }
    public static int Main(string[] args) {
        root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("Passed " + passed + " snapshot verification groups."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
