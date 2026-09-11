using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MabinogiBarter;

static class MaterialSortingRunner
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly StringComparer Korean = StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), false);
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static int assertions;
    static T Field<T>(MainWindow main, string name) { return (T)typeof(MainWindow).GetField(name, Private).GetValue(main); }
    static void Set(MainWindow main, string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(main, value); }
    static object Call(MainWindow main, string name, params object[] args) { return typeof(MainWindow).GetMethod(name, Private).Invoke(main, args); }
    static void Require(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Equal(IEnumerable<string> actual, IEnumerable<string> expected, string message)
    {
        string[] a = actual.ToArray(), e = expected.ToArray();
        Require(a.SequenceEqual(e), message + "\r\nExpected: " + String.Join(" | ", e) + "\r\nActual: " + String.Join(" | ", a));
    }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T) yield return (T)root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Children<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    static void Pump(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    static void Expand(Window window)
    {
        for (int i = 0; i < 2; i++) { Pump(window); foreach (var expander in Children<Expander>(window)) expander.IsExpanded = true; }
        Pump(window);
    }
    static string CheckKey(CheckBox check)
    {
        string tag = Convert.ToString(check.Tag);
        if (tag.StartsWith("purchase:") || tag.StartsWith("craft:") || tag.StartsWith("acquire:")) return tag;
        string name = AutomationProperties.GetName(check);
        foreach (string prefix in new[] { "pip-ready:", "procurement-ready:" }) if (name.StartsWith(prefix)) return name.Substring(prefix.Length);
        return "";
    }
    static CheckBox Check(Window window, string key) { return Children<CheckBox>(window).Single(c => CheckKey(c) == key); }
    static void Toggle(CheckBox check, bool value) { check.IsChecked = value; check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    static string[] DisplayedKeys(Window window)
    {
        return Children<CheckBox>(window).Where(c => c.IsVisible && CheckKey(c) != "").Select(CheckKey).ToArray();
    }
    static List<ProcurementStep> Steps(MainWindow main) { return ProcurementReadiness.GetSteps(Field<ProcurementPlan>(main, "procurementPlan"), Field<ProgressState>(main, "state")); }
    static int Stage(ProcurementStep step) { return step.IsFinal ? 2 : step.Kind == "craft" ? 1 : 0; }
    static List<ProcurementStep> Expected(IEnumerable<ProcurementStep> steps)
    {
        return steps.OrderBy(Stage).ThenBy(s => Stage(s) == 1 ? s.Level : 0)
            .ThenBy(s => ItemCategories.GetOrder(s.Name)).ThenBy(s => s.Name, Korean).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
    }
    static void MainTab(MainWindow main, int tab)
    {
        main.ShowSummary(); Set(main, "summaryTab", tab); Call(main, "RenderSummary"); Expand(main);
    }
    static void PipTab(PipChecklistWindow pip, int tab)
    {
        (tab == 1 ? pip.PurchaseTabButton : pip.PreparationTabButton).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Expand(pip); Require(pip.SelectedTab == tab, "PIP tab action failed");
    }
    static void AssertLists(MainWindow main, PipChecklistWindow pip, int tab, string query, bool remaining)
    {
        Expand(main); Expand(pip);
        var rows = Expected(Steps(main).Where(s => tab == 1 ? s.Kind == "purchase" : s.Kind != "purchase"));
        if (remaining) rows = rows.Where(s => s.CompletionFraction < 1m).ToList();
        Equal(DisplayedKeys(pip), rows.Select(s => s.Key), "PIP category and preparation-stage order");
        Equal(DisplayedKeys(main), rows.Where(s => String.IsNullOrEmpty(query) || s.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).Select(s => s.Key), "Main category and preparation-stage order");
    }
    static void Capture(Window window, string path)
    {
        Pump(window); var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream);
    }
    static void GoldenChecks()
    {
        var samples = new[] {
            new[] { "방직·천·가죽", "가는 실뭉치", "거미줄", "굵은 실뭉치", "양털" },
            new[] { "목공·장작", "고급 나무장작", "나무장작", "중급 나무장작", "최고급 나무장작" },
            new[] { "허브", "골드 허브", "마나 허브", "베이스 허브", "블러디 허브", "선라이트 허브" },
            new[] { "실리엔·매직 크래프트", "실리엔", "실리엔 결정" },
            new[] { "힐웬 공학", "힐웬", "힐웬 광석 조각" }
        };
        foreach (var sample in samples)
        {
            foreach (string name in sample.Skip(1)) Require(ItemCategories.GetGroup(name) == sample[0], "Known material classification: " + name);
            Equal(ItemCategories.OrderByCategory(sample.Skip(1).Reverse(), s => s), sample.Skip(1), "Golden Korean-name order within " + sample[0]);
        }
        Require(ItemCategories.GetGroup("분류 없는 가상 재료") == "기타 재료", "Unknown materials need the stable fallback family");
        var culture = Thread.CurrentThread.CurrentCulture;
        var golden = samples.SelectMany(s => s.Skip(1)).Reverse().ToArray();
        string[] korean = ItemCategories.OrderByCategory(golden, s => s).ToArray();
        try { Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); Equal(ItemCategories.OrderByCategory(golden, s => s), korean, "Category sorting must not depend on the Windows display culture"); }
        finally { Thread.CurrentThread.CurrentCulture = culture; }
        var finals = new[] {
            new ProcurementStep { Name = "양털", Key = "craft:양털", Kind = "craft", IsFinal = true, Level = 0 },
            new ProcurementStep { Name = "거미줄", Key = "craft:거미줄", Kind = "craft", IsFinal = true, Level = 5 },
            new ProcurementStep { Name = "가는 실뭉치", Key = "craft:가는 실뭉치", Kind = "craft", IsFinal = true, Level = 9 }
        };
        Equal(ItemCategories.OrderSteps(finals).Select(s => s.Name), new[] { "가는 실뭉치", "거미줄", "양털" }, "Terminal exchange items stay together regardless of recipe depth");
        var mixed = finals.Concat(new[] {
            new ProcurementStep { Name = "최고급 나무장작", Key = "craft:최고급 나무장작", Kind = "craft", Level = 1 },
            new ProcurementStep { Name = "가는 실뭉치", Key = "middle-thread", Kind = "craft", Level = 2 },
            new ProcurementStep { Name = "선라이트 허브", Key = "purchase:선라이트 허브", Kind = "purchase", Level = 0 }
        }).ToArray();
        Equal(ItemCategories.OrderSteps(mixed).Select(s => s.Key), new[] { "purchase:선라이트 허브", "craft:최고급 나무장작", "middle-thread", "craft:가는 실뭉치", "craft:거미줄", "craft:양털" }, "Preparation stage takes priority over material family");
    }
    static void Populate(Catalog catalog, ProgressState state, ProcurementPlanner planner)
    {
        foreach (var trade in catalog.Trades) { state.Targets[trade.Id] = 1; Calculator.SetSelected(state, trade, true); }
        for (int depth = 0; depth < 20; depth++)
        {
            bool changed = false;
            foreach (var node in planner.Build(state).Nodes)
            {
                var recipes = planner.GetRecipes(node.Name);
                if (!node.IsCrafting && recipes.Count > 0) { planner.SetChoice(state, node.Name, recipes[0].Id); changed = true; }
            }
            if (!changed) break;
        }
        foreach (string name in new[] { "거미줄", "양털", "실리엔 결정", "힐웬 광석 조각", "나무장작", "골드 허브" })
            if (planner.CanAcquire(name)) planner.SetChoice(state, name, "acquire");
    }
    static void CoreChecks(Catalog catalog, ProgressState state, ProcurementPlanner planner)
    {
        string before = Json.Serialize(state); var plan = planner.Build(state); var steps = ProcurementReadiness.GetSteps(plan, state);
        Require(steps.Count > 65 && steps.Any(s => s.Kind == "acquire") && steps.Any(s => s.Kind == "craft") && steps.Any(s => s.Kind == "purchase"), "Full mixed plan fixture is incomplete");
        Equal(steps.Select(s => s.Key), Expected(steps).Select(s => s.Key), "Readiness provides the shared category order");
        var indexes = steps.Select((step, index) => new { step.Key, Index = index }).ToDictionary(pair => pair.Key, pair => pair.Index);
        foreach (var step in steps) foreach (string dependency in step.DependencyKeys)
            Require(indexes[dependency] < indexes[step.Key], "Material grouping moved an ingredient after its parent: " + step.Name);
        var seed = steps.FirstOrDefault(s => s.IsSeed && s.Kind == "purchase");
        Require(seed != null && steps.Any(s => s.Name == seed.Name && s.Kind == "craft" && s.Key != seed.Key), "Synthesis seed and produced ingot remain separate steps");
        Require(steps.Select(s => s.Key).Distinct().Count() == steps.Count, "Sorting must not duplicate or merge preparation steps");
        var status = ProcurementReadiness.GetStatus(steps); ProcurementReadiness.GetStatus(ItemCategories.OrderSteps(steps.AsEnumerable().Reverse()));
        Require(status.Total == steps.Count && Json.Serialize(state) == before, "Sorting and status reads changed saved preparation data");
        foreach (var family in new[] { "방직·천·가죽", "목공·장작", "허브", "실리엔·매직 크래프트", "힐웬 공학" })
            Require(steps.Any(s => ItemCategories.GetGroup(s.Name) == family), "Full fixture misses family " + family);
    }
    static string[] ExportNames(string text, string header)
    {
        var lines = text.Replace("\r", "").Split('\n'); int start = Array.IndexOf(lines, header);
        Require(start >= 0, "Export omitted section " + header);
        return lines.Skip(start + 2).TakeWhile(line => !String.IsNullOrWhiteSpace(line)).Select(line => line.Split('\t')[0]).ToArray();
    }
    static void ExportChecks(MainWindow main, ProcurementPlanner planner)
    {
        string text = (string)Call(main, "ProcurementExport"); var steps = Expected(Steps(main));
        Equal(ExportNames(text, "경매장 구매 목록"), steps.Where(s => s.Kind == "purchase").Select(s => s.Name), "Export purchase order");
        Equal(ExportNames(text, "직접 제작 목록"), steps.Where(s => s.Kind == "craft").Select(s => s.Name), "Export craft order");
        Equal(ExportNames(text, "직접 확보 목록"), steps.Where(s => s.Kind == "acquire" && !planner.IsNpcPurchase(s.Name)).Select(s => s.Name), "Export acquisition order");
        Equal(ExportNames(text, "NPC 구매 목록"), steps.Where(s => s.Kind == "acquire" && planner.IsNpcPurchase(s.Name)).Select(s => s.Name), "Export NPC order");
    }
    static void Run(Application app, Catalog catalog, string root)
    {
        GoldenChecks();
        var acquisition = new AcquisitionCatalog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "item-acquisition.json"));
        var tradeNames = new HashSet<string>(catalog.Trades.Select(t => t.Name), StringComparer.Ordinal);
        var knownMaterials = acquisition.Items.Where(i => !tradeNames.Contains(i.name)).ToList();
        Require(knownMaterials.Count >= 125, "Acquisition classification coverage fixture is incomplete");
        foreach (var item in knownMaterials) Require(ItemCategories.IsKnown(item.name), "Acquisition material has no explicit family: " + item.name);
        foreach (string name in new ProcurementPlanner(catalog).GetAllQuoteNames()) Require(ItemCategories.IsKnown(name), "A reachable recipe alternative has no explicit family: " + name);
        string profile = Path.Combine(root, "profile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(profile);
        string progress = Path.Combine(profile, "sorting-progress.json"); Func<int> requests;
        var probe = AuctionVerification.CreateUiProbe(profile, out requests); var main = new MainWindow(catalog, new StateStore(progress), true, probe);
        app.MainWindow = main; main.Left = -18000; main.Top = -18000; main.ShowActivated = false; main.ShowInTaskbar = false;
        main.Show(); Pump(main);
        try
        {
            var state = Field<ProgressState>(main, "state"); var planner = Field<ProcurementPlanner>(main, "procurementPlanner");
            Populate(catalog, state, planner); CoreChecks(catalog, state, planner); Call(main, "Persist"); main.ShowSummary(); Pump(main);
            string initialSave = File.ReadAllText(progress), initialState = Json.Serialize(state);
            var expectedRoots = ItemCategories.OrderByCategory(Field<ProcurementPlan>(main, "procurementPlan").Roots, n => n.Name).Select(n => n.Name);
            Equal(Children<Button>(Field<StackPanel>(main, "summaryRows")).Select(b => AutomationProperties.GetName(b)).Where(n => n.EndsWith(" 재료 카드 열기")).Select(n => n.Substring(0, n.Length - " 재료 카드 열기".Length)), expectedRoots, "Preparation-method card order");
            Call(main, "ShowPipChecklist"); var pip = Field<PipChecklistWindow>(main, "pipChecklist"); Require(pip != null, "PIP did not open"); pip.Left = -17900; pip.Top = -17900;
            Require(File.ReadAllText(progress) == initialSave && Json.Serialize(state) == initialState, "Rendering sorted method cards or opening PIP changed saved progress");
            foreach (int tab in new[] { 1, 2 })
            {
                MainTab(main, tab); PipTab(pip, tab); AssertLists(main, pip, tab, "", false);
                string[] initialOrder = DisplayedKeys(main); string key = initialOrder.First();
                var mainCheck = Check(main, key); var pipCheck = Check(pip, key);
                Toggle(pipCheck, true); Pump(main); Pump(pip);
                Require(Check(main, key).IsChecked == true && Steps(main).Single(s => s.Key == key).IsReady, "PIP readiness did not synchronize to main");
                Require(new StateStore(progress).Load(catalog).ProcurementReady.ContainsKey(key), "PIP readiness was not saved");
                Equal(DisplayedKeys(main), initialOrder, "PIP check changed main row order"); AssertLists(main, pip, tab, "", false);
                Require(Object.ReferenceEquals(mainCheck, Check(main, key)) && Object.ReferenceEquals(pipCheck, Check(pip, key)), "A readiness check rebuilt sorted controls");
                Toggle(mainCheck, false); Pump(pip); Require(Check(pip, key).IsChecked == false, "Main uncheck did not synchronize to PIP");
                Toggle(mainCheck, true); Pump(pip); Require(Check(pip, key).IsChecked == true, "Main check did not synchronize to PIP");
                Toggle(pip.RemainingOnlyControl, true); Pump(main); Pump(pip); AssertLists(main, pip, tab, "", true);
                Require(!DisplayedKeys(pip).Contains(key) && Field<bool>(main, "checklistRemainingOnly"), "Remaining filter did not hide the completed item in both windows");
                Toggle(Field<CheckBox>(main, "checklistRemainingControl"), false); Pump(pip); AssertLists(main, pip, tab, "", false);
                Require(!pip.RemainingOnly, "Main remaining filter did not synchronize to PIP");
                string beforeRead = File.ReadAllText(progress); string stateBeforeRead = Json.Serialize(state);
                string query = Steps(main).First(s => tab == 1 ? s.Kind == "purchase" : s.Kind != "purchase").Name.Substring(0, 1);
                Field<TextBox>(main, "summarySearch").Text = query; Pump(main); AssertLists(main, pip, tab, query, false);
                Require(File.ReadAllText(progress) == beforeRead && Json.Serialize(state) == stateBeforeRead, "Searching or filtering changed saved progress");
                Field<TextBox>(main, "summarySearch").Text = ""; Pump(main); AssertLists(main, pip, tab, "", false);
                Toggle(Check(main, key), false); Pump(pip); Equal(DisplayedKeys(main), initialOrder, "Filter restoration changed the family order");
                Capture(main, Path.Combine(root, tab == 1 ? "sorting-main-purchases.png" : "sorting-main-crafting.png"));
                Capture(pip, Path.Combine(root, tab == 1 ? "sorting-pip-purchases.png" : "sorting-pip-crafting.png"));
            }
            string saveBeforeExport = File.ReadAllText(progress), stateBeforeExport = Json.Serialize(state);
            ExportChecks(main, planner); CoreChecks(catalog, state, planner);
            Require(File.ReadAllText(progress) == saveBeforeExport && Json.Serialize(state) == stateBeforeExport, "Export or sorting mutated progress");
            Require(Json.Serialize(state.ProcurementChoices) == Json.Serialize(Json.Deserialize<ProgressState>(initialState).ProcurementChoices), "Checklist ordering changed procurement choices");
            Require(Json.Serialize(state.Targets) == Json.Serialize(Json.Deserialize<ProgressState>(initialSave).Targets), "Checklist ordering changed selected trade quantities");
            Require(requests() == 0, "Sorting or UI operations requested auction data");
        }
        finally { main.Close(); }
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
        string root = null;
        try
        {
            if (args.Length != 1) throw new Exception("Expected one isolated QA output directory");
            root = ValidateOutputRoot(args[0]);
            Directory.CreateDirectory(root); AppMotion.ReducedMotion = true;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AppTheme.Initialize(Path.Combine(root, "appearance.txt"));
            var catalog = Catalog.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "barter-data.json"));
            Run(app, catalog, root); app.Shutdown();
            string report = "PASS material sorting: " + assertions + " assertions; golden family and Korean name order; terminal and intermediate stage boundaries; all dependencies precede parents; actual main/PIP purchase and crafting lists match; readiness synchronizes both ways without reordering; search and remaining filters preserve relative order; method cards and exports agree; independent synthesis seed; no saved plan mutation; zero auction HTTP.";
            File.WriteAllText(Path.Combine(root, "material-sorting-verification.txt"), report); Console.WriteLine(report); return 0;
        }
        catch (Exception e)
        {
            if (root != null) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "material-sorting-error.txt"), e.ToString()); }
            Console.Error.WriteLine(e); return 1;
        }
    }
}
