using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class PipChecklistWindow : Window
    {
        sealed class RowView
        {
            public string Key;
            public Border Frame;
            public CheckBox Check;
            public TextBlock Name, Quantity, Description, Status;
        }

        sealed class GroupView
        {
            public string Key;
            public List<string> Keys;
            public TextBlock Header;
            public Expander Expander;
        }

        sealed class TabView
        {
            public int Number;
            public string Signature;
            public double Offset;
            public int RestoreRevision;
            public bool Restoring;
            public ScrollViewer Scroll;
            public StackPanel Body;
            public TextBlock RemainingEmpty;
            public readonly Dictionary<string, RowView> Rows = new Dictionary<string, RowView>(StringComparer.Ordinal);
            public readonly Dictionary<string, GroupView> Groups = new Dictionary<string, GroupView>(StringComparer.Ordinal);
            public readonly Dictionary<string, bool> Expanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        readonly Func<string, ImageSource> icons;
        readonly Action<string, bool> setReady;
        readonly Action openMain;
        readonly TabView purchases;
        readonly TabView preparations;
        readonly Grid listContent = new Grid();
        StackPanel checklistProgress;
        TextBlock footerNote;
        readonly Dictionary<string, ProcurementStep> latest = new Dictionary<string, ProcurementStep>(StringComparer.Ordinal);
        readonly Dictionary<string, CheckBox> readyControls = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        readonly Dictionary<string, Expander> groupControls = new Dictionary<string, Expander>(StringComparer.Ordinal);
        readonly TextBlock overallText = Label("0%", 15, Green, true);
        readonly TextBlock selectedCount = Label("구매 0종 · 0 / 0 준비", 11, Muted, false);
        readonly TextBlock interactionMessage = Label("", 10, Paint("#916020"), false);
        readonly ProgressBar overallBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Foreground = Green, Background = Paint("#E4EBE6"), BorderThickness = new Thickness(0) };
        Func<ProcurementStep, string> describe;
        int selectedTab = 1;
        bool closed;
        bool remainingOnly;
        public CheckBox RemainingOnlyControl { get; private set; }
        public Action<bool> RemainingOnlyChanged;
        public bool RemainingOnly
        {
            get { return remainingOnly; }
            set {
                if (remainingOnly == value) return;
                CloseAcquisitionHelp();
                remainingOnly = value; RemainingOnlyControl.IsChecked = value;
                ApplyRemainingFilter(purchases); ApplyRemainingFilter(preparations);
                if (RemainingOnlyChanged != null) RemainingOnlyChanged(value);
            }
        }

        static readonly Brush Green = Paint("#226C54");
        static readonly Brush Ink = Paint("#202D35");
        static readonly Brush Muted = Paint("#748278");
        static readonly Brush Line = Paint("#DCE5DF");
        static readonly Brush ReadyFill = Paint("#E5F2E9");
        static readonly Brush CoveredFill = Paint("#EAF1F7");
        static readonly Brush ReadyLine = Paint("#A4CBB4");
        static readonly Brush CoveredLine = Paint("#BDD0E0");
        static readonly Brush CoveredInk = Paint("#547A98");

        public Action<int> TabChanged;
        public Button PurchaseTabButton { get; private set; }
        public Button PreparationTabButton { get; private set; }
        public Button OpenMainButton { get; private set; }
        public Button CloseButton { get; private set; }
        public IDictionary<string, CheckBox> ReadyControls { get { return readyControls; } }
        public IDictionary<string, Expander> GroupControls { get { return groupControls; } }
        public ScrollViewer PurchaseScroll { get { return purchases.Scroll; } }
        public ScrollViewer PreparationScroll { get { return preparations.Scroll; } }
        public ScrollViewer CraftScroll { get { return preparations.Scroll; } }
        public TextBlock OverallPercentText { get { return overallText; } }
        public TextBlock SelectedCountText { get { return selectedCount; } }
        public ProgressBar OverallProgress { get { return overallBar; } }
        public TextBlock InteractionMessage { get { return interactionMessage; } }

        public int SelectedTab
        {
            get { return selectedTab; }
            set
            {
                VerifyAccess();
                int next = value == 3 ? 3 : value == 2 ? 2 : 1;
                if (selectedTab == next) return;
                CloseAcquisitionHelp();
                SaveOffset(CurrentView);
                SaveSearchOffset();
                AppMotion.Transition(listContent, delegate {
                    if (closed) return;
                    selectedTab = next;
                    MountCurrentView();
                    UpdateTabButtons();
                    UpdateSelectedCount();
                    var changed = TabChanged;
                    if (changed != null) changed(selectedTab);
                });
            }
        }

        TabView CurrentView { get { return selectedTab == 3 ? null : selectedTab == 1 ? purchases : preparations; } }

        public PipChecklistWindow(Func<string, ImageSource> icons, Action<string, bool> setReady, Action openMain)
        {
            this.icons = icons;
            this.setReady = setReady;
            this.openMain = openMain;
            selectedCount.ToolTip = "하위 재료 → 중간 제작품 → 최종 교환 재료 순서입니다. 각 단계에서 같은 분류의 재료끼리 모아 이름순으로 표시합니다.";
            Title = "밀레시안 장부 · PIP";
            Width = 360; Height = 540; MinWidth = 320; MinHeight = 300; MaxWidth = 720; MaxHeight = 1000;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Owner = null; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            Background = Paint("#F4F7F4");
            FontFamily = new FontFamily("Malgun Gothic"); FontSize = 12;
            Foreground = Ink; UseLayoutRounding = true; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowChrome.SetWindowChrome(this, new WindowChrome {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false
            });
            purchases = CreateTab(1); preparations = CreateTab(2);
            BuildMarketSearch();
            BuildShell();
            UpdateSteps(new List<ProcurementStep>(), 0m, null);
            MountCurrentView();
            AppMotion.WindowContent(this);
            PipWindowBehavior.Attach(this);
            LocationChanged += delegate { CloseAcquisitionHelp(); };
            SizeChanged += delegate { CloseAcquisitionHelp(); };
            Closed += delegate { CloseAcquisitionHelp(); closed = true; CloseMarketSearch(); ++purchases.RestoreRevision; ++preparations.RestoreRevision; };
        }

        void BuildShell()
        {
            var layout = new Grid { Margin = new Thickness(12, 10, 12, 8) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid { Margin = new Thickness(0, 0, 0, 12), MinHeight = 30 };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var drag = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "드래그해서 PIP 위치 이동" };
            var title = Label("밀레시안 장부", 13, Ink, true);
            drag.Child = title;
            drag.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                e.Handled = true;
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            AutomationProperties.SetName(drag, "PIP 창 이동"); header.Children.Add(drag);
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            OpenMainButton = SmallButton("메인 앱", delegate { if (openMain != null) openMain(); });
            OpenMainButton.ToolTip = "메인 앱으로 돌아가 교역 계획과 준비 방식을 설정합니다.";
            AutomationProperties.SetName(OpenMainButton, "PIP 메인 앱 열기");
            CloseButton = SmallButton("닫기", Close); CloseButton.Margin = new Thickness(5, 0, 0, 0);
            CloseButton.ToolTip = "PIP만 닫습니다. 메인 앱과 준비 상태는 유지됩니다.";
            AutomationProperties.SetName(CloseButton, "PIP 닫기");
            tools.Children.Add(OpenMainButton); tools.Children.Add(CloseButton); Grid.SetColumn(tools, 1); header.Children.Add(tools); layout.Children.Add(header);

            var progress = new StackPanel { Margin = new Thickness(1, 0, 1, 12) };
            checklistProgress = progress;
            var totals = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            totals.ColumnDefinitions.Add(new ColumnDefinition()); totals.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            totals.Children.Add(Label("전체 재료 준비율", 11, Muted, true));
            Grid.SetColumn(overallText, 1); totals.Children.Add(overallText); progress.Children.Add(totals); progress.Children.Add(overallBar);
            AutomationProperties.SetName(overallText, "PIP 전체 재료 준비율");
            AutomationProperties.SetName(overallBar, "PIP 준비 진행도");
            Grid.SetRow(progress, 1); layout.Children.Add(progress);

            var tabs = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition());
            PurchaseTabButton = SmallButton("경매장 구매", delegate { SelectedTab = 1; }); PurchaseTabButton.Height = 34; PurchaseTabButton.Margin = new Thickness(0, 0, 3, 0);
            PreparationTabButton = SmallButton("제작·확보", delegate { SelectedTab = 2; }); PreparationTabButton.Height = 34; PreparationTabButton.Margin = new Thickness(3, 0, 3, 0);
            SearchTabButton = SmallButton("시세 검색", delegate { SelectedTab = 3; }); SearchTabButton.Height = 34; SearchTabButton.Margin = new Thickness(3, 0, 0, 0);
            foreach (var button in new[] { PurchaseTabButton, PreparationTabButton, SearchTabButton }) button.Padding = new Thickness(3, 0, 3, 0);
            AutomationProperties.SetName(PurchaseTabButton, "PIP 경매장 구매 체크리스트");
            AutomationProperties.SetName(PreparationTabButton, "PIP 제작·확보 체크리스트");
            AutomationProperties.SetName(SearchTabButton, "PIP 경매장 시세 검색");
            buttons.Children.Add(PurchaseTabButton); Grid.SetColumn(PreparationTabButton, 1); buttons.Children.Add(PreparationTabButton); tabs.Children.Add(buttons);
            Grid.SetColumn(SearchTabButton, 2); buttons.Children.Add(SearchTabButton);
            selectedCount.Margin = new Thickness(2, 8, 2, 0); tabs.Children.Add(selectedCount);
            AutomationProperties.SetName(selectedCount, "PIP 현재 목록 준비 개수");
            RemainingOnlyControl = new CheckBox { Content = "남은 품목만 보기", Focusable = false, FontSize = 12, Margin = new Thickness(2, 8, 0, 0), Cursor = Cursors.Hand };
            AutomationProperties.SetName(RemainingOnlyControl, "PIP 남은 품목만 보기");
            RemainingOnlyControl.ToolTip = "구비 완료와 상위 품목 구비로 충족된 항목을 숨깁니다. 해제하면 전체 목록을 다시 봅니다.";
            RemainingOnlyControl.Click += delegate { RemainingOnly = RemainingOnlyControl.IsChecked == true; };
            StyleReadyCheck(RemainingOnlyControl);
            tabs.Children.Add(RemainingOnlyControl);
            interactionMessage.TextWrapping = TextWrapping.Wrap; interactionMessage.Margin = new Thickness(2, 6, 2, 0); interactionMessage.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(interactionMessage, "PIP 조작 제한 안내"); tabs.Children.Add(interactionMessage);
            Grid.SetRow(tabs, 2); layout.Children.Add(tabs);

            Grid.SetRow(listContent, 3); layout.Children.Add(listContent);
            var footer = new Grid { Margin = new Thickness(1, 8, 0, 0) };
            footerNote = Label("체크는 메인 앱과 함께 저장됩니다.", 10, Muted, false); footerNote.Margin = new Thickness(0, 0, 16, 0); footer.Children.Add(footerNote);
            var grip = new ResizeGrip { Width = 15, Height = 15, Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            AutomationProperties.SetName(grip, "PIP 크기 조절"); footer.Children.Add(grip);
            Grid.SetRow(footer, 4); layout.Children.Add(footer);
            Content = new Border { Background = Background, BorderBrush = Paint("#AAC4B5"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Child = layout };
            UpdateTabButtons();
        }

        TabView CreateTab(int number)
        {
            var view = new TabView { Number = number, Body = new StackPanel() };
            view.Scroll = new ScrollViewer { Content = view.Body, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 1), CanContentScroll = false };
            AutomationProperties.SetName(view.Scroll, number == 1 ? "PIP 구매 목록 스크롤" : "PIP 제작 목록 스크롤");
            view.Scroll.ScrollChanged += delegate(object sender, ScrollChangedEventArgs e) {
                if (e.VerticalChange != 0) CloseAcquisitionHelp();
                if (!view.Restoring && selectedTab == view.Number && view.Scroll.IsVisible) view.Offset = view.Scroll.VerticalOffset;
            };
            return view;
        }

        public void UpdateSteps(IList<ProcurementStep> steps, decimal overallPercent, Func<ProcurementStep, string> describe)
        {
            VerifyAccess();
            if (closed) return;
            this.describe = describe;
            latest.Clear();
            if (steps != null)
                foreach (var step in steps.Where(s => s != null && !String.IsNullOrEmpty(s.Key) && s.Quantity > 0)) latest[step.Key] = step;
            var ordered = ItemCategories.OrderSteps(latest.Values);
            UpdateTab(purchases, ordered.Where(s => s.Kind == "purchase").ToList());
            UpdateTab(preparations, ordered.Where(s => s.Kind != "purchase").ToList());
            overallBar.Value = (double)Math.Max(0m, Math.Min(100m, overallPercent));
            overallText.Text = Percent(overallPercent);
            UpdateSelectedCount();
            ApplyRemainingFilter(purchases); ApplyRemainingFilter(preparations);
        }

        void ApplyRemainingFilter(TabView view)
        {
            foreach (var row in view.Rows.Values)
                row.Frame.Visibility = remainingOnly && latest[row.Key].CompletionFraction == 1m ? Visibility.Collapsed : Visibility.Visible;
            foreach (var group in view.Groups.Values)
                group.Expander.Visibility = remainingOnly && group.Keys.All(k => latest[k].CompletionFraction == 1m) ? Visibility.Collapsed : Visibility.Visible;
            if (view.RemainingEmpty != null) view.RemainingEmpty.Visibility = remainingOnly && view.Rows.Count > 0 && view.Rows.Keys.All(k => latest[k].CompletionFraction == 1m) ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetInteractionBlocked(string message)
        {
            VerifyAccess();
            bool blocked = !String.IsNullOrWhiteSpace(message);
            if (blocked) CloseAcquisitionHelp();
            purchases.Body.IsEnabled = !blocked; preparations.Body.IsEnabled = !blocked;
            PurchaseTabButton.IsEnabled = !blocked; PreparationTabButton.IsEnabled = !blocked;
            SearchTabButton.IsEnabled = !blocked; marketSearchRoot.IsEnabled = !blocked;
            interactionMessage.Text = blocked ? message : "";
            interactionMessage.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateTab(TabView view, IList<ProcurementStep> steps)
        {
            string signature = String.Join("\n", steps.Select(s => StageKey(s) + "\t" + s.Key));
            bool rebuild = !String.Equals(view.Signature, signature, StringComparison.Ordinal);
            if (rebuild)
            {
                CloseAcquisitionHelp();
                SaveOffset(view); view.Restoring = true;
                foreach (var old in view.Rows.Keys) readyControls.Remove(old);
                foreach (var old in view.Groups.Keys) groupControls.Remove(view.Number + "|" + old);
                view.Body.Children.Clear(); view.Rows.Clear(); view.Groups.Clear();
                foreach (var entries in steps.GroupBy(StageKey))
                {
                    string key = entries.Key;
                    var group = new GroupView { Key = key, Keys = entries.Select(s => s.Key).ToList(), Header = Label("", 12, Green, true) };
                    var body = new StackPanel { Margin = new Thickness(0, 7, 0, 1) };
                    foreach (var step in entries)
                    {
                        var row = CreateRow(step);
                        view.Rows.Add(step.Key, row); readyControls[step.Key] = row.Check; body.Children.Add(row.Frame);
                    }
                    bool expanded; if (!view.Expanded.TryGetValue(key, out expanded)) expanded = true;
                    group.Expander = new Expander { Header = group.Header, Content = body, IsExpanded = expanded, Focusable = false, Foreground = Green, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(5, 7, 5, 4), Background = Paint("#EBF1EC"), BorderBrush = Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 7) };
                    AutomationProperties.SetName(group.Expander, "pip-stage:" + view.Number + "|" + key);
                    var currentGroup = group;
                    group.Expander.Expanded += delegate(object sender, RoutedEventArgs e) { if (e.OriginalSource == currentGroup.Expander) view.Expanded[key] = true; };
                    group.Expander.Collapsed += delegate(object sender, RoutedEventArgs e) { if (e.OriginalSource == currentGroup.Expander) view.Expanded[key] = false; };
                    view.Groups.Add(key, group); groupControls[view.Number + "|" + key] = group.Expander; view.Body.Children.Add(group.Expander);
                }
                if (steps.Count == 0)
                {
                    var empty = Label(view.Number == 1 ? "경매장에서 살 품목이 없습니다.\n메인 앱에서 준비 방식을 정해 주세요." : "직접 제작하거나 확보할 품목이 없습니다.\n메인 앱에서 준비 방식을 정해 주세요.", 12, Muted, false);
                    empty.TextWrapping = TextWrapping.Wrap; empty.LineHeight = 20;
                    empty.Margin = new Thickness(10, 19, 10, 10); view.Body.Children.Add(empty);
                }
                view.Signature = signature;
                view.RemainingEmpty = Label("남은 품목이 없습니다.\n체크를 해제하면 전체 목록을 봅니다.", 12, Green, false);
                view.RemainingEmpty.Margin = new Thickness(10, 18, 0, 0); view.Body.Children.Add(view.RemainingEmpty);
            }
            foreach (var step in steps) UpdateRow(view.Rows[step.Key], step);
            foreach (var group in view.Groups.Values)
            {
                var first = latest[group.Keys[0]];
                int complete = group.Keys.Count(key => latest[key].CompletionFraction == 1m);
                group.Header.Text = StageTitle(first) + "   " + complete + " / " + group.Keys.Count;
                group.Header.ToolTip = StageTitle(first) + " · " + complete + " / " + group.Keys.Count + "종 준비 충족";
            }
            if (rebuild) RestoreOffset(view);
        }

        RowView CreateRow(ProcurementStep step)
        {
            var row = new RowView { Key = step.Key };
            var layout = new Grid { MinHeight = 64 };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.Check = new CheckBox { Width = 30, MinHeight = 44, Focusable = false, VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Tag = step.Key };
            StyleReadyCheck(row.Check);
            AutomationProperties.SetName(row.Check, "pip-ready:" + step.Key);
            row.Check.Click += delegate(object sender, RoutedEventArgs e) {
                e.Handled = true;
                if (setReady != null) setReady(row.Key, row.Check.IsChecked == true);
            };
            layout.Children.Add(row.Check);
            var icon = new Image { Source = icons == null ? null : icons(step.Name), Width = 36, Height = 36, Stretch = Stretch.Uniform, SnapsToDevicePixels = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.NearestNeighbor); Grid.SetColumn(icon, 1); layout.Children.Add(icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            row.Name = Label(step.Name, 14, Ink, true); row.Name.TextWrapping = TextWrapping.Wrap; row.Name.ToolTip = step.Name; text.Children.Add(row.Name);
            row.Quantity = Label("", 15, Green, true); row.Quantity.Margin = new Thickness(0, 3, 0, 0); text.Children.Add(row.Quantity);
            row.Description = Label("", 12, Muted, false); row.Description.TextTrimming = TextTrimming.CharacterEllipsis; row.Description.Margin = new Thickness(0, 2, 0, 0); text.Children.Add(row.Description);
            row.Status = Label("", 12, Muted, false); row.Status.Visibility = Visibility.Collapsed;
            Grid.SetColumn(text, 2); layout.Children.Add(text);
            var help = CreateAcquisitionHelp(step.Key, step.Name); Grid.SetColumn(help, 3); layout.Children.Add(help);
            row.Frame = new Border { Child = layout, Background = AppTheme.Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 7, 7, 6), Margin = new Thickness(0, 0, 0, 5), Tag = "pip-row:" + step.Key };
            AutomationProperties.SetName(row.Frame, "pip-row:" + step.Key);
            return row;
        }

        void UpdateRow(RowView row, ProcurementStep step)
        {
            row.Check.IsChecked = step.IsReady;
            row.Name.Text = step.Name; row.Name.ToolTip = step.Name;
            row.Quantity.Text = Calculator.FormatQuantity(step.Quantity) + "개";
            string detail = describe == null ? step.Kind == "craft" ? "직접 제작" : step.Kind == "purchase" ? "경매장 구매" : "직접 확보 · 채집 / NPC 구매" : describe(step);
            if (step.IsSeed) detail = "합성 시작용 · " + detail;
            string brief = step.Kind == "purchase" ? (detail ?? "경매장 구매").Split(new[] { " · " }, StringSplitOptions.None).Last()
                : step.IsSeed ? "합성 시작용" : step.Kind == "craft" ? "직접 제작" : (detail ?? "직접 확보").StartsWith("NPC") ? "NPC 구매" : "직접 확보";
            row.Description.Text = step.IsReady ? "구비 완료" : step.IsCovered ? "상위 품목으로 충족" : step.PartialCoveredFraction > 0 ? "상위 품목 반영 " + Percent(step.CoveredFraction * 100m) : brief;
            row.Description.Foreground = step.IsReady ? Green : step.IsCovered ? CoveredInk : Muted;
            row.Description.ToolTip = detail;
            row.Frame.Background = step.IsReady ? ReadyFill : step.IsCovered ? CoveredFill : AppTheme.Surface;
            row.Frame.BorderBrush = step.IsReady ? ReadyLine : step.IsCovered ? CoveredLine : Line;
            row.Name.Foreground = step.IsReady ? Green : Ink;
            row.Status.Foreground = step.IsReady ? Green : step.IsCovered || step.PartialCoveredFraction > 0 ? CoveredInk : Muted;
            row.Status.Text = step.IsReady ? "구비 완료" : step.IsCovered ? "상위 품목 구비로 충족" : step.PartialCoveredFraction > 0 ? "상위 품목 구비 반영 " + Percent(step.CoveredFraction * 100m) : step.Kind == "craft" ? "제작을 마친 뒤 체크" : "필요 수량을 구비하면 체크";
            row.Status.ToolTip = row.Status.Text;
            row.Check.ToolTip = !step.IsReady && (step.IsCovered || step.PartialCoveredFraction > 0)
                ? "상위 품목 구비가 준비율에 반영됐습니다. 이 재료를 직접 구비한 기록은 별도로 체크하세요."
                : step.Kind == "craft" ? "표시된 수량의 제작을 마쳤으면 체크합니다." : "표시된 수량을 모두 구비했으면 체크합니다.";
            AutomationProperties.SetItemStatus(row.Check, row.Status.Text);
        }

        void MountCurrentView()
        {
            bool search = selectedTab == 3;
            checklistProgress.Visibility = selectedCount.Visibility = RemainingOnlyControl.Visibility = search ? Visibility.Collapsed : Visibility.Visible;
            footerNote.Text = search ? "수집 시점의 개당 최저가 · 실시간 아님" : "체크는 메인 앱과 함께 저장됩니다.";
            listContent.Children.Clear();
            if (search) {
                listContent.Children.Add(marketSearchRoot);
                RestoreSearchOffset();
                EnterMarketSearch();
            } else { listContent.Children.Add(CurrentView.Scroll); RestoreOffset(CurrentView); }
        }

        static void SaveOffset(TabView view)
        {
            if (view != null && !view.Restoring && view.Scroll.IsVisible) view.Offset = view.Scroll.VerticalOffset;
        }

        void RestoreOffset(TabView view)
        {
            if (view.Number != selectedTab || view.Scroll.Parent == null) { view.Restoring = false; return; }
            double offset = view.Offset;
            int revision = ++view.RestoreRevision;
            view.Restoring = true;
            view.Scroll.ScrollToVerticalOffset(offset);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                if (closed || revision != view.RestoreRevision) return;
                if (view.Number == selectedTab && view.Scroll.Parent != null)
                {
                    view.Scroll.UpdateLayout(); view.Scroll.ScrollToVerticalOffset(offset);
                    view.Scroll.UpdateLayout(); view.Offset = view.Scroll.VerticalOffset;
                }
                view.Restoring = false;
            }));
        }

        void UpdateTabButtons()
        {
            if (PurchaseTabButton == null || PreparationTabButton == null || SearchTabButton == null) return;
            foreach (var button in new[] { PurchaseTabButton, PreparationTabButton, SearchTabButton })
            {
                bool active = button == (selectedTab == 3 ? SearchTabButton : selectedTab == 1 ? PurchaseTabButton : PreparationTabButton);
                button.Background = active ? Green : AppTheme.Surface; button.Foreground = active ? AppTheme.OnAccent : Ink; button.BorderBrush = active ? Green : Line;
                AutomationProperties.SetItemStatus(button, active ? "현재 목록" : "목록 전환");
            }
        }

        void UpdateSelectedCount()
        {
            if (selectedTab == 3) return;
            var steps = latest.Values.Where(s => selectedTab == 1 ? s.Kind == "purchase" : s.Kind != "purchase").ToList();
            selectedCount.Text = (selectedTab == 1 ? "구매 " : "제작·확보 ") + steps.Count + "종 · " + steps.Count(s => s.CompletionFraction == 1m) + " / " + steps.Count + " 준비";
            selectedCount.ToolTip = "직접 구비 체크 " + steps.Count(s => s.IsReady) + "종 · 상위 품목 구비로 충족 " + steps.Count(s => s.IsCovered) + "종";
        }

        static string StageKey(ProcurementStep step) { return !String.IsNullOrEmpty(step.GroupKey) ? step.GroupKey : step.IsFinal ? "final" : step.Kind == "craft" ? "middle:" + step.Level : "lower"; }
        static string StageTitle(ProcurementStep step) { return !String.IsNullOrEmpty(step.GroupLabel) ? step.GroupLabel : step.IsFinal ? "최종 교환 재료" : step.Kind == "craft" ? "중간 제작품 · " + step.Level + "단계" : "하위 재료"; }
        static string Percent(decimal percent) { return (percent >= 100m ? 100 : (int)Math.Min(99m, Math.Round(Math.Max(0m, percent)))) + "%"; }
        static SolidColorBrush Paint(string hex) { return AppTheme.Brush(hex); }
        static TextBlock Label(string text, double size, Brush color, bool bold) { return new TextBlock { Text = text, FontSize = size, Foreground = color, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap }; }

        static Button SmallButton(string text, Action action)
        {
            var button = new Button { Content = text, Height = 29, Focusable = false, Padding = new Thickness(9, 0, 9, 0), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ink, Background = AppTheme.Surface, BorderBrush = Line, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7)); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter); template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .84)); template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .66)); template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled);
            button.Template = template; button.Click += delegate { action(); }; return button;
        }
    }
}
