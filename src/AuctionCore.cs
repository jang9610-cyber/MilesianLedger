using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    // This module never schedules work. RefreshAsync is its only network entry point,
    // and the application calls it only from the explicit refresh button.
    public sealed class AuctionSettings
    {
        public const int RequestLimit = 500;
        public Dictionary<string, string> NameMappings { get; set; }
        public int MaxPagesPerItem { get; set; }
        public int MaxRequestsPerRefresh { get; set; }
        public AuctionSettings()
        {
            NameMappings = new Dictionary<string, string>(StringComparer.Ordinal);
            MaxPagesPerItem = 10;
            MaxRequestsPerRefresh = RequestLimit;
        }
        public string ResolveName(string material)
        {
            string mapped;
            string name = NameMappings != null && NameMappings.TryGetValue(material, out mapped) && !String.IsNullOrWhiteSpace(mapped)
                ? mapped.Trim() : (material ?? "").Trim();
            // Keep saved material/procurement keys stable; only the API search name changes.
            if (name == "미스릴 광석") return "미스릴광석";
            if (name == "미스릴 광석 조각") return "미스릴광석 조각";
            return name;
        }
        public static AuctionSettings Load(string filePath)
        {
            if (!File.Exists(filePath)) return new AuctionSettings();
            try
            {
                var settings = new JavaScriptSerializer().Deserialize<AuctionSettings>(File.ReadAllText(filePath, Encoding.UTF8));
                if (settings == null) return new AuctionSettings();
                settings.Normalize();
                return settings;
            }
            catch { return new AuctionSettings(); }
        }
        public void Save(string filePath)
        {
            Normalize();
            AuctionFiles.WriteAtomic(filePath, new JavaScriptSerializer().Serialize(this));
        }
        private void Normalize()
        {
            if (NameMappings == null) NameMappings = new Dictionary<string, string>(StringComparer.Ordinal);
            MaxPagesPerItem = Math.Max(1, Math.Min(10, MaxPagesPerItem));
            // v0.3.1 saved 100 as the default. Carry existing installations forward
            // while preserving deliberately lower limits and name corrections.
            if (MaxRequestsPerRefresh == 100) MaxRequestsPerRefresh = RequestLimit;
            MaxRequestsPerRefresh = Math.Max(1, Math.Min(RequestLimit, MaxRequestsPerRefresh));
        }
    }

    internal static class AuctionFiles
    {
        internal static void WriteAtomic(string path, string content) { WriteAtomicBytes(path, new UTF8Encoding(false).GetBytes(content)); }
        internal static void WriteAtomicBytes(string path, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string pending = path + ".tmp";
            File.WriteAllBytes(pending, content);
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }
    }

    public sealed class AuctionHttpResponse
    {
        public int StatusCode { get; set; }
        public string Body { get; set; }
    }
    public interface IAuctionTransport
    {
        Task<AuctionHttpResponse> GetAsync(string itemName, string cursor, CancellationToken token);
    }
    public interface IAuctionDelay { Task WaitAsync(int milliseconds, CancellationToken token); }
    public sealed class AuctionDelay : IAuctionDelay
    {
        public Task WaitAsync(int milliseconds, CancellationToken token) { return Task.Delay(milliseconds, token); }
    }
    public sealed class ProxyAuctionTransport : IAuctionTransport, IDisposable
    {
        private readonly HttpClient client;
        private readonly AuctionProxyConfig configuration;
        public bool IsConfigured { get { return configuration.IsConfigured; } }
        public string ConfigurationMessage { get { return configuration.StatusMessage; } }
        public ProxyAuctionTransport(AuctionProxyConfig configuration)
            : this(configuration, CreateHandler()) { }
        internal ProxyAuctionTransport(AuctionProxyConfig configuration, HttpMessageHandler handler)
        {
            this.configuration = configuration ?? new AuctionProxyConfig("");
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        }
        private static HttpMessageHandler CreateHandler()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        }
        public static Uri BuildUri(AuctionProxyConfig configuration, string itemName, string cursor)
        {
            if (configuration == null || !configuration.IsConfigured)
                throw new InvalidOperationException(configuration == null ? AuctionProxyConfig.NotConfiguredMessage : configuration.StatusMessage);
            if (String.IsNullOrWhiteSpace(itemName)) throw new ArgumentException("아이템 이름이 필요합니다.", "itemName");
            return new Uri(configuration.BaseUri, "v1/auction/list?item_name="
                + Uri.EscapeDataString(itemName) + "&cursor=" + Uri.EscapeDataString(cursor ?? ""));
        }
        public async Task<AuctionHttpResponse> GetAsync(string itemName, string cursor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(configuration, itemName, cursor)))
            {
                request.Headers.Accept.ParseAdd("application/json");
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new AuctionHttpResponse { StatusCode = (int)response.StatusCode, Body = body };
                }
            }
        }
        public void Dispose() { client.Dispose(); }
    }

    public sealed class AuctionPage
    {
        public decimal? UnitPrice { get; set; }
        public long Quantity { get; set; }
        public int ListingCount { get; set; }
        public string NextCursor { get; set; }
        public DateTime? FetchedUtc { get; set; }
        public static AuctionPage Parse(string body, string exactName)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(body)) throw new InvalidDataException();
                var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var root = json.DeserializeObject(body) as IDictionary<string, object>;
                object itemValue;
                if (root == null || !root.TryGetValue("auction_item", out itemValue) || itemValue == null || itemValue is string)
                    throw new InvalidDataException();
                var items = itemValue as IEnumerable;
                if (items == null || itemValue is IDictionary) throw new InvalidDataException();
                var page = new AuctionPage();
                object fetched;
                if (root.TryGetValue("fetched_at", out fetched))
                {
                    string stamp = fetched as string;
                    DateTimeOffset parsed;
                    DateTime now = DateTime.UtcNow;
                    if (String.IsNullOrEmpty(stamp) || !(stamp.EndsWith("Z", StringComparison.Ordinal) || stamp.EndsWith("+00:00", StringComparison.Ordinal))
                        || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                        || parsed.Offset != TimeSpan.Zero || parsed.UtcDateTime < now.AddDays(-30) || parsed.UtcDateTime > now.AddMinutes(5))
                        throw new InvalidDataException();
                    // A slightly fast proxy clock cannot create a future cached price.
                    page.FetchedUtc = parsed.UtcDateTime > now ? now : parsed.UtcDateTime;
                }
                object cursor;
                if (root.TryGetValue("next_cursor", out cursor) && cursor != null)
                {
                    if (!(cursor is string)) throw new InvalidDataException();
                    page.NextCursor = (string)cursor;
                }
                foreach (object raw in items)
                {
                    var row = raw as IDictionary<string, object>;
                    if (row == null) throw new InvalidDataException();
                    object name;
                    if (!row.TryGetValue("item_name", out name) || !(name is string)) throw new InvalidDataException();
                    if (!String.Equals((string)name, exactName, StringComparison.Ordinal)) continue;
                    object priceValue, countValue;
                    decimal price, count;
                    if (!row.TryGetValue("auction_price_per_unit", out priceValue) || !row.TryGetValue("item_count", out countValue)
                        || !Decimal.TryParse(Convert.ToString(priceValue, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out price)
                        || !Decimal.TryParse(Convert.ToString(countValue, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out count)
                        || price <= 0 || count < 1 || count != Decimal.Truncate(count) || count > Int64.MaxValue) continue;
                    if (!page.UnitPrice.HasValue || price < page.UnitPrice.Value) page.UnitPrice = price;
                    page.Quantity = SaturatingAdd(page.Quantity, (long)count);
                    page.ListingCount++;
                }
                return page;
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException || ex is StackOverflowException) throw;
                throw new InvalidDataException("경매장 응답 형식이 예상과 다릅니다.");
            }
        }
        internal static long SaturatingAdd(long a, long b) { return a > Int64.MaxValue - b ? Int64.MaxValue : a + b; }
    }

    public sealed class AuctionQuote
    {
        public string Material { get; set; }
        public string SearchName { get; set; }
        public decimal? UnitPrice { get; set; }
        public long AvailableQuantity { get; set; }
        public int ListingCount { get; set; }
        public int Pages { get; set; }
        public bool Complete { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public DateTime? PriceUtc { get; set; }
        public DateTime AttemptUtc { get; set; }
        internal AuctionQuote Copy() { return (AuctionQuote)MemberwiseClone(); }
    }
    public sealed class AuctionCache
    {
        public int Version { get; set; }
        public Dictionary<string, AuctionQuote> Quotes { get; set; }
        public AuctionCache() { Version = 1; Quotes = new Dictionary<string, AuctionQuote>(StringComparer.Ordinal); }
    }
    public sealed class AuctionRefreshProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public int Requests { get; set; }
        public string Material { get; set; }
    }
    public sealed class AuctionRefreshFailure
    {
        public string Material { get; set; }
        public string Message { get; set; }
    }
    public sealed class AuctionRefreshResult
    {
        public int RequestedMaterials { get; set; }
        public int UpdatedMaterials { get; set; }
        // Retain the older combined counter for existing consumers. The UI uses
        // the separate counts below so skipped items are not described as errors.
        public int FailedMaterials { get; set; }
        public List<AuctionRefreshFailure> FailedItems { get; private set; }
        public int ErrorMaterials { get { return FailedItems.Count; } }
        public int SkippedMaterials { get { return Math.Max(0, RequestedMaterials - UpdatedMaterials - ErrorMaterials); } }
        public int Requests { get; set; }
        public bool Cancelled { get; set; }
        public string StoppedReason { get; set; }
        public AuctionRefreshResult() { FailedItems = new List<AuctionRefreshFailure>(); }
    }

    public sealed class AuctionService : IDisposable
    {
        private readonly string cachePath;
        private readonly AuctionSettings settings;
        private readonly IAuctionTransport transport;
        private readonly IAuctionDelay delay;
        private readonly object sync = new object();
        private AuctionCache cache;
        private int refreshing;
        private bool sentRequest;
        public bool IsRefreshing { get { return Interlocked.CompareExchange(ref refreshing, 0, 0) != 0; } }
        public string Notice { get; private set; }
        public bool IsConfigured { get { var proxy = transport as ProxyAuctionTransport; return proxy == null || proxy.IsConfigured; } }
        public string ConfigurationMessage { get { var proxy = transport as ProxyAuctionTransport; return proxy == null ? "" : proxy.ConfigurationMessage; } }
        public AuctionService(string directory, AuctionSettings settings)
            : this(directory, settings, new ProxyAuctionTransport(AuctionProxyConfig.Load(Path.Combine(directory, "auction-proxy.json"))), new AuctionDelay()) { }
        public AuctionService(string directory, AuctionSettings settings, IAuctionTransport transport, IAuctionDelay delay)
        {
            this.settings = settings ?? new AuctionSettings();
            this.transport = transport;
            this.delay = delay;
            cachePath = Path.Combine(directory, "auction-cache.json");
            Notice = "";
            cache = new AuctionCache();
            if (!File.Exists(cachePath)) return;
            try
            {
                cache = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<AuctionCache>(File.ReadAllText(cachePath, Encoding.UTF8));
                if (cache == null || cache.Version != 1 || cache.Quotes == null) throw new InvalidDataException();
            }
            catch
            {
                cache = new AuctionCache();
                Notice = "저장된 경매장 정보를 읽지 못했습니다. 갱신 버튼을 누르면 다시 조회합니다.";
            }
        }
        public AuctionQuote GetQuote(string material)
        {
            lock (sync)
            {
                AuctionQuote quote;
                if (material == null || !cache.Quotes.TryGetValue(material, out quote) || quote == null
                    || !String.Equals(quote.SearchName, settings.ResolveName(material), StringComparison.Ordinal)
                    || IsExpired(quote, DateTime.UtcNow)) return null;
                return quote.Copy();
            }
        }
        private static bool IsExpired(AuctionQuote quote, DateTime now)
        {
            DateTime stamp = quote.PriceUtc ?? quote.AttemptUtc;
            return stamp == default(DateTime) || stamp > now.AddMinutes(10) || now - stamp.ToUniversalTime() >= TimeSpan.FromDays(30);
        }
        private void Put(AuctionQuote quote)
        {
            lock (sync) { cache.Quotes[quote.Material] = quote; }
        }
        private void RecordFailure(string material, string searchName, string status, string message)
        {
            var quote = GetQuote(material) ?? new AuctionQuote { Material = material, SearchName = searchName };
            quote.Status = status;
            quote.Message = message;
            quote.AttemptUtc = DateTime.UtcNow;
            Put(quote);
        }
        private void RecordRefreshFailure(AuctionRefreshResult result, string material, string searchName, string status, string message)
        {
            RecordFailure(material, searchName, status, message);
            result.FailedMaterials++;
            if (status == "error") result.FailedItems.Add(new AuctionRefreshFailure { Material = material, Message = message });
        }
        private void SaveCache()
        {
            lock (sync)
            {
                foreach (string key in cache.Quotes.Where(delegate(KeyValuePair<string, AuctionQuote> pair) {
                    return pair.Value == null || IsExpired(pair.Value, DateTime.UtcNow);
                }).Select(delegate(KeyValuePair<string, AuctionQuote> pair) { return pair.Key; }).ToArray()) cache.Quotes.Remove(key);
                try { AuctionFiles.WriteAtomic(cachePath, new JavaScriptSerializer().Serialize(cache)); }
                catch { Notice = "조회 결과는 표시했지만 경매장 캐시를 저장하지 못했습니다. 폴더 쓰기 권한을 확인해 주세요."; }
            }
        }
        public async Task<AuctionRefreshResult> RefreshAsync(IEnumerable<string> materials,
            IProgress<AuctionRefreshProgress> progress, CancellationToken cancellationToken)
        {
            if (!IsConfigured)
                return new AuctionRefreshResult { StoppedReason = ConfigurationMessage, Requests = 0 };
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0)
                throw new InvalidOperationException("이미 경매장 갱신이 진행 중입니다.");
            var result = new AuctionRefreshResult { StoppedReason = "" };
            try
            {
                Notice = "";
                var names = (materials ?? Enumerable.Empty<string>()).Where(delegate(string n) { return !String.IsNullOrWhiteSpace(n); })
                    .Select(delegate(string n) { return n.Trim(); }).Distinct(StringComparer.Ordinal).ToArray();
                // Freeze selection, aliases and budgets once per explicit click.
                var queries = names.Select(delegate(string n) { return new KeyValuePair<string, string>(n, settings.ResolveName(n)); }).ToArray();
                result.RequestedMaterials = queries.Length;
                int maxPages = Math.Max(1, Math.Min(10, settings.MaxPagesPerItem));
                int maxRequests = Math.Max(1, Math.Min(AuctionSettings.RequestLimit, settings.MaxRequestsPerRefresh));
                var searched = new Dictionary<string, AuctionQuote>(StringComparer.Ordinal);
                var failedQueries = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < queries.Length; i++)
                {
                    string material = queries[i].Key, searchName = queries[i].Value;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        result.StoppedReason = "사용자가 갱신을 중단했습니다.";
                        break;
                    }
                    AuctionQuote reused;
                    if (searched.TryGetValue(searchName, out reused))
                    {
                        var copy = reused.Copy(); copy.Material = material; Put(copy);
                        result.UpdatedMaterials++;
                        Report(progress, i + 1, queries.Length, result.Requests, material);
                        continue;
                    }
                    string previousFailure;
                    if (failedQueries.TryGetValue(searchName, out previousFailure))
                    {
                        RecordRefreshFailure(result, material, searchName, "error", previousFailure);
                        Report(progress, i + 1, queries.Length, result.Requests, material);
                        continue;
                    }
                    if (!String.IsNullOrEmpty(result.StoppedReason) || result.Requests >= maxRequests)
                    {
                        if (String.IsNullOrEmpty(result.StoppedReason)) result.StoppedReason = "이번 갱신의 요청 한도에 도달했습니다.";
                        RecordRefreshFailure(result, material, searchName, "skipped", result.StoppedReason);
                        continue;
                    }
                    var quote = new AuctionQuote { Material = material, SearchName = searchName, Status = "ok", Message = "", AttemptUtc = DateTime.UtcNow };
                    string cursor = "";
                    var cursors = new HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        for (int pageNumber = 0; pageNumber < maxPages; pageNumber++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            // Also wait across two rapid explicit refreshes. No retry or polling task is created.
                            if (sentRequest) await delay.WaitAsync(250, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            Report(progress, i, queries.Length, result.Requests, material);
                            result.Requests++;
                            sentRequest = true;
                            AuctionHttpResponse response = await transport.GetAsync(searchName, cursor, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (response == null) throw new InvalidDataException();
                            if (response.StatusCode != 200)
                            {
                                bool stop;
                                string message = HttpError(response.StatusCode, response.Body, out stop);
                                if (stop) result.StoppedReason = message;
                                throw new AuctionRequestException(message);
                            }
                            AuctionPage page = AuctionPage.Parse(response.Body, searchName);
                            DateTime fetchedUtc = page.FetchedUtc ?? DateTime.UtcNow;
                            if (!quote.PriceUtc.HasValue || fetchedUtc < quote.PriceUtc.Value) quote.PriceUtc = fetchedUtc;
                            quote.Pages++;
                            quote.AvailableQuantity = AuctionPage.SaturatingAdd(quote.AvailableQuantity, page.Quantity);
                            quote.ListingCount += page.ListingCount;
                            if (page.UnitPrice.HasValue && (!quote.UnitPrice.HasValue || page.UnitPrice.Value < quote.UnitPrice.Value)) quote.UnitPrice = page.UnitPrice;
                            if (String.IsNullOrEmpty(page.NextCursor)) { quote.Complete = true; break; }
                            if (!cursors.Add(page.NextCursor)) { quote.Message = "페이지 정보가 반복되어 조회한 매물만 표시합니다."; break; }
                            cursor = page.NextCursor;
                            if (result.Requests >= maxRequests) { quote.Message = "갱신 요청 한도에 도달하여 조회한 매물만 표시합니다."; break; }
                        }
                        quote.AttemptUtc = DateTime.UtcNow;
                        if (!quote.UnitPrice.HasValue) quote.PriceUtc = null;
                        quote.Status = quote.Complete ? (quote.UnitPrice.HasValue ? "ok" : "empty") : "partial";
                        if (quote.Status == "empty") quote.Message = "정확히 일치하는 이름의 유효한 매물이 없습니다.";
                        if (!quote.Complete && String.IsNullOrEmpty(quote.Message)) quote.Message = "품목별 페이지 한도에 도달했습니다. 조회 범위 안의 최저 단가입니다.";
                        Put(quote);
                        searched[searchName] = quote.Copy();
                        result.UpdatedMaterials++;
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            RecordRefreshFailure(result, material, searchName, "cancelled", "사용자가 갱신을 중단했습니다. 이전 조회값이 있으면 유지합니다.");
                            result.Cancelled = true;
                            result.StoppedReason = "사용자가 갱신을 중단했습니다.";
                            break;
                        }
                        string timeoutMessage = "요청 시간이 초과되었습니다. 갱신 버튼으로 다시 시도해 주세요.";
                        RecordRefreshFailure(result, material, searchName, "error", timeoutMessage);
                        failedQueries[searchName] = timeoutMessage;
                    }
                    catch (Exception ex)
                    {
                        if (ex is OutOfMemoryException || ex is StackOverflowException) throw;
                        // Do not persist response bodies, URLs, raw exceptions, or credentials.
                        string message = ex is AuctionRequestException ? ex.Message : ex is InvalidDataException
                            ? "경매장 응답 형식이 예상과 다릅니다. 이전 조회값이 있으면 유지합니다."
                            : "경매장에 연결하지 못했습니다. 네트워크 상태를 확인한 뒤 다시 시도해 주세요.";
                        RecordRefreshFailure(result, material, searchName, "error", message);
                        failedQueries[searchName] = message;
                    }
                    Report(progress, i + 1, queries.Length, result.Requests, material);
                }
                return result;
            }
            finally
            {
                SaveCache();
                Interlocked.Exchange(ref refreshing, 0);
            }
        }
        private static void Report(IProgress<AuctionRefreshProgress> progress, int completed, int total, int requests, string material)
        {
            if (progress != null) progress.Report(new AuctionRefreshProgress { Completed = completed, Total = total, Requests = requests, Material = material });
        }
        private static string ErrorCode(string body)
        {
            // The server's free-form message may contain request details. Only use
            // the documented error.code to choose a local, credential-free message.
            try
            {
                if (String.IsNullOrEmpty(body) || body.Length > 65536) return "";
                var root = new JavaScriptSerializer().DeserializeObject(body) as IDictionary<string, object>;
                object rawError, rawCode;
                if (root == null || !root.TryGetValue("error", out rawError)) return "";
                var error = rawError as IDictionary<string, object>;
                return error != null && error.TryGetValue("code", out rawCode) && rawCode is string ? (string)rawCode : "";
            }
            catch { return ""; }
        }
        private static string HttpError(int code, string body, out bool stop)
        {
            stop = code == 401 || code == 403 || code == 429 || code >= 500 || (code >= 300 && code < 400);
            switch (ErrorCode(body))
            {
                case "PROXY_NOT_CONFIGURED":
                    stop = true; return "경매장 서비스 연결을 준비 중입니다. 나중에 다시 시도해 주세요.";
                case "PROXY_RATE_LIMIT":
                    stop = true; return "경매장 요청이 많아 잠시 중단했습니다. 잠시 후 갱신 버튼으로 다시 시도해 주세요.";
                case "PROXY_QUOTA_EXCEEDED":
                    stop = true; return "경매장 서비스의 이용 한도에 도달했습니다. 한도가 복구된 후 다시 시도해 주세요.";
                case "PROXY_QUOTA_UNAVAILABLE":
                    stop = true; return "경매장 서비스의 이용 한도를 확인할 수 없어 갱신을 중단했습니다. 잠시 후 다시 시도해 주세요.";
                case "PROXY_UPSTREAM_LIMIT":
                    stop = true; return "경매장 제공처의 조회 한도에 도달했습니다. 나중에 다시 시도해 주세요.";
                case "UPSTREAM_UNAUTHORIZED":
                case "PROXY_UPSTREAM_AUTH":
                    stop = true; return "경매장 서비스 연결을 운영자가 확인해야 합니다. 나중에 다시 시도해 주세요.";
                case "PROXY_BUSY":
                    stop = true; return "경매장 서비스가 혼잡합니다. 잠시 후 다시 시도해 주세요.";
                case "PROXY_TIMEOUT":
                    stop = true; return "경매장 서비스 응답이 늦어 갱신을 중단했습니다. 잠시 후 다시 시도해 주세요.";
                case "UPSTREAM_UNAVAILABLE":
                case "PROXY_UPSTREAM_ERROR":
                case "PROXY_INVALID_RESPONSE":
                    stop = true; return "경매장 서비스를 일시적으로 이용할 수 없습니다. 나중에 다시 시도해 주세요.";
                case "INVALID_REQUEST":
                case "PROXY_INVALID_REQUEST":
                case "PROXY_ITEM_NOT_ALLOWED":
                    return "해당 품목을 경매장 서비스에서 조회할 수 없습니다.";
                case "PROXY_ITEM_QUERY_REJECTED":
                    return "경매장에서 이 품목의 검색을 처리하지 못했습니다. 다른 품목은 계속 조회합니다.";
                case "PROXY_NOT_FOUND":
                case "PROXY_METHOD_NOT_ALLOWED":
                    stop = true; return "경매장 서비스 연결을 확인해야 합니다. 앱 업데이트를 확인해 주세요.";
            }
            if (code == 401 || code == 403) return "경매장 서비스 연결을 운영자가 확인해야 합니다. 나중에 다시 시도해 주세요.";
            if (code == 429) return "경매장 요청 한도에 도달했습니다. 잠시 후 갱신 버튼으로 다시 시도해 주세요.";
            if (code >= 300 && code < 400) return "경매장 서비스 주소가 변경되어 요청을 중단했습니다. 앱 업데이트를 확인해 주세요.";
            if (code == 400) return "검색 이름 또는 요청 조건을 확인해 주세요.";
            if (code >= 500) return "경매장 서비스를 일시적으로 이용할 수 없습니다. 나중에 다시 시도해 주세요.";
            return "경매장 요청에 실패했습니다 (HTTP " + code.ToString(CultureInfo.InvariantCulture) + ").";
        }
        public void Dispose()
        {
            var disposable = transport as IDisposable;
            if (disposable != null) disposable.Dispose();
        }
        private sealed class AuctionRequestException : Exception { public AuctionRequestException(string message) : base(message) { } }
    }
}
