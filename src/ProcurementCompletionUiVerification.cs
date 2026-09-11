using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        public string RunProcurementCompletionUiChecks(Func<int> requests, string directory)
        {
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Completion UI checks require an injected offline service and a temporary progress directory.");
            int beforeRequests = requests(); var original = StateStore.Copy(state); var originalUndo = undo;
            double originalWidth = Width, originalHeight = Height; int originalTab = summaryTab; string originalQuery = summaryQuery;
            bool originalPrompts = procurementSharedPromptsEnabled;
            try
            {
                if (procurementDialog != null) procurementDialog.Close(); procurementSharedPromptsEnabled = false;
                state = new ProgressState(); state.Normalize(catalog); undo = null; footerActions.Children.Clear();
                foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, true);
                for (int depth = 0; depth < 20; depth++)
                {
                    bool changed = false;
                    foreach (var node in procurementPlanner.Build(state).Nodes)
                        if (!node.IsCrafting && procurementPlanner.GetRecipes(node.Name).Count > 0)
                        { procurementPlanner.SetChoice(state, node.Name, procurementPlanner.GetRecipes(node.Name).First().Id); changed = true; }
                    if (!changed) break;
                }
                procurementPlanner.SetChoice(state, "거미줄", "acquire"); procurementPlanner.SetChoice(state, "실리엔 결정", "acquire");
                Width = 1450; Height = 940; ShowSummary(); Persist(); PumpProcurementInteractionLayout();
                var roots = ProcurementReadiness.GetSteps(procurementPlan, state).Where(s => s.HasExchangeUse).ToList();
                var rootKeys = new HashSet<string>(roots.Select(s => s.Key), StringComparer.Ordinal);
                var silienRoot = roots.Single(s => s.Name == "실리엔");
                if (roots.Count != procurementPlan.Roots.Count || !roots.Any(s => s.Name == "펫 놀이세트") || !roots.Any(s => s.Name == "매듭끈"))
                    throw new Exception("Completion fixture omitted a direct exchange requirement.");
                foreach (int tab in new[] { 1, 2 })
                {
                    OpenProcurementChecklistTab(tab);
                    foreach (var step in roots.Where(s => s.Key != silienRoot.Key && (tab == 1 ? s.Kind == "purchase" : s.Kind != "purchase")))
                        ClickProcurementChecklist(step.Key, true);
                }
                var pendingSilien = ProcurementReadiness.GetSteps(procurementPlan, state).Single(s => s.Key == silienRoot.Key);
                if (pendingSilien.IsReady || pendingSilien.CompletionFraction >= 1 || ProcurementReadiness.GetStatus(procurementPlan, state).Percent >= 100 || percentStat.Text == "100%")
                    throw new Exception("Completing Silien's other parents also completed the separate direct-exchange Silien requirement.");
                ClickProcurementChecklist(silienRoot.Key, true); PumpProcurementInteractionLayout();
                var completed = ProcurementReadiness.GetSteps(procurementPlan, state);
                if (percentStat.Text != "100%" || ProcurementReadiness.GetStatus(procurementPlan, state).Percent != 100 || readyStat.Text != "20 / 20")
                    throw new Exception("Explicitly completing all direct exchange items did not reach 100% and all 20 trades ready.");
                if (!rootKeys.SetEquals(state.ProcurementReady.Keys) || completed.Where(s => !s.HasExchangeUse).Any(s => s.IsReady))
                    throw new Exception("Derived completion secretly checked or saved lower material readiness.");
                if (!completed.Where(s => !s.HasExchangeUse).Any() || completed.Where(s => !s.HasExchangeUse).Any(s => !s.IsCovered || s.CompletionFraction != 1))
                    throw new Exception("Finished exchange items did not fully cover their unprepared lower requirements.");
                OpenProcurementChecklistTab(2); procurementSummaryScroll.ScrollToTop(); PumpProcurementInteractionLayout();
                if (!AuctionTestChildren<TextBlock>(summaryRows).Any(t => (t.Text ?? "").Contains("상위 품목 구비로 충족")))
                    throw new Exception("The checklist does not explain completion derived from an upper item.");
                var web = completed.Single(s => s.Name == "거미줄" && s.Kind == "acquire");
                if (procurementReadyControls[web.Key].IsChecked != false)
                    throw new Exception("A covered raw material visually appeared as an explicit checkbox check.");
                SavePreview(Path.Combine(directory, "procurement-covered-100.png"));

                AssertCompletionReload(directory);
                // An independent raw-material confirmation must survive revoking a parent.
                ClickProcurementChecklist(web.Key, true); PumpProcurementInteractionLayout();
                var explicitWeb = new JavaScriptSerializer().Serialize(state.ProcurementReady[web.Key]);
                var scroll = procurementSummaryScroll;
                var knot = roots.Single(s => s.Name == "매듭끈"); var knotControl = procurementReadyControls[knot.Key];
                scroll.ScrollToVerticalOffset(Math.Max(0, scroll.ScrollableHeight * .45)); PumpProcurementInteractionLayout();
                double offset = scroll.VerticalOffset;
                ClickProcurementChecklist(knot.Key, false); PumpProcurementInteractionLayout();
                var reopened = ProcurementReadiness.GetSteps(procurementPlan, state).Single(s => s.Key == knot.Key);
                if (reopened.IsReady || reopened.CompletionFraction >= 1 || percentStat.Text == "100%" || ProcurementReadiness.GetStatus(procurementPlan, state).Percent >= 100)
                    throw new Exception("Removing an exchange-item check failed to reopen its outstanding requirement.");
                if (explicitWeb != new JavaScriptSerializer().Serialize(state.ProcurementReady[web.Key]))
                    throw new Exception("Revoking a parent erased an explicitly prepared lower-material snapshot.");
                if (!Object.ReferenceEquals(scroll, procurementSummaryScroll) || !Object.ReferenceEquals(knotControl, procurementReadyControls[knot.Key]) || Math.Abs(scroll.VerticalOffset - offset) > 2)
                    throw new Exception("Derived progress update replaced checklist controls or moved the scroll position.");
                ClickProcurementChecklist(knot.Key, true); PumpProcurementInteractionLayout();
                if (percentStat.Text != "100%") throw new Exception("Rechecking the final item did not immediately restore 100%.");

                OpenProcurementDetail("나무판"); procurementDialog.WindowStartupLocation = WindowStartupLocation.Manual;
                procurementDialog.Left = -18000; procurementDialog.Top = -18000; PumpProcurementInteractionLayout();
                var boardModes = AuctionTestChildren<Button>(procurementDialog).Select(AutomationProperties.GetName).ToList();
                if (!boardModes.Contains("나무판 경매장 구매 선택") || boardModes.Contains("나무판 직접 제작 선택") || boardModes.Contains("나무판 직접 확보 선택"))
                    throw new Exception("Board detail is not restricted to the purchase mode button.");
                if (AuctionTestChildren<Expander>(procurementDialog).Any() || !procurementDetailQuoteNames.SetEquals(new[] { "나무판" }))
                    throw new Exception("Board detail still exposes its manufacturing subtree or price comparison names.");
                if (!AuctionTestChildren<TextBlock>(procurementDialog).Any(t => (t.Text ?? "").Contains("경매장 구매 전용")))
                    throw new Exception("Board detail is missing its purchase-only explanation.");
                SaveProcurementDialogPreview(Path.Combine(directory, "procurement-board-purchase-only.png")); procurementDialog.Close();
                AssertBoardCompletionMigration(directory);

                // Partial shared coverage is quantity-based and must not complete
                // either another consuming root or the directly exchanged Silien.
                state = new ProgressState(); state.Normalize(catalog);
                foreach (string id in new[] { "C20", "C31", "C44", "K38", "K42" }) Calculator.SetSelected(state, catalog.Trades.Single(t => t.Id == id), true);
                foreach (string name in new[] { "펫 놀이세트", "마력이 깃든 나무장작", "실리엔", "정화된 토끼의 발", "에너지 컨버터", "에너지 증폭 장치", "끈끈이 풀" })
                    procurementPlanner.SetChoice(state, name, procurementPlanner.GetRecipes(name).First().Id);
                ShowSummary(); Persist(); OpenProcurementChecklistTab(2);
                var oneParent = ProcurementReadiness.GetSteps(procurementPlan, state).Single(s => s.Name == "정화된 토끼의 발" && s.Kind == "craft");
                ClickProcurementChecklist(oneParent.Key, true); PumpProcurementInteractionLayout();
                var partial = ProcurementReadiness.GetSteps(procurementPlan, state);
                var sharedSilien = partial.Single(s => s.Name == "실리엔" && s.Kind == "craft");
                var sharedCrystal = partial.Single(s => s.Name == "실리엔 결정" && s.Kind == "purchase");
                if (sharedSilien.IsReady || sharedSilien.IsCovered || sharedSilien.CompletionFraction <= 0 || sharedSilien.CompletionFraction >= 1 ||
                    sharedCrystal.IsReady || sharedCrystal.CompletionFraction <= 0 || sharedCrystal.CompletionFraction >= 1)
                    throw new Exception("One finished parent did not yield partial-only coverage of shared Silien and crystals.");
                if (state.ProcurementReady.Count != 1 || percentStat.Text == "100%" || partial.Where(s => s.HasExchangeUse && s.Key != oneParent.Key).Any(s => s.IsReady))
                    throw new Exception("Partial shared coverage persisted checks or completed another exchange item.");
                if (requests() != beforeRequests) throw new Exception("Completion, purchase-only detail, migration or reload requested auction HTTP.");
                return "PASS procurement completion UI: all 20 selected trades reach 100% from explicit exchange-item checks, including direct Silien; lower requirements are derived covered without checked controls or saved snapshots; saved-state reopen remains 100%; unchecking a parent restores outstanding progress and preserves explicit lower stock, controls and scroll; one shared parent grants partial-only coverage; board detail is purchase-only with no recipe subtree; legacy board stock migrates by maximum quantity; zero auction HTTP.";
            }
            finally
            {
                if (procurementDialog != null) procurementDialog.Close();
                state = original; undo = originalUndo; Width = originalWidth; Height = originalHeight;
                summaryTab = originalTab; summaryQuery = originalQuery; procurementSharedPromptsEnabled = originalPrompts; Persist(); RenderAll();
            }
        }

        void AssertCompletionReload(string directory)
        {
            Func<int> reloadRequests;
            var fake = AuctionVerification.CreateUiProbe(Path.Combine(directory, "completion-reload-probe"), out reloadRequests);
            var reopened = new MainWindow(catalog, new StateStore(store.FilePath), false, fake);
            try
            {
                reopened.WindowStartupLocation = WindowStartupLocation.Manual; reopened.Left = -18000; reopened.Top = -18000; reopened.ShowInTaskbar = false; reopened.Show();
                reopened.ShowSummary(); reopened.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {}));
                if (reopened.percentStat.Text != "100%" || ProcurementReadiness.GetStatus(reopened.procurementPlan, reopened.state).Percent != 100)
                    throw new Exception("Reopening a window from saved explicit final-item checks lost 100% completion.");
                if (ProcurementReadiness.GetSteps(reopened.procurementPlan, reopened.state).Where(s => !s.HasExchangeUse).Any(s => s.IsReady))
                    throw new Exception("State reload wrote derived lower completion as explicit inventory.");
                if (reloadRequests() != 0) throw new Exception("The saved completion reload requested auction HTTP.");
            }
            finally { reopened.Close(); }
        }

        void AssertBoardCompletionMigration(string directory)
        {
            var legacy = StateStore.Copy(state); legacy.ProcurementChoices["나무판"] = "default";
            foreach (var record in new[] { new { Kind = "purchase", Quantity = 3m }, new { Kind = "craft", Quantity = 8m }, new { Kind = "acquire", Quantity = 5m } })
                legacy.ProcurementReady[record.Kind + ":나무판"] = new ProcurementReadySnapshot { Name = "나무판", Kind = record.Kind, Quantity = record.Quantity, Context = record.Kind };
            var json = new JavaScriptSerializer();
            string unrelatedChoices = json.Serialize(legacy.ProcurementChoices.Where(p => p.Key != "나무판").ToDictionary(p => p.Key, p => p.Value));
            var migrationStore = new StateStore(Path.Combine(directory, "board-completion-migration.json")); migrationStore.Save(legacy);
            var migrated = migrationStore.Load(catalog); var boardRecords = migrated.ProcurementReady.Values.Where(s => s.Name == "나무판").ToList();
            if (migrated.ProcurementChoices.ContainsKey("나무판") || boardRecords.Count != 1 || boardRecords[0].Kind != "purchase" || boardRecords[0].Context != "purchase" || boardRecords[0].Quantity != 8)
                throw new Exception("Legacy board completion must migrate the maximum prepared quantity, not the sum of alternative modes.");
            if (json.Serialize(migrated.ProcurementChoices) != unrelatedChoices || json.Serialize(migrated.Selected) != json.Serialize(legacy.Selected) || json.Serialize(migrated.Targets) != json.Serialize(legacy.Targets))
                throw new Exception("Board completion migration changed unrelated choices or trade targets.");
        }
    }
}
