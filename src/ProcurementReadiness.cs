using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MabinogiBarter
{
    public sealed class ProcurementReadySnapshot
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public decimal Quantity { get; set; }
        public string Context { get; set; }
    }

    public sealed class ProcurementStep
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public decimal Quantity { get; set; }
        public string RecipeId { get; set; }
        public string Context { get; set; }
        public bool IsReady { get; set; }
        // Progress credit from completed upper uses, not an inventory quantity
        // or an automatically checked/persisted lower preparation step.
        public decimal CoveredFraction { get; internal set; }
        public decimal CompletionFraction { get { return IsReady ? 1m : CoveredFraction; } }
        public bool IsCovered { get { return !IsReady && CoveredFraction == 1m; } }
        public decimal PartialCoveredFraction { get { return CoveredFraction > 0m && CoveredFraction < 1m ? CoveredFraction : 0m; } }
        public int Level { get; set; }
        public bool IsFinal { get; set; }
        public bool HasExchangeUse { get; set; }
        public decimal DirectQuantity { get; set; }
        public bool IsSeed { get; set; }
        public string GroupLabel { get; set; }
        public string GroupKey { get; set; }
        public ProcurementNode Node { get; set; }
        public List<string> Uses { get; set; }
        public List<string> DependencyKeys { get; set; }
        public ProcurementStep() { Uses = new List<string>(); DependencyKeys = new List<string>(); }
    }

    public sealed class ProcurementReadyStatus
    {
        public int Ready { get; set; }
        public int Total { get; set; }
        public decimal Percent { get; set; }
        public int ExplicitReady { get; set; }
        public int CoveredReady { get; set; }
        public int PurchaseReady { get; set; }
        public int PurchaseTotal { get; set; }
        public int CraftReady { get; set; }
        public int CraftTotal { get; set; }
        public int AcquireReady { get; set; }
        public int AcquireTotal { get; set; }
    }

    public static class ProcurementReadiness
    {
        public static List<ProcurementStep> GetSteps(ProcurementPlan plan, ProgressState state)
        {
            if (plan == null) return new List<ProcurementStep>();
            var steps = new Dictionary<string, ProcurementStep>(StringComparer.Ordinal);
            var usedByCraft = new HashSet<string>(plan.Crafts.SelectMany(n => n.Children).Where(c => !c.IsSeed).Select(c => c.Name), StringComparer.Ordinal);
            foreach (var row in plan.Purchases)
            {
                var seed = plan.Crafts.Where(n => n.Name == row.Name).SelectMany(n => n.Children).FirstOrDefault(c => c.IsSeed && c.Name == row.Name);
                var node = seed != null ? seed.Node : plan.Nodes.FirstOrDefault(n => n.Name == row.Name);
                AddStep(steps, row.Name, "purchase", row.Quantity, node, row.Uses, seed != null, usedByCraft);
            }
            foreach (var row in plan.Acquisitions)
                AddStep(steps, row.Name, "acquire", row.Quantity, plan.Nodes.FirstOrDefault(n => n.Name == row.Name), row.Uses, false, usedByCraft);
            foreach (var node in plan.Crafts)
                AddStep(steps, node.Name, "craft", node.Quantity, node, node.Uses, false, usedByCraft);

            foreach (var step in steps.Values.Where(s => s.Kind == "craft"))
                foreach (var child in step.Node.Children)
                {
                    string key = ChildKey(child);
                    if (steps.ContainsKey(key) && !step.DependencyKeys.Contains(key)) step.DependencyKeys.Add(key);
                }
            var levels = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var step in steps.Values)
            {
                step.Level = GetLevel(step, steps, levels, new HashSet<string>(StringComparer.Ordinal));
                step.GroupKey = step.IsFinal ? "final" : step.Kind == "craft" ? "middle:" + step.Level : "lower";
                step.GroupLabel = step.IsFinal ? "최종 교환 재료" : step.Kind == "craft" ? "중간 제작품 · " + step.Level + "단계" : "하위 재료";
                step.IsReady = IsReady(state, step);
            }
            ApplyCoverage(steps);
            // Terminal exchange roots can safely appear last; shared exchange
            // roots remain intermediate so Silien precedes energy converters.
            return ItemCategories.OrderSteps(steps.Values);
        }

        static void AddStep(Dictionary<string, ProcurementStep> steps, string name, string kind, decimal quantity,
            ProcurementNode node, IEnumerable<string> uses, bool seed, HashSet<string> usedByCraft)
        {
            if (quantity <= 0) return;
            bool exchange = !seed && node != null && node.DirectQuantity > 0;
            var step = new ProcurementStep { Key = Key(kind, name), Name = name, Kind = kind, Quantity = quantity,
                Node = node, Uses = uses.ToList(), IsSeed = seed, HasExchangeUse = exchange,
                DirectQuantity = exchange ? node.DirectQuantity : 0, IsFinal = exchange && !usedByCraft.Contains(name),
                RecipeId = kind == "craft" && node != null && node.Recipe != null ? node.Recipe.Id : "" };
            step.Context = kind == "craft" ? RecipeContext(node.Recipe) : kind;
            steps.Add(step.Key, step);
        }

        static string Key(string kind, string name) { return kind + ":" + name; }

        static string ChildKey(ProcurementDemand child)
        {
            string kind = child.IsSeed ? "purchase" : child.Node.IsCrafting ? "craft" : child.Node.IsAcquiring ? "acquire" : "purchase";
            return Key(kind, child.Name);
        }

        static void ApplyCoverage(Dictionary<string, ProcurementStep> steps)
        {
            var coveredDemand = new Dictionary<string, decimal>(StringComparer.Ordinal);
            // Every parent has a greater dependency level than its children.
            // Combine all of a shared item's uses before forwarding its credit.
            foreach (var step in steps.Values.OrderByDescending(s => s.Level))
            {
                decimal demand;
                coveredDemand.TryGetValue(step.Key, out demand);
                // Upper uses never fulfill this item's own exchange requirement.
                decimal coverable = Math.Max(0m, step.Quantity - step.DirectQuantity);
                step.CoveredFraction = step.Quantity <= 0 ? 0m : Math.Min(coverable, Math.Max(0m, demand)) / step.Quantity;
                if (step.Kind != "craft" || step.CompletionFraction == 0m) continue;
                foreach (var child in step.Node.Children)
                {
                    string key = ChildKey(child);
                    if (!steps.ContainsKey(key)) continue;
                    decimal previous;
                    coveredDemand.TryGetValue(key, out previous);
                    // Quantities already include global batch rounding. Forward
                    // proportional progress without rounding it as physical stock.
                    // Synthesis seeds use their independent purchase step/edge.
                    coveredDemand[key] = previous + child.Quantity * step.CompletionFraction;
                }
            }
        }

        static string RecipeContext(ProcurementRecipe recipe)
        {
            return "craft|" + recipe.Id + "|" + recipe.OutputPerBatch.ToString(CultureInfo.InvariantCulture) + "|" + (recipe.IsReproduction ? "synthesis" : "batch") + "|"
                + String.Join(";", recipe.Ingredients.OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.IsSeed)
                    .Select(i => i.Name + "=" + i.Quantity.ToString(CultureInfo.InvariantCulture) + (i.IsSeed ? ":seed" : "")));
        }

        static int GetLevel(ProcurementStep step, Dictionary<string, ProcurementStep> steps, Dictionary<string, int> levels, HashSet<string> path)
        {
            int level;
            if (levels.TryGetValue(step.Key, out level)) return level;
            if (!path.Add(step.Key)) throw new InvalidDataException("준비 단계에 순환 참조가 있습니다: " + step.Name);
            level = step.DependencyKeys.Count == 0 ? 0 : step.DependencyKeys.Max(key => GetLevel(steps[key], steps, levels, path)) + 1;
            path.Remove(step.Key); levels[step.Key] = level; return level;
        }

        public static bool IsReady(ProgressState state, ProcurementStep step)
        {
            ProcurementReadySnapshot snapshot;
            return state != null && step != null && state.ProcurementReady != null && state.ProcurementReady.TryGetValue(step.Key, out snapshot)
                && snapshot != null && snapshot.Quantity >= step.Quantity && snapshot.Name == step.Name && snapshot.Kind == step.Kind
                && String.Equals(snapshot.Context, step.Context, StringComparison.Ordinal);
        }

        public static void SetReady(ProgressState state, ProcurementStep step, bool ready)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (step == null) throw new ArgumentNullException("step");
            if (state.ProcurementReady == null) state.ProcurementReady = new Dictionary<string, ProcurementReadySnapshot>();
            if (ready && step.Quantity > 0)
                state.ProcurementReady[step.Key] = new ProcurementReadySnapshot { Name = step.Name, Kind = step.Kind, Quantity = step.Quantity, Context = step.Context };
            else state.ProcurementReady.Remove(step.Key);
            step.IsReady = ready && step.Quantity > 0;
        }

        public static void InvalidateChoice(ProgressState state, string name)
        {
            if (state == null || state.ProcurementReady == null) return;
            foreach (var key in state.ProcurementReady.Where(p => p.Value != null && p.Value.Name == name).Select(p => p.Key).ToList()) state.ProcurementReady.Remove(key);
        }

        public static ProcurementReadyStatus GetStatus(ProcurementPlan plan, ProgressState state)
        {
            return GetStatus(GetSteps(plan, state));
        }

        public static ProcurementReadyStatus GetStatus(IEnumerable<ProcurementStep> currentSteps)
        {
            var steps = currentSteps.ToList();
            int ready = steps.Count(s => s.CompletionFraction == 1m);
            return new ProcurementReadyStatus { Ready = ready, Total = steps.Count, Percent = steps.Count == 0 ? 0 : 100m * steps.Sum(s => s.CompletionFraction) / steps.Count,
                ExplicitReady = steps.Count(s => s.IsReady), CoveredReady = steps.Count(s => s.IsCovered),
                PurchaseReady = steps.Count(s => s.Kind == "purchase" && s.CompletionFraction == 1m), PurchaseTotal = steps.Count(s => s.Kind == "purchase"),
                CraftReady = steps.Count(s => s.Kind == "craft" && s.CompletionFraction == 1m), CraftTotal = steps.Count(s => s.Kind == "craft"),
                AcquireReady = steps.Count(s => s.Kind == "acquire" && s.CompletionFraction == 1m), AcquireTotal = steps.Count(s => s.Kind == "acquire") };
        }

        public static bool IsItemReady(ProcurementPlan plan, ProgressState state, string name)
        {
            var step = GetSteps(plan, state).FirstOrDefault(s => s.Name == name && !s.IsSeed);
            return step != null && step.IsReady;
        }

        public static bool IsTradeReady(Catalog catalog, ProgressState state, TradeItem trade, ProcurementPlan plan)
        {
            return IsTradeReady(state, trade, GetSteps(plan, state));
        }

        public static bool IsTradeReady(ProgressState state, TradeItem trade, IEnumerable<ProcurementStep> currentSteps)
        {
            if (trade == null || Calculator.EffectiveTarget(state, trade) <= 0) return false;
            var steps = currentSteps.Where(s => !s.IsSeed && s.HasExchangeUse).ToDictionary(s => s.Name, StringComparer.Ordinal);
            return trade.Groups.Count > 0 && trade.Groups.All(g => {
                ProcurementStep step; return steps.TryGetValue(g.Name, out step) && step.IsReady;
            });
        }

        // A station's old raw-material checkbox must never manufacture a crafted
        // parent or mark a shared total complete from only one partial use.
        public static bool TrySetFromLegacyLine(ProgressState state, ProcurementPlan plan, MaterialLine line, int target, bool ready)
        {
            if (line == null || target <= 0 || (line.ChildCheckIds != null && line.ChildCheckIds.Count > 0)) return false;
            string name = line.Name == "돌연변이 식물의 점액" ? "돌연변이 식물의 점액질" : line.Name;
            decimal quantity = Calculator.Quantity(line, target);
            var step = GetSteps(plan, state).FirstOrDefault(s => s.Name == name && s.Kind != "craft" && s.Quantity == quantity);
            if (step == null) return false;
            SetReady(state, step, ready); return true;
        }
    }
}
