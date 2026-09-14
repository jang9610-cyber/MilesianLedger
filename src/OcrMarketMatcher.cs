using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MabinogiBarter
{
    public sealed class OcrMarketMatch
    {
        public string Text, OriginalText;
        public List<MarketSearchEntry> Candidates = new List<MarketSearchEntry>();
        public bool Exact;
    }

    // OCR is only a name suggestion source. Prices come from exact snapshot
    // identities; a spelling suggestion or an enchant alias requires a choice.
    public sealed class OcrMarketMatcher
    {
        public const int MaximumRows = 100, MaximumLineLength = 512;
        const int MaximumInputCharacters = 32768, MaximumInputLines = 256, MaximumCandidates = 5, MaximumPrioritizedMatches = 512;
        readonly MarketSearchIndex search;
        readonly Dictionary<string, List<MarketSearchEntry>> exact = new Dictionary<string, List<MarketSearchEntry>>(StringComparer.Ordinal);
        readonly Dictionary<string, List<MarketSearchEntry>> enchantAliases = new Dictionary<string, List<MarketSearchEntry>>(StringComparer.Ordinal);
        readonly Dictionary<char, List<string>> starts = new Dictionary<char, List<string>>();
        readonly List<string> keys = new List<string>();
        static readonly Regex spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);
        static readonly Regex endQuantity = new Regex(@"\s*(?:[xX×]\s*\d[\d,]*|\(?\s*\d[\d,]*\s*개\s*\)?|\[\s*\d[\d,]*\s*개\s*\])\s*$", RegexOptions.CultureInvariant);
        static readonly Regex startQuantity = new Regex(@"^\d[\d,]*\s*[xX×]\s+", RegexOptions.CultureInvariant);
        static readonly Regex decoration = new Regex(@"^[\s•●◆◇▶*·|,;:/\[\]()]+|[\s•●◆◇▶*·|,;:/\[\]()]+$", RegexOptions.CultureInvariant);
        static readonly Regex quantityToken = new Regex(@"(?:[xX×]\s*\d[\d,]*|\d[\d,]*\s*개)", RegexOptions.CultureInvariant);
        static readonly Regex attachedQuantity = new Regex(@"^(?:[xX×]\s*\d[\d,]*|\d[\d,]*\s*개)(?=$|[\s|,;/])", RegexOptions.CultureInvariant);

        public OcrMarketMatcher(MarketSnapshotData data)
        {
            search = new MarketSearchIndex(data);
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (data != null) {
                if (data.Quotes != null) foreach (var pair in data.Quotes)
                    if (pair.Value != null) names.Add(String.IsNullOrWhiteSpace(pair.Value.Name) ? pair.Key : pair.Value.Name);
                AddNames(names, data.Items24h); AddNames(names, data.Items7d);
            }
            var ordered = new List<string>(names); ordered.Sort(StringComparer.Ordinal);
            foreach (string name in ordered) {
                if (String.IsNullOrWhiteSpace(name) || name.Length > MaximumLineLength) continue;
                List<MarketSearchEntry> found = search.Search(name, 1);
                if (found.Count == 0 || found[0].Name != name) continue;
                MarketSearchEntry entry = found[0]; string key = Key(name);
                Add(exact, key, entry);
                if (entry.IsEnchantScroll && entry.EnchantNameKnown) {
                    int separator = name.LastIndexOf(" · ", StringComparison.Ordinal);
                    string prefix = name.Substring(0, separator);
                    int rank = prefix.IndexOf(" (", StringComparison.Ordinal);
                    string alias = rank > 0 ? prefix.Substring(0, rank) : prefix;
                    Add(enchantAliases, Key(alias), entry);
                    Add(enchantAliases, Key(alias + " " + name.Substring(separator + 3)), entry);
                }
            }
            keys.AddRange(exact.Keys); keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys) {
                List<string> bucket;
                if (!starts.TryGetValue(key[0], out bucket)) { bucket = new List<string>(); starts.Add(key[0], bucket); }
                bucket.Add(key);
            }
            foreach (List<string> bucket in starts.Values) bucket.Sort(delegate(string a, string b) {
                int length = b.Length.CompareTo(a.Length); return length != 0 ? length : StringComparer.Ordinal.Compare(a, b);
            });
        }

        public List<OcrMarketMatch> Resolve(IEnumerable<string> lines, bool prioritizeItems = false, bool marketItemsOnly = false)
        {
            var result = new List<OcrMarketMatch>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            if (lines == null || (marketItemsOnly && search.Count == 0)) return result;
            int characters = 0, lineCount = 0;
            foreach (string input in lines) {
                if (lineCount++ >= MaximumInputLines || characters >= MaximumInputCharacters || (!prioritizeItems && result.Count >= MaximumRows)) break;
                if (String.IsNullOrWhiteSpace(input)) continue;
                string limited = input.Substring(0, Math.Min(input.Length, Math.Min(MaximumLineLength, MaximumInputCharacters - characters)));
                characters += limited.Length;
                foreach (string raw in limited.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                    if (!prioritizeItems && result.Count >= MaximumRows) break;
                    string original = spaces.Replace(raw, " ").Trim();
                    if (original.Length == 0) continue;
                    string text = Clean(original);
                    if (text.Length == 0) continue;
                    List<MarketSearchEntry> matches;
                    if (exact.TryGetValue(Key(text), out matches)) {
                        Append(result, seen, Match(text, original, matches, true), prioritizeItems, marketItemsOnly); continue;
                    }
                    List<OcrMarketMatch> packed = Packed(text, original);
                    if (packed.Count > 1) {
                        foreach (OcrMarketMatch match in packed) Append(result, seen, match, prioritizeItems, marketItemsOnly);
                    } else Append(result, seen, Match(text, original, Suggest(text), false), prioritizeItems, marketItemsOnly);
                }
            }
            if (prioritizeItems) {
                // A whole-screen capture may contain HUD text before any loot.
                // Scan the bounded input first, then retain OCR order within each
                // confidence group instead of letting the first 100 HUD rows win.
                var prioritized = new List<OcrMarketMatch>();
                for (int rank = 0; rank < 3; rank++) foreach (OcrMarketMatch match in result) {
                    if (Rank(match) == rank) prioritized.Add(match);
                    if (prioritized.Count == MaximumRows) return prioritized;
                }
                return prioritized;
            }
            return result;
        }

        string Clean(string value)
        {
            // An official name wins before decorations/quantity removal, because
            // quantities in a product or bundle name can be meaningful.
            if (exact.ContainsKey(Key(value))) return value;
            string clean = decoration.Replace(value, "").Trim();
            if (exact.ContainsKey(Key(clean))) return clean;
            clean = startQuantity.Replace(clean, "");
            clean = endQuantity.Replace(clean, "").Trim();
            return decoration.Replace(clean, "").Trim();
        }

        List<OcrMarketMatch> Packed(string text, string original)
        {
            var normalized = new StringBuilder(); var positions = new List<int>();
            for (int i = 0; i < text.Length; i++) if (!Char.IsWhiteSpace(text[i])) {
                normalized.Append(Char.ToUpperInvariant(text[i])); positions.Add(i);
            }
            string key = normalized.ToString(); var covered = new bool[text.Length]; var result = new List<OcrMarketMatch>();
            for (int start = 0; start < key.Length; start++) {
                int rawStart = positions[start];
                if (rawStart > 0 && Word(text[rawStart - 1])) continue;
                List<string> bucket; if (!starts.TryGetValue(key[start], out bucket)) continue;
                foreach (string candidate in bucket) {
                    if (candidate.Length > key.Length - start || String.CompareOrdinal(key, start, candidate, 0, candidate.Length) != 0) continue;
                    int rawEnd = positions[start + candidate.Length - 1] + 1;
                    if (rawEnd < text.Length && Word(text[rawEnd]) && !attachedQuantity.IsMatch(text.Substring(rawEnd))) continue;
                    for (int p = rawStart; p < rawEnd; p++) covered[p] = true;
                    result.Add(Match(text.Substring(rawStart, rawEnd - rawStart), original, exact[candidate], true));
                    start += candidate.Length - 1; break;
                }
            }
            var rest = new StringBuilder();
            for (int i = 0; i < text.Length; i++) rest.Append(covered[i] ? ' ' : text[i]);
            string remainder = quantityToken.Replace(rest.ToString(), "");
            // Do not turn a fragment of an unknown longer item or surrounding
            // UI sentence into a confirmed item. Keep the original editable row.
            if (decoration.Replace(remainder, "").Trim().Length > 0) result.Clear();
            return result;
        }

        List<MarketSearchEntry> Suggest(string text)
        {
            string key = Key(text); List<MarketSearchEntry> aliases;
            if (enchantAliases.TryGetValue(key, out aliases)) return aliases;
            List<MarketSearchEntry> matches = search.Search(text, MaximumCandidates);
            if (matches.Count > 0 || key.Length < 2 || key.Length > 80) return matches;
            int maximumDistance = key.Length < 7 ? 1 : 2, best = maximumDistance + 1;
            foreach (string candidate in keys) {
                if (Math.Abs(candidate.Length - key.Length) > maximumDistance) continue;
                int distance = Distance(key, candidate, maximumDistance);
                if (distance > maximumDistance || distance > best) continue;
                if (distance < best) { matches.Clear(); best = distance; }
                foreach (MarketSearchEntry entry in exact[candidate]) {
                    if (matches.Count >= MaximumCandidates) break;
                    matches.Add(entry);
                }
            }
            return matches;
        }

        static int Distance(string a, string b, int limit)
        {
            var previous = new int[b.Length + 1]; var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;
            for (int i = 1; i <= a.Length; i++) {
                current[0] = i; int minimum = i;
                for (int j = 1; j <= b.Length; j++) {
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    minimum = Math.Min(minimum, current[j]);
                }
                if (minimum > limit) return limit + 1;
                var swap = previous; previous = current; current = swap;
            }
            return previous[b.Length];
        }

        static OcrMarketMatch Match(string text, string original, List<MarketSearchEntry> candidates, bool exactMatch)
        {
            var match = new OcrMarketMatch { Text = text, OriginalText = original, Exact = exactMatch && candidates.Count == 1 };
            foreach (MarketSearchEntry entry in candidates) {
                if (match.Candidates.Count == MaximumCandidates) break;
                match.Candidates.Add(Copy(entry));
            }
            if (match.Exact) match.Text = match.Candidates[0].Name;
            return match;
        }
        static int Rank(OcrMarketMatch match) { return match.Exact ? 0 : match.Candidates.Count > 0 ? 1 : 2; }
        static void Append(List<OcrMarketMatch> rows, HashSet<string> seen, OcrMarketMatch match, bool prioritizeItems, bool marketItemsOnly)
        {
            // Filter before the output/buffer cap, so a HUD paragraph cannot
            // consume the result slots before a later known item is examined.
            // A known identity remains eligible even without a current listing.
            if (marketItemsOnly && match.Candidates.Count == 0) return;
            string key = Key(match.Text);
            if (seen.Contains(key)) return;
            int limit = prioritizeItems ? MaximumPrioritizedMatches : MaximumRows;
            if (rows.Count >= limit) {
                if (!prioritizeItems) return;
                // Packed lines can exceed the bounded buffer. Later confirmed
                // items may replace lower-confidence rows, but never earlier
                // rows of equal confidence, preserving stable output ordering.
                int replace = -1, worst = Rank(match);
                for (int i = rows.Count - 1; i >= 0; i--) if (Rank(rows[i]) > worst) { replace = i; worst = Rank(rows[i]); }
                if (replace < 0) return;
                seen.Remove(Key(rows[replace].Text)); rows.RemoveAt(replace);
            }
            seen.Add(key); rows.Add(match);
        }
        static string Key(string value) { return KoreanNameSearch.Normalize(value).ToUpperInvariant(); }
        static bool Word(char value) { return Char.IsLetterOrDigit(value) || value == '_'; }
        static void AddNames(HashSet<string> names, List<MarketSnapshotItem> items)
        { if (items != null) foreach (MarketSnapshotItem item in items) if (item != null) names.Add(item.Name); }
        static void Add(Dictionary<string, List<MarketSearchEntry>> dictionary, string key, MarketSearchEntry entry)
        {
            if (key.Length == 0) return;
            List<MarketSearchEntry> items;
            if (!dictionary.TryGetValue(key, out items)) { items = new List<MarketSearchEntry>(); dictionary.Add(key, items); }
            foreach (MarketSearchEntry item in items) if (item.Name == entry.Name) return;
            items.Add(entry);
        }
        static MarketSearchEntry Copy(MarketSearchEntry entry)
        {
            return new MarketSearchEntry { Name = entry.Name, Category = entry.Category, UnitPrice = entry.UnitPrice,
                Quantity = entry.Quantity, ListingCount = entry.ListingCount, QuantityKnown = entry.QuantityKnown,
                HasListing = entry.HasListing, PriceComparable = entry.PriceComparable, IsEnchantScroll = entry.IsEnchantScroll,
                EnchantNameKnown = entry.EnchantNameKnown, FetchedUtc = entry.FetchedUtc };
        }
    }
}
