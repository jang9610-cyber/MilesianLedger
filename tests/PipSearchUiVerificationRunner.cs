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
            VerifyEnchantScrolls();
            VerifyInitialSearch();
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
        for (int i = 0; i < 20; i++) steps.Add(new ProcurementStep { Key = "craft:fixture" + i, Name = "제작 확인 재료 " + i.ToString("00"), Kind = "craft", Quantity = i + 1, GroupKey = "lower", GroupLabel = "테스트 제작" });
        var window = NewWindow(delegate(string key, bool value) { checkCalls++; checkKey = key; checkValue = value; });
        window.UpdateSteps(steps, 0m, null); Pump(); Pump();
        var primaryBounds = PrimaryTabBounds(window);
        VerifyPrimaryTabs(window, primaryBounds, false);
        Assert(Convert.ToString(window.TradeTabButton.Content) == "교역" && Convert.ToString(window.SearchTabButton.Content) == "경매장 검색", "Primary tabs do not identify trade and auction search");
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
        VerifyPrimaryTabs(window, primaryBounds, true);
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
        VerifyPrimaryTabs(window, primaryBounds, true);
        Click(window.TradeTabButton); Pump();
        Assert(window.SelectedTab == 1, "Trade tab did not return to the purchase checklist");
        VerifyPrimaryTabs(window, primaryBounds, false);
        window.PurchaseScroll.ScrollToVerticalOffset(130); Pump(); Pump();
        double purchaseOffset = window.PurchaseScroll.VerticalOffset;
        Assert(purchaseOffset > 0, "Purchase fixture has no scrollable checklist");
        var check = window.ReadyControls[steps[0].Key]; check.IsChecked = true;
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(checkCalls == 1 && checkKey == steps[0].Key && checkValue, "Purchase checkbox callback broke");
        steps[0].IsReady = true; window.UpdateSteps(steps, 5m, null);
        Click(window.SearchTabButton); Pump();
        VerifyPrimaryTabs(window, primaryBounds, true);
        Assert(Object.ReferenceEquals(searchInput, window.SearchInput) && window.SearchInput.Text == "오프라인 공통 재료", "Tab switching lost the search input");
        Assert(Math.Abs(window.SearchResultsScroll.VerticalOffset - searchOffset) < 2, "Tab switching reset search scroll");
        Click(window.TradeTabButton); Pump();
        Assert(Math.Abs(window.PurchaseScroll.VerticalOffset - purchaseOffset) < 2, "Search tab reset purchase scroll");
        Assert(Object.ReferenceEquals(check, window.ReadyControls[steps[0].Key]) && check.IsChecked == true, "Search navigation lost the synchronized purchase check");
        Click(window.PreparationTabButton); Pump();
        Assert(window.SelectedTab == 2, "Preparation subtab did not select the craft checklist");
        VerifyPrimaryTabs(window, primaryBounds, false);
        window.PreparationScroll.ScrollToVerticalOffset(170); Pump(); Pump();
        double preparationOffset = window.PreparationScroll.VerticalOffset;
        Assert(preparationOffset > 0, "Preparation fixture has no scrollable checklist");
        var craft = steps.First(s => s.Kind == "craft");
        var craftCheck = window.ReadyControls[craft.Key]; craftCheck.IsChecked = true;
        craftCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(checkCalls == 2 && checkKey == craft.Key && checkValue, "Preparation checkbox callback broke");
        Click(window.SearchTabButton); Pump();
        VerifyPrimaryTabs(window, primaryBounds, true);
        craft.IsReady = true; window.UpdateSteps(steps, 33m, null); Pump(); Pump();
        Assert(window.SelectedTab == 3 && Object.ReferenceEquals(firstCard, Cards(window).First()) && window.SearchInput.Text == "오프라인 공통 재료", "Craft synchronization replaced the active search state");
        Assert(Math.Abs(window.SearchResultsScroll.VerticalOffset - searchOffset) < 2, "Craft synchronization reset search scroll");
        VerifyPrimaryTabs(window, primaryBounds, true);
        Click(window.TradeTabButton); Pump();
        Assert(window.SelectedTab == 2, "Craft to search to trade navigation forgot the craft subtab");
        VerifyPrimaryTabs(window, primaryBounds, false);
        Assert(Math.Abs(window.PreparationScroll.VerticalOffset - preparationOffset) < 2, "Search navigation reset preparation scroll");
        Assert(Object.ReferenceEquals(craftCheck, window.ReadyControls[craft.Key]) && craftCheck.IsChecked == true, "Search navigation lost the synchronized preparation check");
        Assert(window.OverallProgress.Value == 33 && window.OverallPercentText.Text.Contains("33"), "Trade view did not show the progress synchronized while searching");
        Click(window.SearchTabButton); Pump();
        VerifyPrimaryTabs(window, primaryBounds, true);
        Wait(delegate { return !window.SearchBusy; }, "return to cached search");
        Assert(refreshCalls == 0, "Typing or tab switching fetched data");
        Assert(!window.SearchStatusText.Text.Contains("읽지 못"), "Returning to the cache failed");
        passed.Add("cached exact/space-insensitive/full-universe search; fractional prices and unknown quantity; empty/option/no-result states; 30-result cap; zero implicit fetches");
        passed.Add("fixed trade/search primary tabs; hidden trade progress and subtabs while searching; remembered craft subtab; both checklist callbacks, checks, scrolls, search query and cards preserved through synchronization");

        Query(window, LongName, LongName);
        window.Width = 320; window.Height = 540; PumpFor(100);
        VerifyWidth(window);
        VerifyPrimaryNavigationGeometry(window, "tabs-minimum-light");
        var name = Blocks(window.SearchResultsPanel).First(t => t.Text == LongName);
        Assert(name.TextWrapping != TextWrapping.NoWrap && name.ActualHeight > name.FontSize * 2, "Long name does not wrap at minimum width");
        Color light = ((SolidColorBrush)window.SearchInput.Background).Color;
        var visibleCard = Cards(window).First();
        Capture(window, "search-minimum-light.png");
        AppTheme.SetDark(true); Pump(); Pump();
        VerifyWidth(window);
        VerifyPrimaryNavigationGeometry(window, "tabs-minimum-dark");
        Assert(((SolidColorBrush)window.SearchInput.Background).Color != light, "Existing search field did not update its theme");
        Assert(Object.ReferenceEquals(visibleCard, Cards(window).First()) && window.SearchInput.Text == LongName, "Theme change rebuilt or reset search");
        Capture(window, "search-minimum-dark.png");
        AppTheme.SetDark(false); Pump();
        window.Width = 360; PumpFor(100);
        VerifyWidth(window); VerifyPrimaryNavigationGeometry(window, "tabs-default-light"); Capture(window, "search-default-light.png");
        AppTheme.SetDark(true); Pump(); Pump();
        VerifyWidth(window); VerifyPrimaryNavigationGeometry(window, "tabs-default-dark"); Capture(window, "search-default-dark.png");
        Assert(window.SearchInput.Focusable && window.SearchInput.IsTabStop && !window.SearchInput.IsReadOnly, "Search field cannot accept normal keyboard input");
        FocusManager.SetFocusedElement(window, window.SearchInput); Pump();
        Assert(Object.ReferenceEquals(FocusManager.GetFocusedElement(window), window.SearchInput), "Search input cannot retain logical focus");
        AppTheme.SetDark(false); Pump();
        passed.Add("live light/dark appearance, wrapped long names and invariant primary tab bounds across purchase/craft/search at 320px and 360px, with captures");
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

    static void VerifyInitialSearch()
    {
        var stamp = DateTime.UtcNow.AddMinutes(-10);
        var snapshot = new MarketSnapshotData { Version = "initial-search", GeneratedUtc = stamp, ListingsFetchedUtc = stamp,
            Items24h = new List<MarketSnapshotItem>(), Items7d = new List<MarketSnapshotItem>(),
            Quotes = new Dictionary<string, MarketSnapshotQuote>(StringComparer.Ordinal), Status = new Dictionary<string, object>() };
        string[] names = { "거미줄", "거미줄 조각", "가는 거미줄", "실리엔", "가는 실뭉치" };
        decimal[] prices = { 187.25m, 991m, 992m, 321.75m, 456.5m };
        for (int i = 0; i < names.Length; i++) {
            snapshot.Items24h.Add(Item(names[i], true)); snapshot.Items7d.Add(Item(names[i], true));
            snapshot.Quotes[names[i]] = new MarketSnapshotQuote { Name = names[i], UnitPrice = prices[i],
                Quantity = 600, ListingCount = 6, FetchedUtc = stamp };
        }
        int beforeRefresh = refreshCalls, reads = 0, requests = 0;
        var window = NewWindow(null);
        window.ConfigureMarketSearch(delegate { Interlocked.Increment(ref reads); return snapshot; }, delegate {
            Interlocked.Increment(ref requests); Interlocked.Increment(ref refreshCalls);
            return Task.FromResult(new MarketSnapshotResult { Data = snapshot });
        }, "");
        window.SelectedTab = 3;
        Query(window, "거미줄", "거미줄");
        Assert(Blocks(Cards(window).First()).Any(t => t.Text == "거미줄") && Text(Cards(window).First()).Contains("187.25 G"),
            "Literal spider-web search lost its exact-first result or authoritative fractional quote");
        Query(window, "ㄱㅁㅈ", "거미줄");
        var initialCards = Cards(window);
        Assert(initialCards.Count == 3 && Blocks(initialCards[0]).Any(t => t.Text == "거미줄")
            && Blocks(initialCards[1]).Any(t => t.Text == "거미줄 조각") && Blocks(initialCards[2]).Any(t => t.Text == "가는 거미줄"),
            "Initial-only search did not render full, prefix and contained matches in order");
        Assert(Text(initialCards[0]).Contains("187.25 G") && Text(initialCards[0]).Contains("600개")
            && !Text(initialCards[0]).Contains("190 G"), "Initial-only search changed the spider-web quote or used the metadata minimum");
        Query(window, "ㅅㄹㅇ", "실리엔");
        Assert(Cards(window).Count == 1 && Text(Cards(window).First()).Contains("321.75 G"), "Silien initials did not retain the single item and its price");
        Query(window, "가는 ㅅㅁㅊ", "가는 실뭉치");
        Assert(Cards(window).Count == 1 && Text(Cards(window).First()).Contains("456.5 G"), "Mixed syllable/initial search did not find the fine thread item");
        Query(window, "ㄱㅁㅈ", "거미줄");
        window.Width = 320; window.Height = 540; PumpFor(100); VerifyWidth(window);
        Capture(window, "search-initials-minimum-light.png");
        Assert(Text(Cards(window).First()).Contains("187.25 G") && snapshot.Quotes["거미줄"].UnitPrice == 187.25m
            && snapshot.Items24h.First(item => item.Name == "거미줄").LowestListingPrice == 190m,
            "Changing between literal, initial-only and mixed queries mutated the spider-web market data");
        Assert(reads == 1 && requests == 0 && refreshCalls == beforeRefresh, "Initial-only or mixed typing fetched data or reloaded its cache");
        window.Close(); Pump(); current = null;
        passed.Add("PIP initial-only ㄱㅁㅈ/ㅅㄹㅇ and mixed 가는 ㅅㅁㅊ searches; full/prefix/contains ordering, unchanged authoritative spider-web fractional price and quantity, one cached load and zero implicit refreshes");
    }

    static void VerifyEnchantScrolls()
    {
        var snapshot = Fixture("enchant-scrolls");
        string normal = "템포 (접미 / 랭크 6) · 인챈트 스크롤", dedicated = "템포 (접미 / 랭크 6) · 전용 인챈트 스크롤";
        var stamp = snapshot.ListingsFetchedUtc.Value;
        snapshot.Items24h.Add(new MarketSnapshotItem { Name = "인챈트 스크롤", Category = "인챈트 스크롤", PriceComparable = false, ListingCount = 3, ListedQuantity = 5 });
        snapshot.Quotes["인챈트 스크롤"] = new MarketSnapshotQuote { Name = "인챈트 스크롤", UnitPrice = 100m, ListingCount = 3, Quantity = 5, FetchedUtc = stamp };
        snapshot.Items24h.Add(new MarketSnapshotItem { Name = "개방된 전용 인챈트 스크롤", Category = "인챈트 스크롤", PriceComparable = false, ListingCount = 2, ListedQuantity = 2 });
        foreach (string name in new[] { normal, dedicated })
            snapshot.Items24h.Add(new MarketSnapshotItem { Name = name, Category = "인챈트 스크롤", PriceComparable = true });
        snapshot.Quotes[normal] = new MarketSnapshotQuote { Name = normal, UnitPrice = 123456m, ListingCount = 2, Quantity = 3, FetchedUtc = stamp };
        snapshot.Quotes[dedicated] = new MarketSnapshotQuote { Name = dedicated, UnitPrice = 654321m, ListingCount = 1, Quantity = 1, FetchedUtc = stamp };
        var window = NewWindow(null);
        window.ConfigureMarketSearch(delegate { return snapshot; }, delegate { Interlocked.Increment(ref refreshCalls); return Task.FromResult(new MarketSnapshotResult { Data = snapshot }); }, "");
        window.SelectedTab = 3;
        Query(window, "인챈트 스크롤", "인챈트 이름 미확인");
        var generic = Cards(window).First(c => Blocks(c).Any(t => t.Text == "인챈트 스크롤"));
        Assert(Text(generic).Contains("인챈트 이름 미확인") && Text(generic).Contains("시세 갱신 후 이름으로 검색하세요."), "Unidentified enchant state or refresh guidance missing");
        Assert(!Text(generic).Contains("100 G") && !Text(generic).Contains("매물 없음"), "Old generic scroll exposed a mixed price or false empty listing");
        Query(window, "개방된전용인챈트스크롤", "개방된 전용 인챈트 스크롤");
        Assert(Text(window.SearchResultsPanel).Contains("인챈트 이름 미확인") && !Text(window.SearchResultsPanel).Contains("매물 없음"), "Metadata-only generic scroll became an empty market");
        Query(window, "템포", normal);
        Assert(Cards(window).Count == 2 && Text(window.SearchResultsPanel).Contains(dedicated), "Enchant name did not find both distinct scroll forms");
        Assert(Text(window.SearchResultsPanel).Contains("123,456 G") && Text(window.SearchResultsPanel).Contains("654,321 G"), "Scroll forms did not retain their own prices");
        Assert(Text(window.SearchResultsPanel).Contains("같은 인챈트 이름 · 스크롤 종류 기준") && !Text(window.SearchResultsPanel).Contains("옵션별 가격 차이"), "Named scroll has the wrong comparison cue");
        Query(window, "ㅌㅍ", normal);
        Assert(Cards(window).Count == 2 && Text(Cards(window).Single(c => Blocks(c).Any(t => t.Text == normal))).Contains("123,456 G")
            && Text(Cards(window).Single(c => Blocks(c).Any(t => t.Text == dedicated))).Contains("654,321 G"),
            "Tempo initials merged normal/dedicated scroll identities or replaced their individual prices");
        window.Width = 320; window.Height = 540; PumpFor(100); VerifyWidth(window);
        Capture(window, "search-enchant-light.png");
        AppTheme.SetDark(true); Pump(); Pump(); VerifyWidth(window);
        Capture(window, "search-enchant-dark.png");
        Query(window, "인챈트 스크롤", "인챈트 이름 미확인");
        Capture(window, "search-enchant-unidentified-dark.png");
        Assert(refreshCalls == 0, "Enchant searches or theme switching requested server data");
        AppTheme.SetDark(false); Pump();
        passed.Add("named enchant scrolls and base types with light/dark captures; ㅌㅍ initials preserve normal/dedicated scroll identities and prices; cached mixed minima hidden; metadata-only scrolls identified without false empty listings; zero implicit fetches");
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
    static Rect Bounds(PipChecklistWindow window, FrameworkElement control)
    {
        var root = (FrameworkElement)window.Content;
        return control.TransformToAncestor(root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
    }
    static Rect[] PrimaryTabBounds(PipChecklistWindow window)
    {
        window.UpdateLayout();
        Assert(window.TradeTabButton != null && window.SearchTabButton != null, "Trade/search primary tab controls are missing");
        Assert(window.TradeTabButton.IsVisible && window.SearchTabButton.IsVisible, "A primary tab is not visible");
        return new[] { Bounds(window, window.TradeTabButton), Bounds(window, window.SearchTabButton) };
    }
    static void VerifyPrimaryTabs(PipChecklistWindow window, Rect[] expected, bool search)
    {
        var actual = PrimaryTabBounds(window);
        var root = (FrameworkElement)window.Content;
        for (int i = 0; i < actual.Length; i++) {
            Assert(actual[i].Width > 0 && actual[i].Height > 0, "A primary tab has no clickable area");
            Assert(actual[i].Left >= -1 && actual[i].Right <= root.ActualWidth + 1, "A primary tab clips outside the window");
            Assert(Math.Abs(actual[i].Top - expected[i].Top) < 1 && Math.Abs(actual[i].Left - expected[i].Left) < 1
                && Math.Abs(actual[i].Width - expected[i].Width) < 1 && Math.Abs(actual[i].Height - expected[i].Height) < 1,
                "Primary tab bounds moved while switching trade/search: expected " + expected[i] + ", actual " + actual[i]);
        }
        Assert(Math.Abs(actual[0].Top - actual[1].Top) < 1 && actual[0].Right <= actual[1].Left + 1, "Trade/search primary tabs are not aligned as two separate buttons");
        Assert((window.SelectedTab == 3) == search, "SelectedTab does not match the visible primary view");
        foreach (var control in new FrameworkElement[] { window.PurchaseTabButton, window.PreparationTabButton,
            window.OverallPercentText, window.OverallProgress, window.SelectedCountText, window.RemainingOnlyControl }) {
            Assert(control.IsVisible == !search, "Trade-only control has wrong visibility in search/trade: " + control.GetType().Name);
            if (!search) Assert(Bounds(window, control).Top >= Math.Max(actual[0].Bottom, actual[1].Bottom) - 1, "Trade progress or subtab appears above the primary navigation");
        }
        Assert(window.SearchInput.IsVisible == search && window.SearchResultsScroll.IsVisible == search, "Auction search controls leaked into the trade view or disappeared from search");
        Assert(window.PurchaseScroll.IsVisible == (!search && window.SelectedTab == 1)
            && window.PreparationScroll.IsVisible == (!search && window.SelectedTab == 2), "The wrong checklist is visible under the selected subtab");
    }
    static void VerifyPrimaryNavigationGeometry(PipChecklistWindow window, string capturePrefix)
    {
        Assert(window.SelectedTab == 3, "Navigation geometry fixture must begin in search");
        var expected = PrimaryTabBounds(window);
        var input = window.SearchInput;
        string query = input.Text;
        var cards = Cards(window).ToArray();
        double searchOffset = window.SearchResultsScroll.VerticalOffset;
        Click(window.TradeTabButton); Pump();
        Assert(window.SelectedTab == 2, "Trade return forgot the last preparation subtab during size/theme verification");
        VerifyPrimaryTabs(window, expected, false);
        Capture(window, capturePrefix + "-craft.png");
        Click(window.PurchaseTabButton); Pump();
        Assert(window.SelectedTab == 1, "Purchase subtab selection changed its saved numeric value");
        VerifyPrimaryTabs(window, expected, false);
        Capture(window, capturePrefix + "-purchase.png");
        Click(window.PreparationTabButton); Pump();
        Assert(window.SelectedTab == 2, "Preparation subtab selection changed its saved numeric value");
        VerifyPrimaryTabs(window, expected, false);
        Click(window.SearchTabButton); Pump();
        Wait(delegate { return !window.SearchBusy; }, "search after primary tab geometry checks");
        VerifyPrimaryTabs(window, expected, true);
        Assert(Object.ReferenceEquals(input, window.SearchInput) && input.Text == query, "Navigation geometry checks lost the search input");
        Assert(cards.SequenceEqual(Cards(window)), "Primary navigation rebuilt the cached search results");
        Assert(Math.Abs(window.SearchResultsScroll.VerticalOffset - searchOffset) < 2, "Primary navigation changed the search scroll at this size/theme");
    }
    static void VerifyWidth(PipChecklistWindow window)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        foreach (var control in new FrameworkElement[] { window.TradeTabButton, window.SearchTabButton, window.SearchInput, window.SearchRefreshButton, window.SearchResultsScroll }) {
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
