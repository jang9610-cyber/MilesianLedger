using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MabinogiBarter;

// All profiles are synthetic. Configured refresh checks use the injected fake transport only.
static class AuctionProxyUiRunner
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static readonly List<string> Reports = new List<string>();
    static int assertions;
    static T Field<T>(MainWindow main, string name) { return (T)typeof(MainWindow).GetField(name, Private).GetValue(main); }
    static object Call(MainWindow main, string name, params object[] args) { return typeof(MainWindow).GetMethod(name, Private).Invoke(main, args); }
    static void Require(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T) yield return (T)root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Children<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    static void Pump(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void Report(string report) { Reports.Add(report); Console.WriteLine(report); }
    static void Capture(Window window, string path)
    {
        Pump(window);
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
    }
    static string NewProfile(string root, string label)
    {
        string profile = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); return profile;
    }
    static MainWindow OpenMain(Application app, Catalog catalog, string profile, AuctionService service)
    {
        var main = new MainWindow(catalog, new StateStore(Path.Combine(profile, "progress.json")), true, service);
        main.Left = -18000; main.Top = -18000; main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.ShowActivated = false; main.ShowInTaskbar = false;
        app.MainWindow = main; main.Show(); Pump(main); return main;
    }
    static void AssertNoCredentialUi(Application app)
    {
        foreach (Window window in app.Windows)
        {
            Require(!Children<PasswordBox>(window).Any(), "A desktop API key input is still present.");
            Require(!Children<Button>(window).Any(b => AutomationProperties.GetName(b) == "경매장 API 설정 메뉴"
                || Convert.ToString(b.Content) == "경매장 API 설정"), "An API credential settings menu is still present.");
        }
    }
    static void SelectExamplePlan(MainWindow main, Catalog catalog)
    {
        var state = Field<ProgressState>(main, "state");
        foreach (var trade in catalog.Trades)
        {
            Calculator.SetSelected(state, trade, trade.Tier >= 3);
            state.Targets[trade.Id] = trade.Limit;
        }
        Calculator.SetSelected(state, catalog.Trades.Single(t => t.Id == "C6"), true);
        Call(main, "Persist"); main.ShowStationHub(); Pump(main);
    }
    static void CheckUnconfigured(Application app, Catalog catalog, string root)
    {
        string profile = NewProfile(root, "unconfigured");
        // This deliberately invalid legacy file is newly created test data, never a real credential.
        string legacyPath = Path.Combine(profile, "auction-key.dat");
        byte[] sentinel = Encoding.UTF8.GetBytes("offline-ui-legacy-file-sentinel-not-a-key");
        File.WriteAllBytes(legacyPath, sentinel);
        string cachePath = Path.Combine(profile, "auction-cache.json");
        var cache = new AuctionCache();
        cache.Quotes["매듭끈"] = new AuctionQuote {
            Material = "매듭끈", SearchName = "매듭끈", UnitPrice = 12345m,
            AvailableQuantity = 100, ListingCount = 2, Pages = 1, Complete = true,
            Status = "ok", Message = "", PriceUtc = DateTime.UtcNow.AddHours(-2), AttemptUtc = DateTime.UtcNow.AddHours(-2)
        };
        File.WriteAllText(cachePath, Json.Serialize(cache), new UTF8Encoding(false));
        byte[] cacheBefore = File.ReadAllBytes(cachePath);
        var service = new AuctionService(profile, new AuctionSettings());
        Require(!service.IsConfigured && !String.IsNullOrWhiteSpace(service.ConfigurationMessage), "Missing proxy configuration must give a waiting message.");
        Require(service.GetQuote("매듭끈") != null && service.GetQuote("매듭끈").UnitPrice == 12345m, "A cached quote must remain usable before proxy deployment.");
        var main = OpenMain(app, catalog, profile, service);
        try
        {
            SelectExamplePlan(main, catalog); AssertNoCredentialUi(app);
            Require(Field<TextBlock>(main, "auctionStatus").Text == service.ConfigurationMessage, "Startup must explain the missing proxy address without a key prompt.");
            string progressPath = Path.Combine(profile, "progress.json");
            string saved = File.ReadAllText(progressPath), state = Json.Serialize(Field<ProgressState>(main, "state"));
            bool shellBlocked = false;
            var shell = Field<Grid>(main, "shell");
            shell.IsEnabledChanged += delegate { if (!shell.IsEnabled) shellBlocked = true; };
            int windowCount = app.Windows.Count;
            foreach (string buttonField in new[] { "auctionAllRefreshButton", "auctionRefreshButton" })
            {
                var button = Field<Button>(main, buttonField);
                Require(button.IsEnabled, "Refresh action should remain available for its explanatory message.");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Call(main, "PumpAuctionTestTask"); Pump(main);
                Require(Field<Task>(main, "auctionRefreshTask").IsCompleted && !Field<bool>(main, "auctionRefreshing") && !service.IsRefreshing,
                    "An unconfigured refresh left the UI in a pending operation.");
                Require(Field<TextBlock>(main, "auctionStatus").Text == service.ConfigurationMessage, "Both refresh buttons must explain the pending proxy connection.");
                Require(Field<TextBlock>(main, "footerMessage").Text.Contains("연결을 준비 중"), "Missing proxy configuration must provide an actionable footer state.");
                Require(Field<object>(main, "auctionLoading") == null && !shellBlocked && shell.IsEnabled && shell.Effect == null,
                    "An unconfigured refresh opened loading or blocked the planning UI.");
                Require(app.Windows.Count == windowCount, "An unconfigured refresh opened an unexpected window.");
                Require(Json.Serialize(Field<ProgressState>(main, "state")) == state && File.ReadAllText(progressPath) == saved,
                    "An unconfigured refresh changed the user's synthetic plan.");
                Require(File.ReadAllBytes(legacyPath).SequenceEqual(sentinel), "The legacy credential sentinel was modified.");
                Require(File.ReadAllBytes(cachePath).SequenceEqual(cacheBefore) && service.GetQuote("매듭끈").UnitPrice == 12345m,
                    "An unconfigured refresh replaced a previously cached quote.");
                AssertNoCredentialUi(app);
            }
            Capture(main, Path.Combine(root, "proxy-unconfigured-plan.png"));
            main.ShowSummary(); Pump(main);
            Require(Children<Button>(main).Any(b => AutomationProperties.GetName(b).EndsWith(" 재료 카드 열기")),
                "Material preparation cards must still work without a deployed price server.");
            AssertNoCredentialUi(app); Capture(main, Path.Combine(root, "proxy-unconfigured-materials.png"));
            main.ShowStation("오아시스"); Pump(main);
            Require(Field<Grid>(main, "shell").IsEnabled && Children<TextBlock>(main).Any(t => t.Text == "고운 모래"),
                "Station item details must still work without proxy configuration.");
        }
        finally { main.Close(); }
        Require(File.ReadAllBytes(legacyPath).SequenceEqual(sentinel), "Closing the client touched the legacy credential sentinel.");
        Require(File.ReadAllBytes(cachePath).SequenceEqual(cacheBefore), "Closing the client replaced cached data without refreshing.");
        Report("PASS unconfigured proxy UI: " + assertions + " assertions; both refresh buttons show connection-pending status without loading, credential UI or blocked planning; existing cached quotes, progress and a synthetic legacy key file remain unchanged.");
    }
    static void CheckConfiguredFake(Application app, Catalog catalog, string root)
    {
        string profile = NewProfile(root, "configured-fake"); Func<int> requests;
        var service = AuctionVerification.CreateUiProbe(profile, out requests);
        Require(service.IsConfigured && requests() == 0, "An injected fake service must be configured without startup requests.");
        var main = OpenMain(app, catalog, profile, service);
        try
        {
            AssertNoCredentialUi(app);
            Report(main.RunSourcesUiChecks(requests, profile));
            Report(main.RunAuctionUiChecks(requests, profile));
            Report(main.RunNpcProcurementUiChecks(requests, profile));
            int beforeNavigation = requests();
            SelectExamplePlan(main, catalog); AssertNoCredentialUi(app);
            main.Width = 1400; main.Height = 940; Pump(main);
            Capture(main, Path.Combine(root, "proxy-main-plan.png"));
            main.ShowSummary(); Pump(main); Capture(main, Path.Combine(root, "proxy-main-materials.png"));
            Require(requests() == beforeNavigation, "Ordinary navigation or screenshot capture caused implicit auction requests.");
            Require(!File.Exists(Path.Combine(profile, "auction-key.dat")), "Configured fake UI wrote a desktop provider key file.");
            Require(Field<object>(main, "auctionLoading") == null && Field<Grid>(main, "shell").IsEnabled, "Configured fake checks left the loading overlay or blocked shell behind.");
        }
        finally { main.Close(); }
        Report("PASS configured proxy UI: existing auction-scope and NPC workflows run with fake transport only; no desktop credential file or key input; normal navigation sends no requests.");
    }
    // Runners are copied beside an isolated application under artifacts/verification/<guid>/app.
    // Reject arbitrary output paths before creating profiles or writing reports.
    static string ValidateOutputRoot(string argument)
    {
        var app = new DirectoryInfo(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));
        var run = app.Parent;
        Guid runId;
        if (!String.Equals(app.Name, "app", StringComparison.OrdinalIgnoreCase) || run == null ||
            !Guid.TryParse(run.Name, out runId) || run.Parent == null ||
            !String.Equals(run.Parent.Name, "verification", StringComparison.OrdinalIgnoreCase) ||
            run.Parent.Parent == null || !String.Equals(run.Parent.Parent.Name, "artifacts", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run this test from an isolated artifacts/verification/<guid>/app directory.");
        string root = Path.GetFullPath(argument).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string runPrefix = run.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string appRoot = app.FullName.TrimEnd(Path.DirectorySeparatorChar);
        if (!root.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase) ||
            root.Equals(appRoot, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("QA output must be inside this isolated run, outside its app directory.");
        return root;
    }
    [STAThread] static int Main(string[] args)
    {
        string root = null; Application app = null;
        try
        {
            if (args.Length != 1) throw new ArgumentException("Expected one isolated QA output directory.");
            root = ValidateOutputRoot(args[0]); Directory.CreateDirectory(root);
            AppMotion.ReducedMotion = true;
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AppTheme.Initialize(Path.Combine(root, "appearance.txt"));
            var catalog = Catalog.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "barter-data.json"));
            CheckUnconfigured(app, catalog, root); CheckConfiguredFake(app, catalog, root);
            app.Shutdown();
            File.WriteAllText(Path.Combine(root, "auction-proxy-ui-verification.txt"), String.Join(Environment.NewLine, Reports), new UTF8Encoding(false));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (root != null) File.WriteAllText(Path.Combine(root, "auction-proxy-ui-error.txt"), error.ToString(), new UTF8Encoding(false));
            if (app != null) app.Shutdown();
            return 1;
        }
    }
}
