using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MabinogiBarter;

// Offline WPF fixture: no MainWindow, player catalog, provider, or real cache.
public static class PipSearchUiVerificationRunner
{
    const string ExactName = "오프라인 신규 거미줄";
    const string LongName = "아주 긴 이름의 오프라인 신규 아이템과 특별한 장식이 달린 프리미엄 모험가용 한정 거래 재료";
    static readonly List<PipChecklistWindow> windows = new List<PipChecklistWindow>();
    static readonly List<string> passed = new List<string>();
    static PipChecklistWindow current;
    static int refreshCalls, cacheReads, checkCalls;
    static string output;

    [STAThread]
    public static int Main(string[] args)
    {
        output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        try {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            AppTheme.Initialize(Path.Combine(output, "appearance.txt"));
            AppTheme.SetDark(false); AppMotion.ReducedMotion = true;
            VerifyCachedSearchAndChecklist();
            VerifyRefresh();
            VerifyUnknownListings();
            VerifyMissingConfiguration();
            VerifyCloseCancellation();
            VerifySettings();
            File.WriteAllLines(Path.Combine(output, "report.txt"), passed.Concat(new[] { "Refresh delegate calls: " + refreshCalls, "No network client or production files used." }), Encoding.UTF8);
            Console.WriteLine("PASS: " + String.Join("; ", passed));
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(ex);
            if (current != null) {
                Console.Error.WriteLine("Visible UI:\n" + Text(current));
                try { Capture(current, "failure.png"); } catch { }
            }
            return 1;
        } finally {
            foreach (var window in windows) if (window.IsLoaded) window.Close();
        }
    }

    static MarketSnapshotItem Item(string name, bool comparable)
    {
        return new MarketSnapshotItem { Name = name, Category = comparable ? "생활 재료" : "검", PriceComparable = comparable,
            SoldQuantity = 1234, TradeCount = 12, TradedGold = 246800m,
            ListedQuantity = 600, ListingCount = 6, AverageSalePrice = comparable ? (decimal?)200 : null,
            LowestListingPrice = comparable ? (decimal?)190 : null };
    }

    static MarketSnapshotData Fixture(string version)
    {
        var stamp = DateTime.UtcNow.AddMinutes(-10);
        var data = new MarketSnapshotData { Version = version, GeneratedUtc = stamp, ListingsFetchedUtc = stamp,
            Items24h = new List<MarketSnapshotItem>(), Items7d = new List<MarketSnapshotItem>(),
            Quotes = new Dictionary<string, MarketSnapshotQuote>(StringComparer.Ordinal), Status = new Dictionary<string, object>() };
        data.Items24h.Add(Item(ExactName, true));
        data.Items24h.Add(Item(ExactName + " 조각", true));
        data.Items24h.Add(Item(LongName, true));
        data.Items24h.Add(Item("옵션별 테스트 검", false));
        data.Items24h.Add(new MarketSnapshotItem { Name = "미확인 수량 재료", Category = "생활 재료", PriceComparable = true });
        data.Items24h.Add(new MarketSnapshotItem { Name = "확인된 빈 매물 재료", Category = "생활 재료", PriceComparable = true, SoldQuantity = 0, TradeCount = 0, ListedQuantity = 0, ListingCount = 0, TradedGold = 0m });
        for (int i = 0; i < 45; i++) data.Items24h.Add(Item("오프라인 공통 재료 " + i.ToString("00"), true));
        data.Items7d.Add(Item("주간에만 존재하는 신규 이름", true));
        foreach (var item in data.Items24h) data.Items7d.Add(item);
        data.Quotes[ExactName] = new MarketSnapshotQuote { Name = ExactName, UnitPrice = 187.25m, Quantity = 600, ListingCount = 6, FetchedUtc = stamp };
        data.Quotes["시세에만 존재하는 신규 이름"] = new MarketSnapshotQuote { Name = "시세에만 존재하는 신규 이름", UnitPrice = 321m, Quantity = 8, ListingCount = 2, FetchedUtc = stamp };
        data.Quotes[LongName] = new MarketSnapshotQuote { Name = LongName, UnitPrice = 123456m, Quantity = 5678, ListingCount = 42, FetchedUtc = stamp };
        data.Quotes["옵션별 테스트 검"] = new MarketSnapshotQuote { Name = "옵션별 테스트 검", UnitPrice = 987654m, Quantity = 1, ListingCount = 1, FetchedUtc = stamp };
        data.Quotes["미확인 수량 재료"] = new MarketSnapshotQuote { Name = "미확인 수량 재료", UnitPrice = 425m, QuantityKnown = false, ListingCount = 1, FetchedUtc = stamp };
        return data;
    }

    static PipChecklistWindow NewWindow(Action<string, bool> checkedCallback)
    {
        var window = new PipChecklistWindow(delegate { return null; }, checkedCallback, delegate { });
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -18000; window.Top = -18000; window.ShowActivated = false; window.ShowInTaskbar = false;
        windows.Add(window); current = window;
        window.Show(); Pump();
        Assert(window.Left < -10000 && !window.IsActive, "Verification window became visible or activated");
        return window;
    }

    static void VerifyCachedSearchAndChecklist()
    {
        string checkKey = null; bool checkValue = false;
        var steps = new List<ProcurementStep>();
        for (int i = 0; i < 20; i++) steps.Add(new ProcurementStep { Key = "purchase:fixture" + i, Name = "구매 확인 재료 " + i.ToString("00"), Kind = "purchase", Quantity = i + 1, GroupKey = "lower", GroupLabel = "테스트 구매" });
        var window = NewWindow(delegate(string key, bool value) { checkCalls++; checkKey = key; checkValue = value; });
        window.UpdateSteps(steps, 0m, null);
        var snapshot = Fixture("cached");
        int uiThread = Thread.CurrentThread.ManagedThreadId, cacheThread = uiThread;
        using (var gate = new ManualResetEvent(false)) {
            window.ConfigureMarketSearch(delegate { int reads = Interlocked.Increment(ref cacheReads); cacheThread = Thread.CurrentThread.ManagedThreadId; if (reads == 1) gate.WaitOne(3000); return snapshot; },
                delegate { Interlocked.Increment(ref refreshCalls); return Task.FromResult(new MarketSnapshotResult { Data = snapshot }); }, "");
            var clock = Stopwatch.StartNew(); window.SelectedTab = 3; clock.Stop();
            Assert(clock.ElapsedMilliseconds < 500, "Entering search blocked on disk cache");
            gate.Set();
            Wait(delegate { return cacheReads > 0 && !window.SearchBusy; }, "cached index");
        }
        Assert(cacheThread != uiThread, "Cache load ran on the WPF dispatcher");
        Assert(refreshCalls == 0, "Tab entry fetched data");
        Query(window, "오프라인신규거미줄", ExactName);
        Assert(Blocks(Cards(window).First()).Any(t => t.Text == ExactName), "Exact match did not rank first");
        Assert(Text(Cards(window).First()).Contains("187.25 G"), "Fractional unit price was rounded away");
        Assert(Text(window.SearchResultsPanel).Contains("600개"), "Cached listing quantity not rendered");
        Query(window, "시세에만존재하는신규이름", "시세에만 존재하는 신규 이름");
        Query(window, "주간에만존재하는신규이름", "주간에만 존재하는 신규 이름");
        Query(window, "미확인수량재료", "미확인 수량 재료");
        Assert(Text(window.SearchResultsPanel).Contains("425 G") && !Text(window.SearchResultsPanel).Contains("/ 0개"), "Unknown listing quantity was treated as zero");
        Query(window, "확인된빈매물재료", "확인된 빈 매물 재료");
        Assert(Text(window.SearchResultsPanel).Contains("수집 당시 매물 없음"), "Confirmed empty listing was not distinguished from unknown data");
        Query(window, "옵션별테스트검", "옵션별 테스트 검");
        Assert(Text(window.SearchResultsPanel).Contains("옵션별 가격 차이") && Text(window.SearchResultsPanel).Contains("참고 최저가"), "Equipment option caveat missing");
        window.SearchInput.Text = "없는품목_오프라인검증";
        Wait(delegate { return Cards(window).Count == 0 && !window.SearchBusy && Text(window.SearchResultsPanel).Length > 0; }, "no results");
        Query(window, "오프라인 공통 재료", "오프라인 공통 재료");
        Assert(Cards(window).Count == 30, "Large query did not render exactly 30 bounded cards");
        Assert((window.SearchStatusText.Text + Text(window.SearchResultsPanel)).Contains("30"), "Bounded results missing a refinement notice");
        window.SearchResultsScroll.ScrollToVerticalOffset(200); Pump(); Pump();
        double searchOffset = window.SearchResultsScroll.VerticalOffset;
        Assert(searchOffset > 0, "Search fixture has no scrollable result area");
        var searchInput = window.SearchInput;
        var firstCard = Cards(window).First();
        steps[0].Quantity += 1; window.UpdateSteps(steps, 5m, null); Pump();
        Assert(window.SelectedTab == 3 && window.SearchInput.Text == "오프라인 공통 재료", "Checklist update replaced active query or tab");
        Assert(Object.ReferenceEquals(firstCard, Cards(window).First()), "Checklist update rebuilt search cards");
        Assert(Math.Abs(window.SearchResultsScroll.VerticalOffset - searchOffset) < 2, "Checklist update reset search scroll");
        window.SelectedTab = 1; Pump(); Pump();
        window.PurchaseScroll.ScrollToVerticalOffset(130); Pump(); Pump();
        double purchaseOffset = window.PurchaseScroll.VerticalOffset;
        var check = window.ReadyControls[steps[0].Key]; check.IsChecked = true;
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(checkCalls == 1 && checkKey == steps[0].Key && checkValue, "Purchase checkbox callback broke");
        steps[0].IsReady = true; window.UpdateSteps(steps, 5m, null);
        window.SelectedTab = 3; Pump(); Pump();
        Assert(Object.ReferenceEquals(searchInput, window.SearchInput) && window.SearchInput.Text == "오프라인 공통 재료", "Tab switching lost the search input");
        Assert(Math.Abs(window.SearchResultsScroll.VerticalOffset - searchOffset) < 2, "Tab switching reset search scroll");
        window.SelectedTab = 1; Pump(); Pump();
        Assert(Math.Abs(window.PurchaseScroll.VerticalOffset - purchaseOffset) < 2, "Search tab reset purchase scroll");
        window.SelectedTab = 3; Pump(); Pump();
        Wait(delegate { return !window.SearchBusy; }, "return to cached search");
        Assert(refreshCalls == 0, "Typing or tab switching fetched data");
        Assert(!window.SearchStatusText.Text.Contains("읽지 못"), "Returning to the cache failed");
        passed.Add("cached exact/space-insensitive/full-universe search; fractional prices and unknown quantity; empty/option/no-result states; 30-result cap; zero implicit fetches");
        passed.Add("checkbox callback and purchase/search query, scroll, and controls preserved through updates");

        Query(window, LongName, LongName);
        window.Width = 320; window.Height = 540; PumpFor(100);
        VerifyWidth(window);
        var name = Blocks(window.SearchResultsPanel).First(t => t.Text == LongName);
        Assert(name.TextWrapping != TextWrapping.NoWrap && name.ActualHeight > name.FontSize * 2, "Long name does not wrap at minimum width");
        Color light = ((SolidColorBrush)window.SearchInput.Background).Color;
        var visibleCard = Cards(window).First();
        Capture(window, "search-minimum-light.png");
        AppTheme.SetDark(true); Pump(); Pump();
        VerifyWidth(window);
        Assert(((SolidColorBrush)window.SearchInput.Background).Color != light, "Existing search field did not update its theme");
        Assert(Object.ReferenceEquals(visibleCard, Cards(window).First()) && window.SearchInput.Text == LongName, "Theme change rebuilt or reset search");
        Capture(window, "search-minimum-dark.png");
        AppTheme.SetDark(false); Pump();
        window.Width = 360; PumpFor(100);
        VerifyWidth(window); Capture(window, "search-default-light.png");
        AppTheme.SetDark(true); Pump(); Pump();
        VerifyWidth(window); Capture(window, "search-default-dark.png");
        Assert(window.SearchInput.Focusable && window.SearchInput.IsTabStop && !window.SearchInput.IsReadOnly, "Search field cannot accept normal keyboard input");
        FocusManager.SetFocusedElement(window, window.SearchInput); Pump();
        Assert(Object.ReferenceEquals(FocusManager.GetFocusedElement(window), window.SearchInput), "Search input cannot retain logical focus");
        AppTheme.SetDark(false); Pump();
        passed.Add("live light/dark appearance and wrapped long names at 320px, with captures");
    }

    static void VerifyRefresh()
    {
        var oldData = Fixture("before-refresh");
        var newData = Fixture("after-refresh"); newData.Items24h.Add(Item("갱신으로 추가된 품목", true));
        var completion = new TaskCompletionSource<MarketSnapshotResult>();
        var window = NewWindow(null);
        window.ConfigureMarketSearch(delegate { return oldData; }, delegate { Interlocked.Increment(ref refreshCalls); return completion.Task; }, "");
        window.SelectedTab = 3; Query(window, ExactName, ExactName);
        Click(window.SearchRefreshButton);
        Assert(window.SearchBusy && !window.SearchRefreshButton.IsEnabled, "Refresh did not expose its pending state");
        completion.SetResult(new MarketSnapshotResult { Data = newData, Downloaded = true });
        Wait(delegate { return !window.SearchBusy && window.SearchRefreshButton.IsEnabled; }, "refresh success");
        Query(window, "갱신으로추가된품목", "갱신으로 추가된 품목");
        Assert(refreshCalls == 1, "Refresh request count differs from explicit click count");
        window.ConfigureMarketSearch(delegate { return newData; }, delegate { Interlocked.Increment(ref refreshCalls); return Task.FromResult(new MarketSnapshotResult { Data = newData, UsedCached = true, ErrorMessage = "오프라인 테스트 서버 오류" }); }, "");
        Query(window, ExactName, ExactName); Click(window.SearchRefreshButton);
        Wait(delegate { return !window.SearchBusy && window.SearchStatusText.Text.Contains("오프라인 테스트 서버 오류"); }, "refresh failure");
        Assert(Text(window.SearchResultsPanel).Contains(ExactName), "Refresh failure erased cached results");
        Capture(window, "search-refresh-failure.png");
        Assert(refreshCalls == 2, "Refresh failure automatically retried");
        passed.Add("explicit refresh pending/success/failure with cache retention and no automatic retry");
    }

    static void VerifyMissingConfiguration()
    {
        var window = NewWindow(null);
        window.ConfigureMarketSearch(null, null, "테스트용 시장 주소 미설정");
        window.SelectedTab = 3; Pump();
        window.SearchInput.Text = ExactName; PumpFor(250);
        Assert(!window.SearchBusy && !window.SearchRefreshButton.IsEnabled, "Missing provider still permits refresh");
        Assert(Text(window).Contains("테스트용 시장 주소 미설정"), "Missing provider reason was not visible");
        Assert(refreshCalls == 2, "Missing provider state fetched data");
        passed.Add("missing provider stays idle and explains unavailable refresh");
    }

    static void VerifyUnknownListings()
    {
        var snapshot = Fixture("unknown-listings"); snapshot.ListingsFetchedUtc = null; snapshot.Quotes.Clear();
        var window = NewWindow(null);
        window.ConfigureMarketSearch(delegate { return snapshot; }, delegate { Interlocked.Increment(ref refreshCalls); return Task.FromResult(new MarketSnapshotResult { Data = snapshot }); }, "");
        window.SelectedTab = 3;
        Query(window, "확인된빈매물재료", "확인된 빈 매물 재료");
        Assert(Text(window.SearchResultsPanel).Contains("매물 미확인") && !Text(window.SearchResultsPanel).Contains("수집 당시 매물 없음"), "Missing collection timestamp was interpreted as zero listings");
        Assert(window.SearchDataText.Text.Contains("미확인"), "Unknown collection timestamp was not surfaced");
        passed.Add("missing listing timestamp remains unknown, separate from a collected empty market");
    }

    static void VerifyCloseCancellation()
    {
        var completion = new TaskCompletionSource<MarketSnapshotResult>();
        CancellationToken pending = CancellationToken.None;
        var window = NewWindow(null);
        window.ConfigureMarketSearch(delegate { return Fixture("close"); }, delegate(CancellationToken token) { Interlocked.Increment(ref refreshCalls); pending = token; token.Register(delegate { completion.TrySetCanceled(); }); return completion.Task; }, "");
        window.SelectedTab = 3; Query(window, ExactName, ExactName);
        Click(window.SearchRefreshButton); Assert(window.SearchBusy && pending.CanBeCanceled, "Refresh cancellation token missing");
        window.Close(); Pump();
        Assert(pending.IsCancellationRequested, "Closing PIP did not cancel pending refresh");
        Wait(delegate { return completion.Task.IsCanceled; }, "closed refresh cancellation");
        current = null;
        passed.Add("closing PIP cancels in-flight refresh safely");
    }

    static void VerifySettings()
    {
        string path = Path.Combine(output, "pip-settings.json");
        var settings = new PipWindowSettings { Tab = 3, Width = 320, Height = 540 };
        settings.Save(path);
        Assert(PipWindowSettings.Load(path).Tab == 3, "Search tab selection was not persisted");
        settings.Tab = 99; settings.Save(path);
        Assert(PipWindowSettings.Load(path).Tab == 1, "Invalid saved tab was not normalized");
        passed.Add("tab 3 settings round-trip and invalid-tab fallback");
    }

    static void Query(PipChecklistWindow window, string query, string expected)
    {
        window.SearchInput.Text = query;
        PumpFor(190);
        Wait(delegate { return !window.SearchBusy && Text(window.SearchResultsPanel).Contains(expected); }, "query: " + query);
    }
    static List<Border> Cards(PipChecklistWindow window) { return window.SearchResultsPanel.Children.OfType<Border>().ToList(); }
    static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    static IEnumerable<TextBlock> Blocks(DependencyObject root)
    {
        var block = root as TextBlock; if (block != null) yield return block;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Blocks(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    static string Text(DependencyObject root) { return String.Join("\n", Blocks(root).Select(t => t.Text)); }
    static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void PumpFor(int milliseconds) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(10); } Pump(); }
    static void Wait(Func<bool> done, string reason)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { if (clock.ElapsedMilliseconds > 6000) throw new Exception("Timed out waiting for " + reason); Pump(); Thread.Sleep(10); }
        Pump();
    }
    static void VerifyWidth(PipChecklistWindow window)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        foreach (var control in new FrameworkElement[] { window.SearchTabButton, window.SearchInput, window.SearchRefreshButton, window.SearchResultsScroll }) {
            Assert(control.ActualWidth > 0, "Search control collapsed at minimum width");
            var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
            Assert(bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1, "Search control clips horizontally at minimum width");
        }
    }
    static void Capture(Window window, string filename)
    {
        window.UpdateLayout(); var root = (FrameworkElement)window.Content;
        Console.WriteLine("Capture " + filename + ": window " + window.Width + " / actual " + window.ActualWidth + " x " + window.ActualHeight + ", content " + root.ActualWidth + " x " + root.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, filename))) encoder.Save(file);
    }
}
