using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        Window marketWindow;
        void ShowMarketStatistics()
        {
            if (marketWindow != null) { marketWindow.Activate(); return; }
            var window = new Window { Title = "밀레시안 장부 · 시장 통계", Owner = this,
                Width = 1000, Height = 720, MinWidth = 760, MinHeight = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = AppTheme.Brush("#F4F6F5") };
            marketWindow = window;
            var cancel = new CancellationTokenSource();
            var root = new Grid { Margin = new Thickness(24), Background = AppTheme.Brush("#F4F6F5") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            window.Content = root;
            var heading = new StackPanel();
            heading.Children.Add(T("시장 통계", 26, Ink, true));
            heading.Children.Add(T("서버가 모아둔 판매량과 매물 현황을 함께 확인하세요.", 13, Muted, false));
            heading.Margin = new Thickness(0, 0, 0, 18); root.Children.Add(heading);
            var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            Grid.SetRow(controls, 1); root.Children.Add(controls);
            var period = new ComboBox { ItemsSource = new[] { "최근 24시간", "최근 7일" }, SelectedIndex = 0, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
            var sort = new ComboBox { ItemsSource = new[] { "판매 수량순", "거래 건수순", "거래 금액순", "매물 수량순" }, SelectedIndex = 0, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
            StyleStationCombo(period, "기간"); StyleStationCombo(sort, "정렬");
            controls.Children.Add(period); controls.Children.Add(sort);
            var search = new TextBox { Width = 180, FontSize = 14, Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 0), ToolTip = "아이템 이름으로 검색" };
            controls.Children.Add(T("품목 이름  ", 13, Muted, false)); controls.Children.Add(search);
            var status = T("공통 시장 데이터를 확인하는 중…", 12, Muted, false);
            status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 0, 0, 12); Grid.SetRow(status, 2); root.Children.Add(status);
            var rows = new StackPanel();
            var scroller = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroller, 3); root.Children.Add(scroller);
            var bottom = new StackPanel(); Grid.SetRow(bottom, 4); root.Children.Add(bottom);
            var paging = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) }; bottom.Children.Add(paging);
            bottom.Children.Add(T("검색·정렬·페이지 이동은 내려받은 데이터로 바로 적용합니다. 갱신 시 새 버전만 내려받습니다.\n수집 이전·장애 구간의 거래는 누락될 수 있습니다. 장비 등 옵션이 있는 품목은 단가 비교에서 제외합니다.", 11, Muted, false));
            foreach (var child in bottom.Children) { var line = child as TextBlock; if (line != null) line.TextWrapping = TextWrapping.Wrap; }
            int offset = 0; bool busy = false;
            MarketSnapshotData snapshot = null;
            string refreshMessage = "";
            Button refresh = null, previous = null, next = null;
            Action render = delegate {
                if (cancel.IsCancellationRequested) return;
                rows.Children.Clear();
                if (snapshot == null) {
                    status.Text = busy ? "공통 시장 데이터를 확인하는 중…" : refreshMessage;
                    rows.Children.Add(T("아직 내려받은 공통 시장 데이터가 없습니다. 서버 수집이 완료되면 갱신해 주세요.", 15, Muted, false));
                    previous.IsEnabled = next.IsEnabled = false; return;
                }
                var source = period.SelectedIndex == 1 ? snapshot.Items7d : snapshot.Items24h;
                string query = search.Text.Trim();
                var filtered = source.Where(item => item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                IOrderedEnumerable<MarketSnapshotItem> ordered;
                switch (sort.SelectedIndex) {
                    case 1: ordered = filtered.OrderByDescending(item => item.TradeCount); break;
                    case 2: ordered = filtered.OrderByDescending(item => item.TradedGold); break;
                    case 3: ordered = filtered.OrderByDescending(item => item.ListedQuantity); break;
                    default: ordered = filtered.OrderByDescending(item => item.SoldQuantity); break;
                }
                var matching = ordered.ThenBy(item => item.Name, StringComparer.CurrentCulture).ToList();
                offset = Math.Min(offset, matching.Count == 0 ? 0 : (matching.Count - 1) / 50 * 50);
                foreach (var item in matching.Skip(offset).Take(50)) {
                    var panel = new StackPanel();
                    panel.Children.Add(T("◇  " + item.Name + "   ·   " + item.Category, 16, Ink, true));
                    panel.Children.Add(T("판매 " + MarketQuantity(item.SoldQuantity) + "개  ·  거래 " + MarketQuantity(item.TradeCount)
                        + "건  ·  거래 금액 " + MarketPrice(item.TradedGold) + " G", 13, Green, true));
                    string price = item.PriceComparable
                        ? "평균 거래 단가 " + MarketPrice(item.AverageSalePrice) + " G  ·  최저 매물 단가 " + MarketPrice(item.LowestListingPrice) + " G"
                        : "옵션별 가격 차이 · 단가 비교 제외";
                    panel.Children.Add(T("현재 매물 " + MarketQuantity(item.ListedQuantity) + "개 (" + MarketQuantity(item.ListingCount) + "건)  ·  " + price, 12, Muted, false));
                    foreach (var child in panel.Children) { var line = child as TextBlock; if (line != null) line.TextWrapping = TextWrapping.Wrap; }
                    var card = Box(panel, AppTheme.Surface, 10, new Thickness(16)); card.Margin = new Thickness(0, 0, 8, 10); rows.Children.Add(card);
                }
                if (matching.Count == 0) rows.Children.Add(T("검색에 해당하는 품목이 없습니다.", 15, Muted, false));
                var history = MarketValue(snapshot.Status, "history") as IDictionary<string, object>;
                var listings = MarketValue(snapshot.Status, "listings") as IDictionary<string, object>;
                status.Text = "공통 데이터 " + snapshot.GeneratedUtc.ToLocalTime().ToString("MM/dd HH:mm")
                    + "  ·  거래 수집 " + MarketPublishedTime(history) + "  ·  매물 수집 " + MarketPublishedTime(listings)
                    + "  ·  " + matching.Count.ToString("N0") + "종 · " + (offset / 50 + 1) + "페이지";
                if (!String.IsNullOrEmpty(refreshMessage)) status.Text += "\n" + refreshMessage;
                if (snapshot.GeneratedUtc < DateTime.UtcNow.AddHours(-2) || Equals(MarketValue(history, "stale"), true) || Equals(MarketValue(listings, "stale"), true))
                    status.Text += "\n일부 수집 기록이 오래되었거나 아직 없습니다. 위 수집 시각을 확인하세요.";
                if (MarketCount(snapshot.Status, "failed_runs_7d") > 0) status.Text += "\n최근 수집 실패 구간이 있어 통계에 누락이 있을 수 있습니다.";
                if (MarketCount(snapshot.Status, "limited_runs_7d") > 0) status.Text += "\n페이지를 제한한 시범 수집 기록이 포함되어 있습니다. 전체 시장 통계가 아닙니다.";
                previous.IsEnabled = offset > 0; next.IsEnabled = offset + 50 < matching.Count;
            };
            Func<Task> load = async delegate {
                if (busy) return;
                busy = true; refresh.IsEnabled = false; refreshMessage = "새 공통 데이터가 있는지 확인하는 중…"; render();
                try {
                    var config = AuctionProxyConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "auction-proxy.json"));
                    if (!config.IsConfigured) { refreshMessage = config.StatusMessage; return; }
                    var result = await MarketSnapshotClient.ForBaseUri(config.BaseUri).RefreshAsync(cancel.Token);
                    if (cancel.IsCancellationRequested) return;
                    if (result.Data != null) snapshot = result.Data;
                    refreshMessage = !String.IsNullOrEmpty(result.ErrorMessage)
                        ? result.ErrorMessage + (snapshot == null ? "" : " 이전 공통 데이터를 유지합니다.")
                        : result.Downloaded ? "새 공통 데이터를 내려받았습니다." : "최신 버전입니다. 추가 데이터 다운로드가 없습니다.";
                } catch (OperationCanceledException) { }
                catch { refreshMessage = "시장 데이터를 확인하지 못했습니다. 이전 결과가 있다면 그대로 유지합니다."; }
                finally { busy = false; if (!cancel.IsCancellationRequested) { refresh.IsEnabled = true; render(); } }
            };
            refresh = Btn("통계 갱신", async delegate { await load(); }, true); controls.Children.Add(refresh);
            previous = Btn("← 이전", delegate { offset = Math.Max(0, offset - 50); render(); scroller.ScrollToTop(); }, false);
            next = Btn("다음 →", delegate { offset += 50; render(); scroller.ScrollToTop(); }, false); paging.Children.Add(previous); paging.Children.Add(next);
            var searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            Action filtersChanged = delegate { offset = 0; render(); scroller.ScrollToTop(); };
            searchDelay.Tick += delegate { searchDelay.Stop(); filtersChanged(); };
            period.SelectionChanged += delegate { filtersChanged(); };
            sort.SelectionChanged += delegate { filtersChanged(); };
            search.TextChanged += delegate { searchDelay.Stop(); searchDelay.Start(); };
            window.Closed += delegate { searchDelay.Stop(); cancel.Cancel(); marketWindow = null; };
            window.Loaded += async delegate { await load(); };
            window.Show();
        }
        static object MarketValue(IDictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value : null; }
        static string MarketQuantity(long? value) { return value.HasValue ? value.Value.ToString("N0") : "—"; }
        static string MarketPrice(decimal? value) { return value.HasValue ? value.Value.ToString("N0") : "—"; }
        static long MarketCount(IDictionary<string, object> data, string key) { long value; return Int64.TryParse(Convert.ToString(MarketValue(data, key), CultureInfo.InvariantCulture), out value) ? value : 0; }
        static string MarketPublishedTime(IDictionary<string, object> state) {
            var published = MarketValue(state, "published") as IDictionary<string, object>; DateTimeOffset time;
            return DateTimeOffset.TryParse(Convert.ToString(MarketValue(published, "finished_at")), out time) ? time.ToLocalTime().ToString("MM/dd HH:mm") : "대기 중";
        }
    }
}
