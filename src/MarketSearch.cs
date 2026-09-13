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
        public bool QuantityKnown, HasListing, PriceComparable;
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
        }

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
                indexed.HasMetadata = true;
            }
        }

        static DateTime? ValidTime(DateTime? value)
        {
            return value.HasValue && value.Value != DateTime.MinValue ? value : null;
        }

        static string MatchKey(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            var result = new StringBuilder(value.Length);
            foreach (char c in value) if (!Char.IsWhiteSpace(c)) result.Append(c);
            return result.ToString();
        }

        static MarketSearchEntry Copy(MarketSearchEntry entry)
        {
            // Callers may format or annotate results; they must not mutate the index.
            return new MarketSearchEntry { Name = entry.Name, Category = entry.Category,
                UnitPrice = entry.UnitPrice, Quantity = entry.Quantity, ListingCount = entry.ListingCount,
                QuantityKnown = entry.QuantityKnown, HasListing = entry.HasListing,
                PriceComparable = entry.PriceComparable, FetchedUtc = entry.FetchedUtc };
        }
    }
}
