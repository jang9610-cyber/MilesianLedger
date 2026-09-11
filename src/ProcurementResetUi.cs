using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        Window procurementResetDialog;
        Button procurementResetCancelButton, procurementResetApplyButton;

        void ConfirmProcurementReset()
        {
            if (procurementResetDialog != null) { procurementResetDialog.Activate(); return; }
            if (state.ProcurementChoices == null || state.ProcurementChoices.Count == 0)
            {
                footerMessage.Text = "구매·제작 선택이 이미 기본값입니다. 초기화할 설정이 없습니다.";
                return;
            }
            var position = CaptureProcurementPosition(null);
            var dialog = CreateProcurementResetDialog(); procurementResetDialog = dialog;
            try { dialog.ShowDialog(); }
            finally
            {
                procurementResetDialog = null; procurementResetCancelButton = null; procurementResetApplyButton = null;
                RestoreProcurementPosition(position);
            }
        }

        Window CreateProcurementResetDialog()
        {
            int savedCount = state.ProcurementChoices == null ? 0 : state.ProcurementChoices.Count;
            var changedNames = new HashSet<string>((state.ProcurementChoices ?? new Dictionary<string, string>()).Keys
                .Where(name => procurementPlanner.GetChoice(state, name) != "buy"), StringComparer.Ordinal);
            int readyCount = state.ProcurementReady == null ? 0 : state.ProcurementReady.Values.Count(r => r != null && changedNames.Contains(r.Name));
            var dialog = new Window { Title = "구매·제작 선택 초기화", Owner = procurementDialog ?? this,
                Width = 610, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = BackgroundColor, FontFamily = FontFamily, FontSize = 12, UseLayoutRounding = true };
            AutomationProperties.SetName(dialog, "준비 방식 초기화 경고");
            var body = new StackPanel();
            body.Children.Add(T("구매·제작 선택을 초기화할까요?", 22, Ink, true));
            var intro = T("저장한 재료별 준비 방식 " + savedCount + "종을 기본값으로 되돌립니다.", 13, Ink, true);
            intro.Margin = new Thickness(0, 15, 0, 12); body.Children.Add(intro);
            var scope = T("직접 제작·직접 확보 선택과 선택한 제작법을 지우고, 경매장 구매로 바꿉니다. 현재 교역에 쓰지 않는 재료의 설정도 포함합니다.", 12, Muted, false);
            scope.LineHeight = 21; body.Children.Add(scope);
            var npc = T("새우·설탕·마늘은 계속 NPC 구매로 표시됩니다.", 11, Muted, false); npc.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(npc);
            var warning = T("준비 방식이 바뀌는 재료의 구비 완료 체크 " + readyCount + "건도 해제됩니다.\n원래 경매장 구매였던 재료의 구비 체크는 유지합니다.", 12, B("#985621"), true);
            warning.LineHeight = 22;
            var warningBox = Box(warning, B("#FFF2E4"), 8, new Thickness(14)); warningBox.Margin = new Thickness(0, 16, 0, 14); body.Children.Add(warningBox);
            var retained = T("교역품 선택·목표 수량·기존 교역소 표 체크·경매장 설정과 저장된 가격은 유지합니다.", 11, Muted, false);
            retained.LineHeight = 20; body.Children.Add(retained);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            procurementResetCancelButton = Btn("취소", delegate { }, false);
            procurementResetApplyButton = Btn("경매장 구매로 되돌리기", delegate { }, true);
            procurementResetCancelButton.Margin = new Thickness(0, 0, 8, 0); procurementResetApplyButton.Margin = new Thickness(0);
            AutomationProperties.SetName(procurementResetCancelButton, "준비 방식 초기화 취소");
            AutomationProperties.SetName(procurementResetApplyButton, "준비 방식 초기화 적용");
            // Explicit handlers avoid dispatching a second dialog-cancel command
            // after Close() has already removed the modal.
            procurementResetCancelButton.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; dialog.Close(); };
            procurementResetApplyButton.Click += delegate(object sender, RoutedEventArgs e) {
                e.Handled = true; procurementResetApplyButton.IsEnabled = false;
                ApplyProcurementReset(); dialog.Close();
            };
            AttachProcurementWindowLifecycle(dialog);
            dialog.Loaded += delegate {
                FocusManager.SetFocusedElement(dialog, procurementResetCancelButton);
                procurementResetCancelButton.Focus(); Keyboard.Focus(procurementResetCancelButton);
            };
            buttons.Children.Add(procurementResetCancelButton); buttons.Children.Add(procurementResetApplyButton); body.Children.Add(buttons);
            dialog.Content = new Border { Padding = new Thickness(26), Background = BackgroundColor, Child = body };
            return dialog;
        }

        void ApplyProcurementReset()
        {
            if (state.ProcurementChoices == null || state.ProcurementChoices.Count == 0) return;
            if (!SaveProgressCheckpoint("구매·제작 선택 초기화 전")) return;
            var position = CaptureProcurementPosition(null);
            var changedNames = state.ProcurementChoices.Keys.Where(name => procurementPlanner.GetChoice(state, name) != "buy").ToList();
            foreach (string name in changedNames) ProcurementReadiness.InvalidateChoice(state, name);
            state.ProcurementChoices.Clear();
            undo = null; footerActions.Children.Clear(); Persist();
            procurementPlan = procurementPlanner.Build(state); RenderStats(procurementPlan);
            if (summaryView) RefreshProcurementSummaryChoice();
            else { RenderTradeList(); RenderDetail(); }
            if (procurementDialog != null) { RenderProcurementDetail(false); RestoreProcurementPosition(position); }
            footerMessage.Text = "구매·제작 선택을 초기화했습니다. " + changedNames.Count + "종의 준비 방식이 기본값으로 돌아갔습니다. 교역품 선택과 목표 수량은 유지됩니다.";
        }
    }
}
