using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public static class NpcProcurementVerification
    {
        static int assertions;
        static void Require(bool value, string message) { assertions++; if (!value) throw new Exception("NPC procurement: " + message); }
        public static string Run(Catalog catalog)
        {
            assertions = 0;
            var planner = new ProcurementPlanner(catalog); var state = new ProgressState(); state.Normalize(catalog);
            foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, trade.Id == "C31");
            state.Targets["C31"] = 3;
            var expected = new HashSet<string>(new[] { "새우", "설탕", "마늘" }, StringComparer.Ordinal);
            Require(expected.SetEquals(ProcurementPlanner.NpcPurchaseNames), "NPC policy is restricted to the three agreed bait ingredients");
            var namesCopy = ProcurementPlanner.NpcPurchaseNames; namesCopy[0] = "변경 테스트";
            Require(expected.SetEquals(ProcurementPlanner.NpcPurchaseNames), "public NPC names cannot mutate the policy");
            var recipe = planner.GetRecipes("새우 조련 미끼").Single();
            Require(recipe.OutputPerBatch == 10 && expected.SetEquals(recipe.Ingredients.Select(i => i.Name)) && recipe.Ingredients.All(i => i.Quantity == 1m), "current catalog recipe remains ten bait from one of each shop ingredient");
            Require(!planner.IsNpcPurchase("새우 조련 미끼") && planner.GetChoice(state, "새우 조련 미끼") == "buy", "finished bait retains normal default purchase choice");
            var bought = planner.Build(state);
            Require(bought.Purchases.Single(p => p.Name == "새우 조련 미끼").Quantity == 12m && !bought.Nodes.Any(n => expected.Contains(n.Name)), "buying finished bait does not include its shop ingredients");
            var preview = planner.Preview(state, "새우 조련 미끼", 12m);
            Require(preview.Batches == 2m && preview.Children.Count == 3 && preview.Children.All(c => c.Quantity == 2m && c.Node.IsAcquiring), "bought-parent preview displays correct rounded NPC quantities without activating the recipe");

            foreach (string name in expected)
            {
                foreach (string stale in new[] { "buy", "acquire", "default" })
                {
                    state.ProcurementChoices[name] = stale;
                    Require(planner.GetChoice(state, name) == "acquire", "old " + stale + " choice cannot override NPC policy for " + name);
                }
                Require(planner.IsNpcPurchase(name) && !planner.CanAcquire(name) && planner.GetRecipes(name).Count == 0, "NPC material exposes neither manual acquisition toggle nor craft recipe for " + name);
                planner.SetChoice(state, name, "buy");
                Require(planner.GetChoice(state, name) == "acquire" && !state.ProcurementChoices.ContainsKey(name), "explicit attempted buy is normalized without enabling auction purchases for " + name);
            }
            planner.SetChoice(state, "새우 조련 미끼", recipe.Id); var plan = planner.Build(state);
            Require(plan.Crafts.Single().Name == "새우 조련 미끼" && plan.Crafts.Single().Batches == 2m, "finished bait still accepts craft choice with rounded batches");
            Require(expected.SetEquals(plan.Acquisitions.Select(a => a.Name)) && plan.Acquisitions.All(a => a.Quantity == 2m), "NPC ingredients retain all quantities in acquisition requirements");
            Require(plan.Purchases.Count == 1 && plan.Purchases.Single().Name == "실리엔", "NPC materials contribute zero to auction purchase totals");
            Require(!plan.QuoteNames.Any(expected.Contains) && new HashSet<string>(plan.QuoteNames).SetEquals(new[] { "실리엔", "새우 조련 미끼" }), "active quote targets include finished comparison item but exclude every NPC ingredient");
            var steps = ProcurementReadiness.GetSteps(plan, state);
            Require(steps.Count(s => expected.Contains(s.Name) && s.Kind == "acquire") == 3 && !steps.Any(s => expected.Contains(s.Name) && s.Kind == "purchase"), "NPC quantities retain independent preparation checkboxes through existing acquire kind");
            var shrimp = steps.Single(s => s.Name == "새우"); ProcurementReadiness.SetReady(state, shrimp, true);
            Require(ProcurementReadiness.GetStatus(plan, state).AcquireReady == 1 && !ProcurementReadiness.IsItemReady(plan, state, "새우 조련 미끼"), "NPC purchase check advances progress without completing bait crafting");
            var serializer = new JavaScriptSerializer(); string original = serializer.Serialize(state);
            planner.Build(state); ProcurementReadiness.GetStatus(plan, state);
            Require(serializer.Serialize(state) == original, "policy evaluation and progress reads do not mutate user choices or readiness records");
            ProcurementReadiness.SetReady(state, steps.Single(s => s.Name == "새우 조련 미끼"), true);
            ProcurementReadiness.SetReady(state, steps.Single(s => s.Name == "실리엔"), true);
            Require(ProcurementReadiness.GetStatus(plan, state).Percent == 100 && !ProcurementReadiness.GetSteps(plan, state).Single(s => s.Name == "설탕").IsReady, "finished roots cover NPC preparation without writing additional ingredient checks");
            state.Targets["C31"] = 6; plan = planner.Build(state);
            Require(plan.Acquisitions.All(a => a.Quantity == 3m) && !ProcurementReadiness.GetSteps(plan, state).Single(s => s.Name == "새우").IsReady, "crossing bait batch boundary increases NPC quantities and reopens insufficient snapshots");
            planner.SetChoice(state, "새우 조련 미끼", "buy"); plan = planner.Build(state);
            Require(plan.Acquisitions.Count == 0 && plan.Purchases.Any(p => p.Name == "새우 조련 미끼"), "switching finished bait back to buy removes its NPC branch");

            var shared = new ProcurementSharedPlanning(planner);
            foreach (string name in expected) Require(shared.Suggest(state, name, "acquire").Count == 0, "NPC material never opens a shared manual-acquisition suggestion for " + name);
            var staleSuggestion = new ProcurementSharedSuggestion { AnchorName = "새우" };
            staleSuggestion.Changes.Add(new ProcurementChoiceChange { Name = "새우", BeforeChoice = "buy", AfterChoice = "acquire" });
            original = serializer.Serialize(state);
            Require(shared.Apply(state, new[] { staleSuggestion }).Count == 0 && serializer.Serialize(state) == original, "stale shared suggestion ignores NPC targets without invalidating their prepared snapshots");

            foreach (string name in new[] { "엘레멘탈 리무버", "펫이 좋아하는 잡동사니", "종이", "코스모스 추출액", "빈 병", "생기 있는 깃털", "미니 바닐라 향초", "아라트의 결정", "거미줄" })
            {
                Require(!planner.IsNpcPurchase(name) && planner.CanAcquire(name), "other vendor or raw materials retain their manual source choices: " + name);
                planner.SetChoice(state, name, "buy"); Require(planner.GetChoice(state, name) == "buy", "other material remains buyable: " + name);
                planner.SetChoice(state, name, "acquire"); Require(planner.GetChoice(state, name) == "acquire", "other material remains directly acquirable: " + name);
            }
            foreach (string name in new[] { "나무판", "스태미나 500 포션", "마리오네트 500 포션" })
                Require(planner.IsPurchaseOnly(name) && !planner.IsNpcPurchase(name) && planner.GetChoice(state, name) == "buy" && !planner.CanAcquire(name), "existing finished purchase-only policy remains unchanged for " + name);
            return "PASS NPC procurement: " + assertions + " assertions; three catalog-verified bait ingredients use fixed NPC acquisition with retained quantities and explicit checks, no auction totals or quote targets, no shared choice mutation; bait buy/craft and all other material choices remain unchanged.";
        }
    }
}
