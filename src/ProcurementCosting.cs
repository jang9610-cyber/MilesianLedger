using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MabinogiBarter
{
    public sealed class ProcurementCostLine
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public bool IsSeed { get; set; }
        // Station quantities are cost-allocation equivalents, not separate batches to make.
        public decimal Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal KnownCost { get; set; }
    }

    public sealed class ProcurementCostSummary
    {
        public List<ProcurementCostLine> Lines { get; set; }
        public decimal KnownCost { get { return Lines.Sum(line => line.KnownCost); } }
        public List<string> UnknownNames { get { return Lines.Where(line => line.Quantity > 0 && !line.UnitPrice.HasValue).Select(line => line.Name).Distinct(StringComparer.Ordinal).ToList(); } }
        public int UnknownCount { get { return UnknownNames.Count; } }
        public bool IsComplete { get { return UnknownCount == 0; } }
        public ProcurementCostSummary() { Lines = new List<ProcurementCostLine>(); }
    }

    public sealed class ProcurementStationCost
    {
        public string Station { get; set; }
        public ProcurementCostSummary AllBuy { get; set; }
        public ProcurementCostSummary Planned { get; set; }
        public ProcurementCostSummary Remaining { get; set; }
        public ProcurementStationCost()
        {
            AllBuy = new ProcurementCostSummary(); Planned = new ProcurementCostSummary(); Remaining = new ProcurementCostSummary();
        }
    }

    public sealed class ProcurementCostReport
    {
        public ProcurementCostSummary AllBuy { get; set; }
        public ProcurementCostSummary Planned { get; set; }
        public ProcurementCostSummary Remaining { get; set; }
        public Dictionary<string, ProcurementStationCost> Stations { get; set; }
        public ProcurementCostReport()
        {
            AllBuy = new ProcurementCostSummary(); Planned = new ProcurementCostSummary(); Remaining = new ProcurementCostSummary();
            Stations = new Dictionary<string, ProcurementStationCost>(StringComparer.Ordinal);
        }
    }

    // Offline costing. The supplied delegate must read an already cached unit price.
    // No state, recipes, readiness records, or caller-owned plans are modified.
    public sealed class ProcurementCosting
    {
        public const string AllocationNote = "공유 재료·제작 묶음·시작 괴는 전체 선택에서 한 번 계산합니다. 여분 생산 비용도 해당 재료의 교역소별 수요 비율로 배분하며, 교역소 금액의 합계는 전체 금액과 같습니다.";
        public const string RemainingNote = "남은 지출은 구비 기록을 기준으로 한 추정입니다. 상위 완료품에 사용한 몫을 하위 구비량에서 먼저 공제하며, 기록되지 않은 제작 여분은 보유량으로 인정하지 않습니다. 완료 몫과 남은 몫의 제작 묶음은 각각 올림합니다. 직접 확보·NPC 구매는 0원으로 계산합니다.";

        readonly Catalog catalog;
        readonly ProcurementPlanner planner;
        public ProcurementCosting(Catalog catalog, ProcurementPlanner planner)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            if (planner == null) throw new ArgumentNullException("planner");
            this.catalog = catalog; this.planner = planner;
        }

        sealed class FlowNode
        {
            public string Name;
            public ProcurementRecipe Recipe;
            public bool IsAcquiring;
            public Dictionary<string, decimal> Demand = new Dictionary<string, decimal>(StringComparer.Ordinal);
            public decimal Consumed;
        }

        public ProcurementCostReport Build(ProgressState state, Func<string, decimal?> auctionUnitPrice, ProcurementPlan plan = null)
        {
            if (state == null) throw new ArgumentNullException("state");
            plan = plan ?? planner.Build(state);
            var report = new ProcurementCostReport();
            foreach (string station in catalog.Trades.Select(t => t.Station ?? "").Distinct(StringComparer.Ordinal))
                report.Stations[station] = new ProcurementStationCost { Station = station };
            var direct = DirectDemands(state);
            var prices = new Dictionary<string, decimal?>(StringComparer.Ordinal);
            Func<string, decimal?> cachedPrice = name => {
                decimal? value;
                if (!prices.TryGetValue(name, out value))
                {
                    value = auctionUnitPrice == null ? null : auctionUnitPrice(name);
                    if (value.HasValue && value.Value < 0) value = null;
                    prices.Add(name, value);
                }
                return value;
            };

            // Default choices include forced NPC acquisition, while ordinary roots
            // are compared as complete-item auction purchases regardless of mode.
            var defaultState = new ProgressState();
            foreach (var item in direct.OrderBy(p => p.Key, StringComparer.Ordinal))
                AddCost(report, "all", item.Key, planner.GetChoice(defaultState, item.Key) == "acquire" ? "acquire" : "purchase", false, item.Value, cachedPrice);

            Calculate(report, "planned", state, plan, direct, false, cachedPrice);
            Calculate(report, "remaining", state, plan, direct, true, cachedPrice);
            return report;
        }

        Dictionary<string, Dictionary<string, decimal>> DirectDemands(ProgressState state)
        {
            var result = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);
            foreach (var trade in catalog.Trades)
            {
                int target = Calculator.EffectiveTarget(state, trade);
                if (target <= 0) continue;
                foreach (var group in trade.Groups)
                {
                    decimal quantity = Calculator.GroupQuantity(group, target);
                    if (quantity <= 0) continue;
                    string name = CanonicalName(group.Name);
                    Dictionary<string, decimal> shares;
                    if (!result.TryGetValue(name, out shares)) { shares = new Dictionary<string, decimal>(StringComparer.Ordinal); result.Add(name, shares); }
                    Add(shares, trade.Station ?? "", quantity);
                }
            }
            return result;
        }

        void Calculate(ProcurementCostReport report, string section, ProgressState state, ProcurementPlan plan,
            Dictionary<string, Dictionary<string, decimal>> direct, bool useInventory, Func<string, decimal?> price)
        {
            var nodes = new Dictionary<string, FlowNode>(StringComparer.Ordinal);
            foreach (var item in direct)
            {
                var node = EnsureNode(state, item.Key, nodes, new HashSet<string>(StringComparer.Ordinal));
                foreach (var share in item.Value) Add(node.Demand, share.Key, share.Value);
            }
            // Include recipe edges whose original batch demand was zero. A saved
            // larger completed quantity can still have consumed one of these inputs.
            var incoming = nodes.Values.ToDictionary(node => node.Name, node => 0, StringComparer.Ordinal);
            foreach (var node in nodes.Values.Where(n => n.Recipe != null))
                foreach (var input in node.Recipe.Ingredients.Where(i => !i.IsSeed)) incoming[input.Name]++;
            var queue = new Queue<FlowNode>(nodes.Values.Where(n => incoming[n.Name] == 0).OrderBy(n => n.Name, StringComparer.Ordinal));
            var steps = useInventory ? ProcurementReadiness.GetSteps(plan, state).ToDictionary(s => s.Key, StringComparer.Ordinal)
                : new Dictionary<string, ProcurementStep>(StringComparer.Ordinal);
            int processed = 0;
            while (queue.Count > 0)
            {
                var node = queue.Dequeue(); processed++;
                string kind = node.Recipe != null ? "craft" : node.IsAcquiring ? "acquire" : "purchase";
                decimal demand = node.Demand.Values.Sum();
                decimal recorded = useInventory ? RecordedQuantity(state, steps, kind, node.Name) : 0;
                // A preparation checkbox records a total prepared quantity. It is
                // not an additional stock on top of finished products already checked.
                decimal available = Math.Max(0, recorded - node.Consumed);
                decimal remaining = Math.Max(0, demand - available);
                var remainingShares = Allocate(remaining, node.Demand);
                if (node.Recipe == null)
                {
                    AddCost(report, section, node.Name, kind, false, remainingShares, price);
                    continue;
                }

                // max, not sum: the lower completed record already includes the
                // quantity consumed by upper completed records. This also handles
                // valid snapshots retained after the target quantity decreases.
                decimal prepared = useInventory ? Math.Max(recorded, node.Consumed) : 0;
                var consumedInputs = Ingredients(node.Recipe, prepared, false);
                // A remaining completed ingot can be borrowed as the starting seed;
                // successful synthesis returns it, so it is not spent twice.
                bool borrowSeed = node.Recipe.IsReproduction && available >= 1 && remaining > 0;
                var requiredInputs = Ingredients(node.Recipe, remaining, borrowSeed);
                foreach (var input in node.Recipe.Ingredients)
                {
                    string edge = EdgeKey(input);
                    decimal required = requiredInputs[edge];
                    decimal consumed = consumedInputs[edge];
                    if (input.IsSeed)
                    {
                        decimal seedRecorded = useInventory ? RecordedQuantity(state, steps, "purchase", input.Name) : 0;
                        decimal seedAvailable = Math.Max(0, seedRecorded - consumed);
                        decimal buySeed = Math.Max(0, required - seedAvailable);
                        AddCost(report, section, input.Name, "purchase", true, Allocate(buySeed, remainingShares), price);
                    }
                    else
                    {
                        var child = nodes[input.Name];
                        foreach (var share in Allocate(required, remainingShares)) Add(child.Demand, share.Key, share.Value);
                        child.Consumed += consumed;
                        if (--incoming[input.Name] == 0) queue.Enqueue(child);
                    }
                }
            }
            if (processed != nodes.Count) throw new InvalidOperationException("비용 계산의 제작 재료에 순환 참조가 있습니다.");
        }

        FlowNode EnsureNode(ProgressState state, string name, Dictionary<string, FlowNode> nodes, HashSet<string> path)
        {
            if (path.Contains(name)) throw new InvalidOperationException("비용 계산의 제작 재료가 자기 자신을 참조합니다: " + name);
            FlowNode node;
            if (nodes.TryGetValue(name, out node)) return node;
            string choice = planner.GetChoice(state, name);
            node = new FlowNode { Name = name, IsAcquiring = choice == "acquire", Recipe = planner.GetRecipes(name).FirstOrDefault(r => r.Id == choice) };
            nodes.Add(name, node);
            if (node.Recipe != null)
            {
                path.Add(name);
                foreach (var input in node.Recipe.Ingredients.Where(i => !i.IsSeed)) EnsureNode(state, input.Name, nodes, path);
                path.Remove(name);
            }
            return node;
        }

        static decimal RecordedQuantity(ProgressState state, Dictionary<string, ProcurementStep> steps, string kind, string name)
        {
            string key = kind + ":" + name;
            ProcurementStep step;
            ProcurementReadySnapshot snapshot;
            if (state.ProcurementReady == null || !steps.TryGetValue(key, out step) || !state.ProcurementReady.TryGetValue(key, out snapshot)
                || snapshot == null || snapshot.Name != name || snapshot.Kind != kind || snapshot.Context != step.Context) return 0;
            return Math.Max(0, snapshot.Quantity);
        }

        static string EdgeKey(ProcurementIngredient input) { return (input.IsSeed ? "seed:" : "input:") + input.Name; }

        static Dictionary<string, decimal> Ingredients(ProcurementRecipe recipe, decimal quantity, bool borrowedSeed)
        {
            decimal batches = 0;
            if (quantity > 0)
                batches = recipe.IsReproduction
                    ? Math.Ceiling(Math.Max(0, quantity - (borrowedSeed ? 0 : 1)) / (recipe.OutputPerBatch - 1))
                    : Math.Ceiling(quantity / recipe.OutputPerBatch);
            return recipe.Ingredients.ToDictionary(EdgeKey, input => quantity <= 0 ? 0
                : input.IsSeed ? (borrowedSeed ? 0 : 1) : input.Quantity * batches, StringComparer.Ordinal);
        }

        static Dictionary<string, decimal> Allocate(decimal quantity, Dictionary<string, decimal> weights)
        {
            var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var positive = weights.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            decimal total = positive.Sum(p => p.Value);
            if (quantity <= 0 || total <= 0) return result;
            decimal assigned = 0;
            for (int i = 0; i < positive.Count; i++)
            {
                decimal share = i == positive.Count - 1 ? quantity - assigned : quantity * positive[i].Value / total;
                if (share > 0) result.Add(positive[i].Key, share);
                assigned += share;
            }
            return result;
        }

        static void Add(Dictionary<string, decimal> destination, string name, decimal quantity)
        {
            if (quantity <= 0) return;
            decimal existing; destination.TryGetValue(name, out existing); destination[name] = existing + quantity;
        }

        static ProcurementCostSummary Summary(ProcurementCostReport report, string section)
        {
            return section == "all" ? report.AllBuy : section == "planned" ? report.Planned : report.Remaining;
        }

        static ProcurementCostSummary Summary(ProcurementStationCost station, string section)
        {
            return section == "all" ? station.AllBuy : section == "planned" ? station.Planned : station.Remaining;
        }

        static void AddCost(ProcurementCostReport report, string section, string name, string kind, bool seed,
            Dictionary<string, decimal> shares, Func<string, decimal?> price)
        {
            decimal quantity = shares.Values.Sum();
            if (quantity <= 0) return;
            decimal? unitPrice = kind == "acquire" ? (decimal?)0 : price(name);
            decimal cost = unitPrice.HasValue ? quantity * unitPrice.Value : 0;
            Summary(report, section).Lines.Add(new ProcurementCostLine { Name = name, Kind = kind, IsSeed = seed, Quantity = quantity, UnitPrice = unitPrice, KnownCost = cost });
            var amounts = Allocate(cost, shares);
            foreach (var share in shares)
            {
                decimal amount; amounts.TryGetValue(share.Key, out amount);
                Summary(report.Stations[share.Key], section).Lines.Add(new ProcurementCostLine {
                    Name = name, Kind = kind, IsSeed = seed, Quantity = share.Value, UnitPrice = unitPrice, KnownCost = amount });
            }
        }

        static string CanonicalName(string name)
        {
            name = Regex.Replace((name ?? "").Trim(), @"\s+", " ");
            return name == "돌연변이 식물의 점액" ? "돌연변이 식물의 점액질" : name;
        }
    }
}
