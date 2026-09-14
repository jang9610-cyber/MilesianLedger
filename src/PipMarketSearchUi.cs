using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class PipChecklistWindow
    {
        readonly Grid marketSearchRoot = new Grid();
        readonly CancellationTokenSource searchLifetime = new CancellationTokenSource();
        DispatcherTimer searchDelay;
        Func<MarketSnapshotData> readSearchCache;
        Func<CancellationToken, Task<MarketSnapshotResult>> refreshSearchData;
        MarketSnapshotData searchSnapshot;
        MarketSearchIndex searchIndex;
        string searchUnavailable = "시세 서버 연결이 설정되지 않았습니다.";
        bool searchBusy, searchRestoring;
        double searchOffset;
        int searchSourceRevision;

        public Button SearchTabButton { get; private set; }
        public TextBox SearchInput { get; private set; }
        public Button SearchRefreshButton { get; private set; }
        public StackPanel SearchResultsPanel { get; private set; }
        public ScrollViewer SearchResultsScroll { get; private set; }
        public TextBlock SearchStatusText { get; private set; }
        public TextBlock SearchDataText { get; private set; }
        public bool SearchBusy { get { return searchBusy; } }

        // The same verified snapshot client is shared with the main window.
        // Cache reads and typing never initiate an HTTP request.
        public void ConfigureMarketSearch(Func<MarketSnapshotData> readCache,
            Func<CancellationToken, Task<MarketSnapshotResult>> refresh, string unavailableMessage)
        {
            VerifyAccess();
            if (closed) return;
            ++searchSourceRevision;
            readSearchCache = readCache; refreshSearchData = refresh;
            searchUnavailable = String.IsNullOrWhiteSpace(unavailableMessage) ? "시세 서버 연결이 설정되지 않았습니다." : unavailableMessage;
            searchSnapshot = null; searchIndex = null;
            UpdateSearchDataLabel(); RenderMarketSearch(true);
            SearchRefreshButton.IsEnabled = !searchBusy && refresh != null;
            if (selectedTab == 3 && !searchBusy) EnterMarketSearch();
        }

        void BuildMarketSearch()
        {
            marketSearchRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            marketSearchRoot.RowDefinitions.Add(new RowDefinition());
            var top = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            var searchBox = new Grid();
            SearchInput = new TextBox { FontSize = 14, Height = 38, Padding = new Thickness(10, 7, 30, 7), MaxLength = 120,
                VerticalContentAlignment = VerticalAlignment.Center, Foreground = Ink, Background = AppTheme.Surface,
                BorderThickness = new Thickness(0), CaretBrush = Ink, SelectionBrush = Green };
            AutomationProperties.SetName(SearchInput, "PIP 아이템 이름 검색");
            SearchInput.ToolTip = "초성·이름을 섞어 검색할 수 있습니다. 예: ㄱㅁㅈ, 가는 ㅅㅁㅊ";
            var placeholder = Label("이름·초성 검색 (ㄱㅁㅈ)", 14, Muted, false);
            placeholder.Margin = new Thickness(10, 0, 30, 0); placeholder.IsHitTestVisible = false;
            var clear = SmallButton("×", delegate { SearchInput.Clear(); SearchInput.Focus(); });
            clear.Width = 26; clear.Height = 28; clear.FontSize = 18; clear.Padding = new Thickness(0);
            clear.Margin = new Thickness(0, 0, 5, 0); clear.HorizontalAlignment = HorizontalAlignment.Right;
            clear.Background = Brushes.Transparent; clear.BorderBrush = Brushes.Transparent; clear.ToolTip = "검색어 지우기";
            clear.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(clear, "PIP 검색어 지우기");
            searchBox.Children.Add(SearchInput); searchBox.Children.Add(placeholder); searchBox.Children.Add(clear);
            top.Children.Add(new Border { Child = searchBox, Background = AppTheme.Surface, BorderBrush = Line,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true });

            var source = new Grid { Margin = new Thickness(1, 8, 0, 0) };
            source.ColumnDefinitions.Add(new ColumnDefinition()); source.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SearchDataText = Label("저장된 시세 없음", 11, Muted, false); SearchDataText.TextWrapping = TextWrapping.Wrap;
            SearchDataText.Margin = new Thickness(0, 0, 6, 0);
            AutomationProperties.SetName(SearchDataText, "PIP 매물 수집 시각");
            SearchRefreshButton = SmallButton("시세 받기", RefreshMarketSearch);
            SearchRefreshButton.Height = 32; SearchRefreshButton.IsEnabled = false;
            SearchRefreshButton.ToolTip = "서버가 수집한 최신 공통 시세를 받습니다. 새 버전만 내려받으며 실시간 조회를 요청하지 않습니다.";
            AutomationProperties.SetName(SearchRefreshButton, "PIP 공통 시세 갱신");
            var sourceActions = new StackPanel { Orientation = Orientation.Horizontal };
            sourceActions.Children.Add(SearchRefreshButton);
            source.Children.Add(SearchDataText); Grid.SetColumn(sourceActions, 1); source.Children.Add(sourceActions); top.Children.Add(source);
            SearchStatusText = Label("", 11, Muted, false); SearchStatusText.TextWrapping = TextWrapping.Wrap;
            SearchStatusText.Margin = new Thickness(1, 6, 1, 0); SearchStatusText.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(SearchStatusText, "PIP 시세 갱신 상태"); top.Children.Add(SearchStatusText);
            marketSearchRoot.Children.Add(top);
            SearchResultsPanel = new StackPanel();
            SearchResultsScroll = new ScrollViewer { Content = SearchResultsPanel, Focusable = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 4, 1), CanContentScroll = false };
            AutomationProperties.SetName(SearchResultsScroll, "PIP 시세 검색 결과");
            SearchResultsScroll.ScrollChanged += delegate {
                if (selectedTab == 3 && !searchRestoring && SearchResultsScroll.IsVisible) searchOffset = SearchResultsScroll.VerticalOffset;
            };
            Grid.SetRow(SearchResultsScroll, 1); marketSearchRoot.Children.Add(SearchResultsScroll);
            searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            searchDelay.Tick += delegate { searchDelay.Stop(); RenderMarketSearch(true); };
            SearchInput.TextChanged += delegate {
                placeholder.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                clear.Visibility = SearchInput.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                searchDelay.Stop(); searchDelay.Start();
            };
            SearchInput.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key != Key.Enter) return;
                e.Handled = true; searchDelay.Stop(); RenderMarketSearch(true);
            };
            RenderMarketSearch(false);
        }

        async void EnterMarketSearch()
        {
            if (closed || searchBusy || readSearchCache == null) return;
            int revision = searchSourceRevision;
            var read = readSearchCache;
            SetSearchBusy(true);
            try {
                var data = await Task.Run(read);
                if (closed || revision != searchSourceRevision) return;
                await ApplySearchSnapshot(data, revision);
            } catch {
                if (!closed && revision == searchSourceRevision) SearchMessage("저장된 시세를 읽지 못했습니다. 시세 받기로 다시 확인하세요.", true);
            } finally {
                if (!closed) { SetSearchBusy(false); if (revision != searchSourceRevision && selectedTab == 3) EnterMarketSearch(); }
            }
        }

        async Task ApplySearchSnapshot(MarketSnapshotData data, int revision)
        {
            if (data == null || ReferenceEquals(searchSnapshot, data)) return;
            var index = await Task.Run(() => new MarketSearchIndex(data));
            if (closed || revision != searchSourceRevision) return;
            searchSnapshot = data; searchIndex = index;
            UpdateSearchDataLabel(); RenderMarketSearch(true);
        }

        async void RefreshMarketSearch()
        {
            if (closed || searchBusy || refreshSearchData == null) return;
            int revision = searchSourceRevision;
            SetSearchBusy(true); SearchMessage("새 공통 시세를 확인하는 중…", false);
            try {
                var result = await refreshSearchData(searchLifetime.Token);
                if (closed || revision != searchSourceRevision) return;
                if (result == null) throw new InvalidOperationException();
                await ApplySearchSnapshot(result.Data, revision);
                if (closed || revision != searchSourceRevision) return;
                if (!String.IsNullOrEmpty(result.ErrorMessage))
                    SearchMessage(result.ErrorMessage + (searchIndex == null ? "" : " 저장된 시세를 표시합니다."), true);
                else if (searchIndex == null) SearchMessage("아직 게시된 시세가 없습니다. 나중에 다시 받아 주세요.", true);
                else SearchMessage(result.Downloaded ? "새 공통 시세를 받았습니다." : "최신 공통 시세입니다.", false);
            } catch (OperationCanceledException) {
                if (!closed) SearchMessage("시세 받기가 취소되었습니다.", false);
            } catch {
                if (!closed && revision == searchSourceRevision)
                    SearchMessage(searchIndex == null ? "시세를 받지 못했습니다. 연결을 확인하고 다시 시도하세요." : "시세를 받지 못해 저장된 결과를 유지합니다.", true);
            } finally {
                if (!closed) { SetSearchBusy(false); if (revision != searchSourceRevision && selectedTab == 3) EnterMarketSearch(); }
            }
        }

        void SetSearchBusy(bool value)
        {
            searchBusy = value;
            SearchRefreshButton.IsEnabled = !value && refreshSearchData != null;
            SearchRefreshButton.Content = value ? "확인 중…" : searchIndex == null ? "시세 받기" : "시세 갱신";
        }

        void SearchMessage(string text, bool error)
        {
            SearchStatusText.Text = text;
            SearchStatusText.Foreground = error ? Paint("#916020") : Muted;
            SearchStatusText.Visibility = String.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void UpdateSearchDataLabel()
        {
            if (searchIndex == null) { SearchDataText.Text = "저장된 시세 없음"; return; }
            var fetched = searchSnapshot.ListingsFetchedUtc;
            SearchDataText.Text = (fetched.HasValue ? "매물 " + fetched.Value.ToLocalTime().ToString("MM/dd HH:mm") + " 수집" : "매물 수집 시각 미확인")
                + "\n" + searchIndex.Count.ToString("N0") + "종 검색 가능";
            SearchDataText.ToolTip = "매물은 서버에서 정기 수집합니다. 시세 갱신을 눌러도 수집 시각이 같으면 같은 가격입니다.";
            if (fetched.HasValue && fetched.Value < DateTime.UtcNow.AddHours(-6)) SearchDataText.Text += " · 오래된 시세";
        }

        void RenderMarketSearch(bool resetScroll)
        {
            if (closed || SearchResultsPanel == null) return;
            SearchResultsPanel.Children.Clear();
            if (resetScroll) { searchOffset = 0; SearchResultsScroll.ScrollToTop(); }
            if (searchIndex == null) {
                AddSearchNote(refreshSearchData == null ? searchUnavailable : "시세 받기를 누르면 전체 품목을 검색할 수 있습니다. 받은 시세는 오프라인에서도 볼 수 있어요.");
                return;
            }
            if (String.IsNullOrWhiteSpace(SearchInput.Text)) { AddSearchNote("드랍 아이템이 얼마인지 궁금한가요?\n위에 아이템 이름을 입력해 보세요."); return; }
            var entries = searchIndex.Search(SearchInput.Text, 31);
            if (entries.Count == 0) { AddSearchNote("수집한 품목에서 찾지 못했습니다.\n이름을 짧게 입력하거나 시세를 갱신해 보세요."); return; }
            for (int i = 0; i < Math.Min(30, entries.Count); ++i) SearchResultsPanel.Children.Add(CreateSearchResult(entries[i]));
            if (entries.Count > 30) AddSearchNote("일치하는 품목 30개를 표시했습니다.\n이름을 더 입력하면 결과를 좁힐 수 있어요.");
        }

        void AddSearchNote(string text)
        {
            var note = Label(text, 13, Muted, false); note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 22; note.Margin = new Thickness(8, 15, 8, 15); SearchResultsPanel.Children.Add(note);
        }

        Border CreateSearchResult(MarketSearchEntry entry)
        {
            var body = new StackPanel();
            var title = new Grid(); title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); title.ColumnDefinitions.Add(new ColumnDefinition());
            ImageSource icon = icons == null ? null : icons(entry.Name);
            FrameworkElement symbol;
            if (icon != null) {
                var image = new Image { Source = icon, Width = 32, Height = 32, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor); symbol = image;
            } else symbol = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Background = Paint("#EBF1EC"),
                Child = new TextBlock { Text = "◇", FontSize = 22, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
            symbol.HorizontalAlignment = HorizontalAlignment.Left; symbol.VerticalAlignment = VerticalAlignment.Top;
            title.Children.Add(symbol);
            var name = Label(entry.Name, 14, Ink, true); name.TextWrapping = TextWrapping.Wrap; name.LineHeight = 20;
            name.ToolTip = String.IsNullOrEmpty(entry.Category) ? entry.Name : entry.Name + "\n" + entry.Category;
            Grid.SetColumn(name, 1); title.Children.Add(name); body.Children.Add(title);
            bool priced = entry.HasListing && entry.UnitPrice.HasValue;
            bool unidentifiedEnchant = entry.IsEnchantScroll && !entry.EnchantNameKnown;
            var price = Label(unidentifiedEnchant ? "인챈트 이름 미확인" : priced ? Decimal.Truncate(entry.UnitPrice.Value).ToString("#,0", CultureInfo.CurrentCulture) + " G" : entry.ListingCount > 0 ? "가격 미확인" : entry.FetchedUtc.HasValue ? "수집 당시 매물 없음" : "매물 미확인", 17, priced ? Green : Muted, true);
            price.Margin = new Thickness(0, 9, 0, 2); price.TextWrapping = TextWrapping.Wrap; body.Children.Add(price);
            if (unidentifiedEnchant) {
                var unidentified = Label("시세 갱신 후 이름으로 검색하세요.", 11, Muted, false);
                unidentified.TextWrapping = TextWrapping.Wrap; body.Children.Add(unidentified);
            } else if (priced) {
                string availability = "개당 최저가 · " + entry.ListingCount.ToString("N0") + "건";
                if (entry.QuantityKnown) availability += " / " + entry.Quantity.ToString("N0") + "개";
                var amount = Label(availability, 11, Muted, false); amount.TextWrapping = TextWrapping.Wrap; body.Children.Add(amount);
                if (entry.IsEnchantScroll) {
                    var scroll = Label("같은 인챈트 이름 · 스크롤 종류 기준", 11, Muted, false);
                    scroll.Margin = new Thickness(0, 4, 0, 0); scroll.TextWrapping = TextWrapping.Wrap;
                    body.Children.Add(scroll);
                } else if (!entry.PriceComparable) {
                    var options = Label("옵션별 가격 차이 · 참고 최저가", 11, Paint("#916020"), false);
                    options.Margin = new Thickness(0, 4, 0, 0); options.TextWrapping = TextWrapping.Wrap;
                    options.ToolTip = "같은 이름의 모든 옵션을 합친 최저 매물입니다. 드랍된 장비의 강화·인챈트·세공 옵션별 가치를 뜻하지 않습니다.";
                    body.Children.Add(options);
                }
            }
            var card = new Border { Child = body, Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 7),
                Background = AppTheme.Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
            card.ToolTip = entry.FetchedUtc.HasValue ? "매물 수집 " + entry.FetchedUtc.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "매물 수집 기록이 없습니다.";
            AutomationProperties.SetName(card, entry.Name + " 시세"); return card;
        }

        void SaveSearchOffset()
        {
            if (selectedTab == 3 && !searchRestoring && SearchResultsScroll.IsVisible) searchOffset = SearchResultsScroll.VerticalOffset;
        }

        void RestoreSearchOffset()
        {
            searchRestoring = true;
            double offset = searchOffset;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                if (!closed && selectedTab == 3) { SearchResultsScroll.UpdateLayout(); SearchResultsScroll.ScrollToVerticalOffset(offset); }
                searchRestoring = false;
            }));
        }

        void CloseMarketSearch()
        {
            searchDelay.Stop(); searchLifetime.Cancel(); searchLifetime.Dispose();
        }
    }
}
