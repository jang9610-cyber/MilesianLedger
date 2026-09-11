using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    public sealed class Catalog
    {
        public string Version { get; set; }
        public List<string> Notes { get; set; }
        public List<TradeItem> Trades { get; set; }

        public Catalog()
        {
            Notes = new List<string>();
            Trades = new List<TradeItem>();
        }

        public static Catalog Load(string path)
        {
            return FromJson(File.ReadAllText(path, Encoding.UTF8));
        }

        public static Catalog FromJson(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            Catalog catalog = serializer.Deserialize<Catalog>(json);
            if (catalog == null || catalog.Trades == null || catalog.Trades.Count == 0)
                throw new InvalidDataException("교역 데이터가 비어 있습니다.");
            var allIds = new HashSet<string>();
            foreach (TradeItem trade in catalog.Trades)
            {
                if (String.IsNullOrWhiteSpace(trade.Id) || !allIds.Add(trade.Id) || trade.Limit < 0 || trade.Groups == null)
                    throw new InvalidDataException("교역품 데이터가 올바르지 않습니다.");
                foreach (MaterialGroup group in trade.Groups)
                {
                    if (String.IsNullOrWhiteSpace(group.Id) || !allIds.Add(group.Id) || group.PerTrade < 0 || group.Lines == null)
                        throw new InvalidDataException("재료 데이터가 올바르지 않습니다.");
                    foreach (MaterialLine line in group.Lines)
                    {
                        if (String.IsNullOrWhiteSpace(line.Id) || !allIds.Add(line.Id) || String.IsNullOrWhiteSpace(line.CheckId) || line.PerTrade < 0)
                            throw new InvalidDataException("밑재료 데이터가 올바르지 않습니다.");
                        if (line.ChildCheckIds == null) line.ChildCheckIds = new List<string>();
                        if (line.Components == null) line.Components = new List<string> { line.Name };
                        if (line.BatchSize < 0 || line.BatchDemandPerTrade < 0 || line.BatchInputPerBatch < 0)
                            throw new InvalidDataException("제작 단위가 올바르지 않습니다.");
                    }
                }
            }
            return catalog;
        }
    }

    public sealed class TradeItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Station { get; set; }
        public int Tier { get; set; }
        public int Limit { get; set; }
        public int DefaultQuantity { get; set; }
        public string SourceCell { get; set; }
        public List<MaterialGroup> Groups { get; set; }
        public TradeItem() { Groups = new List<MaterialGroup>(); }
    }

    public sealed class MaterialGroup
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public decimal PerTrade { get; set; }
        public decimal OriginalQuantity { get; set; }
        public string SourceCell { get; set; }
        public string Formula { get; set; }
        public string Note { get; set; }
        public decimal OutputPerBatch { get; set; }
        public bool IsPurchased { get; set; }
        public List<MaterialLine> Lines { get; set; }
        public MaterialGroup() { Lines = new List<MaterialLine>(); }
    }

    public sealed class MaterialLine
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string SourceName { get; set; }
        public decimal PerTrade { get; set; }
        public string AlternateName { get; set; }
        public decimal AlternatePerTrade { get; set; }
        public decimal OriginalQuantity { get; set; }
        public decimal OriginalAlternateQuantity { get; set; }
        public int Depth { get; set; }
        public string ParentId { get; set; }
        public string CheckId { get; set; }
        public string LegacyCheckId { get; set; }
        public List<string> ChildCheckIds { get; set; }
        public List<string> Components { get; set; }
        public string SourceCell { get; set; }
        public string Formula { get; set; }
        public string CheckFormula { get; set; }
        public string Note { get; set; }
        public decimal BatchSize { get; set; }
        public decimal BatchDemandPerTrade { get; set; }
        public decimal BatchInputPerBatch { get; set; }
        public MaterialLine()
        {
            ChildCheckIds = new List<string>();
            Components = new List<string>();
        }
    }

    public sealed class MaterialTotal
    {
        public string Name { get; set; }
        public decimal Quantity { get; set; }
        public string AlternateName { get; set; }
        public decimal AlternateQuantity { get; set; }
        public List<string> Uses { get; set; }
        public List<string> CraftingNotes { get; set; }
        public bool PurchaseRecommended { get; set; }
        public MaterialTotal() { Uses = new List<string>(); CraftingNotes = new List<string>(); }
    }

    public sealed class TradePlanPreset
    {
        public string Name { get; set; }
        public Dictionary<string, int> Quantities { get; set; }
    }

    public sealed class ProgressState
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, int> Targets { get; set; }
        public Dictionary<string, bool> Selected { get; set; }
        public Dictionary<string, bool> Checks { get; set; }
        public Dictionary<string, string> ProcurementChoices { get; set; }
        public Dictionary<string, ProcurementReadySnapshot> ProcurementReady { get; set; }
        public TradePlanningSettings TradeSettings { get; set; }
        public List<TradePlanPreset> TradePresets { get; set; }
        public List<ProcurementMethodPreset> ProcurementMethodPresets { get; set; }
        public int ActiveProcurementPresetSlot { get; set; }

        public ProgressState()
        {
            Targets = new Dictionary<string, int>();
            Checks = new Dictionary<string, bool>();
            ProcurementChoices = new Dictionary<string, string>();
            ProcurementReady = new Dictionary<string, ProcurementReadySnapshot>();
            TradeSettings = new TradePlanningSettings();
            TradePresets = new List<TradePlanPreset>();
        }

        public void Normalize(Catalog catalog)
        {
            if (Targets == null) Targets = new Dictionary<string, int>();
            if (Checks == null) Checks = new Dictionary<string, bool>();
            if (TradeSettings == null) TradeSettings = new TradePlanningSettings();
            if (TradePresets == null) TradePresets = new List<TradePlanPreset>();
            TradePresets = TradePresets.Where(p => p != null && !String.IsNullOrWhiteSpace(p.Name) && p.Quantities != null)
                .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            foreach (var preset in TradePresets)
            {
                preset.Name = preset.Name.Trim();
                preset.Quantities = catalog.Trades.ToDictionary(t => t.Id, t => preset.Quantities.ContainsKey(t.Id) ? Math.Max(0, Math.Min(t.Limit, preset.Quantities[t.Id])) : 0);
            }
            if (ProcurementChoices == null) ProcurementChoices = new Dictionary<string, string>();
            ProcurementChoices = ProcurementChoices.Where(pair => !String.IsNullOrWhiteSpace(pair.Key) && !String.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (ProcurementReady == null) ProcurementReady = new Dictionary<string, ProcurementReadySnapshot>();
            ProcurementReady = ProcurementReady.Where(pair => !String.IsNullOrWhiteSpace(pair.Key) && pair.Value != null
                && pair.Value.Quantity > 0 && !String.IsNullOrWhiteSpace(pair.Value.Context))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            // Boards are now purchase-only. Existing completed boards remain
            // available; alternative preparation records describe the same stock.
            ProcurementChoices.Remove("나무판");
            var boardRecords = ProcurementReady.Where(pair => pair.Value.Name == "나무판"
                && (pair.Value.Kind == "purchase" || pair.Value.Kind == "craft" || pair.Value.Kind == "acquire")).ToList();
            if (boardRecords.Count > 0)
            {
                decimal boardQuantity = boardRecords.Max(pair => pair.Value.Quantity);
                foreach (var pair in boardRecords) ProcurementReady.Remove(pair.Key);
                ProcurementReady["purchase:나무판"] = new ProcurementReadySnapshot { Name = "나무판", Kind = "purchase", Quantity = boardQuantity, Context = "purchase" };
            }
            foreach (string npcName in ProcurementPlanner.NpcPurchaseNames)
            {
                ProcurementChoices.Remove(npcName);
                var records = ProcurementReady.Where(p => p.Value.Name == npcName && (p.Value.Kind == "purchase" || p.Value.Kind == "acquire")).ToList();
                if (records.Count == 0) continue;
                decimal prepared = records.Max(p => p.Value.Quantity);
                foreach (var record in records) ProcurementReady.Remove(record.Key);
                ProcurementReady["acquire:" + npcName] = new ProcurementReadySnapshot { Name = npcName, Kind = "acquire", Quantity = prepared, Context = "acquire" };
            }
            // Null distinguishes the original state format, which had no
            // selection field, from a current plan with no selected goods.
            bool migrateLegacySelection = SchemaVersion < 2 && Selected == null && Targets.Count > 0;
            var normalizedSelection = new Dictionary<string, bool>();
            var normalized = new Dictionary<string, int>();
            var validChecks = new HashSet<string>();
            foreach (TradeItem trade in catalog.Trades)
            {
                int target;
                if (!Targets.TryGetValue(trade.Id, out target)) target = trade.DefaultQuantity;
                normalized[trade.Id] = Math.Max(0, Math.Min(trade.Limit, target));
                bool selected;
                normalizedSelection[trade.Id] = Selected != null && Selected.TryGetValue(trade.Id, out selected)
                    ? selected : migrateLegacySelection && Targets.ContainsKey(trade.Id) && normalized[trade.Id] > 0;
                foreach (MaterialLine line in trade.Groups.SelectMany(g => g.Lines))
                {
                    validChecks.Add(line.CheckId);
                    foreach (string child in line.ChildCheckIds) validChecks.Add(child);
                    bool legacyChecked;
                    if (!Checks.ContainsKey(line.CheckId) && !String.IsNullOrEmpty(line.LegacyCheckId)
                        && Checks.TryGetValue(line.LegacyCheckId, out legacyChecked))
                        Checks[line.CheckId] = legacyChecked;
                }
            }
            Targets = normalized;
            Selected = normalizedSelection;
            Checks = Checks.Where(pair => validChecks.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            ProcurementPresets.Normalize(this);
            SchemaVersion = 2;
        }

        public void ResetChecks()
        {
            if (Checks == null) Checks = new Dictionary<string, bool>();
            else Checks.Clear();
            if (ProcurementReady == null) ProcurementReady = new Dictionary<string, ProcurementReadySnapshot>();
            else ProcurementReady.Clear();
        }
    }

    public static class Calculator
    {
        public static bool IsSelected(ProgressState state, TradeItem trade)
        {
            bool selected;
            return state != null && state.Selected != null && state.Selected.TryGetValue(trade.Id, out selected) && selected;
        }

        public static void SetSelected(ProgressState state, TradeItem trade, bool selected)
        {
            bool wasSelected = IsSelected(state, trade);
            if (state.Selected == null) state.Selected = new Dictionary<string, bool>();
            state.Selected[trade.Id] = selected;
            // Removing a use leaves already prepared shared material sufficient;
            // adding one increases demand and requires a fresh shared check.
            if (selected && !wasSelected && Target(state, trade) > 0
                && trade.Groups.SelectMany(group => group.Lines).Any(line => line.CheckId == "T41"))
            {
                if (state.Checks == null) state.Checks = new Dictionary<string, bool>();
                state.Checks["T41"] = false;
            }
        }

        public static int EffectiveTarget(ProgressState state, TradeItem trade)
        {
            return IsSelected(state, trade) ? Target(state, trade) : 0;
        }

        public static int Target(ProgressState state, TradeItem trade)
        {
            int value;
            if (state == null || state.Targets == null || !state.Targets.TryGetValue(trade.Id, out value))
                value = trade.DefaultQuantity;
            return Math.Max(0, Math.Min(trade.Limit, value));
        }

        public static decimal Quantity(MaterialLine line, int target)
        {
            target = Math.Max(0, target);
            if (line.BatchSize > 0)
                return Math.Ceiling(target * line.BatchDemandPerTrade / line.BatchSize) * line.BatchInputPerBatch;
            return line.PerTrade * target;
        }

        public static decimal AlternateQuantity(MaterialLine line, int target)
        {
            return line.AlternatePerTrade * Math.Max(0, target);
        }

        public static decimal GroupQuantity(MaterialGroup group, int target)
        {
            return group.PerTrade * Math.Max(0, target);
        }

        public static decimal BatchCount(MaterialGroup group, int target)
        {
            return group.OutputPerBatch > 1 ? Math.Ceiling(GroupQuantity(group, target) / group.OutputPerBatch) : GroupQuantity(group, target);
        }

        public static decimal Surplus(MaterialGroup group, int target)
        {
            return group.OutputPerBatch > 1 ? BatchCount(group, target) * group.OutputPerBatch - GroupQuantity(group, target) : 0;
        }

        private static bool Check(ProgressState state, string checkId)
        {
            bool value;
            return state != null && state.Checks != null && state.Checks.TryGetValue(checkId, out value) && value;
        }

        public static bool IsChecked(ProgressState state, MaterialLine line)
        {
            // Calculated parent checks depend on their children;
            // saved stale values for H48, H52 and P45 never override the formula.
            if (line.ChildCheckIds != null && line.ChildCheckIds.Count > 0)
                return line.ChildCheckIds.All(id => Check(state, id));
            return Check(state, line.CheckId);
        }

        public static void SetChecked(ProgressState state, MaterialLine line, bool value)
        {
            if (state.Checks == null) state.Checks = new Dictionary<string, bool>();
            if (line.ChildCheckIds != null && line.ChildCheckIds.Count > 0)
            {
                foreach (string child in line.ChildCheckIds) state.Checks[child] = value;
                state.Checks.Remove(line.CheckId);
            }
            else state.Checks[line.CheckId] = value;
        }

        public static bool IsGroupReady(ProgressState state, MaterialGroup group)
        {
            return group.Lines != null && group.Lines.Count > 0 && group.Lines.All(line => IsChecked(state, line));
        }

        public static bool IsTradeReady(ProgressState state, TradeItem trade)
        {
            return EffectiveTarget(state, trade) > 0 && trade.Groups != null && trade.Groups.Count > 0 && trade.Groups.All(group => IsGroupReady(state, group));
        }

        public static decimal SharedSilien(Catalog catalog, ProgressState state)
        {
            return catalog.Trades.Sum(trade => trade.Groups.SelectMany(group => group.Lines)
                .Where(line => line.CheckId == "T41").Sum(line => Quantity(line, EffectiveTarget(state, trade))));
        }

        private static string CanonicalName(string name)
        {
            name = Regex.Replace((name ?? "").Trim(), @"\s+", " ");
            if (name == "돌연변이 식물의 점액") return "돌연변이 식물의 점액질";
            return name;
        }

        public static List<MaterialTotal> Summarize(Catalog catalog, ProgressState state, bool directMaterials)
        {
            var totals = new Dictionary<string, MaterialTotal>(StringComparer.Ordinal);
            foreach (TradeItem trade in catalog.Trades)
            {
                int target = EffectiveTarget(state, trade);
                if (target <= 0) continue;
                string use = trade.Station + " · " + trade.Name;
                foreach (MaterialGroup group in trade.Groups)
                {
                    if (directMaterials)
                        AddTotal(totals, CanonicalName(group.Name), GroupQuantity(group, target), use);
                    else
                    {
                        // The table's Silien exchange recipe already expands it
                        // into crystals, while other recipes stop at Silien.
                        // Keep one preparation stage: finished Silien, with its
                        // crystal equivalent shown only as a crafting alternative.
                        if (CanonicalName(group.Name) == "실리엔")
                        {
                            AddTotal(totals, "실리엔", GroupQuantity(group, target), use);
                            continue;
                        }
                        foreach (MaterialLine line in group.Lines)
                        {
                            if (line.ChildCheckIds != null && line.ChildCheckIds.Count > 0) continue;
                            AddTotal(totals, CanonicalName(line.Name), Quantity(line, target), use);
                        }
                    }
                }
            }
            if (!directMaterials)
            {
                // Reuse only an explicitly recorded, consistent conversion for
                // the same item (e.g. every fine-thread row uses five spider webs).
                // This fills the original F6 row's omitted alternate annotation
                // without adding alternate materials to the required totals.
                var knownAlternates = catalog.Trades.SelectMany(trade => trade.Groups)
                    .SelectMany(group => group.Lines)
                    .Where(line => !String.IsNullOrEmpty(line.AlternateName) && line.PerTrade > 0)
                    .GroupBy(line => CanonicalName(line.Name));
                foreach (var alternatives in knownAlternates)
                {
                    MaterialTotal total;
                    if (!totals.TryGetValue(alternatives.Key, out total)) continue;
                    MaterialLine first = alternatives.First();
                    string alternateName = CanonicalName(first.AlternateName);
                    decimal ratio = first.AlternatePerTrade / first.PerTrade;
                    if (!alternatives.All(line => CanonicalName(line.AlternateName) == alternateName
                        && line.AlternatePerTrade / line.PerTrade == ratio)) continue;
                    total.AlternateName = alternateName;
                    total.AlternateQuantity = total.Quantity * ratio;
                }
                foreach (var total in totals.Values) AddCraftingDetails(total);
            }
            return totals.Values.OrderBy(total => total.Name, StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), false)).ToList();
        }

        private static void AddCraftingDetails(MaterialTotal total)
        {
            decimal q = total.Quantity;
            if (total.Name == "실리엔")
            {
                total.AlternateName = "실리엔 결정"; total.AlternateQuantity = q * 5;
                total.CraftingNotes.Add("실리엔 1개 = 결정 5개 · 위 완제품을 준비하면 결정은 별도 준비하지 않습니다.");
            }
            if (total.Name == "중급 나무장작" || total.Name == "고급 나무장작" || total.Name == "최고급 나무장작")
            {
                decimal ratio = total.Name == "중급 나무장작" ? 3 : total.Name == "고급 나무장작" ? 9 : 27;
                total.AlternateName = "나무장작"; total.AlternateQuantity = q * ratio;
                if (total.Name == "최고급 나무장작")
                    total.CraftingNotes.Add("고급 " + FormatQuantity(q * 3) + "개 또는 중급 " + FormatQuantity(q * 9) + "개부터 제작 가능 · 각 단계는 대체 관계입니다.");
            }
            string ore = total.Name == "은괴" ? "은광석" : total.Name == "동괴" ? "동광석" : total.Name == "금괴" ? "금광석" : total.Name == "미스릴괴" ? "미스릴 광석" : null;
            if (ore != null)
                total.CraftingNotes.Add("직접 제련: " + ore + " " + FormatQuantity(q * 5) + "개 또는 " + ore + " 조각 " + FormatQuantity(q * 5) + "개 · 둘 중 한 종류로 준비");
            if (total.Name == "힐웬")
                total.CraftingNotes.Add("직접 제작: 힐웬 광석 조각 " + FormatQuantity(q * 5) + "개");
            if (total.Name == "나무판")
            {
                total.PurchaseRecommended = true;
            }
        }

        public static string CraftingSummary(MaterialTotal material)
        {
            if (material.Name == "나무판") return "경매장 구매 전용";
            var notes = new List<string>();
            if (material.PurchaseRecommended) notes.Add("경매장 구매 추천");
            if (!String.IsNullOrEmpty(material.AlternateName)) notes.Add("직접 제작: " + material.AlternateName + " " + FormatQuantity(material.AlternateQuantity) + "개");
            notes.AddRange(material.CraftingNotes);
            return String.Join(" / ", notes);
        }

        private static void AddTotal(Dictionary<string, MaterialTotal> totals, string name, decimal quantity, string use)
        {
            if (quantity <= 0) return;
            MaterialTotal total;
            if (!totals.TryGetValue(name, out total))
            {
                total = new MaterialTotal { Name = name };
                totals.Add(name, total);
            }
            total.Quantity += quantity;
            if (!total.Uses.Contains(use)) total.Uses.Add(use);
        }

        public static string FormatQuantity(decimal quantity)
        {
            return quantity.ToString("#,0.##", CultureInfo.InvariantCulture);
        }
    }
}
