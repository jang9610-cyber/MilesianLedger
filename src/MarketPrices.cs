using System;

namespace MabinogiBarter
{
    public static class MarketPrices
    {
        // Older snapshots omit category prices for option-bearing equipment,
        // but retain the same name's actual minimum in the shared quote table.
        // This is a display reference, not an option-matched valuation.
        public static decimal? LowestFor(MarketSnapshotItem item, MarketSnapshotData data)
        {
            if (item == null || item.ListedQuantity == 0 || item.ListingCount == 0) return null;
            if (item.LowestListingPrice > 0) return item.LowestListingPrice;
            if (data == null || data.Quotes == null || String.IsNullOrWhiteSpace(item.Name)) return null;
            MarketSnapshotQuote quote;
            if (!data.Quotes.TryGetValue(item.Name, out quote) || quote == null
                || (!String.IsNullOrEmpty(quote.Name) && !String.Equals(quote.Name, item.Name, StringComparison.Ordinal))
                || !(quote.UnitPrice > 0) || quote.ListingCount <= 0
                || (quote.QuantityKnown && quote.Quantity <= 0) || quote.FetchedUtc == DateTime.MinValue) return null;
            if (data.ListingsFetchedUtc.HasValue && quote.FetchedUtc != data.ListingsFetchedUtc.Value) return null;
            return quote.UnitPrice;
        }
    }
}
