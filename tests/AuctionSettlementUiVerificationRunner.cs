using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MabinogiBarter;

// Standalone, offscreen WPF windows with memory-only market fixtures and a
// substituted image sink. No MainWindow, provider, player files, or clipboard.
public static class AuctionSettlementUiVerificationRunner
{
    const string Material = "오프라인 정산 검증 재료";
    const string Duplicate = "동명이름 검증 재료";
    const string Equipment = "옵션 있는 검증 장비";
    const string Enchant = "템포 (접미 / 랭크 6) · 인챈트 스크롤";
    const string DedicatedEnchant = "템포 (접미 / 랭크 6) · 전용 인챈트 스크롤";
    const string ManualGross = "100,000,000.00";
    static readonly int[] Coupons = { 10, 20, 30, 50, 100 };
    static readonly List<AuctionSettlementWindow> windows = new List<AuctionSettlementWindow>();
    static readonly List<string> reports = new List<string>();
    static AuctionSettlementWindow current;
    static BitmapSource copiedImage;
    static string output;
    static int assertions, cacheReads, refreshCalls, copyCalls;

    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    static void Pass(string message) { reports.Add("PASS " + message); Console.WriteLine(reports[reports.Count - 1]); }
    static MarketSnapshotItem Item(string name, string category, bool comparable, decimal average)
    {
        return new MarketSnapshotItem { Name = name, Category = category, PriceComparable = comparable,
            SoldQuantity = 12, TradeCount = 3, TradedGold = average * 12, AverageSalePrice = average,
            ListedQuantity = 20, ListingCount = 2, LowestListingPrice = average + 99999m };
    }
    static void Add(MarketSnapshotData data, string name, string category, bool comparable, decimal lowest, decimal average24, decimal average7)
    {
        data.Items24h.Add(Item(name, category, comparable, average24));
        data.Items7d.Add(Item(name, category, comparable, average7));
        data.Quotes[name] = new MarketSnapshotQuote { Name = name, UnitPrice = lowest, Quantity = 20, ListingCount = 2, FetchedUtc = data.ListingsFetchedUtc.Value };
    }
    static MarketSnapshotData Fixture(string version, bool refreshed)
    {
        var stamp = new DateTime(2026, 9, 13, 12, refreshed ? 30 : 0, 0, DateTimeKind.Utc);
        var data = new MarketSnapshotData { Version = version, GeneratedUtc = stamp, ListingsFetchedUtc = stamp,
            Items24h = new List<MarketSnapshotItem>(), Items7d = new List<MarketSnapshotItem>(),
            Quotes = new Dictionary<string, MarketSnapshotQuote>(StringComparer.Ordinal), Status = new Dictionary<string, object>() };
        decimal[] prices = refreshed ? new[] { 500000m, 750000m, 2000000m, 13000000m, 20000000m }
            : new[] { 220000m, 740000m, 1680000m, 12940000m, 19550000m };
        for (int i = 0; i < Coupons.Length; i++) Add(data, "경매장 수수료 " + Coupons[i] + "% 할인 쿠폰", "기타 소모품", true, prices[i], prices[i] + 100m, prices[i] + 200m);
        Add(data, Material, "생활 재료", true, refreshed ? 197.25m : 187.25m, 222.22m, 333.33m);
        Add(data, Duplicate, "생활 재료", true, 19.75m, 901.25m, 903.75m);
        data.Items24h.Add(Item(Duplicate, "다른 재료", true, 902.50m));
        data.Items7d.Add(Item(Duplicate, "다른 재료", true, 904.25m));
        Add(data, Equipment, "검", false, 7654321m, 888888.5m, 999999.5m);
        Add(data, Enchant, "인챈트 스크롤", true, 2400000m, 2300000m, 2200000m);
        Add(data, DedicatedEnchant, "인챈트 스크롤", true, 2800000m, 2900000m, 3000000m);
        Add(data, "인챈트 스크롤", "인챈트 스크롤", false, 99m, 100m, 101m);
        return data;
    }
    static AuctionSettlementWindow NewWindow()
    {
        var window = new AuctionSettlementWindow { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false };
        window.ImageCopier = delegate(BitmapSource image) { copyCalls++; copiedImage = image; };
        windows.Add(window); current = window; window.Show(); Pump();
        Check(window.Left < -10000 && !window.IsActive, "Verification window became visible or active");
        return window;
    }
    static AuctionSettlementScenario Row(AuctionSettlementWindow window, int discount)
    {
        Check(window.CurrentReport != null, "No current calculation report");
        return window.CurrentReport.Scenarios.Single(s => s.DiscountPercent == discount);
    }
    static void SetSale(AuctionSettlementWindow window, string gross)
    {
        window.GrossInput.Text = gross; window.PeopleInput.Text = "4"; window.ExtraCostInput.Text = "0"; Pump();
    }
    static void SameSale(AuctionSettlementWindow window, string operation)
    {
        Check(window.GrossInput.Text == ManualGross && window.PeopleInput.Text == "4" && window.ExtraCostInput.Text == "0", operation + " changed a manual sale input");
    }
    static void Query(AuctionSettlementWindow window, string query, string expected)
    {
        window.MarketPanel.ItemNameInput.Text = query; PumpFor(180);
        Wait(delegate { return !window.MarketPanel.IsBusy && (Text(window.MarketPanel.ResultsPanel) + Text(window.MarketPanel.ReferencePanel)).Contains(expected); }, "search " + query);
        SameSale(window, "Name search");
    }
    static void VerifySearch(AuctionSettlementWindow window)
    {
        Query(window, Material, Material);
        string reference = Text(window.MarketPanel.ReferencePanel);
        Check(window.MarketPanel.SelectedItemName == Material && reference.Contains("187.25 G") && reference.Contains("222.22 G") && reference.Contains("333.33 G"), "Exact name did not show quote minimum and distinct 24h/7d sale averages");
        Check(!reference.Contains("100,221.22"), "Metadata listing minimum replaced the quote minimum");
        Query(window, "템포", Enchant);
        Check(window.MarketPanel.ResultsPanel.Children.OfType<Button>().Count() == 2, "Enchant search did not distinguish both scroll forms");
        var choose = window.MarketPanel.ResultsPanel.Children.OfType<Button>().Single(b => Text(b) == Enchant);
        Click(choose);
        Check(window.MarketPanel.SelectedItemName == Enchant && window.MarketPanel.ItemNameInput.Text == Enchant, "Suggestion selection did not retain the complete enchant identity");
        reference = Text(window.MarketPanel.ReferencePanel);
        Check(reference.Contains("2,400,000 G") && reference.Contains("2,300,000 G") && reference.Contains("2,200,000 G") && reference.Contains("같은 인챈트 이름"), "Named enchant references mixed scroll forms or lost the comparison cue");
        SameSale(window, "Suggestion selection");
        Query(window, Equipment, Equipment);
        reference = Text(window.MarketPanel.ReferencePanel);
        Check(reference.Contains("7,654,321 G") && reference.Contains("옵션별 가격 차이") && !reference.Contains("888,888.5") && !reference.Contains("999,999.5"), "Option-bearing equipment exposed an unsafe sale average");
        Query(window, Duplicate, Duplicate);
        reference = Text(window.MarketPanel.ReferencePanel);
        Check(reference.Contains("19.75 G") && reference.Contains("미확인") && !reference.Contains("901.25") && !reference.Contains("902.5") && !reference.Contains("903.75") && !reference.Contains("904.25"), "Same-name category averages were mixed");
        Query(window, "인챈트 스크롤", "인챈트 이름 미확인");
        Check(!Text(window.MarketPanel.ReferencePanel).Contains("99 G"), "Generic enchant exposed a mixed coupon-independent price");
        Query(window, Material, Material);
        window.MarketPanel.ItemNameInput.Text = "수집되지 않은 자유 입력 아이템";
        Check(window.MarketPanel.SelectedItemName == null && !Text(window.MarketPanel.ReferencePanel).Contains("187.25 G") && !Text(window.MarketPanel.ReferencePanel).Contains(Material), "Editing a selected name left stale market references before debounce");
        PumpFor(180);
        Check(window.MarketPanel.ItemNameInput.Text == "수집되지 않은 자유 입력 아이템" && window.CurrentReport != null && window.CopyImageButton.IsEnabled, "Unknown free-form names cannot be used for a manual settlement");
        SameSale(window, "Unknown free-form name");
        Check(refreshCalls == 0, "Name entry or result selection performed an implicit refresh");
        Query(window, Enchant, Enchant);
        Pass("exact/free-form names, separate enchant forms and immediate stale-reference clearing; authoritative minima/24h/7d averages; ambiguous and equipment averages withheld; no implicit refresh or sale edits");
    }
    static void VerifyExpected(AuctionSettlementWindow window, bool premium)
    {
        window.PremiumControl.IsChecked = premium; Pump();
        decimal[] fees = premium ? new[] { 4000000m, 3600000m, 3200000m, 2800000m, 2000000m, 0m } : new[] { 5000000m, 4500000m, 4000000m, 3500000m, 2500000m, 0m };
        decimal[] nets = premium ? new[] { 96000000m, 96180000m, 96060000m, 95520000m, 85060000m, 80450000m } : new[] { 95000000m, 95280000m, 95260000m, 94820000m, 84560000m, 80450000m };
        int[] discounts = { 0, 10, 20, 30, 50, 100 };
        for (int i = 0; i < discounts.Length; i++) {
            var row = Row(window, discounts[i]);
            Check(row.Fee == fees[i] && row.NetAmount == nets[i] && row.PerPerson == nets[i] / 4m && row.Remainder == 0m, "UI calculation differs from the " + (premium ? "premium" : "standard") + " screenshot row " + discounts[i]);
        }
        Check(window.CurrentReport.BestScenario.DiscountPercent == 10, "Coupon cost comparison lost the optimal 10% coupon");
        SameSale(window, "Premium toggle");
    }
    static void VerifyImagesAndLayout(AuctionSettlementWindow window)
    {
        Check(window.SelectedDiscount == 0 && window.CurrentReport.BestScenario.DiscountPercent == 10, "Recommendation automatically selected a coupon");
        window.Width = 1080; PumpFor(60); VerifyWidth(window);
        CaptureWindow(window, "settlement-default-light-top.png");
        window.BodyScroll.ScrollToEnd(); Pump(); CaptureWindow(window, "settlement-default-light-comparison.png");
        window.Width = 780;
        foreach (bool dark in new[] { false, true }) {
            AppTheme.SetDark(dark); Pump();
            foreach (bool premium in new[] { false, true }) {
                VerifyExpected(window, premium); Click(window.CouponSelectButton(0));
                string label = "settlement-minimum-" + (premium ? "premium" : "standard") + "-" + (dark ? "dark" : "light");
                window.BodyScroll.ScrollToTop(); PumpFor(60); VerifyWidth(window); CaptureWindow(window, label + "-top.png");
                window.BodyScroll.ScrollToEnd(); Pump(); VerifyWidth(window); CaptureWindow(window, label + "-comparison.png");
                CopyImage(window, label + "-report.png");
                Check(window.SelectedDiscount == 0, "Rendering or copying selected the recommendation automatically");
            }
        }
        AppTheme.SetDark(false); window.PremiumControl.IsChecked = false; Click(window.CouponSelectButton(10));
        Check(window.SelectedDiscount == 10 && Text(window).Contains("23,820,000 G"), "Explicit coupon selection did not update standard distribution");
        CopyImage(window, "settlement-selected-10-standard.png");
        window.PremiumControl.IsChecked = true; Pump();
        Check(window.SelectedDiscount == 10 && Text(window).Contains("24,045,000 G"), "Premium toggle lost the chosen coupon or share");
        CopyImage(window, "settlement-selected-10-premium.png");
        window.PremiumControl.IsChecked = false; Click(window.CouponSelectButton(0));
        Check(refreshCalls == 0, "Layout, premium, coupon selection, or image operations refreshed the market");
        Pass("100m standard/premium six-row calculations, explicit coupon selection, 780px light/dark layout and 960-DIP report images at 144 DPI; copying uses the injected image sink only");
    }
    static void VerifyRefreshAndManualPrices(AuctionSettlementWindow window, Action<TaskCompletionSource<MarketSnapshotResult>> setPending)
    {
        window.CouponPriceInput(10).Text = "0"; window.CouponPriceInput(20).Text = "123456"; window.CouponPriceInput(100).Clear();
        Click(window.CouponSelectButton(10));
        var pending = new TaskCompletionSource<MarketSnapshotResult>(); setPending(pending);
        Click(window.MarketPanel.RefreshButton);
        Check(window.MarketPanel.IsBusy && !window.MarketPanel.RefreshButton.IsEnabled && refreshCalls == 1, "Explicit refresh did not expose its pending state exactly once");
        SameSale(window, "Pending refresh");
        var fresh = Fixture("fresh", true); pending.SetResult(new MarketSnapshotResult { Data = fresh, Downloaded = true });
        Wait(delegate { return !window.MarketPanel.IsBusy && Object.ReferenceEquals(window.MarketPanel.Snapshot, fresh); }, "successful market refresh");
        SameSale(window, "Completed refresh");
        Check(window.CouponPriceInput(10).Text == "0" && window.CouponPriceInput(20).Text == "123456" && window.CouponPriceInput(100).Text == "", "Refresh overwrote a manual/free/unknown coupon cost");
        Check(Row(window, 10).CouponPrice == 0m && Row(window, 20).CouponPrice == 123456m && !Row(window, 100).IsKnown && Row(window, 30).CouponPrice == 2000000m, "Manual and market-derived coupon costs were not kept distinct");
        Check(window.SelectedDiscount == 10, "Refresh changed the explicitly selected coupon");
        var failed = new TaskCompletionSource<MarketSnapshotResult>(); setPending(failed); Click(window.MarketPanel.RefreshButton);
        failed.SetResult(new MarketSnapshotResult { Data = Fixture("failed-old", false), UsedCached = true, ErrorMessage = "오프라인 검증 갱신 실패" });
        Wait(delegate { return !window.MarketPanel.IsBusy && window.MarketPanel.StatusText.Text.Contains("갱신 실패"); }, "refresh error");
        Check(Object.ReferenceEquals(window.MarketPanel.Snapshot, fresh) && Row(window, 30).CouponPrice == 2000000m && refreshCalls == 2, "Failed refresh replaced the successful snapshot or automatically retried");
        SameSale(window, "Failed refresh");
        Pass("explicit refresh pending/success/failure retains sale inputs, selected coupon, manual/free/unknown prices and the last successful market snapshot");
    }
    static void VerifyInvalidAndRemainder(AuctionSettlementWindow window)
    {
        Click(window.CouponSelectButton(0)); SetSale(window, "101");
        var row = Row(window, 0);
        Check(row.Fee == 5.05m && row.PerPerson == 23m && row.Remainder == 3.95m && Text(window).Contains("3.95 G"), "Four-person fractional remainder was rounded away in the UI");
        CopyImage(window, "settlement-four-person-remainder.png");
        foreach (string invalid in new[] { "", "-1", "abc", "1e8", "1000000000000001", "79228162514264337593543950335" }) {
            window.GrossInput.Text = invalid; InvalidReport(window, "gross " + invalid); window.GrossInput.Text = "101";
        }
        foreach (string invalid in new[] { "0", "4.5", "1001", "abc" }) {
            window.PeopleInput.Text = invalid; InvalidReport(window, "people " + invalid); window.PeopleInput.Text = "4";
        }
        window.ExtraCostInput.Text = "-1"; InvalidReport(window, "negative extra cost"); window.ExtraCostInput.Text = "0";
        window.CouponPriceInput(20).Text = "-1"; InvalidReport(window, "negative coupon cost"); window.CouponPriceInput(20).Text = "123456";
        window.CouponPriceInput(100).Text = "0"; Click(window.CouponSelectButton(100)); window.CouponPriceInput(100).Clear(); Pump();
        Check(window.CurrentReport != null && !Row(window, 100).IsKnown && !window.CopyImageButton.IsEnabled && window.SelectedDiscount == 100, "Selected coupon with unknown cost enabled a stale image or changed selection");
        int before = copyCalls; window.CopyImageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(copyCalls == before, "Unknown selected coupon copied a stale report");
        Click(window.CouponSelectButton(0)); window.ExtraCostInput.Text = "1000"; Pump();
        Check(Row(window, 0).IsLoss && !Row(window, 0).PerPerson.HasValue && Text(window).Contains("분배 가능 금액 없음"), "Loss was represented as a negative per-person payout");
        CopyImage(window, "settlement-loss.png");
        window.ExtraCostInput.Text = "0";
        window.MarketPanel.ItemNameInput.Text = "자유 입력한 긴 이름의 거래 아이템과 분배 조건을 확인하는 오프라인 검증 " + new string('가', 100); PumpFor(180);
        CopyImage(window, "settlement-long-freeform-name.png");
        Pass("invalid amounts/people clear the report and disable guarded copying; unknown selected coupon stays unresolved; fractional remainder, loss and long free-form-name report images");
    }
    static void InvalidReport(AuctionSettlementWindow window, string reason)
    {
        Pump(); Check(window.CurrentReport == null && !window.CopyImageButton.IsEnabled, "Invalid input retained a report or enabled copying: " + reason);
        int before = copyCalls; window.CopyImageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(copyCalls == before, "Invalid input copied a previous report: " + reason);
        BitmapSource invalid = null;
        try { invalid = window.CreateReportImage(); } catch (InvalidOperationException) { }
        Check(invalid == null, "Direct image rendering reused a stale report: " + reason);
    }
    static void VerifyNoProviderAndCloseCancellation()
    {
        var unavailable = NewWindow(); unavailable.MarketPanel.Configure(null, null, "검증용 시세 연결 없음");
        SetSale(unavailable, "100"); unavailable.MarketPanel.ItemNameInput.Text = "자유 입력 이름"; PumpFor(180);
        Check(!unavailable.MarketPanel.RefreshButton.IsEnabled && !unavailable.MarketPanel.IsBusy && Text(unavailable.MarketPanel).Contains("검증용 시세 연결 없음"), "Missing provider still permits market fetching");
        Check(unavailable.CurrentReport != null && unavailable.CopyImageButton.IsEnabled && Row(unavailable, 10).Fee == 4.5m && !Row(unavailable, 10).IsKnown, "No-provider state prevents manual no-coupon settlement or treats unknown coupons as free");
        CopyImage(unavailable, "settlement-no-provider.png"); unavailable.Close(); Pump();
        var closing = NewWindow(); var completion = new TaskCompletionSource<MarketSnapshotResult>(); CancellationToken token = CancellationToken.None;
        closing.MarketPanel.Configure(delegate { return Fixture("close", false); }, delegate(CancellationToken pending) {
            refreshCalls++; token = pending; pending.Register(delegate { completion.TrySetCanceled(); }); return completion.Task;
        }, null);
        Wait(delegate { return !closing.MarketPanel.IsBusy && closing.MarketPanel.Snapshot != null; }, "close fixture cache");
        SetSale(closing, "100"); Click(closing.MarketPanel.RefreshButton);
        Check(closing.MarketPanel.IsBusy && token.CanBeCanceled, "Pending refresh has no cancellation token");
        closing.Close(); Pump();
        Check(token.IsCancellationRequested, "Closing the settlement window did not cancel refresh");
        Wait(delegate { return completion.Task.IsCanceled; }, "closed refresh cancellation");
        current = null;
        Pass("missing provider leaves manual calculations available; closing cancels the injected pending refresh without later UI mutation");
    }
    static void Run()
    {
        var window = NewWindow();
        Check(window.CurrentReport == null && !window.CopyImageButton.IsEnabled && window.SelectedDiscount == 0, "Initial report/copy/default coupon state");
        SetSale(window, ManualGross);
        int uiThread = Thread.CurrentThread.ManagedThreadId, readThread = uiThread;
        var snapshot = Fixture("cached", false);
        TaskCompletionSource<MarketSnapshotResult> pending = null;
        using (var gate = new ManualResetEvent(false)) {
            var clock = Stopwatch.StartNew();
            window.MarketPanel.Configure(delegate { Interlocked.Increment(ref cacheReads); readThread = Thread.CurrentThread.ManagedThreadId; gate.WaitOne(3000); return snapshot; },
                delegate { refreshCalls++; if (pending == null) throw new Exception("Unexpected implicit refresh"); return pending.Task; }, null);
            clock.Stop(); Check(clock.ElapsedMilliseconds < 500, "Market configuration blocked the WPF dispatcher on cache I/O"); gate.Set();
            Wait(delegate { return !window.MarketPanel.IsBusy && Object.ReferenceEquals(window.MarketPanel.Snapshot, snapshot); }, "initial cached snapshot");
        }
        Check(readThread != uiThread && cacheReads == 1 && refreshCalls == 0, "Cache loading used the UI thread or triggered a market refresh");
        SameSale(window, "Cached snapshot application");
        VerifyExpected(window, false); VerifySearch(window); VerifyImagesAndLayout(window);
        VerifyRefreshAndManualPrices(window, delegate(TaskCompletionSource<MarketSnapshotResult> value) { pending = value; });
        VerifyInvalidAndRemainder(window); VerifyNoProviderAndCloseCancellation();
    }
    static void Click(Button button) { Check(button.IsEnabled, "Attempt to click a disabled fixture button"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject
    {
        var value = root as T; if (value != null) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Elements<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    static string Text(DependencyObject root) { return String.Join("\n", Elements<TextBlock>(root).Select(t => t.Text)); }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void PumpFor(int milliseconds) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(5); } Pump(); }
    static void Wait(Func<bool> condition, string reason)
    {
        var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.ElapsedMilliseconds > 6000) throw new Exception("Timed out: " + reason); Pump(); Thread.Sleep(5); } Pump();
    }
    static void VerifyWidth(AuctionSettlementWindow window)
    {
        window.UpdateLayout(); var root = (FrameworkElement)window.Content;
        var controls = new List<FrameworkElement> { window.MarketPanel.ItemNameInput, window.MarketPanel.RefreshButton,
            window.GrossInput, window.PeopleInput, window.ExtraCostInput, window.CopyImageButton };
        foreach (int discount in Coupons) { controls.Add(window.CouponPriceInput(discount)); controls.Add(window.CouponSelectButton(discount)); }
        foreach (var control in controls) {
            var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
            Check(control.ActualWidth > 10 && control.ActualHeight > 0 && bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1, "Input or action clips at 780px: " + control.GetType().Name + " " + bounds);
        }
        Check(window.BodyScroll.ViewportHeight > 100 && window.BodyScroll.ScrollableHeight > 0, "Comparison cards have no usable vertical scrolling area");
    }
    static void CopyImage(AuctionSettlementWindow window, string filename)
    {
        int before = copyCalls; copiedImage = null; Click(window.CopyImageButton);
        Check(copyCalls == before + 1 && copiedImage != null, "Image copy did not use the injected sink exactly once");
        Check(copiedImage.PixelWidth == 1440 && Math.Abs(copiedImage.DpiX - 144) < 0.1 && copiedImage.PixelHeight >= 450 && copiedImage.PixelHeight < 15000, "Report image is cropped or has unexpected dimensions");
        SaveImage(copiedImage, filename);
        var direct = window.CreateReportImage();
        Check(direct != null && direct.PixelWidth == copiedImage.PixelWidth && direct.PixelHeight == copiedImage.PixelHeight, "Copy and direct report image sizes differ");
    }
    static void CaptureWindow(AuctionSettlementWindow window, string filename)
    {
        window.UpdateLayout(); var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) {
            var area = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            drawing.DrawRectangle(window.Background, null, area);
            drawing.DrawRectangle(new VisualBrush(root), null, area);
        }
        bitmap.Render(visual); SaveImage(bitmap, filename);
    }
    static void SaveImage(BitmapSource bitmap, string filename)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, filename))) encoder.Save(stream);
        Console.WriteLine("Image " + filename + ": " + bitmap.PixelWidth + " x " + bitmap.PixelHeight);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ko-KR");
            AppTheme.Initialize(Path.Combine(output, "appearance.txt")); AppTheme.SetDark(false); AppMotion.ReducedMotion = true;
            Run();
            reports.Add("PASS " + assertions + " UI assertions; cache reads " + cacheReads + ", explicit refreshes " + refreshCalls + ", injected image copies " + copyCalls + ".");
            reports.Add("No HTTP client, production profile/configuration, main window, or OS clipboard used.");
            File.WriteAllLines(Path.Combine(output, "report.txt"), reports, Encoding.UTF8);
            Console.WriteLine(reports[reports.Count - 2]); return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(ex);
            if (current != null && current.IsLoaded) { Console.Error.WriteLine(Text(current)); try { CaptureWindow(current, "failure.png"); } catch { } }
            return 1;
        } finally { foreach (var window in windows) if (window.IsLoaded) window.Close(); }
    }
}
