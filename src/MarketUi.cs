using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        Window marketWindow;
        void ShowMarketStatistics()
        {
            if (marketWindow != null) { marketWindow.Activate(); return; }
            var window = new Window { Title = "밀레시안 장부 · 시장 통계 (개발 중)", Owner = this,
                Width = 1000, Height = 720, MinWidth = 760, MinHeight = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = AppTheme.Brush("#F4F6F5") };
            marketWindow = window;
            var cancel = new CancellationTokenSource();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) {
                Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MilesianLedger/1.1.0-dev.1");
            var root = new Grid { Margin = new Thickness(24), Background = AppTheme.Brush("#F4F6F5") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            window.Content = root;
            var heading = new StackPanel();
            heading.Children.Add(T("시장 통계", 26, Ink, true));
            heading.Children.Add(T("판매량과 매물 현황을 확인하세요. 서버가 수집한 기록을 보여줍니다.", 13, Muted, false));
            heading.Margin = new Thickness(0, 0, 0, 18); root.Children.Add(heading);
            var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            Grid.SetRow(controls, 1); root.Children.Add(controls);
            var period = new ComboBox { ItemsSource = new[] { "최근 24시간", "최근 7일" }, SelectedIndex = 0, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
            var sort = new ComboBox { ItemsSource = new[] { "판매 수량순", "거래 건수순", "거래 금액순", "매물 수량순" }, SelectedIndex = 0, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
            StyleStationCombo(period, "기간"); StyleStationCombo(sort, "정렬");
            controls.Children.Add(period); controls.Children.Add(sort);
            var search = new TextBox { Width = 180, FontSize = 14, Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 0), ToolTip = "아이템 이름으로 검색" };
            controls.Children.Add(T("품목 이름  ", 13, Muted, false)); controls.Children.Add(search);
            var status = T("기록을 불러올 준비가 되었습니다.", 12, Muted, false);
            status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 0, 0, 12); Grid.SetRow(status, 2); root.Children.Add(status);
            var rows = new StackPanel();
            var scroller = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroller, 3); root.Children.Add(scroller);
            var bottom = new StackPanel(); Grid.SetRow(bottom, 4); root.Children.Add(bottom);
            var paging = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) }; bottom.Children.Add(paging);
            bottom.Children.Add(T("이미지는 준비 중입니다. 옵션이 있는 품목과 장비는 이름만으로 가격을 비교하지 않습니다.\n수집 이전·장애 구간의 거래는 누락될 수 있으며, 갱신 버튼은 저장된 통계만 다시 불러옵니다.", 11, Muted, false));
            foreach (var child in bottom.Children) { var line = child as TextBlock; if (line != null) line.TextWrapping = TextWrapping.Wrap; }
            int offset = 0, displayedOffset = 0; bool busy = false, more = false;
            Button refresh = null, previous = null, next = null;
            Func<Task> load = async delegate {
                if (busy) return;
                busy = true; refresh.IsEnabled = previous.IsEnabled = next.IsEnabled = false;
                period.IsEnabled = sort.IsEnabled = search.IsEnabled = false;
                status.Text = "시장 기록을 불러오는 중…";
                bool loaded = false;
                try {
                    var config = AuctionProxyConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "auction-proxy.json"));
                    if (!config.IsConfigured) { status.Text = config.StatusMessage; return; }
                    string route = "v1/market/rankings?window=" + (period.SelectedIndex == 1 ? "7d" : "24h")
                        + "&sort=" + new[] { "quantity", "trades", "gold", "supply" }[Math.Max(0, sort.SelectedIndex)]
                        + "&q=" + Uri.EscapeDataString(search.Text.Trim()) + "&limit=50&offset=" + offset;
                    using (var response = await client.GetAsync(new Uri(config.BaseUri, route), cancel.Token)) {
                        if (!response.IsSuccessStatusCode) {
                            status.Text = response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                                ? "시장 통계 서버를 준비 중입니다. 수집 기능이 활성화되면 이곳에서 기록을 볼 수 있습니다."
                                : "시장 기록을 불러오지 못했습니다. 잠시 후 다시 갱신해 주세요.";
                            if (rows.Children.Count > 0) status.Text += " 이전 조회 결과를 유지합니다.";
                            return;
                        }
                        var text = await response.Content.ReadAsStringAsync();
                        cancel.Token.ThrowIfCancellationRequested();
                        var data = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }.DeserializeObject(text) as IDictionary<string, object>;
                        var items = MarketValue(data, "items") as IEnumerable;
                        if (items == null) throw new InvalidDataException();
                        var preparedRows = new List<UIElement>();
                        foreach (object entry in items) {
                            var item = entry as IDictionary<string, object>; if (item == null) throw new InvalidDataException();
                            var panel = new StackPanel();
                            panel.Children.Add(T("◇  " + Convert.ToString(MarketValue(item, "name")) + "   ·   " + Convert.ToString(MarketValue(item, "category")), 16, Ink, true));
                            panel.Children.Add(T("판매 " + MarketNumber(item, "sold_quantity") + "개  ·  거래 " + MarketNumber(item, "trade_count")
                                + "건  ·  거래 금액 " + MarketNumber(item, "traded_gold") + " G", 13, Green, true));
                            panel.Children.Add(T("현재 매물 " + MarketNumber(item, "listed_quantity") + "개 (" + MarketNumber(item, "listing_count")
                                + "건)  ·  평균 거래 단가 " + MarketNumber(item, "average_sale_price") + " G  ·  최저 매물 단가 " + MarketNumber(item, "lowest_listing_price") + " G", 12, Muted, false));
                            var card = Box(panel, AppTheme.Surface, 10, new Thickness(16)); card.Margin = new Thickness(0, 0, 8, 10); preparedRows.Add(card);
                            foreach (var child in panel.Children) { var line = child as TextBlock; if (line != null) line.TextWrapping = TextWrapping.Wrap; }
                        }
                        rows.Children.Clear(); foreach (var row in preparedRows) rows.Children.Add(row);
                        if (preparedRows.Count == 0) rows.Children.Add(T("아직 수집된 기록이 없거나 검색에 해당하는 품목이 없습니다.", 15, Muted, false));
                        more = MarketValue(data, "has_more") is bool && (bool)MarketValue(data, "has_more");
                        var meta = MarketValue(data, "status") as IDictionary<string, object>;
                        var history = MarketValue(meta, "history") as IDictionary<string, object>;
                        var listings = MarketValue(meta, "listings") as IDictionary<string, object>;
                        status.Text = "거래 수집 " + MarketPublishedTime(history) + "  ·  매물 수집 " + MarketPublishedTime(listings)
                            + "  ·  " + (offset / 50 + 1) + "페이지";
                        if (Equals(MarketValue(history, "stale"), true) || Equals(MarketValue(listings, "stale"), true)) status.Text += "\n일부 수집 기록이 오래되었거나 아직 없습니다.";
                        if (MarketValue(meta, "failed_runs_7d") != null && Convert.ToInt64(MarketValue(meta, "failed_runs_7d")) > 0) status.Text += "\n최근 수집 실패 구간이 있어 통계에 누락이 있을 수 있습니다.";
                        if (MarketValue(meta, "limited_runs_7d") != null && Convert.ToInt64(MarketValue(meta, "limited_runs_7d")) > 0) status.Text += "\n페이지를 제한한 시범 수집 기록이 포함되어 있습니다. 전체 시장 통계가 아닙니다.";
                        scroller.ScrollToTop(); displayedOffset = offset; loaded = true;
                    }
                } catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) status.Text = "응답 시간이 초과되었습니다. 다시 갱신해 주세요."; }
                catch { if (!cancel.IsCancellationRequested) status.Text = "시장 기록을 확인하지 못했습니다. 이전 결과가 있다면 그대로 유지합니다."; }
                finally { if (!loaded) offset = displayedOffset; busy = false; if (!cancel.IsCancellationRequested) { refresh.IsEnabled = true; previous.IsEnabled = offset > 0; next.IsEnabled = more; period.IsEnabled = sort.IsEnabled = search.IsEnabled = true; } }
            };
            refresh = Btn("통계 갱신", async delegate { offset = 0; await load(); }, true); controls.Children.Add(refresh);
            previous = Btn("← 이전", async delegate { offset = Math.Max(0, offset - 50); await load(); }, false);
            next = Btn("다음 →", async delegate { offset += 50; await load(); }, false); paging.Children.Add(previous); paging.Children.Add(next);
            Action filtersChanged = delegate {
                if (busy) return;
                offset = 0; previous.IsEnabled = next.IsEnabled = false;
                status.Text = "조회 조건이 바뀌었습니다. 통계 갱신을 눌러 적용하세요.";
            };
            period.SelectionChanged += delegate { filtersChanged(); };
            sort.SelectionChanged += delegate { filtersChanged(); };
            search.TextChanged += delegate { filtersChanged(); };
            window.Closed += delegate { cancel.Cancel(); client.Dispose(); marketWindow = null; };
            window.Loaded += async delegate { await load(); };
            window.Show();
        }
        static object MarketValue(IDictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value : null; }
        static string MarketNumber(IDictionary<string, object> data, string key) { var value = MarketValue(data, key); return value == null ? "—" : Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("N0"); }
        static string MarketPublishedTime(IDictionary<string, object> state) {
            var published = MarketValue(state, "published") as IDictionary<string, object>; DateTimeOffset time;
            return DateTimeOffset.TryParse(Convert.ToString(MarketValue(published, "finished_at")), out time) ? time.ToLocalTime().ToString("MM/dd HH:mm") : "대기 중";
        }
    }
}
