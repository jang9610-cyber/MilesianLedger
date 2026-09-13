using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MabinogiBarter
{
    // Name selection and read-only market references. This panel has no sale
    // amount input, amount callback, or action that transfers a market price.
    public sealed class SettlementMarketPanel : Grid, IDisposable
    {
        sealed class IndexedSnapshot
        {
            public MarketSnapshotData Data;
            public MarketSearchIndex Search;
            public Dictionary<string, decimal?> Average24, Average7;
            public IndexedSnapshot(MarketSnapshotData data)
            {
                Data = data; Search = new MarketSearchIndex(data);
                Average24 = Averages(data.Items24h); Average7 = Averages(data.Items7d);
            }
            static Dictionary<string, decimal?> Averages(List<MarketSnapshotItem> items)
            {
                var result = new Dictionary<string, decimal?>(StringComparer.Ordinal);
                if (items != null) foreach (var item in items) {
                    if (item == null || String.IsNullOrWhiteSpace(item.Name)) continue;
                    // More than one category with the same name cannot supply
                    // one unambiguous average. Do not average those averages.
                    if (result.ContainsKey(item.Name)) result[item.Name] = null;
                    else result.Add(item.Name, item.PriceComparable && item.AverageSalePrice.HasValue
                        && item.AverageSalePrice.Value > 0 ? item.AverageSalePrice : null);
                }
                return result;
            }
        }

        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly DispatcherTimer inputDelay;
        Func<MarketSnapshotData> readCache;
        Func<CancellationToken, Task<MarketSnapshotResult>> refresh;
        CancellationTokenSource operation;
        IndexedSnapshot indexed;
        MarketSnapshotClient observedClient;
        Action<MarketSnapshotData> publicationListener;
        MarketSnapshotData buildingData;
        Task<IndexedSnapshot> buildingIndex;
        string unavailable = "시세 서버 연결이 설정되지 않았습니다.";
        int sourceRevision, snapshotRevision;
        volatile bool disposed;
        bool cacheAttempted, settingSelection, busy;

        public TextBox ItemNameInput;
        public Action<MarketSnapshotData> SnapshotChanged;
        public Button RefreshButton { get; private set; }
        public StackPanel ResultsPanel { get; private set; }
        public ScrollViewer ResultsScroll { get; private set; }
        public StackPanel ReferencePanel { get; private set; }
        public TextBlock StatusText { get; private set; }
        public TextBlock SourceText { get; private set; }
        public bool IsBusy { get { return busy; } }
        public string SelectedItemName { get; private set; }
        public MarketSnapshotData Snapshot { get { return indexed == null ? null : indexed.Data; } }

        public SettlementMarketPanel()
        {
            Background = AppTheme.Surface;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var body = new StackPanel(); Children.Add(body);
            body.Children.Add(Label("아이템 이름 · 초성 검색", 13, "#202D35", true));
            var inputRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            inputRow.ColumnDefinitions.Add(new ColumnDefinition());
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ItemNameInput = new TextBox { FontSize = 14, MinHeight = 38, Padding = new Thickness(9, 7, 9, 7),
                MaxLength = 200, Foreground = AppTheme.Brush("#202D35"), Background = AppTheme.Surface,
                BorderBrush = AppTheme.Brush("#DCE5DF"), BorderThickness = new Thickness(1),
                CaretBrush = AppTheme.Brush("#202D35"), SelectionBrush = AppTheme.Brush("#226C54"),
                VerticalContentAlignment = VerticalAlignment.Center };
            ItemNameInput.ToolTip = "초성·이름을 섞어 검색할 수 있습니다. 예: ㄱㅁㅈ, 가는 ㅅㅁㅊ. 판매품 이름은 직접 입력해도 됩니다.";
            AutomationProperties.SetName(ItemNameInput, "정산 아이템 이름");
            inputRow.Children.Add(ItemNameInput);
            RefreshButton = Button("시세 갱신"); RefreshButton.MinWidth = 94;
            RefreshButton.Margin = new Thickness(8, 0, 0, 0); RefreshButton.IsEnabled = false;
            RefreshButton.ToolTip = "서버가 수집한 공통 시세를 받습니다. 실시간 경매장 조회를 요청하지 않습니다.";
            AutomationProperties.SetName(RefreshButton, "정산 참고 시세 갱신");
            Grid.SetColumn(RefreshButton, 1); inputRow.Children.Add(RefreshButton); body.Children.Add(inputRow);
            SourceText = Label("저장된 시세 없음", 11, "#728087", false);
            SourceText.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(SourceText);
            StatusText = Label("", 11, "#728087", false);
            StatusText.Margin = new Thickness(0, 5, 0, 0); StatusText.Visibility = Visibility.Collapsed; body.Children.Add(StatusText);
            ResultsPanel = new StackPanel();
            ResultsScroll = new ScrollViewer { Content = ResultsPanel, MaxHeight = 180, CanContentScroll = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 7, 0, 0) };
            AutomationProperties.SetName(ResultsScroll, "정산 아이템 이름 검색 결과"); body.Children.Add(ResultsScroll);
            ReferencePanel = new StackPanel();
            var reference = new Border { Child = ReferencePanel, Background = AppTheme.Brush("#F4F6F5"),
                BorderBrush = AppTheme.Brush("#DCE5DF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12), Margin = new Thickness(0, 9, 0, 0) };
            AutomationProperties.SetName(reference, "정산 시장 참고 시세"); body.Children.Add(reference);
            inputDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            inputDelay.Tick += DelayElapsed;
            ItemNameInput.TextChanged += NameChanged;
            ItemNameInput.KeyDown += NameKeyDown;
            RefreshButton.Click += RefreshClicked;
            Loaded += PanelLoaded;
            RenderSearch();
        }

        public void Configure(Func<MarketSnapshotData> cacheReader,
            Func<CancellationToken, Task<MarketSnapshotResult>> refreshData, string unavailableMessage)
        {
            ConfigureSources(cacheReader, refreshData, unavailableMessage);
            if (!disposed && IsLoaded) ReadCacheOnce();
        }

        public void Configure(MarketSnapshotClient client)
        {
            if (client == null) { Configure(null, null, null); return; }
            ConfigureSources(client.ReadCachedData, client.RefreshAsync, null);
            if (disposed) return;
            int revision = sourceRevision;
            observedClient = client;
            publicationListener = delegate(MarketSnapshotData data) {
                if (disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                try { Dispatcher.BeginInvoke(new Action(delegate { ReceivePublication(client, data, revision); })); }
                catch (InvalidOperationException) { /* The owning dispatcher is closing. */ }
            };
            client.SnapshotPublished += publicationListener;
            if (IsLoaded) ReadCacheOnce();
        }

        void ConfigureSources(Func<MarketSnapshotData> cacheReader,
            Func<CancellationToken, Task<MarketSnapshotResult>> refreshData, string unavailableMessage)
        {
            VerifyAccess(); if (disposed) return;
            ++sourceRevision; ++snapshotRevision; StopObserving(); CancelOperation();
            buildingData = null; buildingIndex = null;
            readCache = cacheReader; refresh = refreshData;
            unavailable = String.IsNullOrWhiteSpace(unavailableMessage) ? "시세 서버 연결이 설정되지 않았습니다." : unavailableMessage;
            indexed = null; cacheAttempted = false; inputDelay.Stop();
            SetBusy(false); Message("", false); UpdateSource(); RenderSearch();
        }

        void StopObserving()
        {
            if (observedClient != null && publicationListener != null) observedClient.SnapshotPublished -= publicationListener;
            observedClient = null; publicationListener = null;
        }

        async void ReceivePublication(MarketSnapshotClient client, MarketSnapshotData data, int revision)
        {
            if (disposed || revision != sourceRevision || !Object.ReferenceEquals(observedClient, client)
                || !Object.ReferenceEquals(client.CachedData, data) || Object.ReferenceEquals(Snapshot, data)) return;
            try { await ApplyData(data, revision); }
            catch {
                if (!disposed && revision == sourceRevision)
                    Message("새 공통 시세를 표시하지 못해 기존 시세를 유지합니다.", true);
            }
        }

        void PanelLoaded(object sender, RoutedEventArgs e) { ReadCacheOnce(); }
        async void ReadCacheOnce()
        {
            if (disposed || cacheAttempted || busy || readCache == null) return;
            cacheAttempted = true;
            int revision = sourceRevision, previousSnapshot = snapshotRevision; var reader = readCache;
            bool restoring = true;
            var pending = StartOperation();
            try {
                var data = await Task.Run(delegate {
                    pending.Token.ThrowIfCancellationRequested();
                    var restored = reader();
                    pending.Token.ThrowIfCancellationRequested();
                    return restored;
                }, pending.Token);
                if (!Current(revision, pending)) return;
                restoring = false;
                // A publication received while disk I/O was pending owns the view.
                if (previousSnapshot == snapshotRevision) await ApplyData(data, revision);
            } catch (OperationCanceledException) {
                // Cancellation during disposal/reconfiguration has no UI result.
            } catch {
                if (Current(revision, pending) && (!restoring || previousSnapshot == snapshotRevision))
                    Message("저장된 시세를 읽지 못했습니다. 시세 갱신으로 다시 확인하세요.", true);
            } finally { FinishOperation(revision, pending); }
        }

        async void RefreshClicked(object sender, RoutedEventArgs e)
        {
            if (disposed || busy || refresh == null) return;
            // Serializing with the one-time cache read prevents a late disk
            // result from replacing a newer explicit refresh.
            cacheAttempted = true;
            int revision = sourceRevision; var fetch = refresh;
            var pending = StartOperation(); Message("공통 시세를 확인하는 중…", false);
            try {
                var result = await fetch(pending.Token);
                if (!Current(revision, pending)) return;
                if (result == null) throw new InvalidOperationException();
                bool failed = !String.IsNullOrWhiteSpace(result.ErrorMessage);
                if (result.Data != null && (!failed || indexed == null)
                    && !Object.ReferenceEquals(Snapshot, result.Data)) {
                    if (observedClient == null || Object.ReferenceEquals(observedClient.CachedData, result.Data))
                        await ApplyData(result.Data, revision);
                }
                if (!Current(revision, pending)) return;
                if (failed) Message(result.ErrorMessage + (indexed == null ? "" : " 저장된 시세를 유지합니다."), true);
                else if (indexed == null) Message("아직 게시된 시세가 없습니다. 이름은 그대로 입력할 수 있습니다.", false);
                else Message(result.Downloaded ? "새 공통 시세를 받았습니다." : "최신 공통 시세입니다.", false);
            } catch (OperationCanceledException) {
                if (Current(revision, pending)) Message("시세 갱신이 취소되었습니다.", false);
            } catch {
                if (Current(revision, pending)) Message(indexed == null ? "시세를 받지 못했습니다. 다시 갱신해 주세요."
                    : "시세를 받지 못해 저장된 시세를 유지합니다.", true);
            } finally { FinishOperation(revision, pending); }
        }

        CancellationTokenSource StartOperation()
        {
            operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            SetBusy(true); return operation;
        }
        bool Current(int revision, CancellationTokenSource pending)
        {
            return !disposed && revision == sourceRevision && Object.ReferenceEquals(operation, pending) && !pending.IsCancellationRequested;
        }
        void FinishOperation(int revision, CancellationTokenSource pending)
        {
            if (!disposed && revision == sourceRevision && Object.ReferenceEquals(operation, pending)) {
                operation = null; SetBusy(false);
            }
            pending.Dispose();
        }
        void CancelOperation()
        {
            var previous = operation; operation = null;
            if (previous != null) try { previous.Cancel(); } catch (AggregateException) { }
        }
        void Apply(IndexedSnapshot next)
        {
            indexed = next; UpdateSource(); RenderSearch();
            var changed = SnapshotChanged;
            if (changed != null) changed(next.Data);
        }
        async Task ApplyData(MarketSnapshotData data, int revision)
        {
            if (disposed || revision != sourceRevision || data == null || Object.ReferenceEquals(Snapshot, data)) return;
            Task<IndexedSnapshot> work;
            int publication;
            if (Object.ReferenceEquals(buildingData, data) && buildingIndex != null) {
                work = buildingIndex; publication = snapshotRevision;
            } else {
                publication = ++snapshotRevision; buildingData = data;
                work = buildingIndex = Task.Run(() => new IndexedSnapshot(data), lifetime.Token);
            }
            try {
                var next = await work;
                if (disposed || revision != sourceRevision || publication != snapshotRevision) return;
                if (!Object.ReferenceEquals(Snapshot, data)) Apply(next);
            } catch {
                if (!disposed && revision == sourceRevision && publication == snapshotRevision) throw;
            } finally {
                if (publication == snapshotRevision && Object.ReferenceEquals(buildingIndex, work)) {
                    buildingData = null; buildingIndex = null;
                }
            }
        }
        void SetBusy(bool value)
        {
            busy = value; RefreshButton.IsEnabled = !value && refresh != null;
            RefreshButton.Content = value ? "확인 중…" : "시세 갱신";
        }
        void Message(string text, bool error)
        {
            StatusText.Text = text; StatusText.Foreground = AppTheme.Brush(error ? "#916020" : "#728087");
            StatusText.Visibility = String.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }
        void UpdateSource()
        {
            if (indexed == null) { SourceText.Text = refresh == null ? unavailable : "저장된 시세 없음 · 시세 갱신으로 공통 데이터를 받으세요."; return; }
            SourceText.Text = "매물 수집 " + Stamp(indexed.Data.ListingsFetchedUtc) + " · 통계 게시 " + Stamp(indexed.Data.GeneratedUtc);
        }

        void NameChanged(object sender, TextChangedEventArgs e)
        {
            if (disposed || settingSelection) return;
            inputDelay.Stop(); SelectedItemName = null;
            ResultsPanel.Children.Clear(); ResultsScroll.Visibility = Visibility.Collapsed;
            RenderReference(null); // Clear the prior item's price before debounce.
            inputDelay.Start();
        }
        void NameKeyDown(object sender, KeyEventArgs e)
        {
            if (disposed || e.Key != Key.Enter) return;
            e.Handled = true; inputDelay.Stop(); RenderSearch();
        }
        void DelayElapsed(object sender, EventArgs e) { inputDelay.Stop(); if (!disposed) RenderSearch(); }
        void RenderSearch()
        {
            if (disposed) return;
            ResultsPanel.Children.Clear(); ResultsScroll.Visibility = Visibility.Collapsed; SelectedItemName = null;
            string query = ItemNameInput.Text;
            var matches = indexed == null ? new List<MarketSearchEntry>() : indexed.Search.Search(query, 9);
            foreach (var match in matches) if (String.Equals(match.Name, query, StringComparison.Ordinal)) {
                SelectedItemName = match.Name; RenderReference(match); return;
            }
            RenderReference(null);
            if (indexed == null || String.IsNullOrWhiteSpace(query)) return;
            for (int i = 0; i < Math.Min(8, matches.Count); ++i) {
                var entry = matches[i];
                var choose = Button(entry.Name); choose.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                choose.Content = Label(entry.Name, 12, "#202D35", false); choose.Margin = new Thickness(0, 0, 0, 4);
                choose.ToolTip = String.IsNullOrWhiteSpace(entry.Category) ? "이 이름 선택" : entry.Category + " · 이 이름 선택";
                AutomationProperties.SetName(choose, entry.Name + " 이름 선택");
                choose.Click += delegate { SelectName(entry.Name); }; ResultsPanel.Children.Add(choose);
            }
            if (matches.Count == 0) ResultsPanel.Children.Add(Label("수집한 품목에서 찾지 못했습니다. 입력한 이름을 그대로 사용할 수 있습니다.", 11, "#728087", false));
            else if (matches.Count > 8) ResultsPanel.Children.Add(Label("8개를 표시했습니다. 이름을 더 입력하면 결과를 좁힐 수 있습니다.", 11, "#728087", false));
            ResultsScroll.Visibility = Visibility.Visible; ResultsScroll.ScrollToTop();
        }
        void SelectName(string name)
        {
            if (disposed) return;
            inputDelay.Stop(); settingSelection = true;
            try { ItemNameInput.Text = name; ItemNameInput.CaretIndex = ItemNameInput.Text.Length; }
            finally { settingSelection = false; }
            RenderSearch(); ItemNameInput.Focus();
        }
        void RenderReference(MarketSearchEntry entry)
        {
            ReferencePanel.Children.Clear();
            ReferencePanel.Children.Add(Label("시장 참고", 12, "#202D35", true));
            if (entry == null) {
                ReferencePanel.Children.Add(Label(String.IsNullOrWhiteSpace(ItemNameInput.Text)
                    ? "아이템 이름을 입력하면 수집된 시세를 참고할 수 있습니다."
                    : "등록된 이름을 선택하면 참고 시세를 표시합니다. 입력한 이름은 그대로 사용할 수 있습니다.", 12, "#728087", false));
                return;
            }
            ReferencePanel.Children.Add(Label(entry.Name, 12, "#202D35", true));
            if (entry.IsEnchantScroll && !entry.EnchantNameKnown) {
                ReferencePanel.Children.Add(Label("인챈트 이름 미확인 · 가격 표시 제외", 13, "#916020", true));
                ReferencePanel.Children.Add(Label("시세 갱신 후 인챈트 이름으로 검색하세요.", 11, "#728087", false));
                return;
            }
            string lowest = entry.HasListing && entry.UnitPrice.HasValue ? Gold(entry.UnitPrice.Value)
                : entry.ListingCount > 0 ? "가격 미확인" : entry.FetchedUtc.HasValue ? "수집 당시 매물 없음" : "매물 미확인";
            ReferencePanel.Children.Add(Label("최저 매물 단가 · " + lowest, 15, "#226C54", true));
            if (entry.PriceComparable) {
                ReferencePanel.Children.Add(Label("24시간 평균 판매단가 · " + Average(indexed.Average24, entry.Name), 12, "#202D35", false));
                ReferencePanel.Children.Add(Label("7일 평균 판매단가 · " + Average(indexed.Average7, entry.Name), 12, "#202D35", false));
                if (entry.IsEnchantScroll) ReferencePanel.Children.Add(Label("같은 인챈트 이름 · 스크롤 종류 기준", 11, "#728087", false));
            } else ReferencePanel.Children.Add(Label("옵션별 가격 차이가 있는 참고 최저가입니다. 평균 판매단가는 표시하지 않습니다.", 11, "#916020", false));
            ReferencePanel.Children.Add(Label("매물 수집 " + Stamp(entry.FetchedUtc) + " · 모든 가격은 개당 기준", 11, "#728087", false));
        }

        static string Average(Dictionary<string, decimal?> values, string name)
        {
            decimal? value; return values.TryGetValue(name, out value) && value.HasValue ? Gold(value.Value) : "미확인";
        }
        static string Gold(decimal value) { return Decimal.Truncate(value).ToString("#,0", CultureInfo.CurrentCulture) + " G"; }
        static string Stamp(DateTime? value)
        {
            return value.HasValue && value.Value != DateTime.MinValue ? value.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "미확인";
        }
        static TextBlock Label(string text, double size, string color, bool bold)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = AppTheme.Brush(color),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2), LineHeight = size + 6 };
        }
        static Button Button(string text)
        {
            var button = new Button { Content = text, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(10, 6, 10, 6), Background = AppTheme.Surface,
                Foreground = AppTheme.Brush("#202D35"), BorderBrush = AppTheme.Brush("#DCE5DF"),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, MinHeight = 34 };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(FrameworkElement.VerticalAlignmentProperty, new Binding("VerticalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(presenter); template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, .84)); template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .66)); template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled);
            button.Template = template; return button;
        }
        public void Dispose()
        {
            VerifyAccess(); if (disposed) return;
            disposed = true; ++sourceRevision; ++snapshotRevision; StopObserving(); inputDelay.Stop(); CancelOperation();
            buildingData = null; buildingIndex = null;
            try { lifetime.Cancel(); } catch (AggregateException) { }
            lifetime.Dispose();
            inputDelay.Tick -= DelayElapsed; Loaded -= PanelLoaded;
            ItemNameInput.TextChanged -= NameChanged; ItemNameInput.KeyDown -= NameKeyDown;
            RefreshButton.Click -= RefreshClicked; SnapshotChanged = null;
        }
    }
}
