using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MabinogiBarter
{
    public sealed class ProcurementIngredient
    {
        public string Name { get; set; }
        public decimal Quantity { get; set; }
        public bool IsSeed { get; set; }
    }

    public sealed class ProcurementRecipe
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public decimal OutputPerBatch { get; set; }
        public List<ProcurementIngredient> Ingredients { get; set; }
        public string Note { get; set; }
        public bool IsReproduction { get; set; }
        public ProcurementRecipe() { Ingredients = new List<ProcurementIngredient>(); }
    }

    public sealed class ProcurementDemand
    {
        public string Name { get; set; }
        public decimal Quantity { get; set; }
        public ProcurementNode Node { get; set; }
        public bool IsSeed { get; set; }
    }

    public sealed class ProcurementNode
    {
        public string Name { get; set; }
        // Shared demand from every selected trade and active parent recipe.
        public decimal Quantity { get; set; }
        public decimal DirectQuantity { get; set; }
        public List<string> Uses { get; set; }
        public bool IsCrafting { get; set; }
        public bool IsAcquiring { get; set; }
        public ProcurementRecipe Recipe { get; set; }
        public decimal Batches { get; set; }
        public decimal ProducedQuantity { get; set; }
        public decimal Surplus { get; set; }
        public List<ProcurementDemand> Children { get; set; }
        public ProcurementNode() { Uses = new List<string>(); Children = new List<ProcurementDemand>(); }
    }

    public sealed class ProcurementPlan
    {
        public List<ProcurementNode> Roots { get; set; }
        public List<ProcurementNode> Nodes { get; set; }
        public List<MaterialTotal> Purchases { get; set; }
        public List<ProcurementNode> Crafts { get; set; }
        public List<MaterialTotal> Acquisitions { get; set; }
        public List<string> QuoteNames { get; set; }
        public ProcurementPlan()
        {
            Roots = new List<ProcurementNode>(); Nodes = new List<ProcurementNode>();
            Purchases = new List<MaterialTotal>(); Crafts = new List<ProcurementNode>();
            Acquisitions = new List<MaterialTotal>(); QuoteNames = new List<string>();
        }
    }

    // Pure local arithmetic: building, previewing and changing a plan never requests prices.
    public sealed class ProcurementPlanner
    {
        // The shrimp taming bait recipe's three shop ingredients are fixed NPC
        // purchases. Other materials retain their existing sourcing choices;
        // this is a local accounting policy, not a general vendor classification.
        static readonly HashSet<string> npcPurchaseItems = new HashSet<string>(new[] {
            "새우", "설탕", "마늘"
        }, StringComparer.Ordinal);
        public static string[] NpcPurchaseNames { get { return npcPurchaseItems.OrderBy(n => n, StringComparer.Ordinal).ToArray(); } }
        readonly Catalog catalog;
        readonly Dictionary<string, List<ProcurementRecipe>> recipes = new Dictionary<string, List<ProcurementRecipe>>(StringComparer.Ordinal);
        public ProcurementPlanner(Catalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException("catalog");
            this.catalog = catalog;
            ImportCatalogRecipes();
            AddBaseRecipes();
        }

        public List<ProcurementRecipe> GetRecipes(string name)
        {
            name = CanonicalName(name);
            List<ProcurementRecipe> result;
            return name != null && !IsPurchaseOnly(name) && !IsNpcPurchase(name) && recipes.TryGetValue(name, out result)
                ? new List<ProcurementRecipe>(result) : new List<ProcurementRecipe>();
        }

        public bool CanAcquire(string name) { return !String.IsNullOrWhiteSpace(name) && !IsPurchaseOnly(name) && !IsNpcPurchase(name) && GetRecipes(name).Count == 0; }

        public bool IsNpcPurchase(string name) { return npcPurchaseItems.Contains(CanonicalName(name)); }

        public string[] GetAllQuoteNames()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(catalog.Trades.SelectMany(t => t.Groups).Where(g => g.PerTrade > 0).Select(g => g.Name));
            while (pending.Count > 0)
            {
                string name = CanonicalName(pending.Dequeue());
                if (String.IsNullOrWhiteSpace(name) || IsNpcPurchase(name) || !seen.Add(name)) continue;
                // Include all supported recipe alternatives; visited names also
                // terminate reproduction recipes that use their own ingot.
                foreach (var input in GetRecipes(name).SelectMany(r => r.Ingredients).Where(i => i.Quantity > 0)) pending.Enqueue(input.Name);
            }
            return seen.OrderBy(n => n, StringComparer.CurrentCulture).ToArray();
        }

        public bool IsPurchaseOnly(string name)
        {
            name = CanonicalName(name);
            return name == "나무판" || (name != null && name.IndexOf("500", StringComparison.Ordinal) >= 0 && name.IndexOf("포션", StringComparison.Ordinal) >= 0);
        }

        public string GetChoice(ProgressState state, string name)
        {
            name = CanonicalName(name);
            if (IsNpcPurchase(name)) return "acquire";
            if (IsPurchaseOnly(name)) return "buy";
            string choice;
            if (state == null || state.ProcurementChoices == null || name == null || !state.ProcurementChoices.TryGetValue(name, out choice)) return "buy";
            if (choice == "acquire" && CanAcquire(name)) return choice;
            return GetRecipes(name).Any(r => r.Id == choice) ? choice : "buy";
        }

        public void SetChoice(ProgressState state, string name, string recipeIdOrBuy)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("재료 이름이 필요합니다.");
            name = CanonicalName(name);
            if (state.ProcurementChoices == null) state.ProcurementChoices = new Dictionary<string, string>();
            if (IsNpcPurchase(name)) { state.ProcurementChoices.Remove(name); return; }
            if (recipeIdOrBuy == "buy" || String.IsNullOrEmpty(recipeIdOrBuy)) state.ProcurementChoices.Remove(name);
            else if ((recipeIdOrBuy == "acquire" && CanAcquire(name)) || GetRecipes(name).Any(r => r.Id == recipeIdOrBuy)) state.ProcurementChoices[name] = recipeIdOrBuy;
            else throw new ArgumentException("선택할 수 없는 제작 방법입니다: " + name);
        }

        public ProcurementNode Preview(ProgressState state, string name, decimal quantity, string recipeId = null)
        {
            name = CanonicalName(name);
            var options = GetRecipes(name);
            var selected = options.FirstOrDefault(r => r.Id == (recipeId ?? GetChoice(state, name))) ?? options.FirstOrDefault();
            var node = new ProcurementNode { Name = name, Quantity = Math.Max(0, quantity), DirectQuantity = Math.Max(0, quantity), IsCrafting = selected != null, IsAcquiring = GetChoice(state, name) == "acquire", Recipe = selected };
            CalculateProduction(node);
            if (node.Recipe != null)
                foreach (var input in node.Recipe.Ingredients)
                {
                    decimal needed = IngredientDemand(node, input);
                    if (needed <= 0) continue;
                    node.Children.Add(new ProcurementDemand { Name = input.Name, Quantity = needed, IsSeed = input.IsSeed,
                        Node = new ProcurementNode { Name = input.Name, Quantity = needed, IsAcquiring = IsNpcPurchase(input.Name) } });
                }
            return node;
        }

        public ProcurementPlan Build(ProgressState state)
        {
            var plan = new ProcurementPlan();
            var nodes = new Dictionary<string, ProcurementNode>(StringComparer.Ordinal);
            var roots = Calculator.Summarize(catalog, state, true);
            foreach (var root in roots)
            {
                var node = EnsureNode(state, root.Name, nodes, new HashSet<string>(StringComparer.Ordinal));
                node.Quantity += root.Quantity; node.DirectQuantity += root.Quantity; AddUses(node.Uses, root.Uses);
                plan.Roots.Add(node);
            }

            // Process parents before their ingredients. An ingredient waits for ALL
            // parents (including a direct exchange use), so shared batches round once.
            var incoming = nodes.Values.ToDictionary(n => n.Name, n => 0, StringComparer.Ordinal);
            foreach (var node in nodes.Values)
                foreach (var child in node.Children.Where(c => !c.IsSeed)) incoming[child.Name]++;
            var ready = new Queue<ProcurementNode>(nodes.Values.Where(n => incoming[n.Name] == 0));
            var purchaseTotals = new Dictionary<string, MaterialTotal>(StringComparer.Ordinal);
            var acquisitionTotals = new Dictionary<string, MaterialTotal>(StringComparer.Ordinal);
            int processed = 0;
            while (ready.Count > 0)
            {
                var node = ready.Dequeue(); processed++;
                CalculateProduction(node);
                if (node.Quantity > 0 && !node.IsCrafting)
                    AddTotal(node.IsAcquiring ? acquisitionTotals : purchaseTotals, node.Name, node.Quantity, node.Uses);
                if (node.IsCrafting)
                {
                    foreach (var child in node.Children)
                    {
                        var input = node.Recipe.Ingredients.First(i => i.Name == child.Name && i.IsSeed == child.IsSeed);
                        child.Quantity = IngredientDemand(node, input);
                        if (child.IsSeed)
                        {
                            child.Node.Quantity = child.Quantity; AddUses(child.Node.Uses, node.Uses);
                            AddTotal(purchaseTotals, child.Name, child.Quantity, node.Uses.Concat(new[] { "합성 시작용 괴" }));
                        }
                        else
                        {
                            child.Node.Quantity += child.Quantity; AddUses(child.Node.Uses, node.Uses);
                            if (--incoming[child.Name] == 0) ready.Enqueue(child.Node);
                        }
                    }
                }
            }
            if (processed != nodes.Count) throw new InvalidDataException("제작 재료에 순환 참조가 있습니다.");
            foreach (var node in nodes.Values) node.Children = node.Children.Where(c => c.Quantity > 0).ToList();
            plan.Nodes = nodes.Values.Where(n => n.Quantity > 0).OrderBy(n => n.Name, StringComparer.CurrentCulture).ToList();
            plan.Crafts = plan.Nodes.Where(n => n.IsCrafting).ToList();
            plan.Purchases = purchaseTotals.Values.Where(t => t.Quantity > 0).OrderBy(t => t.Name, StringComparer.CurrentCulture).ToList();
            plan.Acquisitions = acquisitionTotals.Values.Where(t => t.Quantity > 0).OrderBy(t => t.Name, StringComparer.CurrentCulture).ToList();
            plan.QuoteNames = plan.Nodes.Select(n => n.Name).Concat(plan.Purchases.Select(n => n.Name)).Where(n => !IsNpcPurchase(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.CurrentCulture).ToList();
            return plan;
        }

        ProcurementNode EnsureNode(ProgressState state, string name, Dictionary<string, ProcurementNode> nodes, HashSet<string> path)
        {
            if (path.Contains(name)) throw new InvalidDataException("제작 재료가 자기 자신을 참조합니다: " + name);
            ProcurementNode existing;
            if (nodes.TryGetValue(name, out existing)) return existing;
            string choice = GetChoice(state, name);
            var recipe = GetRecipes(name).FirstOrDefault(r => r.Id == choice);
            var node = new ProcurementNode { Name = name, IsCrafting = recipe != null, Recipe = recipe, IsAcquiring = choice == "acquire" };
            nodes.Add(name, node);
            if (recipe != null)
            {
                path.Add(name);
                foreach (var input in recipe.Ingredients)
                    node.Children.Add(new ProcurementDemand { Name = input.Name, IsSeed = input.IsSeed,
                        Node = input.IsSeed ? new ProcurementNode { Name = input.Name } : EnsureNode(state, input.Name, nodes, path) });
                path.Remove(name);
            }
            return node;
        }

        static void CalculateProduction(ProcurementNode node)
        {
            if (!node.IsCrafting || node.Recipe == null || node.Quantity <= 0) return;
            if (node.Recipe.IsReproduction)
            {
                node.Batches = Math.Ceiling(Math.Max(0, node.Quantity - 1) / (node.Recipe.OutputPerBatch - 1));
                node.ProducedQuantity = 1 + node.Batches * (node.Recipe.OutputPerBatch - 1);
            }
            else
            {
                node.Batches = Math.Ceiling(node.Quantity / node.Recipe.OutputPerBatch);
                node.ProducedQuantity = node.Batches * node.Recipe.OutputPerBatch;
            }
            node.Surplus = node.ProducedQuantity - node.Quantity;
        }

        static decimal IngredientDemand(ProcurementNode node, ProcurementIngredient ingredient)
        {
            return node.Quantity <= 0 ? 0 : ingredient.IsSeed ? 1 : node.Batches * ingredient.Quantity;
        }

        static void AddUses(List<string> destination, IEnumerable<string> uses)
        {
            foreach (var use in uses) if (!destination.Contains(use)) destination.Add(use);
        }

        static void AddTotal(Dictionary<string, MaterialTotal> totals, string name, decimal quantity, IEnumerable<string> uses)
        {
            if (quantity <= 0) return;
            MaterialTotal total;
            if (!totals.TryGetValue(name, out total)) { total = new MaterialTotal { Name = name }; totals.Add(name, total); }
            total.Quantity += quantity; AddUses(total.Uses, uses);
        }

        static string CanonicalName(string name)
        {
            name = System.Text.RegularExpressions.Regex.Replace((name ?? "").Trim(), @"\s+", " ");
            return name == "돌연변이 식물의 점액" ? "돌연변이 식물의 점액질" : name;
        }

        void ImportCatalogRecipes()
        {
            foreach (var group in catalog.Trades.SelectMany(t => t.Groups))
            {
                if (!group.IsPurchased && group.PerTrade > 0)
                    ImportRecipe(group.Name, group.PerTrade, group.OutputPerBatch > 0 ? group.OutputPerBatch : 1,
                        group.Lines.Where(l => String.IsNullOrEmpty(l.ParentId)), "제작 성공을 기준으로 필요한 재료를 계산하며, 실패로 인한 추가 소모량은 포함하지 않습니다.");
                foreach (var parent in group.Lines)
                {
                    var children = group.Lines.Where(l => l.ParentId == parent.Id).ToList();
                    if (children.Count > 0 && parent.PerTrade > 0)
                        ImportRecipe(parent.Name, parent.PerTrade, 1, children, "하위 재료의 제작 성공을 기준으로 필요한 수량을 계산합니다.");
                }
            }
        }

        void ImportRecipe(string name, decimal demandPerTrade, decimal output, IEnumerable<MaterialLine> lines, string note)
        {
            if (GetRecipes(name).Count > 0 || IsPurchaseOnly(name)) return;
            var inputs = lines.Where(l => l.PerTrade > 0 && l.Name != name).Select(l => new ProcurementIngredient {
                Name = l.Name, Quantity = l.BatchSize > 0 ? l.BatchInputPerBatch : l.PerTrade / demandPerTrade * output }).ToList();
            if (inputs.Count == 0) return;
            AddRecipe(name, "default", "직접 제작", output, inputs, note, false);
        }

        static ProcurementIngredient Input(string name, decimal quantity) { return new ProcurementIngredient { Name = name, Quantity = quantity }; }

        void AddRecipe(string item, string id, string name, decimal output, IEnumerable<ProcurementIngredient> ingredients, string note, bool replace)
        {
            item = CanonicalName(item);
            var inputs = ingredients.GroupBy(i => new { Name = CanonicalName(i.Name), i.IsSeed }).Select(g => new ProcurementIngredient { Name = g.Key.Name, IsSeed = g.Key.IsSeed, Quantity = g.Sum(i => i.Quantity) }).ToList();
            if (output <= 0 || inputs.Count == 0 || inputs.Any(i => String.IsNullOrWhiteSpace(i.Name) || i.Quantity <= 0)) throw new InvalidDataException("제작 비율이 올바르지 않습니다: " + item);
            List<ProcurementRecipe> options;
            if (!recipes.TryGetValue(item, out options) || replace) { options = new List<ProcurementRecipe>(); recipes[item] = options; }
            options.RemoveAll(r => r.Id == id);
            options.Add(new ProcurementRecipe { Id = id, Name = name, OutputPerBatch = output, Ingredients = inputs, Note = note });
        }

        void AddSimple(string item, string skill, params ProcurementIngredient[] ingredients)
        {
            AddRecipe(item, "default", skill, 1, ingredients, "1개 제작 기준 · 성공 기준이며 실패 소모량은 포함하지 않습니다.", true);
        }

        void AddBaseRecipes()
        {
            // Ratios verified in data/item-acquisition.json. Alternatives are choices,
            // never simultaneous inputs. Preserve the catalog's direct recipe graph.
            AddSimple("가는 실뭉치", "방직", Input("거미줄", 5));
            AddSimple("굵은 실뭉치", "방직", Input("양털", 5));
            AddSimple("중급 나무장작", "목공", Input("나무장작", 3));
            AddSimple("고급 나무장작", "목공", Input("중급 나무장작", 3));
            AddSimple("최고급 나무장작", "목공", Input("고급 나무장작", 3));
            AddSimple("실리엔", "매직 크래프트", Input("실리엔 결정", 5));
            AddSimple("힐웬", "힐웬 공학", Input("힐웬 광석 조각", 5));
            AddSimple("철봉", "블랙스미스", Input("철괴", 1));
            recipes["철봉"][0].Note = "철봉 도면·대장장이 망치·모루가 필요합니다. 성공 기준 철괴 1개로 철봉 1개를 제작하며 실패 소모량은 포함하지 않습니다.";
            AddSimple("육각 볼트", "힐웬 공학", Input("힐웬 광석 조각", 1));
            AddSimple("육각 너트", "힐웬 공학", Input("힐웬 광석 조각", 1));
            AddSimple("포이즌 포션", "포션 조제", Input("베이스 허브", 1), Input("포이즌 허브", 1), Input("물이 든 병", 1));
            foreach (string color in new[] { "파란", "빨간", "은색" })
                AddRecipe("마법가루", "bead-" + color, "핸디크래프트 · " + color + "구슬", 1,
                    new[] { Input("베이스 허브", 1), Input("작은 " + color + "구슬", 1) }, "구슬은 선택한 한 종류만 준비합니다.", false);
            foreach (var leather in new[] { new { Name = "저가형 가죽", Amount = 20m }, new { Name = "일반 가죽", Amount = 10m }, new { Name = "고급 가죽", Amount = 5m }, new { Name = "최고급 가죽", Amount = 2m } })
                AddRecipe("부드러운 양피지", "leather-" + leather.Name, "필기구 크래프트 · " + leather.Name, 1,
                    new[] { Input(leather.Name, leather.Amount) }, "가죽은 선택한 한 종류만 준비합니다.", false);
            foreach (var metal in new[] { new { Ingot = "철괴", Ore = "철광석" }, new { Ingot = "동괴", Ore = "동광석" }, new { Ingot = "은괴", Ore = "은광석" }, new { Ingot = "금괴", Ore = "금광석" }, new { Ingot = "미스릴괴", Ore = "미스릴 광석" } })
            {
                AddRecipe(metal.Ingot, "synthesis", "합성 괴 불리기 · 추천", 10,
                    new[] { new ProcurementIngredient { Name = metal.Ingot, Quantity = 1, IsSeed = true }, Input("아라트의 결정", 1), Input("축복의 포션", 1) },
                    "건식 화덕에서 같은 괴 1 + 아라트의 결정 1 + 축복의 포션 1 → 성공 시 괴 10개. 시작 괴 1개를 준비하고 결과 중 1개를 다음 합성에 재사용합니다. 성공 1회당 9개 순증가이며 실패 소모량은 포함하지 않습니다.", true);
                recipes[metal.Ingot][0].IsReproduction = true;
                AddRecipe(metal.Ingot, "ore", "제련 · 광석", 1, new[] { Input(metal.Ore, 5) }, "광석과 광석 조각 중 선택한 한 종류만 사용합니다. 성공 기준입니다.", false);
                AddRecipe(metal.Ingot, "fragments", "제련 · 광석 조각", 1, new[] { Input(metal.Ore + " 조각", 5) }, "광석과 광석 조각 중 선택한 한 종류만 사용합니다. 성공 기준입니다.", false);
            }
        }
    }
}
