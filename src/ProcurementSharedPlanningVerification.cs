using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public static class ProcurementSharedPlanningVerification
    {
        static int assertions;
        static void Require(bool value, string text) { assertions++; if (!value) throw new Exception("Shared planning: " + text); }
        static ProgressState Select(Catalog catalog, params string[] ids)
        {
            var state = new ProgressState(); state.Normalize(catalog);
            foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, ids.Contains(trade.Id));
            return state;
        }
        static decimal Quantity(IEnumerable<MaterialTotal> totals, string name) { var row = totals.FirstOrDefault(t => t.Name == name); return row == null ? 0 : row.Quantity; }
        public static string Run(Catalog catalog)
        {
            assertions = 0;
            var planner = new ProcurementPlanner(catalog); var engine = new ProcurementSharedPlanning(planner);
            var json = new JavaScriptSerializer(); var state = Select(catalog);
            Require(engine.Suggest(state, "실리엔 결정", "acquire").Count == 0, "empty selection has no propagation");
            state = Select(catalog, "C6", "C20", "C31", "C44", "K38", "K42");
            Require(engine.Suggest(state, "스태미나 500 포션", "acquire").Count == 0, "500 potion cannot propagate acquisition");
            Require(engine.Suggest(state, "실리엔", "buy").Count == 0, "buy click does not propose direct preparation");
            Require(engine.Suggest(state, "없는 재료", "acquire").Count == 0, "unknown unrelated item has no proposal");
            planner.SetChoice(state, "실리엔 결정", "acquire");
            var initialPlan = planner.Build(state);
            var unrelated = ProcurementReadiness.GetSteps(initialPlan, state).Single(s => s.Name == "스태미나 500 포션");
            ProcurementReadiness.SetReady(state, unrelated, true);
            var silienPurchase = ProcurementReadiness.GetSteps(initialPlan, state).Single(s => s.Name == "실리엔");
            ProcurementReadiness.SetReady(state, silienPurchase, true);
            string before = json.Serialize(state);
            var suggestions = engine.Suggest(state, "실리엔 결정", "acquire", "실리엔");
            Require(json.Serialize(state) == before, "suggestion preview does not mutate choices or readiness");
            Require(suggestions.Count == 1 && suggestions[0].AnchorName == "실리엔 결정", "clicked leaf is a single shared anchor");
            var crystal = suggestions[0];
            Require(crystal.RootNames.Count == 6 && crystal.RootNames.Contains("실리엔") && crystal.RootNames.Contains("정화된 토끼의 발") && crystal.RootNames.Contains("에너지 증폭 장치"), "all actual crystal-consuming exchange roots are affected");
            Require(!crystal.RootNames.Contains("뮤턴트"), "unrelated mutant does not acquire invented crystal dependency");
            Require(crystal.Paths.Contains("에너지 증폭 장치 → 에너지 컨버터 → 실리엔 → 실리엔 결정"), "readable paths retain nested parent chain");
            Require(crystal.Changes.Any(c => c.Name == "실리엔" && c.AfterChoice == "default"), "source root itself activates crystal branch");
            Require(!crystal.Changes.Any(c => c.Name == "힐웬" || c.Name == "돌연변이 토끼의 발" || c.Name == "중급 나무장작"), "sibling preparation modes remain unchanged");
            var applied = engine.Apply(state, new[] { crystal }); var plan = planner.Build(state);
            Require(applied.Count == 6 && applied.Select(c => c.Name).Distinct().Count() == applied.Count, "each changed parent applied once");
            Require(Quantity(plan.Acquisitions, "실리엔 결정") == 715 && Quantity(plan.Purchases, "실리엔") == 0, "all activated branches produce correct pooled crystal quantity");
            Require(planner.GetChoice(state, "힐웬") == "buy" && planner.GetChoice(state, "돌연변이 토끼의 발") == "buy", "unselected siblings stay buying after apply");
            Require(ProcurementReadiness.IsReady(state, unrelated), "unrelated completed purchase remains ready");
            Require(!state.ProcurementReady.ContainsKey(silienPurchase.Key), "changed preparation invalidates only affected completed item");
            Require(engine.Apply(state, new[] { crystal }).Count == 0, "applying same changes twice is a no-op");
            Require(engine.Suggest(state, "실리엔 결정", "acquire", "실리엔").Count == 0, "fully applied shared path has no redundant suggestion");

            state = Select(catalog, "C20", "C31", "C44", "K21", "K38", "K42");
            planner.SetChoice(state, "정화된 토끼의 발", "default");
            suggestions = engine.Suggest(state, "정화된 토끼의 발", "default", "정화된 토끼의 발");
            Require(suggestions.Any(s => s.AnchorName == "실리엔"), "craft click exposes its shared lower material");
            Require(!suggestions.Any(s => s.AnchorName == "실리엔 결정"), "ancestor and descendant with equal affected roots are coalesced");
            Require(suggestions.Any(s => s.AnchorName == "돌연변이 토끼의 발"), "separate shared sibling remains independently selectable");
            var silien = suggestions.Single(s => s.AnchorName == "실리엔");
            Require(silien.Changes.Any(c => c.Name == "실리엔 결정" && c.AfterChoice == "acquire"), "chosen anchor recursively prepares its lower leaf");
            var readyParent = ProcurementReadiness.GetSteps(planner.Build(state), state).Single(s => s.Name == "정화된 토끼의 발" && s.Kind == "craft");
            ProcurementReadiness.SetReady(state, readyParent, true);
            engine.Apply(state, new[] { silien });
            Require(planner.GetChoice(state, "돌연변이 토끼의 발") == "buy" && planner.GetChoice(state, "뮤턴트") == "buy", "declined common sibling does not affect its own roots");
            Require(ProcurementReadiness.IsReady(state, readyParent), "already-chosen unchanged parent completion is retained");
            state = Select(catalog, "K38"); planner.SetChoice(state, "정화된 토끼의 발", "default");
            suggestions = engine.Suggest(state, "정화된 토끼의 발", "default", "정화된 토끼의 발");
            Require(suggestions.Single().AnchorName == "실리엔" && suggestions.Single().RootNames.Count == 2, "same selected trade can contain two separate exchange roots sharing a material");
            state = Select(catalog, "C31"); planner.SetChoice(state, "실리엔", "default");
            Require(engine.Suggest(state, "실리엔", "default", "실리엔").Count == 0, "one root with no other shared path has no suggestion");

            VerifyAlternatives(catalog, json);
            VerifySynthesis(planner, engine, catalog);
            VerifyBoardPurchaseOnly(planner, engine, catalog, json);
            VerifyCycles();
            state = Select(catalog, catalog.Trades.Select(t => t.Id).ToArray()); planner.SetChoice(state, "마법의 양피지", "default");
            suggestions = engine.Suggest(state, "마법의 양피지", "default", "마법의 양피지");
            Require(suggestions.Count > 0, "full selected catalog exposes shared materials");
            foreach (var suggestion in suggestions)
            {
                Require(suggestion.Changes.Count > 0 && suggestion.RootNames.Count > 1, "every exposed suggestion has real changes and another root");
                Require(suggestion.Changes.Select(c => c.Name).Distinct().Count() == suggestion.Changes.Count, "candidate changes deduplicate shared parent names");
            }
            var changed = engine.Apply(state, suggestions);
            Require(changed.Select(c => c.Name).Distinct().Count() == changed.Count, "combined proposals update overlapping materials once");
            Require(planner.Build(state).Nodes.Count > 0, "combined apply builds a valid active plan");
            return "PASS shared planning: " + assertions + " assertions; scoped paths, anchor choices, deduplication, alternative recipes, independent siblings, atomic apply, readiness, seeds and cycle guards.";
        }

        static void VerifyAlternatives(Catalog source, JavaScriptSerializer json)
        {
            var catalog = Catalog.FromJson(json.Serialize(source));
            catalog.Trades.Add(new TradeItem { Id = "extra-silver", Name = "추가 은 부품 교역품", Limit = 5, DefaultQuantity = 1,
                Groups = new List<MaterialGroup> { new MaterialGroup { Id = "extra-silver-group", Name = "추가 은 부품", PerTrade = 1, OutputPerBatch = 1,
                    Lines = new List<MaterialLine> { new MaterialLine { Id = "extra-silver-line", CheckId = "extra-silver-check", Name = "은괴", PerTrade = 2 } } } } });
            var planner = new ProcurementPlanner(catalog); var engine = new ProcurementSharedPlanning(planner);
            var state = Select(catalog, "C15", "extra-silver"); planner.SetChoice(state, "은괴", "fragments"); planner.SetChoice(state, "은판", "default");
            var suggestion = engine.Suggest(state, "은판", "default", "은판").Single(s => s.AnchorName == "은괴");
            Require(suggestion.Changes.Any(c => c.Name == "은광석 조각" && c.AfterChoice == "acquire"), "currently selected fragment recipe respected");
            Require(!suggestion.Changes.Any(c => c.Name == "은광석" || c.Name == "아라트의 결정" || c.Name == "축복의 포션"), "unselected refining and synthesis alternatives excluded");
            engine.Apply(state, new[] { suggestion });
            Require(planner.GetChoice(state, "은괴") == "fragments" && Quantity(planner.Build(state).Acquisitions, "은광석 조각") == 90, "alternate recipe preserved through combined demand");
            var unchanged = json.Serialize(state); bool conflict = false;
            try { engine.Apply(state, new[] { new ProcurementSharedSuggestion { Changes = new List<ProcurementChoiceChange> {
                new ProcurementChoiceChange { Name = "은괴", AfterChoice = "ore" }, new ProcurementChoiceChange { Name = "은괴", AfterChoice = "synthesis" } } } }); }
            catch (ArgumentException) { conflict = true; }
            Require(conflict && json.Serialize(state) == unchanged, "conflicting selections fail atomically before modifying state");
        }

        static void VerifySynthesis(ProcurementPlanner planner, ProcurementSharedPlanning engine, Catalog catalog)
        {
            var state = Select(catalog, "C15", "K12"); planner.SetChoice(state, "은판", "default");
            var suggestions = engine.Suggest(state, "은판", "default", "은판");
            var arat = suggestions.Single(s => s.AnchorName == "아라트의 결정");
            Require(!suggestions.Any(s => s.AnchorName == "은괴"), "synthesis seed recursion does not invent another silver root");
            engine.Apply(state, new[] { arat }); var plan = planner.Build(state);
            Require(planner.GetChoice(state, "축복의 포션") == "buy", "choosing Arat leaves sibling blessing potion mode untouched");
            Require(plan.Purchases.Any(p => p.Name == "은괴" && p.Quantity == 1) && plan.Purchases.Any(p => p.Name == "금괴" && p.Quantity == 1), "synthesis starting ingots remain terminal purchases");
            Require(plan.Acquisitions.Any(p => p.Name == "아라트의 결정"), "shared synthesis reagent acquisition activated");
            Require(!plan.Acquisitions.Any(p => p.Name == "은괴" || p.Name == "금괴"), "seed ingots are never changed to acquisition");
        }

        static void VerifyBoardPurchaseOnly(ProcurementPlanner planner, ProcurementSharedPlanning engine, Catalog catalog, JavaScriptSerializer json)
        {
            var state = Select(catalog, catalog.Trades.Select(t => t.Id).ToArray());
            planner.SetChoice(state, "펫 놀이세트", "default");
            state.ProcurementChoices["나무판"] = "default";
            Require(engine.Suggest(state, "펫 놀이세트", "default", "펫 놀이세트").Count == 0,
                "purchase-only board hides its manufacturing subtree from shared pet-set suggestions");
            Require(engine.Suggest(state, "나무판", "default").Count == 0 && engine.Suggest(state, "나무판", "acquire").Count == 0,
                "board cannot initiate shared craft or acquisition propagation");
            var plan = planner.Build(state);
            Require(Quantity(plan.Purchases, "나무판") == 3 && plan.Crafts.Any(n => n.Name == "펫 놀이세트") && !plan.Crafts.Any(n => n.Name == "나무판"),
                "pet play set stays craftable while its board is purchased");
            string before = json.Serialize(state); bool rejected = false;
            try { engine.Apply(state, new[] { new ProcurementSharedSuggestion { AnchorName = "나무판", Changes = new List<ProcurementChoiceChange> {
                new ProcurementChoiceChange { Name = "매듭끈", AfterChoice = "default" }, new ProcurementChoiceChange { Name = "나무판", AfterChoice = "default" } } } }); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected && json.Serialize(state) == before, "stale bulk board-craft change is rejected before other changes apply");
        }

        static void VerifyCycles()
        {
            var catalog = new Catalog();
            var trade = new TradeItem { Id = "cycle", Name = "순환 시험", Limit = 1, DefaultQuantity = 1 };
            trade.Groups.Add(new MaterialGroup { Id = "cycleA", Name = "순환 A", PerTrade = 1, Lines = new List<MaterialLine> { new MaterialLine { Id = "lineB", CheckId = "checkB", Name = "순환 B", PerTrade = 1 } } });
            trade.Groups.Add(new MaterialGroup { Id = "cycleB", Name = "순환 B", PerTrade = 1, Lines = new List<MaterialLine> { new MaterialLine { Id = "lineA", CheckId = "checkA", Name = "순환 A", PerTrade = 1 } } });
            catalog.Trades.Add(trade);
            var planner = new ProcurementPlanner(catalog); var engine = new ProcurementSharedPlanning(planner); var state = Select(catalog, "cycle"); planner.SetChoice(state, "순환 A", "default");
            Require(engine.Suggest(state, "순환 A", "default", "순환 A").Count == 0, "cyclic preview cannot produce an unbuildable suggestion");
            string before = new JavaScriptSerializer().Serialize(state); bool blocked = false;
            try { engine.Apply(state, new[] { new ProcurementSharedSuggestion { Changes = new List<ProcurementChoiceChange> { new ProcurementChoiceChange { Name = "순환 B", AfterChoice = "default" } } } }); }
            catch (System.IO.InvalidDataException) { blocked = true; }
            Require(blocked && new JavaScriptSerializer().Serialize(state) == before, "cyclic combined apply fails before changing real state");
        }
    }
}
