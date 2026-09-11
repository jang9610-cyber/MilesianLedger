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
        public string RunNpcProcurementUiChecks(Func<int> requests, string directory)
        {
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("NPC UI checks require an injected offline service and temporary progress directory.");
            int before = requests(); var original = StateStore.Copy(state); var originalUndo = undo;
            int originalTab = summaryTab; string originalQuery = summaryQuery; bool originalPrompts = procurementSharedPromptsEnabled;
            double originalWidth = Width, originalHeight = Height;
            var npcNames = new HashSet<string>(ProcurementPlanner.NpcPurchaseNames, StringComparer.Ordinal);
            try
            {
                if (procurementDialog != null) procurementDialog.Close(); procurementSharedPromptsEnabled = false;
                state = new ProgressState(); state.Normalize(catalog); undo = null; footerActions.Children.Clear();
                foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, trade.Id == "C31");
                state.Targets["C31"] = 3; Width = 1450; Height = 940; ShowSummary(); PumpProcurementInteractionLayout();
                procurementCardButtons["새우 조련 미끼"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                procurementDialog.WindowStartupLocation = WindowStartupLocation.Manual; procurementDialog.Left = -18000; procurementDialog.Top = -18000;
                PumpProcurementInteractionLayout();
                var rows = VisibleProcurementRows(procurementRootView).Where(r => npcNames.Contains(r.Name)).ToList();
                if (rows.Count != 3 || rows.Any(r => r.Buy != null || r.Craft != null || r.Acquire != null || r.Demand != 2m))
                    throw new Exception("Bought bait preview must show three NPC quantities without purchase or acquisition mode buttons.");
                if (rows.Any(r => !r.Price.Text.Contains("NPC 구매") || !r.Price.Text.Contains("0 G")))
                    throw new Exception("NPC detail rows must explain excluded auction cost instead of showing cached auction comparisons.");
                if (procurementDetailQuoteNames.Any(npcNames.Contains) || GetAuctionMaterialSnapshot().Any(npcNames.Contains) || procurementPlan.Acquisitions.Count != 0)
                    throw new Exception("Bought bait preview must neither quote NPC materials nor activate their requirements.");
                var modes = AuctionTestChildren<Button>(procurementDialog).Select(AutomationProperties.GetName).ToList();
                if (!modes.Contains("새우 조련 미끼 경매장 구매 선택") || !modes.Contains("새우 조련 미끼 직접 제작 선택"))
                    throw new Exception("Finished bait lost one of its normal buy/craft buttons.");
                ProcurementInteractionButton("새우 조련 미끼 직접 제작 선택").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpProcurementInteractionLayout();
                if (!npcNames.SetEquals(procurementPlan.Acquisitions.Select(r => r.Name)) || procurementPlan.Acquisitions.Any(r => r.Quantity != 2m) || procurementPlan.Purchases.Any(r => npcNames.Contains(r.Name)))
                    throw new Exception("Actual bait craft click did not activate the three fixed NPC requirements outside purchases.");
                SaveProcurementDialogPreview(Path.Combine(directory, "procurement-npc-bait-detail.png"));
                string[] frozen = GetAuctionMaterialSnapshot();
                if (frozen.Length != 1 || frozen.Any(npcNames.Contains) || requests() != before)
                    throw new Exception("Preview and craft choice must send no requests and leave only one actual non-NPC purchase name.");
                // The existing offline probe is the only transport. Explicitly
                // clicking the refresh button must honor this filtered snapshot.
                procurementDetailRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAuctionTestTask();
                if (requests() != before + frozen.Length) throw new Exception("Explicit refresh requested more than the frozen non-NPC snapshot.");
                procurementDialog.Close(); summaryTab = 2; RenderSummary(); PumpProcurementInteractionLayout();
                foreach (string name in npcNames)
                    if (!procurementReadyControls.ContainsKey("acquire:" + name) || procurementReadyControls.ContainsKey("purchase:" + name))
                        throw new Exception("NPC purchase must retain an acquire-kind preparation checkbox: " + name);
                var sugarCheck = procurementReadyControls["acquire:설탕"];
                sugarCheck.IsChecked = true; sugarCheck.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); PumpProcurementInteractionLayout();
                if (!state.ProcurementReady.ContainsKey("acquire:설탕") || ProcurementReadiness.GetStatus(procurementPlan, state).Percent <= 0 || !Object.ReferenceEquals(sugarCheck, procurementReadyControls["acquire:설탕"]))
                    throw new Exception("NPC preparation check failed to persist, update progress, or preserve the control.");
                if (!store.Load(catalog).ProcurementReady.ContainsKey("acquire:설탕")) throw new Exception("NPC preparation check was not saved in the isolated store.");
                if (!AuctionTestChildren<TextBlock>(summaryRows).Any(t => (t.Text ?? "").Contains("NPC 구매 · 경매장 합산 0 G")))
                    throw new Exception("Preparation checklist lacks its NPC purchase explanation.");
                SavePreview(Path.Combine(directory, "procurement-npc-checklist.png"));
                string exported = ProcurementExport(); int npcSection = exported.IndexOf("NPC 구매 목록", StringComparison.Ordinal);
                if (npcSection < 0 || npcNames.Any(n => !exported.Substring(npcSection).Contains(n + "\t2\t0 G")) || exported.Substring(0, exported.IndexOf("직접 제작 목록", StringComparison.Ordinal)).Contains("설탕"))
                    throw new Exception("Export must separate NPC quantities and zero auction contribution from auction purchases.");
                UpdateAuctionSummary(procurementPlan.Purchases);
                decimal expectedSum = procurementPlan.Purchases.Sum(p => p.Quantity * 100m);
                if (!auctionSummaryStatus.Text.Contains(Gold(expectedSum)) || expectedSum != 600m)
                    throw new Exception("NPC materials inflated the six-Silien purchase total after comparison refresh.");
                OpenProcurementDetail("새우 조련 미끼"); PumpProcurementInteractionLayout();
                ProcurementInteractionButton("새우 조련 미끼 경매장 구매 선택").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpProcurementInteractionLayout();
                if (procurementPlan.Acquisitions.Count != 0 || !procurementPlan.Purchases.Any(p => p.Name == "새우 조련 미끼") || GetAuctionMaterialSnapshot().Any(npcNames.Contains))
                    throw new Exception("Actual buy click failed to remove NPC requirements while retaining safe preview filtering.");
                if (requests() != before + frozen.Length) throw new Exception("NPC check, export, or buy switch made an implicit auction request.");
                return "PASS NPC procurement UI: actual bait preview and buy/craft clicks retain three fixed NPC quantities with no mode buttons; preparation checks persist and update progress; NPC prices are excluded from comparison, export and purchase totals; only the explicit refresh button sends exactly one offline non-NPC purchase request.";
            }
            finally
            {
                if (procurementDialog != null) procurementDialog.Close();
                state = original; undo = originalUndo; summaryTab = originalTab; summaryQuery = originalQuery; procurementSharedPromptsEnabled = originalPrompts;
                Width = originalWidth; Height = originalHeight; Persist(); RenderAll();
            }
        }
    }
}
