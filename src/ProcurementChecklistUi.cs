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
        List<ProcurementStep> procurementReadinessSteps = new List<ProcurementStep>();
        readonly Dictionary<string, CheckBox> procurementReadyControls = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        readonly Dictionary<string, Action<ProcurementStep>> procurementReadyRowUpdates = new Dictionary<string, Action<ProcurementStep>>(StringComparer.Ordinal);
        readonly Dictionary<string, bool> procurementStageExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        readonly List<Action> procurementStageHeaderUpdates = new List<Action>();

        static string ProcurementStageKey(ProcurementStep step) { return step.GroupKey; }
        static string ProcurementStageTitle(ProcurementStep step) { return step.GroupLabel; }
        static string ProcurementPercentText(decimal percent)
        {
            return (percent >= 100 ? 100 : (int)Math.Min(99m, Math.Round(Math.Max(0m, percent)))) + "%";
        }

        void RenderProcurementChecklist()
        {
            procurementReadinessSteps = ProcurementReadiness.GetSteps(procurementPlan, state);
            var all = procurementReadinessSteps.Where(s => summaryTab == 1 ? s.Kind == "purchase" : s.Kind != "purchase").ToList();
            var visible = ItemCategories.OrderSteps(all.Where(s => ProcurementMatches(s.Name)));
            summaryCount.Text = (summaryTab == 1 ? "구매할 품목 " : "제작·확보할 품목 ") + all.Count + "종 · 준비 " + all.Count(s => s.CompletionFraction == 1) + " / " + all.Count + "단계";
            if (!String.IsNullOrWhiteSpace(summaryQuery)) summaryCount.Text += " · 검색 " + visible.Count + "종";
            int groupNumber = 0;
            foreach (var group in visible.GroupBy(ProcurementStageKey))
            {
                var entries = group.ToList(); var first = entries[0]; string groupKey = group.Key;
                string stateKey = summaryTab + "|" + groupKey;
                var body = new StackPanel { Margin = new Thickness(0, 10, 0, 2) };
                foreach (var step in entries) body.Children.Add(ProcurementChecklistRow(step));
                var header = new Grid { MinWidth = 280 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var number = T((++groupNumber).ToString("00"), 11, Green, true); header.Children.Add(number);
                var title = T(ProcurementStageTitle(first), 14, Ink, true); Grid.SetColumn(title, 1); header.Children.Add(title);
                var count = T("", 11, Green, true); count.Margin = new Thickness(14, 0, 10, 0); Grid.SetColumn(count, 2); header.Children.Add(count);
                bool expanded; if (!procurementStageExpanded.TryGetValue(stateKey, out expanded)) expanded = true;
                var expander = new Expander { Header = header, Content = body, IsExpanded = expanded, Foreground = Green, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Background = B("#F2F6F3"), BorderBrush = Line, BorderThickness = new Thickness(1), Tag = "procurement-stage:" + groupKey };
                AutomationProperties.SetName(expander, "procurement-stage:" + groupKey);
                expander.Expanded += delegate(object sender, RoutedEventArgs e) { if (e.OriginalSource == expander) procurementStageExpanded[stateKey] = true; };
                expander.Collapsed += delegate(object sender, RoutedEventArgs e) { if (e.OriginalSource == expander) procurementStageExpanded[stateKey] = false; };
                var keys = new HashSet<string>(entries.Select(s => s.Key), StringComparer.Ordinal);
                Action refreshCount = delegate {
                    int complete = procurementReadinessSteps.Count(s => keys.Contains(s.Key) && s.CompletionFraction == 1);
                    count.Text = complete + " / " + entries.Count + " 충족";
                    expander.Visibility = checklistRemainingOnly && complete == entries.Count ? Visibility.Collapsed : Visibility.Visible;
                };
                procurementStageHeaderUpdates.Add(refreshCount); refreshCount(); summaryRows.Children.Add(expander);
            }
            if (visible.Count == 0) summaryRows.Children.Add(T(all.Count == 0 ? "이 목록에서 준비할 재료가 없습니다." : "검색한 재료가 없습니다.", 13, Muted, false));
            else
            {
                var done = T("남은 품목이 없습니다. 체크를 해제하면 구비한 품목도 볼 수 있습니다.", 12, Green, false);
                summaryRows.Children.Add(done);
                var keys = new HashSet<string>(visible.Select(s => s.Key));
                Action updateEmpty = delegate { done.Visibility = checklistRemainingOnly && !procurementReadinessSteps.Any(s => keys.Contains(s.Key) && s.CompletionFraction < 1m) ? Visibility.Visible : Visibility.Collapsed; };
                procurementStageHeaderUpdates.Add(updateEmpty); updateEmpty();
            }
        }

        FrameworkElement ProcurementChecklistRow(ProcurementStep step)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            var check = new CheckBox { IsChecked = step.IsReady, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Tag = step.Key, ToolTip = "표시된 필요 수량을 모두 구비했으면 체크합니다. 제작품은 제작을 마친 뒤 체크하세요." };
            AutomationProperties.SetName(check, "procurement-ready:" + step.Key);
            check.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; SetProcurementReady(step.Key, check.IsChecked == true); };
            procurementReadyControls[step.Key] = check; grid.Children.Add(check);
            var names = new StackPanel(); var label = T(step.Name, 12, Ink, true); names.Children.Add(ItemLabel(step.Name, label, 25));
            if (step.IsSeed) { var seed = T("합성 시작용 괴", 10, B("#916020"), true); seed.Margin = new Thickness(0, 4, 0, 0); names.Children.Add(seed); }
            else if (!step.IsFinal && step.Node != null && step.Node.DirectQuantity > 0) { var shared = T("다른 제작에도 쓰는 교환 재료", 10, Green, false); shared.Margin = new Thickness(0, 4, 0, 0); names.Children.Add(shared); }
            bool npc = procurementPlanner.IsNpcPurchase(step.Name);
            var open = Btn(npc ? "NPC 구매 안내" : "준비 방식 / 재료 보기", delegate { OpenProcurementDetail(step.Name); }, false); open.Margin = new Thickness(0, 7, 10, 0); open.Padding = new Thickness(7, 5, 7, 5); open.FontSize = 10; open.HorizontalAlignment = HorizontalAlignment.Left; names.Children.Add(open); Grid.SetColumn(names, 1); grid.Children.Add(names);
            var quantity = T(Calculator.FormatQuantity(step.Quantity) + "개", 13, Green, true); quantity.Margin = new Thickness(10, 0, 10, 0); Grid.SetColumn(quantity, 2); grid.Children.Add(quantity);
            var details = new StackPanel();
            if (npc)
            {
                details.Children.Add(T("NPC 구매 · 경매장 합산 0 G", 11, Green, true));
                details.Children.Add(T("판매처는 ? 안내에서 확인", 10, Muted, false));
            }
            else if (step.Kind == "purchase")
            {
                var quote = auction.GetQuote(step.Name); bool priced = quote != null && quote.UnitPrice.HasValue;
                details.Children.Add(T(priced ? "예상 " + Gold(quote.UnitPrice.Value * step.Quantity) : QuoteStatus(quote), 12, priced ? Ink : Muted, true));
                if (priced) details.Children.Add(T("개당 " + Gold(quote.UnitPrice.Value) + " · " + QuoteStatus(quote), 10, Muted, false)); details.ToolTip = QuoteTooltip(quote);
            }
            else if (step.Kind == "craft")
            {
                var node = step.Node;
                details.Children.Add(T(node.Recipe.Name + " · " + Calculator.FormatQuantity(node.Batches) + "회 제작", 11, Ink, true));
                details.Children.Add(T("생산 " + Calculator.FormatQuantity(node.ProducedQuantity) + "개 · 잔여 " + Calculator.FormatQuantity(node.Surplus) + "개", 10, Muted, false));
                details.Children.Add(T(String.Join(" / ", node.Children.Select(c => c.Name + " " + Calculator.FormatQuantity(c.Quantity) + "개")), 10, Muted, false));
            }
            else details.Children.Add(T("직접 확보 · 채집 / NPC 구매 등", 11, Muted, false));
            var readiness = T("", 10, Green, true); readiness.Margin = new Thickness(0, 7, 0, 0); details.Children.Add(readiness);
            Grid.SetColumn(details, 3); grid.Children.Add(details);
            var box = Box(grid, AppTheme.Surface, 8, new Thickness(12)); box.BorderThickness = new Thickness(1); box.BorderBrush = Line; box.Margin = new Thickness(0, 0, 0, 7);
            Action<ProcurementStep> update = current => {
                check.IsChecked = current.IsReady; label.Foreground = current.CompletionFraction == 1 ? Green : Ink;
                box.Visibility = checklistRemainingOnly && current.CompletionFraction == 1m ? Visibility.Collapsed : Visibility.Visible;
                box.Background = current.IsReady ? B("#EAF4ED") : current.IsCovered ? B("#EDF3F8") : AppTheme.Surface;
                check.ToolTip = current.IsCovered ? "상위 품목을 구비하여 이 단계의 준비율이 충족됐습니다. 이 재료를 직접 구비한 기록을 남기려면 별도로 체크하세요." : "표시된 필요 수량을 모두 구비했으면 체크합니다. 상위 제작품을 구비하면 해당 하위 작업도 준비율에 반영됩니다.";
                if (current.IsReady) readiness.Text = "✓ 구비 완료";
                else if (current.IsCovered) readiness.Text = "상위 품목 구비로 충족";
                else if (current.CoveredFraction > 0) readiness.Text = "상위 품목 구비 반영 " + ProcurementPercentText(current.CoveredFraction * 100) + (current.Kind == "craft" ? " · 제작 후 체크" : "");
                else if (current.Kind == "craft" && current.DependencyKeys.Count > 0)
                {
                    int readyInputs = procurementReadinessSteps.Count(s => current.DependencyKeys.Contains(s.Key) && s.CompletionFraction == 1);
                    readiness.Text = "재료 구비 " + readyInputs + " / " + current.DependencyKeys.Count + " · 제작 후 체크";
                }
                else readiness.Text = "필요 수량을 모두 구비하면 체크";
            };
            procurementReadyRowUpdates[step.Key] = update; update(step); return box;
        }

        void SetProcurementReady(string key, bool ready)
        {
            procurementPlan = procurementPlanner.Build(state);
            var step = ProcurementReadiness.GetSteps(procurementPlan, state).FirstOrDefault(s => s.Key == key);
            if (step == null) { RenderStats(procurementPlan); return; }
            ProcurementReadiness.SetReady(state, step, ready); undo = null; footerActions.Children.Clear(); Persist();
            RenderStats(procurementPlan);
            if (summaryView && summaryCount != null)
            {
            foreach (var current in procurementReadinessSteps)
            {
                Action<ProcurementStep> update; if (procurementReadyRowUpdates.TryGetValue(current.Key, out update)) update(current);
            }
            foreach (var refresh in procurementStageHeaderUpdates) refresh();
            var rows = procurementReadinessSteps.Where(s => summaryTab == 1 ? s.Kind == "purchase" : s.Kind != "purchase").ToList();
            if (summaryTab != 0)
            {
                summaryCount.Text = (summaryTab == 1 ? "구매할 품목 " : "제작·확보할 품목 ") + rows.Count + "종 · 준비 " + rows.Count(s => s.CompletionFraction == 1) + " / " + rows.Count + "단계";
                if (!String.IsNullOrWhiteSpace(summaryQuery)) summaryCount.Text += " · 검색 " + rows.Count(s => ProcurementMatches(s.Name)) + "종";
            }
            else foreach (var refresh in procurementCardUpdates.Values) refresh();
            }
            footerMessage.Text = step.Name + " · " + (ready ? "구비 완료로 표시했습니다." : "구비 완료 표시를 해제했습니다.");
        }
    }
}
