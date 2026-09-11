using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        public string RunProcurementChecklistUiChecks(Func<int> requests, string directory)
        {
            // No credential access or price refresh is allowed in this bounded UI regression.
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)),
                Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Checklist checks require an offline service and a temporary state directory.");
            int before = requests();
            var original = StateStore.Copy(state);
            var originalUndo = undo;
            double originalWidth = Width, originalHeight = Height;
            int originalTab = summaryTab;
            string originalQuery = summaryQuery;
            try
            {
                if (procurementDialog != null) procurementDialog.Close();
                var trade = catalog.Trades.First(t => t.Id == "C6");
                BeginProcurementChecklistScenario(trade, 2);
                OpenProcurementChecklistTab(1);
                var initial = ProcurementReadiness.GetSteps(procurementPlan, state);
                if (initial.Count != 2 || initial.Any(s => s.Kind != "purchase") || percentStat.Text != "0%")
                    throw new Exception("The isolated sand trade must begin with two unchecked purchases and 0% readiness.");
                var potion = initial.Single(s => s.Name == "스태미나 500 포션");
                var knot = initial.Single(s => s.Name == "매듭끈");
                ClickProcurementChecklist(potion.Key, true);
                if (percentStat.Text != "50%") throw new Exception("The first real purchase checkbox did not update readiness to 50% immediately.");
                var reload = store.Load(catalog);
                if (ProcurementReadiness.GetStatus(procurementPlanner.Build(reload), reload).Ready != 1)
                    throw new Exception("The purchase checkbox was not persisted immediately.");
                ClickProcurementChecklist(knot.Key, true);
                if (percentStat.Text != "100%" || !ProcurementReadiness.IsTradeReady(catalog, state, trade, procurementPlan))
                    throw new Exception("Both explicitly completed purchases did not finish the isolated trade.");
                SetTarget(trade, 3);
                PumpProcurementInteractionLayout();
                if (percentStat.Text != "0%" || ProcurementReadiness.GetSteps(procurementPlan, state).Any(s => s.IsReady))
                    throw new Exception("Increasing demand incorrectly reused smaller completed purchase quantities.");
                OpenProcurementChecklistTab(1);
                if (AuctionTestChildren<CheckBox>(summaryRows).Any(c => c.Tag is string && c.IsChecked == true))
                    throw new Exception("Increased quantities still appear checked in the reopened purchase list.");

                // Lower materials are preparation steps, never automatic evidence of completed crafting.
                BeginProcurementChecklistScenario(trade, 2);
                SetProcurementChoice("매듭끈", procurementPlanner.GetRecipes("매듭끈").First().Id);
                SetProcurementChoice("가는 실뭉치", procurementPlanner.GetRecipes("가는 실뭉치").First().Id);
                SetProcurementChoice("거미줄", "acquire");
                OpenProcurementChecklistTab(1);
                AssertProcurementChecklistStages(1);
                foreach (var step in ProcurementReadiness.GetSteps(procurementPlan, state).Where(s => s.Kind == "purchase").ToList())
                    ClickProcurementChecklist(step.Key, true);
                SaveProcurementChecklistPreview(Path.Combine(directory, "procurement-purchase-stages.png"), 1450, 940, false);
                OpenProcurementChecklistTab(2);
                AssertProcurementChecklistStages(2);
                foreach (var step in ProcurementReadiness.GetSteps(procurementPlan, state).Where(s => s.Kind == "acquire").ToList())
                    ClickProcurementChecklist(step.Key, true);
                var partiallyReady = ProcurementReadiness.GetSteps(procurementPlan, state);
                if (partiallyReady.Any(s => s.Kind == "craft" && s.IsReady) ||
                    ProcurementReadiness.IsTradeReady(catalog, state, trade, procurementPlan) || percentStat.Text == "100%")
                    throw new Exception("Preparing lower materials automatically completed a crafted parent.");
                SaveProcurementChecklistPreview(Path.Combine(directory, "procurement-crafting-stages.png"), 1450, 940, false);
                SaveProcurementChecklistPreview(Path.Combine(directory, "procurement-checklist-minimum.png"), MinWidth, MinHeight, true);
                foreach (var step in partiallyReady.Where(s => s.Kind == "craft").OrderBy(s => s.Level).ToList())
                    ClickProcurementChecklist(step.Key, true);
                if (percentStat.Text != "100%" || !ProcurementReadiness.IsTradeReady(catalog, state, trade, procurementPlan))
                    throw new Exception("Independent intermediate and final crafting checks did not finish readiness.");
                reload = store.Load(catalog);
                var savedSteps = ProcurementReadiness.GetSteps(procurementPlanner.Build(reload), reload);
                if (savedSteps.Count == 0 || savedSteps.Any(s => !s.IsReady))
                    throw new Exception("Mixed purchase, acquisition and crafting readiness did not survive reload.");

                // A collapsed purchase stage stays collapsed while a different visible stage changes.
                OpenProcurementChecklistTab(1);
                AssertProcurementChecklistStages(1);
                var lower = ProcurementChecklistStages().Single(e => (string)e.Tag == "procurement-stage:lower");
                lower.IsExpanded = false; PumpProcurementInteractionLayout();
                var finalPurchase = ProcurementReadiness.GetSteps(procurementPlan, state).First(s => s.Kind == "purchase" && s.IsFinal);
                var unchangedScroll = procurementSummaryScroll;
                double unchangedOffset = unchangedScroll.VerticalOffset;
                ClickProcurementChecklist(finalPurchase.Key, false);
                PumpProcurementInteractionLayout();
                if (lower.IsExpanded || !ProcurementChecklistStages().Contains(lower) ||
                    !Object.ReferenceEquals(unchangedScroll, procurementSummaryScroll) ||
                    Math.Abs(unchangedScroll.VerticalOffset - unchangedOffset) > 2)
                    throw new Exception("Checking a visible stage rebuilt, reopened or moved a collapsed sibling stage.");
                SetProcurementChoice("가는 실뭉치", "buy");
                var switched = ProcurementReadiness.GetSteps(procurementPlan, state).Single(s => s.Name == "가는 실뭉치" && s.Kind == "purchase");
                if (switched.IsReady || percentStat.Text == "100%")
                    throw new Exception("Switching a completed craft to purchase inherited the previous mode's completion.");

                // Station detail is informational; preparation checks live in the shared checklist.
                BeginProcurementChecklistScenario(trade, 2);
                ShowStation(trade.Station); selected = trade; RenderAll();
                if (AuctionTestChildren<CheckBox>(detail).Any()) throw new Exception("Station detail still exposes material checkboxes.");
                if (ProcurementReadiness.GetSteps(procurementPlanner.Build(state), state).Any(s => s.IsReady)) throw new Exception("Reading station requirements changed readiness.");

                // Starting ingot and produced ingot use different step keys despite sharing a material name.
                BeginProcurementChecklistScenario(null, 0);
                foreach (var item in catalog.Trades) Calculator.SetSelected(state, item, true);
                SetProcurementChoice("금판", procurementPlanner.GetRecipes("금판").First().Id);
                SetProcurementChoice("금괴", "synthesis"); ShowSummary();
                var ingots = ProcurementReadiness.GetSteps(procurementPlan, state).Where(s => s.Name == "금괴").ToList();
                var seed = ingots.Single(s => s.Kind == "purchase");
                var produced = ingots.Single(s => s.Kind == "craft");
                if (seed.Key == produced.Key || seed.Quantity != 1) throw new Exception("The synthesis seed is not a separate one-ingot purchase step.");
                OpenProcurementChecklistTab(1); ExpandProcurementChecklistStages();
                ClickProcurementChecklist(seed.Key, true);
                if (ProcurementReadiness.GetSteps(procurementPlan, state).Single(s => s.Key == produced.Key).IsReady)
                    throw new Exception("Checking the starting ingot completed the produced ingot.");
                OpenProcurementChecklistTab(2); AssertProcurementChecklistStages(2);
                if (procurementReadyControls[produced.Key].IsChecked == true)
                    throw new Exception("The crafting tab displays the seed's completion on the produced ingot.");

                // The large purchase list exercises a real lower checkbox while the viewport is scrolled.
                OpenProcurementChecklistTab(1); ExpandProcurementChecklistStages();
                var scroll = procurementSummaryScroll;
                if (scroll.ScrollableHeight <= 0) throw new Exception("Expected a scrollable all-selected checklist.");
                var lowerCheck = AuctionTestChildren<CheckBox>(summaryRows).Where(c => c.Tag is string)
                    .OrderBy(c => c.TranslatePoint(new Point(), scroll).Y).Last();
                double rowY = lowerCheck.TranslatePoint(new Point(), scroll).Y;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + rowY - scroll.ViewportHeight * 0.45);
                PumpProcurementInteractionLayout();
                double offset = scroll.VerticalOffset;
                rowY = lowerCheck.TranslatePoint(new Point(), scroll).Y;
                if (offset <= 0 || rowY < 0 || rowY + lowerCheck.ActualHeight > scroll.ViewportHeight + 2)
                    throw new Exception("The checklist action is not visible in a scrolled viewport.");
                string lowerKey = (string)lowerCheck.Tag;
                ClickProcurementChecklist(lowerKey, true); PumpProcurementInteractionLayout();
                if (!Object.ReferenceEquals(scroll, procurementSummaryScroll) ||
                    !Object.ReferenceEquals(lowerCheck, procurementReadyControls[lowerKey]) ||
                    Math.Abs(offset - scroll.VerticalOffset) > 2 ||
                    Math.Abs(rowY - lowerCheck.TranslatePoint(new Point(), scroll).Y) > 2)
                    throw new Exception("Checking a lower purchase rebuilt its control or jumped the scroll position.");

                // The public reset and undo buttons must include all new readiness snapshots.
                int checkedBeforeReset = ProcurementReadiness.GetStatus(procurementPlan, state).Ready;
                var targetsBeforeReset = state.Targets.ToDictionary(p => p.Key, p => p.Value);
                var modesBeforeReset = state.ProcurementChoices.ToDictionary(p => p.Key, p => p.Value);
                AuctionTestChildren<Button>(shell).Single(b => b.Content as string == "주간 리셋")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (percentStat.Text != "0%" || ProcurementReadiness.GetStatus(procurementPlan, state).Ready != 0)
                    throw new Exception("Weekly reset left procurement readiness checked.");
                if (targetsBeforeReset.Any(p => state.Targets[p.Key] != p.Value) ||
                    modesBeforeReset.Count != state.ProcurementChoices.Count || modesBeforeReset.Any(p => state.ProcurementChoices[p.Key] != p.Value))
                    throw new Exception("Weekly reset changed quantities or procurement choices.");
                AuctionTestChildren<Button>(footerActions).Single(b => b.Content as string == "리셋 되돌리기")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (ProcurementReadiness.GetStatus(procurementPlan, state).Ready != checkedBeforeReset)
                    throw new Exception("Undo reset did not restore the new readiness snapshots.");
                AssertProcurementChecklistPercent();
                if (requests() != before) throw new Exception("Checklist actions, stages, target changes or reset/undo sent auction HTTP.");
                return "PASS procurement checklist UI: actual checks update percent and persistence immediately; purchase/acquire/craft steps stay independent; lower-middle-final stages are ordered in both tabs; collapse and scrolling preserve controls; larger quantities reopen checks; mode changes and synthesis seeds cannot inherit other completion; station material information has no readiness controls; reset/undo preserves the plan; zero auction HTTP.";
            }
            finally
            {
                if (procurementDialog != null) procurementDialog.Close();
                state = original; undo = originalUndo; Width = originalWidth; Height = originalHeight;
                summaryTab = originalTab; summaryQuery = originalQuery; Persist(); RenderAll();
            }
        }

        void BeginProcurementChecklistScenario(TradeItem trade, int target)
        {
            if (procurementDialog != null) procurementDialog.Close();
            state = new ProgressState(); state.Normalize(catalog); undo = null; footerActions.Children.Clear();
            if (trade != null) { state.Targets[trade.Id] = target; Calculator.SetSelected(state, trade, true); }
            Width = 1100; Height = 740; ShowSummary(); PumpProcurementInteractionLayout();
        }

        void SaveProcurementChecklistPreview(string path, double width, double height, bool compact)
        {
            double previousWidth = Width, previousHeight = Height;
            try
            {
                Width = width; Height = height; PumpProcurementInteractionLayout();
                ExpandProcurementChecklistStages();
                // The minimum-size preview also demonstrates a completed lower stage
                // folded away while intermediate and final crafting remain actionable.
                if (compact)
                    foreach (var stage in ProcurementChecklistStages().Where(e => (string)e.Tag == "procurement-stage:lower"))
                        stage.IsExpanded = false;
                procurementSummaryScroll.ScrollToTop(); PumpProcurementInteractionLayout();
                SavePreview(path);
            }
            finally
            {
                Width = previousWidth; Height = previousHeight; PumpProcurementInteractionLayout();
                ExpandProcurementChecklistStages();
            }
        }

        void OpenProcurementChecklistTab(int tab)
        {
            if (!summaryView) ShowSummary();
            string title = tab == 1 ? "경매장 구매 체크리스트" : "제작·확보 체크리스트";
            AuctionTestChildren<Button>(content).Single(b => b.Content as string == title)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpProcurementInteractionLayout();
            if (summaryTab != tab) throw new Exception("The actual checklist tab button did not change tabs.");
            ExpandProcurementChecklistStages();
        }

        void ClickProcurementChecklist(string key, bool ready)
        {
            CheckBox control;
            if (!procurementReadyControls.TryGetValue(key, out control) ||
                AutomationProperties.GetName(control) != "procurement-ready:" + key)
                throw new Exception("Missing accessible checklist checkbox: " + key);
            control.IsChecked = ready;
            control.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            // This assertion deliberately precedes any dispatcher/layout pump.
            AssertProcurementChecklistPercent();
        }

        void AssertProcurementChecklistPercent()
        {
            var currentPlan = procurementPlanner.Build(state);
            decimal percent = ProcurementReadiness.GetStatus(currentPlan, state).Percent;
            string expected = (percent >= 100 ? 100 : (int)Math.Min(99m, Math.Round(percent))) + "%";
            if (percentStat.Text != expected) throw new Exception("Readiness percent is stale: expected " + expected + ", displayed " + percentStat.Text + ".");
        }

        List<Expander> ProcurementChecklistStages()
        {
            return AuctionTestChildren<Expander>(summaryRows).Where(e => e.Tag is string &&
                ((string)e.Tag).StartsWith("procurement-stage:", StringComparison.Ordinal)).ToList();
        }

        void ExpandProcurementChecklistStages()
        {
            // There are at most the bounded recipe depth's stages; no unbounded UI polling.
            var stages = ProcurementChecklistStages();
            if (stages.Count > 16) throw new Exception("Unexpectedly many checklist stages.");
            foreach (var stage in stages) stage.IsExpanded = true;
            PumpProcurementInteractionLayout();
        }

        void AssertProcurementChecklistStages(int tab)
        {
            ExpandProcurementChecklistStages();
            var steps = ProcurementReadiness.GetSteps(procurementPlan, state)
                .Where(s => tab == 1 ? s.Kind == "purchase" : s.Kind == "craft" || s.Kind == "acquire").ToList();
            Func<ProcurementStep, string> stageKey = s => s.GroupKey;
            var expected = new HashSet<string>(steps.Select(stageKey), StringComparer.Ordinal);
            var stages = ProcurementChecklistStages();
            var actual = stages.Select(e => ((string)e.Tag).Substring("procurement-stage:".Length)).ToList();
            if (actual.Count != expected.Count || !expected.SetEquals(actual))
                throw new Exception("Checklist stage groups do not match the active steps in tab " + tab + ".");
            int previous = -1;
            foreach (var group in stages)
            {
                string key = ((string)group.Tag).Substring("procurement-stage:".Length);
                int order = key == "lower" ? 0 : key == "final" ? Int32.MaxValue : Int32.Parse(key.Substring("middle:".Length));
                if (order < previous) throw new Exception("Checklist stages are not ordered lower, intermediate, final.");
                previous = order;
                var contained = new HashSet<string>(AuctionTestChildren<CheckBox>(group).Where(c => c.Tag is string).Select(c => (string)c.Tag));
                if (!contained.SetEquals(steps.Where(s => stageKey(s) == key).Select(s => s.Key)))
                    throw new Exception("A checklist step appears in the wrong stage: " + key);
            }
            var allKeys = new HashSet<string>(AuctionTestChildren<CheckBox>(summaryRows).Where(c => c.Tag is string).Select(c => (string)c.Tag));
            if (!allKeys.SetEquals(steps.Select(s => s.Key)))
                throw new Exception("A checklist tab omitted an active step or included an inactive preview.");
        }
    }
}
