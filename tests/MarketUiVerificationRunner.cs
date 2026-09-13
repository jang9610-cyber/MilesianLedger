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
            Check(sword.AveragePriceText == "옵션 제외" && sword.LowestPriceText == "옵션 제외", "equipment metadata prices must stay excluded");
            Check(unknown.SoldQuantityText == "—" && unknown.ListedQuantityText == "—" && unknown.AveragePriceText == "—", "unknown values must not become zero");
            Check(((MarketStatisticsRow)view.Table.Items[0]).AveragePriceText == "200.5", "fractional average must remain visible");
            Capture(window, Path.Combine(output, "market-default-light.png"));
            foreach (bool dark in new[] { false, true }) {
                AppTheme.SetDark(dark); window.Width = 830; window.Height = 650; Pump(); CheckLayout(view);
                Capture(window, Path.Combine(output, dark ? "market-minimum-dark.png" : "market-minimum-light.png"));
            }
            view.SearchInput.Text = "검 ("; Wait(() => view.Table.Items.Count == 1);
            view.SearchInput.Text = ""; Wait(() => view.Table.Items.Count == 252);
            view.PeriodInput.SelectedIndex = 1; view.SortInput.SelectedIndex = 2; Pump();
            Check(requests == 2, "filter and period changes must not make requests");
            view.Table.SelectedItem = view.Table.Items[100]; var selected = ((MarketStatisticsRow)view.Table.SelectedItem).Name;
            var scroll = Find<ScrollViewer>(view.Table); scroll.ScrollToVerticalOffset(90); Pump(); double offset = scroll.VerticalOffset;
            Check(offset > 0 && scroll.ScrollableHeight > 0, "table must own its vertical scrolling");
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
            var empty = new MarketStatisticsView(null, "오프라인 연결 미설정"); window = new Window { Content = empty, Width = 830, Height = 650, Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); Pump(); Check(!empty.RefreshButton.IsEnabled && empty.Table.Items.Count == 0, "unconfigured view must remain a local empty state"); empty.Dispose();
            Console.WriteLine("PASS embedded table, 252 rows, precise/unknown/equipment values, 1130x820 and 830x650 light/dark layout, cache-only entry, explicit refresh, hidden publication, filter/selection/scroll retention, failure fallback and disposal; 8 loopback fixture requests."); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); Console.Error.WriteLine("Requests: " + requests); if (window != null) Capture(window, Path.Combine(output, "failure.png")); return 1; }
        finally { if (view != null) view.Dispose(); if (window != null) window.Close(); stopped = true; listener.Stop(); }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Click(Button button) { Check(button.IsEnabled, "refresh button disabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    static T Find<T>(DependencyObject obj) where T : DependencyObject { var value = obj as T; if (value != null) return value; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) { var found = Find<T>(VisualTreeHelper.GetChild(obj, i)); if (found != null) return found; } return null; }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void Wait(Func<bool> done) { var deadline = DateTime.UtcNow.AddSeconds(10); while (!done()) { if (DateTime.UtcNow > deadline) throw new Exception("UI timeout"); Pump(); Thread.Sleep(10); } Pump(); }
    static void CheckLayout(MarketStatisticsView view) { view.UpdateLayout(); Check(view.Table.ActualHeight > 300, "too little table space"); foreach (var control in new FrameworkElement[] { view.SearchInput, view.PeriodInput, view.SortInput, view.RefreshButton, view.Table }) { var bounds = control.TransformToAncestor(view).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight)); Check(bounds.Left >= -1 && bounds.Right <= view.ActualWidth + 1, "fixed controls extend outside page"); } }
    static void Capture(Window window, string path) { window.UpdateLayout(); var visual = (FrameworkElement)window.Content; var bmp = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(AppTheme.Brush("#F4F6F5"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight)); dc.DrawRectangle(new VisualBrush(visual), null, new Rect(24, 24, visual.ActualWidth, visual.ActualHeight)); } bmp.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using (var file = File.Create(path)) encoder.Save(file); }
}
