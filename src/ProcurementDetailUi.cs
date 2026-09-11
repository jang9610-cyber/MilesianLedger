using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        sealed class ProcurementRowView
        {
            public string Key, Name, LastChoice, ChildrenSignature;
            public int Depth;
            public bool Seed, Included, Updating, ChildrenCreated;
            public decimal Demand;
            public HashSet<string> Ancestors;
            public Border Frame;
            public TextBlock Quantity, Price, Shared, PreviewLabel, Production, Note;
            public Button Buy, Craft, Acquire;
            public Expander Expander;
            public ComboBox RecipeSelect;
            public StackPanel Branch, ChildPanel;
            public ProcurementRecipe Recipe;
            public List<ProcurementRowView> Children = new List<ProcurementRowView>();
        }
        sealed class ProcurementPosition
        {
            public ScrollViewer Scroll;
            public FrameworkElement Anchor;
            public double Offset, AnchorY;
        }
        StackPanel procurementDetailBody;
        ProcurementRowView procurementRootView;
        Button procurementDetailRefreshButton;
        Button procurementDetailAllRefreshButton;
        readonly Dictionary<string, bool> procurementExpansionStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        Dictionary<string, ProcurementNode> procurementActiveNodes = new Dictionary<string, ProcurementNode>(StringComparer.Ordinal);
        int procurementDetailScrollRevision;

        void OpenProcurementDetail(string name)
        {
            if (procurementDialog == null)
            {
                procurementDetailName = name; procurementExpansionStates.Clear(); procurementRootView = null;
                ++procurementDetailScrollRevision;
                procurementDialog = new Window { Owner = this, Width = 820, Height = Math.Min(790, SystemParameters.WorkArea.Height - 45), MinWidth = 650, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BackgroundColor, FontFamily = FontFamily, ShowInTaskbar = false, UseLayoutRounding = true };
                AttachProcurementWindowLifecycle(procurementDialog);
                procurementDialog.Closed += delegate {
                    ++procurementDetailScrollRevision; procurementDialog = null; procurementDetailScroll = null; procurementDetailBody = null;
                    procurementRootView = null; procurementDetailRefreshButton = null; procurementDetailAllRefreshButton = null; procurementDetailQuoteNames.Clear();
                };
                BuildProcurementDetailShell(); RenderProcurementDetail(false); procurementDialog.Show();
                if (procurementDetailScroll != null) procurementDetailScroll.ScrollToTop();
            }
            else
            {
                if (!String.Equals(procurementDetailName, name, StringComparison.Ordinal))
                {
                    AppMotion.Transition(procurementDetailBody, delegate {
                        procurementDetailName = name; procurementExpansionStates.Clear(); procurementRootView = null;
                        ++procurementDetailScrollRevision;
                        RenderProcurementDetail(false);
                        if (procurementDetailScroll != null) procurementDetailScroll.ScrollToTop();
                    });
                }
                procurementDialog.Activate();
            }
        }

        void BuildProcurementDetailShell()
        {
            var outer = new Grid { Background = BackgroundColor };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); outer.RowDefinitions.Add(new RowDefinition()); outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var intro = new StackPanel { Margin = new Thickness(22, 20, 22, 14) };
            intro.Children.Add(T("어떻게 준비할까요?", 22, Ink, true));
            var hint = T("각 재료에서 준비 방식을 고르세요. 상위 품목을 구매하면 하위 재료는 미리보기이며 합산하지 않습니다.", 11, Muted, false); hint.Margin = new Thickness(0, 7, 0, 0); intro.Children.Add(hint); outer.Children.Add(intro);
            procurementDetailBody = new StackPanel { Margin = new Thickness(22, 2, 22, 18) };
            procurementDetailScroll = new ScrollViewer { Content = procurementDetailBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(procurementDetailScroll, 1); outer.Children.Add(procurementDetailScroll);
            var footer = new Grid { Margin = new Thickness(22, 12, 22, 18) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(T("가격은 갱신 버튼을 누를 때만 조회합니다.\n제작은 성공 기준이며 실패 소모량은 포함하지 않습니다.", 10, Muted, false));
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            procurementDetailAllRefreshButton = Btn("전체 시세 갱신", delegate { auctionRefreshTask = RefreshAuctionFromButtonAsync(true); }, false);
            procurementDetailAllRefreshButton.ToolTip = auctionAllRefreshButton.ToolTip; actions.Children.Add(procurementDetailAllRefreshButton);
            procurementDetailRefreshButton = Btn("구매품목만 갱신", delegate { auctionRefreshTask = RefreshAuctionFromButtonAsync(false); }, false);
            procurementDetailRefreshButton.ToolTip = auctionRefreshButton.ToolTip; actions.Children.Add(procurementDetailRefreshButton);
            var detailWindow = procurementDialog;
            var close = Btn("완료", delegate { detailWindow.Close(); }, true); close.Margin = new Thickness(4, 0, 0, 0); actions.Children.Add(close);
            AutomationProperties.SetName(close, "조달 상세 완료");
            Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); outer.Children.Add(footer);
            procurementDialog.Content = CreateAuctionLayerHost(outer);
        }

        void RenderProcurementDetail(bool preservePosition = true)
        {
            if (procurementDialog == null) return;
            var position = preservePosition ? CaptureProcurementPosition(null) : null;
            procurementDialog.Title = procurementDetailName + " · 재료와 준비 방식";
            procurementActiveNodes = procurementPlan.Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
            ProcurementNode active;
            procurementDetailQuoteNames.Clear();
            if (!procurementActiveNodes.TryGetValue(procurementDetailName, out active))
            {
                procurementRootView = null; procurementDetailBody.Children.Clear();
                procurementDetailBody.Children.Add(T("현재 계획에 포함되지 않은 재료입니다. 교환 재료 카드를 다시 선택하세요.", 13, Muted, false));
            }
            else
            {
                // Quote scope is data-driven: folded visual branches are still
                // available to the explicit comparison refresh, without creating UI.
                CollectProcurementPreviewNames(active.Name, active.Quantity, 0, true, new HashSet<string>(), false);
                if (procurementRootView == null || procurementRootView.Name != active.Name)
                {
                    procurementRootView = CreateProcurementRow(active.Name, "root:" + active.Name, 0, false);
                    procurementDetailBody.Children.Clear(); procurementDetailBody.Children.Add(procurementRootView.Frame);
                }
                UpdateProcurementRow(procurementRootView, active.Quantity, true, new HashSet<string>());
            }
            UpdateAuctionRefreshButtons();
            if (position != null) RestoreProcurementPosition(position);
        }

        void CollectProcurementPreviewNames(string name, decimal demand, int depth, bool included, HashSet<string> ancestors, bool seed)
        {
            if (!procurementPlanner.IsNpcPurchase(name)) procurementDetailQuoteNames.Add(name);
            if (seed || depth >= 12 || ancestors.Contains(name)) return;
            var recipes = procurementPlanner.GetRecipes(name); if (recipes.Count == 0) return;
            string choice = procurementPlanner.GetChoice(state, name);
            var recipe = recipes.FirstOrDefault(r => r.Id == choice) ?? recipes[0];
            ProcurementNode active;
            decimal quantity = included && procurementActiveNodes.TryGetValue(name, out active) ? active.Quantity : demand;
            var preview = procurementPlanner.Preview(state, name, quantity, recipe.Id);
            var next = new HashSet<string>(ancestors); next.Add(name);
            foreach (var child in preview.Children)
                CollectProcurementPreviewNames(child.Name, child.Quantity, depth + 1, included && choice != "buy" && choice != "acquire", next, child.IsSeed);
        }

        ProcurementRowView CreateProcurementRow(string name, string key, int depth, bool seed)
        {
            var row = new ProcurementRowView { Name = name, Key = key, Depth = depth, Seed = seed };
            var panel = new StackPanel();
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(ItemLabel(name, T(name + (seed ? " · 증식 시작용" : ""), depth == 0 ? 17 : 13, Ink, true), depth == 0 ? 36 : 26));
            row.Quantity = T("", 14, Green, true); row.Quantity.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(row.Quantity, 1); heading.Children.Add(row.Quantity); panel.Children.Add(heading);
            row.Price = T("", 11, Muted, false); row.Price.Margin = new Thickness(0, 8, 0, 9); panel.Children.Add(row.Price);
            row.Shared = T("", 10, Green, true); row.Shared.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(row.Shared);
            var recipes = procurementPlanner.GetRecipes(name);
            bool npc = procurementPlanner.IsNpcPurchase(name);
            if (npc) { var label = T("NPC 구매 전용", 11, Green, true); label.Margin = new Thickness(0, 0, 0, 7); panel.Children.Add(label); }
            if (procurementPlanner.IsPurchaseOnly(name)) { var label = T("경매장 구매 전용", 11, Green, true); label.Margin = new Thickness(0, 0, 0, 7); panel.Children.Add(label); }
            if (!seed && recipes.Any(r => r.IsReproduction)) { var label = T("추천: 아라트의 결정 + 축복의 포션으로 합성 괴 불리기", 11, B("#926521"), true); label.Margin = new Thickness(0, 0, 0, 7); panel.Children.Add(label); }
            row.PreviewLabel = T("제작 미리보기 · 현재 합계에서 제외", 10, B("#916020"), true); row.PreviewLabel.Margin = new Thickness(0, 0, 0, 7); panel.Children.Add(row.PreviewLabel);
            var modes = new WrapPanel();
            if (!npc) { row.Buy = ProcurementModeButton(name, "경매장 구매", delegate { SetProcurementChoice(name, "buy", row.Buy); }); row.Buy.IsEnabled = !seed; modes.Children.Add(row.Buy); }
            if (!seed && recipes.Count > 0)
            {
                row.Craft = ProcurementModeButton(name, "직접 제작", delegate { RequestProcurementChoice(name, row.Recipe.Id, row.Craft); }); modes.Children.Add(row.Craft);
                row.Craft.ToolTip = "공통 재료가 있으면 함께 제작할 품목을 확인합니다. 이미 선택된 버튼을 다시 눌러도 확인할 수 있습니다.";
            }
            else if (!seed && procurementPlanner.CanAcquire(name))
            {
                row.Acquire = ProcurementModeButton(name, "직접 확보", delegate { RequestProcurementChoice(name, "acquire", row.Acquire); });
                row.Acquire.ToolTip = "채집·NPC 구매·보유분 등으로 준비합니다. 경매장 구매액에서는 제외합니다."; modes.Children.Add(row.Acquire);
            }
            panel.Children.Add(modes);
            if (seed) panel.Children.Add(T("첫 합성을 시작할 괴입니다. 이후에는 생산된 괴를 다시 사용합니다.", 10, Muted, false));
            else if (recipes.Count == 0) panel.Children.Add(T(npc ? "NPC 상점에서 구매합니다. 판매처는 ? 안내에서 확인하세요. 상점 비용은 경매장 예상 금액에 합산하지 않습니다." : procurementPlanner.IsPurchaseOnly(name) ? "완제품을 경매장에서 구매합니다. 하위 제작 재료는 합산하지 않습니다." : "제작식이 없는 재료입니다. 채집·NPC 구매 등은 ?에서 확인하세요.", 10, Muted, false));
            else if (depth < 12)
            {
                row.Expander = new Expander { FontSize = 11, Foreground = Green, Margin = new Thickness(0, 5, 0, 0) };
                row.Expander.Expanded += delegate(object sender, RoutedEventArgs e) {
                    if (e.OriginalSource != row.Expander || row.Updating) return;
                    procurementExpansionStates[row.Key] = true;
                    UpdateProcurementBranch(row);
                };
                row.Expander.Collapsed += delegate(object sender, RoutedEventArgs e) { if (e.OriginalSource == row.Expander && !row.Updating) procurementExpansionStates[row.Key] = false; };
                panel.Children.Add(row.Expander);
            }
            row.Frame = Box(panel, depth == 0 ? AppTheme.Surface : depth % 2 == 1 ? B("#F5F8F5") : AppTheme.Surface, 9, new Thickness(depth == 0 ? 17 : 12));
            row.Frame.BorderThickness = new Thickness(1); row.Frame.Margin = new Thickness(0, depth == 0 ? 0 : 9, 0, 0);
            return row;
        }

        Button ProcurementModeButton(string name, string label, Action action)
        {
            var button = Btn(label, action, false); button.FontSize = 11; button.Padding = new Thickness(10, 6, 10, 6); button.Margin = new Thickness(0, 0, 7, 7);
            AutomationProperties.SetName(button, name + " " + label + " 선택"); return button;
        }
        static void SetProcurementButtonSelected(Button button, bool selected)
        {
            if (button == null) return;
            button.Background = selected ? Green : AppTheme.Surface; button.Foreground = selected ? AppTheme.OnAccent : Ink; button.BorderBrush = selected ? Green : Line;
        }

        void UpdateProcurementRow(ProcurementRowView row, decimal demand, bool included, HashSet<string> ancestors)
        {
            row.Updating = true;
            try
            {
                row.Demand = demand; row.Included = included; row.Ancestors = ancestors;
                string choice = row.Seed ? "buy" : procurementPlanner.GetChoice(state, row.Name);
                var recipes = procurementPlanner.GetRecipes(row.Name); row.Recipe = recipes.FirstOrDefault(r => r.Id == choice) ?? recipes.FirstOrDefault();
                row.Quantity.Text = Calculator.FormatQuantity(demand) + "개";
                bool npc = procurementPlanner.IsNpcPurchase(row.Name);
                var quote = npc ? null : auction.GetQuote(row.Name);
                row.Price.Text = npc ? "NPC 구매 · 경매장 합산 0 G" : quote != null && quote.UnitPrice.HasValue ? "경매장 개당 " + Gold(quote.UnitPrice.Value) + " · " + Calculator.FormatQuantity(demand) + "개 구매 시 " + Gold(quote.UnitPrice.Value * demand) : "경매장 " + QuoteStatus(quote);
                row.Price.ToolTip = npc ? "NPC 상점 비용은 경매장 예상 금액에서 제외합니다." : QuoteTooltip(quote);
                ProcurementNode active;
                decimal productionDemand = included && !row.Seed && procurementActiveNodes.TryGetValue(row.Name, out active) ? active.Quantity : demand;
                row.Shared.Visibility = productionDemand != demand ? Visibility.Visible : Visibility.Collapsed;
                row.Shared.Text = productionDemand != demand ? "이 제작에 " + Calculator.FormatQuantity(demand) + "개 · 전체 계획 " + Calculator.FormatQuantity(productionDemand) + "개를 모아 제작" : "";
                row.PreviewLabel.Visibility = included ? Visibility.Collapsed : Visibility.Visible;
                if (row.LastChoice != choice)
                {
                    SetProcurementButtonSelected(row.Buy, choice == "buy"); SetProcurementButtonSelected(row.Craft, choice != "buy" && choice != "acquire"); SetProcurementButtonSelected(row.Acquire, choice == "acquire"); row.LastChoice = choice;
                }
                var color = included ? Line : B("#E7DDCD"); if (row.Frame.BorderBrush != color) row.Frame.BorderBrush = color;
                if (row.Expander != null && row.Recipe != null && !ancestors.Contains(row.Name))
                {
                    bool expanded;
                    if (!procurementExpansionStates.TryGetValue(row.Key, out expanded)) expanded = row.Depth == 0 || (included && choice != "buy" && choice != "acquire");
                    row.Expander.Header = "하위 재료 " + row.Recipe.Ingredients.Count + "종 보기";
                    row.Expander.IsExpanded = expanded;
                    if (expanded) UpdateProcurementBranch(row);
                }
            }
            finally { row.Updating = false; }
        }

        void UpdateProcurementBranch(ProcurementRowView row)
        {
            if (row.Recipe == null || row.Ancestors.Contains(row.Name)) return;
            bool wasUpdating = row.Updating; row.Updating = true;
            try
            {
                if (!row.ChildrenCreated)
                {
                    row.Branch = new StackPanel { Margin = new Thickness(row.Depth == 0 ? 0 : 5, 9, 0, 0) };
                    var recipes = procurementPlanner.GetRecipes(row.Name);
                    if (recipes.Count > 1)
                    {
                        row.RecipeSelect = new ComboBox { ItemsSource = recipes, DisplayMemberPath = "Name", SelectedValuePath = "Id", MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 0, 0, 8) };
                        AppMotion.Dropdown(row.RecipeSelect);
                        row.RecipeSelect.SelectionChanged += delegate { if (!row.Updating) { var recipe = row.RecipeSelect.SelectedItem as ProcurementRecipe; if (recipe != null) SetProcurementChoice(row.Name, recipe.Id, row.RecipeSelect); } };
                        row.Branch.Children.Add(row.RecipeSelect);
                    }
                    row.Production = T("", 11, Green, true); row.Branch.Children.Add(row.Production);
                    row.Note = T("", 10, Muted, false); row.Note.Margin = new Thickness(0, 5, 0, 7); row.Branch.Children.Add(row.Note);
                    row.ChildPanel = new StackPanel(); row.Branch.Children.Add(row.ChildPanel); row.Expander.Content = row.Branch; row.ChildrenCreated = true;
                }
                if (row.RecipeSelect != null) row.RecipeSelect.SelectedValue = row.Recipe.Id;
                ProcurementNode active;
                decimal quantity = row.Included && procurementActiveNodes.TryGetValue(row.Name, out active) ? active.Quantity : row.Demand;
                var preview = procurementPlanner.Preview(state, row.Name, quantity, row.Recipe.Id);
                row.Production.Text = "이 필요량 기준 " + Calculator.FormatQuantity(preview.Batches) + "회 제작 → " + Calculator.FormatQuantity(preview.ProducedQuantity) + "개 / 잔여 " + Calculator.FormatQuantity(preview.Surplus) + "개";
                row.Note.Text = row.Recipe.Note ?? ""; row.Note.Visibility = String.IsNullOrEmpty(row.Note.Text) ? Visibility.Collapsed : Visibility.Visible;
                string signature = String.Join("|", preview.Children.Select(c => c.Name + (c.IsSeed ? ":seed" : "")));
                var next = new HashSet<string>(row.Ancestors); next.Add(row.Name);
                bool childIncluded = row.Included && row.LastChoice != "buy" && row.LastChoice != "acquire";
                Action updateChildren = delegate {
                    for (int i = 0; i < row.Children.Count; i++) UpdateProcurementRow(row.Children[i], preview.Children[i].Quantity, childIncluded, next);
                };
                if (row.ChildrenSignature != signature)
                {
                    AppMotion.Transition(row.ChildPanel, delegate {
                        row.Children.Clear(); row.ChildPanel.Children.Clear(); row.ChildrenSignature = signature;
                        foreach (var child in preview.Children)
                        {
                            var view = CreateProcurementRow(child.Name, row.Key + "/" + child.Name + (child.IsSeed ? ":seed" : ""), row.Depth + 1, child.IsSeed);
                            row.Children.Add(view); row.ChildPanel.Children.Add(view.Frame);
                        }
                        updateChildren();
                    });
                }
                else updateChildren();
            }
            finally { row.Updating = wasUpdating; }
        }

        IEnumerable<ProcurementRowView> VisibleProcurementRows(ProcurementRowView row)
        {
            if (row == null) yield break;
            yield return row;
            if (row.Expander != null && row.Expander.IsExpanded)
                foreach (var child in row.Children) foreach (var descendant in VisibleProcurementRows(child)) yield return descendant;
        }
        ProcurementPosition CaptureProcurementPosition(FrameworkElement anchor)
        {
            var scroll = procurementDetailScroll; if (scroll == null) return null;
            if (anchor == null) anchor = Keyboard.FocusedElement as FrameworkElement;
            if (anchor == null || !anchor.IsDescendantOf(scroll) || !anchor.IsVisible)
                anchor = VisibleProcurementRows(procurementRootView).Select(r => (FrameworkElement)r.Frame).FirstOrDefault(frame => {
                    if (!frame.IsDescendantOf(scroll) || !frame.IsVisible) return false;
                    double y = frame.TranslatePoint(new Point(), scroll).Y; return y >= -1 && y < scroll.ViewportHeight;
                });
            return new ProcurementPosition { Scroll = scroll, Offset = scroll.VerticalOffset, Anchor = anchor,
                AnchorY = anchor == null ? 0 : anchor.TranslatePoint(new Point(), scroll).Y };
        }
        void RestoreProcurementPosition(ProcurementPosition position)
        {
            if (position == null) return;
            int revision = ++procurementDetailScrollRevision;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                if (revision != procurementDetailScrollRevision || position.Scroll != procurementDetailScroll || procurementDialog == null) return;
                var scroll = position.Scroll;
                double offset = position.Offset;
                if (position.Anchor != null && position.Anchor.IsVisible && position.Anchor.IsDescendantOf(scroll))
                    offset = scroll.VerticalOffset + position.Anchor.TranslatePoint(new Point(), scroll).Y - position.AnchorY;
                scroll.ScrollToVerticalOffset(Math.Max(0, offset));
            }));
        }
    }
}
