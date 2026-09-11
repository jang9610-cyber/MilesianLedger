using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public static class ProcurementVerification
    {
        static int assertions;
        static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception("Procurement: " + message); }
        static decimal Quantity(IEnumerable<MaterialTotal> totals, string name) { var item = totals.FirstOrDefault(t => t.Name == name); return item == null ? 0 : item.Quantity; }
        static ProgressState Selected(Catalog catalog, params string[] ids)
        {
            foreach (var line in catalog.Trades.SelectMany(t => t.Groups).SelectMany(g => g.Lines))
                if (String.IsNullOrEmpty(line.CheckId)) line.CheckId = "check-" + line.Id;
            var state = new ProgressState(); state.Normalize(catalog);
            foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, ids.Contains(trade.Id));
            return state;
        }
        static void Craft(ProcurementPlanner planner, ProgressState state, string name) { planner.SetChoice(state, name, planner.GetRecipes(name).First().Id); }

        public static string Run(Catalog catalog)
        {
            assertions = 0;
            var planner = new ProcurementPlanner(catalog);
            var empty = Selected(catalog);
            Require(planner.Build(empty).Nodes.Count == 0, "empty selection has no demand");
            var all = Selected(catalog, catalog.Trades.Select(t => t.Id).ToArray());
            var buying = planner.Build(all);
            var direct = Calculator.Summarize(catalog, all, true);
            Require(buying.Roots.Count == direct.Count && buying.Purchases.Count == direct.Count, "all default purchases match direct exchange roots");
            Require(buying.Crafts.Count == 0 && buying.Acquisitions.Count == 0, "default is buying");
            foreach (var item in direct) Require(Quantity(buying.Purchases, item.Name) == item.Quantity, "direct purchase quantity " + item.Name);
            Require(!buying.Nodes.Any(n => n.Name == "실리엔 결정"), "bought Silien does not add crystals");
            Require(!buying.Nodes.Any(n => n.Name == "나무판"), "bought pet play set stops its recipe");

            var state = Selected(catalog, "C44");
            Craft(planner, state, "튼튼한 고리");
            var plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "매듭끈") == 3, "buy intermediate knot for sturdy ring");
            Require(Quantity(plan.Purchases, "가는 실뭉치") == 0 && Quantity(plan.Purchases, "굵은 실뭉치") == 0, "nested children not flattened");
            Craft(planner, state, "매듭끈");
            plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "매듭끈") == 0 && Quantity(plan.Purchases, "가는 실뭉치") == 3 && Quantity(plan.Purchases, "굵은 실뭉치") == 3, "craft selected intermediate exactly once");
            Calculator.SetSelected(state, catalog.Trades.Single(t => t.Id == "C6"), true);
            plan = planner.Build(state);
            var knot = plan.Nodes.Single(n => n.Name == "매듭끈");
            Require(knot.Quantity == 53 && knot.DirectQuantity == 50, "shared direct and nested demands aggregate");
            Require(Quantity(plan.Purchases, "가는 실뭉치") == 53, "shared recipe expands once");
            Craft(planner, state, "가는 실뭉치");
            planner.SetChoice(state, "거미줄", "acquire");
            plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "거미줄") == 0 && Quantity(plan.Acquisitions, "거미줄") == 265, "direct acquisition excluded from purchase totals");
            Require(plan.QuoteNames.Contains("거미줄"), "acquisition still supports optional cached price comparison");
            planner.SetChoice(state, "매듭끈", "buy");
            plan = planner.Build(state);
            Require(!plan.Nodes.Any(n => n.Name == "가는 실뭉치" || n.Name == "거미줄"), "buying parent removes inactive descendants");
            Require(planner.GetChoice(state, "가는 실뭉치") == "default", "inactive child preference preserved");

            state = Selected(catalog, "C44", "K38");
            Craft(planner, state, "에너지 증폭 장치"); Craft(planner, state, "에너지 컨버터");
            plan = planner.Build(state);
            Require(plan.Nodes.Single(n => n.Name == "에너지 컨버터").Quantity == 33, "converter combines direct 15 and nested 18");
            Require(Quantity(plan.Purchases, "실리엔") == 33 && Quantity(plan.Purchases, "힐웬") == 33, "converter original descendants not counted twice");

            state = Selected(catalog, "C20"); Craft(planner, state, "펫 놀이세트");
            plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "나무판") == 3, "crafting pet play set still purchases three boards");
            Require(planner.IsPurchaseOnly("나무판") && !planner.CanAcquire("나무판") && planner.GetRecipes("나무판").Count == 0, "board is explicitly purchase only with no crafting recipe");
            Require(plan.Crafts.Any(n => n.Name == "펫 놀이세트") && plan.Nodes.Single(n => n.Name == "나무판").Children.Count == 0, "board leaf never removes its parent's craft choice");
            foreach (string oldChoice in new[] { "default", "acquire" })
            {
                state.ProcurementChoices["나무판"] = oldChoice; plan = planner.Build(state);
                Require(planner.GetChoice(state, "나무판") == "buy" && Quantity(plan.Purchases, "나무판") == 3, "legacy board choice is forced to purchase " + oldChoice);
                var preview = planner.Preview(state, "나무판", 3, oldChoice);
                Require(!preview.IsCrafting && preview.Recipe == null && preview.Children.Count == 0, "legacy board choice cannot expose a crafting preview " + oldChoice);
                bool rejected = false; try { planner.SetChoice(state, "나무판", oldChoice); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "direct nonpurchase choice is rejected for board " + oldChoice);
            }
            Require(!plan.QuoteNames.Any(n => new[] { "나무장작", "대못", "철봉", "질긴 끈" }.Contains(n)), "board manufacturing materials do not enter preparation or price scope");
            planner.SetChoice(state, "나무판", "buy");
            Require(!state.ProcurementChoices.ContainsKey("나무판"), "buy choice can remove obsolete board preference");
            Require(planner.GetRecipes("철봉").Count > 0 && planner.GetRecipes("질긴 끈").Count > 0 && !planner.IsPurchaseOnly("최고급 나무장작"), "other material recipes remain available");

            state = Selected(catalog, "K52"); Craft(planner, state, "최고급 나무장작"); Craft(planner, state, "고급 나무장작"); Craft(planner, state, "중급 나무장작");
            plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "나무장작") == 243, "three wood tiers yield 27 ordinary per highest wood");
            state = Selected(catalog, "C31", "C20", "K38");
            Craft(planner, state, "마력이 깃든 나무장작"); Craft(planner, state, "정화된 토끼의 발"); Craft(planner, state, "에너지 컨버터"); Craft(planner, state, "실리엔");
            plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "실리엔") == 0 && Quantity(plan.Purchases, "실리엔 결정") == 475, "shared Silien finished demand 95 becomes 475 crystals");
            state = Selected(catalog, "K21", "K42"); Craft(planner, state, "뮤턴트"); Craft(planner, state, "끈끈이 풀"); plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "돌연변이 식물의 점액질") == 45 && !plan.QuoteNames.Contains("돌연변이 식물의 점액"), "workbook material aliases merge before purchases and quotes");

            state = Selected(catalog, "C15"); Craft(planner, state, "은판"); planner.SetChoice(state, "은괴", "synthesis");
            plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "은괴") == 1 && Quantity(plan.Purchases, "아라트의 결정") == 2 && Quantity(plan.Purchases, "축복의 포션") == 2, "16 ingots use one seed and two synthesis successes");
            var silver = plan.Crafts.Single(n => n.Name == "은괴");
            Require(silver.Batches == 2 && silver.ProducedQuantity == 19 && silver.Surplus == 3, "reproduction uses net nine gain");
            Require(silver.Children.Single(c => c.IsSeed).Node.Children.Count == 0, "seed is terminal and cannot recurse");
            foreach (var sample in new[] { new { Need = 1m, Batches = 0m, Produced = 1m }, new { Need = 10m, Batches = 1m, Produced = 10m }, new { Need = 19m, Batches = 2m, Produced = 19m }, new { Need = 20m, Batches = 3m, Produced = 28m } })
            {
                var preview = planner.Preview(state, "은괴", sample.Need, "synthesis");
                Require(preview.Batches == sample.Batches && preview.ProducedQuantity == sample.Produced, "synthesis boundary " + sample.Need);
                Require(preview.Children.Single(c => c.IsSeed).Quantity == 1, "single synthesis seed " + sample.Need);
                Require(preview.Children.Where(c => !c.IsSeed).All(c => c.Quantity == sample.Batches), "synthesis inputs " + sample.Need);
            }
            planner.SetChoice(state, "은괴", "ore"); plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "은광석") == 80 && Quantity(plan.Purchases, "은광석 조각") == 0 && Quantity(plan.Purchases, "은괴") == 0, "ore recipe excludes fragments and seed");
            planner.SetChoice(state, "은괴", "fragments"); plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "은광석 조각") == 80 && Quantity(plan.Purchases, "은광석") == 0, "fragments replace ores");
            foreach (string potion in new[] { "생명력 500 포션", "마나 500 포션", "스태미나 500 포션", "마리오네트 500 포션" })
            {
                Require(planner.IsPurchaseOnly(potion) && planner.GetRecipes(potion).Count == 0 && !planner.CanAcquire(potion), "500 potions remain purchase only " + potion);
                state.ProcurementChoices[potion] = "default"; Require(planner.GetChoice(state, potion) == "buy", "invalid potion craft cannot activate");
            }

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(state);
            var restored = serializer.Deserialize<ProgressState>(json); restored.Normalize(catalog);
            Require(planner.GetChoice(restored, "은괴") == "fragments", "selection persists round trip");
            var legacy = serializer.Deserialize<ProgressState>("{\"SchemaVersion\":2,\"Targets\":{\"C6\":12},\"Selected\":{\"C6\":true},\"Checks\":{\"H6\":true}}"); legacy.Normalize(catalog);
            Require(legacy.Targets["C6"] == 12 && legacy.Selected["C6"] && legacy.ProcurementChoices.Count == 0, "legacy progress retained and new choices default buy");

            VerifySharedBatch();
            VerifyWorkbookBranches(catalog);
            VerifyLegacyBoardRecipe();
            // Exercise every available active branch in the real graph, with seeds
            // explicitly terminal. This catches accidental cycles and zero-demand inputs.
            state = Selected(catalog, catalog.Trades.Select(t => t.Id).ToArray());
            for (int depth = 0; depth < 20; depth++)
            {
                plan = planner.Build(state); bool changed = false;
                foreach (var node in plan.Nodes)
                    if (planner.GetRecipes(node.Name).Count > 0 && !node.IsCrafting) { Craft(planner, state, node.Name); changed = true; }
                if (!changed) break;
            }
            plan = planner.Build(state);
            Require(plan.Nodes.All(n => n.Quantity > 0) && plan.Purchases.All(n => n.Quantity > 0), "full graph contains positive demand only");
            Require(plan.Purchases.Select(n => n.Name).Distinct().Count() == plan.Purchases.Count, "full purchase totals deduplicate names");
            Require(plan.QuoteNames.Count == plan.QuoteNames.Distinct().Count() && plan.QuoteNames.Count <= 500, "price scope unique and within manual request cap");
            Require(plan.Crafts.Count > 50, "recipes cover exchange and lower ingredients");
            return "PASS procurement: " + assertions + " assertions; default purchases, recursive choices, shared rounding, synthesis seeds, independent acquisition, potion policy and saved progress.";
        }

        static void VerifySharedBatch()
        {
            var synthetic = new Catalog();
            var a = new TradeItem { Id = "testA", Name = "시험 교역품 A", Limit = 5, DefaultQuantity = 1 };
            var b = new TradeItem { Id = "testB", Name = "시험 교역품 B", Limit = 5, DefaultQuantity = 1 };
            a.Groups.Add(new MaterialGroup { Id = "groupA", Name = "제작품 A", PerTrade = 1, OutputPerBatch = 1, Lines = new List<MaterialLine> { new MaterialLine { Id = "inputA", Name = "공동 제작품", PerTrade = 4 } } });
            b.Groups.Add(new MaterialGroup { Id = "groupB", Name = "제작품 B", PerTrade = 1, OutputPerBatch = 1, Lines = new List<MaterialLine> { new MaterialLine { Id = "inputB", Name = "공동 제작품", PerTrade = 4 } } });
            a.Groups.Add(new MaterialGroup { Id = "groupShared", Name = "공동 제작품", PerTrade = 1, OutputPerBatch = 10, Lines = new List<MaterialLine> { new MaterialLine { Id = "inputShared", Name = "공동 원료", PerTrade = 0.7m, BatchSize = 10, BatchDemandPerTrade = 1, BatchInputPerBatch = 7 } } });
            synthetic.Trades.Add(a); synthetic.Trades.Add(b);
            var planner = new ProcurementPlanner(synthetic); var state = Selected(synthetic, "testA", "testB");
            Craft(planner, state, "제작품 A"); Craft(planner, state, "제작품 B"); Craft(planner, state, "공동 제작품");
            var plan = planner.Build(state); var shared = plan.Nodes.Single(n => n.Name == "공동 제작품");
            Require(shared.Quantity == 9 && shared.Batches == 1 && shared.Surplus == 1, "shared 4+4+1 demand rounds once to batch of ten");
            Require(Quantity(plan.Purchases, "공동 원료") == 7, "one shared batch uses seven raw inputs");
            state.Targets["testB"] = 2; plan = planner.Build(state); shared = plan.Nodes.Single(n => n.Name == "공동 제작품");
            Require(shared.Quantity == 13 && shared.Batches == 2 && Quantity(plan.Purchases, "공동 원료") == 14, "shared demand crosses batch boundary once");
            planner.SetChoice(state, "제작품 A", "buy"); plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "공동 원료") == 7, "inactive parent removed before shared rounding");

            var recursive = new Catalog();
            var trade = new TradeItem { Id = "cycle", Name = "순환 시험", Limit = 1, DefaultQuantity = 1 };
            trade.Groups.Add(new MaterialGroup { Id = "cycleA", Name = "순환 A", PerTrade = 1, Lines = new List<MaterialLine> { new MaterialLine { Id = "cycleBline", Name = "순환 B", PerTrade = 1 } } });
            trade.Groups.Add(new MaterialGroup { Id = "cycleB", Name = "순환 B", PerTrade = 1, Lines = new List<MaterialLine> { new MaterialLine { Id = "cycleAline", Name = "순환 A", PerTrade = 1 } } });
            recursive.Trades.Add(trade); planner = new ProcurementPlanner(recursive); state = Selected(recursive, "cycle"); Craft(planner, state, "순환 A"); Craft(planner, state, "순환 B");
            bool blocked = false; try { planner.Build(state); } catch (System.IO.InvalidDataException) { blocked = true; }
            Require(blocked, "cyclic recipes are rejected without recursion overflow");
        }

        static void VerifyLegacyBoardRecipe()
        {
            var catalog = new Catalog();
            catalog.Trades.Add(new TradeItem { Id = "legacy-board-trade", Name = "나무판 교환 시험", Limit = 1, DefaultQuantity = 1,
                Groups = new List<MaterialGroup> { new MaterialGroup { Id = "legacy-board-group", Name = "나무판", PerTrade = 2, OutputPerBatch = 1,
                    Lines = new List<MaterialLine> { new MaterialLine { Id = "legacy-board-line", CheckId = "legacy-board-check", Name = "나무장작", PerTrade = 10 } } } } });
            var planner = new ProcurementPlanner(catalog); var state = Selected(catalog, "legacy-board-trade");
            state.ProcurementChoices["나무판"] = "default";
            var plan = planner.Build(state);
            Require(planner.GetRecipes("나무판").Count == 0 && Quantity(plan.Purchases, "나무판") == 2 && plan.Nodes.Count == 1,
                "legacy workbook board recipe cannot override purchase-only policy");
        }

        static void VerifyWorkbookBranches(Catalog catalog)
        {
            var planner = new ProcurementPlanner(catalog);
            foreach (var trade in catalog.Trades)
            {
                var state = Selected(catalog, trade.Id);
                foreach (var group in trade.Groups)
                {
                    if (!group.IsPurchased) Craft(planner, state, group.Name);
                    foreach (var parent in group.Lines.Where(line => group.Lines.Any(child => child.ParentId == line.Id)))
                        Craft(planner, state, parent.Name);
                }
                // Independently read the workbook's terminal rows, retaining its
                // explicit nested-parent boundaries and batch formulas. Only the
                // original represented stages are crafted in this regression.
                var expected = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var group in trade.Groups)
                    foreach (var line in group.Lines.Where(line => !group.Lines.Any(child => child.ParentId == line.Id)))
                    {
                        string name = line.Name == "돌연변이 식물의 점액" ? "돌연변이 식물의 점액질" : line.Name;
                        decimal quantity = Calculator.Quantity(line, trade.DefaultQuantity);
                        if (!expected.ContainsKey(name)) expected[name] = 0;
                        expected[name] += quantity;
                    }
                var plan = planner.Build(state);
                var terminal = plan.Purchases.Concat(plan.Acquisitions).ToList();
                Require(expected.Count == terminal.Count, "workbook terminal item count " + trade.Id);
                foreach (var item in expected)
                    Require(Quantity(terminal, item.Key) == item.Value, "workbook terminal quantity " + trade.Id + " / " + item.Key);
            }
            foreach (var name in new[] { "질긴 끈", "질긴 실", "고급 가죽끈", "최고급 가죽끈", "가는 실뭉치", "굵은 실뭉치", "철봉", "고급 바닐라 향초", "포이즌 포션", "부드러운 양피지", "마법가루", "육각 너트", "육각 볼트" })
                Require(planner.GetRecipes(name).Count > 0, "deterministic lower ingredient remains craftable " + name);
            Require(planner.GetRecipes("순도 높은 강화제").Count == 0 && planner.CanAcquire("순도 높은 강화제"), "random decomposition stays direct acquisition without invented yield");
        }
    }
}
