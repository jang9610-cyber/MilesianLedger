using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MabinogiBarter;

// Isolated, offline UI fixture; never uses the saved provider address or player profile.
public static class MarketUiVerificationRunner
{
    static volatile bool stopped;
    static int requests;
    static volatile bool unavailable;
    static byte[] Snapshot;
    static string Manifest, Version;
    static void CreateFixture() {
        string stamp = DateTime.UtcNow.AddMinutes(-20).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var items = new[] {
            new { name = "거미줄 (테스트 데이터)", category = "천옷/방직", sold_quantity = 12000, trade_count = 120, traded_gold = 2400000, listed_quantity = 1800, listing_count = 18, average_sale_price = (int?)200, lowest_listing_price = (int?)195, price_comparable = true },
            new { name = "검 (테스트 데이터)", category = "검", sold_quantity = 3, trade_count = 3, traded_gold = 9000000, listed_quantity = 7, listing_count = 7, average_sale_price = (int?)null, lowest_listing_price = (int?)null, price_comparable = false }
        };
        byte[] raw = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { schema_version = 1, generated_at = stamp, items_24h = items, items_7d = items,
            quotes = new object[0], status = new { history = new { published = new { state = "complete", finished_at = stamp }, stale = false }, listings = new { published = new { state = "complete", finished_at = stamp }, stale = false }, failed_runs_7d = 0 } }));
        using (var output = new MemoryStream()) { using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(raw, 0, raw.Length); Snapshot = output.ToArray(); }
        using (var sha = SHA256.Create()) Version = BitConverter.ToString(sha.ComputeHash(Snapshot)).Replace("-", "").ToLowerInvariant();
        Manifest = new JavaScriptSerializer().Serialize(new { schema_version = 1, version = Version, generated_at = stamp,
            snapshot_url = "/v1/market/snapshots/" + Version + ".json.gz", compressed_bytes = Snapshot.Length, uncompressed_bytes = raw.Length, sha256 = Version });
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory, output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        string config = Path.Combine(root, "data", "auction-proxy.json"); byte[] original = File.ReadAllBytes(config);
        CreateFixture();
        typeof(MarketSnapshotClient).GetMethod("ParseSnapshot", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new JavaScriptSerializer().DeserializeObject(Manifest), Snapshot });
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var thread = new Thread(delegate() {
            while (!stopped) try { using (var client = listener.AcceptTcpClient()) using (var stream = client.GetStream()) {
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true); string first = reader.ReadLine();
                bool manifest = first != null && first.StartsWith("GET /v1/market/manifest ");
                bool snapshot = first != null && first.StartsWith("GET /v1/market/snapshots/" + Version + ".json.gz ");
                if (!manifest && !snapshot) throw new Exception("Unexpected request");
                bool conditional = false; string line;
                while (!String.IsNullOrEmpty(line = reader.ReadLine())) if (line.StartsWith("If-None-Match:", StringComparison.OrdinalIgnoreCase)) conditional = true;
                Interlocked.Increment(ref requests);
                bool unchanged = !unavailable && manifest && conditional;
                byte[] payload = unavailable ? Encoding.UTF8.GetBytes("{}") : unchanged ? new byte[0] : manifest ? Encoding.UTF8.GetBytes(Manifest) : Snapshot;
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (unavailable ? "503 Service Unavailable" : unchanged ? "304 Not Modified" : "200 OK") + "\r\nContent-Type: " + (snapshot ? "application/gzip" : "application/json") + "\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length); stream.Write(payload, 0, payload.Length);
            } } catch { if (!stopped) return; }
        }); thread.IsBackground = true; thread.Start();
        MainWindow main = null;
        try {
            File.WriteAllText(config, "{\"BaseUrl\":\"http://127.0.0.1:" + port + "\"}");
            var app = new Application(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            AppTheme.Initialize(Path.Combine(output, "appearance.txt")); AppTheme.SetDark(false); AppMotion.ReducedMotion = true;
            main = new MainWindow(Catalog.Load(Path.Combine(root, "data", "barter-data.json")), new StateStore(Path.Combine(output, "progress.json")), true);
            main.Left = -18000; main.Top = -18000; main.ShowInTaskbar = false; main.Show();
            typeof(MainWindow).GetMethod("ShowMarketStatistics", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
            var market = (Window)typeof(MainWindow).GetField("marketWindow", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
            Console.WriteLine("Waiting initial snapshot"); Wait(() => AllText(market).Contains("12,000"));
            if (requests != 2 || !AllText(market).Contains("단가 비교 제외")) throw new Exception("Initial request or unknown price rendering failed");
            Capture(market, Path.Combine(output, "market-light.png")); AppTheme.SetDark(true); Pump();
            Capture(market, Path.Combine(output, "market-dark.png"));
            var search = Find<TextBox>(market); search.Text = "검";
            Wait(() => !AllText(market).Contains("12,000"));
            search.Text = ""; Wait(() => AllText(market).Contains("12,000"));
            Find<ComboBox>(market).SelectedIndex = 1; Pump();
            if (requests != 2) throw new Exception("Local filtering or period selection made network request");
            FindButton(market, "통계 갱신").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => AllText(market).Contains("최신 버전입니다"));
            if (requests != 3) throw new Exception("Unchanged refresh downloaded a snapshot again");
            unavailable = true;
            FindButton(market, "통계 갱신").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => AllText(market).Contains("이전 공통 데이터를 유지합니다"));
            if (!AllText(market).Contains("12,000") || requests != 4) throw new Exception("Failed refresh erased results or retried automatically");
            market.Close(); main.Close(); main = null;
            Console.WriteLine("PASS: snapshot UI, local filtering/period changes without network, conditional refresh, light/dark rendering, failure preserves rows; 4 local fixture requests.");
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); Console.Error.WriteLine("Requests: " + requests); if (main != null) { var current = typeof(MainWindow).GetField("marketWindow", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main) as Window; if (current != null) Console.Error.WriteLine(AllText(current)); } return 1; }
        finally { if (main != null) main.Close(); stopped = true; listener.Stop(); File.WriteAllBytes(config, original); }
    }
    static T Find<T>(DependencyObject obj) where T : DependencyObject { var value = obj as T; if (value != null) return value; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) { var found = Find<T>(VisualTreeHelper.GetChild(obj, i)); if (found != null) return found; } return null; }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void Wait(Func<bool> done) { var deadline = DateTime.UtcNow.AddSeconds(10); while (!done()) { if (DateTime.UtcNow > deadline) throw new Exception("UI timeout"); Pump(); Thread.Sleep(20); } Pump(); }
    static string AllText(DependencyObject obj) { var text = obj as TextBlock; string result = text == null ? "" : text.Text; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) result += "\n" + AllText(VisualTreeHelper.GetChild(obj, i)); return result; }
    static Button FindButton(DependencyObject obj, string name) { var button = obj as Button; if (button != null && Convert.ToString(button.Content) == name) return button; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) { var found = FindButton(VisualTreeHelper.GetChild(obj, i), name); if (found != null) return found; } return null; }
    static void Capture(Window w, string path) { w.UpdateLayout(); var visual = (FrameworkElement)w.Content; var bmp = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight)); bmp.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using (var f = File.Create(path)) encoder.Save(f); }
}
