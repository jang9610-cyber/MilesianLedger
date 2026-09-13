using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed class MarketStatisticsRow
    {
        public MarketSnapshotItem Item { get; private set; }
        public string SearchKey { get; private set; }
        public string Name { get { return Item.Name; } }
        public string Category { get { return Item.Category; } }
        public string SoldQuantityText { get { return Count(Item.SoldQuantity); } }
        public string TradeCountText { get { return Count(Item.TradeCount); } }
        public string TradedGoldText { get { return Price(Item.TradedGold); } }
        public string AveragePriceText { get { return Item.PriceComparable ? Price(Item.AverageSalePrice) : "옵션 제외"; } }
        public string LowestPriceText { get { return Item.PriceComparable ? Price(Item.LowestListingPrice) : "옵션 제외"; } }
        public string ListedQuantityText { get { return Count(Item.ListedQuantity); } }
        public string ListingCountText { get { return Count(Item.ListingCount); } }
        public string Detail { get { return Name + " · " + Category + (Item.PriceComparable ? " · 평균은 수량 가중 단가입니다." : " · 옵션별 가격 차이로 단가 비교에서 제외합니다."); } }
        public MarketStatisticsRow(MarketSnapshotItem item) { Item = item; SearchKey = KoreanNameSearch.Normalize(item.Name); }
        static string Count(long? value) { return value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "—"; }
        static string Price(decimal? value) { return value.HasValue ? value.Value.ToString("#,0.##", CultureInfo.InvariantCulture) : "—"; }
    }

    // Detaching a workspace page preserves its filters, selection and scroll.
    // Only Dispose ends observation of the shared client's local publications.
    public sealed class MarketStatisticsView : UserControl, IDisposable
    {
        sealed class PreparedSnapshot
        {
            public MarketSnapshotData Data;
            public List<MarketStatisticsRow> Day, Week;
            public PreparedSnapshot(MarketSnapshotData data) { Data = data; Day = Rows(data.Items24h); Week = Rows(data.Items7d); }
            static List<MarketStatisticsRow> Rows(List<MarketSnapshotItem> items) { return items == null ? new List<MarketStatisticsRow>() : items.Where(item => item != null && !String.IsNullOrEmpty(item.Name)).Select(item => new MarketStatisticsRow(item)).ToList(); }
        }
        readonly MarketSnapshotClient client;
        readonly string unavailable;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly DispatcherTimer searchDelay;
        readonly TextBlock emptyText, selectionText;
        PreparedSnapshot prepared;
        Task<PreparedSnapshot> building;
        MarketSnapshotData buildingData;
        volatile bool disposed;
        bool loadedOnce, busy;
        int snapshotGeneration, renderGeneration;
        string refreshMessage = "";
        public TextBox SearchInput { get; private set; }
        public ComboBox PeriodInput { get; private set; }
        public ComboBox SortInput { get; private set; }
        public Button RefreshButton { get; private set; }
        public DataGrid Table { get; private set; }
        public TextBlock StatusText { get; private set; }
        public MarketSnapshotData Snapshot { get { return prepared == null ? null : prepared.Data; } }
        public bool IsBusy { get { return busy; } }

        public MarketStatisticsView(MarketSnapshotClient client, string unavailableMessage = null)
        {
            this.client = client;
            unavailable = String.IsNullOrWhiteSpace(unavailableMessage) ? "시세 서버 연결이 설정되지 않았습니다." : unavailableMessage;
            Background = Paint("#F4F6F5");
            var root = new Grid { Background = Background };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            heading.Children.Add(Text("시장 통계", 25, "#202D35", true));
            heading.Children.Add(Text("판매량과 매물 현황 · 단가와 거래 금액은 G 기준", 12, "#728087", false)); root.Children.Add(heading);
            var filters = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            PeriodInput = Combo(new[] { "최근 24시간", "최근 7일" }, 108); PeriodInput.Margin = new Thickness(0, 0, 7, 0);
            SortInput = Combo(new[] { "판매 수량순", "거래 건수순", "거래 금액순", "매물 수량순" }, 120); SortInput.Margin = new Thickness(0, 0, 7, 0);
            Grid.SetColumn(SortInput, 1); filters.Children.Add(PeriodInput); filters.Children.Add(SortInput);
            var search = new Grid { Margin = new Thickness(0, 0, 7, 0), MinWidth = 90 };
            SearchInput = new TextBox { FontSize = 13, MinHeight = 34, Padding = new Thickness(8, 5, 8, 5), MaxLength = 200,
                Background = AppTheme.Surface, Foreground = Paint("#202D35"), BorderBrush = Paint("#DCE5DF"), BorderThickness = new Thickness(1),
                CaretBrush = Paint("#202D35"), VerticalContentAlignment = VerticalAlignment.Center };
            SearchInput.ToolTip = "아이템 이름이나 초성으로 검색하세요. ㄱㅁㅈ, 거ㅁ줄처럼 입력할 수 있으며 띄어쓰기는 생략해도 됩니다.";
            var hint = Text("이름·초성 검색", 12, "#728087", false); hint.Margin = new Thickness(9, 0, 0, 0); hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false;
            search.Children.Add(SearchInput); search.Children.Add(hint); Grid.SetColumn(search, 2); filters.Children.Add(search);
            RefreshButton = RefreshControl(); RefreshButton.IsEnabled = client != null;
            RefreshButton.ToolTip = "서버의 공통 게시본을 받습니다. 검색과 화면 전환은 경매장 조회를 시작하지 않습니다.";
            Grid.SetColumn(RefreshButton, 3); filters.Children.Add(RefreshButton); Grid.SetRow(filters, 1); root.Children.Add(filters);
            AutomationProperties.SetName(SearchInput, "시장 통계 품목 검색"); AutomationProperties.SetName(PeriodInput, "시장 통계 기간");
            AutomationProperties.SetName(SortInput, "시장 통계 정렬"); AutomationProperties.SetName(RefreshButton, "시장 통계 갱신");
            StatusText = Text("", 11, "#728087", false); StatusText.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(StatusText, 2); root.Children.Add(StatusText);
            var tableHost = new Grid(); Table = CreateTable(); tableHost.Children.Add(Table);
            emptyText = Text("", 14, "#728087", false); emptyText.Margin = new Thickness(20, 40, 20, 20);
            emptyText.VerticalAlignment = VerticalAlignment.Center; emptyText.HorizontalAlignment = HorizontalAlignment.Center; emptyText.IsHitTestVisible = false;
            tableHost.Children.Add(emptyText); Grid.SetRow(tableHost, 3); root.Children.Add(tableHost);
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            selectionText = Text("품목을 선택하면 분류와 가격 비교 기준을 확인할 수 있습니다.", 11, "#728087", false); footer.Children.Add(selectionText);
            footer.Children.Add(Text("완료된 수집 구간만 집계합니다. 수집 이전·장애 구간은 누락될 수 있으며 옵션이 있는 장비의 단가는 비교하지 않습니다.", 10, "#728087", false)); Grid.SetRow(footer, 4); root.Children.Add(footer);
            searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) }; searchDelay.Tick += SearchElapsed;
            SearchInput.TextChanged += delegate { if (disposed) return; hint.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; searchDelay.Stop(); searchDelay.Start(); };
            SearchInput.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; searchDelay.Stop(); Render(false); } };
            PeriodInput.SelectionChanged += FilterChanged; SortInput.SelectionChanged += FilterChanged;
            Table.SelectionChanged += delegate { var row = Table.SelectedItem as MarketStatisticsRow; selectionText.Text = row == null ? "품목을 선택하면 분류와 가격 비교 기준을 확인할 수 있습니다." : row.Detail; };
            RefreshButton.Click += RefreshClicked; Loaded += ViewLoaded;
            if (client != null) client.SnapshotPublished += SharedPublished;
            Render(false);
        }

        async void ViewLoaded(object sender, RoutedEventArgs e)
        {
            if (disposed || loadedOnce) return; loadedOnce = true; if (client == null) return;
            int generation = snapshotGeneration; SetBusy(true);
            try { var data = await Task.Run(() => client.ReadCachedData(), lifetime.Token); if (!disposed && generation == snapshotGeneration) await ApplyData(data); }
            catch (OperationCanceledException) { }
            catch { if (!disposed && generation == snapshotGeneration) refreshMessage = "저장된 시세를 읽지 못했습니다. 통계 갱신으로 다시 확인하세요."; }
            finally { if (!disposed) { SetBusy(false); UpdateStatus(Table.Items.Count); } }
        }
        async void RefreshClicked(object sender, RoutedEventArgs e)
        {
            if (disposed || busy || client == null) return;
            loadedOnce = true; SetBusy(true); refreshMessage = "새 공통 데이터를 확인하는 중…"; UpdateStatus(Table.Items.Count);
            try {
                var result = await client.RefreshAsync(lifetime.Token); if (disposed) return; if (result == null) throw new InvalidOperationException();
                bool failed = !String.IsNullOrWhiteSpace(result.ErrorMessage);
                if (result.Data != null && (!failed || prepared == null) && Object.ReferenceEquals(client.CachedData, result.Data)) await ApplyData(result.Data);
                if (disposed) return;
                refreshMessage = failed ? result.ErrorMessage + (prepared == null ? "" : " 이전 공통 데이터를 유지합니다.") : result.Downloaded ? "새 공통 데이터를 내려받았습니다." : "최신 버전입니다. 추가 다운로드가 없습니다.";
            } catch (OperationCanceledException) { }
            catch { if (!disposed) refreshMessage = "시장 데이터를 확인하지 못했습니다. 이전 공통 데이터를 유지합니다."; }
            finally { if (!disposed) { SetBusy(false); UpdateStatus(Table.Items.Count); } }
        }
        void SharedPublished(MarketSnapshotData data)
        {
            if (disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(new Action(async delegate {
                if (disposed || !Object.ReferenceEquals(client.CachedData, data)) return;
                try { await ApplyData(data); }
                catch { if (!disposed) { refreshMessage = "새 시세를 표시하지 못해 이전 결과를 유지합니다."; UpdateStatus(Table.Items.Count); } }
            })); } catch (InvalidOperationException) { }
        }
        async Task ApplyData(MarketSnapshotData data)
        {
            if (disposed || data == null || Object.ReferenceEquals(Snapshot, data)) return;
            int generation; Task<PreparedSnapshot> work;
            if (Object.ReferenceEquals(buildingData, data) && building != null) { work = building; generation = snapshotGeneration; }
            else { generation = ++snapshotGeneration; buildingData = data; work = building = Task.Run(() => new PreparedSnapshot(data), lifetime.Token); }
            try {
                var next = await work; if (disposed || generation != snapshotGeneration || Object.ReferenceEquals(Snapshot, data)) return;
                prepared = next; refreshMessage = ""; Render(true);
            } catch { if (!disposed && generation == snapshotGeneration) throw; }
            finally { if (generation == snapshotGeneration && Object.ReferenceEquals(building, work)) { building = null; buildingData = null; } }
        }
        void SetBusy(bool value) { busy = value; RefreshButton.IsEnabled = !value && client != null; RefreshButton.Content = value ? "확인 중…" : "통계 갱신"; }
        void SearchElapsed(object sender, EventArgs e) { searchDelay.Stop(); Render(false); }
        void FilterChanged(object sender, SelectionChangedEventArgs e) { searchDelay.Stop(); Render(false); }
        void Render(bool preservePosition)
        {
            if (disposed) return; int generation = ++renderGeneration;
            var selected = Table.SelectedItem as MarketStatisticsRow;
            var scroller = FindScroll(Table); double offset = preservePosition && scroller != null ? scroller.VerticalOffset : 0;
            var rows = prepared == null ? new List<MarketStatisticsRow>() : PeriodInput.SelectedIndex == 1 ? prepared.Week : prepared.Day;
            string query = KoreanNameSearch.Normalize(SearchInput.Text);
            var filtered = rows.Where(row => KoreanNameSearch.Contains(row.SearchKey, query));
            IOrderedEnumerable<MarketStatisticsRow> ordered;
            switch (SortInput.SelectedIndex) {
                case 1: ordered = filtered.OrderByDescending(row => row.Item.TradeCount); break;
                case 2: ordered = filtered.OrderByDescending(row => row.Item.TradedGold); break;
                case 3: ordered = filtered.OrderByDescending(row => row.Item.ListedQuantity); break;
                default: ordered = filtered.OrderByDescending(row => row.Item.SoldQuantity); break;
            }
            var matching = ordered.ThenBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Category, StringComparer.Ordinal).ToList(); Table.ItemsSource = matching;
            if (selected != null) Table.SelectedItem = matching.FirstOrDefault(row => row.Name == selected.Name && row.Category == selected.Category);
            emptyText.Text = prepared == null ? client == null ? unavailable : "저장된 공통 시세가 없습니다. 통계 갱신으로 데이터를 받으세요." : "검색에 해당하는 품목이 없습니다.";
            emptyText.Visibility = matching.Count == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateStatus(matching.Count);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { if (disposed || generation != renderGeneration) return; var scroll = FindScroll(Table); if (scroll != null) scroll.ScrollToVerticalOffset(offset); }));
        }
        void UpdateStatus(int count)
        {
            if (prepared == null) StatusText.Text = busy ? "저장된 공통 데이터를 확인하는 중…" : client == null ? unavailable : "아직 받은 시세가 없습니다.";
            else {
                var history = Value(Snapshot.Status, "history") as IDictionary<string, object>; var listings = Value(Snapshot.Status, "listings") as IDictionary<string, object>;
                StatusText.Text = "공통 데이터 " + Stamp(Snapshot.GeneratedUtc) + " · 거래 수집 " + Published(history) + " · 매물 수집 " + (Snapshot.ListingsFetchedUtc.HasValue ? Stamp(Snapshot.ListingsFetchedUtc.Value) : "미확인") + " · " + count.ToString("N0") + "종";
                if (Snapshot.GeneratedUtc < DateTime.UtcNow.AddHours(-2) || Equals(Value(history, "stale"), true) || Equals(Value(listings, "stale"), true)) StatusText.Text += "\n일부 수집 기록이 오래되었거나 아직 없습니다. 수집 시각을 확인하세요.";
                if (Count(Snapshot.Status, "failed_runs_7d") > 0) StatusText.Text += "\n최근 수집 실패 구간이 있어 통계에 누락이 있을 수 있습니다.";
                if (Count(Snapshot.Status, "limited_runs_7d") > 0) StatusText.Text += "\n페이지를 제한한 시범 수집 기록입니다. 전체 시장 통계가 아닙니다.";
            }
            if (!String.IsNullOrEmpty(refreshMessage)) StatusText.Text += "\n" + refreshMessage;
        }

        DataGrid CreateTable()
        {
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
                CanUserReorderColumns = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow, RowHeaderWidth = 0, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, RowHeight = 34, ColumnHeaderHeight = 36, BorderThickness = new Thickness(1),
                BorderBrush = Paint("#DCE5DF"), HorizontalGridLinesBrush = Paint("#E2E8E5"), Background = AppTheme.Surface, Foreground = Paint("#202D35"), FontSize = 12 };
            ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            var header = new Style(typeof(DataGridColumnHeader)); header.Setters.Add(new Setter(Control.BackgroundProperty, Paint("#F4F6F5"))); header.Setters.Add(new Setter(Control.ForegroundProperty, Paint("#202D35")));
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold)); header.Setters.Add(new Setter(Control.FontSizeProperty, 11.0)); header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 0, 7, 0))); header.Setters.Add(new Setter(Control.BorderBrushProperty, Paint("#DCE5DF"))); grid.ColumnHeaderStyle = header;
            var rowStyle = new Style(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, AppTheme.Surface)); rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Paint("#202D35"))); rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Detail"))); grid.RowStyle = rowStyle;
            var cells = new Style(typeof(DataGridCell)); cells.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, Paint("#EAF3E9"))); selected.Setters.Add(new Setter(Control.ForegroundProperty, Paint("#19543F"))); cells.Triggers.Add(selected); grid.CellStyle = cells;
            AddColumn(grid, "품목", "Name", 2.8, 170, false); AddColumn(grid, "판매 수량", "SoldQuantityText", 1, 65, true);
            AddColumn(grid, "거래 건수", "TradeCountText", .9, 60, true); AddColumn(grid, "거래 금액", "TradedGoldText", 1.4, 90, true);
            AddColumn(grid, "평균 단가", "AveragePriceText", 1.2, 82, true); AddColumn(grid, "최저 단가", "LowestPriceText", 1.2, 82, true);
            AddColumn(grid, "매물 수량", "ListedQuantityText", 1, 65, true); AddColumn(grid, "등록 건수", "ListingCountText", .9, 60, true);
            AutomationProperties.SetName(grid, "시장 통계 품목 표"); return grid;
        }
        static void AddColumn(DataGrid grid, string title, string property, double weight, double minimum, bool numeric)
        {
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, numeric ? TextAlignment.Right : TextAlignment.Left));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(7, 0, 7, 0))); style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(property)));
            grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = new DataGridLength(weight, DataGridLengthUnitType.Star), MinWidth = minimum, ElementStyle = style });
        }
        static ScrollViewer FindScroll(DependencyObject root) { var found = root as ScrollViewer; if (found != null) return found; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = FindScroll(VisualTreeHelper.GetChild(root, i)); if (child != null) return child; } return null; }
        static ControlTemplate comboTemplate;
        static Style comboItemStyle;
        static ComboBox Combo(string[] values, double width)
        {
            var combo = new ComboBox { ItemsSource = values, SelectedIndex = 0, Width = width, MinHeight = 34, Padding = new Thickness(8, 0, 23, 0),
                Background = AppTheme.Surface, Foreground = Paint("#202D35"), BorderBrush = Paint("#DCE5DF"), BorderThickness = new Thickness(1), FontSize = 12 };
            combo.Resources["MarketComboSurface"] = AppTheme.Surface; combo.Resources["MarketComboInk"] = Paint("#202D35");
            combo.Resources["MarketComboLine"] = Paint("#DCE5DF"); combo.Resources["MarketComboSelected"] = Paint("#EAF3E9");
            if (comboTemplate == null) comboTemplate = (ControlTemplate)XamlReader.Parse(ComboTemplate);
            if (comboItemStyle == null) comboItemStyle = (Style)XamlReader.Parse(ComboItemStyle);
            combo.Template = comboTemplate; combo.ItemContainerStyle = comboItemStyle; return combo;
        }
        const string ComboTemplate = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ComboBox}'>
 <Grid>
  <Border CornerRadius='7' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'/>
  <ToggleButton Focusable='False' ClickMode='Press' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
   <ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'><Border Background='Transparent' CornerRadius='7'/></ControlTemplate></ToggleButton.Template>
  </ToggleButton>
  <ContentPresenter IsHitTestVisible='False' Margin='{TemplateBinding Padding}' VerticalAlignment='Center' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' TextElement.Foreground='{TemplateBinding Foreground}'/>
  <TextBlock IsHitTestVisible='False' Text='⌄' Foreground='{TemplateBinding Foreground}' Margin='0,0,8,2' VerticalAlignment='Center' HorizontalAlignment='Right'/>
  <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'>
   <Border MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' Background='{DynamicResource MarketComboSurface}' BorderBrush='{DynamicResource MarketComboLine}' BorderThickness='1' CornerRadius='7' Padding='3'>
    <ScrollViewer MaxHeight='260' CanContentScroll='True'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>
   </Border>
  </Popup>
 </Grid>
 <ControlTemplate.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger></ControlTemplate.Triggers>
</ControlTemplate>";
        const string ComboItemStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ComboBoxItem}'>
 <Setter Property='Foreground' Value='{DynamicResource MarketComboInk}'/><Setter Property='Background' Value='{DynamicResource MarketComboSurface}'/><Setter Property='Padding' Value='8,7'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBoxItem}'><Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' CornerRadius='4'><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter>
 <Style.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter Property='Background' Value='{DynamicResource MarketComboSelected}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='{DynamicResource MarketComboSelected}'/></Trigger></Style.Triggers>
</Style>";
        static Button RefreshControl()
        {
            var button = new Button { Content = "통계 갱신", MinWidth = 96, Padding = new Thickness(12, 6, 12, 6), Background = Paint("#226C54"), Foreground = AppTheme.OnAccent, FontWeight = FontWeights.SemiBold, BorderBrush = Paint("#226C54"), Cursor = Cursors.Hand };
            var template = new ControlTemplate(typeof(Button)); var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent }); border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .84)); template.Triggers.Add(hover);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled); button.Template = template; return button;
        }
        static Brush Paint(string color) { return AppTheme.Brush(color); }
        static TextBlock Text(string value, double size, string color, bool bold) { return new TextBlock { Text = value, FontSize = size, Foreground = Paint(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap }; }
        static object Value(IDictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value : null; }
        static long Count(IDictionary<string, object> data, string key) { long value; return Int64.TryParse(Convert.ToString(Value(data, key), CultureInfo.InvariantCulture), out value) ? value : 0; }
        static string Stamp(DateTime time) { return time == DateTime.MinValue ? "미확인" : time.ToLocalTime().ToString("MM/dd HH:mm"); }
        static string Published(IDictionary<string, object> state) { var published = Value(state, "published") as IDictionary<string, object>; DateTimeOffset time; return DateTimeOffset.TryParse(Convert.ToString(Value(published, "finished_at")), out time) ? time.ToLocalTime().ToString("MM/dd HH:mm") : "대기 중"; }
        public void Dispose()
        {
            VerifyAccess(); if (disposed) return; disposed = true; ++snapshotGeneration; ++renderGeneration;
            if (client != null) client.SnapshotPublished -= SharedPublished;
            Loaded -= ViewLoaded; RefreshButton.Click -= RefreshClicked; PeriodInput.SelectionChanged -= FilterChanged; SortInput.SelectionChanged -= FilterChanged;
            searchDelay.Stop(); searchDelay.Tick -= SearchElapsed; try { lifetime.Cancel(); } catch (AggregateException) { } lifetime.Dispose(); building = null; buildingData = null;
        }
    }
}
