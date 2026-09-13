using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public enum MarketOpportunity { All, ActiveTrading, LowSupply, BelowAverage }

    // These comparisons describe the observed period; they do not predict a sale.
    public static class MarketInsights
    {
        static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

        public static bool Matches(MarketSnapshotItem item, MarketOpportunity mode)
        {
            if (item == null) return false;
            switch (mode) {
                case MarketOpportunity.All: return true;
                case MarketOpportunity.ActiveTrading: return item.TradeCount > 0;
                case MarketOpportunity.LowSupply:
                    return item.SoldQuantity > 0 && item.ListedQuantity.HasValue
                        && item.ListedQuantity.Value >= 0 && item.ListedQuantity < item.SoldQuantity;
                case MarketOpportunity.BelowAverage:
                    return item.PriceComparable && item.TradeCount > 0 && item.ListedQuantity > 0
                        && item.AverageSalePrice > 0 && item.LowestListingPrice > 0
                        && item.LowestListingPrice < item.AverageSalePrice;
                default: return false;
            }
        }

        public static decimal? Rank(MarketSnapshotItem item, MarketOpportunity mode)
        {
            if (!Matches(item, mode)) return null;
            switch (mode) {
                case MarketOpportunity.ActiveTrading: return item.TradeCount.Value;
                case MarketOpportunity.LowSupply:
                    // No current listings sort first; this sentinel is never displayed as a ratio.
                    return item.ListedQuantity.Value == 0 ? Decimal.MaxValue
                        : (decimal)item.SoldQuantity.Value / item.ListedQuantity.Value;
                case MarketOpportunity.BelowAverage:
                    return (1m - item.LowestListingPrice.Value / item.AverageSalePrice.Value) * 100m;
                default: return null;
            }
        }

        public static string Detail(MarketSnapshotItem item, MarketOpportunity mode)
        {
            if (!Matches(item, mode)) return "";
            switch (mode) {
                case MarketOpportunity.ActiveTrading:
                    return "선택 기간 거래 " + item.TradeCount.Value.ToString("N0", Korean) + "건";
                case MarketOpportunity.LowSupply:
                    if (item.ListedQuantity.Value == 0)
                        return "선택 기간 " + item.SoldQuantity.Value.ToString("N0", Korean) + "개 판매 · 현재 매물 없음";
                    return "선택 기간 판매 / 현재 매물 " + RatioText(Rank(item, mode).Value)
                        + "배 · " + item.SoldQuantity.Value.ToString("N0", Korean) + " / "
                        + item.ListedQuantity.Value.ToString("N0", Korean) + "개";
                case MarketOpportunity.BelowAverage:
                    return "현재 최저 단가가 선택 기간 평균 거래 단가보다 "
                        + PercentageText(Rank(item, mode).Value) + " 낮음 · 전체 거래 평균과의 차이이며 수익률은 아닙니다.";
                default: return "";
            }
        }

        // Keep fractional measurements for filtering and ranking; only their text is truncated.
        public static string RatioText(decimal value) { return Decimal.Truncate(value).ToString("0", Korean); }
        public static string PercentageText(decimal value)
        {
            return value > 0 && value < 1 ? "1% 미만" : Decimal.Truncate(value).ToString("0", Korean) + "%";
        }
    }

    // This personal file contains names only and is independent of shared market snapshots.
    public sealed class WatchlistStore
    {
        public const int MaximumEntries = 1000;
        const int MaximumNameLength = 512, MaximumFileBytes = 2 * 1024 * 1024;
        readonly string filePath;
        readonly bool readOnly;
        HashSet<string> entries = new HashSet<string>(StringComparer.Ordinal);
        public string Notice { get; private set; }
        public string[] Entries { get { return entries.OrderBy(x => x, StringComparer.Ordinal).ToArray(); } }
        public int Count { get { return entries.Count; } }

        public WatchlistStore(string path)
        {
            if (path == null) return;
            try {
                if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException();
                filePath = Path.GetFullPath(path);
                if (!File.Exists(filePath)) return;
                if (new FileInfo(filePath).Length > MaximumFileBytes) throw new InvalidDataException();
                var serializer = Serializer();
                var root = serializer.DeserializeObject(File.ReadAllText(filePath, new UTF8Encoding(false, true))) as IDictionary<string, object>;
                object version, rawEntries;
                if (root == null || !root.TryGetValue("version", out version) || !(version is int) || (int)version != 1
                    || !root.TryGetValue("items", out rawEntries)) throw new InvalidDataException();
                var values = rawEntries as IList;
                if (values == null || values.Count > MaximumEntries) throw new InvalidDataException();
                var loaded = new HashSet<string>(StringComparer.Ordinal);
                foreach (object value in values) {
                    string name = value as string;
                    if (!ValidName(name)) throw new InvalidDataException();
                    loaded.Add(name);
                }
                entries = loaded;
            } catch (Exception ex) {
                RethrowFatal(ex);
                readOnly = true;
                Notice = "관심 목록 파일을 읽지 못했습니다. 기존 파일을 보존하기 위해 저장을 중단했습니다.";
            }
        }

        public bool Contains(string name) { return name != null && entries.Contains(name); }

        public bool TrySet(string name, bool selected, out string error)
        {
            error = null;
            if (!ValidName(name)) { error = "관심 품목 이름을 확인해 주세요."; return false; }
            if (entries.Contains(name) == selected) return true;
            if (readOnly) { error = Notice; return false; }
            if (selected && entries.Count >= MaximumEntries) {
                error = "관심 목록에는 최대 " + MaximumEntries.ToString("N0", CultureInfo.GetCultureInfo("ko-KR")) + "개까지 저장할 수 있습니다.";
                return false;
            }
            var changed = new HashSet<string>(entries, StringComparer.Ordinal);
            if (selected) changed.Add(name); else changed.Remove(name);
            try {
                if (filePath != null) Save(changed);
                entries = changed;
                return true;
            } catch (Exception ex) {
                RethrowFatal(ex);
                error = "관심 목록을 저장하지 못했습니다. 폴더의 쓰기 권한과 저장 공간을 확인해 주세요.";
                return false;
            }
        }

        void Save(HashSet<string> values)
        {
            string folder = Path.GetDirectoryName(filePath);
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, ".market-watchlist-" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                var data = new Dictionary<string, object> { { "version", 1 }, { "items", values.OrderBy(x => x, StringComparer.Ordinal).ToArray() } };
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(Serializer().Serialize(data));
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                }
                if (File.Exists(filePath)) File.Replace(temporary, filePath, null);
                else File.Move(temporary, filePath);
            } finally {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes, RecursionLimit = 8 }; }
        static bool ValidName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength) return false;
            foreach (char character in name) if (Char.IsControl(character)) return false;
            return true;
        }
        static void RethrowFatal(Exception error)
        {
            if (error is OutOfMemoryException || error is StackOverflowException || error is System.Threading.ThreadAbortException) throw error;
        }
    }
}
