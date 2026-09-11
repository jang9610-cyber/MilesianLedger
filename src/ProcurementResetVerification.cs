using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        public string RunProcurementResetUiChecks(Func<int> requests, string directory)
        {
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Procurement reset checks require an offline service and a temporary state directory.");
            int requestBefore = requests(); var original = StateStore.Copy(state); var originalUndo = undo;
            int originalTab = summaryTab; string originalQuery = summaryQuery;
            var json = new JavaScriptSerializer();
            try
            {
                if (procurementDialog != null) procurementDialog.Close();
                state = new ProgressState(); state.Normalize(catalog);
                foreach (string id in new[] { "C6", "C20" })
                {
                    var trade = catalog.Trades.Single(t => t.Id == id); state.Targets[id] = 2; Calculator.SetSelected(state, trade, true);
                }
                procurementPlanner.SetChoice(state, "매듭끈", "default");
                procurementPlanner.SetChoice(state, "가는 실뭉치", "default");
                procurementPlanner.SetChoice(state, "거미줄", "acquire");
                procurementPlanner.SetChoice(state, "금괴", "synthesis"); // Stored but currently inactive.
                state.ProcurementChoices["스태미나 500 포션"] = "legacy-invalid-choice"; // Actual policy still buy.
                string checkId = catalog.Trades.Single(t => t.Id == "C6").Groups.SelectMany(g => g.Lines).First().CheckId;
                state.Checks[checkId] = true;
                procurementPlan = procurementPlanner.Build(state);
                foreach (var step in ProcurementReadiness.GetSteps(procurementPlan, state).Where(s => s.Name == "매듭끈" || s.Name == "거미줄" || s.Name == "스태미나 500 포션"))
                    ProcurementReadiness.SetReady(state, step, true);
                state.ProcurementReady["craft:금괴"] = new ProcurementReadySnapshot { Name = "금괴", Kind = "craft", Quantity = 10, Context = "inactive-test-context" };
                state.ProcurementReady["purchase:종이"] = new ProcurementReadySnapshot { Name = "종이", Kind = "purchase", Quantity = 99, Context = "purchase" };
                undo = StateStore.Copy(state); footerActions.Children.Clear(); footerActions.Children.Add(Btn("시험용 주간 리셋 되돌리기", delegate { }, false));
                ShowSummary(); Persist(); PumpProcurementInteractionLayout(); OpenProcurementDetail("매듭끈");
                procurementDialog.WindowStartupLocation = WindowStartupLocation.Manual; procurementDialog.Left = -18000; procurementDialog.Top = -18000; procurementDialog.Height = 540;
                ExpandProcurementInteractionTree(); PumpProcurementInteractionLayout();
                var detailWindow = procurementDialog; var detailScroller = procurementDetailScroll; var mainScroller = procurementSummaryScroll;
                string initial = json.Serialize(state); string storedInitial = File.ReadAllText(store.FilePath);
                string selectedInitial = json.Serialize(state.Selected), targetsInitial = json.Serialize(state.Targets), checksInitial = json.Serialize(state.Checks);
                string settingsInitial = json.Serialize(auctionSettings);
                var cachedNames = procurementPlanner.Build(state).QuoteNames.Concat(new[] { "금괴", "설탕" }).Distinct().ToList();
                string quotesInitial = json.Serialize(cachedNames.Select(auction.GetQuote).ToList());

                InvokeProcurementResetModal(delegate(Window dialog) {
                    if (procurementResetCancelButton.IsCancel || procurementResetApplyButton.IsDefault || procurementResetApplyButton.IsCancel)
                        throw new Exception("Reset warning must not use implicit cancel/default window commands.");
                    if (!Object.ReferenceEquals(FocusManager.GetFocusedElement(dialog), procurementResetCancelButton))
                        throw new Exception("Cancellation is not the initially focused reset action.");
                    if (initial != json.Serialize(state) || storedInitial != File.ReadAllText(store.FilePath))
                        throw new Exception("Opening the reset warning changed state before confirmation.");
                    SaveProcurementResetWarning(dialog, Path.Combine(directory, "procurement-reset-warning.png"));
                    procurementResetCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                AssertProcurementResetCancelled(initial, storedInitial, detailWindow);
                InvokeProcurementResetModal(delegate(Window dialog) { dialog.Close(); });
                AssertProcurementResetCancelled(initial, storedInitial, detailWindow);
                InvokeProcurementResetModal(delegate(Window dialog) {
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    dialog.RaiseEvent(escape);
                    if (!escape.Handled) throw new Exception("Escape must be explicitly handled by the reset warning.");
                });
                AssertProcurementResetCancelled(initial, storedInitial, detailWindow);

                InvokeProcurementResetModal(delegate(Window dialog) { procurementResetApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
                if (state.ProcurementChoices.Count != 0 || procurementPlanner.GetChoice(state, "금괴") != "buy" || procurementPlan.Crafts.Count != 0 || procurementPlan.Acquisitions.Count != 0)
                    throw new Exception("Confirmed reset did not restore every saved active and inactive choice to buying.");
                if (json.Serialize(state.Selected) != selectedInitial || json.Serialize(state.Targets) != targetsInitial || json.Serialize(state.Checks) != checksInitial)
                    throw new Exception("Reset changed selected trades, target quantities or existing station checks.");
                if (state.ProcurementReady.Values.Any(r => r.Name == "매듭끈" || r.Name == "거미줄" || r.Name == "금괴") ||
                    !state.ProcurementReady.ContainsKey("purchase:스태미나 500 포션") || !state.ProcurementReady.ContainsKey("purchase:종이"))
                    throw new Exception("Reset did not preserve original-buy completion while clearing just the changed item completions.");
                if (undo != null || footerActions.Children.Count != 0) throw new Exception("Reset retained an unrelated weekly undo action.");
                if (json.Serialize(store.Load(catalog)) != json.Serialize(state)) throw new Exception("Confirmed reset was not persisted.");
                if (json.Serialize(auctionSettings) != settingsInitial || json.Serialize(cachedNames.Select(auction.GetQuote).ToList()) != quotesInitial)
                    throw new Exception("Reset changed auction settings or cached quotes.");
                if (!IsVisible || !detailWindow.IsVisible || !Object.ReferenceEquals(detailWindow, procurementDialog) ||
                    !Object.ReferenceEquals(detailScroller, procurementDetailScroll) || !Object.ReferenceEquals(mainScroller, procurementSummaryScroll))
                    throw new Exception("Reset closed an owner window or replaced a persistent scroll viewer.");
                string resetState = json.Serialize(state), resetStored = File.ReadAllText(store.FilePath);
                ConfirmProcurementReset();
                if (procurementResetDialog != null || resetState != json.Serialize(state) || resetStored != File.ReadAllText(store.FilePath))
                    throw new Exception("Resetting an already default plan opened a prompt or changed state.");
                if (requests() != requestBefore) throw new Exception("Reset warning or application requested auction HTTP.");
                return "PASS procurement reset UI: explicit warning keeps cancel focused; cancellation, title-bar close and Escape preserve state and owner windows; confirmation resets active and inactive choices to buy, invalidates only changed-item completion, preserves selections/targets/station checks/settings/cached prices, persists once, clears weekly undo and retains scroll controls; already-default reset is inert; zero auction HTTP.";
            }
            finally
            {
                if (procurementResetDialog != null) procurementResetDialog.Close();
                if (procurementDialog != null) procurementDialog.Close();
                state = original; undo = originalUndo; summaryTab = originalTab; summaryQuery = originalQuery; Persist(); RenderAll();
            }
        }

        void InvokeProcurementResetModal(Action<Window> inspectAndChoose)
        {
            Exception callbackError = null; bool observed = false;
            var pending = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
                var dialog = procurementResetDialog; if (dialog == null) return;
                observed = true;
                try
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -18000; dialog.Top = -18000;
                    dialog.UpdateLayout(); inspectAndChoose(dialog);
                }
                catch (Exception ex) { callbackError = ex; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }));
            try { AuctionTestChildren<Button>(content).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "구매·제작 선택 초기화").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { PumpProcurementInteractionLayout(); if (pending.Status == DispatcherOperationStatus.Pending) pending.Abort(); }
            if (callbackError != null) throw new Exception("Reset warning assertion failed.", callbackError);
            if (!observed) throw new Exception("Reset command did not open its required confirmation warning.");
        }

        void AssertProcurementResetCancelled(string expectedState, string expectedStored, Window detailWindow)
        {
            if (new JavaScriptSerializer().Serialize(state) != expectedState || File.ReadAllText(store.FilePath) != expectedStored || undo == null)
                throw new Exception("Cancelling reset changed the current plan, saved file or weekly undo.");
            if (!IsVisible || !detailWindow.IsVisible || !Object.ReferenceEquals(detailWindow, procurementDialog))
                throw new Exception("Cancelling reset closed the main window or its detail owner.");
        }

        void SaveProcurementResetWarning(Window dialog, string path)
        {
            dialog.UpdateLayout(); var content = (FrameworkElement)dialog.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
