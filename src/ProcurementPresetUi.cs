using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        readonly Dictionary<int, Button> procurementPresetButtons = new Dictionary<int, Button>();
        TextBlock procurementPresetStatus;
        Window procurementPresetDialog;
        Button procurementPresetCancelButton, procurementPresetApplyButton;

        FrameworkElement BuildProcurementPresetControls()
        {
            var body = new StackPanel();
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var title = T("준비 방식 프리셋", 11, Ink, true); title.Margin = new Thickness(0, 0, 15, 0); row.Children.Add(title);
            var numbers = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 1; i <= ProcurementPresets.SlotCount; i++)
            {
                int slot = i;
                var button = Btn(slot.ToString(), delegate { SelectProcurementPreset(slot); }, false);
                button.Content = new TextBlock { Text = slot.ToString(), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                button.Width = 34; button.Height = 30; button.Padding = new Thickness(0); button.Margin = new Thickness(0, 0, 6, 0);
                AutomationProperties.SetName(button, "준비 방식 프리셋 " + slot + "번");
                procurementPresetButtons[slot] = button; numbers.Children.Add(button);
            }
            Grid.SetColumn(numbers, 1); row.Children.Add(numbers);
            procurementPresetStatus = T("", 11, Green, true); procurementPresetStatus.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(procurementPresetStatus, 2); row.Children.Add(procurementPresetStatus); body.Children.Add(row);
            var hint = T("번호를 누르면 저장된 방식으로 바뀝니다. 바꾼 준비 방식은 사용 중인 번호에 자동 저장됩니다.", 10, Muted, false);
            hint.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(hint);
            var box = Box(body, B("#F1F6F3"), 8, new Thickness(12)); box.BorderBrush = Line; box.BorderThickness = new Thickness(1);
            box.Margin = new Thickness(0, 0, 0, 12); RefreshProcurementPresetControls(); return box;
        }

        void RefreshProcurementPresetControls()
        {
            if (state == null || state.ProcurementMethodPresets == null) return;
            foreach (var entry in procurementPresetButtons)
            {
                bool active = entry.Key == state.ActiveProcurementPresetSlot; var button = entry.Value;
                button.Background = active ? Green : AppTheme.Surface; button.BorderBrush = active ? Green : Line;
                button.Foreground = active ? AppTheme.OnAccent : Ink;
                var slot = state.ProcurementMethodPresets.First(p => p.Slot == entry.Key);
                var preview = new ProgressState { ProcurementChoices = slot.Choices };
                var choices = slot.Choices.Keys.Select(name => procurementPlanner.GetChoice(preview, name)).ToList();
                int acquire = choices.Count(choice => choice == "acquire"), craft = choices.Count(choice => choice != "acquire" && choice != "buy");
                button.ToolTip = entry.Key + "번" + (active ? " · 사용 중" : " · 클릭하여 전환") + "\n"
                    + (craft + acquire == 0 ? "기본 경매장 구매 방식" : "직접 제작 " + craft + "종 · 직접 확보 " + acquire + "종 · 나머지는 경매장 구매")
                    + "\nNPC 구매품은 모든 번호에서 유지됩니다.";
                AutomationProperties.SetItemStatus(button, active ? "사용 중" : "");
                AutomationProperties.SetHelpText(button, Convert.ToString(button.ToolTip));
            }
            if (procurementPresetStatus != null) procurementPresetStatus.Text = state.ActiveProcurementPresetSlot + "번 사용 중 · 자동 저장";
        }

        void SelectProcurementPreset(int slot)
        {
            if (slot == state.ActiveProcurementPresetSlot) return;
            if (procurementPresetDialog != null) { procurementPresetDialog.Activate(); return; }
            var changed = new HashSet<string>(ProcurementPresets.ChangedNames(state, procurementPlanner, slot), StringComparer.Ordinal);
            int readyCount = state.ProcurementReady.Values.Count(r => r != null && changed.Contains(r.Name));
            if (readyCount == 0) { ApplyProcurementPreset(slot); return; }
            var position = CaptureProcurementPosition(null);
            var dialog = CreateProcurementPresetDialog(slot, changed.Count, readyCount); procurementPresetDialog = dialog;
            try { dialog.ShowDialog(); }
            finally
            {
                procurementPresetDialog = null; procurementPresetCancelButton = null; procurementPresetApplyButton = null;
                RestoreProcurementPosition(position);
            }
        }

        Window CreateProcurementPresetDialog(int slot, int changedCount, int readyCount)
        {
            var dialog = new Window { Title = "준비 방식 프리셋 변경", Owner = procurementDialog ?? this,
                Width = 580, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = BackgroundColor, FontFamily = FontFamily, FontSize = 12, UseLayoutRounding = true };
            AutomationProperties.SetName(dialog, "준비 방식 프리셋 변경 확인");
            var body = new StackPanel(); body.Children.Add(T(slot + "번 준비 방식으로 바꿀까요?", 22, Ink, true));
            var description = T("현재 방식은 " + state.ActiveProcurementPresetSlot + "번에 보관되고, " + changedCount + "종의 준비 방식이 바뀝니다.", 12, Ink, false);
            description.Margin = new Thickness(0, 14, 0, 14); body.Children.Add(description);
            var warning = T("방식이 바뀌는 재료의 구비 완료 체크 " + readyCount + "건은 해제됩니다.\n그 외 재료의 구비 체크와 교역품 선택·수량은 유지합니다.", 12, B("#985621"), true);
            warning.LineHeight = 22; body.Children.Add(Box(warning, B("#FFF2E4"), 8, new Thickness(14)));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            procurementPresetCancelButton = Btn("취소", delegate { }, false); procurementPresetApplyButton = Btn(slot + "번으로 변경", delegate { }, true);
            procurementPresetApplyButton.Margin = new Thickness(0);
            procurementPresetCancelButton.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; dialog.Close(); };
            procurementPresetApplyButton.Click += delegate(object sender, RoutedEventArgs e) {
                e.Handled = true; procurementPresetApplyButton.IsEnabled = false; ApplyProcurementPreset(slot); dialog.Close();
            };
            buttons.Children.Add(procurementPresetCancelButton); buttons.Children.Add(procurementPresetApplyButton); body.Children.Add(buttons);
            dialog.Content = new Border { Child = body, Background = BackgroundColor, Padding = new Thickness(26) };
            AttachProcurementWindowLifecycle(dialog);
            dialog.Loaded += delegate { FocusManager.SetFocusedElement(dialog, procurementPresetCancelButton); procurementPresetCancelButton.Focus(); };
            return dialog;
        }

        void ApplyProcurementPreset(int slot)
        {
            if (slot != state.ActiveProcurementPresetSlot && !SaveProgressCheckpoint("준비 방식 " + slot + "번 적용 전")) return;
            var position = CaptureProcurementPosition(null);
            var changed = ProcurementPresets.Switch(state, procurementPlanner, slot);
            undo = null; footerActions.Children.Clear(); Persist();
            procurementPlan = procurementPlanner.Build(state); RenderStats(procurementPlan);
            if (summaryView) RefreshProcurementSummaryChoice(); else { RenderTradeList(); RenderDetail(); }
            if (procurementDialog != null) RenderProcurementDetail(false);
            RestoreProcurementPosition(position);
            footerMessage.Text = slot + "번 준비 방식을 불러왔습니다. " + changed.Count + "종 변경 · 이후 준비 방식은 " + slot + "번에 자동 저장됩니다.";
        }
    }
}
