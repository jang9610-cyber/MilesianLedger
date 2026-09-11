using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MabinogiBarter
{
    public sealed class ProcurementChoiceChange
    {
        public string Name { get; set; }
        public string BeforeChoice { get; set; }
        public string AfterChoice { get; set; }
    }

    public sealed class ProcurementSharedSuggestion
    {
        public string AnchorName { get; set; }
        public List<string> RootNames { get; set; }
        public List<string> Paths { get; set; }
        public List<ProcurementChoiceChange> Changes { get; set; }
        public ProcurementSharedSuggestion()
        {
            RootNames = new List<string>(); Paths = new List<string>(); Changes = new List<ProcurementChoiceChange>();
        }
    }

    // Local, non-mutating suggestions for a user's already-applied manual choice.
    // Applying checked suggestions is the only operation that changes state.
    public sealed class ProcurementSharedPlanning
    {
        readonly ProcurementPlanner planner;
        sealed class Occurrence
        {
            public string Root;
            public List<string> Path;
            public string Name { get { return Path[Path.Count - 1]; } }
        }
        public ProcurementSharedPlanning(ProcurementPlanner planner)
        {
            if (planner == null) throw new ArgumentNullException("planner");
            this.planner = planner;
        }

        public List<ProcurementSharedSuggestion> Suggest(ProgressState state, string clickedName, string clickedChoice, string sourceRootName = null)
        {
            clickedName = NormalizeName(clickedName);
            var result = new List<ProcurementSharedSuggestion>();
            if (state == null || String.IsNullOrEmpty(clickedName) || clickedChoice == "buy") return result;
            bool valid = clickedChoice == "acquire" ? planner.CanAcquire(clickedName) : planner.GetRecipes(clickedName).Any(r => r.Id == clickedChoice);
            if (!valid) return result;
            ProcurementPlan plan;
            try { plan = planner.Build(state); } catch (InvalidDataException) { return result; }
            if (plan.Roots.Count == 0) return result;

            var occurrences = new List<Occurrence>();
            foreach (var root in plan.Roots)
                VisitPreview(state, root.Name, root.Name, new List<string>(), new HashSet<string>(StringComparer.Ordinal), occurrences);
            if (!occurrences.Any(o => o.Name == clickedName)) return result;
            var clickedTree = new List<Occurrence>();
            VisitPreview(state, clickedName, clickedName, new List<string>(), new HashSet<string>(StringComparer.Ordinal), clickedTree);
            var depths = clickedTree.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.Min(o => o.Path.Count), StringComparer.Ordinal);
            bool explicitRoot = !String.IsNullOrEmpty(sourceRootName) && plan.Roots.Any(r => r.Name == sourceRootName);

            foreach (var anchor in depths.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.CurrentCulture))
            {
                var paths = occurrences.Where(o => o.Name == anchor.Key).ToList();
                var rootNames = paths.Select(o => o.Root).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.CurrentCulture).ToList();
                if (rootNames.Count < 2 || (explicitRoot && !rootNames.Any(n => n != sourceRootName))) continue;
                var desired = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in paths)
                    foreach (var parent in path.Path.Take(path.Path.Count - 1))
                    {
                        var recipe = PreferredRecipe(state, parent);
                        if (recipe != null) desired[parent] = recipe.Id;
                    }
                PrepareSubtree(state, anchor.Key, desired, new HashSet<string>(StringComparer.Ordinal));
                var changes = desired.Where(p => planner.GetChoice(state, p.Key) != p.Value)
                    .Select(p => new ProcurementChoiceChange { Name = p.Key, BeforeChoice = planner.GetChoice(state, p.Key), AfterChoice = p.Value }).ToList();
                if (changes.Count == 0) continue;
                var suggestion = new ProcurementSharedSuggestion { AnchorName = anchor.Key, RootNames = rootNames,
                    Paths = paths.Select(p => String.Join(" → ", p.Path)).Distinct(StringComparer.Ordinal).ToList(), Changes = changes };
                // A malformed imported cycle must not offer a change which cannot
                // build. The same validation runs atomically when applying a set.
                try { Apply(CopyState(state), new[] { suggestion }); }
                catch (InvalidDataException) { continue; }
                catch (ArgumentException) { continue; }
                result.Add(suggestion);
            }

            // If an ancestor exposes exactly the same affected roots, its direct
            // preparation already includes the descendant. Show the higher anchor.
            return result.Where(candidate => !result.Any(parent => parent != candidate
                && parent.RootNames.SequenceEqual(candidate.RootNames)
                && clickedTree.Any(o => o.Name == candidate.AnchorName && o.Path.Take(o.Path.Count - 1).Contains(parent.AnchorName))))
                .OrderBy(s => depths[s.AnchorName]).ThenBy(s => s.AnchorName, StringComparer.CurrentCulture).ToList();
        }

        public List<ProcurementChoiceChange> Apply(ProgressState state, IEnumerable<ProcurementSharedSuggestion> selected)
        {
            if (state == null) throw new ArgumentNullException("state");
            var desired = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var suggestion in selected ?? Enumerable.Empty<ProcurementSharedSuggestion>())
            {
                if (suggestion == null) continue;
                foreach (var change in suggestion.Changes ?? new List<ProcurementChoiceChange>())
                {
                    if (change == null || String.IsNullOrWhiteSpace(change.Name) || planner.IsNpcPurchase(change.Name)) continue;
                    string previous;
                    if (desired.TryGetValue(change.Name, out previous) && previous != change.AfterChoice)
                        throw new ArgumentException("서로 다른 준비 방식이 제안되었습니다: " + change.Name);
                    bool valid = change.AfterChoice == "buy" || (change.AfterChoice == "acquire" && planner.CanAcquire(change.Name))
                        || planner.GetRecipes(change.Name).Any(r => r.Id == change.AfterChoice);
                    if (!valid) throw new ArgumentException("선택할 수 없는 준비 방식입니다: " + change.Name);
                    desired[change.Name] = change.AfterChoice;
                }
            }
            var changes = desired.Where(p => planner.GetChoice(state, p.Key) != p.Value)
                .Select(p => new ProcurementChoiceChange { Name = p.Key, BeforeChoice = planner.GetChoice(state, p.Key), AfterChoice = p.Value }).ToList();
            if (changes.Count == 0) return changes;
            var validation = CopyState(state);
            foreach (var change in changes) planner.SetChoice(validation, change.Name, change.AfterChoice);
            planner.Build(validation);
            foreach (var change in changes)
            {
                ProcurementReadiness.InvalidateChoice(state, change.Name);
                planner.SetChoice(state, change.Name, change.AfterChoice);
            }
            return changes;
        }

        void VisitPreview(ProgressState state, string root, string name, List<string> parents, HashSet<string> ancestors, List<Occurrence> result)
        {
            if (parents.Count >= 32 || ancestors.Contains(name)) return;
            var path = new List<string>(parents); path.Add(name);
            result.Add(new Occurrence { Root = root, Path = path });
            var recipe = PreferredRecipe(state, name);
            if (recipe == null) return;
            var next = new HashSet<string>(ancestors, StringComparer.Ordinal); next.Add(name);
            foreach (var input in recipe.Ingredients.Where(i => !i.IsSeed)) VisitPreview(state, root, input.Name, path, next, result);
        }

        void PrepareSubtree(ProgressState state, string name, Dictionary<string, string> desired, HashSet<string> ancestors)
        {
            if (ancestors.Count >= 32 || !ancestors.Add(name)) return;
            var recipe = PreferredRecipe(state, name);
            if (recipe == null)
            {
                if (planner.CanAcquire(name)) desired[name] = "acquire";
            }
            else
            {
                desired[name] = recipe.Id;
                foreach (var input in recipe.Ingredients.Where(i => !i.IsSeed)) PrepareSubtree(state, input.Name, desired, ancestors);
            }
            ancestors.Remove(name);
        }

        ProcurementRecipe PreferredRecipe(ProgressState state, string name)
        {
            var options = planner.GetRecipes(name);
            return options.FirstOrDefault(r => r.Id == planner.GetChoice(state, name)) ?? options.FirstOrDefault();
        }

        static string NormalizeName(string name)
        {
            name = System.Text.RegularExpressions.Regex.Replace((name ?? "").Trim(), @"\s+", " ");
            return name == "돌연변이 식물의 점액" ? "돌연변이 식물의 점액질" : name;
        }

        static ProgressState CopyState(ProgressState state)
        {
            return new ProgressState { SchemaVersion = state.SchemaVersion,
                Targets = state.Targets == null ? null : new Dictionary<string, int>(state.Targets),
                Selected = state.Selected == null ? null : new Dictionary<string, bool>(state.Selected),
                Checks = state.Checks == null ? null : new Dictionary<string, bool>(state.Checks),
                ProcurementChoices = state.ProcurementChoices == null ? null : new Dictionary<string, string>(state.ProcurementChoices),
                ProcurementReady = state.ProcurementReady == null ? null : new Dictionary<string, ProcurementReadySnapshot>(state.ProcurementReady) };
        }
    }
}
