using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public static class ProcurementCostingVerification
    {
        static int assertions;
        static void Require(bool value, string message) { assertions++; if (!value) throw new Exception("ProcurementCosting: " + message); }
        static decimal Quantity(ProcurementCostSummary summary, string name) { return summary.Lines.Where(line => line.Name == name).Sum(line => line.Quantity); }
        static ProgressState Selected(Catalog catalog, params string[] ids)
        {
            var state = new ProgressState(); state.Normalize(catalog);
            foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, ids.Contains(trade.Id));
            return state;
        }
        static void Craft(ProcurementPlanner planner, ProgressState state, string name) { planner.SetChoice(state, name, planner.GetRecipes(name).First().Id); }
        static void Check(ProcurementPlanner planner, ProgressState state, string name, string kind, decimal? quantity = null)
        {
            var step = ProcurementReadiness.GetSteps(planner.Build(state), state).Single(s => s.Name == name && s.Kind == kind);
            ProcurementReadiness.SetReady(state, step, true);
            if (quantity.HasValue) state.ProcurementReady[step.Key].Quantity = quantity.Value;
        }
        static MaterialGroup Group(string name, decimal perTrade, string input, decimal inputPerTrade, decimal output = 1)
        {
            var group = new MaterialGroup { Name = name, PerTrade = perTrade, OutputPerBatch = output, IsPurchased = input == null };
            if (input != null) group.Lines.Add(new MaterialLine { Name = input, PerTrade = inputPerTrade });
            return group;
        }
        static Catalog Fixture(params MaterialGroup[] groups)
        {
            var catalog = new Catalog();
            for (int i = 0; i < groups.Length; i++)
            {
                string id = "cost-" + i;
                groups[i].Id = id + "-group";
                int index = 0;
                foreach (var line in groups[i].Lines)
                {
                    line.Id = id + "-line-" + index++; line.CheckId = line.Id;
                }
                catalog.Trades.Add(new TradeItem { Id = id, Name = id, Station = "교역소 " + i, Limit = 100, DefaultQuantity = 1, Groups = new List<MaterialGroup> { groups[i] } });
            }
            return catalog;
        }
        static void Balanced(ProcurementCostReport report)
        {
            Require(report.Stations.Values.Sum(s => s.AllBuy.KnownCost) == report.AllBuy.KnownCost, "all-buy station amounts sum exactly");
            Require(report.Stations.Values.Sum(s => s.Planned.KnownCost) == report.Planned.KnownCost, "planned station amounts sum exactly");
            Require(report.Stations.Values.Sum(s => s.Remaining.KnownCost) == report.Remaining.KnownCost, "remaining station amounts sum exactly");
            foreach (var pair in new[] { new { Total = report.AllBuy, Sections = report.Stations.Values.Select(s => s.AllBuy) },
                new { Total = report.Planned, Sections = report.Stations.Values.Select(s => s.Planned) },
                new { Total = report.Remaining, Sections = report.Stations.Values.Select(s => s.Remaining) } })
                foreach (var line in pair.Total.Lines)
                    Require(pair.Sections.SelectMany(s => s.Lines).Where(l => l.Name == line.Name && l.Kind == line.Kind && l.IsSeed == line.IsSeed).Sum(l => l.Quantity) == line.Quantity,
                        "station allocated quantity sums exactly: " + line.Name);
        }

        public static string Run(Catalog realCatalog)
        {
            assertions = 0;
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var planner = new ProcurementPlanner(realCatalog);
            var costing = new ProcurementCosting(realCatalog, planner);
            var state = Selected(realCatalog);
            int priceReads = 0;
            var empty = costing.Build(state, name => { priceReads++; return 2m; });
            Require(empty.Planned.Lines.Count == 0 && empty.Remaining.KnownCost == 0 && priceReads == 0, "empty selection does not read any prices");
            Require(empty.Stations.Count == realCatalog.Trades.Select(t => t.Station).Distinct().Count(), "empty station cards still have zero summaries");

            state = Selected(realCatalog, realCatalog.Trades.Select(t => t.Id).ToArray());
            foreach (string name in new[] { "매듭끈", "가는 실뭉치", "굵은 실뭉치", "정화된 토끼의 발", "실리엔", "펫 놀이세트", "에너지 증폭 장치", "에너지 컨버터", "금괴" })
                if (planner.GetRecipes(name).Count > 0) Craft(planner, state, name);
            planner.SetChoice(state, "거미줄", "acquire");
            var plan = planner.Build(state);
            var before = serializer.Serialize(state); var beforePlan = serializer.Serialize(plan);
            var reads = new Dictionary<string, int>();
            Func<string, decimal?> quoted = name => { int count; reads.TryGetValue(name, out count); reads[name] = count + 1; return 2m; };
            var report = costing.Build(state, quoted, plan);
            Require(report.Planned.KnownCost == plan.Purchases.Sum(p => p.Quantity * 2), "real planned price equals globally aggregated purchase list");
            Require(report.Remaining.KnownCost == report.Planned.KnownCost, "no snapshots leaves whole planned cost remaining");
            foreach (var line in plan.Purchases) Require(Quantity(report.Planned, line.Name) == line.Quantity, "real planned purchase quantity: " + line.Name);
            foreach (var line in plan.Acquisitions)
                Require(report.Planned.Lines.Any(l => l.Name == line.Name && l.Kind == "acquire" && l.Quantity == line.Quantity && l.UnitPrice == 0), "direct acquisition has known zero cost: " + line.Name);
            Require(reads.Values.All(count => count == 1), "price delegate read once per distinct item across all sections");
            Require(serializer.Serialize(state) == before && serializer.Serialize(plan) == beforePlan, "costing does not mutate progress or supplied plan");
            Balanced(report);
            foreach (var step in ProcurementReadiness.GetSteps(plan, state).Where(s => s.HasExchangeUse)) ProcurementReadiness.SetReady(state, step, true);
            report = costing.Build(state, quoted);
            Require(report.Remaining.KnownCost == 0 && report.Remaining.Lines.Count == 0 && report.Remaining.UnknownCount == 0, "all exchange items checked leaves no additional acquisition");
            Require(report.Planned.KnownCost > 0, "completed plan preserves original full budget");
            Require(ProcurementReadiness.GetSteps(planner.Build(state), state).Any(s => !s.IsReady && s.IsCovered), "derived covered inputs need no stored inventory");
            var restored = serializer.Deserialize<ProgressState>(serializer.Serialize(state));
            Require(costing.Build(restored, quoted).Remaining.KnownCost == 0, "saved explicit final completion reloads zero remaining");

            SimpleInventoryChecks(); BatchChecks(); SynthesisChecks(); UnknownAndNpcChecks();
            return "PASS procurement costing: " + assertions + " assertions; whole-plan and remaining costs, exact station allocation, shared demand, consumed stock, batch boundaries, seeds and NPC exclusion.";
        }

        static void SimpleInventoryChecks()
        {
            var catalog = Fixture(Group("완성 A", 1, "공유 원료", 50), Group("완성 B", 1, "공유 원료", 50));
            var planner = new ProcurementPlanner(catalog); var costing = new ProcurementCosting(catalog, planner);
            var state = Selected(catalog, "cost-0", "cost-1"); Craft(planner, state, "완성 A"); Craft(planner, state, "완성 B");
            Func<string, decimal?> price = name => name == "공유 원료" ? 3m : 500m;
            Check(planner, state, "공유 원료", "purchase", 50); Check(planner, state, "완성 A", "craft");
            var report = costing.Build(state, price);
            Require(report.Planned.KnownCost == 300 && report.AllBuy.KnownCost == 1000, "complete purchase comparison differs from selected crafting cost");
            Require(Quantity(report.Remaining, "공유 원료") == 50 && report.Remaining.KnownCost == 150, "raw50 already consumed in completed A cannot prepare B again");
            Require(report.Stations["교역소 0"].Remaining.KnownCost == 0 && report.Stations["교역소 1"].Remaining.KnownCost == 150, "remaining cost follows unfinished station only");
            Check(planner, state, "공유 원료", "purchase", 100);
            Require(costing.Build(state, price).Remaining.KnownCost == 0, "raw100 less completed A50 leaves B50 already prepared");
            state.ProcurementReady.Remove("purchase:공유 원료");
            Require(costing.Build(state, price).Remaining.KnownCost == 150, "unchecking raw immediately restores unfinished B cost");
            state.ProcurementReady.Remove("craft:완성 A");
            Require(costing.Build(state, price).Remaining.KnownCost == 300, "unchecking completed A restores both demands");
            Check(planner, state, "공유 원료", "purchase", 50);
            report = costing.Build(state, price);
            Require(report.Remaining.KnownCost == 150 && report.Stations.Values.All(s => s.Remaining.KnownCost == 75), "partial raw inventory allocated proportionally across shared uses");
            state.ProcurementReady["purchase:공유 원료"].Context = "old-mode";
            Require(costing.Build(state, price).Remaining.KnownCost == 300, "mismatched snapshot context cannot reduce cost");
            state.ProcurementReady["purchase:공유 원료"].Context = "purchase";
            state.ProcurementReady["purchase:공유 원료"].Name = "wrong-name";
            Require(costing.Build(state, price).Remaining.KnownCost == 300, "mismatched snapshot name cannot reduce cost");
            state.ProcurementReady.Clear();
            Check(planner, state, "완성 A", "craft"); state.Targets["cost-0"] = 2;
            Require(Quantity(costing.Build(state, price).Remaining, "공유 원료") == 100, "increased target retains earlier finished quantity as partial inventory");
            planner.SetChoice(state, "완성 A", "buy");
            Require(costing.Build(state, price).Remaining.Lines.Any(l => l.Name == "완성 A" && l.Quantity == 2), "old craft snapshot cannot reduce purchase-mode demand");

            // Shared intermediate is both an exchange item and a parent ingredient.
            catalog = Fixture(Group("상위품", 1, "실리엔", 4), Group("실리엔", 6, null, 0));
            planner = new ProcurementPlanner(catalog); costing = new ProcurementCosting(catalog, planner);
            state = Selected(catalog, "cost-0", "cost-1"); Craft(planner, state, "상위품"); Craft(planner, state, "실리엔");
            Check(planner, state, "상위품", "craft"); Check(planner, state, "실리엔", "craft", 4);
            Require(Quantity(costing.Build(state, name => 1m).Remaining, "실리엔 결정") == 30, "four Silien used by upper completion cannot satisfy six direct exchange units");
            Check(planner, state, "실리엔", "craft", 10);
            Require(costing.Build(state, name => 1m).Remaining.KnownCost == 0, "ten prepared Silien covers upper four and direct six once");
        }

        static void BatchChecks()
        {
            var catalog = Fixture(Group("묶음 A", 1, "묶음 중간품", 3), Group("묶음 B", 1, "묶음 중간품", 7), Group("묶음 중간품", 10, "묶음 원료", 1, 10));
            var planner = new ProcurementPlanner(catalog); var costing = new ProcurementCosting(catalog, planner);
            var state = Selected(catalog, "cost-0", "cost-1");
            Craft(planner, state, "묶음 A"); Craft(planner, state, "묶음 B"); Craft(planner, state, "묶음 중간품");
            var report = costing.Build(state, name => 10m);
            Require(Quantity(report.Planned, "묶음 원료") == 1, "three plus seven shared units require one ten-unit batch");
            Require(report.Stations["교역소 0"].Planned.KnownCost == 3 && report.Stations["교역소 1"].Planned.KnownCost == 7, "batch cost allocated three to seven without separate station rounding");
            Balanced(report);
            state.Targets["cost-0"] = 2;
            report = costing.Build(state, name => 7m);
            Require(Quantity(report.Planned, "묶음 원료") == 2, "six plus seven shared units round once to twenty output");
            Balanced(report);
            state.Targets["cost-0"] = 1;
            Check(planner, state, "묶음 A", "craft"); Check(planner, state, "묶음 원료", "purchase");
            report = costing.Build(state, name => 10m);
            Require(Quantity(report.Remaining, "묶음 원료") == 1, "completed three consumed one batch; unrecorded seven-output surplus is not assumed held");
            Check(planner, state, "묶음 중간품", "craft");
            Require(costing.Build(state, name => 10m).Remaining.KnownCost == 0, "explicit ten prepared intermediates include consumed three and remaining seven");
            // A snapshot of a lower crafted stage includes upper consumed units;
            // forwarding both records by addition would consume the same raw twice.
            state.ProcurementReady["craft:묶음 중간품"].Quantity = 3;
            state.ProcurementReady["purchase:묶음 원료"].Quantity = 2;
            Require(costing.Build(state, name => 10m).Remaining.KnownCost == 0, "overlapping upper and middle completion consumes one prior batch rather than two");
        }

        static void SynthesisChecks()
        {
            var catalog = Fixture(Group("괴 A", 1, "금괴", 3), Group("괴 B", 1, "금괴", 7));
            var planner = new ProcurementPlanner(catalog); var costing = new ProcurementCosting(catalog, planner);
            var state = Selected(catalog, "cost-0", "cost-1"); Craft(planner, state, "괴 A"); Craft(planner, state, "괴 B"); planner.SetChoice(state, "금괴", "synthesis");
            Func<string, decimal?> price = name => name == "금괴" ? 5m : name == "아라트의 결정" ? 7m : name == "축복의 포션" ? 11m : 100m;
            var report = costing.Build(state, price);
            Require(report.Planned.KnownCost == 23 && Quantity(report.Planned, "금괴") == 1 && Quantity(report.Planned, "아라트의 결정") == 1, "shared synthesis buys one seed and one catalyst set for ten ingots");
            Require(report.Planned.Lines.Single(l => l.Name == "금괴").IsSeed, "seed is a distinct purchase cost line");
            Balanced(report);
            Check(planner, state, "금괴", "purchase");
            Require(costing.Build(state, price).Remaining.KnownCost == 18, "checked starting seed only removes seed price");
            state.ProcurementReady.Clear(); Check(planner, state, "금괴", "craft", 1);
            report = costing.Build(state, price);
            Require(report.Remaining.KnownCost == 18 && Quantity(report.Remaining, "금괴") == 0, "prepared ingot can be borrowed to grow nine more without new seed");
            Check(planner, state, "금괴", "craft"); Check(planner, state, "아라트의 결정", "purchase"); Check(planner, state, "축복의 포션", "purchase");
            state.Targets["cost-1"] = 2;
            report = costing.Build(state, price);
            Require(report.Remaining.KnownCost == 18 && Quantity(report.Remaining, "아라트의 결정") == 1, "old ten completed ingots need one new synthesis for seventeen; old catalysts already consumed");
            Require(Quantity(report.Remaining, "금괴") == 0, "remaining completed ingots are reusable seed after target increase");
            state.Targets["cost-1"] = 1; state.ProcurementReady.Clear();
            Check(planner, state, "괴 A", "craft"); Check(planner, state, "금괴", "purchase");
            Check(planner, state, "아라트의 결정", "purchase"); Check(planner, state, "축복의 포션", "purchase");
            report = costing.Build(state, price);
            Require(report.Remaining.KnownCost == 23, "seed and catalysts spent in checked upper item are not reused as still-held stock");
            Require(report.Stations["교역소 0"].Remaining.KnownCost == 0 && report.Stations["교역소 1"].Remaining.KnownCost == 23, "unfinished station carries its new synthesis cost only");
            Balanced(report);
            Check(planner, state, "괴 B", "craft");
            Require(costing.Build(state, price).Remaining.Lines.Count == 0, "all upper products complete removes seed and catalyst demand entirely");
        }

        static void UnknownAndNpcChecks()
        {
            var catalog = Fixture(Group("미조회 품목", 1, null, 0), Group("미조회 품목", 2, null, 0), Group("무료 확보품", 1, null, 0), Group("가격 0 품목", 1, null, 0));
            var planner = new ProcurementPlanner(catalog); var costing = new ProcurementCosting(catalog, planner);
            var state = Selected(catalog, catalog.Trades.Select(t => t.Id).ToArray()); planner.SetChoice(state, "무료 확보품", "acquire");
            var report = costing.Build(state, name => name == "가격 0 품목" ? (decimal?)0 : null);
            Require(report.Planned.UnknownCount == 1 && report.Planned.UnknownNames.Single() == "미조회 품목", "missing shared item counted once while acquire and known zero prices are complete");
            Require(!report.Planned.IsComplete && report.Planned.KnownCost == 0, "unknown price is not represented as fully priced zero");
            Require(report.AllBuy.UnknownCount == 2, "complete-buy comparison still quotes ordinary acquired roots");
            Balanced(report);

            catalog = Fixture(Group("NPC 조합품", 1, "설탕", 2), Group("새우", 3, null, 0), Group("마늘", 1, null, 0));
            planner = new ProcurementPlanner(catalog); costing = new ProcurementCosting(catalog, planner);
            state = Selected(catalog, catalog.Trades.Select(t => t.Id).ToArray()); Craft(planner, state, "NPC 조합품");
            // This remains useful before forced-NPC policy is linked into the core.
            foreach (string name in new[] { "설탕", "새우", "마늘" })
                if (planner.GetChoice(state, name) != "acquire") planner.SetChoice(state, name, "acquire");
            report = costing.Build(state, name => name == "NPC 조합품" ? (decimal?)500 : 20m);
            Require(report.Planned.KnownCost == 0 && report.Planned.UnknownCount == 0 && report.Planned.Lines.All(l => l.Kind == "acquire" && l.UnitPrice == 0), "NPC acquisition contributes known zero under current policy");
            Require(report.Remaining.KnownCost == 0, "NPC acquisition requires no auction spending before ready checks");
            Balanced(report);
        }
    }
}
