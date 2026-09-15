using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public sealed class MarketSnapshotItem
    {
        public string Name, Category;
        public long? SoldQuantity, TradeCount, ListedQuantity, ListingCount;
        public decimal? TradedGold;
        public decimal? AverageSalePrice, LowestListingPrice;
        public bool PriceComparable;
    }
    public sealed class MarketSnapshotQuote
    {
        public string Name;
        public decimal? UnitPrice;
        public int ListingCount;
        public long Quantity;
        public bool QuantityKnown = true;
        public DateTime FetchedUtc;
    }
    public sealed class MarketSnapshotData
    {
        public string Version;
        public DateTime GeneratedUtc;
        public DateTime? ListingsFetchedUtc;
        public List<MarketSnapshotItem> Items24h, Items7d;
        public Dictionary<string, MarketSnapshotQuote> Quotes;
        public IDictionary<string, object> Status;
    }
    public sealed class MarketSnapshotResult
    {
        public MarketSnapshotData Data;
        public string ErrorMessage;
        public bool UsedCached, Downloaded;
        public int Requests;
    }

    // One shared download per origin; filtering and paging use Data without network calls.
    public sealed class MarketSnapshotClient
    {
        const int CompressedLimit = 8 * 1024 * 1024, PlainLimit = 16 * 1024 * 1024;
        static readonly object InstancesLock = new object();
        static readonly Dictionary<string, MarketSnapshotClient> Instances = new Dictionary<string, MarketSnapshotClient>();
        readonly object gate = new object();
        readonly Uri origin;
        readonly string cachePath;
        readonly HttpClient client;
        MarketSnapshotData cached;
        bool diskLoaded;
        Task<MarketSnapshotResult> flight;
        CancellationTokenSource flightCancel;
        int waiters;

        public static MarketSnapshotClient ForBaseUri(Uri baseUri)
        {
            if (baseUri == null || !baseUri.IsAbsoluteUri || (baseUri.Scheme != "https" && !(baseUri.Scheme == "http" && baseUri.IsLoopback)))
                throw new ArgumentException("시장 서버는 HTTPS 주소가 필요합니다.");
            string key = baseUri.GetLeftPart(UriPartial.Authority);
            lock (InstancesLock) {
                MarketSnapshotClient result;
                if (!Instances.TryGetValue(key, out result)) {
                    var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None };
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "market-snapshots");
                    result = new MarketSnapshotClient(new Uri(key + "/"), Path.Combine(folder, Hash(Encoding.UTF8.GetBytes(key)) + ".json"), handler);
                    Instances.Add(key, result);
                }
                return result;
            }
        }
        internal MarketSnapshotClient(Uri baseUri, string filePath, HttpMessageHandler handler)
        {
            origin = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/");
            cachePath = filePath;
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MilesianLedger/1.1.0-beta.2");
        }
        public MarketSnapshotData CachedData { get { lock (gate) return cached; } }
        // A successful new download is announced after the cache lock is released.
        // Local disk restoration and unchanged/error responses do not publish.
        public event Action<MarketSnapshotData> SnapshotPublished;

        void PublishSnapshot(MarketSnapshotData data)
        {
            Action<MarketSnapshotData> listeners;
            lock (gate) {
                if (Object.ReferenceEquals(cached, data)) return;
                cached = data; listeners = SnapshotPublished;
            }
            if (listeners == null) return;
            foreach (Action<MarketSnapshotData> listener in listeners.GetInvocationList()) {
                try { listener(data); }
                catch (Exception ex) {
                    if (ex is OutOfMemoryException || ex is StackOverflowException) throw;
                    // A view callback cannot turn a verified download into a failure
                    // or prevent other open views from seeing the publication.
                }
            }
        }

        // Local-only restoration for views that must work before any refresh.
        // LoadDisk holds gate through its one-time verification, so it cannot
        // publish an older disk value over a concurrently refreshed snapshot.
        public MarketSnapshotData ReadCachedData()
        {
            LoadDisk();
            lock (gate) return cached;
        }

        public async Task<MarketSnapshotResult> RefreshAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Task<MarketSnapshotResult> shared;
            lock (gate) {
                if (flight == null || flight.IsCompleted || flightCancel.IsCancellationRequested) {
                    flightCancel = new CancellationTokenSource();
                    var workToken = flightCancel.Token;
                    flight = Task.Run(delegate { return FetchAsync(workToken); });
                    waiters = 0;
                }
                shared = flight; waiters++;
            }
            var cancelled = new TaskCompletionSource<bool>();
            try {
                using (token.Register(delegate { cancelled.TrySetResult(true); })) {
                    if (await Task.WhenAny(shared, cancelled.Task).ConfigureAwait(false) != shared)
                        token.ThrowIfCancellationRequested();
                    token.ThrowIfCancellationRequested();
                    return await shared.ConfigureAwait(false);
                }
            } finally {
                lock (gate) {
                    if (flight == shared && --waiters == 0 && !shared.IsCompleted) flightCancel.Cancel();
                }
            }
        }

        async Task<MarketSnapshotResult> FetchAsync(CancellationToken cancel)
        {
            var result = new MarketSnapshotResult();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var token = timeout.Token;
                try {
                    LoadDisk(); token.ThrowIfCancellationRequested();
                    IDictionary<string, object> manifest;
                    using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "v1/market/manifest"))) {
                        lock (gate) { if (cached != null) request.Headers.TryAddWithoutValidation("If-None-Match", "\"" + cached.Version + "\""); }
                        result.Requests++;
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false)) {
                            if (response.StatusCode == HttpStatusCode.NotModified) {
                                lock (gate) { if (cached == null) throw new InvalidDataException(); result.Data = cached; }
                                result.UsedCached = true; return result;
                            }
                            EnsureSuccess(response);
                            byte[] bytes = await ReadBounded(response.Content, 64 * 1024, token).ConfigureAwait(false);
                            manifest = Json(64 * 1024).DeserializeObject(StrictUtf8(bytes)) as IDictionary<string, object>;
                        }
                    }
                    string version = ValidateManifest(manifest);
                    lock (gate) {
                        if (cached != null && cached.Version == version) { result.Data = cached; result.UsedCached = true; return result; }
                    }
                    byte[] compressed;
                    using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, (string)Value(manifest, "snapshot_url")))) {
                        result.Requests++;
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false)) {
                            EnsureSuccess(response);
                            if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("이중 압축 응답입니다.");
                            compressed = await ReadBounded(response.Content, CompressedLimit, token).ConfigureAwait(false);
                        }
                    }
                    var data = ParseSnapshot(manifest, compressed);
                    token.ThrowIfCancellationRequested();
                    string envelope = Json(24 * 1024 * 1024).Serialize(new Dictionary<string, object> {
                        { "manifest", manifest }, { "compressed_base64", Convert.ToBase64String(compressed) }
                    });
                    // One atomic envelope keeps manifest and payload from different versions apart.
                    WriteAtomic(cachePath, envelope, token);
                    PublishSnapshot(data);
                    result.Data = data; result.Downloaded = true;
                    return result;
                } catch (OperationCanceledException) {
                    if (cancel.IsCancellationRequested) throw;
                    result.ErrorMessage = "시장 서버 응답 시간이 초과되었습니다.";
                } catch (MarketUnavailableException ex) { result.ErrorMessage = ex.Message; }
                catch (Exception ex) {
                    if (ex is OutOfMemoryException || ex is StackOverflowException) throw;
                    result.ErrorMessage = "시장 데이터 다운로드 또는 검증에 실패했습니다.";
                }
            }
            lock (gate) { result.Data = cached; result.UsedCached = cached != null; }
            return result;
        }
        void LoadDisk()
        {
            lock (gate) {
                if (diskLoaded) return;
                diskLoaded = true;
                try {
                    if (!File.Exists(cachePath) || new FileInfo(cachePath).Length > 24 * 1024 * 1024) return;
                    var envelope = Json(24 * 1024 * 1024).DeserializeObject(File.ReadAllText(cachePath, Encoding.UTF8)) as IDictionary<string, object>;
                    var manifest = Value(envelope, "manifest") as IDictionary<string, object>;
                    ValidateManifest(manifest);
                    string encoded = Value(envelope, "compressed_base64") as string;
                    if (encoded == null || encoded.Length > (CompressedLimit + 2) / 3 * 4) return;
                    var data = ParseSnapshot(manifest, Convert.FromBase64String(encoded));
                    cached = data;
                } catch { /* A corrupt disk cache must not prevent a fresh download. */ }
            }
        }
        static void EnsureSuccess(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw new MarketUnavailableException("아직 완성된 공통 시장 데이터가 없습니다. 서버 수집 완료 후 갱신해 주세요.");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
        }
        static async Task<byte[]> ReadBounded(HttpContent content, int limit, CancellationToken token)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > limit) throw new InvalidDataException();
            using (var source = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream()) {
                var buffer = new byte[16384]; int count;
                while ((count = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0) {
                    if (output.Length + count > limit) throw new InvalidDataException();
                    output.Write(buffer, 0, count);
                }
                token.ThrowIfCancellationRequested(); return output.ToArray();
            }
        }
        internal static string ValidateManifest(IDictionary<string, object> manifest)
        {
            if (Number(manifest, "schema_version", false) != 1) throw new InvalidDataException();
            string version = Value(manifest, "version") as string;
            if (version == null || version.Length != 64) throw new InvalidDataException();
            foreach (char c in version) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) throw new InvalidDataException();
            if (!Equals(Value(manifest, "sha256"), version) || !Equals(Value(manifest, "snapshot_url"), "/v1/market/snapshots/" + version + ".json.gz")) throw new InvalidDataException();
            long compressed = Integer(manifest, "compressed_bytes"), plain = Integer(manifest, "uncompressed_bytes");
            if (compressed < 1 || compressed > CompressedLimit || plain < 1 || plain > PlainLimit) throw new InvalidDataException();
            Timestamp(Value(manifest, "generated_at")); return version;
        }
        static MarketSnapshotData ParseSnapshot(IDictionary<string, object> manifest, byte[] compressed)
        {
            string version = ValidateManifest(manifest);
            if (compressed.Length != Integer(manifest, "compressed_bytes") || Hash(compressed) != version) throw new InvalidDataException();
            byte[] plain;
            using (var source = new MemoryStream(compressed))
            using (var gzip = new GZipStream(source, CompressionMode.Decompress))
            using (var output = new MemoryStream()) {
                var buffer = new byte[16384]; int count;
                while ((count = gzip.Read(buffer, 0, buffer.Length)) > 0) {
                    if (output.Length + count > PlainLimit) throw new InvalidDataException();
                    output.Write(buffer, 0, count);
                }
                plain = output.ToArray();
            }
            if (plain.Length != Integer(manifest, "uncompressed_bytes")) throw new InvalidDataException();
            var root = Json(PlainLimit).DeserializeObject(StrictUtf8(plain)) as IDictionary<string, object>;
            if (Number(root, "schema_version", false) != 1) throw new InvalidDataException();
            var generated = Timestamp(Value(root, "generated_at"));
            if (generated != Timestamp(Value(manifest, "generated_at"))) throw new InvalidDataException();
            var data = new MarketSnapshotData { Version = version, GeneratedUtc = generated,
                Items24h = ParseItems(Value(root, "items_24h")), Items7d = ParseItems(Value(root, "items_7d")),
                Quotes = new Dictionary<string, MarketSnapshotQuote>(StringComparer.Ordinal), Status = Value(root, "status") as IDictionary<string, object> };
            if (data.Status == null) throw new InvalidDataException();
            var listingState = Value(data.Status, "listings") as IDictionary<string, object>;
            var published = Value(listingState, "published") as IDictionary<string, object>;
            object listingTime;
            bool hasExplicitListingTime = root.TryGetValue("listings_fetched_at", out listingTime);
            if (hasExplicitListingTime) {
                // A published run may remain in status while its usable listing data
                // has expired. Explicit null means unknown supply, never zero supply.
                if (listingTime != null) {
                    DateTime fetched = Timestamp(listingTime);
                    if (!Equals(Value(published, "state"), "complete") || Value(published, "finished_at") == null
                        || Timestamp(Value(published, "finished_at")) != fetched) throw new InvalidDataException();
                    data.ListingsFetchedUtc = fetched;
                }
            } else if (Equals(Value(published, "state"), "complete") && Value(published, "finished_at") != null)
                data.ListingsFetchedUtc = Timestamp(Value(published, "finished_at"));
            foreach (var row in Rows(Value(root, "quotes"))) {
                string name = Text(row, "name", false);
                long listings = Integer(row, "listing_count");
                if (listings > Int32.MaxValue || data.Quotes.ContainsKey(name)) throw new InvalidDataException();
                long? quantity = NullableInteger(row, "quantity");
                var quote = new MarketSnapshotQuote { Name = name, UnitPrice = Number(row, "unit_price", true),
                    ListingCount = (int)listings, Quantity = quantity.GetValueOrDefault(), QuantityKnown = quantity.HasValue,
                    FetchedUtc = Timestamp(Value(row, "fetched_at")) };
                if (quote.UnitPrice.HasValue && quote.UnitPrice.Value <= 0) throw new InvalidDataException();
                data.Quotes.Add(name, quote);
            }
            // Apply the current category policy to the parsed view only. The verified
            // compressed source is saved unchanged, so old disk caches remain valid.
            MarketPricePolicy.Apply(data);
            return data;
        }
        static List<MarketSnapshotItem> ParseItems(object raw)
        {
            var result = new List<MarketSnapshotItem>();
            foreach (var row in Rows(raw)) {
                if (!(Value(row, "price_comparable") is bool)) throw new InvalidDataException();
                result.Add(new MarketSnapshotItem { Name = Text(row, "name", false), Category = Text(row, "category", true),
                    SoldQuantity = NullableInteger(row, "sold_quantity"), TradeCount = NullableInteger(row, "trade_count"),
                    TradedGold = Number(row, "traded_gold", true), ListedQuantity = NullableInteger(row, "listed_quantity"),
                    ListingCount = NullableInteger(row, "listing_count"), AverageSalePrice = Number(row, "average_sale_price", true),
                    LowestListingPrice = Number(row, "lowest_listing_price", true), PriceComparable = (bool)Value(row, "price_comparable") });
            }
            return result;
        }
        static IEnumerable<IDictionary<string, object>> Rows(object raw)
        {
            var array = raw as object[]; if (array == null || array.Length > 100000) throw new InvalidDataException();
            foreach (var item in array) { var row = item as IDictionary<string, object>; if (row == null) throw new InvalidDataException(); yield return row; }
        }
        static JavaScriptSerializer Json(int max) { return new JavaScriptSerializer { MaxJsonLength = max, RecursionLimit = 40 }; }
        static string StrictUtf8(byte[] data) { return new UTF8Encoding(false, true).GetString(data); }
        internal static string Hash(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        internal static object Value(IDictionary<string, object> row, string key) { object value; return row != null && row.TryGetValue(key, out value) ? value : null; }
        static string Text(IDictionary<string, object> row, string key, bool emptyAllowed)
        {
            string value = Value(row, key) as string;
            if (value == null || value.Length > 300 || (!emptyAllowed && String.IsNullOrWhiteSpace(value))) throw new InvalidDataException();
            return value;
        }
        static decimal? Number(IDictionary<string, object> row, string key, bool nullable)
        {
            object raw = Value(row, key); decimal number;
            if (raw == null && nullable) return null;
            if (raw == null || raw is string || raw is bool || !Decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || number < 0) throw new InvalidDataException();
            return number;
        }
        static long Integer(IDictionary<string, object> row, string key)
        {
            decimal value = Number(row, key, false).Value;
            if (value > Int64.MaxValue || Decimal.Truncate(value) != value) throw new InvalidDataException(); return (long)value;
        }
        static long? NullableInteger(IDictionary<string, object> row, string key)
        {
            if (Value(row, key) == null) return null;
            return Integer(row, key);
        }
        static DateTime Timestamp(object raw)
        {
            string value = raw as string; DateTimeOffset parsed;
            if (value == null || !value.EndsWith("Z", StringComparison.Ordinal) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                || parsed.UtcDateTime > DateTime.UtcNow.AddMinutes(5)) throw new InvalidDataException();
            return parsed.UtcDateTime;
        }
        static void WriteAtomic(string path, string data, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(pending, data, new UTF8Encoding(false)); token.ThrowIfCancellationRequested();
                if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
            } finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        sealed class MarketUnavailableException : Exception { public MarketUnavailableException(string message) : base(message) { } }
    }
}
