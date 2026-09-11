using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    // All transport checks use an in-process handler; no listening ports or upstream calls.
    public static class AuctionProxyVerification
    {
        private static int assertions;
        private static string runDirectory;
        public static string Run(string directory)
        {
            assertions = 0;
            runDirectory = Path.Combine(Path.GetFullPath(directory), "proxy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            Task.Run(async delegate { await RunAsync(); }).GetAwaiter().GetResult();
            return "PASS auction proxy: " + assertions + " assertions; configuration validation, no credentials, manual-only requests, no redirects/retries, source timestamps, cancellation and retained local cache; in-process HTTP only.";
        }
        private static async Task RunAsync()
        {
            VerifyAddresses();
            await VerifyUnconfigured();
            await VerifyTransportAndTimestamps();
            await VerifyCancellation();
            await VerifySafeErrors();
        }
        private static void VerifyAddresses()
        {
            foreach (string address in new[] { "", " ", "https://", "not a url", "ftp://ledger.example.test", "file:///c:/data", "http://ledger.example.test", "https://user:password@ledger.example.test", "https://ledger.example.test?key=private", "https://ledger.example.test?", "https://ledger.example.test#", "https://ledger.example.test/path#private", "https://open.api.nexon.com", "https://open.api.nexon.com./mabinogi", "https://NEXON.COM", "https://mabinogi.nexon.com", "https://api.nexon.co.kr", "https://ledger.example.test\n", " https://ledger.example.test", "https://ledger.example.test\\wrong" })
                Require(!new AuctionProxyConfig(address).IsConfigured, "Invalid, secret-bearing or provider addresses cannot enable requests: " + address);
            foreach (string address in new[] { "https://ledger.example.test", "https://ledger.example.test/reverse/proxy", "https://ledger.example.test/reverse/proxy/", "http://localhost:8787", "http://127.0.0.1:8787", "http://[::1]:8787" })
                Require(new AuctionProxyConfig(address).IsConfigured, "HTTPS public proxy and loopback development addresses are supported.");
            var uri = ProxyAuctionTransport.BuildUri(new AuctionProxyConfig("https://ledger.example.test/reverse/proxy"), "가는 실뭉치 &evil=1", "next &=?/#");
            Require(uri.AbsolutePath == "/reverse/proxy/v1/auction/list", "Configured path prefixes survive endpoint construction.");
            var parts = uri.Query.TrimStart('?').Split('&');
            Require(parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == "item_name=가는 실뭉치 &evil=1" && Uri.UnescapeDataString(parts[1]) == "cursor=next &=?/#", "Names and cursors remain two encoded query values.");
            var factory = typeof(ProxyAuctionTransport).GetMethod("CreateHandler", BindingFlags.Static | BindingFlags.NonPublic);
            using (var handler = (HttpClientHandler)factory.Invoke(null, null))
                Require(!handler.AllowAutoRedirect && !handler.UseCookies && !handler.UseDefaultCredentials && handler.Credentials == null, "Production transport does not redirect or attach cookies, Windows credentials or provider credentials.");
        }
        private static async Task VerifyUnconfigured()
        {
            string directory = Folder("configuration");
            string path = Path.Combine(directory, "auction-proxy.json");
            Require(!AuctionProxyConfig.Load(path).IsConfigured, "Missing deployment configuration remains launchable.");
            foreach (string body in new[] { "", "broken", "{}", "null", "{\"BaseUrl\":123}", "{\"BaseUrl\":null}", "{\"BaseUrl\":\"\"}", "{\"BaseUrl\":\"https://open.api.nexon.com\"}" })
            {
                File.WriteAllText(path, body, new UTF8Encoding(false));
                var handler = new RecordingHandler();
                using (var service = new AuctionService(directory, new AuctionSettings(), new ProxyAuctionTransport(AuctionProxyConfig.Load(path), handler), new NoDelay()))
                {
                    Require(!service.IsConfigured && !String.IsNullOrEmpty(service.ConfigurationMessage), "Malformed or blank deployment configuration has a local explanation.");
                    var result = await service.RefreshAsync(new[] { "가는 실뭉치" }, null, CancellationToken.None);
                    Require(result.Requests == 0 && handler.Calls.Count == 0 && !service.IsRefreshing && !String.IsNullOrEmpty(result.StoppedReason), "Unconfigured refresh never enters the network or busy state.");
                }
            }
            using (var production = new AuctionService(directory, new AuctionSettings()))
                Require(!production.IsConfigured && production.GetQuote("가는 실뭉치") == null, "Production constructor reads only local deployment config and cache.");
            var cache = new AuctionCache();
            DateTime original = DateTime.UtcNow.AddMinutes(-20);
            cache.Quotes["가는 실뭉치"] = new AuctionQuote { Material = "가는 실뭉치", SearchName = "가는 실뭉치", UnitPrice = 125, PriceUtc = original, AttemptUtc = original, Status = "ok", Complete = true };
            string cachePath = Path.Combine(directory, "auction-cache.json");
            File.WriteAllText(cachePath, new JavaScriptSerializer().Serialize(cache), new UTF8Encoding(false));
            byte[] before = File.ReadAllBytes(cachePath);
            using (var offline = new AuctionService(directory, new AuctionSettings()))
            {
                Require(offline.GetQuote("가는 실뭉치").UnitPrice == 125, "A pending proxy deployment still displays previous local prices.");
                await offline.RefreshAsync(new[] { "가는 실뭉치" }, null, CancellationToken.None);
                Require(before.SequenceEqual(File.ReadAllBytes(cachePath)), "An unavailable proxy cannot rewrite or erase the local cache.");
            }
        }
        private static async Task VerifyTransportAndTimestamps()
        {
            var handler = new RecordingHandler();
            DateTime older = DateTime.UtcNow.AddMinutes(-5), newer = DateTime.UtcNow.AddMinutes(-1);
            handler.Enqueue(200, Page(100, older, "second & cursor"));
            handler.Enqueue(200, Page(80, newer, null));
            string directory = Folder("transport");
            using (var service = Service(directory, handler))
            {
                Require(service.IsConfigured && handler.Calls.Count == 0, "Construction and configuration perform no HTTP.");
                service.GetQuote("가는 실뭉치");
                await service.RefreshAsync(new string[0], null, CancellationToken.None);
                Require(handler.Calls.Count == 0, "Cache reads and an empty explicit snapshot perform no HTTP.");
                var result = await service.RefreshAsync(new[] { "가는 실뭉치" }, null, CancellationToken.None);
                var quote = service.GetQuote("가는 실뭉치");
                Require(result.Requests == 2 && quote.UnitPrice == 80 && quote.Complete, "Explicit proxy pagination supplies unit prices.");
                Require(quote.PriceUtc == older && quote.AttemptUtc > newer, "A server TTL hit retains the oldest contributing source timestamp instead of reporting the button-click time.");
                Require(handler.Calls.All(c => c.Method == "GET" && c.Headers.SequenceEqual(new[] { "Accept" }) && c.Uri.Host == "ledger.example.test"), "Requests contain only public item/cursor queries and JSON Accept, with no authentication headers.");
                Require(handler.Calls.All(c => c.Uri.AbsolutePath == "/base/v1/auction/list" && c.Uri.Query.TrimStart('?').Split('&').Length == 2), "Every page remains on the configured proxy route.");
                handler.Enqueue(302, "private-redirect-detail", "https://untrusted.example.test/private");
                var redirected = await service.RefreshAsync(new[] { "가는 실뭉치", "다른 재료" }, null, CancellationToken.None);
                Require(redirected.Requests == 1 && handler.Calls.Count == 3 && !String.IsNullOrEmpty(redirected.StoppedReason), "Redirect responses stop the batch without following a location or trying another material.");
                Require(service.GetQuote("가는 실뭉치").PriceUtc == older && service.GetQuote("가는 실뭉치").UnitPrice == 80, "Redirect failures retain the existing source price.");
                for (int i = 0; i < 4; i++) service.GetQuote("가는 실뭉치");
                Require(handler.Calls.Count == 3, "Reading stale values after an error creates no automatic retry.");
            }
            DateTime before = DateTime.UtcNow;
            var nearFuture = AuctionPage.Parse(Page(100, before.AddMinutes(1), null), "가는 실뭉치");
            Require(nearFuture.FetchedUtc >= before && nearFuture.FetchedUtc <= DateTime.UtcNow, "Small clock skew is bounded so cached timestamps are never future values.");
            foreach (string stamp in new[] { "invalid", "2020-01-01T00:00:00Z", DateTime.UtcNow.AddMinutes(6).ToString("o"), "2026-09-01T00:00:00", "2026-09-01T00:00:00+09:00" })
            {
                bool rejected = false;
                try { AuctionPage.Parse(PageWithStamp(100, stamp, null), "가는 실뭉치"); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Invalid, expired, non-UTC and far-future source timestamps cannot make stale prices appear fresh.");
            }
            Require(!AuctionPage.Parse("{\"auction_item\":[],\"next_cursor\":null}", "가는 실뭉치").FetchedUtc.HasValue, "Legacy offline fixture payloads can omit source metadata.");
        }
        private static async Task VerifyCancellation()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(200, Page(70, DateTime.UtcNow.AddMinutes(-2), null));
            using (var service = Service(Folder("cancel"), handler))
            {
                var preCancelled = new CancellationTokenSource();
                preCancelled.Cancel();
                var before = await service.RefreshAsync(new[] { "가는 실뭉치" }, null, preCancelled.Token);
                Require(before.Cancelled && handler.Calls.Count == 0, "Pre-cancellation never sends HTTP.");
                await service.RefreshAsync(new[] { "가는 실뭉치" }, null, CancellationToken.None);
                DateTime? original = service.GetQuote("가는 실뭉치").PriceUtc;
                var entered = new TaskCompletionSource<bool>();
                handler.Handler = async delegate(HttpRequestMessage request, CancellationToken token) {
                    entered.SetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                };
                var cancellation = new CancellationTokenSource();
                Task<AuctionRefreshResult> pending = service.RefreshAsync(new[] { "가는 실뭉치", "다른 재료" }, null, cancellation.Token);
                await entered.Task;
                cancellation.Cancel();
                var result = await pending;
                Require(result.Cancelled && result.Requests == 1 && handler.Calls.Count == 2 && !service.IsRefreshing, "Cancelling the active HTTP request starts no next item and releases the busy state.");
                Require(service.GetQuote("가는 실뭉치").UnitPrice == 70 && service.GetQuote("가는 실뭉치").PriceUtc == original, "Proxy cancellation preserves prior prices and timestamps.");
            }
        }
        private static async Task VerifySafeErrors()
        {
            foreach (string code in new[] { "PROXY_NOT_CONFIGURED", "PROXY_RATE_LIMIT", "PROXY_QUOTA_EXCEEDED", "PROXY_QUOTA_UNAVAILABLE", "PROXY_BUSY", "PROXY_UPSTREAM_AUTH", "PROXY_UPSTREAM_LIMIT", "PROXY_UPSTREAM_ERROR", "PROXY_TIMEOUT", "PROXY_INVALID_RESPONSE", "UPSTREAM_UNAUTHORIZED", "UPSTREAM_UNAVAILABLE" })
            {
                var handler = new RecordingHandler();
                handler.Enqueue(400, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"private-provider-detail\"}}");
                string directory = Folder(code);
                using (var service = Service(directory, handler))
                {
                    var result = await service.RefreshAsync(new[] { "가는 실뭉치", "다른 재료" }, null, CancellationToken.None);
                    Require(result.Requests == 1 && handler.Calls.Count == 1 && !String.IsNullOrEmpty(result.StoppedReason), "Service-wide error " + code + " stops rather than retrying the batch.");
                    Require(!result.StoppedReason.Contains("private-provider-detail") && !File.ReadAllText(Path.Combine(directory, "auction-cache.json")).Contains("private-provider-detail"), "Server free-form messages never enter visible or persisted errors.");
                }
            }
        }
        private static AuctionService Service(string directory, RecordingHandler handler)
        {
            return new AuctionService(directory, new AuctionSettings(), new ProxyAuctionTransport(new AuctionProxyConfig("https://ledger.example.test/base"), handler), new NoDelay());
        }
        private static string Page(int price, DateTime stamp, string cursor) { return PageWithStamp(price, stamp.ToString("o", CultureInfo.InvariantCulture), cursor); }
        private static string PageWithStamp(int price, string stamp, string cursor)
        {
            return new JavaScriptSerializer().Serialize(new { auction_item = new[] { new { item_name = "가는 실뭉치", auction_price_per_unit = price, item_count = 10 } }, next_cursor = cursor, fetched_at = stamp });
        }
        private static string Folder(string name) { string directory = Path.Combine(runDirectory, name); Directory.CreateDirectory(directory); return directory; }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception("Auction proxy verification: " + message); }
        private sealed class RecordedCall { public Uri Uri; public string Method; public string[] Headers; }
        private sealed class RecordingHandler : HttpMessageHandler
        {
            public readonly List<RecordedCall> Calls = new List<RecordedCall>();
            private readonly Queue<HttpResponseMessage> responses = new Queue<HttpResponseMessage>();
            public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler;
            public void Enqueue(int status, string body) { Enqueue(status, body, null); }
            public void Enqueue(int status, string body, string location)
            {
                var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (location != null) response.Headers.Location = new Uri(location);
                responses.Enqueue(response);
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Calls.Add(new RecordedCall { Uri = request.RequestUri, Method = request.Method.Method, Headers = request.Headers.Select(h => h.Key).ToArray() });
                if (Handler != null) return Handler(request, token);
                if (responses.Count == 0) throw new InvalidOperationException("Unexpected request in offline proxy verification.");
                return Task.FromResult(responses.Dequeue());
            }
        }
        private sealed class NoDelay : IAuctionDelay
        {
            public Task WaitAsync(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(true); }
        }
    }
}
