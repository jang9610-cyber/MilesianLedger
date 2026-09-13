using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        PipChecklistWindow pipChecklist;
        PipWindowSettings pipSettings;
        string pipSettingsFile;
        DispatcherTimer pipSettingsSave;
        bool pipBehindModal;
        WindowState pipMainRestoreState = WindowState.Normal;

        void InitializePip()
        {
            // Keep presentation preferences beside this profile, separate from progress.
            pipSettingsFile = Path.Combine(Path.GetDirectoryName(store.FilePath), "pip-settings.json");
            pipSettings = PipWindowSettings.Load(pipSettingsFile);
            pipSettingsSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            pipSettingsSave.Tick += delegate { pipSettingsSave.Stop(); SavePipSettings(); };
            PipWindowBehavior.ObserveEnabled(this, RefreshPipInteractivity);
            StateChanged += delegate { if (WindowState != WindowState.Minimized) pipMainRestoreState = WindowState; };
        }

        void ShowPipChecklist()
        {
            if (procurementMainClosing) return;
            // A number edit can be saved before the delayed main-screen refresh.
            RenderStats();
            if (pipChecklist != null) { PipWindowSettings.EnsureVisible(pipChecklist); RefreshPipChecklist(); if (!pipBehindModal) PipWindowBehavior.BringForward(pipChecklist); return; }
            var pip = new PipChecklistWindow(name => itemIcons.Get(name), SetPipReady, OpenMainFromPip);
            var marketConfig = AuctionProxyConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "auction-proxy.json"));
            if (marketConfig.IsConfigured) {
                var marketClient = MarketSnapshotClient.ForBaseUri(marketConfig.BaseUri);
                pip.ConfigureMarketSearch(() => marketClient.ReadCachedData(), token => marketClient.RefreshAsync(token), null);
            } else pip.ConfigureMarketSearch(null, null, marketConfig.StatusMessage);
            pip.AcquisitionContent = name => BuildPipAcquisitionContent(name);
            pipChecklist = pip;
            pipBehindModal = false;
            pip.Icon = Icon;
            pipSettings.Apply(pip);
            pip.SelectedTab = pipSettings.Tab;
            pip.RemainingOnly = checklistRemainingOnly;
            pip.RemainingOnlyChanged = SetChecklistRemainingOnly;
            pip.TabChanged = delegate { QueuePipSettingsSave(); };
            pip.LocationChanged += delegate { QueuePipSettingsSave(); };
            pip.SizeChanged += delegate { QueuePipSettingsSave(); };
            pip.Closing += delegate { pipSettingsSave.Stop(); SavePipSettings(); };
            pip.Closed += delegate { if (pipChecklist == pip) pipChecklist = null; };
            RefreshPipChecklist();
            pip.Show();
            PipWindowBehavior.BringForward(pip);
            RefreshPipInteractivity();
        }

        void RefreshPipChecklist()
        {
            if (pipChecklist == null || procurementMainClosing) return;
            var steps = ProcurementReadiness.GetSteps(procurementPlan, state);
            pipChecklist.UpdateSteps(steps, ProcurementReadiness.GetStatus(steps).Percent, DescribePipStep);
            RefreshPipInteractivity();
        }

        string DescribePipStep(ProcurementStep step)
        {
            if (procurementPlanner.IsNpcPurchase(step.Name)) return "NPC 구매 · 비용 제외";
            if (step.Kind == "purchase")
            {
                var quote = auction.GetQuote(step.Name);
                return quote != null && quote.UnitPrice.HasValue
                    ? "예상 " + Gold(quote.UnitPrice.Value * step.Quantity) + " · 개당 " + Gold(quote.UnitPrice.Value)
                    : "경매장 구매 · " + QuoteStatus(quote);
            }
            if (step.Kind == "craft" && step.Node != null && step.Node.Recipe != null)
                return step.Node.Recipe.Name + " · " + Calculator.FormatQuantity(step.Node.Batches) + "회 제작\n"
                    + String.Join(" / ", step.Node.Children.Select(c => c.Name + " " + Calculator.FormatQuantity(c.Quantity) + "개"));
            return step.IsSeed ? "합성 시작용 괴 · 직접 확보" : "직접 확보 · 채집 / 보유분";
        }

        FrameworkElement BuildPipAcquisitionContent(string name)
        {
            var body = new System.Windows.Controls.StackPanel();
            var heading = T(name, 17, Ink, true); heading.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(heading);
            var item = acquisition.Get(name);
            if (item == null) body.Children.Add(T("획득 정보가 없습니다.", 14, Muted, false));
            else foreach (var method in item.methods ?? new System.Collections.Generic.List<AcquisitionMethod>())
            {
                var label = T(method.kind, 12, Green, true); label.Margin = new Thickness(0, 4, 0, 5); body.Children.Add(label);
                var text = T(method.text, 14, Ink, false); text.LineHeight = 23; text.Margin = new Thickness(0, 0, 0, 10); body.Children.Add(text);
            }
            if (item != null) foreach (var note in item.notes ?? new System.Collections.Generic.List<string>())
            {
                var text = T(note, 12, Muted, false); text.LineHeight = 20; text.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(text);
            }
            return body;
        }

        void SetPipReady(string key, bool ready)
        {
            if (procurementMainClosing) return;
            if (!CanUsePipChecklist()) { RefreshPipChecklist(); return; }
            SetProcurementReady(key, ready);
        }

        bool CanUsePipChecklist()
        {
            return !auctionRefreshing && shell.IsEnabled && PipWindowBehavior.IsNativeEnabled(this);
        }

        void RefreshPipInteractivity()
        {
            if (pipChecklist == null || procurementMainClosing) return;
            bool modal = !PipWindowBehavior.IsNativeEnabled(this);
            if (pipBehindModal != modal)
            {
                pipBehindModal = modal;
                // A disabled topmost PIP must not obscure a modal confirmation.
                PipWindowBehavior.PlaceBehindModal(pipChecklist, modal);
            }
            pipChecklist.SetInteractionBlocked(auctionRefreshing ? "경매장 갱신 중입니다. 완료 후 체크하세요."
                : !CanUsePipChecklist() ? "메인 앱에서 설정을 마친 뒤 체크하세요." : null);
        }

        void OpenMainFromPip()
        {
            if (procurementMainClosing) return;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = pipMainRestoreState;
            // Only this explicit button brings the main app into focus.
            Activate();
        }

        void QueuePipSettingsSave()
        {
            if (pipChecklist == null || procurementMainClosing) return;
            pipSettingsSave.Stop(); pipSettingsSave.Start();
        }

        void SavePipSettings()
        {
            if (pipChecklist == null) return;
            pipSettings.Left = pipChecklist.Left; pipSettings.Top = pipChecklist.Top;
            pipSettings.Width = pipChecklist.Width; pipSettings.Height = pipChecklist.Height;
            pipSettings.Tab = pipChecklist.SelectedTab;
            try { pipSettings.Save(pipSettingsFile); }
            catch (IOException) { footerMessage.Text = "PIP 창 위치를 저장하지 못했습니다. 재료 구비 상태는 별도로 저장됩니다."; }
            catch (UnauthorizedAccessException) { footerMessage.Text = "PIP 창 위치를 저장할 폴더에 접근할 수 없습니다."; }
        }

        void ClosePipChecklist()
        {
            if (pipSettingsSave != null) pipSettingsSave.Stop();
            if (pipChecklist != null) pipChecklist.Close();
        }
    }
}
