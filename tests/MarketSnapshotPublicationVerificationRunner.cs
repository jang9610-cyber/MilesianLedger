using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MabinogiBarter;

// The snapshot protocol/validation is covered by MarketSnapshotVerificationRunner.
// This fixture injects verified local publications to exercise open-view observers
// without HTTP requests, a production provider, or the operating-system clipboard.
public static class MarketSnapshotPublicationVerificationRunner
{
    sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; throw new InvalidOperationException("Publication observation must not request HTTP.");
        }
    }
    static readonly MethodInfo publish = typeof(MarketSnapshotClient).GetMethod("PublishSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void Wait(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready()) { if (elapsed.ElapsedMilliseconds > 6000) throw new Exception("Publication fixture timed out."); Pump(); Thread.Sleep(5); }
        Pump();
    }
    static IEnumerable<TextBlock> Blocks(DependencyObject root)
    {
        var text = root as TextBlock; if (text != null) yield return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Blocks(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    static string Text(DependencyObject root) { return String.Join("\n", Blocks(root).Select(value => value.Text)); }
    static MarketSnapshotData Data(string version, decimal price, int count)
    {
        var stamp = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var data = new MarketSnapshotData { Version = version, GeneratedUtc = stamp, ListingsFetchedUtc = stamp,
            Items24h = new List<MarketSnapshotItem>(), Items7d = new List<MarketSnapshotItem>(),
            Quotes = new Dictionary<string, MarketSnapshotQuote>(), Status = new Dictionary<string, object>() };
        for (int i = 0; i < count; i++) {
            string name = "공유 재료 " + i;
            data.Items24h.Add(new MarketSnapshotItem { Name = name, Category = "재료", PriceComparable = true, AverageSalePrice = price + 1 });
            data.Quotes[name] = new MarketSnapshotQuote { Name = name, UnitPrice = price, Quantity = 2, ListingCount = 1, FetchedUtc = stamp };
        }
        foreach (int discount in new[] { 10, 20, 30, 50, 100 }) {
            string name = "경매장 수수료 " + discount + "% 할인 쿠폰";
            data.Items24h.Add(new MarketSnapshotItem { Name = name, Category = "기타 소모품", PriceComparable = true, AverageSalePrice = price * 1000m + discount });
            data.Quotes[name] = new MarketSnapshotQuote { Name = name, UnitPrice = price * 1000m + discount, Quantity = 2, ListingCount = 1, FetchedUtc = stamp };
        }
        return data;
    }
    static void ManualState(AuctionSettlementView view, string operation)
    {
        Check(view.GrossInput.Text == "123,456,789.00" && view.PeopleInput.Text == "7" && view.ExtraCostInput.Text == "321"
            && view.PremiumControl.IsChecked == true && view.SelectedDiscount == 20,
            operation + " changed the embedded sale, fee option or selected coupon");
        Check(view.CouponPriceInput(10).Text == "765432" && view.CouponPriceInput(20).Text == "0" && view.CouponPriceInput(100).Text == "",
            operation + " changed a manual, owned or unknown coupon cost");
        Check(view.MarketPanel.ItemNameInput.Text == "공유 재료 0" && view.MarketPanel.SelectedItemName == "공유 재료 0",
            operation + " lost the retained item name and selection");
        Check(view.CurrentReport != null && view.CurrentReport.Scenarios.Single(s => s.DiscountPercent == 20).CouponPrice == 0m
            && !view.CurrentReport.Scenarios.Single(s => s.DiscountPercent == 100).IsKnown && view.CopyImageButton.IsEnabled,
            operation + " lost the selected report or treated the unresolved coupon as free");
    }
    static Task Publish(MarketSnapshotClient client, MarketSnapshotData data)
    {
        return Task.Run(delegate { publish.Invoke(client, new object[] { data }); });
    }
    [STAThread] public static int Main(string[] args)
    {
        Window window = null; AuctionSettlementView view = null;
        try {
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            AppTheme.Initialize(Path.Combine(output, "appearance.txt"));
            var handler = new NoNetwork();
            var client = (MarketSnapshotClient)Activator.CreateInstance(typeof(MarketSnapshotClient), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { new Uri("https://market.example/"), Path.Combine(output, "missing-cache.json"), handler }, null);
            var first = Data("first", 40m, 2);
            publish.Invoke(client, new object[] { first });
            view = new AuctionSettlementView(); var panel = view.MarketPanel; int callbacks = 0; bool callbackOnUi = true;
            int uiThread = Thread.CurrentThread.ManagedThreadId;
            panel.SnapshotChanged += delegate { callbacks++; callbackOnUi &= Thread.CurrentThread.ManagedThreadId == uiThread; };
            panel.Configure(client);
            var content = new Grid(); content.Children.Add(view);
            window = new Window { Content = content, Width = 1140, Height = 830, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -18000, Top = -18000, ShowActivated = false, ShowInTaskbar = false };
            var hostedView = view; window.Closed += delegate { hostedView.Dispose(); };
            window.Show(); Wait(() => !panel.IsBusy && Object.ReferenceEquals(panel.Snapshot, first));
            Check(window.Left < -10000 && !window.IsActive, "embedded publication host became visible or active");
            panel.ItemNameInput.Text = "공유 재료 0";
            Wait(() => Text(panel.ReferencePanel).Contains("40 G"));
            view.GrossInput.Text = "123,456,789.00"; view.PeopleInput.Text = "7"; view.ExtraCostInput.Text = "321";
            view.PremiumControl.IsChecked = true; view.CouponPriceInput(10).Text = "765432";
            view.CouponPriceInput(20).Text = "0"; view.CouponPriceInput(100).Clear();
            view.CouponSelectButton(20).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            ManualState(view, "Initial cached view");
            Check(callbacks == 1 && handler.Calls == 0, "initial shared cache should load once without HTTP");
            var second = Data("second", 80m, 2); var external = Publish(client, second);
            Wait(() => external.IsCompleted && Object.ReferenceEquals(panel.Snapshot, second));
            Check(callbacks == 2 && callbackOnUi && Text(panel.ReferencePanel).Contains("80 G"), "external publication must update references and callback on UI thread");
            ManualState(view, "External shared publication");
            Check(view.CurrentReport.Scenarios.Single(s => s.DiscountPercent == 30).CouponPrice == 80030m
                && view.CouponMarketText(10).Text.Contains("80,010 G") && view.CouponSourceText(10).Text.Contains("직접")
                && view.CouponSourceText(20).Text.Contains("보유"), "shared publication did not update quote labels and automatic prices behind retained manual costs");
            var same = Publish(client, second); Wait(() => same.IsCompleted);
            Check(callbacks == 2, "identical reference must not notify an open panel twice");

            // Shell navigation detaches and restores this same view. Unloaded is
            // not disposal: inputs, quote mode and the shared subscription survive.
            decimal? selectedNet = view.CurrentReport.Scenarios.Single(s => s.DiscountPercent == 20).NetAmount;
            content.Children.Remove(view); content.Children.Add(new TextBlock { Text = "다른 화면" }); Pump();
            Check(!view.IsLoaded, "navigation fixture failed to detach the embedded view");
            var hiddenData = Data("while-hidden", 85m, 2); var hidden = Publish(client, hiddenData);
            Wait(() => hidden.IsCompleted && Object.ReferenceEquals(panel.Snapshot, hiddenData));
            ManualState(view, "Publication while another screen is shown");
            Check(callbacks == 3 && view.CurrentReport.Scenarios.Single(s => s.DiscountPercent == 30).CouponPrice == 85030m,
                "detaching the view disposed its live market subscription");
            content.Children.Clear(); content.Children.Add(view); Wait(() => view.IsLoaded && !panel.IsBusy);
            ManualState(view, "Returning to the embedded settlement screen");
            Check(callbacks == 3 && Object.ReferenceEquals(panel.Snapshot, hiddenData)
                && view.CurrentReport.Scenarios.Single(s => s.DiscountPercent == 20).NetAmount == selectedNet,
                "returning to the view reread the cache or lost the selected settlement");

            // Let the first observation begin indexing a large snapshot, then
            // publish its successor before that background build can own the view.
            var large = Data("large", 90m, 40000); var latest = Data("latest", 100m, 2);
            var rapid = Task.Run(delegate { publish.Invoke(client, new object[] { large }); publish.Invoke(client, new object[] { latest }); });
            Wait(() => rapid.IsCompleted && Object.ReferenceEquals(panel.Snapshot, latest));
            for (int i = 0; i < 30; i++) { Pump(); Thread.Sleep(5); }
            Check(Object.ReferenceEquals(panel.Snapshot, latest) && Text(panel.ReferencePanel).Contains("100 G"), "older async index must not replace a newer publication");
            int before = callbacks;
            panel.Configure(() => first, null, null);
            Wait(() => !panel.IsBusy && Object.ReferenceEquals(panel.Snapshot, first));
            before = callbacks;
            var ignored = Publish(client, Data("unsubscribed", 200m, 2)); Wait(() => ignored.IsCompleted);
            Check(callbacks == before && Object.ReferenceEquals(panel.Snapshot, first), "delegate reconfiguration must unsubscribe from the shared client");
            panel.Configure(client); Wait(() => !panel.IsBusy && Object.ReferenceEquals(panel.Snapshot, client.CachedData));
            before = callbacks;
            var afterClose = Data("after-close", 300m, 2); window.Close(); window = null;
            var closed = Publish(client, afterClose); Wait(() => closed.IsCompleted);
            Check(callbacks == before && !Object.ReferenceEquals(panel.Snapshot, afterClose) && handler.Calls == 0, "closing must unsubscribe without late UI updates or HTTP");
            Console.WriteLine("PASS embedded view shared cache restoration; background publication updates references and automatic coupon prices on UI thread while preserving manual/free/unknown costs; identical-reference suppression; navigating away and back retains inputs, selection and subscription without reloading; newest publication wins; reconfiguration and host-close view disposal unsubscribe; zero HTTP.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (window != null) window.Close(); else if (view != null) view.Dispose(); }
    }
}
