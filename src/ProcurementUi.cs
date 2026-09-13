using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        int summaryTab;
        bool checklistRemainingOnly;
        CheckBox checklistRemainingControl;

        void SetChecklistRemainingOnly(bool value)
        {
            if (checklistRemainingOnly == value) return;
            checklistRemainingOnly = value;
            if (checklistRemainingControl != null) checklistRemainingControl.IsChecked = value;
            if (summaryView && summaryTab != 0) RenderSummaryRows();
            if (pipChecklist != null) pipChecklist.RemainingOnly = value;
        }
        int summaryCardColumns;
        ProcurementPlan procurementPlan;
        Window procurementDialog;
        string procurementDetailName;
        ScrollViewer procurementDetailScroll;
        ScrollViewer procurementSummaryScroll;
        int procurementRenderedTab = -1;
        int procurementSummaryScrollRevision;

        readonly HashSet<string> procurementDetailQuoteNames = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, Button> procurementCardButtons = new Dictionary<string, Button>(StringComparer.Ordinal);
        readonly Dictionary<string, Action> procurementCardUpdates = new Dictionary<string, Action>(StringComparer.Ordinal);

        void RenderSummary()
        {
            if (workspacePage != WorkspacePage.Trade) return;
            procurementPresetButtons.Clear(); procurementPresetStatus = null;
            double offset = procurementSummaryScroll != null && procurementRenderedTab == summaryTab ? procurementSummaryScroll.VerticalOffset : 0;
            // State-changing actions refresh the plan before rendering. Tab and
            // cached-price changes can reuse it without another graph rebuild.
            if (procurementPlan == null) procurementPlan = procurementPlanner.Build(state);
            content.Children.Clear(); content.ColumnDefinitions.Clear(); content.RowDefinitions.Clear();
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            var top = new StackPanel();
            var toolbar = new Grid(); toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tabs = new WrapPanel();
            var setupGroup = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var setupLabel = T("1. 준비 방식 정하기", 10, Muted, true); setupLabel.Margin = new Thickness(0, 0, 0, 8); setupGroup.Children.Add(setupLabel);
            var checklistGroup = new StackPanel();
            var checklistLabel = T("2. 설정대로 구매·제작하기", 10, Muted, true); checklistLabel.Margin = new Thickness(0, 0, 0, 8); checklistGroup.Children.Add(checklistLabel);
            var checklistTabs = new WrapPanel(); checklistGroup.Children.Add(checklistTabs);
            string[] labels = { "재료 준비 방식 설정", "경매장 구매 체크리스트", "제작·확보 체크리스트" };
            string[] descriptions = {
                "교역소에 낼 최종 재료부터, 경매장 구매·직접 제작·직접 확보 중 준비 방식을 정하세요.",
                "설정에 따라 경매장에서 살 품목과 수량을 모았습니다. 이 목록을 보며 구매하고, 구비했으면 체크하세요.",
                "설정에 따라 직접 만들거나 확보할 품목입니다. 하위재료부터 채집·제작하고 완료한 항목을 체크하세요. NPC 구매품도 포함됩니다."
            };
            for (int i = 0; i < labels.Length; i++)
            {
                int tab = i;
                var button = Btn(labels[i], delegate { if (summaryTab == tab) return; AppMotion.Transition(content, delegate { summaryTab = tab; summaryQuery = ""; RenderSummary(); }); }, summaryTab == i);
                button.Margin = new Thickness(0, 0, 8, 6); button.ToolTip = descriptions[i];
                AutomationProperties.SetName(button, labels[i]); AutomationProperties.SetHelpText(button, descriptions[i]);
                if (i == 0) setupGroup.Children.Add(button); else checklistTabs.Children.Add(button);
            }
            tabs.Children.Add(setupGroup);
            tabs.Children.Add(new Border { Child = checklistGroup, BorderBrush = Line, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(14, 0, 0, 0) });
            toolbar.Children.Add(tabs);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var copy = Btn("목록 복사", CopySummary, false); copy.Margin = new Thickness(0, 0, 8, 6); copy.FontSize = 11; copy.Padding = new Thickness(10, 9, 10, 9);
            copy.ToolTip = "경매장 구매·직접 제작·직접 확보·NPC 구매 목록을 함께 복사합니다."; actions.Children.Add(copy);
            var reset = Btn("구매·제작 선택 초기화", ConfirmProcurementReset, false); reset.Margin = new Thickness(0, 0, 0, 6); reset.FontSize = 11; reset.Padding = new Thickness(10, 9, 10, 9);
            reset.ToolTip = "재료별 직접 제작·직접 확보 선택과 제작법을 지우고 기본 경매장 구매로 되돌립니다. NPC 구매품은 유지하며, 적용 전에 초기화 범위를 확인합니다.";
            AutomationProperties.SetName(reset, "구매·제작 선택 초기화"); actions.Children.Add(reset);
            var actionGroup = new Border { Child = actions, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(actionGroup, 1); toolbar.Children.Add(actionGroup); top.Children.Add(toolbar);
            var hint = T(descriptions[summaryTab] + (summaryTab == 0 ? " 같은 분류의 재료끼리 모았습니다." : " 단계별로 재료 분류 → 이름순으로 표시합니다."), 11, Muted, false);
            hint.Margin = new Thickness(0, 8, 0, 9); top.Children.Add(hint);
            if (summaryTab == 0) top.Children.Add(BuildProcurementPresetControls());
            auctionSummaryStatus = T("", 12, Green, true); auctionSummaryStatus.Margin = new Thickness(0, 0, 0, 10); top.Children.Add(auctionSummaryStatus);
            UpdateAuctionSummary(procurementPlan.Purchases);
            layout.Children.Add(top);
            var search = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            search.ColumnDefinitions.Add(new ColumnDefinition()); search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            summaryCount = T("", 11, Ink, true); search.Children.Add(summaryCount);
            summarySearch = new TextBox { Text = summaryQuery, Width = 190, Height = 30, Padding = new Thickness(8, 4, 8, 4), BorderBrush = Line, BorderThickness = new Thickness(1), ToolTip = "아이템 이름으로 검색" };
            AutomationProperties.SetName(summarySearch, "재료 검색");
            summarySearch.TextChanged += delegate { summaryQuery = summarySearch.Text; RenderSummaryRows(); };
            var searchPanel = new StackPanel { Orientation = Orientation.Horizontal };
            checklistRemainingControl = null;
            if (summaryTab != 0)
            {
                checklistRemainingControl = new CheckBox { Content = "남은 품목만 보기", IsChecked = checklistRemainingOnly, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Cursor = Cursors.Hand };
                AutomationProperties.SetName(checklistRemainingControl, "남은 품목만 보기");
                checklistRemainingControl.ToolTip = "구비 완료와 상위 품목 구비로 충족된 항목을 숨깁니다. 해제하면 전체 목록을 다시 봅니다.";
                checklistRemainingControl.Click += delegate { SetChecklistRemainingOnly(checklistRemainingControl.IsChecked == true); };
                searchPanel.Children.Add(checklistRemainingControl);
            }
            var searchLabel = T("재료 검색", 11, Muted, false); searchLabel.Margin = new Thickness(0, 0, 8, 0); searchPanel.Children.Add(searchLabel); searchPanel.Children.Add(summarySearch);
            Grid.SetColumn(searchPanel, 1); search.Children.Add(searchPanel); Grid.SetRow(search, 1); layout.Children.Add(search);
            summaryRows = new StackPanel();
            if (procurementSummaryScroll == null) procurementSummaryScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(3, 4, 9, 6) };
            var oldParent = procurementSummaryScroll.Parent as Panel; if (oldParent != null) oldParent.Children.Remove(procurementSummaryScroll);
            procurementSummaryScroll.Content = summaryRows;
            Grid.SetRow(procurementSummaryScroll, 2); layout.Children.Add(procurementSummaryScroll);
            var box = Box(layout, AppTheme.Surface, 11, new Thickness(19)); box.BorderBrush = Line; box.BorderThickness = new Thickness(1); content.Children.Add(box);
            RenderSummaryRows();
            procurementRenderedTab = summaryTab; RestoreProcurementSummaryOffset(offset);
            if (procurementDialog != null) RenderProcurementDetail();
        }

        bool ProcurementMatches(string name) { return String.IsNullOrWhiteSpace(summaryQuery) || name.IndexOf(summaryQuery.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0; }
        void RenderSummaryRows()
        {
            double offset = procurementSummaryScroll == null ? 0 : procurementSummaryScroll.VerticalOffset;
            summaryRows.Children.Clear(); procurementCardButtons.Clear(); procurementCardUpdates.Clear();
            procurementReadyControls.Clear(); procurementReadyRowUpdates.Clear(); procurementStageHeaderUpdates.Clear();
            if (procurementPlan == null) procurementPlan = procurementPlanner.Build(state);
            if (procurementPlan.Roots.Count == 0)
            {
                summaryCount.Text = "선택한 교역품 0종";
                var empty = new StackPanel { Margin = new Thickness(20, 56, 20, 20) };
                empty.Children.Add(T("선택한 교역품이 없습니다", 20, Ink, true));
                var help = T("교역 계획에서 품목을 선택하면 최종 교환재료 카드가 나타납니다.", 12, Muted, false); help.Margin = new Thickness(0, 12, 0, 0); empty.Children.Add(help); summaryRows.Children.Add(empty); return;
            }
            if (summaryTab == 0)
            {
                var roots = ItemCategories.OrderByCategory(procurementPlan.Roots.Where(n => ProcurementMatches(n.Name)), n => n.Name).ToList();
                summaryCount.Text = "최종 교환재료 " + procurementPlan.Roots.Count + "종 · 경매장 구매 " + procurementPlan.Purchases.Count + "종 · 직접 제작 " + procurementPlan.Crafts.Count + "종";
                if (!String.IsNullOrWhiteSpace(summaryQuery)) summaryCount.Text += " / 검색 " + roots.Count + "종";
                var cards = new Grid(); int columns = ActualWidth >= 1280 ? 3 : 2; summaryCardColumns = columns;
                for (int i = 0; i < columns; i++) cards.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < roots.Count; i++)
                {
                    if (i % columns == 0) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var card = ProcurementCard(roots[i]); Grid.SetRow(card, i / columns); Grid.SetColumn(card, i % columns); cards.Children.Add(card);
                }
                summaryRows.Children.Add(cards);
                if (roots.Count == 0) summaryRows.Children.Add(T("검색한 최종 교환재료가 없습니다.", 12, Muted, false));
            }
            else RenderProcurementChecklist();
            RestoreProcurementSummaryOffset(offset);
        }

        void RestoreProcurementSummaryOffset(double offset)
        {
            int revision = ++procurementSummaryScrollRevision;
            var scroll = procurementSummaryScroll;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                if (scroll == null || !summaryView || revision != procurementSummaryScrollRevision || scroll != procurementSummaryScroll) return;
                scroll.ScrollToVerticalOffset(offset);
            }));
        }

        void RefreshProcurementSummaryChoice()
        {
            RefreshProcurementPresetControls();
            UpdateAuctionSummary(procurementPlan.Purchases);
            if (summaryTab != 0) { RenderSummaryRows(); return; }
            foreach (var update in procurementCardUpdates.Values) update();
            summaryCount.Text = "최종 교환재료 " + procurementPlan.Roots.Count + "종 · 경매장 구매 " + procurementPlan.Purchases.Count + "종 · 직접 제작 " + procurementPlan.Crafts.Count + "종";
            if (!String.IsNullOrWhiteSpace(summaryQuery)) summaryCount.Text += " / 검색 " + procurementCardButtons.Count + "종";
        }

        FrameworkElement ProcurementIcon(string name, double size)
        {
            var source = itemIcons.Get(name);
            var frame = new Border { Width = size, Height = size, Background = B("#EDF4EF"), CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0) };
            if (source != null)
            {
                var icon = new Image { Source = source, Stretch = Stretch.Uniform, MaxWidth = size - 10, MaxHeight = size - 10 };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.NearestNeighbor); frame.Child = icon;
            }
            else { var letter = T(name.Length > 1 ? name.Substring(0, 2) : name, 11, Green, true); letter.TextAlignment = TextAlignment.Center; frame.Child = letter; }
            return frame;
        }
        string ProcurementMode(string name)
        {
            if (procurementPlanner.IsNpcPurchase(name)) return "NPC 구매";
            string choice = procurementPlanner.GetChoice(state, name);
            return choice == "buy" ? "경매장 구매" : choice == "acquire" ? "직접 확보" : "직접 제작";
        }
        Border ProcurementCard(ProcurementNode node)
        {
            var panel = new StackPanel();
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var open = Btn("", delegate { OpenProcurementDetail(node.Name); }, false); open.Padding = new Thickness(0); open.Margin = new Thickness(0); open.BorderBrush = Brushes.Transparent;
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) }); heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.Children.Add(ProcurementIcon(node.Name, 43));
            var titlePanel = new StackPanel(); titlePanel.Children.Add(T(node.Name, 14, Ink, true));
            var quantity = T("교환에 " + Calculator.FormatQuantity(node.DirectQuantity) + "개 필요", 11, Muted, false); quantity.Margin = new Thickness(0, 5, 0, 0); titlePanel.Children.Add(quantity);
            Grid.SetColumn(titlePanel, 1); heading.Children.Add(titlePanel); open.Content = heading; header.Children.Add(open);
            var help = AcquisitionHelp(node.Name); Grid.SetColumn(help, 1); header.Children.Add(help); panel.Children.Add(header);
            AutomationProperties.SetName(open, node.Name + " 재료 카드 열기"); procurementCardButtons[node.Name] = open;
            var status = new Grid { Margin = new Thickness(0, 14, 0, 10) }; status.ColumnDefinitions.Add(new ColumnDefinition()); status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var modeLabel = T(ProcurementMode(node.Name), 11, node.IsCrafting ? B("#916020") : Green, true); status.Children.Add(modeLabel);
            bool npc = procurementPlanner.IsNpcPurchase(node.Name);
            var quote = npc ? null : auction.GetQuote(node.Name);
            var price = T(npc ? "경매장 합산 0 G" : quote != null && quote.UnitPrice.HasValue ? Gold(quote.UnitPrice.Value) + " / 개" : QuoteStatus(quote), 10, Muted, false); price.ToolTip = npc ? "NPC 상점 비용은 경매장 예상 금액에서 제외합니다." : QuoteTooltip(quote); Grid.SetColumn(price, 1); status.Children.Add(price); panel.Children.Add(status);
            var enter = Btn("재료 · 가격 · 준비 방식  ↗", delegate { OpenProcurementDetail(node.Name); }, false); enter.Content = T("재료 · 가격 · 준비 방식  ↗", 11, Green, true); enter.Margin = new Thickness(0); enter.Padding = new Thickness(10, 7, 10, 7); enter.Background = B("#F2F7F3"); panel.Children.Add(enter);
            var card = Box(panel, AppTheme.Surface, 10, new Thickness(15)); card.BorderBrush = node.IsCrafting ? B("#DECBA8") : B("#D9E5DD"); card.BorderThickness = new Thickness(1); card.Margin = new Thickness(0, 0, 11, 12);
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { if (!e.Handled) { e.Handled = true; OpenProcurementDetail(node.Name); } };
            var shadow = new DropShadowEffect { Color = Color.FromRgb(34, 70, 50), BlurRadius = 11, ShadowDepth = 3, Opacity = 0.05 }; card.Effect = shadow;
            AppMotion.HoverCard(card);
            procurementCardUpdates[node.Name] = delegate {
                string mode = ProcurementMode(node.Name); AppMotion.SetText(modeLabel, mode);
                bool craft = mode == "직접 제작"; modeLabel.Foreground = craft ? B("#916020") : Green;
                card.BorderBrush = craft ? B("#DECBA8") : B("#D9E5DD");
            };
            return card;
        }
        void SetProcurementChoice(string name, string recipeId, FrameworkElement anchor = null)
        {
            if (procurementPlanner.GetChoice(state, name) == recipeId) return;
            var position = CaptureProcurementPosition(anchor);
            ProcurementReadiness.InvalidateChoice(state, name);
            procurementPlanner.SetChoice(state, name, recipeId); Persist();
            procurementPlan = procurementPlanner.Build(state);
            RenderStats(procurementPlan);
            if (summaryView) RefreshProcurementSummaryChoice();
            if (procurementDialog != null) { RenderProcurementDetail(false); RestoreProcurementPosition(position); }
            footerMessage.Text = name + " · " + ProcurementMode(name) + " 적용. 가격은 저장된 조회 결과를 사용합니다.";
        }
        string ProcurementExport()
        {
            var plan = procurementPlanner.Build(state); var text = new StringBuilder("밀레시안 장부 · 교역 준비\r\n경매장 구매 목록\r\n재료\t필요량\t조회 단가\t예상 금액\t조회 상태\t사용처\r\n");
            var ordered = ProcurementReadiness.GetSteps(plan, state);
            var purchases = plan.Purchases.ToDictionary(r => r.Name, StringComparer.Ordinal);
            foreach (var step in ordered.Where(s => s.Kind == "purchase")) { var row = purchases[step.Name]; text.AppendLine(row.Name + "\t" + Calculator.FormatQuantity(row.Quantity) + AuctionExportColumns(row) + "\t" + String.Join(" / ", row.Uses)); }
            text.AppendLine("\r\n직접 제작 목록\r\n재료\t필요량\t제작 방식\t제작 횟수\t생산량\t잔여량\t제작 재료");
            foreach (var node in ordered.Where(s => s.Kind == "craft").Select(s => s.Node)) text.AppendLine(node.Name + "\t" + Calculator.FormatQuantity(node.Quantity) + "\t" + node.Recipe.Name + "\t" + Calculator.FormatQuantity(node.Batches) + "\t" + Calculator.FormatQuantity(node.ProducedQuantity) + "\t" + Calculator.FormatQuantity(node.Surplus) + "\t" + String.Join(" / ", node.Children.Select(c => c.Name + " " + Calculator.FormatQuantity(c.Quantity) + "개")));
            text.AppendLine("\r\n직접 확보 목록\r\n재료\t필요량");
            foreach (var row in ordered.Where(s => s.Kind == "acquire" && !procurementPlanner.IsNpcPurchase(s.Name))) text.AppendLine(row.Name + "\t" + Calculator.FormatQuantity(row.Quantity));
            text.AppendLine("\r\nNPC 구매 목록\r\n재료\t필요량\t경매장 합산액");
            foreach (var row in ordered.Where(s => s.Kind == "acquire" && procurementPlanner.IsNpcPurchase(s.Name))) text.AppendLine(row.Name + "\t" + Calculator.FormatQuantity(row.Quantity) + "\t0 G");
            return text.ToString();
        }
        void CopySummary()
        {
            try { Clipboard.SetText(ProcurementExport()); footerMessage.Text = "경매장 구매·직접 제작·직접 확보·NPC 구매 목록을 구분해 복사했습니다."; }
            catch { footerMessage.Text = "클립보드가 사용 중입니다. 잠시 후 다시 복사해 주세요."; }
        }

        void SaveProcurementDialogPreview(string path)
        {
            procurementDialog.UpdateLayout();
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {}));
            var visual = (FrameworkElement)procurementDialog.Content;
            var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(path)) encoder.Save(file);
        }
        public string RunProcurementUiChecks(Func<int> requests, string directory)
        {
            int before = requests(); var original = StateStore.Copy(state);
            try
            {
                state = new ProgressState(); state.Normalize(catalog);
                foreach (string id in new[] { "C6", "C20", "K12" }) Calculator.SetSelected(state, catalog.Trades.First(t => t.Id == id), true);
                summaryTab = 2; ShowSummary();
                if (summaryTab != 0 || procurementCardButtons.Count != procurementPlan.Roots.Count || procurementPlan.Purchases.Count != procurementPlan.Roots.Count) throw new Exception("Summary must enter exchange cards with every root buying by default.");
                procurementCardButtons["매듭끈"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (procurementDialog == null) throw new Exception("Card did not open ingredients.");
                procurementDialog.WindowStartupLocation = WindowStartupLocation.Manual; procurementDialog.Left = -18000; procurementDialog.Top = -18000;
                procurementDialog.UpdateLayout();
                if (procurementPlan.Crafts.Count != 0) throw new Exception("Recipe preview unexpectedly changed purchase choices.");
                AuctionTestChildren<Button>(procurementDialog).Single(b => AutomationProperties.GetName(b) == "매듭끈 직접 제작 선택").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (procurementPlan.Purchases.Any(p => p.Name == "매듭끈") || !procurementPlan.Purchases.Any(p => p.Name == "가는 실뭉치")) throw new Exception("Craft button did not replace parent purchase with ingredients.");
                procurementDialog.UpdateLayout();
                AuctionTestChildren<Button>(procurementDialog).Single(b => AutomationProperties.GetName(b) == "가는 실뭉치 직접 제작 선택").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                procurementDialog.UpdateLayout();
                AuctionTestChildren<Button>(procurementDialog).Single(b => AutomationProperties.GetName(b) == "거미줄 직접 확보 선택").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!procurementPlan.Acquisitions.Any(p => p.Name == "거미줄") || procurementPlan.Purchases.Any(p => p.Name == "거미줄")) throw new Exception("Leaf acquisition must be separate from auction purchases.");
                var reloaded = store.Load(catalog);
                if (procurementPlanner.GetChoice(reloaded, "거미줄") != "acquire" || procurementPlanner.GetChoice(reloaded, "매듭끈") == "buy") throw new Exception("Procurement preferences did not persist.");
                SaveProcurementDialogPreview(Path.Combine(directory, "procurement-recursive-choice.png"));
                procurementDialog.Close();
                SavePreview(Path.Combine(directory, "procurement-cards.png"));
                summaryTab = 1; RenderSummary(); SavePreview(Path.Combine(directory, "procurement-purchases.png"));
                summaryTab = 2; RenderSummary(); SavePreview(Path.Combine(directory, "procurement-crafts.png"));
                if (!ProcurementExport().Contains("직접 확보 목록") || !ProcurementExport().Contains("거미줄")) throw new Exception("Combined export omitted acquisition requirements.");
                SetProcurementChoice("펫 놀이세트", procurementPlanner.GetRecipes("펫 놀이세트").First().Id);
                OpenProcurementDetail("나무판"); procurementDialog.Left = -18000; procurementDialog.Top = -18000;
                SaveProcurementDialogPreview(Path.Combine(directory, "procurement-board.png")); procurementDialog.Close();
                SetProcurementChoice("금판", procurementPlanner.GetRecipes("금판").First().Id);
                SetProcurementChoice("금괴", "synthesis"); OpenProcurementDetail("금괴"); procurementDialog.Left = -18000; procurementDialog.Top = -18000;
                SaveProcurementDialogPreview(Path.Combine(directory, "procurement-synthesis.png")); procurementDialog.Close();
                if (procurementPlan.Purchases.Single(p => p.Name == "금괴").Quantity != 1) throw new Exception("Synthesis must buy just one starting ingot.");
                foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, true);
                Width = 1100; Height = 740; ShowSummary(); SavePreview(Path.Combine(directory, "procurement-compact-cards.png"));
                summaryTab = 1; RenderSummary(); SavePreview(Path.Combine(directory, "procurement-compact-purchases.png"));
                summarySearch.Text = "500 포션";
                if (procurementPlan.Crafts.Any(n => n.Name.Contains("500 포션"))) throw new Exception("Purchased potions became craftable.");
                ShowStation("오아시스"); ShowSummary();
                if (summaryTab != 0 || summaryQuery != "") throw new Exception("Reentering summary did not reset to exchange cards.");
                if (requests() != before) throw new Exception("Cards, previews, source choices or procurement lists requested auction HTTP.");
                return "PASS procurement UI: exchange cards first, card click opens nested materials, real craft/acquire buttons replace purchase leaves, choices persist, synthesis seed is counted once, separate lists/export, compact layouts and previews send zero HTTP.";
            }
            finally
            {
                if (procurementDialog != null) procurementDialog.Close();
                state = original; Width = 1400; Height = 940; summaryTab = 0; summaryQuery = ""; Persist(); RenderAll();
            }
        }
    }
}
