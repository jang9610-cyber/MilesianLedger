using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MabinogiBarter;

// Standalone workspace view with a loopback server and an isolated assembly
// directory. No main-window profile, saved provider, or production cache is used.
public static class MarketUiVerificationRunner
{
    static volatile bool stopped, unavailable;
    static int requests;
    static byte[] snapshot;
    static string manifest, version;
    static void CreateFixture(int revision)
    {
        string stamp = DateTime.UtcNow.AddMinutes(-20).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var items = new List<object>();
        for (int i = 0; i < 250; i++) items.Add(new { name = i == 0 ? "거미줄 (테스트 데이터)" : "검증 재료 " + i.ToString("000"), category = "천옷/방직",
            sold_quantity = 12000 + revision - i, trade_count = 120, traded_gold = 2400000, listed_quantity = 1800, listing_count = 18,
            average_sale_price = (decimal?)200.5m, lowest_listing_price = (decimal?)195.25m, price_comparable = true });
        items.Add(new { name = "검 (테스트 데이터)", category = "검", sold_quantity = 3, trade_count = 3, traded_gold = 9000000, listed_quantity = 7, listing_count = 7,
            average_sale_price = (decimal?)999999m, lowest_listing_price = (decimal?)999999m, price_comparable = false });
        items.Add(new { name = "미확인 재료", category = "재료", sold_quantity = (long?)null, trade_count = (long?)null, traded_gold = (decimal?)null, listed_quantity = (long?)null,
            listing_count = (long?)null, average_sale_price = (decimal?)null, lowest_listing_price = (decimal?)null, price_comparable = true });
        byte[] raw = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = 2000000 }.Serialize(new { schema_version = 1, generated_at = stamp,
            items_24h = items, items_7d = items, quotes = new object[0], status = new { history = new { published = new { state = "complete", finished_at = stamp }, stale = false },
                listings = new { published = new { state = "complete", finished_at = stamp }, stale = false }, failed_runs_7d = 0 } }));
        using (var output = new MemoryStream()) { using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(raw, 0, raw.Length); snapshot = output.ToArray(); }
        using (var sha = SHA256.Create()) version = BitConverter.ToString(sha.ComputeHash(snapshot)).Replace("-", "").ToLowerInvariant();
        manifest = new JavaScriptSerializer().Serialize(new { schema_version = 1, version = version, generated_at = stamp,
            snapshot_url = "/v1/market/snapshots/" + version + ".json.gz", compressed_bytes = snapshot.Length, uncompressed_bytes = raw.Length, sha256 = version });
    }
    static void Serve(TcpListener listener)
    {
        while (!stopped) try {
            using (var socket = listener.AcceptTcpClient()) using (var stream = socket.GetStream()) {
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true); string first = reader.ReadLine();
                bool isManifest = first != null && first.StartsWith("GET /v1/market/manifest ");
                bool isSnapshot = first != null && first.StartsWith("GET /v1/market/snapshots/" + version + ".json.gz ");
                if (!isManifest && !isSnapshot) throw new Exception("Unexpected loopback request");
                bool conditional = false; string line;
                while (!String.IsNullOrEmpty(line = reader.ReadLine())) if (line.StartsWith("If-None-Match:", StringComparison.OrdinalIgnoreCase) && line.Contains(version)) conditional = true;
                Interlocked.Increment(ref requests); bool unchanged = !unavailable && isManifest && conditional;
                byte[] payload = unavailable ? Encoding.UTF8.GetBytes("{}") : unchanged ? new byte[0] : isManifest ? Encoding.UTF8.GetBytes(manifest) : snapshot;
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (unavailable ? "503 Service Unavailable" : unchanged ? "304 Not Modified" : "200 OK")
                    + "\r\nContent-Type: " + (isSnapshot ? "application/gzip" : "application/json") + "\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length); stream.Write(payload, 0, payload.Length);
            }
        } catch { if (!stopped) return; }
    }
    [STAThread] public static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output); CreateFixture(0);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new Thread(() => Serve(listener)); server.IsBackground = true; server.Start();
        Window window = null; MarketStatisticsView view = null;
        try {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            AppTheme.Initialize(Path.Combine(output, "appearance.txt")); AppTheme.SetDark(false);
            var client = MarketSnapshotClient.ForBaseUri(new Uri("http://127.0.0.1:" + port));
            view = new MarketStatisticsView(client) { Margin = new Thickness(24) };
            window = new Window { Content = view, Width = 1130, Height = 820, Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false, Background = AppTheme.Brush("#F4F6F5") };
            window.Show(); Wait(() => !view.IsBusy);
            Check(requests == 0 && view.Snapshot == null && view.Table.Items.Count == 0, "first page entry must only read local cache");
            Click(view.RefreshButton); Wait(() => !view.IsBusy && view.Table.Items.Count == 252);
            Check(requests == 2, "explicit initial refresh must obtain one manifest and snapshot");
            var sword = view.Table.Items.Cast<MarketStatisticsRow>().Single(row => row.Category == "검");
            var unknown = view.Table.Items.Cast<MarketStatisticsRow>().Single(row => row.Name == "미확인 재료");
            Check(sword.AveragePriceText == "옵션 제외" && sword.LowestPriceText == "999,999", "equipment average comparison must stay excluded while its actual minimum is visible");
            Check(unknown.SoldQuantityText == "—" && unknown.ListedQuantityText == "—" && unknown.AveragePriceText == "—", "unknown values must not become zero");
            Check(((MarketStatisticsRow)view.Table.Items[0]).AveragePriceText == "200", "fractional average display must truncate instead of round");
            Capture(window, Path.Combine(output, "market-default-light.png"));
            foreach (bool dark in new[] { false, true }) {
                AppTheme.SetDark(dark); window.Width = 830; window.Height = 650; Pump(); CheckLayout(view);
                VerifySharedControls(view, output, dark);
                Capture(window, Path.Combine(output, dark ? "market-minimum-dark.png" : "market-minimum-light.png"));
            }
            VerifyInitialSearch(view);
            Capture(window, Path.Combine(output, "market-initial-search-dark.png"));
            Search(view, "", 252);
            view.PeriodInput.SelectedIndex = 0; view.SortInput.SelectedIndex = 0; Pump();
            view.SearchInput.Text = "검 ("; Wait(() => view.Table.Items.Count == 1);
            view.SearchInput.Text = ""; Wait(() => view.Table.Items.Count == 252);
            view.PeriodInput.SelectedIndex = 1; view.SortInput.SelectedIndex = 2; Pump();
            Check(requests == 2, "filter and period changes must not make requests");
            view.Table.SelectedItem = view.Table.Items[100]; var selected = ((MarketStatisticsRow)view.Table.SelectedItem).Name;
            var scroll = Find<ScrollViewer>(view.Table); scroll.ScrollToVerticalOffset(90); Pump(); double offset = scroll.VerticalOffset;
            Check(offset > 0 && scroll.ScrollableHeight > 0, "table must own its vertical scrolling");
            VerifyStarClick(view);
            window.Content = null; Pump(); window.Content = view; Pump();
            Check(view.PeriodInput.SelectedIndex == 1 && view.SortInput.SelectedIndex == 2 && ((MarketStatisticsRow)view.Table.SelectedItem).Name == selected,
                "detaching and reattaching must preserve filters/selection");
            Check(Math.Abs(Find<ScrollViewer>(view.Table).VerticalOffset - offset) < 1 && requests == 2, "page return must preserve scroll without reloading");
            Click(view.RefreshButton); Wait(() => !view.IsBusy && view.StatusText.Text.Contains("최신 버전")); Check(requests == 3, "unchanged refresh must not fetch gzip again");
            CreateFixture(100); string newer = version;
            window.Content = null; var external = client.RefreshAsync(CancellationToken.None);
            Wait(() => external.IsCompleted && view.Snapshot.Version == newer); window.Content = view; Pump();
            Check(requests == 5 && ((MarketStatisticsRow)view.Table.SelectedItem).Name == selected && view.PeriodInput.SelectedIndex == 1,
                "shared refresh must update hidden page without resetting state");
            Check(Math.Abs(Find<ScrollViewer>(view.Table).VerticalOffset - offset) < 1, "external publication must retain table position");
            unavailable = true; Click(view.RefreshButton); Wait(() => !view.IsBusy && view.StatusText.Text.Contains("이전 공통 데이터를 유지"));
            Check(view.Table.Items.Count == 252 && requests == 6 && view.Snapshot.Version == newer, "refresh failure must retain prior data");
            view.Dispose(); unavailable = false; CreateFixture(200); var final = client.RefreshAsync(CancellationToken.None); Wait(() => final.IsCompleted);
            Check(view.Snapshot.Version == newer && requests == 8, "disposed view must stop observing shared publications");
            window.Close(); window = null; view = null;
            VerifyInsights(output);
            VerifyRiskViews(output);
            VerifyCategoryTree(output);
            VerifyPriceCategoryPolicy(output);
            Check(requests == 8, "insight filters, favorites and restart must not make network requests");
            var empty = new MarketStatisticsView(null, "오프라인 연결 미설정"); window = new Window { Content = empty, Width = 830, Height = 650, Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); Pump(); Check(!empty.RefreshButton.IsEnabled && empty.Table.Items.Count == 0, "unconfigured view must remain a local empty state"); empty.Dispose();
            Console.WriteLine("PASS embedded table, 252 rows, initial/mixed/space-insensitive Korean search preserving metric sorting and period, precise/unknown/equipment values, 1130x820 and 830x650 light/dark layout, cache-only entry, explicit refresh, hidden publication, filter/selection/scroll retention, failure fallback and disposal; 8 loopback fixture requests."); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); Console.Error.WriteLine("Requests: " + requests); if (window != null) Capture(window, Path.Combine(output, "failure.png")); return 1; }
        finally { if (view != null) view.Dispose(); if (window != null) window.Close(); stopped = true; listener.Stop(); }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void VerifySharedControls(MarketStatisticsView view, string output, bool dark)
    {
        int before = requests;
        Check(view.SearchInput.ActualHeight == 36 && view.RefreshButton.ActualHeight == 36,
            "shared search field and refresh button must align with the trade dropdown height");
        var inputBorder = view.SearchInput.Template.FindName("Frame", view.SearchInput) as Border;
        Check(inputBorder != null && inputBorder.CornerRadius.TopLeft == 8, "search field is missing the shared rounded input chrome");
        var inputs = new[] { view.PeriodInput, view.SortInput, view.OpportunityInput };
        for (int index = 0; index < inputs.Length; index++) {
            var combo = inputs[index]; int original = combo.SelectedIndex;
            combo.ApplyTemplate(); combo.IsDropDownOpen = true;
            var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
            Check(popup != null, "shared dropdown lost its native popup part");
            Wait(() => popup.IsOpen && popup.Child != null && ((FrameworkElement)popup.Child).IsLoaded);
            var menu = popup.Child as Border;
            Check(menu != null && menu.CornerRadius.TopLeft == 8 && menu.ActualWidth >= combo.ActualWidth,
                "shared dropdown popup must retain its rounded menu and minimum trigger width");
            Check(popup.PopupAnimation == PopupAnimation.None, "shared AppMotion reveal must replace the legacy popup slide animation");
            Check(SameColor(menu.Background, AppTheme.Surface) && SameColor(combo.Foreground, AppTheme.Brush("#202D35")),
                "shared dropdown did not follow the active light/dark theme");
            var selected = combo.ItemContainerGenerator.ContainerFromIndex(original) as ComboBoxItem;
            Check(selected != null && selected.IsSelected && selected.IsEnabled, "opening a shared dropdown lost its active choice");
            if (AppMotion.Enabled && menu.IsVisible) Wait(() => HasMotion(menu));
            var settled = DateTime.UtcNow.AddMilliseconds(500); Wait(() => DateTime.UtcNow >= settled);
            Check(!HasMotion(menu), "dropdown reveal left active animation clocks after arrival");
            CaptureElement(menu, Path.Combine(output, "market-dropdown-" + index + (dark ? "-dark.png" : "-light.png")));
            combo.IsDropDownOpen = false; Pump();
            PressKey(combo, Key.End); Pump(); Check(combo.SelectedIndex == combo.Items.Count - 1, "shared dropdown lost End-key selection");
            combo.SelectedIndex = original; Pump();
        }
        bool previousMotion = AppMotion.ReducedMotion;
        try {
            AppMotion.ReducedMotion = true; view.OpportunityInput.IsDropDownOpen = true; Pump();
            var popup = (Popup)view.OpportunityInput.Template.FindName("PART_Popup", view.OpportunityInput);
            Wait(() => popup.IsOpen && ((FrameworkElement)popup.Child).IsLoaded);
            Check(!HasMotion(popup.Child) && popup.Child.Opacity == 1, "reduced motion must show the dropdown immediately without residual animation");
            view.OpportunityInput.IsDropDownOpen = false; Pump();
        } finally { AppMotion.ReducedMotion = previousMotion; }
        Check(requests == before, "dropdown animations, keyboard selection and theme rendering must remain local");
    }
    static bool HasMotion(DependencyObject element)
    {
        var visual = element as UIElement;
        if (visual != null && (visual.HasAnimatedProperties || Animated(visual.RenderTransform))) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) if (HasMotion(VisualTreeHelper.GetChild(element, i))) return true;
        return false;
    }
    static bool Animated(Transform transform)
    {
        if (transform == null) return false; if (transform.HasAnimatedProperties) return true;
        var group = transform as TransformGroup; return group != null && group.Children.Any(Animated);
    }
    static bool SameColor(Brush left, Brush right)
    {
        var first = left as SolidColorBrush; var second = right as SolidColorBrush;
        return first != null && second != null && first.Color == second.Color;
    }
    static void PressKey(UIElement target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }
    static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using (var file = File.Create(path)) encoder.Save(file);
    }
    static void VerifyStarClick(MarketStatisticsView view)
    {
        view.Table.UpdateLayout();
        var container = Enumerable.Range(0, view.Table.Items.Count)
            .Select(index => view.Table.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow)
            .First(row => row != null && row.IsVisible && row.TransformToAncestor(view.Table).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight)).Top > 40);
        var button = Find<Button>(container); var item = (MarketStatisticsRow)container.Item;
        Check(button != null && button.IsHitTestVisible, "star cell has no clickable button");
        var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), view);
        DependencyObject hit = view.InputHitTest(point) as DependencyObject;
        while (hit != null && !Object.ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
        Check(Object.ReferenceEquals(hit, button), "star is covered by another hit-test surface");
        var source = view.Table.ItemsSource; var selected = view.Table.SelectedItem;
        double offset = Find<ScrollViewer>(view.Table).VerticalOffset;
        Click(button); Check(item.IsWatched && (string)button.Content == "★", "star click did not update saved state and binding");
        Check(Object.ReferenceEquals(source, view.Table.ItemsSource) && Object.ReferenceEquals(selected, view.Table.SelectedItem), "star click rebuilt the table or changed selected row");
        Check(Math.Abs(Find<ScrollViewer>(view.Table).VerticalOffset - offset) < 1, "star click jumped the viewport");
        Click(button); Check(!item.IsWatched && (string)button.Content == "☆", "second star click did not remove the favorite");
    }
    static MarketSnapshotItem InsightItem(string name, string category, long? sold, long? trades, long? listed, decimal? average, decimal? lowest, bool comparable = true)
    {
        return new MarketSnapshotItem { Name = name, Category = category, SoldQuantity = sold, TradeCount = trades, ListedQuantity = listed,
            ListingCount = listed.HasValue ? (long?)Math.Min(listed.Value, 10) : null, AverageSalePrice = average, LowestListingPrice = lowest,
            TradedGold = sold.HasValue && average.HasValue ? sold * average : null, PriceComparable = comparable };
    }
    static MarketSnapshotData InsightData(bool removeWeb)
    {
        var items = new List<MarketSnapshotItem> {
            InsightItem("거미줄", "천옷/방직", 1000, 50, 100, 200, 150),
            InsightItem("가는 실뭉치", "천옷/방직", 500, 100, 500, 300, 300),
            InsightItem("굵은 실뭉치", "천옷/방직", 200, 40, 0, 400, null),
            InsightItem("양털", "천옷/방직", 100, 5, 10, 100, 25),
            InsightItem("튼튼한 고리", "블랙스미스", 80, 12, null, 200, 100),
            InsightItem("검", "검", 3, 3, 2, 1000, 1, false),
            InsightItem("베이스 허브", "허브", 0, 0, 90, null, 10),
            InsightItem("판매 미확인", "재료", null, null, 5, null, 10),
            InsightItem("템포 (접미 / 랭크 6) · 전용 인챈트 스크롤", "인챈트 스크롤", 20, 10, 2, 100000, 80000),
            InsightItem("템포 (접미 / 랭크 6) · 인챈트 스크롤", "인챈트 스크롤", 30, 11, 30, 90000, 90000)
        };
        if (removeWeb) items.RemoveAll(item => item.Name == "거미줄");
        return new MarketSnapshotData { Version = removeWeb ? "insight-next" : "insight-first", GeneratedUtc = DateTime.UtcNow,
            ListingsFetchedUtc = DateTime.UtcNow, Items24h = items, Items7d = items.Where(item => item.Name != "거미줄").ToList(),
            Quotes = new Dictionary<string, MarketSnapshotQuote>(), Status = new Dictionary<string, object>() };
    }
    static void Publish(MarketStatisticsView view, MarketSnapshotData data)
    {
        var task = (System.Threading.Tasks.Task)typeof(MarketStatisticsView).GetMethod("ApplyData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(view, new object[] { data });
        Wait(() => task.IsCompleted); if (task.IsFaulted) throw task.Exception;
    }
    static List<MarketStatisticsRow> Rows(MarketStatisticsView view) { return view.Table.Items.Cast<MarketStatisticsRow>().ToList(); }
    static IEnumerable<TreeViewItem> CategoryNodes(ItemsControl parent)
    {
        foreach (var node in parent.Items.OfType<TreeViewItem>()) {
            yield return node;
            foreach (var child in CategoryNodes(node)) yield return child;
        }
    }
    static TreeViewItem CategoryNode(MarketStatisticsView view, string id)
    {
        return CategoryNodes(view.CategoryTree).SingleOrDefault(node => node.Tag is MarketCategoryNode && ((MarketCategoryNode)node.Tag).Id == id);
    }
    static void SelectCategory(MarketStatisticsView view, string id)
    {
        Check(view.SelectCategory(id), "unreachable category: " + id); Pump();
        Check(view.SelectedCategoryId == id, "category selection does not expose its stable id: " + id);
    }
    static MarketSnapshotData CategoryData(string version)
    {
        var items = new List<MarketSnapshotItem> {
            InsightItem("목걸이", "액세서리", 100, 10, 10, 100, 90),
            InsightItem("안경", "얼굴 장식", 100, 10, 10, 100, 90),
            InsightItem("위험 목걸이", "액세서리", 100, 10, 10, 600, 100),
            InsightItem("새로운 장식", "아직 알려지지 않은 API 분류", 100, 10, 10, 100, 90)
        };
        items.AddRange(Enumerable.Range(0, 180).Select(i => InsightItem("검증용 검 " + i.ToString("000"), "검", 100 + i, 10, 10, 100, 90, false)));
        return new MarketSnapshotData { Version = version, GeneratedUtc = DateTime.UtcNow, Items24h = items,
            Items7d = items.Where(item => item.Category != "액세서리").ToList(), Quotes = new Dictionary<string, MarketSnapshotQuote>(), Status = new Dictionary<string, object>() };
    }
    static void VerifyCategoryTree(string output)
    {
        int before = requests;
        var view = new MarketStatisticsView(null, "분류 트리 검증") { Margin = new Thickness(24) };
        var window = new Window { Content = view, Width = 1130, Height = 820, Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false, Background = AppTheme.Brush("#F4F6F5") };
        try {
            window.Show(); Publish(view, CategoryData("category-first"));
            Check(view.SelectedCategoryId == MarketCategories.AllId && view.Table.Items.Count == 183, "tree must start with all ordinary observed items");
            string groupId = MarketCategories.GroupId("액세서리"), leafId = MarketCategories.LeafId("액세서리");
            var group = CategoryNode(view, groupId); var leaf = CategoryNode(view, leafId);
            Check(group != null && leaf != null && !Object.ReferenceEquals(group, leaf)
                && ((MarketCategoryNode)group.Tag).IsGroup && !((MarketCategoryNode)leaf.Tag).IsGroup, "equal parent and leaf names must retain distinct node identities");
            group.IsExpanded = true; group.IsSelected = true; Pump();
            Check(view.SelectedCategoryId == groupId && Rows(view).Select(row => row.Name).OrderBy(name => name).SequenceEqual(new[] { "목걸이", "안경" }), "selecting a parent must include all descendants and apply ordinary risk exclusion");
            SelectCategory(view, leafId);
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == "목걸이", "same-name leaf must filter its exact API category instead of its parent's descendants");
            SelectCategory(view, groupId); Search(view, "ㅇㄱ", 1);
            Check(Rows(view)[0].Name == "안경", "initial search must intersect a category group"); Search(view, "", 2);
            Check(view.SetWatched("목걸이", true) && view.SetWatched("위험 목걸이", true), "category favorites could not be set");
            view.WatchlistOnlyInput.IsChecked = true; Pump();
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == "목걸이", "ordinary favorites must intersect the category group");
            view.RiskOnlyInput.IsChecked = true; Pump();
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == "위험 목걸이", "risk, category and favorites must intersect without hiding excluded favorites");
            SelectCategory(view, MarketCategories.LeafId("얼굴 장식")); Check(view.Table.Items.Count == 0, "risk list ignored the selected category");
            view.RiskOnlyInput.IsChecked = false; view.WatchlistOnlyInput.IsChecked = false; Pump();
            Check(Object.ReferenceEquals(view.Table.Columns.Last().HeaderStyle, view.Table.ColumnHeaderStyle),
                "leaving excluded mode must restore the themed ordinary column header");
            SelectCategory(view, leafId); CategoryNode(view, MarketCategories.GroupId("생활 재료")).IsExpanded = true;
            view.PeriodInput.SelectedIndex = 1; Pump();
            Check(view.SelectedCategoryId == leafId && view.Table.Items.Count == 0 && CategoryNode(view, groupId).IsExpanded,
                "empty period must retain the selected game category and its expanded parent");
            Publish(view, CategoryData("category-next"));
            Check(view.SelectedCategoryId == leafId && view.Table.Items.Count == 0 && CategoryNode(view, MarketCategories.GroupId("생활 재료")).IsExpanded,
                "a shared snapshot must preserve category and expansion even with no matches");
            window.Content = null; Pump(); window.Content = view; Pump();
            Check(view.SelectedCategoryId == leafId && CategoryNode(view, groupId).IsExpanded && CategoryNode(view, MarketCategories.GroupId("생활 재료")).IsExpanded,
                "navigation lost category or expanded branches");
            view.PeriodInput.SelectedIndex = 0; Pump(); Check(view.Table.Items.Count == 1, "returning to a period did not restore the selected leaf's results");
            CategoryNode(view, groupId).IsExpanded = false; Pump();
            Check(view.SelectedCategoryId == groupId && !CategoryNode(view, groupId).IsExpanded && view.Table.Items.Count == 2,
                "collapsing a selected leaf's ancestor must retain the native parent selection without reopening the branch");
            var expandedChange = CategoryData("category-rebuilt");
            expandedChange.Items24h.Add(InsightItem("새 분류 검증 품목", "추가로 도착한 API 분류", 100, 10, 10, 100, 90));
            Publish(view, expandedChange);
            Check(CategoryNode(view, MarketCategories.LeafId("추가로 도착한 API 분류")) != null && view.SelectedCategoryId == groupId
                && !CategoryNode(view, groupId).IsExpanded && view.Table.Items.Count == 2,
                "tree rebuild must preserve the intentionally collapsed selected parent and its descendant results");
            SelectCategory(view, leafId);
            Check(CategoryNode(view, groupId).IsExpanded, "explicitly reselecting a hidden leaf must reveal its ancestor");
            SelectCategory(view, MarketCategories.LeafId("아직 알려지지 않은 API 분류"));
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == "새로운 장식", "unmapped API categories must stay selectable without renaming source data");
            string remembered = view.SelectedCategoryId;
            Check(!view.SelectCategory("missing-test-category") && view.SelectedCategoryId == remembered, "invalid category selection must not reset a valid selection");
            foreach (var node in CategoryNodes(view.CategoryTree).Where(node => ((MarketCategoryNode)node.Tag).IsGroup)) node.IsExpanded = true;
            SelectCategory(view, MarketCategories.GroupId("근거리 장비"));
            Check(view.Table.Items.Count == 180 && Rows(view).All(row => row.Category == "검"), "equipment group did not include descendant sword records");
            foreach (bool dark in new[] { false, true }) {
                AppTheme.SetDark(dark); window.Width = 830; window.Height = 650; Pump(); CheckLayout(view);
                var treeScroll = Find<ScrollViewer>(view.CategoryTree); var tableScroll = Find<ScrollViewer>(view.Table);
                Check(treeScroll != null && tableScroll != null && !Object.ReferenceEquals(treeScroll, tableScroll)
                    && treeScroll.ScrollableHeight > 0 && tableScroll.ScrollableHeight > 0, "category tree and table must have independent vertical scrolling");
                treeScroll.ScrollToVerticalOffset(Math.Min(40, treeScroll.ScrollableHeight)); Pump(); double treeOffset = treeScroll.VerticalOffset;
                tableScroll.ScrollToVerticalOffset(80); Pump(); double tableOffset = tableScroll.VerticalOffset;
                Check(treeOffset > 0 && tableOffset > 0 && Math.Abs(treeScroll.VerticalOffset - treeOffset) < 1, "table scrolling moved the category sidebar");
                treeScroll.ScrollToVerticalOffset(Math.Min(70, treeScroll.ScrollableHeight)); Pump();
                Check(Math.Abs(tableScroll.VerticalOffset - tableOffset) < 1, "category scrolling moved the table");
                var treeBounds = view.CategoryTree.TransformToAncestor(view).TransformBounds(new Rect(0, 0, view.CategoryTree.ActualWidth, view.CategoryTree.ActualHeight));
                var tableBounds = view.Table.TransformToAncestor(view).TransformBounds(new Rect(0, 0, view.Table.ActualWidth, view.Table.ActualHeight));
                Check(treeBounds.Right <= tableBounds.Left && treeBounds.Top >= 0 && treeBounds.Bottom <= view.ActualHeight + 1,
                    "category sidebar must remain to the left of the table and within the available page");
                Check(tableScroll.ScrollableWidth > 0, "minimum-width table must allow access to numeric columns through its own horizontal scroll");
                Capture(window, Path.Combine(output, dark ? "market-category-minimum-dark.png" : "market-category-minimum-light.png"));
            }
            window.Width = 1130; window.Height = 820; Pump();
            SelectCategory(view, groupId); Capture(window, Path.Combine(output, "market-category-group-dark.png"));
            Check(requests == before, "category browsing, expansion, filters, periods or publications requested the network");
            Console.WriteLine("PASS game category tree: parent descendants, same-name leaf identity, unknown API categories, risk/favorite/initial intersections, empty-period/publication/navigation state and independent tree/table scrolling at minimum light/dark size; no network.");
        } catch { Capture(window, Path.Combine(output, "category-failure.png")); throw; }
        finally { view.Dispose(); window.Close(); }
    }
    static void VerifyPriceCategoryPolicy(string output)
    {
        var gem = InsightItem("고정 품목 보석", "보석", 2, 2, 5, null, null, false); gem.TradedGold = 360;
        var expensive = InsightItem("큰 가격 차이 쿠폰", "뷰티 쿠폰", 2, 2, 5, null, 100, false); expensive.TradedGold = 1200;
        var amulet = InsightItem("유동 옵션 애뮬릿", "애뮬릿", 2, 2, 5, 900, 100, true);
        var unnamed = InsightItem("전용 인챈트 스크롤", "인챈트 스크롤", 2, 2, 5, 900, 100, true);
        DateTime stamp = DateTime.UtcNow;
        var items = new List<MarketSnapshotItem> { gem, expensive, amulet, unnamed };
        var data = new MarketSnapshotData { Version = "category-policy", GeneratedUtc = stamp, ListingsFetchedUtc = stamp,
            Items24h = items, Items7d = new List<MarketSnapshotItem>(), Status = new Dictionary<string, object>(),
            Quotes = new Dictionary<string, MarketSnapshotQuote> {
                { gem.Name, new MarketSnapshotQuote { Name = gem.Name, UnitPrice = 100, ListingCount = 5, Quantity = 5, FetchedUtc = stamp } },
                { unnamed.Name, new MarketSnapshotQuote { Name = unnamed.Name, UnitPrice = 100, ListingCount = 5, Quantity = 5, FetchedUtc = stamp } }
            } };
        MarketPricePolicy.Apply(data);
        var view = new MarketStatisticsView(null, "평균 분류 검증") { Margin = new Thickness(24) };
        var window = new Window { Content = view, Width = 1130, Height = 820, Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false };
        int before = requests;
        try {
            window.Show(); Publish(view, data);
            var rows = Rows(view);
            Check(rows.Count == 3 && rows.Single(row => row.Name == gem.Name).AveragePriceText == "180"
                && rows.Single(row => row.Name == gem.Name).LowestPriceText == "100", "restored fixed-category prices did not reach the statistics table");
            var option = rows.Single(row => row.Name == amulet.Name);
            Check(option.AveragePriceText == "옵션 제외" && option.AveragePriceDetail.Contains("유동 옵션") && option.LowestPriceText == "100", "variable-option mean/lowest explanation mismatch");
            var unidentified = rows.Single(row => row.Name == unnamed.Name);
            Check(unidentified.AveragePriceText == "이름 미확인" && unidentified.AveragePriceDetail.Contains("인챈트 이름") && unidentified.LowestPriceText == "미확인", "unidentified enchant identity was treated as an equipment option");
            view.RiskOnlyInput.IsChecked = true; Pump();
            Check(Rows(view).Count == 1 && Rows(view)[0].Name == expensive.Name && Rows(view)[0].RiskMultipleText == "6배", "restored fixed-category mean bypassed five-times risk filtering");
            Check(requests == before, "restoring fixed-category means triggered a network request");
            Console.WriteLine("PASS restored fixed-category means/minima in table, variable-option and unidentified-enchant explanations, five-times filtering; no network.");
        } finally { view.Dispose(); window.Close(); }
    }
    static void VerifyRiskViews(string output)
    {
        var data = InsightData(false);
        var almond = InsightItem("아몬드", "음식", 2, 2, 349, 22350150, 190);
        var boundary = InsightItem("경계 위험 품목", "음식", 10, 2, 10, 500, 100);
        var ordinary = InsightItem("경계 아래 품목", "음식", 10, 2, 10, 499.999m, 100);
        var gear = InsightItem("옵션 검증 장비", "검", 3, 3, 2, null, null, false);
        var zero = InsightItem("매물 없는 장비", "검", 3, 3, 0, null, null, false);
        var unknown = InsightItem("가격 미확인 장비", "검", 3, 3, 2, null, null, false);
        data.Items24h.AddRange(new[] { almond, boundary, ordinary, gear, zero, unknown });
        data.Quotes[gear.Name] = new MarketSnapshotQuote { Name = gear.Name, UnitPrice = 7654321, Quantity = 2, ListingCount = 2, FetchedUtc = data.ListingsFetchedUtc.Value };
        data.Quotes[zero.Name] = new MarketSnapshotQuote { Name = zero.Name, UnitPrice = 5, Quantity = 2, ListingCount = 2, FetchedUtc = data.ListingsFetchedUtc.Value };
        Check(MarketPrices.LowestFor(gear, data) == 7654321, "legacy option metadata must use the recorded same-name quote");
        Check(MarketPrices.LowestFor(zero, data) == null && MarketPrices.LowestFor(unknown, data) == null, "empty or unknown availability invented a minimum");
        var pricedGear = InsightItem("최저가 보유 장비", "검", 3, 3, 2, null, 1234, false);
        Check(MarketPrices.LowestFor(pricedGear, data) == 1234, "option comparison flag hid an available category minimum");
        var quote = data.Quotes[gear.Name]; quote.FetchedUtc = quote.FetchedUtc.AddMinutes(-5);
        Check(MarketPrices.LowestFor(gear, data) == null, "fallback quote from another scan used the current collection timestamp");
        quote.FetchedUtc = data.ListingsFetchedUtc.Value; quote.Name = "다른 장비";
        Check(MarketPrices.LowestFor(gear, data) == null, "fallback name mismatch leaked another item's price"); quote.Name = gear.Name;
        var view = new MarketStatisticsView(null, "테스트 공통 시세") { Margin = new Thickness(24) };
        var window = new Window { Content = view, Width = 1130, Height = 820, Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false, Background = AppTheme.Brush("#F4F6F5") };
        try {
            window.Show(); Publish(view, data);
            Check(view.RiskOnlyInput.IsChecked != true && view.Table.Items.Count == 14, "default list must exclude the two observed >=5x risks");
            Check(!Rows(view).Any(row => row.Name == almond.Name || row.Name == boundary.Name) && Rows(view).Any(row => row.Name == ordinary.Name), "risk boundary classification lost full decimal precision");
            var displayedGear = Rows(view).Single(row => row.Name == gear.Name);
            Check(displayedGear.LowestPriceText == "7,654,321" && displayedGear.AveragePriceText == "옵션 제외", "legacy option equipment minimum is still hidden");
            Check(displayedGear.LowestPriceDetail.Contains("같은 이름") && displayedGear.LowestPriceDetail.Contains("옵션"), "fallback minimum does not explain its name/option scope");
            Check(Rows(view).Single(row => row.Name == zero.Name).LowestPriceText.Contains("매물 없음")
                && Rows(view).Single(row => row.Name == unknown.Name).LowestPriceText.Contains("미확인"), "no listings and no known price must remain distinct");
            view.OpportunityInput.SelectedIndex = 2; view.SortInput.SelectedIndex = 2; Pump();
            view.RiskOnlyInput.IsChecked = true; Pump();
            Check(view.Table.Items.Count == 2 && Rows(view)[0].Name == almond.Name, "excluded list must reveal all risks regardless of the remembered opportunity filter");
            Check(!view.OpportunityInput.IsEnabled && !view.SortInput.IsEnabled && (string)view.Table.Columns.Last().Header == "평균/최저", "excluded view must make its ranking and ratio clear");
            Check(Rows(view).All(row => row.RiskReason.Contains("5배") && row.IsPriceRisk), "excluded rows must carry the threshold reason");
            Search(view, "ㄱㄱ", 1); Check(Rows(view)[0].Name == boundary.Name, "initial search did not filter the excluded list");
            Search(view, "", 2); Check(view.SetWatched(almond.Name, true), "risk item could not be added to favorites");
            view.WatchlistOnlyInput.IsChecked = true; Pump(); Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == almond.Name, "watchlist intersection failed in excluded view");
            view.WatchlistOnlyInput.IsChecked = false;
            foreach (bool dark in new[] { false, true }) {
                AppTheme.SetDark(dark); window.Width = 830; window.Height = 650; Pump(); CheckLayout(view);
                var bounds = view.RiskOnlyInput.TransformToAncestor(view).TransformBounds(new Rect(0, 0, view.RiskOnlyInput.ActualWidth, view.RiskOnlyInput.ActualHeight));
                Check(bounds.Left >= -1 && bounds.Right <= view.ActualWidth + 1, "risk control overflows minimum view width");
                Capture(window, Path.Combine(output, dark ? "market-risk-minimum-dark.png" : "market-risk-minimum-light.png"));
            }
            window.Content = null; Pump(); window.Content = view; Pump();
            Check(view.RiskOnlyInput.IsChecked == true && view.Table.Items.Count == 2, "returning to the page lost excluded mode");
            view.RiskOnlyInput.IsChecked = false; Pump();
            Check(view.OpportunityInput.SelectedIndex == 2 && view.SortInput.SelectedIndex == 2 && view.OpportunityInput.IsEnabled, "returning to ordinary results lost remembered filters");
            view.OpportunityInput.SelectedIndex = 0; SelectCategory(view, MarketCategories.LeafId("음식"));
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == ordinary.Name, "ordinary category view includes excluded items");
            var next = InsightData(false); next.Items24h.Add(InsightItem(almond.Name, "음식", 2, 2, 349, 400, 190)); Publish(view, next);
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == almond.Name && Rows(view)[0].IsWatched, "fresh data did not reclassify a former risk without deleting its favorite");
            Check(data.Items24h.Contains(almond) && almond.AverageSalePrice == 22350150 && data.Items24h.Count == 16, "exclusion mutated or deleted source trades");
            Console.WriteLine("PASS exact 5x exclusion and separate risk list, mode/search/favorite intersections, reclassification on snapshots, retained raw data and recorded option minima, known-empty/unknown distinction; zero network.");
        } catch { Capture(window, Path.Combine(output, "risk-failure.png")); throw; }
        finally { view.Dispose(); window.Close(); }
    }
    static void VerifyIntegerDisplay(MarketStatisticsView view)
    {
        decimal[] values = { 200.5m, 3022.39m, 3856600.67m, -200.9m, 0.9m };
        string[] expected = { "200", "3,022", "3,856,600", "-200", "0" };
        for (int i = 0; i < values.Length; i++) {
            var item = InsightItem("절삭 검증", "재료", 1, 1, 1, values[i], values[i]);
            var row = new MarketStatisticsRow(item);
            Check(row.AveragePriceText == expected[i] && row.LowestPriceText == (values[i] > 0 ? expected[i] : "미확인") && row.TradedGoldText == expected[i],
                "market gold fields must truncate only display values toward zero");
            Check(item.AverageSalePrice == values[i] && item.LowestListingPrice == values[i] && item.TradedGold == values[i],
                "display formatting modified the source prices");
        }
        var example = InsightItem("거미줄", "재료", 967, 103, 224, 2922649m / 967m, 149);
        example.TradedGold = 2922649;
        var exampleRow = new MarketStatisticsRow(example);
        Check(exampleRow.AveragePriceDetail.Contains("2,922,649 G ÷ 판매 수량 967개 ≈ 평균 3,022 G")
            && exampleRow.AveragePriceDetail.Contains("103건") && exampleRow.AveragePriceDetail.Contains("고가·저가 거래"),
            "average tooltip must explain the observed weighted mean and its high-price sensitivity");
        var unknown = new MarketStatisticsRow(InsightItem("정보 없음", "재료", null, null, null, null, null));
        Check(!unknown.AveragePriceDetail.Contains("÷") && !unknown.AveragePriceDetail.Contains("0 G"), "unknown average must not invent a formula");
        example.PriceComparable = false;
        Check(!new MarketStatisticsRow(example).AveragePriceDetail.Contains("÷"), "equipment excluded from prices must not show a mean formula");
        Check(!new MarketStatisticsRow(example, false).AveragePriceDetail.Contains("÷"), "unobserved favorite must not show a mean formula");
        var items = new List<MarketSnapshotItem> {
            InsightItem("가 작은 차이", "재료", 100, 5, 10, 100, 31.99m),
            InsightItem("나 큰 차이", "재료", 100, 5, 10, 100, 31.17m),
            InsightItem("다 최대 근접 차이", "재료", 100, 5, 10, 100, 0.001m),
            InsightItem("라 미세 차이", "재료", 19, 5, 10, 100, 99.9m)
        };
        Publish(view, new MarketSnapshotData { Version = "integer-display", GeneratedUtc = DateTime.UtcNow,
            Items24h = items, Items7d = items, Quotes = new Dictionary<string, MarketSnapshotQuote>(), Status = new Dictionary<string, object>() });
        view.OpportunityInput.SelectedIndex = 3; Pump();
        var rows = Rows(view);
        Check(rows.Count == 3 && !rows.Any(row => row.Name == "다 최대 근접 차이"), "near-100 percent price gaps must move to the excluded risk view");
        Check(rows[0].Name == "나 큰 차이" && rows[1].Name == "가 작은 차이"
            && rows[0].OpportunityMetricText == "-68%" && rows[1].OpportunityMetricText == "-68%", "integer percentage text must preserve fractional ranking");
        Check(rows[2].OpportunityMetricText == "1% 미만" && rows[2].OpportunityDetail.Contains("1% 미만 낮음"), "a fractional percent must not display as negative zero");
        view.OpportunityInput.SelectedIndex = 2; Pump();
        Check(Rows(view).Single(row => row.Name == "라 미세 차이").OpportunityMetricText == "1배", "supply ratio must truncate for display");
        var averageColumn = (DataGridTextColumn)view.Table.Columns.Single(column => (string)column.Header == "평균 단가");
        var tooltipSetter = averageColumn.ElementStyle.Setters.OfType<Setter>().Single(setter => setter.Property == FrameworkElement.ToolTipProperty);
        Check(((System.Windows.Data.Binding)tooltipSetter.Value).Path.Path == "AveragePriceDetail", "average cells must expose calculation details on hover");
        view.OpportunityInput.SelectedIndex = 0; Pump();
    }
    static void VerifyInsights(string output)
    {
        string file = Path.Combine(output, "insights-watchlist.json");
        var view = new MarketStatisticsView(null, "테스트 공통 시세", file) { Margin = new Thickness(24) };
        var window = new Window { Content = view, Width = 1130, Height = 820, Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false, Background = AppTheme.Brush("#F4F6F5") };
        try {
            window.Show(); VerifyIntegerDisplay(view); Publish(view, InsightData(false));
            Check(CategoryNode(view, MarketCategories.LeafId("천옷/방직")) != null && CategoryNode(view, MarketCategories.LeafId("인챈트 스크롤")) != null, "observed categories must be reachable in the tree");
            SelectCategory(view, MarketCategories.LeafId("천옷/방직")); Check(view.Table.Items.Count == 4, "category filter failed");
            Search(view, "ㅅㅁㅊ", 2);
            view.OpportunityInput.SelectedIndex = 1; Pump();
            Check(Rows(view)[0].Name == "가는 실뭉치", "active trading must rank by observed trade count");
            view.OpportunityInput.SelectedIndex = 2; Pump();
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == "굵은 실뭉치", "low supply must intersect category/initial search");
            Search(view, "", 3);
            Check(Rows(view)[0].Name == "굵은 실뭉치", "confirmed zero listings must rank first without division by zero");
            Check(Rows(view)[0].OpportunityMetricText == "매물 없음" && (string)view.Table.Columns.Last().Header == "판매/매물", "low-supply metric must show no listings rather than its sort sentinel");
            SelectCategory(view, MarketCategories.AllId); view.OpportunityInput.SelectedIndex = 3; Pump();
            Check(Rows(view)[0].Name == "양털", "below-average results must rank by percentage gap");
            Check(Rows(view)[0].OpportunityMetricText == "-75%" && (string)view.Table.Columns.Last().Header == "가격 차이", "price-gap percentage is missing from the table");
            Check(Rows(view).All(row => row.Item.PriceComparable && row.Item.LowestListingPrice > 0 && row.Item.ListedQuantity > 0 && row.Item.TradeCount > 0), "unknown, unavailable and option-sensitive prices entered price comparison");
            Check(!Rows(view).Any(row => row.Name == "가는 실뭉치" || row.Name == "튼튼한 고리" || row.Name == "검"), "equal price, unknown supply or equipment passed comparison");
            foreach (bool dark in new[] { false, true }) {
                AppTheme.SetDark(dark); window.Width = 830; window.Height = 650; Pump(); CheckLayout(view);
                foreach (var control in new FrameworkElement[] { view.CategoryTree, view.OpportunityInput, view.WatchlistOnlyInput }) {
                    var bounds = control.TransformToAncestor(view).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                    Check(bounds.Left >= -1 && bounds.Right <= view.ActualWidth + 1, "insight controls overflow minimum width");
                }
                Capture(window, Path.Combine(output, dark ? "market-insights-minimum-dark.png" : "market-insights-minimum-light.png"));
            }
            view.OpportunityInput.SelectedIndex = 0; Pump();
            Check((string)view.Table.Columns.Last().Header == "등록 건수", "leaving a preset did not restore listing counts");
            Check(Object.ReferenceEquals(view.Table.Columns.Last().HeaderStyle, view.Table.ColumnHeaderStyle),
                "leaving a preset must restore the themed ordinary column header");
            Check(view.SetWatched("거미줄", true), "adding a favorite failed");
            string enchant = "템포 (접미 / 랭크 6) · 전용 인챈트 스크롤";
            Check(view.SetWatched(enchant, true), "adding named enchant favorite failed");
            Check(Rows(view).Single(row => row.Name == "거미줄").IsWatched, "row star did not update");
            view.WatchlistOnlyInput.IsChecked = true; Pump(); Check(view.Table.Items.Count == 2, "watchlist filter failed");
            Check(!Rows(view).Any(row => row.Name.EndsWith("· 인챈트 스크롤")), "normal and dedicated enchant favorites were conflated");
            SelectCategory(view, MarketCategories.LeafId("천옷/방직")); view.PeriodInput.SelectedIndex = 1; Pump();
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == "거미줄" && !Rows(view)[0].IsObserved && Rows(view)[0].SoldQuantityText == "—", "missing-period favorite must stay visible with unknown values");
            SelectCategory(view, MarketCategories.AllId); view.PeriodInput.SelectedIndex = 0; Publish(view, InsightData(true));
            Check(view.Table.Items.Count == 2 && !Rows(view).Single(row => row.Name == "거미줄").IsObserved, "new snapshot silently removed a missing favorite");
            window.Width = 1130; window.Height = 820; Capture(window, Path.Combine(output, "market-watchlist-dark.png"));
            view.Dispose(); window.Content = null;
            view = new MarketStatisticsView(null, "테스트 공통 시세", file) { Margin = new Thickness(24) }; window.Content = view;
            Publish(view, InsightData(true)); view.WatchlistOnlyInput.IsChecked = true; Pump();
            Check(view.Table.Items.Count == 2 && Rows(view).All(row => row.IsWatched), "watchlist did not survive a fresh view/store instance");
            Check(view.SetWatched(enchant, false), "removing a favorite failed"); Pump();
            Check(view.Table.Items.Count == 1 && Rows(view)[0].Name == "거미줄", "favorite removal did not update filtered list");
            Check(view.SetWatched("거미줄", false), "removing missing favorite failed"); Pump();
            Check(view.Table.Items.Count == 0, "cleared watchlist still contains rows");
            var bulk = InsightData(false);
            bulk.Items24h = Enumerable.Range(0, 25000).Select(i => InsightItem("대량 검증 재료 " + i, "분류 " + i % 10,
                1000 + i, 100 + i % 300, 10 + i % 100, 100 + i % 10, 50 + i % 10)).ToList();
            bulk.Items7d = bulk.Items24h; view.WatchlistOnlyInput.IsChecked = false; Publish(view, bulk);
            Check(view.Table.Items.Count == 25000 && view.Table.EnableRowVirtualization, "large snapshot lost rows or row virtualization");
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); SelectCategory(view, MarketCategories.LeafId("분류 0")); view.OpportunityInput.SelectedIndex = 3; Pump(); elapsed.Stop();
            Check(view.Table.Items.Count == 2500, "large snapshot category and opportunity intersection failed");
            var sourceBeforeStar = view.Table.ItemsSource;
            Check(view.SetWatched("대량 검증 재료 100", true) && Object.ReferenceEquals(sourceBeforeStar, view.Table.ItemsSource), "large-list favorite toggle rebuilt the grid");
            Console.WriteLine("25,000-item local category/opportunity change: " + elapsed.ElapsedMilliseconds + " ms; 2,500 matching virtualized rows.");
            Console.WriteLine("PASS category/opportunity/initial intersections, ranking boundaries, light/dark minimum layout, exact enchant favorites, missing-period/missing-snapshot retention and persisted restart/removal; local calculations only.");
        } catch { Capture(window, Path.Combine(output, "insights-failure.png")); throw; }
        finally { view.Dispose(); window.Close(); }
    }
    static void Search(MarketStatisticsView view, string query, int count)
    {
        var previous = view.Table.ItemsSource;
        view.SearchInput.Text = query;
        Wait(() => !Object.ReferenceEquals(previous, view.Table.ItemsSource));
        Check(view.Table.Items.Count == count, "unexpected market search matches: " + query);
    }
    static void VerifyInitialSearch(MarketStatisticsView view)
    {
        int before = requests;
        foreach (string query in new[] { "ㄱㅁㅈ", "거ㅁ줄", "거미줄(테스트데이터)", " ㄱ\tㅁ　ㅈ ", "\u1100\u1106\u110C" }) {
            Search(view, query, 1);
            Check(((MarketStatisticsRow)view.Table.Items[0]).Name == "거미줄 (테스트 데이터)", "initial search changed the published name");
        }
        view.PeriodInput.SelectedIndex = 1; view.SortInput.SelectedIndex = 2; Pump();
        Search(view, "ㄱ", 251);
        Check(((MarketStatisticsRow)view.Table.Items[0]).Name == "검 (테스트 데이터)", "initial matching replaced metric-based ranking");
        Search(view, "ㄱㅈ재ㄹ", 249);
        Check(view.PeriodInput.SelectedIndex == 1 && view.SortInput.SelectedIndex == 2, "initial search reset period or sorting");
        Search(view, "ㄱㅁㅈ", 1);
        Check(requests == before, "initial/mixed/space searches or local period/sort changes made HTTP requests");
    }
    static void Click(Button button) { Check(button.IsEnabled, "refresh button disabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    static T Find<T>(DependencyObject obj) where T : DependencyObject { var value = obj as T; if (value != null) return value; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) { var found = Find<T>(VisualTreeHelper.GetChild(obj, i)); if (found != null) return found; } return null; }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void Wait(Func<bool> done) { var deadline = DateTime.UtcNow.AddSeconds(10); while (!done()) { if (DateTime.UtcNow > deadline) throw new Exception("UI timeout"); Pump(); Thread.Sleep(10); } Pump(); }
    static void CheckLayout(MarketStatisticsView view) { view.UpdateLayout(); double minimum = Window.GetWindow(view).Height <= 650 ? 260 : 300; Check(view.Table.ActualHeight > minimum, "too little table space: " + view.Table.ActualHeight); foreach (var control in new FrameworkElement[] { view.SearchInput, view.PeriodInput, view.SortInput, view.RefreshButton, view.Table }) { var bounds = control.TransformToAncestor(view).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight)); Check(bounds.Left >= -1 && bounds.Right <= view.ActualWidth + 1, "fixed controls extend outside page"); } }
    static void Capture(Window window, string path) { window.UpdateLayout(); var visual = (FrameworkElement)window.Content; var bmp = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(AppTheme.Brush("#F4F6F5"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight)); dc.DrawRectangle(new VisualBrush(visual), null, new Rect(24, 24, visual.ActualWidth, visual.ActualHeight)); } bmp.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using (var file = File.Create(path)) encoder.Save(file); }
}
