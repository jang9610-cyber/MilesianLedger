using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public sealed class TradePlanningOption
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public decimal Percent { get; set; }
        public int Slots { get; set; }
        public decimal Weight { get; set; }
        public string RequiredRideId { get; set; }
        public override string ToString() { return Name; }
    }

    public sealed class TradePlanningItem
    {
        public string TradeId { get; set; }
        public string Station { get; set; }
        public int Tier { get; set; }
        public string SourceKey { get; set; }
        public string SourceName { get; set; }
        public decimal AverageUnitDucats { get; set; }
        public int WeeklyLimit { get; set; }
        public int MaxStack { get; set; }
        public decimal WeightPerUnit { get; set; }
        public int ExtremeQuantity { get; set; }
    }

    public sealed class TradePlanningData
    {
        public string Version { get; set; }
        public string SourceUrl { get; set; }
        public string SourceScriptUrl { get; set; }
        public string SourceScriptSha256 { get; set; }
        public string CheckedAt { get; set; }
        public List<string> Notes { get; set; }
        public List<TradePlanningItem> Items { get; set; }
        public List<TradePlanningOption> Ranks { get; set; }
        public List<TradePlanningOption> Warranties { get; set; }
        public List<TradePlanningOption> GrandMasters { get; set; }
        public List<TradePlanningOption> Rides { get; set; }
        public List<TradePlanningOption> Partners { get; set; }

        public static TradePlanningData Load(string path, Catalog catalog)
        {
            return FromJson(File.ReadAllText(path, Encoding.UTF8), catalog);
        }

        public static TradePlanningData FromJson(string json, Catalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            var data = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Deserialize<TradePlanningData>(json);
            if (data == null || data.Items == null || data.Items.Count != catalog.Trades.Count)
                throw new InvalidDataException("교역 계획 데이터의 품목 수가 올바르지 않습니다.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.Items)
            {
                var trade = item == null ? null : catalog.Trades.FirstOrDefault(t => t.Id == item.TradeId);
                if (item == null || trade == null || !ids.Add(item.TradeId) || item.Station != trade.Station || item.Tier != trade.Tier
                    || item.WeeklyLimit != trade.Limit || item.MaxStack <= 0 || item.WeightPerUnit <= 0 || item.AverageUnitDucats < 0
                    || item.ExtremeQuantity < 0 || item.ExtremeQuantity > item.WeeklyLimit)
                    throw new InvalidDataException("교역 계획 데이터의 품목 연결 또는 수량이 올바르지 않습니다.");
            }
            ValidateOptions(data.Ranks); ValidateOptions(data.Warranties); ValidateOptions(data.GrandMasters);
            ValidateOptions(data.Rides); ValidateOptions(data.Partners);
            if (data.Rides.Any(o => o.Slots <= 0 || o.Weight <= 0)
                || data.Partners.Any(o => !String.IsNullOrEmpty(o.RequiredRideId) && !data.Rides.Any(r => r.Id == o.RequiredRideId)))
                throw new InvalidDataException("운송수단 또는 파트너 제한이 올바르지 않습니다.");
            if (data.Notes == null) data.Notes = new List<string>();
            return data;
        }

        static void ValidateOptions(List<TradePlanningOption> options)
        {
            if (options == null || options.Count == 0) throw new InvalidDataException("교역 설정 목록이 비어 있습니다.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
                if (option == null || String.IsNullOrWhiteSpace(option.Id) || !ids.Add(option.Id) || String.IsNullOrWhiteSpace(option.Name)
                    || option.Percent < 0 || option.Slots < 0 || option.Weight < 0)
                    throw new InvalidDataException("교역 설정 값이 올바르지 않습니다.");
        }
    }

    public sealed class TradePlanningSettings
    {
        public string RankId { get; set; }
        public string WarrantyId { get; set; }
        public string GrandMasterId { get; set; }
        public string RideId { get; set; }
        public string PartnerId { get; set; }
        public TradePlanningSettings()
        {
            RankId = "practice"; WarrantyId = "none"; GrandMasterId = "none"; RideId = "backpack"; PartnerId = "none";
        }
        public void Normalize(TradePlanningData data)
        {
            if (data == null) throw new ArgumentNullException("data");
            RankId = ValidId(RankId, data.Ranks); WarrantyId = ValidId(WarrantyId, data.Warranties);
            GrandMasterId = ValidId(GrandMasterId, data.GrandMasters); RideId = ValidId(RideId, data.Rides);
            PartnerId = ValidId(PartnerId, data.Partners);
            // Keep a known but incompatible partner choice visible so the user
            // can correct it. Capacity reports that combination as invalid.
        }
        static string ValidId(string id, List<TradePlanningOption> options)
        {
            return options.Any(o => o.Id == id) ? id : options[0].Id;
        }
        internal TradePlanningSettings NormalizedCopy(TradePlanningData data)
        {
            var copy = new TradePlanningSettings { RankId = RankId, WarrantyId = WarrantyId, GrandMasterId = GrandMasterId, RideId = RideId, PartnerId = PartnerId };
            copy.Normalize(data); return copy;
        }
    }

    public sealed class TradeCapacity
    {
        public bool IsValid { get; set; }
        public int Slots { get; set; }
        public decimal Weight { get; set; }
        public string Warning { get; set; }
    }

    public sealed class TradePlanningEstimate
    {
        public string TradeId { get; set; }
        public int Quantity { get; set; }
        public decimal BonusPercent { get; set; }
        public decimal UnitGold { get; set; }
        public decimal UnitDucats { get; set; }
        public decimal GrossGold { get; set; }
        public decimal GrossDucats { get; set; }
        public int Slots { get; set; }
        public decimal Weight { get; set; }
    }

    public sealed class TradePlanningSummary
    {
        public List<TradePlanningEstimate> Items { get; set; }
        public int Quantity { get; set; }
        public decimal GrossGold { get; set; }
        public decimal GrossDucats { get; set; }
        public int Slots { get; set; }
        public decimal Weight { get; set; }
        public TradeCapacity Capacity { get; set; }
        public int? MinimumTrips { get; set; }
        public TradePlanningSummary() { Items = new List<TradePlanningEstimate>(); }
    }

    // Public reference data and pure arithmetic only. This component never
    // requests prices, changes selected goods, or updates readiness records.
    public sealed class TradePlanningCalculator
    {
        readonly TradePlanningData data;
        readonly Dictionary<string, TradePlanningItem> items;
        public static readonly string[] BuiltinPresetNames = { "성실한 상인", "체리피커", "극한의 한탕" };

        public TradePlanningCalculator(TradePlanningData data)
        {
            if (data == null) throw new ArgumentNullException("data");
            this.data = data; items = data.Items.ToDictionary(i => i.TradeId, StringComparer.Ordinal);
        }

        public TradePlanningItem GetItem(string tradeId)
        {
            TradePlanningItem item;
            if (tradeId == null || !items.TryGetValue(tradeId, out item)) throw new ArgumentException("교역 계획에 없는 품목입니다: " + tradeId);
            return item;
        }

        TradePlanningSettings Settings(TradePlanningSettings settings)
        {
            return (settings ?? new TradePlanningSettings()).NormalizedCopy(data);
        }

        public decimal TotalBonusPercent(TradePlanningSettings settings)
        {
            var chosen = Settings(settings);
            return data.Ranks.Single(o => o.Id == chosen.RankId).Percent + data.Warranties.Single(o => o.Id == chosen.WarrantyId).Percent;
        }

        public TradePlanningEstimate EstimateTrade(TradeItem trade, int quantity, TradePlanningSettings settings)
        {
            if (trade == null) throw new ArgumentNullException("trade");
            var item = GetItem(trade.Id);
            int count = Math.Max(0, Math.Min(quantity, Math.Min(trade.Limit, item.WeeklyLimit)));
            decimal bonus = TotalBonusPercent(settings);
            // Source parseInt truncates a positive adjusted unit price before
            // multiplying by quantity. Gold and Ducat use that same reference.
            decimal unit = Math.Floor(item.AverageUnitDucats * (1m + bonus / 100m));
            return new TradePlanningEstimate { TradeId = trade.Id, Quantity = count, BonusPercent = bonus,
                UnitGold = unit, UnitDucats = unit, GrossGold = unit * count, GrossDucats = unit * count,
                Slots = (int)Math.Ceiling((decimal)count / item.MaxStack), Weight = count * item.WeightPerUnit };
        }

        public TradeCapacity Capacity(TradePlanningSettings settings)
        {
            var chosen = Settings(settings);
            var ride = data.Rides.Single(o => o.Id == chosen.RideId);
            var master = data.GrandMasters.Single(o => o.Id == chosen.GrandMasterId);
            var partner = data.Partners.Single(o => o.Id == chosen.PartnerId);
            if (!String.IsNullOrEmpty(partner.RequiredRideId) && partner.RequiredRideId != ride.Id)
                return new TradeCapacity { IsValid = false, Warning = "알파카는 마차와 함께 사용해야 합니다." };
            return new TradeCapacity { IsValid = true, Slots = ride.Slots + master.Slots + partner.Slots,
                Weight = ride.Weight + master.Weight + partner.Weight, Warning = "" };
        }

        public TradePlanningSummary Summarize(Catalog catalog, ProgressState state, TradePlanningSettings settings, string station = null)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            var summary = new TradePlanningSummary { Capacity = Capacity(settings) };
            foreach (var trade in catalog.Trades.Where(t => station == null || t.Station == station))
            {
                int quantity = Calculator.EffectiveTarget(state, trade);
                if (quantity <= 0) continue;
                var estimate = EstimateTrade(trade, quantity, settings); summary.Items.Add(estimate);
                summary.Quantity += estimate.Quantity; summary.GrossGold += estimate.GrossGold; summary.GrossDucats += estimate.GrossDucats;
                summary.Slots += estimate.Slots; summary.Weight += estimate.Weight;
            }
            // This is a capacity lower bound, not a bin-packing result or route.
            summary.MinimumTrips = summary.Quantity == 0 ? (int?)0 : !summary.Capacity.IsValid ? (int?)null
                : (int)Math.Max(Math.Ceiling((decimal)summary.Slots / summary.Capacity.Slots), Math.Ceiling(summary.Weight / summary.Capacity.Weight));
            return summary;
        }

        public Dictionary<string, int> CreatePreset(Catalog catalog, string presetName)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            if (!BuiltinPresetNames.Contains(presetName)) throw new ArgumentException("알 수 없는 기본 프리셋입니다.");
            // Built-ins always describe the complete catalog, independently of
            // whichever station the UI currently shows. Excluded goods are zero.
            var quantities = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var trade in catalog.Trades)
            {
                var item = GetItem(trade.Id);
                int quantity = presetName == "극한의 한탕" ? item.ExtremeQuantity
                    : presetName == "성실한 상인" || trade.Tier >= 3 ? item.WeeklyLimit : 0;
                quantities.Add(trade.Id, Math.Max(0, Math.Min(quantity, trade.Limit)));
            }
            return quantities;
        }
    }
}
