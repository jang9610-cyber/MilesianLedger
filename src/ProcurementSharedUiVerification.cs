using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        public string RunProcurementSharedUiChecks(Func<int> requests, string directory)
        {
            // Only the injected fake transport and its temporary state are permitted.
            // This suite neither reads credentials nor clicks either auction refresh button.
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)),
                Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Shared preparation UI checks require the offline temporary window.");
            int before = requests();
            var original = StateStore.Copy(state);
            var originalUndo = undo;
            bool originalPrompts = procurementSharedPromptsEnabled;
            double originalWidth = Width, originalHeight = Height;
            int originalTab = summaryTab;
            string originalQuery = summaryQuery;
            try
            {
                procurementSharedPromptsEnabled = true;

                // A root with several shared descendants exercises a genuine partial selection.
                BeginProcurementSharedScenario("마력이 깃든 나무장작");
                var woodCraft = ProcurementInteractionButton("마력이 깃든 나무장작 직접 제작 선택");
                InvokeProcurementSharedModal(woodCraft, true, delegate(Window dialog) {
                    if (procurementSharedChecks.Count < 2 || !procurementSharedChecks.ContainsKey("실리엔"))
                        throw new Exception("The magical firewood preview must offer multiple common-material candidates.");
                    AssertSharedCandidatePreviewIsLocal();
                    if (procurementSharedOnlyButton.IsCancel) throw new Exception("A close handler must not also dispatch a second dialog-cancel command.");
                    procurementSharedOnlyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                if (state.ProcurementChoices.Count != 1 || procurementPlanner.GetChoice(state, "마력이 깃든 나무장작") == "buy")
                    throw new Exception("Item-only confirmation did not preserve exactly the initial direct choice.");

                // The main example deliberately prepares only Silien, retaining sibling choices.
                BeginProcurementSharedScenario("정화된 토끼의 발");
                foreach (string name in new[] { "건초 더미", "마력이 깃든 나무장작", "정화된 토끼의 발" })
                {
                    var step = ProcurementReadiness.GetSteps(procurementPlan, state).Single(s => s.Name == name && s.Kind == "purchase");
                    ProcurementReadiness.SetReady(state, step, true);
                }
                Persist(); RenderStats(procurementPlan);
                var unrelated = new[] { "펫 놀이세트", "건초 더미", "새우 조련 미끼", "마법의 깃털펜", "튼튼한 고리", "최고급 바닐라 향초" };
                var unchangedQuantities = procurementPlan.Purchases.Where(p => unrelated.Contains(p.Name))
                    .ToDictionary(p => p.Name, p => p.Quantity, StringComparer.Ordinal);
                if (unchangedQuantities.Count != unrelated.Length) throw new Exception("The shared example is missing an unrelated sibling root.");
                var summaryScroll = procurementSummaryScroll;
                if (summaryScroll.ScrollableHeight > 0) summaryScroll.ScrollToVerticalOffset(summaryScroll.ScrollableHeight * 0.5);
                PumpProcurementInteractionLayout();
                double summaryOffset = summaryScroll.VerticalOffset;
                var cardReferences = procurementCardButtons.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var detailScroll = procurementDetailScroll;
                var craft = ProcurementInteractionButton("정화된 토끼의 발 직접 제작 선택");
                double detailOffset = detailScroll.VerticalOffset;
                double craftY = craft.TranslatePoint(new Point(), detailScroll).Y;

                InvokeProcurementSharedModal(craft, true, delegate(Window dialog) {
                    if (procurementPlanner.GetChoice(state, "정화된 토끼의 발") == "buy")
                        throw new Exception("The user's direct craft choice must apply before the modal preview opens.");
                    AssertSharedCandidatePreviewIsLocal();
                    procurementSharedOnlyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                if (state.ProcurementChoices.Count != 1 || procurementPlanner.GetChoice(state, "실리엔") != "buy")
                    throw new Exception("The item-only path applied a shared suggestion.");
                if (state.ProcurementReady.Values.Any(s => s.Name == "정화된 토끼의 발") ||
                    !state.ProcurementReady.Values.Any(s => s.Name == "마력이 깃든 나무장작"))
                    throw new Exception("The initial choice did not invalidate just its own completion record.");
                AssertProcurementInteractionPosition(detailScroll, craft, detailOffset, craftY, "item-only shared prompt");
                AssertProcurementSummaryUnchanged(summaryScroll, summaryOffset, cardReferences);

                // Closing the window follows the title-bar X path and keeps the already-applied choice.
                string itemOnlyState = SharedUiStateSnapshot();
                InvokeProcurementSharedModal(craft, true, delegate(Window dialog) {
                    if (!procurementSharedChecks.ContainsKey("실리엔")) throw new Exception("Clicking the same direct button no longer offers pending shared work.");
                    dialog.Close();
                });
                if (itemOnlyState != SharedUiStateSnapshot()) throw new Exception("Closing the shared prompt changed the user's current plan.");

                List<string> previewNames = null;
                InvokeProcurementSharedModal(craft, true, delegate(Window dialog) {
                    AssertSharedCandidatePreviewIsLocal();
                    previewNames = procurementSharedPreviewChanges.Select(c => c.Name).ToList();
                    foreach (var expander in AuctionTestChildren<Expander>(dialog).Where(e => e.Header as string == "연결된 제작 경로 보기"))
                        expander.IsExpanded = true;
                    SaveProcurementSharedContentBitmap(dialog, Path.Combine(directory, "procurement-shared-preview.png"), 730, 720);
                    foreach (var expander in AuctionTestChildren<Expander>(dialog)) expander.IsExpanded = false;
                    SaveProcurementSharedContentBitmap(dialog, Path.Combine(directory, "procurement-shared-minimum.png"), 620, 500);
                    procurementSharedApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                if (previewNames == null || previewNames.Count == 0) throw new Exception("No shared changes were previewed before application.");
                var expectedNames = new HashSet<string>(new[] { "정화된 토끼의 발", "실리엔", "실리엔 결정", "마력이 깃든 나무장작", "에너지 증폭 장치", "에너지 컨버터", "끈끈이 풀" }, StringComparer.Ordinal);
                if (!expectedNames.SetEquals(state.ProcurementChoices.Keys))
                    throw new Exception("The Silien action changed an unrelated path or omitted an affected selected root.");
                var appliedPlan = procurementPlanner.Build(state);
                if (appliedPlan.Nodes.Single(n => n.Name == "실리엔").Quantity != 18 ||
                    appliedPlan.Acquisitions.Single(n => n.Name == "실리엔 결정").Quantity != 90 ||
                    appliedPlan.Nodes.Single(n => n.Name == "에너지 컨버터").Quantity != 7)
                    throw new Exception("Shared Silien must aggregate to 18, its crystals to 90, and the converter to 7.");
                foreach (string name in expectedNames.Where(n => n != "실리엔 결정"))
                    if (!appliedPlan.Crafts.Any(n => n.Name == name)) throw new Exception("An affected root or intermediate is not crafting: " + name);
                foreach (var pair in unchangedQuantities)
                    if (procurementPlanner.GetChoice(state, pair.Key) != "buy" || appliedPlan.Purchases.Single(p => p.Name == pair.Key).Quantity != pair.Value)
                        throw new Exception("Shared preparation changed an unrelated sibling purchase: " + pair.Key);
                foreach (string name in new[] { "힐웬", "중급 나무장작", "돌연변이 토끼의 발", "돌연변이 식물의 점액질", "에메랄드 코어" })
                    if (procurementPlanner.GetChoice(state, name) != "buy") throw new Exception("An unselected common candidate or sibling ingredient was changed: " + name);
                if (state.ProcurementReady.Count != 1 || state.ProcurementReady.Values.Single().Name != "건초 더미" ||
                    ProcurementReadiness.GetSteps(appliedPlan, state).Any(s => s.Kind == "craft" && s.IsReady))
                    throw new Exception("Shared preparation cleared unrelated completion or automatically completed new crafts.");
                if (previewNames.Any(name => state.ProcurementReady.Values.Any(s => s.Name == name)))
                    throw new Exception("A changed material retained an obsolete completion snapshot.");
                AssertProcurementChecklistPercent();
                AssertProcurementInteractionPosition(detailScroll, craft, detailOffset, craftY, "shared preparation apply");
                AssertProcurementSummaryUnchanged(summaryScroll, summaryOffset, cardReferences);
                var reloaded = store.Load(catalog);
                if (SharedUiSerialize(reloaded.ProcurementChoices) != SharedUiSerialize(state.ProcurementChoices) ||
                    SharedUiSerialize(reloaded.ProcurementReady) != SharedUiSerialize(state.ProcurementReady))
                    throw new Exception("Shared choices and completion invalidation were not saved.");

                string afterApply = SharedUiStateSnapshot();
                InvokeProcurementSharedModal(craft, false, delegate(Window dialog) {
                    if (procurementSharedPreviewChanges.Count != 0 || procurementSharedApplyButton.IsEnabled)
                        throw new Exception("Repeating a fully applied direct choice still offers additional mutations.");
                    procurementSharedOnlyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                if (afterApply != SharedUiStateSnapshot()) throw new Exception("Repeating a completed shared operation changed state.");
                procurementDialog.Close();
                OpenProcurementChecklistTab(2); ExpandProcurementChecklistStages();
                Width = 1450; Height = 940; PumpProcurementInteractionLayout();
                procurementSummaryScroll.ScrollToTop(); PumpProcurementInteractionLayout();
                SavePreview(Path.Combine(directory, "procurement-shared-applied-crafting.png"));
                if (requests() != before) throw new Exception("Shared previews, candidate checks or bulk preparation sent auction HTTP.");
                return "PASS shared preparation UI: real direct buttons open modal candidate previews; partial selection is local and empty selection disables apply; item-only and window close preserve the initial choice; repeated buttons reopen pending suggestions; Silien applies exactly 18/90 with shared converter 7 while unrelated purchases and completion remain; changed completion resets without auto-ready; saved state, readiness, controls and scrolling stay consistent; popup footers fit 730x720 and 620x500; repeated application is inert; zero auction HTTP.";
            }
            finally
            {
                if (procurementSharedDialog != null) procurementSharedDialog.Close();
                if (procurementDialog != null) procurementDialog.Close();
                procurementSharedPromptsEnabled = originalPrompts;
                state = original; undo = originalUndo; Width = originalWidth; Height = originalHeight;
                summaryTab = originalTab; summaryQuery = originalQuery; Persist(); RenderAll();
            }
        }

        void BeginProcurementSharedScenario(string detailName)
        {
            if (procurementDialog != null) procurementDialog.Close();
            state = new ProgressState(); state.Normalize(catalog); undo = null; footerActions.Children.Clear();
            foreach (string id in new[] { "C20", "C31", "C44", "K38", "K42" })
            {
                var trade = catalog.Trades.First(t => t.Id == id); state.Targets[id] = 1; Calculator.SetSelected(state, trade, true);
            }
            Width = 1450; Height = 940; ShowSummary(); Persist(); PumpProcurementInteractionLayout();
            OpenProcurementDetail(detailName);
            procurementDialog.WindowStartupLocation = WindowStartupLocation.Manual;
            procurementDialog.Left = -18000; procurementDialog.Top = -18000; procurementDialog.Height = 540;
            PumpProcurementInteractionLayout();
        }

        void AssertSharedCandidatePreviewIsLocal()
        {
            if (procurementSharedChecks.Count == 0 || procurementSharedChecks.Values.Any(c => c.IsChecked != true))
                throw new Exception("Shared candidates must initially be selected.");
            string stateBefore = SharedUiStateSnapshot();
            // FilePath has already been constrained to the temporary test directory.
            string storedBefore = File.ReadAllText(store.FilePath);
            foreach (var check in procurementSharedChecks.Values) check.IsChecked = false;
            if (procurementSharedApplyButton.IsEnabled || procurementSharedPreviewChanges.Count != 0)
                throw new Exception("An empty shared selection still enables application.");
            CheckBox silien;
            if (!procurementSharedChecks.TryGetValue("실리엔", out silien)) throw new Exception("The expected Silien candidate is missing.");
            silien.IsChecked = true;
            if (!procurementSharedApplyButton.IsEnabled || procurementSharedPreviewChanges.Count == 0 ||
                procurementSharedChecks.Any(p => p.Key != "실리엔" && p.Value.IsChecked == true))
                throw new Exception("Selecting only Silien did not produce a partial shared preview.");
            if (stateBefore != SharedUiStateSnapshot() || storedBefore != File.ReadAllText(store.FilePath))
                throw new Exception("Changing shared candidate checks mutated the current or persisted plan before confirmation.");
            if (procurementSharedPreviewChanges.Any(c => c.Name == "힐웬" || c.Name == "힐웬 광석 조각"))
                throw new Exception("The Silien-only preview includes an unchecked Hillwen candidate.");
        }

        void InvokeProcurementSharedModal(Button trigger, bool requireDialog, Action<Window> inspectAndChoose)
        {
            Exception callbackError = null;
            bool observed = false;
            // The queued action runs inside ShowDialog's nested dispatcher frame.
            // Every callback exit closes the modal, including assertion failures.
            var pending = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
                var dialog = procurementSharedDialog;
                if (dialog == null) return;
                observed = true;
                try
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -18000; dialog.Top = -18000;
                    dialog.UpdateLayout(); inspectAndChoose(dialog);
                }
                catch (Exception ex) { callbackError = ex; }
                finally { if (procurementSharedDialog != null && dialog.IsVisible) dialog.Close(); }
            }));
            try { trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally
            {
                PumpProcurementInteractionLayout();
                if (pending.Status == DispatcherOperationStatus.Pending) pending.Abort();
                if (procurementSharedDialog != null) procurementSharedDialog.Close();
            }
            if (callbackError != null) throw new Exception("Shared modal UI assertion failed.", callbackError);
            if (requireDialog && !observed) throw new Exception("The real direct button did not open its expected shared prompt.");
        }

        void SaveProcurementSharedContentBitmap(Window dialog, string path, double width, double height)
        {
            dialog.Width = width; dialog.Height = height; dialog.UpdateLayout();
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
            dialog.UpdateLayout();
            var visual = (FrameworkElement)dialog.Content;
            foreach (var button in new[] { procurementSharedOnlyButton, procurementSharedApplyButton })
            {
                var point = button.TranslatePoint(new Point(), visual);
                if (!button.IsVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0 ||
                    point.X < -1 || point.Y < -1 || point.X + button.ActualWidth > visual.ActualWidth + 1 ||
                    point.Y + button.ActualHeight > visual.ActualHeight + 1)
                    throw new Exception("A shared prompt footer button is clipped at " + width + "x" + height + ".");
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
        }

        string SharedUiStateSnapshot() { return SharedUiSerialize(state); }
        static string SharedUiSerialize(object value) { return new JavaScriptSerializer().Serialize(value); }
    }
}
