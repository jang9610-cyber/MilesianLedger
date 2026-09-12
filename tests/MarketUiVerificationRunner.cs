using System;
using System.IO;
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
    static readonly string Body = "{\"items\":[{\"name\":\"거미줄 (테스트 데이터)\",\"category\":\"천옷/방직\",\"sold_quantity\":12000,\"trade_count\":120,\"traded_gold\":2400000,\"listed_quantity\":1800,\"listing_count\":18,\"average_sale_price\":200,\"lowest_listing_price\":195},{\"name\":\"검 (테스트 데이터)\",\"category\":\"검\",\"sold_quantity\":3,\"trade_count\":3,\"traded_gold\":9000000,\"listed_quantity\":7,\"listing_count\":7,\"average_sale_price\":null,\"lowest_listing_price\":null}],\"has_more\":false,\"status\":{\"history\":{\"published\":{\"finished_at\":\"2026-09-12T08:00:00Z\"},\"stale\":false},\"listings\":{\"published\":{\"finished_at\":\"2026-09-12T08:00:00Z\"},\"stale\":false},\"failed_runs_7d\":0}}";
    [STAThread]
    public static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory, output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        string config = Path.Combine(root, "data", "auction-proxy.json"); byte[] original = File.ReadAllBytes(config);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var thread = new Thread(delegate() {
            while (!stopped) try { using (var client = listener.AcceptTcpClient()) using (var stream = client.GetStream()) {
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true); string first = reader.ReadLine();
                if (first == null || !first.StartsWith("GET /v1/market/rankings?")) throw new Exception("Unexpected request");
                while (!String.IsNullOrEmpty(reader.ReadLine())) { }
                Interlocked.Increment(ref requests); byte[] payload = Encoding.UTF8.GetBytes(unavailable ? "{}" : Body);
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (unavailable ? "503 Service Unavailable" : "200 OK") + "\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length); stream.Write(payload, 0, payload.Length);
            } } catch { if (!stopped) return; }
        }); thread.IsBackground = true; thread.Start();
        MainWindow main = null;
        try {
            File.WriteAllText(config, "{\"BaseUrl\":\"http://127.0.0.1:" + port + "\"}");
            var app = new Application(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            AppTheme.Initialize(Path.Combine(output, "appearance.txt")); AppMotion.ReducedMotion = true;
            main = new MainWindow(Catalog.Load(Path.Combine(root, "data", "barter-data.json")), new StateStore(Path.Combine(output, "progress.json")), true);
            main.Left = -18000; main.Top = -18000; main.ShowInTaskbar = false; main.Show();
            typeof(MainWindow).GetMethod("ShowMarketStatistics", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
            var market = (Window)typeof(MainWindow).GetField("marketWindow", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
            Wait(() => AllText(market).Contains("12,000"));
            if (requests != 1 || !AllText(market).Contains("—")) throw new Exception("Initial request or unknown price rendering failed");
            Capture(market, Path.Combine(output, "market-light.png")); AppTheme.SetDark(true); Pump();
            Capture(market, Path.Combine(output, "market-dark.png"));
            unavailable = true;
            FindButton(market, "통계 갱신").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => AllText(market).Contains("이전 조회 결과를 유지합니다"));
            if (!AllText(market).Contains("12,000") || requests != 2) throw new Exception("Failed refresh erased results or retried automatically");
            market.Close(); main.Close(); main = null;
            Console.WriteLine("PASS: cached-market UI, placeholder prices, light/dark rendering, explicit refresh, failure preserves rows; 2 local fixture requests.");
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if (main != null) main.Close(); stopped = true; listener.Stop(); File.WriteAllBytes(config, original); }
    }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void Wait(Func<bool> done) { var deadline = DateTime.UtcNow.AddSeconds(10); while (!done()) { if (DateTime.UtcNow > deadline) throw new Exception("UI timeout"); Pump(); Thread.Sleep(20); } Pump(); }
    static string AllText(DependencyObject obj) { var text = obj as TextBlock; string result = text == null ? "" : text.Text; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) result += "\n" + AllText(VisualTreeHelper.GetChild(obj, i)); return result; }
    static Button FindButton(DependencyObject obj, string name) { var button = obj as Button; if (button != null && Convert.ToString(button.Content) == name) return button; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) { var found = FindButton(VisualTreeHelper.GetChild(obj, i), name); if (found != null) return found; } return null; }
    static void Capture(Window w, string path) { w.UpdateLayout(); var visual = (FrameworkElement)w.Content; var bmp = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight)); bmp.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using (var f = File.Create(path)) encoder.Save(f); }
}
