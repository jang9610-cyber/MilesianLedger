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

// Embedded views in standalone, offscreen WPF hosts with memory-only fixtures and a
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
    static readonly Dictionary<AuctionSettlementView, Window> windows = new Dictionary<AuctionSettlementView, Window>();
    static readonly List<string> reports = new List<string>();
    static AuctionSettlementView current;
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
    static AuctionSettlementView NewWindow()
    {
        var view = new AuctionSettlementView();
        var host = new Window { Content = view, Width = 1140, Height = 830, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false };
        host.Closed += delegate { view.Dispose(); };
        view.ImageCopier = delegate(BitmapSource image) { copyCalls++; copiedImage = image; };
        windows.Add(view, host); current = view; host.Show(); Pump();
        Check(host.Left < -10000 && !host.IsActive, "Verification host became visible or active");
        Resize(view, 1140, 830);
        return view;
    }
    static void CloseWindow(AuctionSettlementView view) { windows[view].Close(); Pump(); }
    static void Resize(AuctionSettlementView view, double width, double height)
    {
        var host = windows[view]; host.Width = width; host.Height = height; PumpFor(60); view.UpdateLayout();
        Check(Math.Abs(view.ActualWidth - width) <= 1 && Math.Abs(view.ActualHeight - height) <= 1,
            "Host did not give the embedded view the requested content size: " + view.ActualWidth + " x " + view.ActualHeight);
    }
    static AuctionSettlementScenario Row(AuctionSettlementView window, int discount)
    {
        Check(window.CurrentReport != null, "No current calculation report");
        return window.CurrentReport.Scenarios.Single(s => s.DiscountPercent == discount);
    }
    static void SetSale(AuctionSettlementView window, string gross)
    {
        window.GrossInput.Text = gross; window.PeopleInput.Text = "4"; window.ExtraCostInput.Text = "0"; Pump();
    }
    static void SameSale(AuctionSettlementView window, string operation)
    {
        Check(window.GrossInput.Text == ManualGross && window.PeopleInput.Text == "4" && window.ExtraCostInput.Text == "0", operation + " changed a manual sale input");
    }
    static void Query(AuctionSettlementView window, string query, string expected)
    {
        window.MarketPanel.ItemNameInput.Text = query; PumpFor(180);
        Wait(delegate { return !window.MarketPanel.IsBusy && (Text(window.MarketPanel.ResultsPanel) + Text(window.MarketPanel.ReferencePanel)).Contains(expected); }, "search " + query);
        SameSale(window, "Name search");
    }
    static void VerifySearch(AuctionSettlementView window)
    {
        Query(window, Material, Material);
        string reference = Text(window.MarketPanel.ReferencePanel);
        Check(window.MarketPanel.SelectedItemName == Material && reference.Contains("187 G") && reference.Contains("222 G") && reference.Contains("333 G")
            && !reference.Contains("187.25 G") && !reference.Contains("222.22 G") && !reference.Contains("333.33 G"), "Exact name did not truncate displayed quote minimum and distinct 24h/7d sale averages");
        Check(window.MarketPanel.Snapshot.Quotes[Material].UnitPrice == 187.25m
            && window.MarketPanel.Snapshot.Items24h.Single(item => item.Name == Material).AverageSalePrice == 222.22m,
            "Integer reference display changed the cached raw price values");
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
        Check(reference.Contains("7,654,321 G") && reference.Contains("옵션별 가격 차이") && !reference.Contains("888,888") && !reference.Contains("999,999"), "Option-bearing equipment exposed an unsafe sale average");
        Query(window, Duplicate, Duplicate);
        reference = Text(window.MarketPanel.ReferencePanel);
        Check(reference.Contains("19 G") && !reference.Contains("20 G") && reference.Contains("미확인") && !reference.Contains("901 G") && !reference.Contains("902 G") && !reference.Contains("903 G") && !reference.Contains("904 G"), "Same-name category averages were mixed or quote display was rounded instead of truncated");
        Query(window, "인챈트 스크롤", "인챈트 이름 미확인");
        Check(!Text(window.MarketPanel.ReferencePanel).Contains("99 G"), "Generic enchant exposed a mixed coupon-independent price");
        Query(window, Material, Material);
        window.MarketPanel.ItemNameInput.Text = "수집되지 않은 자유 입력 아이템";
        Check(window.MarketPanel.SelectedItemName == null && !Text(window.MarketPanel.ReferencePanel).Contains("187 G") && !Text(window.MarketPanel.ReferencePanel).Contains(Material), "Editing a selected name left stale market references before debounce");
        PumpFor(180);
        Check(window.MarketPanel.ItemNameInput.Text == "수집되지 않은 자유 입력 아이템" && window.CurrentReport != null && window.CopyImageButton.IsEnabled, "Unknown free-form names cannot be used for a manual settlement");
        SameSale(window, "Unknown free-form name");
        Check(refreshCalls == 0, "Name entry or result selection performed an implicit refresh");
        Query(window, Enchant, Enchant);
        Pass("exact/free-form names, separate enchant forms and immediate stale-reference clearing; authoritative minima/24h/7d averages; ambiguous and equipment averages withheld; no implicit refresh or sale edits");
    }
    static void VerifyExpected(AuctionSettlementView window, bool premium)
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
    static void VerifyInitialNameSearch()
    {
        var window = NewWindow(); SetSale(window, ManualGross);
        var data = Fixture("initial-name-search", false);
        Add(data, "거미줄", "생활 재료", true, 187.25m, 222.5m, 333.75m);
        Add(data, "실리엔", "생활 재료", true, 456.5m, 501m, 502m);
        Add(data, "가는 실뭉치", "생활 재료", true, 987.75m, 1001m, 1002m);
        int reads = 0, beforeRefresh = refreshCalls;
        window.MarketPanel.Configure(delegate { Interlocked.Increment(ref reads); return data; },
            delegate { refreshCalls++; throw new Exception("Initial name entry unexpectedly refreshed market data."); }, null);
        Wait(delegate { return !window.MarketPanel.IsBusy && Object.ReferenceEquals(window.MarketPanel.Snapshot, data); }, "initial name fixture cache");
        window.CouponPriceInput(20).Text = "765432"; window.CouponPriceInput(10).Text = "0";
        Click(window.CouponSelectButton(20));
        decimal? selectedNet = Row(window, 20).NetAmount;
        string[] queries = { "ㄱㅁㅈ", "ㅅㄹㅇ", "가는 ㅅㅁㅊ", "\u1100\u1106\u110c", "거미줄".Normalize(NormalizationForm.FormD) };
        string[] names = { "거미줄", "실리엔", "가는 실뭉치", "거미줄", "거미줄" };
        string[] prices = { "187 G", "456 G", "987 G", "187 G", "187 G" };
        for (int i = 0; i < queries.Length; i++) {
            Query(window, queries[i], names[i]);
            var choices = window.MarketPanel.ResultsPanel.Children.OfType<Button>().ToArray();
            // ㄱㅁㅈ also prefixes 경매장 coupon names in this fixture; the full
            // material-name match must remain first without duplicate rows.
            Check(choices.Length >= 1 && Text(choices[0]) == names[i] && choices.Select(Text).Distinct().Count() == choices.Length,
                "Initial or decomposed name suggestion lost its first full-name match or duplicated an identity: " + queries[i]);
            Click(choices[0]);
            Check(window.MarketPanel.SelectedItemName == names[i] && Text(window.MarketPanel.ReferencePanel).Contains(prices[i]),
                "Initial name selection did not retain its original quote price: " + queries[i]);
            SameSale(window, "Initial name selection");
        }
        Query(window, "ㅌㅍ", Enchant);
        var enchants = window.MarketPanel.ResultsPanel.Children.OfType<Button>().ToArray();
        Check(enchants.Length == 2 && enchants.Any(button => Text(button) == Enchant) && enchants.Any(button => Text(button) == DedicatedEnchant),
            "Initial enchant query merged normal and dedicated scroll names");
        Click(enchants.Single(button => Text(button) == DedicatedEnchant));
        Check(Text(window.MarketPanel.ReferencePanel).Contains("2,800,000 G") && !Text(window.MarketPanel.ReferencePanel).Contains("2,400,000 G"),
            "Selecting a dedicated scroll through initials used the normal scroll price");
        SameSale(window, "Initial enchant selection");
        Check(window.SelectedDiscount == 20 && window.CouponPriceInput(20).Text == "765432" && window.CouponPriceInput(10).Text == "0"
            && Row(window, 20).NetAmount == selectedNet && reads == 1 && refreshCalls == beforeRefresh,
            "Initial searches changed settlement selection/manual coupon costs/calculation or performed implicit market I/O");
        Check(data.Quotes["거미줄"].UnitPrice == 187.25m && data.Quotes[DedicatedEnchant].UnitPrice == 2800000m,
            "Initial search mutated the cached source prices");
        CloseWindow(window);
        Pass("initial/mixed/decomposed material names and initial enchant suggestions retain exact quote references, manual sale/coupon inputs and chosen distribution without implicit refresh");
    }
    static void VerifyImagesAndLayout(AuctionSettlementView window)
    {
        Check(window.SelectedDiscount == 0 && window.CurrentReport.BestScenario.DiscountPercent == 10, "Recommendation automatically selected a coupon");
        foreach (bool dark in new[] { false, true }) {
            AppTheme.SetDark(dark); Pump();
            VerifyExpected(window, false); Click(window.CouponSelectButton(0));
            foreach (double width in new[] { 1140d, 1100d }) {
                Resize(window, width, 830);
                window.InputScroll.ScrollToTop(); window.BodyScroll.ScrollToTop(); Pump();
                VerifyDefaultLayout(window);
                CaptureWindow(window, "settlement-default-" + (width == 1100 ? "1100-" : "") + (dark ? "dark" : "light") + ".png");
            }
            Resize(window, 830, 650);
            foreach (bool premium in new[] { false, true }) {
                VerifyExpected(window, premium); Click(window.CouponSelectButton(0));
                string label = "settlement-minimum-" + (premium ? "premium" : "standard") + "-" + (dark ? "dark" : "light");
                window.InputScroll.ScrollToTop(); window.BodyScroll.ScrollToTop(); PumpFor(60); VerifyWidth(window); CaptureWindow(window, label + "-top.png");
                window.InputScroll.ScrollToEnd(); window.BodyScroll.ScrollToEnd(); Pump(); VerifyWidth(window); CaptureWindow(window, label + "-comparison.png");
                VerifyMinimumReachability(window);
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
        Pass("100m standard/premium six-row calculations and explicit coupon selection; 1140x830 and 1100x830 light/dark embedded views show all inputs, selected summary and six compact coupon rows without scrolling; 830x650 views remain horizontally unclipped with reachable internal scrolling; 960-DIP report images at 144 DPI use the injected image sink only");
    }
    static void VerifyRefreshAndManualPrices(AuctionSettlementView window, Action<TaskCompletionSource<MarketSnapshotResult>> setPending)
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
        VerifyCouponMarket(window, fresh);
        Check(window.CouponSourceText(10).Text.Contains("보유") && window.CouponSourceText(20).Text.Contains("직접")
            && window.CouponSourceText(30).Text.Contains("자동"), "Coupon cost sources do not distinguish owned, manual and market-following prices");
        Check(window.SelectedDiscount == 10, "Refresh changed the explicitly selected coupon");
        var failed = new TaskCompletionSource<MarketSnapshotResult>(); setPending(failed); Click(window.MarketPanel.RefreshButton);
        failed.SetResult(new MarketSnapshotResult { Data = Fixture("failed-old", false), UsedCached = true, ErrorMessage = "오프라인 검증 갱신 실패" });
        Wait(delegate { return !window.MarketPanel.IsBusy && window.MarketPanel.StatusText.Text.Contains("갱신 실패"); }, "refresh error");
        Check(Object.ReferenceEquals(window.MarketPanel.Snapshot, fresh) && Row(window, 30).CouponPrice == 2000000m && refreshCalls == 2, "Failed refresh replaced the successful snapshot or automatically retried");
        SameSale(window, "Failed refresh");
        Pass("explicit refresh pending/success/failure retains sale inputs, selected coupon, manual/free/unknown prices and the last successful market snapshot");
    }
    static void VerifyCouponMarket(AuctionSettlementView window, MarketSnapshotData snapshot)
    {
        foreach (int discount in Coupons) {
            var quote = snapshot.Quotes["경매장 수수료 " + discount + "% 할인 쿠폰"];
            string expectedPrice = Decimal.Truncate(quote.UnitPrice.Value).ToString("#,0", CultureInfo.InvariantCulture) + " G";
            var market = window.CouponMarketText(discount);
            Check(market.IsVisible && market.Text.Contains(expectedPrice) && market.Text.Contains("최저"), "Coupon card does not retain its current market minimum: " + discount + " / " + market.Text);
            Check(market.Text.Contains("수집") && market.Text.Contains(quote.FetchedUtc.ToLocalTime().ToString("HH:mm")), "Coupon card lacks its market collection time: " + discount);
        }
    }
    static Color BrushColor(Brush brush)
    {
        var solid = brush as SolidColorBrush;
        Check(solid != null, "Recommendation highlight is not a verifiable solid color");
        return solid.Color;
    }
    static int ColorDistance(Color left, Color right)
    {
        return Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
    }
    static void VerifyRecommendation(AuctionSettlementView window, int discount)
    {
        Pump(); Check(window.CurrentReport != null && window.CurrentReport.BestScenario.DiscountPercent == discount, "Recommendation did not change to coupon " + discount);
        Check(window.SelectedDiscount == 0, "Recommending a different coupon changed the actual no-coupon selection");
        string label = discount == 0 ? "쿠폰 없음" : discount + "%";
        Check(window.RecommendationText.IsVisible && window.RecommendationText.Text.Contains(label), "Recommendation summary does not identify the best coupon: " + window.RecommendationText.Text);
        var best = window.CouponCard(discount);
        var neutral = window.CouponCard(Coupons.First(d => d != discount));
        Color fill = BrushColor(best.Background), edge = BrushColor(best.BorderBrush);
        Check(fill.G > fill.R && fill.G >= fill.B && edge.G > edge.R && edge.G >= edge.B, "Best coupon is not emphasized with a green fill and border");
        Check(ColorDistance(fill, BrushColor(neutral.Background)) >= 12 && ColorDistance(edge, BrushColor(neutral.BorderBrush)) >= 30, "Best coupon highlight is indistinguishable from another coupon");
        if (discount != 0) Check(ColorDistance(fill, BrushColor(window.CouponCard(0).Background)) >= 12, "Actual no-coupon selection masks the different best coupon highlight");
        Check(Text(best).Contains("추천") || Text(best).Contains("비용 최소"), "Recommended card has no textual explanation of its highlight");
    }
    static MarketSnapshotData DifferentCouponFixture(string version, decimal[] prices, int minute)
    {
        var data = Fixture(version, false);
        var stamp = new DateTime(2026, 9, 13, 13, minute, 0, DateTimeKind.Utc);
        data.GeneratedUtc = stamp; data.ListingsFetchedUtc = stamp;
        foreach (var quote in data.Quotes.Values) quote.FetchedUtc = stamp;
        for (int i = 0; i < Coupons.Length; i++) data.Quotes["경매장 수수료 " + Coupons[i] + "% 할인 쿠폰"].UnitPrice = prices[i];
        return data;
    }
    static void VerifyCouponSourcesAndRecommendation()
    {
        var window = NewWindow(); SetSale(window, ManualGross);
        var original = DifferentCouponFixture("different-market", new[] { 210123m, 800456m, 1900789m, 11111222m, 22333444m }, 17);
        var refreshed = DifferentCouponFixture("different-market-refreshed", new[] { 310321m, 920654m, 2100987m, 12111333m, 23333555m }, 43);
        var later = DifferentCouponFixture("different-market-later", new[] { 123987m, 923456m, 2134567m, 12345678m, 23456789m }, 56);
        MarketSnapshotData next = refreshed;
        int explicitCalls = 0, before = refreshCalls;
        window.MarketPanel.Configure(delegate { return original; }, delegate {
            explicitCalls++; refreshCalls++; return Task.FromResult(new MarketSnapshotResult { Data = next, Downloaded = true });
        }, null);
        Wait(delegate { return !window.MarketPanel.IsBusy && Object.ReferenceEquals(window.MarketPanel.Snapshot, original); }, "different coupon fixture cache");
        VerifyCouponMarket(window, original);
        foreach (int discount in Coupons) Check(window.CouponSourceText(discount).Text.Contains("자동"), "Initial market coupon is not identified as automatic: " + discount);
        Check(Row(window, 10).CouponPrice == 210123m && Row(window, 20).CouponPrice == 800456m && Row(window, 100).CouponPrice == 22333444m, "Coupon defaults are screenshot constants rather than snapshot values");
        VerifyRecommendation(window, 10);
        foreach (bool dark in new[] { false, true }) {
            AppTheme.SetDark(dark); Pump(); Resize(window, 1140, 830);
            window.InputScroll.ScrollToTop(); window.BodyScroll.ScrollToTop(); Pump();
            VerifyRecommendation(window, 10); VerifyDefaultLayout(window);
            CaptureWindow(window, "settlement-coupon-recommendation-" + (dark ? "dark" : "light") + ".png");
        }
        AppTheme.SetDark(false); Pump();
        window.CouponPriceInput(10).Text = "1000000";
        Check(window.CouponSourceText(10).Text.Contains("직접"), "Manual coupon input retained its automatic source label");
        VerifyCouponMarket(window, original); VerifyRecommendation(window, 20);
        window.CouponPriceInput(20).Clear();
        Check(!Row(window, 20).IsKnown, "Cleared coupon cost was treated as zero");
        VerifyRecommendation(window, 0);
        Click(window.CouponMarketButton(10));
        Check(Row(window, 10).CouponPrice == 210123m && window.CouponSourceText(10).Text.Contains("자동"), "Follow-market action did not restore the latest quote and automatic mode");
        VerifyRecommendation(window, 10);
        window.CouponPriceInput(20).Text = "650123"; VerifyRecommendation(window, 20);
        window.PremiumControl.IsChecked = true; VerifyRecommendation(window, 10);
        window.PremiumControl.IsChecked = false; VerifyRecommendation(window, 20);
        window.GrossInput.Text = "10000000"; VerifyRecommendation(window, 0);
        window.GrossInput.Text = ManualGross; VerifyRecommendation(window, 20);
        window.CouponPriceInput(10).Text = "0";
        Check(window.CouponSourceText(10).Text.Contains("보유"), "Explicit zero coupon input is not labeled as owned/free");
        VerifyCouponMarket(window, original); VerifyRecommendation(window, 10);
        Click(window.MarketPanel.RefreshButton);
        Wait(delegate { return !window.MarketPanel.IsBusy && Object.ReferenceEquals(window.MarketPanel.Snapshot, refreshed); }, "refresh market labels behind manual costs");
        VerifyCouponMarket(window, refreshed);
        Check(Row(window, 10).CouponPrice == 0m && window.CouponSourceText(10).Text.Contains("보유")
            && Row(window, 20).CouponPrice == 650123m && window.CouponSourceText(20).Text.Contains("직접"), "New quotes replaced owned/manual coupon costs or their source modes");
        Check(Row(window, 30).CouponPrice == 2100987m && window.CouponSourceText(30).Text.Contains("자동"), "Automatic coupon did not advance with the new market snapshot");
        SameSale(window, "New market labels behind manual prices");
        Click(window.CouponMarketButton(10));
        Check(Row(window, 10).CouponPrice == 310321m && window.CouponSourceText(10).Text.Contains("자동"), "Owned coupon could not return to current market pricing");
        next = later; Click(window.MarketPanel.RefreshButton);
        Wait(delegate { return !window.MarketPanel.IsBusy && Object.ReferenceEquals(window.MarketPanel.Snapshot, later); }, "follow-market subsequent refresh");
        VerifyCouponMarket(window, later);
        Check(Row(window, 10).CouponPrice == 123987m && window.CouponSourceText(10).Text.Contains("자동") && Row(window, 20).CouponPrice == 650123m,
            "Follow-market mode did not persist into the next refresh or affected a different manual coupon");
        SameSale(window, "Follow-market subsequent refresh");
        VerifyRecommendation(window, 10);
        var missing = DifferentCouponFixture("different-market-missing", new[] { 123987m, 923456m, 2134567m, 12345678m, 23456789m }, 59);
        missing.Quotes.Remove("경매장 수수료 10% 할인 쿠폰"); next = missing; Click(window.MarketPanel.RefreshButton);
        Wait(delegate { return !window.MarketPanel.IsBusy && Object.ReferenceEquals(window.MarketPanel.Snapshot, missing); }, "coupon missing from the next snapshot");
        Check(!Row(window, 10).IsKnown && window.CouponPriceInput(10).Text == "" && window.CouponSourceText(10).Text.Contains("자동")
            && window.CouponMarketText(10).Text.Contains("미확인") && !window.CouponMarketText(10).Text.Contains("123,987 G"), "Missing automatic coupon retained an old price or recommendation eligibility");
        VerifyRecommendation(window, 20); SameSale(window, "Missing automatic coupon quote");
        Check(explicitCalls == 3 && refreshCalls == before + 3, "Coupon editing, recommendation, or following an already cached price fetched extra data");
        CloseWindow(window); current = null;
        Pass("different market fixture values and collection times stay visible behind manual/free costs; follow-market mode survives refresh; green recommendation moves with coupon/premium/sale inputs without changing selection or including unknown costs");
    }
    static void VerifyInvalidAndRemainder(AuctionSettlementView window)
    {
        Click(window.CouponSelectButton(0)); SetSale(window, "101");
        var row = Row(window, 0);
        Check(row.Fee == 5.05m && row.PerPerson == 23m && row.Remainder == 3.95m
            && Text(window).Contains("분배 후 남는 금액 3 G") && !Text(window).Contains("3.95 G") && !Text(window).Contains("5.05 G"),
            "Four-person fees/remainder did not truncate only their displayed amounts while retaining precise calculations");
        window.CouponPriceInput(20).Text = "123456.75"; Pump();
        Check(window.CouponPriceInput(20).Text == "123456.75" && Row(window, 20).CouponPrice == 123456.75m,
            "Integer money display altered a manually entered coupon price");
        window.CouponPriceInput(20).Text = "123456"; Pump();
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
        Pass("invalid amounts/people clear the report and disable guarded copying; unknown selected coupon stays unresolved; integer money display preserves raw fractional fees/remainder and manual coupon input; loss and long free-form-name report images");
    }
    static void InvalidReport(AuctionSettlementView window, string reason)
    {
        Pump(); Check(window.CurrentReport == null && !window.CopyImageButton.IsEnabled, "Invalid input retained a report or enabled copying: " + reason);
        Check(!window.RecommendationText.IsVisible, "Invalid input retained a visible recommendation: " + reason);
        VerifyCouponMarket(window, window.MarketPanel.Snapshot);
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
        CopyImage(unavailable, "settlement-no-provider.png"); CloseWindow(unavailable);
        var closing = NewWindow(); var completion = new TaskCompletionSource<MarketSnapshotResult>(); CancellationToken token = CancellationToken.None;
        closing.MarketPanel.Configure(delegate { return Fixture("close", false); }, delegate(CancellationToken pending) {
            refreshCalls++; token = pending; pending.Register(delegate { completion.TrySetCanceled(); }); return completion.Task;
        }, null);
        Wait(delegate { return !closing.MarketPanel.IsBusy && closing.MarketPanel.Snapshot != null; }, "close fixture cache");
        SetSale(closing, "100"); Click(closing.MarketPanel.RefreshButton);
        Check(closing.MarketPanel.IsBusy && token.CanBeCanceled, "Pending refresh has no cancellation token");
        CloseWindow(closing);
        Check(token.IsCancellationRequested, "Disposing the embedded settlement view on host close did not cancel refresh");
        Wait(delegate { return completion.Task.IsCanceled; }, "closed refresh cancellation");
        current = null;
        Pass("missing provider leaves manual calculations available; host close disposes the embedded view and cancels the injected pending refresh without later UI mutation");
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
        VerifyCouponMarket(window, snapshot);
        VerifyExpected(window, false); VerifySearch(window); VerifyImagesAndLayout(window);
        VerifyRefreshAndManualPrices(window, delegate(TaskCompletionSource<MarketSnapshotResult> value) { pending = value; });
        VerifyInvalidAndRemainder(window); VerifyNoProviderAndCloseCancellation(); VerifyCouponSourcesAndRecommendation(); VerifyInitialNameSearch();
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
    static void VerifyWidth(AuctionSettlementView window)
    {
        window.UpdateLayout();
        foreach (var control in LayoutControls(window)) {
            var bounds = Bounds(control, window);
            Check(control.ActualWidth > 10 && control.ActualHeight > 0 && bounds.Left >= -1 && bounds.Right <= window.ActualWidth + 1,
                "Embedded input, action or coupon clips horizontally at " + window.ActualWidth + "px: " + control.GetType().Name + " " + bounds);
            foreach (var viewport in AncestorViewports(control, window)) {
                var clipped = Bounds(control, viewport);
                Check(clipped.Left >= -1 && clipped.Right <= viewport.ActualWidth + 1,
                    "An inner viewport clips a control horizontally: " + control.GetType().Name + " " + clipped);
            }
        }
        foreach (var scroll in new[] { window.InputScroll, window.BodyScroll })
            Check(scroll.ViewportHeight > 100 && scroll.ViewportWidth > 100 && scroll.ScrollableWidth <= 1,
                "Embedded columns need usable vertical viewports without horizontal scrolling");
    }
    static List<FrameworkElement> LayoutControls(AuctionSettlementView window)
    {
        var controls = new List<FrameworkElement> { window.MarketPanel.ItemNameInput, window.MarketPanel.RefreshButton,
            window.GrossInput, window.PeopleInput, window.ExtraCostInput, window.PremiumControl,
            window.CopyImageButton, window.RecommendationText, SelectedSummary(window) };
        foreach (int discount in new[] { 0, 10, 20, 30, 50, 100 }) {
            controls.Add(window.CouponCard(discount)); controls.Add(window.CouponSelectButton(discount));
            if (discount == 0) continue;
            controls.Add(window.CouponPriceInput(discount)); controls.Add(window.CouponMarketButton(discount));
            controls.Add(window.CouponMarketText(discount)); controls.Add(window.CouponSourceText(discount));
        }
        return controls;
    }
    static TextBlock SelectedSummary(AuctionSettlementView window)
    {
        var summaries = Elements<TextBlock>(window).Where(t => t.Text.Contains("명 분배") && t.Text.Contains("1인당")).ToList();
        Check(summaries.Count == 1, "Selected settlement summary is missing or duplicated");
        return summaries[0];
    }
    static Rect Bounds(FrameworkElement control, FrameworkElement ancestor)
    {
        return control.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
    }
    static IEnumerable<FrameworkElement> AncestorViewports(FrameworkElement control, AuctionSettlementView window)
    {
        for (DependencyObject parent = VisualTreeHelper.GetParent(control); parent != null && parent != window; parent = VisualTreeHelper.GetParent(parent)) {
            var viewport = parent as ScrollContentPresenter; if (viewport != null) yield return viewport;
        }
    }
    static void VerifyFullyVisible(AuctionSettlementView window, FrameworkElement control, string context)
    {
        var bounds = Bounds(control, window);
        Check(control.IsVisible && control.ActualWidth > 10 && control.ActualHeight > 0
            && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
            context + " leaves a control outside the embedded view: " + control.GetType().Name + " " + bounds);
        foreach (var viewport in AncestorViewports(control, window)) {
            var clipped = Bounds(control, viewport);
            Check(clipped.Left >= -1 && clipped.Top >= -1 && clipped.Right <= viewport.ActualWidth + 1 && clipped.Bottom <= viewport.ActualHeight + 1,
                context + " leaves a control clipped inside its scroll viewport: " + control.GetType().Name + " " + clipped);
        }
    }
    static void VerifyDefaultLayout(AuctionSettlementView window)
    {
        string size = window.ActualWidth + "x" + window.ActualHeight;
        VerifyWidth(window);
        Check(window.InputScroll.ScrollableHeight <= 1 && window.BodyScroll.ScrollableHeight <= 1,
            "Default " + size + " embedded layout requires scrolling: inputs=" + window.InputScroll.ScrollableHeight + ", coupons=" + window.BodyScroll.ScrollableHeight);
        foreach (var control in LayoutControls(window)) VerifyFullyVisible(window, control, "Default " + size + " layout");
        var cards = new[] { 0, 10, 20, 30, 50, 100 }.Select(d => Bounds(window.CouponCard(d), window)).ToArray();
        for (int i = 1; i < cards.Length; i++)
            Check(cards[i].Top >= cards[i - 1].Bottom - 1 && Math.Abs(cards[i].Left - cards[0].Left) <= 2 && Math.Abs(cards[i].Right - cards[0].Right) <= 2,
                "Coupon comparison is not six ordered compact rows in a single column: row " + i);
        Check(Bounds(window.GrossInput, window).Right <= cards[0].Left,
            "Manual inputs and coupon comparison do not occupy separate left/right columns");
    }
    static void VerifyMinimumReachability(AuctionSettlementView window)
    {
        foreach (var control in LayoutControls(window)) {
            control.BringIntoView(); Pump(); window.UpdateLayout();
            VerifyFullyVisible(window, control, "Minimum 830x650 scroll fallback");
        }
        VerifyWidth(window);
    }
    static void CopyImage(AuctionSettlementView window, string filename)
    {
        int before = copyCalls; copiedImage = null; Click(window.CopyImageButton);
        Check(copyCalls == before + 1 && copiedImage != null, "Image copy did not use the injected sink exactly once");
        Check(copiedImage.PixelWidth == 1440 && Math.Abs(copiedImage.DpiX - 144) < 0.1 && copiedImage.PixelHeight >= 450 && copiedImage.PixelHeight < 15000, "Report image is cropped or has unexpected dimensions");
        SaveImage(copiedImage, filename);
        var direct = window.CreateReportImage();
        Check(direct != null && direct.PixelWidth == copiedImage.PixelWidth && direct.PixelHeight == copiedImage.PixelHeight, "Copy and direct report image sizes differ");
    }
    static void CaptureWindow(AuctionSettlementView window, string filename)
    {
        window.UpdateLayout(); var root = (FrameworkElement)window;
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
        } finally { foreach (var host in windows.Values) if (host.IsLoaded) host.Close(); }
    }
}
