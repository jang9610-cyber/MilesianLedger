using System;
using System.Linq;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public static class ProcurementReadinessVerification
    {
        static int assertions;
        static void Require(bool value, string message) { assertions++; if (!value) throw new Exception("Procurement readiness: " + message); }
        static ProgressState Select(Catalog catalog, params string[] ids)
        {
            var state = new ProgressState(); state.Normalize(catalog);
            foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, ids.Contains(trade.Id));
            return state;
        }
        static void Craft(ProcurementPlanner planner, ProgressState state, string name)
        {
            ProcurementReadiness.InvalidateChoice(state, name); planner.SetChoice(state, name, planner.GetRecipes(name).First().Id);
        }
        static ProcurementStep Step(ProcurementPlan plan, ProgressState state, string name, string kind)
        {
            return ProcurementReadiness.GetSteps(plan, state).Single(s => s.Name == name && s.Kind == kind);
        }
        static void Near(decimal actual, decimal expected, string message)
        {
            Require(Math.Abs(actual - expected) < 0.000000000000000000000001m, message + " (" + actual + " vs " + expected + ")");
        }
        static ProgressState SelectOne(Catalog catalog, params string[] ids)
        {
            var state = Select(catalog, ids);
            foreach (string id in ids) state.Targets[id] = 1;
            return state;
        }
        static void Set(ProcurementPlan plan, ProgressState state, string name, string kind, bool ready)
        {
            ProcurementReadiness.SetReady(state, Step(plan, state, name, kind), ready);
        }
        public static string Run(Catalog catalog)
        {
            assertions = 0;
            var planner = new ProcurementPlanner(catalog); var state = Select(catalog); var plan = planner.Build(state);
            var status = ProcurementReadiness.GetStatus(plan, state);
            Require(status.Total == 0 && status.Ready == 0 && status.Percent == 0, "empty plan starts at zero without division error");

            state = Select(catalog, "C6"); plan = planner.Build(state);
            Require(ProcurementReadiness.GetSteps(plan, state).Count == 2, "two initial purchase checklist steps");
            var knot = Step(plan, state, "매듭끈", "purchase");
            ProcurementReadiness.SetReady(state, knot, true); status = ProcurementReadiness.GetStatus(plan, state);
            Require(status.Ready == 1 && status.Total == 2 && status.Percent == 50, "purchase check immediately contributes to overall rate");
            Require(!ProcurementReadiness.IsTradeReady(catalog, state, catalog.Trades.Single(t => t.Id == "C6"), plan), "trade waits for every direct item");
            ProcurementReadiness.SetReady(state, Step(plan, state, "스태미나 500 포션", "purchase"), true);
            Require(ProcurementReadiness.IsTradeReady(catalog, state, catalog.Trades.Single(t => t.Id == "C6"), plan), "all direct purchases complete trade");
            ProcurementReadiness.SetReady(state, knot, false);
            Require(ProcurementReadiness.GetStatus(plan, state).Ready == 1, "uncheck immediately removes readiness");

            Craft(planner, state, "매듭끈"); plan = planner.Build(state);
            foreach (var step in ProcurementReadiness.GetSteps(plan, state).Where(s => s.Kind == "purchase")) ProcurementReadiness.SetReady(state, step, true);
            Require(!Step(plan, state, "매듭끈", "craft").IsReady, "having all ingredients never implies completed craft");
            Require(!ProcurementReadiness.IsItemReady(plan, state, "매듭끈"), "item readiness follows explicit finished step");
            ProcurementReadiness.SetReady(state, Step(plan, state, "매듭끈", "craft"), true);
            ProcurementReadiness.SetReady(state, Step(plan, state, "가는 실뭉치", "purchase"), false);
            Require(ProcurementReadiness.IsTradeReady(catalog, state, catalog.Trades.Single(t => t.Id == "C6"), plan), "explicit final readiness can complete trade independently");
            Require(ProcurementReadiness.GetStatus(plan, state).Percent == 100, "all exchange requirements complete global progress through upper-use credit");
            var coveredThread = Step(plan, state, "가는 실뭉치", "purchase");
            Require(!coveredThread.IsReady && coveredThread.IsCovered && !state.ProcurementReady.ContainsKey(coveredThread.Key), "upper completion derives lower credit without recording a checkbox");

            Craft(planner, state, "가는 실뭉치"); planner.SetChoice(state, "거미줄", "acquire"); plan = planner.Build(state);
            var web = Step(plan, state, "거미줄", "acquire"); var thread = Step(plan, state, "가는 실뭉치", "craft"); knot = Step(plan, state, "매듭끈", "craft");
            Require(web.Level == 0 && thread.Level == 1 && knot.Level == 2, "raw then thread then final knot dependency levels");
            Require(web.GroupLabel == "하위 재료" && thread.GroupLabel == "중간 제작품 · 1단계" && knot.GroupLabel == "최종 교환 재료", "classification labels reflect active graph");
            Require(knot.IsReady, "changing lower sourcing does not erase an already finished parent");
            ProcurementReadiness.SetReady(state, web, true);
            Require(ProcurementReadiness.GetStatus(plan, state).AcquireReady == 1, "direct acquisition checklist included");

            state.Targets["C6"] = 10; plan = planner.Build(state); ProcurementReadiness.SetReady(state, Step(plan, state, "거미줄", "acquire"), true);
            state.Targets["C6"] = 11; plan = planner.Build(state);
            Require(!Step(plan, state, "거미줄", "acquire").IsReady, "target increase reopens insufficient checked quantity");
            state.Targets["C6"] = 9; plan = planner.Build(state);
            Require(Step(plan, state, "거미줄", "acquire").IsReady, "target decrease retains sufficient prepared amount");
            state.Targets["C6"] = 10; plan = planner.Build(state);
            ProcurementReadiness.SetReady(state, Step(plan, state, "매듭끈", "craft"), true);
            Calculator.SetSelected(state, catalog.Trades.Single(t => t.Id == "C44"), true); Craft(planner, state, "튼튼한 고리"); plan = planner.Build(state);
            Require(!Step(plan, state, "매듭끈", "craft").IsReady, "new shared use reopens aggregate finished quantity");
            Require(!Step(plan, state, "거미줄", "acquire").IsReady, "new shared use reopens lower quantity");

            var unrelated = Step(plan, state, "스태미나 500 포션", "purchase"); ProcurementReadiness.SetReady(state, unrelated, true);
            ProcurementReadiness.SetReady(state, Step(plan, state, "매듭끈", "craft"), true);
            ProcurementReadiness.InvalidateChoice(state, "매듭끈"); planner.SetChoice(state, "매듭끈", "buy"); plan = planner.Build(state);
            Require(!Step(plan, state, "매듭끈", "purchase").IsReady, "mode change requires confirmation for new preparation step");
            Craft(planner, state, "매듭끈"); plan = planner.Build(state);
            Require(!Step(plan, state, "매듭끈", "craft").IsReady, "returning to a previous mode cannot restore discarded completion");
            Require(Step(plan, state, unrelated.Name, unrelated.Kind).IsReady, "changing item mode preserves unrelated completed items");

            state = Select(catalog, "C15"); Craft(planner, state, "은판"); Craft(planner, state, "은괴"); plan = planner.Build(state);
            var seed = Step(plan, state, "은괴", "purchase"); var ingot = Step(plan, state, "은괴", "craft");
            Require(seed.Key != ingot.Key && seed.IsSeed && !seed.HasExchangeUse && !seed.IsFinal && seed.Quantity == 1, "seed purchase has a separate stable key and lower classification");
            ProcurementReadiness.SetReady(state, seed, true);
            Require(!Step(plan, state, "은괴", "craft").IsReady, "seed check never overwrites crafted ingot check");
            Require(!ProcurementReadiness.IsItemReady(plan, state, "은괴"), "item ready ignores seed-only completion");
            ProcurementReadiness.SetReady(state, ingot, true);
            Require(Step(plan, state, "은괴", "purchase").IsReady && Step(plan, state, "은괴", "craft").IsReady, "seed and final quantities persist independently");
            planner.SetChoice(state, "은괴", "ore"); plan = planner.Build(state);
            Require(!Step(plan, state, "은괴", "craft").IsReady, "recipe fingerprint detects change even before explicit invalidation");
            ProcurementReadiness.InvalidateChoice(state, "은괴");
            Require(!state.ProcurementReady.Keys.Any(k => k.EndsWith(":은괴", StringComparison.Ordinal)), "mode invalidation clears both ingot phases");
            var oreStep = Step(plan, state, "은괴", "craft"); ProcurementReadiness.SetReady(state, oreStep, true);
            var oreRecipe = planner.GetRecipes("은괴").Single(r => r.Id == "ore"); oreRecipe.Ingredients[0].Quantity = 6;
            Require(!Step(planner.Build(state), state, "은괴", "craft").IsReady, "recipe input change invalidates stale completion under same recipe id");
            oreRecipe.Ingredients[0].Quantity = 5;

            var serializer = new JavaScriptSerializer();
            var restored = serializer.Deserialize<ProgressState>(serializer.Serialize(state)); restored.Normalize(catalog);
            Require(Step(planner.Build(restored), restored, "은괴", "craft").IsReady, "readiness survives serialization and normalization");
            var undo = serializer.Deserialize<ProgressState>(serializer.Serialize(restored));
            restored.Checks["legacy-test"] = true; restored.ResetChecks();
            Require(restored.Checks.Count == 0 && restored.ProcurementReady.Count == 0, "weekly reset clears old checks and new checklist");
            Require(ProcurementReadiness.GetStatus(planner.Build(undo), undo).Ready > 0, "undo copy preserves readiness snapshot");
            var legacy = serializer.Deserialize<ProgressState>("{\"SchemaVersion\":2,\"Targets\":{\"C6\":12},\"Selected\":{\"C6\":true},\"Checks\":{\"H6\":true}}"); legacy.Normalize(catalog);
            Require(legacy.ProcurementReady.Count == 0 && legacy.Targets["C6"] == 12 && legacy.Selected["C6"], "legacy saves gain an empty checklist without losing selection");

            state = SelectOne(catalog, "C31", "C44", "K38"); Craft(planner, state, "실리엔"); Craft(planner, state, "에너지 컨버터"); Craft(planner, state, "에너지 증폭 장치"); Craft(planner, state, "정화된 토끼의 발"); plan = planner.Build(state);
            var silien = Step(plan, state, "실리엔", "craft"); var converter = Step(plan, state, "에너지 컨버터", "craft"); var amplifier = Step(plan, state, "에너지 증폭 장치", "craft");
            Require(silien.HasExchangeUse && !silien.IsFinal && silien.DirectQuantity == 2, "shared Silien exchange root remains intermediate before converter");
            Require(converter.HasExchangeUse && !converter.IsFinal && converter.DirectQuantity == 1, "shared converter retains its own exchange requirement");
            Require(amplifier.IsFinal && silien.Level < converter.Level && converter.Level < amplifier.Level, "Silien, converter, final amplifier dependency order");
            foreach (var step in ProcurementReadiness.GetSteps(plan, state).Where(s => s.HasExchangeUse)) ProcurementReadiness.SetReady(state, step, true);
            Require(ProcurementReadiness.IsTradeReady(catalog, state, catalog.Trades.Single(t => t.Id == "C31"), plan), "trade checks direct requirements even when shared root is intermediate");
            Require(ProcurementReadiness.GetStatus(plan, state).Percent == 100, "all exchange roots including intermediate direct uses give 100 percent");

            CheckCoverage(catalog, planner);

            state = Select(catalog, "C6"); Craft(planner, state, "매듭끈"); plan = planner.Build(state);
            var line = catalog.Trades.Single(t => t.Id == "C6").Groups.SelectMany(g => g.Lines).Single(l => l.Id == "F6.1");
            Require(ProcurementReadiness.TrySetFromLegacyLine(state, plan, line, 25, true), "exact active purchase line bridges safely");
            Require(Step(plan, state, "가는 실뭉치", "purchase").IsReady && !Step(plan, state, "매듭끈", "craft").IsReady, "legacy line cannot complete crafted parent");
            Require(!ProcurementReadiness.TrySetFromLegacyLine(state, plan, line, 10, true), "partial legacy quantity cannot mark total ready");
            Calculator.SetSelected(state, catalog.Trades.Single(t => t.Id == "C44"), true); Craft(planner, state, "튼튼한 고리"); plan = planner.Build(state);
            Require(!ProcurementReadiness.TrySetFromLegacyLine(state, plan, line, 25, true), "shared legacy line cannot cover another trade use");
            Craft(planner, state, "가는 실뭉치"); plan = planner.Build(state);
            Require(!ProcurementReadiness.TrySetFromLegacyLine(state, plan, line, 25, true), "legacy line never sets a craft step");
            state = Select(catalog, "C44"); Craft(planner, state, "튼튼한 고리"); plan = planner.Build(state);
            var derivedLine = catalog.Trades.Single(t => t.Id == "C44").Groups.SelectMany(g => g.Lines).Single(l => l.Id == "F48");
            Require(!ProcurementReadiness.TrySetFromLegacyLine(state, plan, derivedLine, 3, true), "calculated legacy parent cannot imply a purchased intermediate is ready");

            state = Select(catalog, catalog.Trades.Select(t => t.Id).ToArray());
            for (int depth = 0; depth < 20; depth++)
            {
                bool changed = false;
                foreach (var node in planner.Build(state).Nodes)
                    if (!node.IsCrafting && planner.GetRecipes(node.Name).Count > 0) { Craft(planner, state, node.Name); changed = true; }
                if (!changed) break;
            }
            plan = planner.Build(state); var ordered = ProcurementReadiness.GetSteps(plan, state);
            Require(ordered.Select(s => s.Key).Distinct().Count() == ordered.Count, "global checklist has unique preparation-step keys");
            for (int i = 0; i < ordered.Count; i++)
                foreach (string dependency in ordered[i].DependencyKeys)
                    Require(ordered.FindIndex(s => s.Key == dependency) < i, "grouping preserves every dependency before parent " + ordered[i].Name);
            foreach (var step in ordered.Where(s => s.HasExchangeUse)) ProcurementReadiness.SetReady(state, step, true);
            status = ProcurementReadiness.GetStatus(plan, state);
            Require(status.Percent == 100 && status.Ready == status.Total, "only all exchange roots checked still complete the full catalog plan");
            Require(status.ExplicitReady == ordered.Count(s => s.HasExchangeUse) && status.CoveredReady > 0, "status distinguishes explicit and derived full completion");
            Require(state.ProcurementReady.Count == status.ExplicitReady, "global coverage never writes lower snapshots");
            Require(ProcurementReadiness.GetSteps(plan, state).All(s => s.CompletionFraction >= 0 && s.CompletionFraction <= 1), "every global progress fraction stays bounded");
            foreach (var step in ordered) ProcurementReadiness.SetReady(state, step, true);
            status = ProcurementReadiness.GetStatus(plan, state);
            Require(status.Ready == status.Total && status.Percent == 100, "all explicit steps produce 100 percent");
            Require(catalog.Trades.All(t => ProcurementReadiness.IsTradeReady(catalog, state, t, plan)), "all direct exchange requirements complete every selected trade");
            return "PASS procurement readiness: " + assertions + " assertions; explicit snapshots, upper-use progress credit, shared demand, direct roots, batch boundaries, seeds, quantity and recipe changes, legacy safety, reset and persistence.";
        }

        static void CheckCoverage(Catalog catalog, ProcurementPlanner planner)
        {
            // Three actual selected roots share Silien 1 + 1 + 3. Completing
            // just the glue must not mark the other two uses, or Silien, ready.
            var state = SelectOne(catalog, "K38", "K42");
            foreach (string name in new[] { "정화된 토끼의 발", "에너지 컨버터", "끈끈이 풀", "실리엔" }) Craft(planner, state, name);
            planner.SetChoice(state, "실리엔 결정", "acquire");
            var plan = planner.Build(state);
            Require(Step(plan, state, "실리엔", "craft").Quantity == 5 && Step(plan, state, "실리엔 결정", "acquire").Quantity == 25, "actual shared demand is pooled before progress allocation");
            Set(plan, state, "끈끈이 풀", "craft", true);
            var silien = Step(plan, state, "실리엔", "craft"); var crystals = Step(plan, state, "실리엔 결정", "acquire");
            Near(silien.CompletionFraction, 0.6m, "glue contributes only three of five shared Silien uses");
            Near(crystals.CompletionFraction, 0.6m, "partial upper credit passes through another crafting level");
            Require(!silien.IsReady && !silien.IsCovered && silien.PartialCoveredFraction == 0.6m, "partial coverage remains visibly distinct from explicit or fully covered readiness");
            Require(!Step(plan, state, "정화된 토끼의 발", "craft").IsReady && !Step(plan, state, "에너지 컨버터", "craft").IsReady, "one parent's completion never completes sibling parents");
            Require(ProcurementReadiness.GetStatus(plan, state).Percent < 100 && state.ProcurementReady.Count == 1, "shared partial progress neither claims 100 percent nor writes descendant checks");
            var current = ProcurementReadiness.GetSteps(plan, state); var status = ProcurementReadiness.GetStatus(current);
            Near(status.Percent, 100m * current.Sum(s => s.CompletionFraction) / current.Count, "global percentage includes fractional contribution");
            Require(status.Ready == current.Count(s => s.CompletionFraction == 1m) && status.AcquireReady == 0, "full counts exclude fractional acquisition credit");

            Set(plan, state, "실리엔 결정", "acquire", true);
            var serializer = new JavaScriptSerializer(); string explicitJson = serializer.Serialize(state.ProcurementReady);
            for (int i = 0; i < 3; i++) ProcurementReadiness.GetStatus(plan, state);
            Require(serializer.Serialize(state.ProcurementReady) == explicitJson, "reading derived progress does not mutate stored snapshots");
            Set(plan, state, "끈끈이 풀", "craft", false);
            Require(Step(plan, state, "실리엔", "craft").CompletionFraction == 0m, "unchecking parent withdraws its shared credit");
            crystals = Step(plan, state, "실리엔 결정", "acquire");
            Require(crystals.IsReady && crystals.CompletionFraction == 1m && crystals.CoveredFraction == 0m, "independent lower snapshot survives withdrawal of upper credit");
            Require(!Step(plan, state, "실리엔", "craft").IsReady, "explicit lower completion still never completes its parent");
            Set(plan, state, "끈끈이 풀", "craft", true); Set(plan, state, "에너지 컨버터", "craft", true);
            Near(Step(plan, state, "실리엔", "craft").CompletionFraction, 0.8m, "separate finished parents add only their own shared uses");
            Set(plan, state, "정화된 토끼의 발", "craft", true);
            silien = Step(plan, state, "실리엔", "craft"); crystals = Step(plan, state, "실리엔 결정", "acquire");
            Require(silien.IsCovered && !silien.IsReady && silien.CompletionFraction == 1m, "all consuming parents fully cover shared intermediate without checking it");
            Require(crystals.IsReady && crystals.CoveredFraction == 1m && !crystals.IsCovered && crystals.CompletionFraction == 1m, "explicit plus derived completion is capped rather than double counted");
            Set(plan, state, "실리엔 결정", "acquire", false);
            Require(Step(plan, state, "실리엔 결정", "acquire").IsCovered && ProcurementReadiness.GetStatus(plan, state).AcquireReady == 1, "unchecking an independently ready lower step retains current full upper coverage");
            Set(plan, state, "최고급 바닐라 향초", "purchase", true);
            Require(ProcurementReadiness.GetStatus(plan, state).Percent == 100, "all selected final items achieve 100 without checking lower resources");
            var restored = serializer.Deserialize<ProgressState>(serializer.Serialize(state)); restored.Normalize(catalog);
            Require(ProcurementReadiness.GetStatus(planner.Build(restored), restored).Percent == 100 && !Step(planner.Build(restored), restored, "실리엔", "craft").IsReady, "reload recomputes full coverage from only explicit saved roots");
            restored.ResetChecks(); Require(ProcurementReadiness.GetStatus(planner.Build(restored), restored).Percent == 0m, "reset removes explicit and all derived progress");

            // A direct exchange need reserves part of a shared item's total.
            state = SelectOne(catalog, "C31", "C44", "K38");
            foreach (string name in new[] { "실리엔", "에너지 컨버터", "에너지 증폭 장치", "정화된 토끼의 발" }) Craft(planner, state, name);
            plan = planner.Build(state); Set(plan, state, "에너지 증폭 장치", "craft", true);
            var converter = Step(plan, state, "에너지 컨버터", "craft"); silien = Step(plan, state, "실리엔", "craft");
            Require(converter.Quantity == 7 && converter.DirectQuantity == 1 && silien.Quantity == 10 && silien.DirectQuantity == 2, "actual intermediate roots retain direct and nested demands");
            Near(converter.CompletionFraction, 6m / 7m, "amplifier cannot cover converter's separate exchange unit");
            Near(silien.CompletionFraction, 0.6m, "fractional converter coverage propagates six Silien uses only");
            Require(!ProcurementReadiness.IsTradeReady(catalog, state, catalog.Trades.Single(t => t.Id == "K38"), plan), "covered upper use cannot satisfy an unprepared direct converter root");
            Set(plan, state, "에너지 컨버터", "craft", true); Set(plan, state, "정화된 토끼의 발", "craft", true);
            Near(Step(plan, state, "실리엔", "craft").CompletionFraction, 0.8m, "all consuming parents still leave two direct Silien exchange units incomplete");
            Require(!ProcurementReadiness.IsItemReady(plan, state, "실리엔"), "partial coverage never makes direct item ready");
            Set(plan, state, "실리엔", "craft", true);
            Require(Step(plan, state, "실리엔 결정", "purchase").IsCovered, "explicit direct shared root finally covers its complete lower demand");

            // A shared synthesis has one separate starter regardless of the
            // number of finished units or rounded synthesis batches.
            state = SelectOne(catalog, "K10", "K52");
            foreach (string name in new[] { "미스릴판", "미스릴 대못", "미스릴괴" }) Craft(planner, state, name);
            plan = planner.Build(state); Set(plan, state, "미스릴판", "craft", true);
            var ingot = Step(plan, state, "미스릴괴", "craft"); var seed = Step(plan, state, "미스릴괴", "purchase");
            Require(ingot.Quantity == 62m && ingot.Node.Batches == 7m && ingot.Node.ProducedQuantity == 64m && seed.Quantity == 1m, "shared synthesis rounds 62 demand once and keeps one starter");
            Near(ingot.CompletionFraction, 1m / 31m, "completed two-unit plate use gives proportional shared ingot credit");
            Near(seed.CompletionFraction, 1m / 31m, "starter receives fractional progress credit rather than full credit from one consuming parent");
            Near(Step(plan, state, "아라트의 결정", "purchase").CompletionFraction, 1m / 31m, "rounded synthesis input demand uses the same fractional progress without a second rounding");
            Require(!seed.IsReady && !seed.IsCovered && !state.ProcurementReady.ContainsKey(seed.Key), "partially covered seed remains an independent unchecked purchase");
            Set(plan, state, "미스릴괴", "purchase", true); Set(plan, state, "미스릴판", "craft", false);
            Require(Step(plan, state, "미스릴괴", "purchase").IsReady && Step(plan, state, "미스릴괴", "craft").CompletionFraction == 0m, "seed snapshot survives parent uncheck without completing ingot production");
            Set(plan, state, "미스릴판", "craft", true); Set(plan, state, "미스릴 대못", "craft", true);
            Require(Step(plan, state, "미스릴괴", "craft").IsCovered && Step(plan, state, "아라트의 결정", "purchase").IsCovered, "all finished shared uses cover synthesis and its inputs");
            ProcurementReadiness.InvalidateChoice(state, "미스릴괴"); planner.SetChoice(state, "미스릴괴", "ore"); plan = planner.Build(state);
            Require(!ProcurementReadiness.GetSteps(plan, state).Any(s => s.IsSeed) && Step(plan, state, "미스릴 광석", "purchase").IsCovered, "recipe switch recalculates coverage over only active ore inputs");
            Set(plan, state, "미스릴판", "craft", false); Set(plan, state, "미스릴 대못", "craft", false);
            Require(Step(plan, state, "미스릴 광석", "purchase").CompletionFraction == 0m, "removing parent checks withdraws new recipe's derived credit");

            // Crossing a batch boundary invalidates a short finished snapshot;
            // raw material completion remains independent even within one batch.
            state = SelectOne(catalog, "C31"); Craft(planner, state, "새우 조련 미끼"); state.Targets["C31"] = 2;
            plan = planner.Build(state); Set(plan, state, "새우 조련 미끼", "craft", true);
            Require(Step(plan, state, "새우", "acquire").Quantity == 1m && Step(plan, state, "새우", "acquire").IsCovered, "eight finished bait units cover one rounded input batch");
            Set(plan, state, "새우", "acquire", true); state.Targets["C31"] = 3; plan = planner.Build(state);
            Require(Step(plan, state, "새우 조련 미끼", "craft").Quantity == 12m && Step(plan, state, "새우", "acquire").Quantity == 2m, "target change crosses the ten-unit batch boundary");
            Require(!Step(plan, state, "새우 조련 미끼", "craft").IsReady && Step(plan, state, "새우", "acquire").CompletionFraction == 0m, "insufficient finished and raw snapshots do not retain stale batch credit");
            state.Targets["C31"] = 1; plan = planner.Build(state);
            Require(Step(plan, state, "새우 조련 미끼", "craft").IsReady && Step(plan, state, "새우", "acquire").IsReady, "decreasing need reuses sufficient independently stored quantities");
            ProcurementReadiness.InvalidateChoice(state, "새우 조련 미끼"); planner.SetChoice(state, "새우 조련 미끼", "buy"); plan = planner.Build(state);
            Require(Step(plan, state, "새우 조련 미끼", "purchase").CompletionFraction == 0m && !ProcurementReadiness.GetSteps(plan, state).Any(s => s.Name == "새우"), "buy mode removes the old crafting branch and its derived progress");
            Require(state.ProcurementReady.ContainsKey("acquire:새우"), "removing a branch preserves independent lower snapshots for later reuse");
        }
    }
}
