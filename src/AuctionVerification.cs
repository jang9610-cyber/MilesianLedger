using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    // Offline boundary tests. Every HTTP request is intercepted by a fake transport;
    // this suite neither needs nor reads a real Nexon API key.
    public static class AuctionVerification
    {
        private static int assertions;
        private static string runDirectory;
        private const string PrivateResponseDetail = "private-server-response-detail";

        public static AuctionService CreateUiProbe(string directory, out Func<int> requestCount)
        {
            var transport = new FakeTransport();
            transport.Handler = delegate(FakeCall call, CancellationToken token)
            {
                return Task.FromResult(new AuctionHttpResponse { StatusCode = 200, Body = Page(Listing(call.Name, 999, 100)) });
            };
            requestCount = delegate { return transport.Calls.Count; };
            return new AuctionService(directory, AuctionSettings.Load(Path.Combine(directory, "auction-settings.json")), transport, new FakeDelay());
        }

        public static string Run(string directory)
        {
            assertions = 0;
            runDirectory = Path.Combine(Path.GetFullPath(directory), "auction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            // WPF may have installed a dispatcher context before this suite runs.
            Task.Run(async delegate { await RunAsync(); }).GetAwaiter().GetResult();
            return String.Format(CultureInfo.InvariantCulture,
                "PASS auction: {0} assertions; offline fake HTTP only. Explicit refresh, exact-name/unit-price parsing, pagination, 500-request ceiling and legacy-settings migration, quota/error stops, cancellation, overlap, stale cache and URI encoding verified.", assertions);
        }

        private static async Task RunAsync()
        {
            VerifyParserAndUri();
            await VerifyManualRefreshAndPersistence();
            await VerifyPagination();
            await VerifyExpandedRequestBudget();
            await VerifyErrorsAndRetention();
            await VerifyCancellationAndOverlap();
            await VerifyFrozenSnapshot();
            VerifyExpiryAndMappings();
        }

        private static void VerifyParserAndUri()
        {
            var page = AuctionPage.Parse("{\"auction_item\":[" +
                Listing("가는 실", 10, 120) + "," + Listing("가는 실", 2, 75) + "," +
                Listing("가는 실뭉치", 99, 1) + "," + Listing("가는 실", 0, 1) + "," +
                Listing("가는 실", 1, -1) + ",{\"item_name\":\"가는 실\"}],\"next_cursor\":\"next & 한=값\"}", "가는 실");
            Require(page.UnitPrice == 75m, "API auction_price_per_unit is already a unit price; stack size must not divide it.");
            Require(page.Quantity == 12, "Only valid exact-name listings contribute available quantity.");
            Require(page.ListingCount == 2, "Near-name and malformed listings are excluded.");
            Require(page.NextCursor == "next & 한=값", "Opaque pagination cursor is preserved.");
            var empty = AuctionPage.Parse(Page(), "가는 실");
            Require(empty.UnitPrice == null && empty.Quantity == 0 && empty.ListingCount == 0, "An empty valid page represents no listings.");
            foreach (string malformed in new[] { "{", "{\"next_cursor\":null}", "{\"auction_item\":{}}" })
            {
                bool threw = false;
                try { AuctionPage.Parse(malformed, "가는 실"); }
                catch (InvalidDataException) { threw = true; }
                Require(threw, "Malformed or missing listing arrays must be errors, not empty markets.");
            }
            Uri uri = ProxyAuctionTransport.BuildUri(new AuctionProxyConfig("https://ledger.example.test/service"), "가는 실 & cursor=evil?", "한 & #/?=cursor");
            Require(uri.Scheme == "https" && uri.Host == "ledger.example.test", "Requests use the configured HTTPS proxy host.");
            Require(uri.AbsolutePath == "/service/v1/auction/list", "Manual prices preserve the proxy path prefix.");
            var query = Query(uri);
            Require(query.Count == 2 && query["item_name"] == "가는 실 & cursor=evil?" && query["cursor"] == "한 & #/?=cursor", "Names and cursors cannot inject additional query parameters.");
            Require(!uri.AbsoluteUri.Contains(PrivateResponseDetail), "Private server details never enter a URL.");
            var firstUri = ProxyAuctionTransport.BuildUri(new AuctionProxyConfig("https://ledger.example.test/service"), "굵은 실", null);
            var initialQuery = Query(firstUri);
            Require(!initialQuery.ContainsKey("cursor") || String.IsNullOrEmpty(initialQuery["cursor"]), "Initial requests have no nonempty cursor.");
        }

        private static async Task VerifyManualRefreshAndPersistence()
        {
            string dir = Folder("manual");
            var settings = new AuctionSettings();
            settings.NameMappings["가는실"] = "가는 실";
            var transport = new FakeTransport();
            var delay = new FakeDelay();
            var service = new AuctionService(dir, settings, transport, delay);
            Require(transport.Calls.Count == 0 && !service.IsRefreshing, "Construction and disk loading never request the network.");
            Require(service.GetQuote("가는실") == null && service.GetQuote("unknown") == null, "Missing cached prices remain missing.");
            settings.Save(Path.Combine(dir, "auction-settings.json"));
            var reloadedSettings = AuctionSettings.Load(Path.Combine(dir, "auction-settings.json"));
            Require(reloadedSettings.ResolveName("가는실") == "가는 실", "Explicit search-name mappings survive local saves.");
            Require(transport.Calls.Count == 0, "Price reads and configuration saves never refresh.");
            await service.RefreshAsync(new string[0], null, CancellationToken.None);
            Require(transport.Calls.Count == 0, "An empty refresh snapshot cannot send HTTP requests.");
            transport.Enqueue(200, Page(Listing("가는 실", 10, 120)));
            transport.Enqueue(200, Page(Listing("굵은 실", 5, 400)));
            var result = await service.RefreshAsync(new[] { "가는실", "가는실", "굵은 실" }, null, CancellationToken.None);
            Require(transport.Calls.Count == 2 && result.Requests == 2, "The explicit refresh deduplicates repeated materials.");
            Require(transport.Calls.Select(c => c.Name).OrderBy(x => x).SequenceEqual(new[] { "가는 실", "굵은 실" }), "Configured names are used in the requested snapshot.");
            Require(!service.IsRefreshing, "Completed refresh releases the busy gate.");
            var quote = service.GetQuote("가는실");
            Require(quote != null && quote.UnitPrice == 120m && quote.AvailableQuantity == 10 && quote.Complete && quote.Status == "ok", "Successful exact-name prices enter the cache.");
            Require(quote.PriceUtc.HasValue && quote.AttemptUtc != default(DateTime), "Cached prices record source-query and attempt times.");
            quote.UnitPrice = 1;
            Require(service.GetQuote("가는실").UnitPrice == 120m, "Readers cannot mutate the cache through returned quote objects.");
            for (int i = 0; i < 5; i++) service.GetQuote("가는실");
            Require(transport.Calls.Count == 2, "Repeated screen reads never refresh a cached price.");
            Require(delay.Waits.Count > 0 && delay.Waits.All(ms => ms >= 200), "Network requests are paced below the development per-second limit.");
            var diskTransport = new FakeTransport();
            var diskService = new AuctionService(dir, settings, diskTransport, new FakeDelay());
            Require(diskService.GetQuote("가는실").UnitPrice == 120m && diskTransport.Calls.Count == 0, "Application restart restores prices without fetching them.");
            string saved = File.ReadAllText(Path.Combine(dir, "auction-cache.json"), Encoding.UTF8);
            Require(!saved.Contains(PrivateResponseDetail), "The price-cache file contains no private server response detail.");
            transport.Enqueue(200, Page(Listing("가는 실", 1, 150)));
            await service.RefreshAsync(new[] { "가는실" }, null, CancellationToken.None);
            Require(transport.Calls.Count == 3 && service.GetQuote("가는실").UnitPrice == 150m, "Only a second explicit refresh updates the previous price.");
        }

        private static async Task VerifyPagination()
        {
            var transport = new FakeTransport();
            var service = NewService("pages", new AuctionSettings(), transport, new FakeDelay());
            transport.Enqueue(200, PageWithCursor("cursor & 한=2", Listing("가는 실", 10, 100)));
            transport.Enqueue(200, Page(Listing("가는 실", 5, 80), Listing("굵은 실", 99, 1)));
            var result = await service.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            var quote = service.GetQuote("가는 실");
            Require(transport.Calls.Count == 2 && String.IsNullOrEmpty(transport.Calls[0].Cursor) && transport.Calls[1].Cursor == "cursor & 한=2", "Only server cursors advance pagination.");
            Require(quote.UnitPrice == 80m && quote.AvailableQuantity == 15 && quote.ListingCount == 2 && quote.Pages == 2 && quote.Complete, "All completed pages contribute to the exact-name market minimum.");
            Require(result.Requests == 2, "Request accounting includes every page.");

            var cappedTransport = new FakeTransport();
            var capped = NewService("page-cap", new AuctionSettings { MaxPagesPerItem = 1 }, cappedTransport, new FakeDelay());
            cappedTransport.Enqueue(200, PageWithCursor("more", Listing("가는 실", 1, 100)));
            await capped.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            var partial = capped.GetQuote("가는 실");
            Require(cappedTransport.Calls.Count == 1 && partial.UnitPrice == 100m && partial.Status == "partial" && !partial.Complete, "Page caps retain an explicitly partial price without extra requests.");

            var repeatedTransport = new FakeTransport();
            var repeated = NewService("repeat-cursor", new AuctionSettings(), repeatedTransport, new FakeDelay());
            repeatedTransport.Enqueue(200, PageWithCursor("same", Listing("가는 실", 1, 100)));
            repeatedTransport.Enqueue(200, PageWithCursor("same", Listing("가는 실", 1, 80)));
            await repeated.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            Require(repeatedTransport.Calls.Count == 2 && repeated.GetQuote("가는 실").Status == "partial" && !repeated.GetQuote("가는 실").Complete, "Repeated cursors stop safely instead of looping or claiming completion.");

            var quotaTransport = new FakeTransport();
            var quota = NewService("request-cap", new AuctionSettings { MaxRequestsPerRefresh = 1 }, quotaTransport, new FakeDelay());
            quotaTransport.Enqueue(200, Page(Listing("가는 실", 1, 100)));
            var quotaResult = await quota.RefreshAsync(new[] { "가는 실", "굵은 실" }, null, CancellationToken.None);
            Require(quotaTransport.Calls.Count == 1 && quotaResult.Requests == 1, "The per-click request cap covers the entire batch.");
            Require(quota.GetQuote("굵은 실") == null || quota.GetQuote("굵은 실").Status == "skipped", "Budget-limited items cannot be represented as empty successful markets.");

            var aliases = new AuctionSettings { MaxRequestsPerRefresh = 1 };
            aliases.NameMappings["가는실"] = "가는 실";
            var aliasTransport = new FakeTransport();
            var aliasService = NewService("alias-cap", aliases, aliasTransport, new FakeDelay());
            aliasTransport.Enqueue(200, Page(Listing("가는 실", 10, 100)));
            await aliasService.RefreshAsync(new[] { "가는실", "가는 실" }, null, CancellationToken.None);
            Require(aliasTransport.Calls.Count == 1 && aliasService.GetQuote("가는실").UnitPrice == 100m && aliasService.GetQuote("가는 실").UnitPrice == 100m, "Aliases with the same query reuse one response even at the request cap.");
        }

        private static async Task VerifyExpandedRequestBudget()
        {
            var defaults = new AuctionSettings();
            Require(defaults.MaxRequestsPerRefresh == 500 && AuctionSettings.RequestLimit == 500, "New installations default to a hard ceiling of 500 requests per click.");
            string settingsPath = Path.Combine(Folder("settings-migration"), "auction-settings.json");
            File.WriteAllText(settingsPath, "{\"MaxRequestsPerRefresh\":100,\"MaxPagesPerItem\":10,\"NameMappings\":{\"가는실\":\"가는 실뭉치\"}}", Encoding.UTF8);
            var migrated = AuctionSettings.Load(settingsPath);
            Require(migrated.MaxRequestsPerRefresh == 500 && migrated.MaxPagesPerItem == 10 && migrated.ResolveName("가는실") == "가는 실뭉치", "A saved legacy 100-request setting upgrades to 500 while retaining page limits and name mappings.");
            migrated.Save(settingsPath);
            Require(AuctionSettings.Load(settingsPath).MaxRequestsPerRefresh == 500, "The migrated budget survives settings saves and reloads.");
            foreach (int requested in new[] { 1, 25, 99, 101, 500, 900 })
            {
                File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(new AuctionSettings { MaxRequestsPerRefresh = requested }), Encoding.UTF8);
                int expected = Math.Min(requested, 500);
                Require(AuctionSettings.Load(settingsPath).MaxRequestsPerRefresh == expected, "Deliberately lower budgets remain unchanged and values above 500 are clamped.");
            }
            var transport = new FakeTransport();
            transport.Handler = delegate(FakeCall call, CancellationToken token) {
                return Task.FromResult(new AuctionHttpResponse { StatusCode = 200, Body = Page(Listing(call.Name, 2, 100)) });
            };
            var delay = new FakeDelay();
            var service = NewService("over-one-hundred", migrated, transport, delay);
            string[] materials = Enumerable.Range(1, 103).Select(i => "재료 " + i.ToString("D3", CultureInfo.InvariantCulture)).ToArray();
            var result = await service.RefreshAsync(materials, null, CancellationToken.None);
            Require(result.Requests == 103 && transport.Calls.Count == 103 && result.UpdatedMaterials == 103 && result.FailedMaterials == 0, "A 103-item selection completes in one manual refresh instead of stopping after 100 requests.");
            Require(result.StoppedReason == "" && materials.All(name => service.GetQuote(name).Complete && service.GetQuote(name).UnitPrice == 100m), "All selected items, including the former last three, receive complete cached quotes.");
            Require(transport.Calls.Count == 103 && delay.Waits.Count == 102 && delay.Waits.All(ms => ms == 250), "Completion stops immediately instead of filling the 500-request budget; reads remain offline and pacing is unchanged.");

            var cappedTransport = new FakeTransport();
            cappedTransport.Handler = transport.Handler;
            var capped = NewService("hard-five-hundred", new AuctionSettings { MaxRequestsPerRefresh = 900 }, cappedTransport, new FakeDelay());
            string[] many = Enumerable.Range(1, 501).Select(i => "검증 재료 " + i).ToArray();
            var cappedResult = await capped.RefreshAsync(many, null, CancellationToken.None);
            Require(cappedResult.Requests == 500 && cappedTransport.Calls.Count == 500 && cappedResult.UpdatedMaterials == 500 && cappedResult.FailedMaterials == 1, "The runtime enforces the 500-request hard ceiling even for unnormalized settings.");
            Require(capped.GetQuote(many[500]).Status == "skipped" && !String.IsNullOrEmpty(cappedResult.StoppedReason), "An item beyond the 500-request ceiling remains explicitly unrefreshed.");
            Require(cappedTransport.Calls.Select(call => call.Name).Distinct(StringComparer.Ordinal).Count() == 500, "The expanded budget introduces no duplicate item requests or automatic continuation.");

            var pagedTransport = new FakeTransport();
            pagedTransport.Handler = delegate(FakeCall call, CancellationToken token) {
                return Task.FromResult(new AuctionHttpResponse { StatusCode = 200, Body = PageWithCursor("page-" + pagedTransport.Calls.Count, Listing(call.Name, 1, 100)) });
            };
            var paged = NewService("five-hundred-pages", new AuctionSettings(), pagedTransport, new FakeDelay());
            var pagedResult = await paged.RefreshAsync(many.Take(51), null, CancellationToken.None);
            Require(pagedResult.Requests == 500 && pagedTransport.Calls.Count == 500 && pagedResult.UpdatedMaterials == 50, "The 500-request budget includes pagination, not only distinct item names.");
            Require(paged.GetQuote(many[0]).Pages == 10 && paged.GetQuote(many[0]).Status == "partial" && paged.GetQuote(many[50]).Status == "skipped", "The existing ten-page per-item cap and explicit partial/skipped states remain intact.");
        }

        private static async Task VerifyErrorsAndRetention()
        {
            foreach (int status in new[] { 401, 403, 429, 500 })
            {
                var transport = new FakeTransport();
                var service = NewService("http-" + status, new AuctionSettings(), transport, new FakeDelay());
                transport.Enqueue(200, Page(Listing("가는 실", 3, 100)));
                await service.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
                var old = service.GetQuote("가는 실");
                transport.Enqueue(status, "{\"error\":{\"name\":\"TEST\",\"message\":\"" + PrivateResponseDetail + "\"}}");
                var failed = await service.RefreshAsync(new[] { "가는 실", "굵은 실" }, null, CancellationToken.None);
                var quote = service.GetQuote("가는 실");
                Require(transport.Calls.Count == 2 && failed.Requests == 1, "HTTP " + status + " stops the batch without a retry or a next material request.");
                Require(quote.UnitPrice == 100m && quote.PriceUtc == old.PriceUtc && quote.Status == "error", "HTTP " + status + " preserves the prior successful price and its timestamp as stale.");
                Require(!String.IsNullOrEmpty(failed.StoppedReason), "HTTP " + status + " exposes a stop reason.");
                Require(!(quote.Message ?? "").Contains(PrivateResponseDetail) && !(failed.StoppedReason ?? "").Contains(PrivateResponseDetail), "Raw private server response text must never appear in errors.");
                Require(!service.IsRefreshing, "HTTP failures release the refresh gate.");
            }

            var invalidTransport = new FakeTransport();
            var invalid = NewService("bad-json", new AuctionSettings(), invalidTransport, new FakeDelay());
            invalidTransport.Enqueue(200, Page(Listing("가는 실", 2, 70)));
            await invalid.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            invalidTransport.Enqueue(200, "{\"wrong_shape\":[]}");
            await invalid.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            Require(invalid.GetQuote("가는 실").UnitPrice == 70m && invalid.GetQuote("가는 실").Status == "error", "A changed response schema cannot erase a known price as if no listings exist.");
            invalidTransport.Enqueue(200, Page());
            await invalid.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            var empty = invalid.GetQuote("가는 실");
            Require(empty.UnitPrice == null && empty.AvailableQuantity == 0 && empty.Complete && empty.Status == "empty", "A valid empty market deliberately clears a formerly successful price.");

            var localErrorTransport = new FakeTransport();
            var localError = NewService("item-error", new AuctionSettings(), localErrorTransport, new FakeDelay());
            localErrorTransport.Enqueue(400, "invalid item name");
            localErrorTransport.Enqueue(200, Page(Listing("굵은 실", 2, 90)));
            var itemError = await localError.RefreshAsync(new[] { "가는 실", "굵은 실" }, null, CancellationToken.None);
            Require(localErrorTransport.Calls.Count == 2 && itemError.FailedMaterials == 1 && itemError.UpdatedMaterials == 1, "An item-specific bad request is not retried and does not discard unrelated material results.");
            Require(localError.GetQuote("가는 실").Status == "error" && localError.GetQuote("굵은 실").UnitPrice == 90m, "Item-specific errors are distinct from no-listing results.");

            var exceptionTransport = new FakeTransport();
            exceptionTransport.Handler = delegate(FakeCall call, CancellationToken token) { throw new IOException("Sensitive transport detail " + PrivateResponseDetail); };
            var exceptionService = NewService("transport-error", new AuctionSettings(), exceptionTransport, new FakeDelay());
            await exceptionService.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            Require(exceptionTransport.Calls.Count == 1 && exceptionService.GetQuote("가는 실").Status == "error", "Transport errors have no automatic retry.");
            Require(!exceptionService.GetQuote("가는 실").Message.Contains(PrivateResponseDetail), "Raw transport exception details never enter the visible error cache.");

            var authorizationTransport = new FakeTransport();
            var authorizationService = NewService("proxy-authorization", new AuctionSettings(), authorizationTransport, new FakeDelay());
            authorizationTransport.Enqueue(400, "{\"error\":{\"code\":\"PROXY_UPSTREAM_AUTH\",\"message\":\"Private detail " + PrivateResponseDetail + "\"}}");
            var authorizationResult = await authorizationService.RefreshAsync(new[] { "가는 실", "굵은 실" }, null, CancellationToken.None);
            Require(authorizationTransport.Calls.Count == 1 && authorizationResult.Requests == 1 && !String.IsNullOrEmpty(authorizationResult.StoppedReason), "Proxy authorization error codes stop the entire batch even with an unexpected HTTP status.");
            Require(!(authorizationResult.StoppedReason ?? "").Contains(PrivateResponseDetail) && !authorizationService.GetQuote("가는 실").Message.Contains(PrivateResponseDetail), "Documented API error messages are mapped safely without exposing response details.");

            var aliasSettings = new AuctionSettings();
            aliasSettings.NameMappings["가는실"] = "가는 실";
            var failedAliasTransport = new FakeTransport();
            var failedAlias = NewService("failed-alias", aliasSettings, failedAliasTransport, new FakeDelay());
            failedAliasTransport.Enqueue(400, "{\"error\":{\"code\":\"PROXY_INVALID_REQUEST\",\"message\":\"Bad parameter\"}}");
            var aliasFailure = await failedAlias.RefreshAsync(new[] { "가는실", "가는 실" }, null, CancellationToken.None);
            Require(failedAliasTransport.Calls.Count == 1 && aliasFailure.FailedMaterials == 2, "Two aliases for a failed search share the failure instead of retrying the same query.");
            Require(failedAlias.GetQuote("가는실").Status == "error" && failedAlias.GetQuote("가는 실").Status == "error", "Failed aliases each retain an explicit failure state.");
        }

        private static async Task VerifyCancellationAndOverlap()
        {
            var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            var beforeTransport = new FakeTransport();
            var before = NewService("cancel-before", new AuctionSettings(), beforeTransport, new FakeDelay());
            try { await before.RefreshAsync(new[] { "가는 실" }, null, alreadyCancelled.Token); }
            catch (OperationCanceledException) { }
            Require(beforeTransport.Calls.Count == 0 && !before.IsRefreshing, "Pre-cancelled refresh sends no request and cannot leave the service busy.");

            var cancellation = new CancellationTokenSource();
            var cancelledTransport = new FakeTransport();
            var cancelledDelay = new FakeDelay();
            var during = NewService("cancel-pages", new AuctionSettings(), cancelledTransport, cancelledDelay);
            cancelledTransport.Handler = delegate(FakeCall call, CancellationToken token)
            {
                cancellation.Cancel();
                return Task.FromResult(new AuctionHttpResponse { StatusCode = 200, Body = PageWithCursor("next", Listing("가는 실", 1, 55)) });
            };
            try { await during.RefreshAsync(new[] { "가는 실", "굵은 실" }, null, cancellation.Token); }
            catch (OperationCanceledException) { }
            Require(cancelledTransport.Calls.Count == 1 && !during.IsRefreshing, "Cancellation prevents the next page and the next material.");
            var cancelled = during.GetQuote("가는 실");
            Require(cancelled == null || !cancelled.Complete || cancelled.Status == "cancelled", "A cancelled page walk must not present a complete current market price.");

            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<AuctionHttpResponse>();
            var busyTransport = new FakeTransport();
            busyTransport.Handler = delegate(FakeCall call, CancellationToken token) { entered.SetResult(true); return release.Task; };
            var busy = NewService("overlap", new AuctionSettings(), busyTransport, new FakeDelay());
            Task<AuctionRefreshResult> pending = busy.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            await entered.Task;
            Require(busy.IsRefreshing, "An unfinished HTTP request owns the refresh gate.");
            bool rejected = false;
            try { await busy.RefreshAsync(new[] { "굵은 실" }, null, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && busyTransport.Calls.Count == 1, "Double clicking cannot launch an overlapping batch.");
            release.SetResult(new AuctionHttpResponse { StatusCode = 200, Body = Page(Listing("가는 실", 1, 55)) });
            await pending;
            Require(!busy.IsRefreshing && busy.GetQuote("가는 실").UnitPrice == 55m, "The original refresh remains valid after an overlapping attempt is rejected.");

            var inflightTransport = new FakeTransport();
            var inflight = NewService("cancel-flight", new AuctionSettings(), inflightTransport, new FakeDelay());
            inflightTransport.Enqueue(200, Page(Listing("가는 실", 2, 77)));
            await inflight.RefreshAsync(new[] { "가는 실" }, null, CancellationToken.None);
            var previous = inflight.GetQuote("가는 실");
            var flightEntered = new TaskCompletionSource<bool>();
            var flightResponse = new TaskCompletionSource<AuctionHttpResponse>();
            var flightCancellation = new CancellationTokenSource();
            inflightTransport.Handler = delegate(FakeCall call, CancellationToken token)
            {
                token.Register(delegate { flightResponse.TrySetCanceled(); });
                flightEntered.SetResult(true);
                return flightResponse.Task;
            };
            var flightTask = inflight.RefreshAsync(new[] { "가는 실", "굵은 실" }, null, flightCancellation.Token);
            await flightEntered.Task;
            flightCancellation.Cancel();
            var cancelledResult = await flightTask;
            var stale = inflight.GetQuote("가는 실");
            Require(cancelledResult.Cancelled && cancelledResult.Requests == 1 && inflightTransport.Calls.Count == 2, "An in-flight cancellation terminates the current request without starting another material.");
            Require(stale.UnitPrice == 77m && stale.PriceUtc == previous.PriceUtc && stale.Status == "cancelled", "Cancellation keeps the previous successful quote explicitly stale.");
            Require(!inflight.IsRefreshing, "In-flight cancellation releases the busy gate.");
        }

        private static async Task VerifyFrozenSnapshot()
        {
            var settings = new AuctionSettings { MaxRequestsPerRefresh = 2 };
            settings.NameMappings["굵은실"] = "굵은 실";
            var materials = new List<string> { "가는 실", "굵은실" };
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<AuctionHttpResponse>();
            var transport = new FakeTransport();
            transport.Handler = delegate(FakeCall call, CancellationToken token)
            {
                if (transport.Calls.Count == 1) { entered.SetResult(true); return release.Task; }
                return Task.FromResult(new AuctionHttpResponse { StatusCode = 200, Body = Page(Listing(call.Name, 1, 80)) });
            };
            var service = NewService("snapshot", settings, transport, new FakeDelay());
            var pending = service.RefreshAsync(materials, null, CancellationToken.None);
            await entered.Task;
            materials.Clear(); materials.Add("새우");
            settings.NameMappings["굵은실"] = "마늘";
            settings.MaxRequestsPerRefresh = 1;
            Require(transport.Calls.Count == 1, "Changing local selection or aliases during a refresh cannot start another request.");
            release.SetResult(new AuctionHttpResponse { StatusCode = 200, Body = Page(Listing("가는 실", 1, 100)) });
            var result = await pending;
            Require(result.RequestedMaterials == 2 && transport.Calls.Count == 2 && transport.Calls[1].Name == "굵은 실", "A click freezes material selection, aliases and request budget for the whole refresh.");
            Require(service.GetQuote("굵은실") == null && service.GetQuote("새우") == null, "Updated aliases and later selections do not receive mismatched cached prices.");
            Require(transport.Calls.Count == 2, "Completing a refresh does not schedule another refresh for changed selection.");
        }

        private static void VerifyExpiryAndMappings()
        {
            string dir = Folder("expiry");
            var cache = new AuctionCache();
            cache.Quotes["가는 실"] = new AuctionQuote { Material = "가는 실", SearchName = "가는 실", UnitPrice = 100m, AvailableQuantity = 10, ListingCount = 1, Pages = 1, Complete = true, Status = "ok", PriceUtc = DateTime.UtcNow.AddDays(-31), AttemptUtc = DateTime.UtcNow.AddDays(-31) };
            cache.Quotes["굵은 실"] = new AuctionQuote { Material = "굵은 실", SearchName = "굵은 실", UnitPrice = 200m, AvailableQuantity = 10, ListingCount = 1, Pages = 1, Complete = true, Status = "ok", PriceUtc = DateTime.UtcNow.AddHours(-1), AttemptUtc = DateTime.UtcNow.AddHours(-1) };
            File.WriteAllText(Path.Combine(dir, "auction-cache.json"), new JavaScriptSerializer().Serialize(cache), new UTF8Encoding(false));
            var transport = new FakeTransport();
            var settings = new AuctionSettings();
            var service = new AuctionService(dir, settings, transport, new FakeDelay());
            Require(service.GetQuote("가는 실") == null, "Prices older than the retention limit are unavailable.");
            Require(service.GetQuote("굵은 실").UnitPrice == 200m, "A retained cached price is usable without a request.");
            settings.NameMappings["굵은 실"] = "다른 아이템";
            settings.Save(Path.Combine(dir, "auction-settings.json"));
            Require(service.GetQuote("굵은 실") == null, "Changing a name mapping immediately invalidates the mismatched cached quote.");
            Require(transport.Calls.Count == 0, "Expiry and mapping changes never trigger an automatic refresh.");
            string badDir = Folder("corrupt-cache");
            File.WriteAllText(Path.Combine(badDir, "auction-cache.json"), "broken cache", Encoding.UTF8);
            var bad = new AuctionService(badDir, new AuctionSettings(), transport, new FakeDelay());
            Require(bad.GetQuote("가는 실") == null && transport.Calls.Count == 0, "Unreadable local cache falls back to missing prices without network access.");
        }

        private static string Folder(string name) { string dir = Path.Combine(runDirectory, name); Directory.CreateDirectory(dir); return dir; }
        private static Dictionary<string, string> Query(Uri uri)
        {
            var result = new Dictionary<string, string>();
            foreach (string item in uri.Query.TrimStart('?').Split('&'))
            {
                string[] parts = item.Split(new[] { '=' }, 2);
                result.Add(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            return result;
        }
        private static AuctionService NewService(string name, AuctionSettings settings, FakeTransport transport, FakeDelay delay) { return new AuctionService(Folder(name), settings, transport, delay); }
        private static string Listing(string name, int count, decimal price) { return new JavaScriptSerializer().Serialize(new { item_name = name, item_count = count, auction_price_per_unit = price }); }
        private static string Page(params string[] listings) { return "{\"auction_item\":[" + String.Join(",", listings) + "],\"next_cursor\":null}"; }
        private static string PageWithCursor(string cursor, params string[] listings) { return "{\"auction_item\":[" + String.Join(",", listings) + "],\"next_cursor\":" + new JavaScriptSerializer().Serialize(cursor) + "}"; }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception("Auction verification: " + message); }

        private sealed class FakeCall { public string Name; public string Cursor; }
        private sealed class FakeTransport : IAuctionTransport
        {
            public readonly List<FakeCall> Calls = new List<FakeCall>();
            private readonly Queue<AuctionHttpResponse> responses = new Queue<AuctionHttpResponse>();
            public Func<FakeCall, CancellationToken, Task<AuctionHttpResponse>> Handler;
            public void Enqueue(int status, string body) { responses.Enqueue(new AuctionHttpResponse { StatusCode = status, Body = body }); }
            public Task<AuctionHttpResponse> GetAsync(string itemName, string cursor, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var call = new FakeCall { Name = itemName, Cursor = cursor };
                Calls.Add(call);
                if (Handler != null) return Handler(call, token);
                if (responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request in offline verification.");
                return Task.FromResult(responses.Dequeue());
            }
        }
        private sealed class FakeDelay : IAuctionDelay
        {
            public readonly List<int> Waits = new List<int>();
            public Task WaitAsync(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); Waits.Add(milliseconds); return Task.FromResult(true); }
        }
    }
}
