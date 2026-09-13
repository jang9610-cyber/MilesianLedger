using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MabinogiBarter;

// Runs against an isolated runtime with no provider, saved player profile or HTTP.
public static class WorkspaceUiVerificationRunner
{
    static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    static void Call(object target, string name, params object[] args) { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); Pump(); }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T) yield return (T)root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    static Button Menu(MainWindow main, string name) { return Children<Button>(main).First(b => AutomationProperties.GetName(b) == name + " 메뉴"); }
    static void Selected(MainWindow main, string name)
    {
        Check(AutomationProperties.GetItemStatus(Menu(main, name)) == "현재 화면", "wrong selected menu: " + name);
        Check(Children<Button>(main).Count(b => AutomationProperties.GetItemStatus(b) == "현재 화면") == 1, "multiple selected pages");
        Check(Application.Current.Windows.Count == 1 && main.OwnedWindows.Count == 0, "navigation opened another window");
    }
    static void Capture(MainWindow main, string folder, string name) { Pump(); main.SavePreview(Path.Combine(folder, name + ".png")); }
    static MarketSnapshotData Fixture()
    {
        var time = DateTime.UtcNow.AddMinutes(-10);
        var quotes = new Dictionary<string, MarketSnapshotQuote>();
        int[] discounts = { 10, 20, 30, 50, 100 }; decimal[] prices = { 210000, 740000, 1680000, 12940000, 19550000 };
        for (int i = 0; i < discounts.Length; i++) {
            string name = "경매장 수수료 " + discounts[i] + "% 할인 쿠폰";
            quotes[name] = new MarketSnapshotQuote { Name = name, UnitPrice = prices[i], ListingCount = 3, Quantity = 8, FetchedUtc = time };
        }
        var items = new List<MarketSnapshotItem>();
        for (int i = 0; i < 60; i++) items.Add(new MarketSnapshotItem {
            Name = "거미줄 검증용 품목 " + (i + 1).ToString("D2"), Category = "테스트 데이터", SoldQuantity = 10000 + i * 100,
            TradeCount = 90 + i, TradedGold = 3000000 + i * 250000, ListedQuantity = 800 + i * 10, ListingCount = 40 + i,
            AverageSalePrice = 320 + i, LowestListingPrice = 250 + i, PriceComparable = true
        });
        return new MarketSnapshotData { Version = "workspace-fixture", GeneratedUtc = time, ListingsFetchedUtc = time, Quotes = quotes,
            Items24h = items, Items7d = items, Status = new Dictionary<string, object>() };
    }
    [STAThread] public static int Main(string[] args)
    {
        MainWindow main = null;
        try {
            string root = AppDomain.CurrentDomain.BaseDirectory, output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            // This is a new generated runtime, never the installed app directory.
            File.WriteAllText(Path.Combine(root, "data", "auction-proxy.json"), "{}");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            AppTheme.Initialize(Path.Combine(output, "appearance.txt")); AppTheme.SetDark(false); AppMotion.ReducedMotion = true;
            main = new MainWindow(Catalog.Load(Path.Combine(root, "data", "barter-data.json")), new StateStore(Path.Combine(output, "progress.json")), true);
            main.Left = -18000; main.Top = -18000; main.ShowActivated = false; main.ShowInTaskbar = false; main.Show(); Pump();
            Check(main.Title == "밀레시안 장부", "window title still limits the app to barter");
            var labels = ((StackPanel)Field(main, "nav")).Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check(labels.SequenceEqual(new[] { "교역", "경매장", "게임 중 도구", "앱 설정" }), "sidebar group order");
            Selected(main, "교역 계획"); Capture(main, output, "workspace-trade-light");

            Call(main, "ShowAuctionSettlement"); Selected(main, "수수료·분배");
            var settlement = (AuctionSettlementView)Field(main, "settlementView");
            Check(settlement.IsVisible && ((FrameworkElement)Field(main, "plannerHeader")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)Field(main, "plannerStats")).Visibility == Visibility.Collapsed, "trade chrome takes settlement space");
            var fixture = Fixture(); settlement.MarketPanel.Configure(() => fixture, null, null);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (settlement.MarketPanel.Snapshot != fixture && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            Check(settlement.MarketPanel.Snapshot == fixture, "settlement cache fixture did not load");
            settlement.MarketPanel.ItemNameInput.Text = "직접 입력한 판매품";
            settlement.GrossInput.Text = "100000000"; settlement.PeopleInput.Text = "4";
            settlement.CouponPriceInput(10).Text = "123456";
            settlement.CouponSelectButton(10).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Capture(main, output, "workspace-settlement-light");
            Check(settlement.BodyScroll.ScrollableHeight <= 1, "six coupon choices need scrolling at the default app size");
            AppTheme.SetDark(true); Capture(main, output, "workspace-settlement-dark");

            Call(main, "ShowMarketStatistics"); Selected(main, "시장 통계");
            var market = (MarketStatisticsView)Field(main, "marketView");
            Call(market, "ApplyData", fixture);
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (market.Table.Items.Count == 0 && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            Check(market.Table.Items.Count == 60, "table fixture did not fill the embedded page");
            market.SearchInput.Text = "거미줄"; market.PeriodInput.SelectedIndex = 1; market.SortInput.SelectedIndex = 2; Pump();
            Check(market.IsVisible && !market.RefreshButton.IsEnabled, "unconfigured page unexpectedly enables HTTP");
            Capture(main, output, "workspace-market-dark");
            Call(main, "RefreshProgress"); Call(main, "RenderAll");
            Check(market.IsVisible && market.SearchInput.Text == "거미줄", "background trade update displaced the market page");
            var steps = (IEnumerable<ProcurementStep>)Field(main, "procurementReadinessSteps");
            var step = steps.FirstOrDefault();
            if (step != null) { Call(main, "SetPipReady", step.Key, !step.IsReady); Check(market.IsVisible, "PIP checklist update displaced the market page"); }

            main.ShowSummary(); Pump(); Selected(main, "재료 준비");
            Check(((FrameworkElement)Field(main, "plannerStats")).IsVisible, "trade stats not restored");
            Call(main, "ShowAuctionSettlement"); Selected(main, "수수료·분배");
            Check(Object.ReferenceEquals(Field(main, "settlementView"), settlement)
                && settlement.GrossInput.Text == "100000000" && settlement.PeopleInput.Text == "4"
                && settlement.CouponPriceInput(10).Text == "123456" && settlement.SelectedDiscount == 10
                && settlement.MarketPanel.ItemNameInput.Text == "직접 입력한 판매품", "navigation lost manual settlement state");
            Call(main, "ShowMarketStatistics"); Selected(main, "시장 통계");
            Check(Object.ReferenceEquals(Field(main, "marketView"), market) && market.SearchInput.Text == "거미줄"
                && market.PeriodInput.SelectedIndex == 1 && market.SortInput.SelectedIndex == 2, "navigation lost market filters");
            AppTheme.SetDark(false); Capture(main, output, "workspace-market-light");

            main.Width = 1100; main.Height = 740; Pump();
            Capture(main, output, "workspace-market-minimum");
            Call(main, "ShowAuctionSettlement"); Capture(main, output, "workspace-settlement-minimum");
            Check(settlement.ActualWidth > 800 && settlement.ActualHeight > 600, "embedded page is unnecessarily constrained");
            main.ShowStationHub(); Pump(); Selected(main, "교역 계획");
            main.Close(); main = null; Pump();
            Check(Application.Current.Windows.Count == 0, "page lifetime left an orphan window");
            Console.WriteLine("PASS exact app title; grouped/selected navigation; embedded market and settlement without extra windows; six coupon choices fit default height; light/dark and minimum-size captures; manual amounts/coupon/selection and filters survive navigation; background trade/PIP changes preserve the active page; app close releases pages. No HTTP or OS clipboard.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (main != null) main.Close(); }
    }
}
