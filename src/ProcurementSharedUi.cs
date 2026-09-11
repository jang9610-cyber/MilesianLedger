using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        bool procurementSharedPromptsEnabled;
        Window procurementSharedDialog;
        readonly Dictionary<string, CheckBox> procurementSharedChecks = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        List<ProcurementSharedSuggestion> procurementSharedSuggestions = new List<ProcurementSharedSuggestion>();
        List<ProcurementChoiceChange> procurementSharedPreviewChanges = new List<ProcurementChoiceChange>();
        Button procurementSharedApplyButton, procurementSharedOnlyButton;

        void RequestProcurementChoice(string name, string choice, FrameworkElement anchor)
        {
            SetProcurementChoice(name, choice, anchor);
            if (!procurementSharedPromptsEnabled || choice == "buy" || procurementSharedDialog != null) return;
            var shared = new ProcurementSharedPlanning(procurementPlanner);
            string source = procurementPlan.Roots.Any(n => n.Name == procurementDetailName) ? procurementDetailName : null;
            var suggestions = shared.Suggest(state, name, choice, source);
            if (suggestions.Count > 0) ShowProcurementSharedPrompt(shared, name, anchor, suggestions);
        }

        string SharedChoiceLabel(string name, string choice)
        {
            if (choice == "buy") return "경매장 구매";
            if (choice == "acquire") return "직접 확보";
            var recipe = procurementPlanner.GetRecipes(name).FirstOrDefault(r => r.Id == choice);
            return recipe == null ? "직접 제작" : "직접 제작 · " + recipe.Name;
        }

        List<ProcurementSharedSuggestion> SelectedSharedSuggestions()
        {
            return procurementSharedSuggestions.Where(s => procurementSharedChecks[s.AnchorName].IsChecked == true).ToList();
        }

        void ShowProcurementSharedPrompt(ProcurementSharedPlanning shared, string name, FrameworkElement anchor, List<ProcurementSharedSuggestion> suggestions)
        {
            procurementSharedSuggestions = suggestions; procurementSharedChecks.Clear();
            var position = CaptureProcurementPosition(anchor);
            var dialog = new Window { Title = "공유 재료 함께 준비", Owner = procurementDialog ?? this,
                Width = 730, Height = Math.Min(770, SystemParameters.WorkArea.Height - 45), MinWidth = 620, MinHeight = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = BackgroundColor, FontFamily = FontFamily, FontSize = 12, UseLayoutRounding = true };
            procurementSharedDialog = dialog;
            AttachProcurementWindowLifecycle(dialog);
            AutomationProperties.SetName(dialog, "공유 재료 함께 준비");
            var outer = new Grid { Background = BackgroundColor };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition());
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var intro = new StackPanel();
            intro.Children.Add(T("공유 재료도 함께 준비할까요?", 22, Ink, true));
            var chosen = T(name + " · " + ProcurementMode(name) + " 적용됨", 12, Green, true);
            chosen.Margin = new Thickness(0, 11, 0, 8); intro.Children.Add(chosen);
            intro.Children.Add(T("아래 공통 재료를 사용하는 품목들도 함께 제작할 수 있습니다. 이번에 선택한 교역품에 필요한 경로를 표시합니다.", 12, Muted, false));
            var guide = T("연결된 중간 재료는 직접 제작, 맨 아래 재료는 직접 확보로 전환합니다. 다른 재료는 현재 준비 방식을 유지합니다.", 11, Muted, false);
            guide.Margin = new Thickness(0, 7, 0, 14); intro.Children.Add(guide); outer.Children.Add(intro);
            var content = new StackPanel();
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 14) };
            Grid.SetRow(scroll, 1); outer.Children.Add(scroll);
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            var previewStatus = T("", 12, Green, true);
            var previewHint = T("", 11, Muted, false); previewHint.Margin = new Thickness(0, 5, 0, 12);
            var changeRows = new StackPanel();
            bool updating = false;
            Action refresh = delegate {
                if (updating) return;
                var copy = StateStore.Copy(state);
                var previewChanges = shared.Apply(copy, SelectedSharedSuggestions());
                var plan = procurementPlanner.Build(copy);
                AppMotion.Transition(changeRows, delegate {
                    procurementSharedPreviewChanges = previewChanges;
                    previewStatus.Text = "추가 전환 " + previewChanges.Count + "종";
                    previewHint.Text = "적용 후 전체 계획: 경매장 구매 " + plan.Purchases.Count + "종 · 직접 제작 " + plan.Crafts.Count + "종 · 직접 확보 " + plan.Acquisitions.Count + "종";
                    procurementSharedApplyButton.IsEnabled = previewChanges.Count > 0;
                    changeRows.Children.Clear();
                    foreach (var change in previewChanges)
                    {
                        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                        row.Children.Add(ItemLabel(change.Name, T(change.Name, 12, Ink, true), 24));
                        var mode = T(SharedChoiceLabel(change.Name, change.BeforeChoice) + " → " + SharedChoiceLabel(change.Name, change.AfterChoice), 11, Muted, false);
                        mode.Margin = new Thickness(34, 4, 0, 0); row.Children.Add(mode); changeRows.Children.Add(row);
                    }
                });
            };
            Action<bool> setAll = value => {
                updating = true;
                foreach (var check in procurementSharedChecks.Values) check.IsChecked = value;
                updating = false; refresh();
            };
            toolbar.Children.Add(Btn("모두 선택", delegate { setAll(true); }, false));
            toolbar.Children.Add(Btn("선택 해제", delegate { setAll(false); }, false));
            content.Children.Add(toolbar);
            foreach (var suggestion in suggestions)
            {
                var card = new StackPanel();
                var check = new CheckBox { IsChecked = true, Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                check.Content = ItemLabel(suggestion.AnchorName, T(suggestion.AnchorName + " 함께 준비", 14, Ink, true), 28);
                AutomationProperties.SetName(check, "공유 준비 선택:" + suggestion.AnchorName);
                procurementSharedChecks[suggestion.AnchorName] = check;
                check.Checked += delegate { refresh(); }; check.Unchecked += delegate { refresh(); };
                card.Children.Add(check);
                var uses = T("사용 품목 " + suggestion.RootNames.Count + "종 · " + String.Join(" · ", suggestion.RootNames), 11, Green, false);
                uses.Margin = new Thickness(21, 8, 0, 5); card.Children.Add(uses);
                var paths = new StackPanel();
                foreach (var path in suggestion.Paths) paths.Children.Add(T(path, 11, Muted, false));
                var pathDetails = new Expander { Header = "연결된 제작 경로 보기", Content = paths, Foreground = Muted, FontSize = 11, Margin = new Thickness(21, 4, 0, 0) };
                card.Children.Add(pathDetails);
                var box = Box(card, AppTheme.Surface, 9, new Thickness(15));
                box.BorderBrush = Line; box.BorderThickness = new Thickness(1); box.Margin = new Thickness(0, 0, 8, 10); content.Children.Add(box);
            }
            var changes = new Expander { Header = "전환되는 항목 자세히 보기", Content = changeRows, Foreground = Green, FontSize = 12, Margin = new Thickness(0, 5, 0, 5) };
            AutomationProperties.SetName(changes, "공유 준비 변경 미리보기"); content.Children.Add(changes);
            var footer = new StackPanel(); footer.Children.Add(previewStatus); footer.Children.Add(previewHint);
            var note = T("구비 완료 체크는 자동으로 켜지지 않습니다. 준비 방식이 바뀐 항목의 기존 완료 표시는 해제됩니다.", 10, Muted, false);
            note.Margin = new Thickness(0, 0, 0, 14); footer.Children.Add(note);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            procurementSharedOnlyButton = Btn("이 항목만 변경", delegate { dialog.Close(); }, false);
            AutomationProperties.SetName(procurementSharedOnlyButton, "공유 준비 없이 닫기");
            procurementSharedApplyButton = Btn("선택한 재료 함께 준비", delegate {
                var applied = shared.Apply(state, SelectedSharedSuggestions());
                if (applied.Count > 0)
                {
                    undo = null; footerActions.Children.Clear(); Persist();
                    procurementPlan = procurementPlanner.Build(state); RenderStats(procurementPlan);
                    if (summaryView) RefreshProcurementSummaryChoice();
                    if (procurementDialog != null) RenderProcurementDetail(false);
                    footerMessage.Text = name + "의 공통 재료를 따라 " + applied.Count + "종의 준비 방식을 함께 변경했습니다.";
                }
                dialog.Close();
            }, true);
            AutomationProperties.SetName(procurementSharedApplyButton, "공유 준비 일괄 적용");
            actions.Children.Add(procurementSharedOnlyButton); actions.Children.Add(procurementSharedApplyButton);
            footer.Children.Add(actions); Grid.SetRow(footer, 2); outer.Children.Add(footer);
            dialog.Content = new Border { Padding = new Thickness(24), Background = BackgroundColor, Child = outer }; refresh();
            try { dialog.ShowDialog(); }
            finally
            {
                procurementSharedDialog = null; procurementSharedChecks.Clear();
                procurementSharedSuggestions = new List<ProcurementSharedSuggestion>();
                procurementSharedPreviewChanges = new List<ProcurementChoiceChange>();
                procurementSharedApplyButton = null; procurementSharedOnlyButton = null;
                RestoreProcurementPosition(position);
            }
        }
    }
}
