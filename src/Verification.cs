using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MabinogiBarter
{
    // Independent controls transcribed from the cached values in the supplied
    // 물물교환 worksheet. These are not generated from the application catalog.
    public static class Verification
    {
        private static int assertions;
        private const string TradeControls =
            "C6:25 C10:15 C13:10 C15:8 C20:3 C31:25 C33:15 C37:10 C39:8 C44:3 " +
            "K6:25 K10:15 K12:10 K15:8 K21:3 K34:25 K38:15 K42:10 K48:8 K52:3";
        private const string GroupControls =
            "D6:50 D7:75 D10:15 D12:30 D13:10 D14:30 D15:8 D18:16 D19:32 D20:3 D22:9 D24:15 " +
            "D31:50 D32:100 D33:30 D34:15 D37:50 D38:20 D39:40 D40:32 D41:8 D44:15 D47:3 D51:6 " +
            "L6:50 L7:75 L10:30 L11:45 L12:50 L13:30 L15:40 L16:16 L19:40 L21:3 L24:9 L26:6 " +
            "L34:50 L35:25 L38:15 L40:15 L42:30 L44:20 L48:8 L50:8 L51:40 L52:9 L53:9 L56:9";
        private const string LineControls =
            "F6.1:50 F6.2:50 D7.purchase:75 F10:75 F11:300 F12:180 F13:10 F14:150 F15:8 F16:8 F17:16 " +
            "F18:16 F19:160 F20:3 F21:3 F22:9 F23:180 F24:15 F25:15 F26:15 " +
            "F31:250 F32.1:10 F32.2:10 F32.3:10 F33:150 F34:15 F35:150 F36:150 F37:500 F38:300 F39:120 F40:160 " +
            "F41:16 F42:40 F43:40 F44:15 F45:150 F46:150 F47:3 F48:3 F49:3 F50:3 F51:18 F52:18 F53:18 F54:18 " +
            "N6:50 N7:75 N8:75 N9:75 N10:30 N11:450 N12:50 L13.purchase:30 N15:240 L16.purchase:16 " +
            "N19:40 N20.1:4 N20.2:4 N20.3:4 N20.4:4 N21:30 N22:15 N23:9 N24:9 N25:9 N26:6 N27:6 N28:60 N29:60 " +
            "N34:150 L35.purchase:25 N38:15 N39:15 N40:15 N41:15 N42:30 N43:30 N44:20 " +
            "N45:20 N46:20 N47:20 N48:160 N49:40 N50:8 N51:40 N52:180 N53:3 N54:3 N55:3 N56:27";
        private const string AlternateControls =
            "F10:375 F12:900 F14:750 F19:800 F22:9 F33:750 F40:800 F49:15 F50:15 N15:1200 N34:450 N56:243";
        private const string OwnershipControls =
            "C6=D6,D7 C10=D10,D12 C13=D13,D14 C15=D15,D18,D19 C20=D20,D22,D24 " +
            "C31=D31,D32 C33=D33,D34 C37=D37,D38 C39=D39,D40,D41 C44=D44,D47,D51 " +
            "K6=L6,L7 K10=L10,L11 K12=L12,L13 K15=L15,L16,L19 K21=L21,L24,L26 " +
            "K34=L34,L35 K38=L38,L40 K42=L42,L44 K48=L48,L50,L51 K52=L52,L53,L56";

        public static string Run(Catalog catalog)
        {
            assertions = 0;
            Dictionary<string, decimal> tradeExpected = Parse(TradeControls);
            Dictionary<string, decimal> groupExpected = Parse(GroupControls);
            Dictionary<string, decimal> lineExpected = Parse(LineControls);
            Dictionary<string, decimal> alternateExpected = Parse(AlternateControls);
            Dictionary<string, TradeItem> trades = catalog.Trades.ToDictionary(t => t.Id);
            Dictionary<string, MaterialGroup> groups = catalog.Trades.SelectMany(t => t.Groups).ToDictionary(g => g.Id);
            Dictionary<string, MaterialLine> lines = groups.Values.SelectMany(g => g.Lines).ToDictionary(l => l.Id);
            Require(trades.Count == 20, "All 20 trades must be present.");
            Require(catalog.Trades.Select(t => t.Station).Distinct().Count() == 4, "Four stations are required.");
            Require(groups.Count == groupExpected.Count, "Direct material group count differs from the source.");
            Require(lines.Count == lineExpected.Count, "Detail material line count differs from the source.");
            foreach (var station in catalog.Trades.GroupBy(t => t.Station))
                Require(station.Select(t => t.Tier).OrderBy(x => x).SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "Station tiers: " + station.Key);
            foreach (string ownership in OwnershipControls.Split(' '))
            {
                string[] pair = ownership.Split('=');
                Require(trades.ContainsKey(pair[0]), "Missing trade " + pair[0]);
                Require(trades[pair[0]].Groups.Select(g => g.Id).OrderBy(id => id).SequenceEqual(pair[1].Split(',').OrderBy(id => id)), "Material group ownership " + pair[0]);
            }

            ProgressState state = new ProgressState();
            state.Normalize(catalog);
            Require(catalog.Trades.All(t => !Calculator.IsSelected(state, t)), "New plans start with no trade selected.");
            Require(Calculator.Summarize(catalog, state, true).Count == 0 && Calculator.Summarize(catalog, state, false).Count == 0, "Unselected target defaults do not create required materials.");
            foreach (var expected in tradeExpected)
            {
                Require(trades.ContainsKey(expected.Key), "Missing trade " + expected.Key);
                TradeItem trade = trades[expected.Key];
                Equal(expected.Value, trade.DefaultQuantity, "Default target " + trade.Id);
                Equal(expected.Value, trade.Limit, "Weekly limit " + trade.Id);
                Equal(expected.Value, Calculator.Target(state, trade), "Initial target " + trade.Id);
                foreach (MaterialGroup group in trade.Groups)
                {
                    Require(groupExpected.ContainsKey(group.Id), "Unexpected group " + group.Id);
                    Equal(groupExpected[group.Id], group.PerTrade * trade.DefaultQuantity, "Default direct material " + group.Id);
                    foreach (MaterialLine line in group.Lines)
                    {
                        Require(lineExpected.ContainsKey(line.Id), "Unexpected line " + line.Id);
                        Equal(lineExpected[line.Id], Calculator.Quantity(line, trade.DefaultQuantity), "Default detail " + line.Id);
                        decimal altDefault;
                        bool hasAlternate = alternateExpected.TryGetValue(line.Id, out altDefault);
                        Require(hasAlternate == !String.IsNullOrEmpty(line.AlternateName), "Alternate mapping " + line.Id);
                        if (hasAlternate)
                            Equal(altDefault, Calculator.AlternateQuantity(line, trade.DefaultQuantity), "Default alternate " + line.Id);

                        // Boundary and single-trade controls, independently calculated
                        // from the source. Batch inputs correct fractional sheet values.
                        foreach (int target in new[] { 0, 1, trade.Limit })
                        {
                            decimal want = lineExpected[line.Id] * target / trade.DefaultQuantity;
                            if (line.Id == "N19") want = Math.Ceiling(target / 2m) * 10m;
                            else if (line.Id.StartsWith("N20.")) want = Math.Ceiling(target / 2m);
                            else if (line.Id.StartsWith("F32.")) want = Math.Ceiling(target * 4m / 10m);
                            Equal(want, Calculator.Quantity(line, target), "Quantity " + line.Id + " at " + target);
                            if (hasAlternate)
                                Equal(altDefault * target / trade.DefaultQuantity, Calculator.AlternateQuantity(line, target), "Alternate " + line.Id + " at " + target);
                        }
                    }
                }
            }

            foreach (TradeItem trade in catalog.Trades) Calculator.SetSelected(state, trade, true);
            Equal(60m, Calculator.SharedSilien(catalog, state), "Default shared silien");
            state.Targets["K38"] = 1;
            Equal(32m, Calculator.SharedSilien(catalog, state), "Shared silien after salmon target changes");
            state.Targets["K42"] = 0;
            Equal(2m, Calculator.SharedSilien(catalog, state), "Shared silien excludes disabled bath additive");
            state.Targets["K38"] = 0;
            Equal(0m, Calculator.SharedSilien(catalog, state), "Shared silien zero");
            state.Normalize(catalog);

            VerifyParent(state, lines, "F48", "F49", "F50");
            VerifyParent(state, lines, "F52", "F53", "F54");
            VerifyParent(state, lines, "N45", "N46", "N47");
            state.ResetChecks();
            Calculator.SetChecked(state, lines["N39"], true);
            Require(Calculator.IsChecked(state, lines["N39"]) && Calculator.IsChecked(state, lines["N41"]) && Calculator.IsChecked(state, lines["N43"]), "Shared checkbox propagates to all three silien rows.");
            Require(!Calculator.IsChecked(state, lines["F25"]) && !Calculator.IsChecked(state, lines["F54"]), "Shared checkbox must not affect unrelated silien rows.");
            Calculator.SetChecked(state, lines["N41"], false);
            Require(!Calculator.IsChecked(state, lines["N39"]) && !Calculator.IsChecked(state, lines["N43"]), "Shared checkbox can be cleared from another use.");

            foreach (MaterialGroup group in groups.Values)
            {
                state.ResetChecks();
                foreach (MaterialLine line in group.Lines.Where(l => l.ChildCheckIds == null || l.ChildCheckIds.Count == 0))
                    Calculator.SetChecked(state, line, true);
                Require(Calculator.IsGroupReady(state, group), "Ready group " + group.Id);
                MaterialLine first = group.Lines.First(l => l.ChildCheckIds == null || l.ChildCheckIds.Count == 0);
                Calculator.SetChecked(state, first, false);
                Require(!Calculator.IsGroupReady(state, group), "Incomplete group " + group.Id);
            }

            foreach (TradeItem trade in catalog.Trades)
            {
                state.ResetChecks();
                state.Targets[trade.Id] = trade.Limit;
                foreach (MaterialLine line in trade.Groups.SelectMany(g => g.Lines).Where(l => l.ChildCheckIds == null || l.ChildCheckIds.Count == 0))
                    Calculator.SetChecked(state, line, true);
                Require(Calculator.IsTradeReady(state, trade), "Ready trade " + trade.Id);
                MaterialLine first = trade.Groups.SelectMany(g => g.Lines).First(l => l.ChildCheckIds == null || l.ChildCheckIds.Count == 0);
                Calculator.SetChecked(state, first, false);
                Require(!Calculator.IsTradeReady(state, trade), "Incomplete trade " + trade.Id);
            }

            state.Targets["C6"] = 10;
            state.Targets["K52"] = 0;
            Dictionary<string, int> savedTargets = new Dictionary<string, int>(state.Targets);
            foreach (MaterialLine line in lines.Values.Where(l => l.ChildCheckIds == null || l.ChildCheckIds.Count == 0))
                Calculator.SetChecked(state, line, true);
            state.ResetChecks();
            Require(lines.Values.All(l => !Calculator.IsChecked(state, l)), "Weekly reset clears direct, derived and shared checks.");
            Require(savedTargets.Count == state.Targets.Count && savedTargets.All(p => state.Targets.ContainsKey(p.Key) && state.Targets[p.Key] == p.Value), "Weekly reset preserves targets.");

            state.Checks["H48"] = true;
            state.Checks["H52"] = true;
            state.Checks["P45"] = true;
            Require(!Calculator.IsChecked(state, lines["F48"]) && !Calculator.IsChecked(state, lines["F52"]) && !Calculator.IsChecked(state, lines["N45"]), "Saved parent values cannot bypass missing child checks.");
            state.Targets["C6"] = -1;
            state.Targets["K52"] = 999;
            state.Targets.Remove("C10");
            state.Targets["UnknownTrade"] = 1;
            state.Checks["UnknownCheck"] = true;
            Equal(0m, Calculator.Target(state, trades["C6"]), "Negative target is clamped");
            Equal(3m, Calculator.Target(state, trades["K52"]), "Excess target is clamped");
            Equal(15m, Calculator.Target(state, trades["C10"]), "Missing target uses default");
            state.Normalize(catalog);
            Equal(0m, state.Targets["C6"], "Normalized negative target");
            Equal(3m, state.Targets["K52"], "Normalized excess target");
            Require(!state.Targets.ContainsKey("UnknownTrade") && !state.Checks.ContainsKey("UnknownCheck"), "Unrecognized saved keys are discarded.");
            Equal(1m, Calculator.BatchCount(groups["L19"], 1), "Paper batch count for one egg");
            Equal(5m, Calculator.Surplus(groups["L19"], 1), "Paper surplus for one egg");
            Equal(0m, Calculator.Surplus(groups["L19"], 2), "Paper has no surplus for two eggs");
            Equal(1m, Calculator.BatchCount(groups["D32"], 1), "Bait batch count for one table");
            Equal(6m, Calculator.Surplus(groups["D32"], 1), "Bait surplus for one table");
            Equal(1m, Calculator.BatchCount(groups["L53"], 1), "Wyvern bolt batch count for one salt");
            Equal(0m, Calculator.Surplus(groups["L53"], 1), "Wyvern bolts have no surplus at whole trade targets");

            VerifySelectionAndSummary(catalog, trades, groups, lines);
            VerifyCraftingDetails(catalog, trades);
            VerifyLegacyMigration(catalog, trades, lines);
            return String.Format(CultureInfo.InvariantCulture,
                "PASS: {0} assertions; 20 trades, {1} direct materials, {2} detail rows. Source quantities, selection totals, separate component checks, purchased potions, legacy migration, production batches and weekly reset verified.",
                assertions, groups.Count, lines.Count);
        }

        private static void VerifySelectionAndSummary(Catalog catalog, Dictionary<string, TradeItem> trades, Dictionary<string, MaterialGroup> groups, Dictionary<string, MaterialLine> lines)
        {
            var state = new ProgressState();
            state.Normalize(catalog);
            state.Targets["C6"] = 10;
            Calculator.SetSelected(state, trades["C6"], true);
            var direct = Calculator.Summarize(catalog, state, true);
            var leaves = Calculator.Summarize(catalog, state, false);
            Require(direct.Count == 2 && leaves.Count == 3, "One sand trade creates only its direct materials or two threads and its purchased potion.");
            Equal(20m, Total(direct, "매듭끈"), "Selected sand direct knots");
            Equal(30m, Total(direct, "스태미나 500 포션"), "Selected sand purchased potion");
            Equal(20m, Total(leaves, "가는 실뭉치"), "Fine thread quantity is independent");
            Equal(20m, Total(leaves, "굵은 실뭉치"), "Coarse thread quantity is independent");
            Calculator.SetChecked(state, lines["F6.1"], true);
            Require(!Calculator.IsChecked(state, lines["F6.2"]) && !Calculator.IsGroupReady(state, groups["D6"]), "Checking fine thread does not prepare coarse thread.");
            Calculator.SetChecked(state, lines["F6.2"], true);
            Require(Calculator.IsGroupReady(state, groups["D6"]) && !Calculator.IsTradeReady(state, trades["C6"]), "Ready threads do not mark an unpurchased potion complete.");
            Calculator.SetSelected(state, trades["C6"], false);
            Require(Calculator.Summarize(catalog, state, false).Count == 0, "Deselecting removes the product from totals.");
            Equal(10m, Calculator.Target(state, trades["C6"]), "Deselecting preserves the requested quantity");
            Calculator.SetSelected(state, trades["C6"], true);
            Require(Calculator.IsChecked(state, lines["F6.1"]) && Calculator.IsChecked(state, lines["F6.2"]), "Selecting again preserves independently prepared materials.");

            foreach (string prefix in new[] { "F32", "N20" })
            {
                state.ResetChecks();
                var components = lines.Values.Where(l => l.Id.StartsWith(prefix + ".")).OrderBy(l => l.Id).ToList();
                Require(components.Count == (prefix == "F32" ? 3 : 4), "All separately prepared components exist for " + prefix);
                Calculator.SetChecked(state, components[0], true);
                Require(components.Skip(1).All(l => !Calculator.IsChecked(state, l)), "Component checkbox isolation for " + prefix);
            }

            state = new ProgressState(); state.Normalize(catalog);
            foreach (string id in new[] { "C6", "K12", "K15", "K34" }) Calculator.SetSelected(state, trades[id], true);
            leaves = Calculator.Summarize(catalog, state, false);
            decimal purchasedCount = 0;
            foreach (string id in new[] { "D7", "L13", "L16", "L35" })
            {
                MaterialGroup group = groups[id];
                Require(group.IsPurchased && group.Lines.Count == 1 && group.Lines[0].Name == group.Name, "Potion is one purchase requirement: " + id);
                decimal expected = id == "D7" ? 75m : id == "L13" ? 30m : id == "L16" ? 16m : 25m;
                Equal(expected, Total(leaves, group.Name), "Purchased potion total " + id);
                purchasedCount += Total(leaves, group.Name);
            }
            Equal(146m, purchasedCount, "Four purchased potion quantities");
            foreach (string id in new[] { "F7", "F8", "F9", "N13", "N14", "N16", "N17", "N18", "N35", "N36", "N37" })
                Require(!lines.ContainsKey(id), "Removed potion crafting ingredient " + id);
            Require(!leaves.Any(l => l.Name.Contains("300") || l.Name == "네잎클로버" || l.Name == "물이 든 병" || l.Name == "골드 허브" || l.Name == "베이스 포션" || l.Name.Contains("주석") || l.Name.Contains("아연") || l.Name.Contains("니켈")), "Purchased potions do not reintroduce crafting materials in the combined list.");

            state = new ProgressState(); state.Normalize(catalog);
            foreach (string id in new[] { "C20", "C44", "K38" }) Calculator.SetSelected(state, trades[id], true);
            leaves = Calculator.Summarize(catalog, state, false);
            Equal(63m, Total(leaves, "실리엔"), "Silien adds three stations and both salmon uses");
            Equal(48m, Total(leaves, "힐웬"), "Hillwen adds three stations");
            Require(!leaves.Any(l => l.Name == "매듭끈" || l.Name == "에너지 컨버터"), "Leaf totals exclude already expanded parents.");
            Equal(3m, Total(leaves, "가는 실뭉치"), "Leaf totals retain nested fine thread");
            Require(!leaves.Any(l => l.Name == "거미줄" || l.Name == "양털"), "Alternative raw materials are not additional required items.");
            Require(catalog.Trades.Count(t => Calculator.IsSelected(state, t)) == 3, "Only the three requested goods are selected.");
            Calculator.SetSelected(state, trades["K38"], false);
            Equal(33m, Total(Calculator.Summarize(catalog, state, false), "실리엔"), "Deselecting removes both shared-check uses without losing the others");

            state = new ProgressState(); state.Normalize(catalog);
            Calculator.SetSelected(state, trades["K10"], true); Calculator.SetSelected(state, trades["K52"], true);
            direct = Calculator.Summarize(catalog, state, true); leaves = Calculator.Summarize(catalog, state, false);
            Equal(30m, Total(direct, "미스릴판"), "Direct mithril plates remain a distinct item");
            Equal(9m, Total(direct, "미스릴 대못"), "Direct mithril nails remain a distinct item");
            Equal(210m, Total(leaves, "미스릴괴"), "Leaf mithril ingots aggregate across stations");
            Require(!direct.Any(l => l.Name == "미스릴괴") && !leaves.Any(l => l.Name == "미스릴판"), "Direct and expanded views do not double count recipe stages.");

            state = new ProgressState(); state.Normalize(catalog);
            Calculator.SetSelected(state, trades["K38"], true); Calculator.SetChecked(state, lines["N39"], true);
            Calculator.SetSelected(state, trades["K42"], true);
            Require(!Calculator.IsChecked(state, lines["N39"]), "Adding another shared silien requirement reopens its preparation check.");
        }

        private static void VerifyCraftingDetails(Catalog catalog, Dictionary<string, TradeItem> trades)
        {
            var state = new ProgressState(); state.Normalize(catalog);
            foreach (string id in new[] { "C31", "C20", "K38" }) Calculator.SetSelected(state, trades[id], true);
            var rows = Calculator.Summarize(catalog, state, false);
            Equal(95m, Total(rows, "실리엔"), "Silien combines 50 from table, 15 from fossil and 30 from salmon exactly once");
            var silien = rows.Single(r => r.Name == "실리엔");
            Equal(475m, silien.AlternateQuantity, "95 finished Silien replaces 475 crystals");
            Require(silien.AlternateName == "실리엔 결정" && silien.Uses.Count == 3 && !rows.Any(r => r.Name == "실리엔 결정"), "Crystals are one conversion, never an extra purchase row; all uses preserved");
            Calculator.SetSelected(state, trades["C31"], false);
            rows = Calculator.Summarize(catalog, state, false);
            Equal(45m, Total(rows, "실리엔"), "Removing table removes only its 50 Silien");
            var board = rows.Single(r => r.Name == "나무판");
            Require(board.PurchaseRecommended && board.Quantity == 3 && board.CraftingNotes.Count == 0, "Three boards remain purchased without manufacturing notes");
            Require(!rows.Any(r => r.Name == "대못" || r.Name == "철봉" || r.Name == "나무장작"), "Purchased boards never add manufacturing ingredients");
            Require(Calculator.CraftingSummary(board) == "경매장 구매 전용", "Clipboard describes boards as purchase-only without a recipe");

            state = new ProgressState(); state.Normalize(catalog);
            foreach (string id in new[] { "K21", "K52", "C15", "K6", "K10", "K12" }) Calculator.SetSelected(state, trades[id], true);
            rows = Calculator.Summarize(catalog, state, false);
            var wood = rows.Single(r => r.Name == "최고급 나무장작");
            Equal(9m, wood.Quantity, "Nine finest firewood are required");
            Equal(243m, wood.AlternateQuantity, "Nine finest firewood replace 243 ordinary firewood");
            Require(wood.CraftingNotes.Single().Contains("고급 27개") && wood.CraftingNotes.Single().Contains("중급 81개"), "Finest wood shows both intermediate starting stages");
            foreach (var pair in new[] { new { Name = "은괴", Raw = "은광석", Amount = 80 }, new { Name = "동괴", Raw = "동광석", Amount = 250 }, new { Name = "금괴", Raw = "금광석", Amount = 250 }, new { Name = "미스릴괴", Raw = "미스릴 광석", Amount = 1050 } })
            {
                string note = rows.Single(r => r.Name == pair.Name).CraftingNotes.Single();
                string amount = Calculator.FormatQuantity(pair.Amount);
                Require(note.Contains(pair.Raw + " " + amount + "개") && note.Contains(pair.Raw + " 조각 " + amount + "개") && note.Contains("또는"), "Ingot ore and fragment recipes are alternatives: " + pair.Name);
            }
            Require(!rows.Any(r => r.Name.Contains("광석")), "Ingot alternative materials do not double-count purchase totals");
            foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, true);
            rows = Calculator.Summarize(catalog, state, false);
            Equal(143m, Total(rows, "실리엔"), "All-selected Silien total including table");
            Equal(715m, rows.Single(r => r.Name == "실리엔").AlternateQuantity, "All-selected crystals count each recipe once");
            Require(Calculator.Summarize(catalog, state, true).All(r => r.AlternateName == null && r.CraftingNotes.Count == 0), "Exchange-material totals retain their finished-product view");
            foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, false);
            Require(Calculator.Summarize(catalog, state, false).Count == 0, "No selected products means no derived crafting needs");
        }

        private static void VerifyLegacyMigration(Catalog catalog, Dictionary<string, TradeItem> trades, Dictionary<string, MaterialLine> lines)
        {
            var legacy = new ProgressState();
            legacy.SchemaVersion = 0;
            legacy.Selected = null;
            foreach (TradeItem trade in catalog.Trades) legacy.Targets[trade.Id] = trade.DefaultQuantity;
            legacy.Targets["C6"] = 10; legacy.Targets["K52"] = 0;
            foreach (string key in new[] { "H6", "H32", "P20", "H7", "H8", "H9", "P13", "P14", "P16", "P17", "P18", "P35", "P36", "P37" }) legacy.Checks[key] = true;
            legacy.Normalize(catalog);
            Require(Calculator.IsSelected(legacy, trades["C6"]) && !Calculator.IsSelected(legacy, trades["K52"]), "Legacy positive targets become selected and zero targets remain excluded.");
            Equal(10m, Calculator.Target(legacy, trades["C6"]), "Legacy target is preserved");
            Require(new[] { "F6.1", "F6.2", "F32.1", "F32.2", "F32.3", "N20.1", "N20.2", "N20.3", "N20.4" }.All(id => Calculator.IsChecked(legacy, lines[id])), "Legacy composite checks migrate to each component.");
            Require(new[] { "D7.purchase", "L13.purchase", "L16.purchase", "L35.purchase" }.All(id => !Calculator.IsChecked(legacy, lines[id])), "Old potion crafting checks never imply purchased potions.");
            Calculator.SetChecked(legacy, lines["F6.1"], false);
            legacy.Normalize(catalog);
            Require(!Calculator.IsChecked(legacy, lines["F6.1"]) && Calculator.IsChecked(legacy, lines["F6.2"]), "Normalizing again does not overwrite independent checks with legacy values.");
        }

        private static decimal Total(List<MaterialTotal> summary, string name)
        {
            var matches = summary.Where(t => t.Name == name).ToList();
            Require(matches.Count == 1, "Expected one aggregate row for " + name);
            return matches[0].Quantity;
        }

        private static void VerifyParent(ProgressState state, Dictionary<string, MaterialLine> lines, string parentId, string firstId, string secondId)
        {
            state.ResetChecks();
            MaterialLine parent = lines[parentId];
            MaterialLine first = lines[firstId];
            MaterialLine second = lines[secondId];
            Require(!Calculator.IsChecked(state, parent), "Parent starts incomplete: " + parentId);
            Calculator.SetChecked(state, first, true);
            Require(!Calculator.IsChecked(state, parent), "One child cannot complete parent: " + parentId);
            Calculator.SetChecked(state, second, true);
            Require(Calculator.IsChecked(state, parent), "Both children complete parent: " + parentId);
            Calculator.SetChecked(state, first, false);
            Require(!Calculator.IsChecked(state, parent), "Clearing a child clears parent: " + parentId);
        }

        private static Dictionary<string, decimal> Parse(string controls)
        {
            return controls.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Split(':'))
                .ToDictionary(parts => parts[0], parts => Decimal.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        private static void Equal(decimal expected, decimal actual, string context)
        {
            Require(expected == actual, context + ": expected " + expected.ToString(CultureInfo.InvariantCulture) + ", actual " + actual.ToString(CultureInfo.InvariantCulture));
        }

        private static void Require(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Verification failed: " + message);
        }
    }
}
