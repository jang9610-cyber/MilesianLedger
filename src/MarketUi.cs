using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    public sealed class MarketStatisticsRow : INotifyPropertyChanged
    {
        bool watched;
        bool riskView;
        readonly decimal? referenceLowest;
        readonly bool isNameQuote;
        MarketOpportunity opportunity;
        public event PropertyChangedEventHandler PropertyChanged;
        public MarketSnapshotItem Item { get; private set; }
        public string SearchKey { get; private set; }
        public bool IsObserved { get; private set; }
        public bool IsWatched { get { return watched; } }
        public string WatchSymbol { get { return watched ? "★" : "☆"; } }
        public string WatchAction { get { return Name + (watched ? " 관심 품목에서 해제" : " 관심 품목에 추가"); } }
        public string Name { get { return Item.Name; } }
        public string NameText { get { return Name + (IsObserved ? "" : " · 관측 없음"); } }
        public string Category { get { return String.IsNullOrWhiteSpace(Item.Category) ? "분류 미확인" : Item.Category; } }
        public string SoldQuantityText { get { return Count(Item.SoldQuantity); } }
        public string TradeCountText { get { return Count(Item.TradeCount); } }
        public string TradedGoldText { get { return Price(Item.TradedGold); } }
        public string AveragePriceText { get { return !IsObserved ? "—" : Item.PriceComparable ? Price(Item.AverageSalePrice) : "옵션 제외"; } }
        public string AveragePriceDetail
        {
            get {
                if (!IsObserved) return "선택 기간에 관측된 거래가 없습니다.";
                if (!Item.PriceComparable) return "옵션별 가격 차이로 단가 비교에서 제외합니다.";
                if (!Item.AverageSalePrice.HasValue || !Item.TradedGold.HasValue || !(Item.SoldQuantity > 0))
                    return "평균 거래 단가를 계산할 정보가 없습니다.";
                return "거래 금액 " + Price(Item.TradedGold) + " G ÷ 판매 수량 " + Count(Item.SoldQuantity)
                    + "개 ≈ 평균 " + AveragePriceText + " G\n거래 " + Count(Item.TradeCount) + "건 · 소수점 버림\n"
                    + "고가·저가 거래를 모두 포함한 수량 가중 평균입니다. 일반적인 판매 가격과 다를 수 있습니다.";
            }
        }
        public string LowestPriceText { get { return !IsObserved ? "—" : referenceLowest > 0 ? Price(referenceLowest) : Item.ListingCount == 0 || Item.ListedQuantity == 0 ? "매물 없음" : "미확인"; } }
        public string LowestPriceDetail
        {
            get {
                if (!IsObserved) return "선택 기간에 관측된 시세가 없습니다.";
                if (!(referenceLowest > 0)) return LowestPriceText == "매물 없음" ? "수집 시점에 확인된 매물이 없습니다." : "수집 데이터에서 최저 단가를 확인하지 못했습니다.";
                string scope = isNameQuote ? "같은 이름 전체 매물의 최저 단가입니다. 분류와 옵션이 다를 수 있습니다." : Item.PriceComparable ? "수집 시점에 등록된 매물의 최저 단가입니다." : "옵션이 서로 다른 매물을 포함한 최저 단가입니다. 같은 옵션의 가격을 뜻하지 않습니다.";
                return "최저 단가 " + LowestPriceText + " G · 소수점 버림\n" + scope;
            }
        }
        public bool IsPriceRisk { get { return IsObserved && MarketInsights.IsPriceRisk(Item); } }
        public string RiskReason { get { return IsPriceRisk ? MarketInsights.RiskReason(Item) : ""; } }
        public string RiskMultipleText { get { decimal? multiple = MarketInsights.RiskMultiple(Item); return IsPriceRisk && multiple.HasValue ? Decimal.Truncate(multiple.Value).ToString("N0", CultureInfo.InvariantCulture) + "배" : "—"; } }
        public string ListedQuantityText { get { return Count(Item.ListedQuantity); } }
        public string ListingCountText { get { return Count(Item.ListingCount); } }
        public string OpportunityDetail { get { return riskView ? RiskReason : IsObserved ? MarketInsights.Detail(Item, opportunity) : "선택 기간에 관측 없음 · 관심 품목은 계속 보관됩니다."; } }
        public string OpportunityMetricText
        {
            get {
                if (riskView) return RiskMultipleText;
                if (!IsObserved || !MarketInsights.Matches(Item, opportunity)) return "—";
                if (opportunity == MarketOpportunity.LowSupply) return Item.ListedQuantity == 0 ? "매물 없음" : MarketInsights.RatioText(MarketInsights.Rank(Item, opportunity).Value) + "배";
                if (opportunity == MarketOpportunity.BelowAverage) {
                    decimal difference = MarketInsights.Rank(Item, opportunity).Value;
                    return (difference < 1 ? "" : "-") + MarketInsights.PercentageText(difference);
                }
                return "—";
            }
        }
        public string Detail { get { return Name + " · " + Category + " · " + (riskView || !IsObserved || opportunity != MarketOpportunity.All ? OpportunityDetail : Item.PriceComparable ? "모든 거래를 포함한 수량 가중 평균 · 고가 거래의 영향을 받을 수 있습니다." : "최저가는 옵션이 다른 매물을 포함한 참고값입니다. 평균 단가는 비교하지 않습니다."); } }
        public MarketStatisticsRow(MarketSnapshotItem item, bool isObserved = true, decimal? referenceLowest = null, bool isNameQuote = false)
        {
            Item = item; IsObserved = isObserved; SearchKey = KoreanNameSearch.Normalize(item.Name);
            this.referenceLowest = !isObserved || item.ListedQuantity == 0 || item.ListingCount == 0 ? null : referenceLowest > 0 ? referenceLowest : item.LowestListingPrice > 0 ? item.LowestListingPrice : null;
            this.isNameQuote = isNameQuote;
        }
        internal void Update(bool isWatched, MarketOpportunity mode, bool showRisk = false)
        {
            if (watched != isWatched) { watched = isWatched; Changed("IsWatched"); Changed("WatchSymbol"); Changed("WatchAction"); }
            if (opportunity != mode || riskView != showRisk) { opportunity = mode; riskView = showRisk; Changed("OpportunityDetail"); Changed("OpportunityMetricText"); Changed("Detail"); }
        }
        void Changed(string property) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(property)); }
        static string Count(long? value) { return value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "—"; }
        static string Price(decimal? value) { return value.HasValue ? Decimal.Truncate(value.Value).ToString("#,0", CultureInfo.InvariantCulture) : "—"; }
    }

    // Detaching a workspace page preserves its filters, selection and scroll.
    // Only Dispose ends observation of the shared client's local publications.
    public sealed class MarketStatisticsView : UserControl, IDisposable
    {
        sealed class PreparedSnapshot
        {
            public MarketSnapshotData Data;
            public List<MarketStatisticsRow> Day, Week;
            public PreparedSnapshot(MarketSnapshotData data) { Data = data; Day = Rows(data.Items24h, data); Week = Rows(data.Items7d, data); }
            static List<MarketStatisticsRow> Rows(List<MarketSnapshotItem> items, MarketSnapshotData data) { return items == null ? new List<MarketStatisticsRow>() : items.Where(item => item != null && !String.IsNullOrEmpty(item.Name)).Select(item => new MarketStatisticsRow(item, true, MarketPrices.LowestFor(item, data), !(item.LowestListingPrice > 0))).ToList(); }
        }
        readonly MarketSnapshotClient client;
        readonly string unavailable;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly DispatcherTimer searchDelay;
        readonly TextBlock emptyText, selectionText, criteriaText, categoryPath;
        readonly MarketCategoryPicker categoryPicker;
        readonly WatchlistStore watchlist;
        DataGridTextColumn metricColumn;
        Style listingCellStyle, opportunityCellStyle, opportunityHeaderStyle;
        MarketOpportunity metricMode;
        bool riskMetric;
        int riskCount;
        PreparedSnapshot prepared;
        Task<PreparedSnapshot> building;
        MarketSnapshotData buildingData;
        volatile bool disposed;
        bool loadedOnce, busy;
        int snapshotGeneration, renderGeneration;
        string refreshMessage = "";
        string watchlistMessage = "";
        public TextBox SearchInput { get; private set; }
        public ComboBox PeriodInput { get; private set; }
        public ComboBox SortInput { get; private set; }
        public TreeView CategoryTree { get { return categoryPicker.Tree; } }
        public string SelectedCategoryId { get { return categoryPicker.SelectedId; } }
        public bool SelectCategory(string id) { return categoryPicker.Select(id); }
        public ComboBox OpportunityInput { get; private set; }
        public CheckBox WatchlistOnlyInput { get; private set; }
        public CheckBox RiskOnlyInput { get; private set; }
        public Button RefreshButton { get; private set; }
        public DataGrid Table { get; private set; }
        public TextBlock StatusText { get; private set; }
        public MarketSnapshotData Snapshot { get { return prepared == null ? null : prepared.Data; } }
        public bool IsBusy { get { return busy; } }

        public MarketStatisticsView(MarketSnapshotClient client, string unavailableMessage = null, string watchlistPath = null)
        {
            this.client = client;
            watchlist = new WatchlistStore(watchlistPath);
            unavailable = String.IsNullOrWhiteSpace(unavailableMessage) ? "시세 서버 연결이 설정되지 않았습니다." : unavailableMessage;
            Background = Paint("#F4F6F5");
            var root = new Grid { Background = Background };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            heading.Children.Add(Text("시장 통계", 25, "#202D35", true));
            heading.Children.Add(Text("판매량과 매물 현황 · 단가와 거래 금액은 G 기준", 12, "#728087", false)); root.Children.Add(heading);
            var filters = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
            var discovery = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            discovery.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            discovery.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            discovery.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            categoryPath = Text("전체", 12, "#226C54", true); categoryPath.MinWidth = 120; categoryPath.Margin = new Thickness(1, 0, 12, 0); categoryPath.VerticalAlignment = VerticalAlignment.Center;
            categoryPath.ToolTip = "왼쪽에서 대분류나 세부 분류를 선택하세요."; discovery.Children.Add(categoryPath);
            OpportunityInput = Combo(new[] { "전체 품목", "거래 활발", "판매량 대비 매물 부족", "최근 거래가보다 저렴" }, Double.NaN);
            OpportunityInput.MinWidth = 205; OpportunityInput.Margin = new Thickness(0, 0, 14, 0); Grid.SetColumn(OpportunityInput, 1); discovery.Children.Add(OpportunityInput);
            WatchlistOnlyInput = new CheckBox { Content = "관심 품목만 · 0", FontSize = 12, Foreground = Paint("#202D35"), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
            WatchlistOnlyInput.ToolTip = "별표로 저장한 품목만 표시합니다. 분류·판매 기회·검색 조건도 함께 적용됩니다.";
            Grid.SetColumn(WatchlistOnlyInput, 2); discovery.Children.Add(WatchlistOnlyInput); Grid.SetRow(discovery, 1); Grid.SetColumnSpan(discovery, 4); filters.Children.Add(discovery);
            var riskFilters = new Grid { Margin = new Thickness(1, 8, 0, 0) };
            riskFilters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); riskFilters.ColumnDefinitions.Add(new ColumnDefinition());
            RiskOnlyInput = new CheckBox { Content = "제외된 위험군 보기 · 0종", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Paint("#AD790C"), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 16, 0) };
            RiskOnlyInput.ToolTip = "평균 거래가가 최저 단가의 5배 이상인 품목을 따로 봅니다. 분류·검색·관심 조건은 유지되며 판매 기회 조건은 잠시 해제됩니다."; riskFilters.Children.Add(RiskOnlyInput);
            var riskCriteria = Text("평균이 최저가의 5배 이상이면 기본 목록에서 제외", 11, "#728087", false); riskCriteria.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(riskCriteria, 1); riskFilters.Children.Add(riskCriteria);
            Grid.SetRow(riskFilters, 2); Grid.SetColumnSpan(riskFilters, 4); filters.Children.Add(riskFilters);
            criteriaText = Text("", 11, "#728087", false); criteriaText.Margin = new Thickness(1, 6, 0, 0); Grid.SetRow(criteriaText, 3); Grid.SetColumnSpan(criteriaText, 4); filters.Children.Add(criteriaText);
            AutomationProperties.SetName(SearchInput, "시장 통계 품목 검색"); AutomationProperties.SetName(PeriodInput, "시장 통계 기간");
            AutomationProperties.SetName(SortInput, "시장 통계 정렬"); AutomationProperties.SetName(RefreshButton, "시장 통계 갱신");
            AutomationProperties.SetName(categoryPath, "현재 시장 통계 분류"); AutomationProperties.SetName(OpportunityInput, "판매 기회 보기"); AutomationProperties.SetName(WatchlistOnlyInput, "관심 품목만 보기");
            AutomationProperties.SetName(RiskOnlyInput, "제외된 위험군 보기");
            StatusText = Text("", 11, "#728087", false); StatusText.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(StatusText, 2); root.Children.Add(StatusText);
            var results = new Grid(); results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) }); results.ColumnDefinitions.Add(new ColumnDefinition());
            categoryPicker = new MarketCategoryPicker { Margin = new Thickness(0, 0, 12, 0) }; results.Children.Add(categoryPicker);
            var tableHost = new Grid(); Table = CreateTable(); tableHost.Children.Add(Table); Grid.SetColumn(tableHost, 1); results.Children.Add(tableHost);
            emptyText = Text("", 14, "#728087", false); emptyText.Margin = new Thickness(20, 40, 20, 20);
            emptyText.VerticalAlignment = VerticalAlignment.Center; emptyText.HorizontalAlignment = HorizontalAlignment.Center; emptyText.IsHitTestVisible = false;
            tableHost.Children.Add(emptyText); Grid.SetRow(results, 3); root.Children.Add(results);
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            selectionText = Text("품목을 선택하면 분류와 가격 비교 기준을 확인할 수 있습니다.", 11, "#728087", false); footer.Children.Add(selectionText);
            footer.Children.Add(Text("완료된 수집 구간만 집계합니다. 최저가는 수집 시점의 참고값이며 옵션 차이가 있을 수 있습니다. 위험군 외 품목도 정상 가격을 보장하지 않습니다.", 10, "#728087", false)); Grid.SetRow(footer, 4); root.Children.Add(footer);
            searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) }; searchDelay.Tick += SearchElapsed;
            SearchInput.TextChanged += delegate { if (disposed) return; hint.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; searchDelay.Stop(); searchDelay.Start(); };
            SearchInput.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; searchDelay.Stop(); Render(false); } };
            PeriodInput.SelectionChanged += FilterChanged; SortInput.SelectionChanged += FilterChanged; categoryPicker.SelectionChanged += CategoryChanged; OpportunityInput.SelectionChanged += FilterChanged;
            WatchlistOnlyInput.Checked += WatchlistFilterChanged; WatchlistOnlyInput.Unchecked += WatchlistFilterChanged;
            RiskOnlyInput.Checked += WatchlistFilterChanged; RiskOnlyInput.Unchecked += WatchlistFilterChanged;
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
                prepared = next; refreshMessage = ""; UpdateCategories(); Render(true);
            } catch { if (!disposed && generation == snapshotGeneration) throw; }
            finally { if (generation == snapshotGeneration && Object.ReferenceEquals(building, work)) { building = null; buildingData = null; } }
        }
        void SetBusy(bool value) { busy = value; RefreshButton.IsEnabled = !value && client != null; RefreshButton.Content = value ? "확인 중…" : "통계 갱신"; }
        void SearchElapsed(object sender, EventArgs e) { searchDelay.Stop(); Render(false); }
        void FilterChanged(object sender, SelectionChangedEventArgs e) { searchDelay.Stop(); Render(false); }
        void CategoryChanged(object sender, EventArgs e) { searchDelay.Stop(); Render(false); }
        void WatchlistFilterChanged(object sender, RoutedEventArgs e) { searchDelay.Stop(); Render(false); }
        void UpdateCategories()
        {
            var names = prepared == null ? new List<string>() : prepared.Day.Concat(prepared.Week).Select(row => row.Category).Distinct(StringComparer.Ordinal).ToList();
            var observed = prepared == null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(prepared.Day.Concat(prepared.Week).Select(row => row.Name), StringComparer.Ordinal);
            if (watchlist.Entries.Any(name => !observed.Contains(name)) && !names.Contains("분류 미확인")) names.Add("분류 미확인");
            categoryPicker.UpdateCategories(names);
        }
        public bool SetWatched(string name, bool watched)
        {
            VerifyAccess(); if (disposed) return false;
            string error;
            if (!watchlist.TrySet(name, watched, out error)) { watchlistMessage = error; UpdateStatus(Table.Items.Count); return false; }
            watchlistMessage = "";
            if (prepared != null) foreach (var row in prepared.Day.Concat(prepared.Week).Where(row => row.Name == name)) row.Update(watched, SelectedOpportunity, RiskOnlyInput.IsChecked == true);
            WatchlistOnlyInput.Content = "관심 품목만 · " + watchlist.Count.ToString("N0");
            UpdateCategories();
            if (WatchlistOnlyInput.IsChecked == true) Render(true); else UpdateStatus(Table.Items.Count);
            return true;
        }
        MarketOpportunity SelectedOpportunity { get { return (MarketOpportunity)Math.Max(0, OpportunityInput.SelectedIndex); } }
        void UpdateCriteria()
        {
            string period = PeriodInput.SelectedIndex == 1 ? "최근 7일" : "최근 24시간";
            var selectedCategory = CategoryTree.SelectedItem as TreeViewItem;
            categoryPath.Text = selectedCategory == null ? "전체" : ((MarketCategoryNode)selectedCategory.Tag).Path;
            bool showRisk = RiskOnlyInput.IsChecked == true;
            if (showRisk) criteriaText.Text = period + " 평균 / 최저가 비율이 큰 순입니다. 가격 차이만으로 분류하며 비정상 거래를 확정하지 않습니다.";
            else switch (SelectedOpportunity) {
                case MarketOpportunity.ActiveTrading: criteriaText.Text = period + " 거래가 있는 품목 · 거래 건수가 많은 순입니다."; break;
                case MarketOpportunity.LowSupply: criteriaText.Text = period + " 판매 수량이 현재 매물보다 많은 품목 · 매물 없음, 판매/매물 비율 순입니다. 판매를 보장하지 않습니다."; break;
                case MarketOpportunity.BelowAverage: criteriaText.Text = period + " 평균보다 최저가가 낮은 품목 · 가격 차이 비율 순입니다. 평균에는 고가 거래도 포함되며, 가격 차이는 수익률이 아닙니다."; break;
                default: criteriaText.Text = "분류와 조건을 골라 비교하세요. ☆를 누르면 관심 품목으로 저장합니다."; break;
            }
            OpportunityInput.IsEnabled = !showRisk;
            SortInput.IsEnabled = !showRisk && SelectedOpportunity == MarketOpportunity.All;
            SortInput.ToolTip = showRisk ? "위험군은 평균 / 최저가 비율이 큰 순서로 정렬합니다." : SortInput.IsEnabled ? "품목 정렬 기준" : "판매 기회 보기에서는 설명에 표시된 기준으로 정렬합니다.";
            OpportunityInput.ToolTip = criteriaText.Text;
            WatchlistOnlyInput.Content = "관심 품목만 · " + watchlist.Count.ToString("N0");
            RiskOnlyInput.Content = "제외된 위험군 보기 · " + riskCount.ToString("N0") + "종";
            if (metricMode != SelectedOpportunity || riskMetric != showRisk) {
                metricMode = SelectedOpportunity;
                riskMetric = showRisk;
                bool comparison = showRisk || metricMode == MarketOpportunity.LowSupply || metricMode == MarketOpportunity.BelowAverage;
                metricColumn.Header = showRisk ? "평균/최저" : metricMode == MarketOpportunity.LowSupply ? "판매/매물" : metricMode == MarketOpportunity.BelowAverage ? "가격 차이" : "등록 건수";
                metricColumn.Binding = new Binding(comparison ? "OpportunityMetricText" : "ListingCountText");
                metricColumn.ElementStyle = comparison ? opportunityCellStyle : listingCellStyle;
                metricColumn.HeaderStyle = comparison ? opportunityHeaderStyle : Table.ColumnHeaderStyle;
            }
        }
        void Render(bool preservePosition)
        {
            if (disposed) return; int generation = ++renderGeneration;
            var selected = Table.SelectedItem as MarketStatisticsRow;
            var scroller = FindScroll(Table); double offset = preservePosition && scroller != null ? scroller.VerticalOffset : 0;
            var rows = prepared == null ? new List<MarketStatisticsRow>() : PeriodInput.SelectedIndex == 1 ? prepared.Week : prepared.Day;
            bool showRisk = RiskOnlyInput.IsChecked == true;
            riskCount = rows.Count(row => row.IsPriceRisk);
            var favorites = new HashSet<string>(watchlist.Entries, StringComparer.Ordinal);
            var mode = SelectedOpportunity; UpdateCriteria();
            if (WatchlistOnlyInput.IsChecked == true) {
                var known = new HashSet<string>(rows.Select(row => row.Name), StringComparer.Ordinal);
                var categories = prepared == null ? new Dictionary<string, string>() : prepared.Day.Concat(prepared.Week).GroupBy(row => row.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Category, StringComparer.Ordinal);
                rows = rows.Concat(favorites.Where(name => !known.Contains(name)).Select(name => {
                    var item = new MarketSnapshotItem { Name = name, Category = categories.ContainsKey(name) ? categories[name] : "분류 미확인" };
                    return new MarketStatisticsRow(item, false, prepared == null ? null : MarketPrices.LowestFor(item, prepared.Data), true);
                })).ToList();
            }
            foreach (var row in rows) row.Update(favorites.Contains(row.Name), mode, showRisk);
            string query = KoreanNameSearch.Normalize(SearchInput.Text);
            string category = SelectedCategoryId;
            var filtered = rows.Where(row => KoreanNameSearch.Contains(row.SearchKey, query)
                && MarketCategories.Matches(category, row.Category)
                && (WatchlistOnlyInput.IsChecked != true || row.IsWatched)
                && (showRisk ? row.IsPriceRisk : !row.IsPriceRisk)
                && (showRisk || mode == MarketOpportunity.All || row.IsObserved && MarketInsights.Matches(row.Item, mode)));
            IOrderedEnumerable<MarketStatisticsRow> ordered;
            if (showRisk) ordered = filtered.OrderByDescending(row => MarketInsights.RiskMultiple(row.Item));
            else if (mode != MarketOpportunity.All) ordered = filtered.OrderByDescending(row => MarketInsights.Rank(row.Item, mode));
            else switch (SortInput.SelectedIndex) {
                case 1: ordered = filtered.OrderByDescending(row => row.Item.TradeCount); break;
                case 2: ordered = filtered.OrderByDescending(row => row.Item.TradedGold); break;
                case 3: ordered = filtered.OrderByDescending(row => row.Item.ListedQuantity); break;
                default: ordered = filtered.OrderByDescending(row => row.Item.SoldQuantity); break;
            }
            var matching = ordered.ThenBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Category, StringComparer.Ordinal).ToList(); Table.ItemsSource = matching;
            if (selected != null) Table.SelectedItem = matching.FirstOrDefault(row => row.Name == selected.Name && row.Category == selected.Category);
            emptyText.Text = WatchlistOnlyInput.IsChecked == true && favorites.Count == 0 ? "관심 품목이 없습니다. 전체 목록에서 ☆를 눌러 추가하세요." : prepared == null ? client == null ? unavailable : "저장된 공통 시세가 없습니다. 통계 갱신으로 데이터를 받으세요." : showRisk ? riskCount == 0 ? "선택 기간에 5배 이상 차이 나는 위험군이 없습니다." : "현재 분류·검색·관심 조건에 맞는 위험군이 없습니다." : "현재 분류·판매 기회·검색 조건에 맞는 품목이 없습니다.";
            emptyText.Visibility = matching.Count == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateStatus(matching.Count);
            var current = Table.SelectedItem as MarketStatisticsRow; selectionText.Text = current == null ? "품목을 선택하면 분류와 가격 비교 기준을 확인할 수 있습니다." : current.Detail;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { if (disposed || generation != renderGeneration) return; var scroll = FindScroll(Table); if (scroll != null) scroll.ScrollToVerticalOffset(offset); }));
        }
        void UpdateStatus(int count)
        {
            if (prepared == null) StatusText.Text = busy ? "저장된 공통 데이터를 확인하는 중…" : client == null ? unavailable : "아직 받은 시세가 없습니다.";
            else {
                var history = Value(Snapshot.Status, "history") as IDictionary<string, object>; var listings = Value(Snapshot.Status, "listings") as IDictionary<string, object>;
                StatusText.Text = "공통 데이터 " + Stamp(Snapshot.GeneratedUtc) + " · 거래 수집 " + Published(history) + " · 매물 수집 " + (Snapshot.ListingsFetchedUtc.HasValue ? Stamp(Snapshot.ListingsFetchedUtc.Value) : "미확인") + " · " + (RiskOnlyInput.IsChecked == true ? "위험군 " + count.ToString("N0") + " / " + riskCount.ToString("N0") + "종" : count.ToString("N0") + "종 · 위험군 " + riskCount.ToString("N0") + "종 제외");
                if (Snapshot.GeneratedUtc < DateTime.UtcNow.AddHours(-2) || Equals(Value(history, "stale"), true) || Equals(Value(listings, "stale"), true)) StatusText.Text += "\n일부 수집 기록이 오래되었거나 아직 없습니다. 수집 시각을 확인하세요.";
                if (Count(Snapshot.Status, "failed_runs_7d") > 0) StatusText.Text += "\n최근 수집 실패 구간이 있어 통계에 누락이 있을 수 있습니다.";
                if (Count(Snapshot.Status, "limited_runs_7d") > 0) StatusText.Text += "\n페이지를 제한한 시범 수집 기록입니다. 전체 시장 통계가 아닙니다.";
            }
            if (!String.IsNullOrEmpty(refreshMessage)) StatusText.Text += "\n" + refreshMessage;
            if (!String.IsNullOrEmpty(watchlist.Notice)) StatusText.Text += "\n" + watchlist.Notice;
            if (!String.IsNullOrEmpty(watchlistMessage) && watchlistMessage != watchlist.Notice) StatusText.Text += "\n" + watchlistMessage;
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
            AddWatchColumn(grid);
            AddColumn(grid, "품목", "NameText", 2.8, 160, false); AddColumn(grid, "판매 수량", "SoldQuantityText", 1, 65, true);
            AddColumn(grid, "거래 건수", "TradeCountText", .9, 60, true); AddColumn(grid, "거래 금액", "TradedGoldText", 1.4, 90, true);
            var averageColumn = AddColumn(grid, "평균 단가", "AveragePriceText", 1.2, 82, true);
            var averageStyle = new Style(typeof(TextBlock), averageColumn.ElementStyle);
            averageStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("AveragePriceDetail"))); averageColumn.ElementStyle = averageStyle;
            var lowestColumn = AddColumn(grid, "최저 단가", "LowestPriceText", 1.2, 82, true);
            var lowestStyle = new Style(typeof(TextBlock), lowestColumn.ElementStyle);
            lowestStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("LowestPriceDetail"))); lowestColumn.ElementStyle = lowestStyle;
            AddColumn(grid, "매물 수량", "ListedQuantityText", 1, 65, true); metricColumn = AddColumn(grid, "등록 건수", "ListingCountText", .9, 60, true);
            listingCellStyle = metricColumn.ElementStyle;
            opportunityCellStyle = new Style(typeof(TextBlock), listingCellStyle);
            opportunityCellStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            opportunityCellStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("OpportunityDetail")));
            opportunityHeaderStyle = new Style(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle);
            opportunityHeaderStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 0, 4, 0)));
            AutomationProperties.SetName(grid, "시장 통계 품목 표"); return grid;
        }
        void AddWatchColumn(DataGrid grid)
        {
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetBinding(ContentControl.ContentProperty, new Binding("WatchSymbol")); button.SetBinding(FrameworkElement.ToolTipProperty, new Binding("WatchAction"));
            button.SetBinding(AutomationProperties.NameProperty, new Binding("WatchAction"));
            button.SetValue(Control.FontSizeProperty, 21.0); button.SetValue(FrameworkElement.WidthProperty, 30.0); button.SetValue(FrameworkElement.HeightProperty, 30.0);
            button.SetValue(Control.PaddingProperty, new Thickness(0)); button.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            button.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); button.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            button.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            var style = new Style(typeof(Button)); style.Setters.Add(new Setter(Control.ForegroundProperty, Paint("#728087"))); style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            var saved = new DataTrigger { Binding = new Binding("IsWatched"), Value = true }; saved.Setters.Add(new Setter(Control.ForegroundProperty, Paint("#AD790C"))); style.Triggers.Add(saved);
            button.SetValue(FrameworkElement.StyleProperty, style);
            var template = new ControlTemplate(typeof(Button)); var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BackgroundProperty, Paint("#EAF3E9"))); template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .6)); template.Triggers.Add(pressed);
            button.SetValue(Control.TemplateProperty, template);
            button.AddHandler(Button.ClickEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e) {
                e.Handled = true; var row = ((Button)sender).DataContext as MarketStatisticsRow; if (row != null) SetWatched(row.Name, !row.IsWatched);
            }));
            grid.Columns.Add(new DataGridTemplateColumn { Header = "관심", CellTemplate = new DataTemplate { VisualTree = button }, Width = 40, MinWidth = 40, MaxWidth = 40 });
        }
        static DataGridTextColumn AddColumn(DataGrid grid, string title, string property, double weight, double minimum, bool numeric)
        {
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, numeric ? TextAlignment.Right : TextAlignment.Left));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(7, 0, 7, 0))); style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(property)));
            var column = new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = new DataGridLength(weight, DataGridLengthUnitType.Star), MinWidth = minimum, ElementStyle = style };
            grid.Columns.Add(column); return column;
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
            categoryPicker.SelectionChanged -= CategoryChanged; OpportunityInput.SelectionChanged -= FilterChanged; WatchlistOnlyInput.Checked -= WatchlistFilterChanged; WatchlistOnlyInput.Unchecked -= WatchlistFilterChanged;
            RiskOnlyInput.Checked -= WatchlistFilterChanged; RiskOnlyInput.Unchecked -= WatchlistFilterChanged;
            searchDelay.Stop(); searchDelay.Tick -= SearchElapsed; try { lifetime.Cancel(); } catch (AggregateException) { } lifetime.Dispose(); building = null; buildingData = null;
        }
    }
}
