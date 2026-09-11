using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public static class TradePlanningVerification
    {
        static int assertions;
        static void Require(bool value, string message)
        {
            assertions++; if (!value) throw new InvalidOperationException("Trade planning: " + message);
        }

        public static string Run(Catalog catalog, TradePlanningData data)
        {
            assertions = 0;
            var serializer = new JavaScriptSerializer();
            var calculator = new TradePlanningCalculator(data);
            Require(data.Items.Count == 20 && data.Items.Select(i => i.TradeId).Distinct().Count() == 20, "all twenty items have unique source mappings");
            Require(data.Items.All(i => catalog.Trades.Any(t => t.Id == i.TradeId && t.Station == i.Station && t.Tier == i.Tier && t.Limit == i.WeeklyLimit)), "source station and tier mapping matches workbook limits");
            Require(!String.IsNullOrWhiteSpace(data.SourceUrl) && !String.IsNullOrWhiteSpace(data.CheckedAt) && data.SourceScriptSha256.Length == 64, "reference provenance is retained");
            Require(data.Ranks.Count == 16 && data.Warranties.Count == 9 && data.GrandMasters.Count == 2 && data.Rides.Count == 7 && data.Partners.Count == 4, "all source setting choices are available");
            var settings = new TradePlanningSettings();
            var sand = catalog.Trades.Single(t => t.Id == "C6");
            var estimate = calculator.EstimateTrade(sand, 25, settings);
            Require(estimate.UnitGold == 3735m && estimate.UnitDucats == 3735m && estimate.GrossGold == 93375m && estimate.GrossDucats == 93375m, "source base revenue is a gross amount in each currency");
            Require(estimate.Slots == 3 && estimate.Weight == 375m, "twenty-five ten-per-slot goods need three slots and per-unit weight");
            Require(calculator.EstimateTrade(sand, 10, settings).Slots == 1 && calculator.EstimateTrade(sand, 11, settings).Slots == 2, "stack boundary rounds by item count");
            Require(calculator.EstimateTrade(sand, 999, settings).Quantity == 25, "quantity is bounded by the weekly catalog limit");
            estimate = calculator.EstimateTrade(sand, -1, settings);
            Require(estimate.Quantity == 0 && estimate.GrossGold == 0 && estimate.Slots == 0 && estimate.Weight == 0, "negative quantities cannot generate a load or income");

            settings.RankId = "1"; settings.WarrantyId = "special-imp";
            estimate = calculator.EstimateTrade(sand, 25, settings);
            Require(estimate.BonusPercent == 165m && estimate.UnitGold == 9897m && estimate.GrossGold == 247425m, "bonuses add before flooring the unit price, not the total");
            Require(estimate.UnitDucats == estimate.UnitGold && estimate.GrossDucats == estimate.GrossGold, "Gold and Ducat use the same published adjusted sale reference");
            settings.GrandMasterId = "yes"; settings.RideId = "airship"; settings.PartnerId = "william";
            Require(calculator.EstimateTrade(sand, 25, settings).GrossGold == 247425m, "transport bonuses do not change sale revenue");
            var capacity = calculator.Capacity(settings);
            Require(capacity.IsValid && capacity.Slots == 10 && capacity.Weight == 1500m, "transport, grandmaster and partner capacities add");
            settings.PartnerId = "alpaca";
            capacity = calculator.Capacity(settings);
            Require(!capacity.IsValid && capacity.Slots == 0 && capacity.Weight == 0 && !String.IsNullOrWhiteSpace(capacity.Warning), "alpaca with a non-wagon transport has no usable capacity");
            settings.Normalize(data);
            Require(settings.PartnerId == "alpaca" && settings.RideId == "airship", "known invalid combination stays visible for correction");
            settings.RideId = "wagon";
            capacity = calculator.Capacity(settings);
            Require(capacity.IsValid && capacity.Slots == 11 && capacity.Weight == 1200m, "alpaca accepts the wagon and adds its capacity");
            foreach (var ride in data.Rides.Where(r => r.Id != "wagon"))
            {
                settings.RideId = ride.Id;
                Require(!calculator.Capacity(settings).IsValid, "alpaca rejects transport " + ride.Id);
            }

            var invalidSettings = new TradePlanningSettings { RankId = "removed", WarrantyId = null, GrandMasterId = "removed", RideId = null, PartnerId = "removed" };
            string invalidBefore = serializer.Serialize(invalidSettings);
            Require(calculator.TotalBonusPercent(invalidSettings) == 0 && calculator.Capacity(invalidSettings).Slots == 4, "calculations safely use defaults for unknown settings");
            Require(serializer.Serialize(invalidSettings) == invalidBefore, "calculation does not overwrite input settings");
            invalidSettings.Normalize(data);
            Require(invalidSettings.RankId == "practice" && invalidSettings.WarrantyId == "none" && invalidSettings.GrandMasterId == "none"
                && invalidSettings.RideId == "backpack" && invalidSettings.PartnerId == "none", "explicit normalization restores each unknown setting to its default");
            settings = new TradePlanningSettings { RankId = "A", WarrantyId = "ogre", GrandMasterId = "yes", RideId = "camel", PartnerId = "trade" };
            var restored = serializer.Deserialize<TradePlanningSettings>(serializer.Serialize(settings)); restored.Normalize(data);
            Require(serializer.Serialize(restored) == serializer.Serialize(settings), "settings survive a JSON save and restore");

            var state = new ProgressState { Selected = catalog.Trades.ToDictionary(t => t.Id, t => true), Targets = catalog.Trades.ToDictionary(t => t.Id, t => t.Limit) };
            state.Checks["test-unchanged"] = true; state.ProcurementChoices["실리엔"] = "default";
            string before = serializer.Serialize(state);
            settings = new TradePlanningSettings();
            var total = calculator.Summarize(catalog, state, settings);
            Require(total.Items.Count == 20 && total.Quantity == 244 && total.Slots == 44 && total.Weight == 4360m, "all source weekly quantities aggregate with per-item slot rounding");
            Require(total.GrossGold == 4126886m && total.GrossDucats == 4126886m, "all twenty published base values produce the independent gross total");
            Require(total.MinimumTrips == 11, "all weekly goods need at least eleven default-capacity loads");
            var oasis = calculator.Summarize(catalog, state, settings, "오아시스");
            Require(oasis.Items.Count == 5 && oasis.Quantity == 61 && oasis.Slots == 11 && oasis.Weight == 1090m && oasis.GrossGold == 943787m, "station subtotal includes only selected station goods");
            Require(oasis.MinimumTrips == 3, "station capacity lower bound uses the larger dimension");
            state.Selected["C6"] = false;
            Require(calculator.Summarize(catalog, state, settings, "오아시스").Quantity == 36, "deselection removes stored quantities from transport and sale totals");
            state.Selected["C6"] = true;
            Require(serializer.Serialize(state) == before, "summary and estimate preserve selection, targets, checks and procurement state");

            settings.RideId = "airship"; settings.PartnerId = "alpaca";
            Require(calculator.Summarize(catalog, state, settings).MinimumTrips == null, "incompatible transport never reports a feasible trip count");
            foreach (var key in state.Selected.Keys.ToList()) state.Selected[key] = false;
            total = calculator.Summarize(catalog, state, settings);
            Require(total.Items.Count == 0 && total.Quantity == 0 && total.GrossGold == 0 && total.MinimumTrips == 0, "an empty selection has no required load even with an invalid saved combination");

            string presetStateBefore = serializer.Serialize(state), presetCatalogBefore = serializer.Serialize(catalog), presetDataBefore = serializer.Serialize(data);
            var faithful = calculator.CreatePreset(catalog, "성실한 상인");
            Require(faithful.Count == 20 && faithful.Values.Count(q => q > 0) == 20 && faithful.Values.Sum() == 244, "faithful preset fills every weekly item across all four stations");
            Require(catalog.Trades.All(t => faithful[t.Id] == t.Limit), "faithful preset maps every trade ID to its weekly maximum");
            var cherry = calculator.CreatePreset(catalog, "체리피커");
            Require(cherry.Count == 20 && cherry.Values.Count(q => q > 0) == 12 && cherry.Values.Sum() == 84, "cherry preset fills twelve high-tier items across all four stations");
            Require(catalog.Trades.All(t => cherry[t.Id] == (t.Tier >= 3 ? t.Limit : 0)), "cherry preset retains zero entries for all tier-one and tier-two goods");
            var extreme = calculator.CreatePreset(catalog, "극한의 한탕");
            Require(extreme.Count == 20 && extreme.Values.Count(q => q > 0) == 10 && extreme.Values.Sum() == 54, "extreme preset retains all twenty IDs and ten source stacks totaling fifty-four goods");
            Require(catalog.Trades.All(t => extreme[t.Id] == (t.Station == "칼리다 호수" ? (t.Tier == 5 ? 3 : 0) : t.Tier == 3 || t.Tier == 4 ? 7 : t.Tier == 5 ? 3 : 0)), "extreme source map includes only Calida tier five and other stations tier three through five");
            Require(serializer.Serialize(state) == presetStateBefore && serializer.Serialize(catalog) == presetCatalogBefore && serializer.Serialize(data) == presetDataBefore, "creating all presets preserves caller progress, catalog and reference data");
            faithful["C6"] = 0;
            Require(calculator.CreatePreset(catalog, "성실한 상인")["C6"] == 25 && cherry["C6"] == 0, "returned preset maps are independent and cannot alter later presets");
            state.Selected = extreme.ToDictionary(p => p.Key, p => p.Value > 0); state.Targets = new Dictionary<string, int>(extreme);
            settings = new TradePlanningSettings { RideId = "airship", GrandMasterId = "yes", PartnerId = "william" };
            total = calculator.Summarize(catalog, state, settings);
            Require(total.Slots == 10 && total.Weight == 1305m && total.MinimumTrips == 1, "extreme reference fits the aggregate lower bound only with enough slots and weight");
            settings.RideId = "wagon"; settings.PartnerId = "alpaca";
            Require(calculator.Summarize(catalog, state, settings).MinimumTrips == 2, "preset name never hardcodes one trip when selected capacity is insufficient");
            bool rejected = false;
            try { calculator.CreatePreset(catalog, "없는 프리셋"); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "unknown preset names are rejected");
            rejected = false;
            try { calculator.CreatePreset(catalog, null); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "null preset names are rejected");

            var copy = TradePlanningData.FromJson(serializer.Serialize(data), catalog);
            Require(copy.Items.Count == 20 && copy.Items.Single(i => i.TradeId == "C6").AverageUnitDucats == 3735m, "reference data loads and round trips");
            copy.Items[0].MaxStack = 0; rejected = false;
            try { TradePlanningData.FromJson(serializer.Serialize(copy), catalog); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "invalid zero stack capacity fails data validation");
            return "PASS trade planning: " + assertions + " assertions; reference mapping, sale bonuses, unit rounding, capacity restrictions, selected totals, preset quantities and settings persistence.";
        }
    }
}
