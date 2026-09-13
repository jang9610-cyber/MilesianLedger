using System;
using System.Collections.Generic;
using System.Text;

namespace MabinogiBarter
{
    public sealed class MarketSearchEntry
    {
        public string Name, Category;
        public decimal? UnitPrice;
        public long Quantity;
        public int ListingCount;
        public bool QuantityKnown, HasListing, PriceComparable, IsEnchantScroll, EnchantNameKnown;
        public DateTime? FetchedUtc;
    }

    // Snapshot-only lookup: construct once after a refresh, then search without I/O
    // or sorting the full market on every keystroke.
    public sealed class MarketSearchIndex
    {
        sealed class IndexedEntry
        {
            public string Key;
            public MarketSearchEntry Entry;
            public bool HasQuote, HasMetadata;
            public MarketSnapshotItem ListingMetadata;
        }

        // Exact official base identities only. Bundles and random-rank products
        // containing these words are different items, not unnamed scrolls.
        static readonly HashSet<string> enchantScrollNames = new HashSet<string>(StringComparer.Ordinal) {
            "인챈트 스크롤", "전용 인챈트 스크롤", "개방된 전용 인챈트 스크롤",
            "인챈트 스크롤(판매 불가)", "8주년 전용 인챈트 스크롤", "시양양 한정 인챈트 스크롤"
        };

        readonly Dictionary<string, IndexedEntry> byName = new Dictionary<string, IndexedEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, List<IndexedEntry>> byMatch = new Dictionary<string, List<IndexedEntry>>(StringComparer.OrdinalIgnoreCase);
        readonly List<IndexedEntry> sorted = new List<IndexedEntry>();

        public MarketSearchIndex(MarketSnapshotData data)
        {
            if (data == null) return;
            DateTime? listingTime = ValidTime(data.ListingsFetchedUtc);
            if (data.Quotes != null) foreach (var pair in data.Quotes) {
                MarketSnapshotQuote quote = pair.Value;
                if (quote == null) continue;
                string name = String.IsNullOrWhiteSpace(quote.Name) ? pair.Key : quote.Name;
                IndexedEntry indexed = GetOrAdd(name, listingTime);
                if (indexed == null) continue;
                bool hasListing = quote.UnitPrice.HasValue && quote.UnitPrice.Value > 0 && quote.ListingCount > 0;
                DateTime? fetched = ValidTime(quote.FetchedUtc) ?? listingTime;
                // Duplicated names describe the same market, so do not sum their
                // quantities/counts. Keep the minimum actually priced quote.
                if (!indexed.HasQuote || (hasListing && (!indexed.Entry.HasListing || quote.UnitPrice.Value < indexed.Entry.UnitPrice.Value))) {
                    MarketSearchEntry entry = indexed.Entry;
                    entry.Name = name;
                    entry.UnitPrice = hasListing ? quote.UnitPrice : null;
                    entry.HasListing = hasListing;
                    entry.ListingCount = Math.Max(0, quote.ListingCount);
                    entry.Quantity = Math.Max(0L, quote.Quantity);
                    entry.QuantityKnown = quote.QuantityKnown && (hasListing || fetched.HasValue);
                    entry.FetchedUtc = fetched;
                }
                indexed.HasQuote = true;
            }
            AddMetadata(data.Items24h, listingTime);
            AddMetadata(data.Items7d, listingTime);
            foreach (IndexedEntry indexed in byName.Values) ResolveEnchantScroll(indexed);
            sorted.AddRange(byName.Values);
            sorted.Sort(delegate(IndexedEntry left, IndexedEntry right) {
                return StringComparer.Ordinal.Compare(left.Entry.Name, right.Entry.Name);
            });
            foreach (IndexedEntry indexed in sorted) {
                List<IndexedEntry> matches;
                if (!byMatch.TryGetValue(indexed.Key, out matches)) {
                    matches = new List<IndexedEntry>(); byMatch.Add(indexed.Key, matches);
                }
                matches.Add(indexed);
            }
        }

        public int Count { get { return sorted.Count; } }

        public List<MarketSearchEntry> Search(string query, int limit)
        {
            limit = Math.Min(100, Math.Max(0, limit));
            var result = new List<MarketSearchEntry>(limit);
            string key = MatchKey(query);
            if (limit == 0 || key.Length == 0) return result;
            IndexedEntry exact;
            if (byName.TryGetValue(query, out exact)) result.Add(Copy(exact.Entry));
            if (result.Count == limit) return result;
            List<IndexedEntry> equalMatches;
            if (byMatch.TryGetValue(key, out equalMatches)) foreach (IndexedEntry indexed in equalMatches) {
                if (Object.ReferenceEquals(indexed, exact)) continue;
                result.Add(Copy(indexed.Entry));
                if (result.Count == limit) return result;
            }
            // The index is already in name order; collect each rank in that order.
            foreach (IndexedEntry indexed in sorted) {
                if (String.Equals(indexed.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (indexed.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)) {
                    result.Add(Copy(indexed.Entry));
                    if (result.Count == limit) return result;
                }
            }
            foreach (IndexedEntry indexed in sorted) {
                if (indexed.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (indexed.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) {
                    result.Add(Copy(indexed.Entry));
                    if (result.Count == limit) return result;
                }
            }
            if (KoreanNameSearch.HasInitials(key)) {
                // Literal name matches retain their existing priority. Match the
                // remaining names once, then prefer a whole initial match over
                // a prefix or an interior match without sorting on each input.
                int remaining = limit - result.Count;
                var ranks = new[] { new List<IndexedEntry>(), new List<IndexedEntry>(), new List<IndexedEntry>() };
                foreach (IndexedEntry indexed in sorted) {
                    if (indexed.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    int offset = KoreanNameSearch.IndexOf(indexed.Key, key);
                    if (offset < 0) continue;
                    int rank = offset == 0 ? indexed.Key.Length == key.Length ? 0 : 1 : 2;
                    if (ranks[rank].Count < remaining) ranks[rank].Add(indexed);
                    if (ranks[0].Count == remaining) break;
                }
                foreach (var rank in ranks) foreach (var indexed in rank) {
                    result.Add(Copy(indexed.Entry));
                    if (result.Count == limit) return result;
                }
            }
            return result;
        }

        IndexedEntry GetOrAdd(string name, DateTime? listingTime)
        {
            string key = MatchKey(name);
            if (key.Length == 0) return null;
            IndexedEntry indexed;
            if (!byName.TryGetValue(name, out indexed)) {
                indexed = new IndexedEntry { Key = key, Entry = new MarketSearchEntry {
                    Name = name, Category = String.Empty,
                    FetchedUtc = listingTime, QuantityKnown = listingTime.HasValue
                } };
                byName.Add(name, indexed);
            }
            return indexed;
        }

        void AddMetadata(List<MarketSnapshotItem> items, DateTime? listingTime)
        {
            if (items == null) return;
            foreach (MarketSnapshotItem item in items) {
                if (item == null) continue;
                IndexedEntry indexed = GetOrAdd(item.Name, listingTime);
                if (indexed == null) continue;
                if (String.IsNullOrWhiteSpace(indexed.Entry.Category) && !String.IsNullOrWhiteSpace(item.Category))
                    indexed.Entry.Category = item.Category.Trim();
                // Conflicting metadata cannot make an option-sensitive item safe
                // to compare. Its observed name minimum is still useful to show.
                indexed.Entry.PriceComparable = indexed.HasMetadata
                    ? indexed.Entry.PriceComparable && item.PriceComparable : item.PriceComparable;
                if (!indexed.HasMetadata) indexed.ListingMetadata = item;
                indexed.HasMetadata = true;
            }
        }

        static void ResolveEnchantScroll(IndexedEntry indexed)
        {
            MarketSearchEntry entry = indexed.Entry;
            int separator = entry.Name.LastIndexOf(" · ", StringComparison.Ordinal);
            bool named = separator > 0 && !String.IsNullOrWhiteSpace(entry.Name.Substring(0, separator))
                && enchantScrollNames.Contains(entry.Name.Substring(separator + 3));
            entry.IsEnchantScroll = enchantScrollNames.Contains(entry.Name) || named;
            entry.EnchantNameKnown = named && entry.Category == "인챈트 스크롤" && entry.PriceComparable;
            if (!entry.IsEnchantScroll || entry.EnchantNameKnown) return;

            // Older snapshots merged every enchant under its base scroll name.
            // Keep observed stock counts, but never present that mixed minimum.
            entry.UnitPrice = null;
            entry.HasListing = false;
            entry.PriceComparable = false;
            MarketSnapshotItem metadata = indexed.ListingMetadata;
            if (!indexed.HasQuote && metadata != null) {
                entry.ListingCount = metadata.ListingCount.HasValue
                    ? (int)Math.Min(Int32.MaxValue, Math.Max(0L, metadata.ListingCount.Value)) : 0;
                entry.Quantity = Math.Max(0L, metadata.ListedQuantity ?? 0);
                entry.QuantityKnown = entry.FetchedUtc.HasValue && metadata.ListedQuantity.HasValue;
            }
        }

        static DateTime? ValidTime(DateTime? value)
        {
            return value.HasValue && value.Value != DateTime.MinValue ? value : null;
        }

        static string MatchKey(string value)
        {
            return KoreanNameSearch.Normalize(value);
        }

        static MarketSearchEntry Copy(MarketSearchEntry entry)
        {
            // Callers may format or annotate results; they must not mutate the index.
            return new MarketSearchEntry { Name = entry.Name, Category = entry.Category,
                UnitPrice = entry.UnitPrice, Quantity = entry.Quantity, ListingCount = entry.ListingCount,
                QuantityKnown = entry.QuantityKnown, HasListing = entry.HasListing,
                PriceComparable = entry.PriceComparable, IsEnchantScroll = entry.IsEnchantScroll,
                EnchantNameKnown = entry.EnchantNameKnown, FetchedUtc = entry.FetchedUtc };
        }
    }

    // Shared local matching for the index and the statistics table. Store each
    // item's normalized key once; normalize the query once per search.
    public static class KoreanNameSearch
    {
        const string Initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";

        public static string Normalize(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            // NFC joins decomposed syllables without turning ordinary complete
            // syllables into initials. Modern isolated choseong share one key.
            try { value = value.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { /* Preserve malformed pasted text as literal input. */ }
            var result = new StringBuilder(value.Length);
            foreach (char c in value) if (!Char.IsWhiteSpace(c))
                result.Append(c >= '\u1100' && c <= '\u1112' ? Initials[c - '\u1100'] : c);
            return result.ToString();
        }

        public static bool HasInitials(string normalizedQuery)
        {
            if (normalizedQuery == null) return false;
            foreach (char c in normalizedQuery) if (Initials.IndexOf(c) >= 0) return true;
            return false;
        }

        public static bool Contains(string normalizedName, string normalizedQuery)
        {
            return IndexOf(normalizedName, normalizedQuery) >= 0;
        }

        public static int IndexOf(string normalizedName, string normalizedQuery)
        {
            if (String.IsNullOrEmpty(normalizedQuery)) return 0;
            if (String.IsNullOrEmpty(normalizedName) || normalizedName.Length < normalizedQuery.Length) return -1;
            if (!HasInitials(normalizedQuery)) return normalizedName.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase);
            for (int start = 0; start <= normalizedName.Length - normalizedQuery.Length; start++) {
                bool matches = true;
                for (int i = 0; i < normalizedQuery.Length; i++) {
                    char expected = normalizedQuery[i], actual = normalizedName[start + i];
                    if (Char.ToUpperInvariant(actual) == Char.ToUpperInvariant(expected)) continue;
                    if (actual >= '\uAC00' && actual <= '\uD7A3' && Initials[(actual - '\uAC00') / 588] == expected) continue;
                    matches = false; break;
                }
                if (matches) return start;
            }
            return -1;
        }
    }
}
