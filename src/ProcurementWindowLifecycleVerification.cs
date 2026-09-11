using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        public string RunProcurementWindowLifecycleChecks(Func<int> requests, string directory)
        {
            // The test window is injected with a fake transport and a temporary store.
            // No existing app window, credential, user state or live endpoint is inspected.
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)),
                Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Window lifecycle checks require the offline temporary test window.");
            int before = requests();
            var original = StateStore.Copy(state);
            var originalUndo = undo;
            bool originalPrompts = procurementSharedPromptsEnabled, originalClosing = procurementMainClosing;
            WindowState originalWindowState = WindowState;
            double originalWidth = Width, originalHeight = Height, originalLeft = Left, originalTop = Top;
            int originalTab = summaryTab;
            string originalQuery = summaryQuery;
            int activeReturnChecks = 0;
            try
            {
                if (procurementSharedDialog != null) procurementSharedDialog.Close();
                if (procurementDialog != null) procurementDialog.Close();
                procurementMainClosing = false; procurementSharedPromptsEnabled = false;
                state = new ProgressState(); state.Normalize(catalog);
                var trade = catalog.Trades.First(t => t.Id == "C6");
                state.Targets[trade.Id] = 1; Calculator.SetSelected(state, trade, true);
                WindowState = WindowState.Normal; ShowSummary(); Persist();
                SetProcurementChoice("매듭끈", procurementPlanner.GetRecipes("매듭끈").First().Id);

                foreach (WindowState mode in new[] { WindowState.Normal, WindowState.Maximized })
                {
                    WindowState = mode; PumpProcurementInteractionLayout();
                    var dialog = OpenLifecycleDetail("매듭끈");
                    bool active = ActivateLifecycleDetail(dialog);
                    string snapshot = SharedUiStateSnapshot();
                    string saved = File.ReadAllText(store.FilePath);
                    var done = LifecycleDoneButton();
                    if (done.IsCancel) throw new Exception("The modeless Done button still schedules a second dialog-cancel command.");
                    InvokeLifecycleButton(done);
                    AssertLifecycleOwner(mode, active, "actual Done button");
                    if (active) activeReturnChecks++;
                    AssertLifecycleStateUnchanged(snapshot, saved, "Done");
                }

                WindowState = WindowState.Normal;
                foreach (bool escape in new[] { true, false })
                {
                    var dialog = OpenLifecycleDetail("매듭끈");
                    bool active = ActivateLifecycleDetail(dialog);
                    string snapshot = SharedUiStateSnapshot();
                    string saved = File.ReadAllText(store.FilePath);
                    if (escape)
                    {
                        var source = PresentationSource.FromVisual(dialog);
                        if (source == null) throw new Exception("The modeless Escape test has no presentation source.");
                        var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        dialog.RaiseEvent(key);
                        if (!key.Handled) throw new Exception("Modeless Escape was not handled by the explicit close path.");
                    }
                    else dialog.Close(); // Same Closing/Closed path as the native title-bar X.
                    PumpProcurementInteractionLayout();
                    AssertLifecycleOwner(WindowState.Normal, active, escape ? "Escape" : "title-bar close path");
                    if (active) activeReturnChecks++;
                    AssertLifecycleStateUnchanged(snapshot, saved, escape ? "Escape" : "Close");
                }

                // Shutdown and deliberate minimization must never cause a deferred activation attempt.
                var closingDialog = OpenLifecycleDetail("매듭끈");
                ActivateLifecycleDetail(closingDialog);
                int attempts = procurementOwnerRestoreAttempts;
                string guardState = SharedUiStateSnapshot();
                procurementMainClosing = true;
                try { InvokeLifecycleButton(LifecycleDoneButton()); }
                finally { procurementMainClosing = false; }
                if (procurementOwnerRestoreAttempts != attempts || !IsVisible || WindowState != WindowState.Normal)
                    throw new Exception("Closing during app shutdown attempted to restore or change the main window.");
                if (guardState != SharedUiStateSnapshot()) throw new Exception("The shutdown close guard changed preparation state.");

                var minimizedDialog = OpenLifecycleDetail("매듭끈");
                ActivateLifecycleDetail(minimizedDialog);
                WindowState = WindowState.Minimized; PumpProcurementInteractionLayout();
                attempts = procurementOwnerRestoreAttempts;
                minimizedDialog.Close(); PumpProcurementInteractionLayout();
                if (WindowState != WindowState.Minimized || procurementOwnerRestoreAttempts != attempts)
                    throw new Exception("Closing an owned detail restored an intentionally minimized main window.");
                if (guardState != SharedUiStateSnapshot()) throw new Exception("The intentional-minimize guard changed preparation state.");
                WindowState = WindowState.Normal; PumpProcurementInteractionLayout();

                // Exercise the real Button.OnClick path through the nested modal popup too.
                procurementSharedPromptsEnabled = true;
                foreach (string outcome in new[] { "only", "apply", "close" })
                {
                    BeginProcurementSharedScenario("정화된 토끼의 발");
                    var detailWindow = procurementDialog;
                    bool active = ActivateLifecycleDetail(detailWindow);
                    var craft = ProcurementInteractionButton("정화된 토끼의 발 직접 제작 선택");
                    InvokeLifecycleSharedChoice(craft, outcome, detailWindow);
                    if (procurementSharedDialog != null || procurementDialog != detailWindow || !detailWindow.IsVisible || !detailWindow.IsEnabled)
                        throw new Exception("The nested shared dialog left its detail owner closed or disabled.");
                    if (active && !detailWindow.IsActive)
                        throw new Exception("The nested shared dialog did not return activation to its detail owner.");
                    if (procurementPlanner.GetChoice(state, "정화된 토끼의 발") == "buy")
                        throw new Exception("Closing the shared dialog lost the initial direct crafting choice.");
                    if (outcome == "apply")
                    {
                        if (procurementPlan.Nodes.Single(n => n.Name == "실리엔").Quantity != 18 ||
                            procurementPlan.Acquisitions.Single(n => n.Name == "실리엔 결정").Quantity != 90)
                            throw new Exception("The actual automation Apply button did not perform the shared operation.");
                    }
                    else if (procurementPlanner.GetChoice(state, "실리엔") != "buy")
                        throw new Exception("Cancelling the shared dialog unexpectedly applied common materials.");
                    string snapshot = SharedUiStateSnapshot();
                    string saved = File.ReadAllText(store.FilePath);
                    bool detailActive = detailWindow.IsActive;
                    InvokeLifecycleButton(LifecycleDoneButton());
                    AssertLifecycleOwner(WindowState.Normal, detailActive, "Done after shared " + outcome);
                    if (detailActive) activeReturnChecks++;
                    AssertLifecycleStateUnchanged(snapshot, saved, "Done after shared " + outcome);
                }

                procurementSharedPromptsEnabled = false;
                // Bounded repetition catches stale global dialog references and queued close callbacks.
                for (int i = 0; i < 6; i++)
                {
                    var dialog = OpenLifecycleDetail("정화된 토끼의 발");
                    bool active = ActivateLifecycleDetail(dialog);
                    string snapshot = SharedUiStateSnapshot();
                    InvokeLifecycleButton(LifecycleDoneButton());
                    AssertLifecycleOwner(WindowState.Normal, active, "repeated Done " + i);
                    if (active) activeReturnChecks++;
                    if (snapshot != SharedUiStateSnapshot()) throw new Exception("Repeated detail closure changed preparation state.");
                }
                if (requests() != before) throw new Exception("Window completion, cancellation or owner restoration sent auction HTTP.");
                return "PASS procurement window lifecycle: ButtonAutomationPeer invokes the full Button.OnClick path; Done/Escape/X and shared Apply/Only/close preserve the main window and its Normal/Maximized state; shutdown and intentional minimization suppress restoration; closing preserves prepared state and saved data; repeated reopening is stable; " + activeReturnChecks + " activation-return checks ran where the test window could activate; zero auction HTTP.";
            }
            finally
            {
                procurementMainClosing = true;
                if (procurementSharedDialog != null) procurementSharedDialog.Close();
                if (procurementDialog != null) procurementDialog.Close();
                PumpProcurementInteractionLayout();
                procurementSharedPromptsEnabled = originalPrompts;
                state = original; undo = originalUndo;
                WindowState = WindowState.Normal; Width = originalWidth; Height = originalHeight; Left = originalLeft; Top = originalTop;
                WindowState = originalWindowState;
                summaryTab = originalTab; summaryQuery = originalQuery;
                procurementMainClosing = originalClosing; Persist(); RenderAll();
            }
        }

        Window OpenLifecycleDetail(string name)
        {
            OpenProcurementDetail(name);
            procurementDialog.WindowStartupLocation = WindowStartupLocation.Manual;
            procurementDialog.Left = -18000; procurementDialog.Top = -18000;
            PumpProcurementInteractionLayout();
            return procurementDialog;
        }

        bool ActivateLifecycleDetail(Window dialog)
        {
            // Strong activation assertions are conditional on this desktop allowing activation.
            bool activated = dialog.Activate();
            var done = LifecycleDoneButton(); done.Focus();
            PumpProcurementInteractionLayout();
            return activated && dialog.IsActive;
        }

        Button LifecycleDoneButton()
        {
            return AuctionTestChildren<Button>(procurementDialog)
                .Single(b => AutomationProperties.GetName(b) == "조달 상세 완료");
        }

        void InvokeLifecycleButton(Button button)
        {
            Exception failure = null;
            DispatcherUnhandledExceptionEventHandler catchError = delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                failure = e.Exception; e.Handled = true;
                if (procurementSharedDialog != null) procurementSharedDialog.Close();
            };
            Dispatcher.UnhandledException += catchError;
            try
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(button) as ButtonAutomationPeer ?? new ButtonAutomationPeer(button);
                var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                if (invoke == null) throw new Exception("The actual button has no automation Invoke provider.");
                invoke.Invoke(); // Peer queues Input -> AutomationButtonBaseClick -> virtual OnClick.
                PumpProcurementInteractionLayout();
            }
            finally { Dispatcher.UnhandledException -= catchError; }
            if (failure != null) throw new Exception("The actual Button.OnClick path failed.", failure);
        }

        void InvokeLifecycleSharedChoice(Button trigger, string outcome, Window expectedOwner)
        {
            bool observed = false;
            Exception failure = null;
            var pending = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate {
                var dialog = procurementSharedDialog;
                if (dialog == null) return;
                observed = true;
                try
                {
                    dialog.Left = -18000; dialog.Top = -18000; dialog.UpdateLayout();
                    if (dialog.Owner != expectedOwner) throw new Exception("The shared prompt has the wrong detail owner.");
                    dialog.Activate();
                    if (outcome == "apply")
                    {
                        foreach (var pair in procurementSharedChecks) pair.Value.IsChecked = pair.Key == "실리엔";
                        InvokeLifecycleButton(procurementSharedApplyButton);
                    }
                    else if (outcome == "only")
                    {
                        if (procurementSharedOnlyButton.IsCancel) throw new Exception("Shared cancellation still schedules an extra cancel command after Close.");
                        InvokeLifecycleButton(procurementSharedOnlyButton);
                    }
                    else dialog.Close();
                }
                catch (Exception ex) { failure = ex; }
                finally { if (procurementSharedDialog != null && dialog.IsVisible) dialog.Close(); }
            }));
            try { InvokeLifecycleButton(trigger); }
            finally
            {
                PumpProcurementInteractionLayout();
                if (pending.Status == DispatcherOperationStatus.Pending) pending.Abort();
                if (procurementSharedDialog != null) procurementSharedDialog.Close();
            }
            if (failure != null) throw new Exception("The nested lifecycle modal check failed.", failure);
            if (!observed) throw new Exception("The actual crafting OnClick did not open its shared dialog.");
        }

        void AssertLifecycleOwner(WindowState expectedState, bool shouldBeActive, string action)
        {
            if (procurementDialog != null || !IsVisible || !IsEnabled || WindowState != expectedState)
                throw new Exception(action + " closed, disabled or changed the main window state.");
            if (shouldBeActive && !IsActive) throw new Exception(action + " left the main window behind after an active owned detail closed.");
        }

        void AssertLifecycleStateUnchanged(string snapshot, string saved, string action)
        {
            if (snapshot != SharedUiStateSnapshot() || saved != File.ReadAllText(store.FilePath))
                throw new Exception(action + " changed preparation data while merely closing a window.");
        }
    }
}
