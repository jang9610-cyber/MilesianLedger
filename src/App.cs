using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                string root = AppDomain.CurrentDomain.BaseDirectory;
                var catalog = Catalog.Load(Path.Combine(root, "data", "barter-data.json"));
                bool smoke = args.Length > 0 && args[0] == "--self-test";
                string testDir = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root, "verification");
                var store = new StateStore(smoke ? Path.Combine(testDir, "smoke-progress.json") : Path.Combine(root, "data", "progress.json"));
                AppTheme.Initialize(Path.Combine(Path.GetDirectoryName(store.FilePath), "appearance.txt"));
                if (smoke)
                {
                    AppMotion.ReducedMotion = true; // Deterministic screenshots and immediate layout assertions.
                    Directory.CreateDirectory(testDir);
                    string verification = Verification.Run(catalog) + "\r\n" + ProcurementVerification.Run(catalog) + "\r\n" + ProcurementReadinessVerification.Run(catalog) + "\r\n" + AuctionVerification.Run(testDir);
                    verification += "\r\n" + ProcurementSharedPlanningVerification.Run(catalog);
                    verification += "\r\n" + NpcProcurementVerification.Run(catalog);
                    verification += "\r\n" + ProcurementCostingVerification.Run(catalog);
                    verification += "\r\n" + TradePlanningVerification.Run(catalog, TradePlanningData.Load(Path.Combine(root, "data", "trade-planning.json"), catalog));
                    Func<int> requestCount;
                    var probe = AuctionVerification.CreateUiProbe(testDir, out requestCount);
                    var w = new MainWindow(catalog, store, true, probe);
                    app.MainWindow = w;
                    w.Left = -18000;
                    w.Top = -18000;
                    w.ShowInTaskbar = false;
                    w.Show();
                    w.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {}));
                    w.SavePreview(Path.Combine(testDir, "prototype-main.png"));
                    string workflow = w.RunWorkflowChecks() + "\r\n" + w.RunIconChecks();
                    w.SaveScenarioPreviews(testDir);
                    w.ShowStation("칼리다 호수");
                    w.SavePreview(Path.Combine(testDir, "prototype-calida.png"));
                    w.ShowSummary();
                    w.SavePreview(Path.Combine(testDir, "prototype-summary-empty.png"));
                    workflow += "\r\n" + w.RunAuctionUiChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunProcurementUiChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunProcurementInteractionChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunProcurementChecklistUiChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunProcurementCompletionUiChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunProcurementSharedUiChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunProcurementResetUiChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunProcurementWindowLifecycleChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunNpcProcurementUiChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunStationUiChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunAcquisitionChecks(requestCount, testDir);
                    workflow += "\r\n" + w.RunSourcesUiChecks(requestCount, testDir);
                    File.WriteAllText(Path.Combine(testDir, "verification.txt"), verification + "\r\n" + workflow, Encoding.UTF8);
                    w.Close();
                    return 0;
                }
                var window = StartupSequence.PrepareMainWindow(app, new StartupReminderWindow(), () => new MainWindow(catalog, store, false));
                return window == null ? 0 : app.Run(window);
            }
            catch (Exception ex)
            {
                if (args.Length > 0)
                {
                    string output = args.Length > 1 ? args[1] : AppDomain.CurrentDomain.BaseDirectory;
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString());
                }
                else MessageBox.Show("프로그램을 열지 못했습니다.\n\n" + ex.Message, "밀레시안 장부", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    public sealed class StateStore
    {
        public string FilePath { get; private set; }
        public string Notice { get; private set; }
        public bool ProcurementPresetsMigrated { get; private set; }
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public StateStore(string path) { FilePath = path; Notice = ""; }
        public ProgressState Load(Catalog catalog)
        {
            var state = new ProgressState();
            if (File.Exists(FilePath))
            {
                try { state = json.Deserialize<ProgressState>(File.ReadAllText(FilePath, Encoding.UTF8)); if (state == null) throw new InvalidDataException(); }
                catch
                {
                    Notice = "저장 파일을 읽지 못해 새 상태로 열었습니다. 기존 파일은 보관했습니다.";
                    File.Copy(FilePath, FilePath + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                    state = new ProgressState();
                }
            }
            ProcurementPresetsMigrated = state.ProcurementMethodPresets == null;
            state.Normalize(catalog);
            return state;
        }
        public void Save(ProgressState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string pending = FilePath + ".tmp";
            File.WriteAllText(pending, json.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(pending, FilePath, FilePath + ".bak");
            else File.Move(pending, FilePath);
        }
        public static ProgressState Copy(ProgressState state)
        {
            var serializer = new JavaScriptSerializer();
            return serializer.Deserialize<ProgressState>(serializer.Serialize(state));
        }
    }

    public sealed partial class MainWindow : Window
    {
        readonly Catalog catalog;
        readonly StateStore store;
        ProgressState state;
        ProgressState undo;
        string station = "오아시스";
        TradeItem selected;
        readonly Grid shell = new Grid();
        readonly StackPanel nav = new StackPanel();
        readonly StackPanel tradeList = new StackPanel();
        readonly Grid detail = new Grid();
        readonly Grid content = new Grid();
        readonly TextBlock title = new TextBlock();
        readonly TextBlock subtitle = new TextBlock();
        readonly TextBlock plannedStat = new TextBlock();
        readonly TextBlock readyStat = new TextBlock();
        readonly TextBlock percentStat = new TextBlock();
        readonly TextBlock saveStatus = new TextBlock();
        readonly TextBlock footerMessage = new TextBlock();
        readonly StackPanel footerActions = new StackPanel();
        readonly ScrollViewer detailScroll = new ScrollViewer();
        ScrollViewer tradeScroll;
        readonly Dictionary<string, CheckBox> tradeControls = new Dictionary<string, CheckBox>();
        readonly Dictionary<string, Action> quantityActions = new Dictionary<string, Action>();
        TradeItem detailHeaderTrade;
        Border detailHeaderBox;
        Action updateDetailHeader;
        readonly DispatcherTimer delayedRefresh = new DispatcherTimer();
        readonly string[] stations = { "오아시스", "카루 숲", "페라 화산", "칼리다 호수" };
        bool summaryView;
        readonly ProcurementPlanner procurementPlanner;
        StackPanel summaryRows = new StackPanel();
        TextBox summarySearch;
        TextBlock summaryCount;
        string summaryQuery = "";
        static readonly Brush Ink = B("#202D35");
        static readonly Brush Muted = B("#728087");
        static readonly Brush BackgroundColor = B("#F4F6F5");
        static readonly Brush Line = B("#E2E8E5");
        static readonly Brush Green = B("#226C54");

        public MainWindow(Catalog data, StateStore persistence, bool fresh, AuctionService injectedService = null)
        {
            AppMotion.Initialize();
            catalog = data; store = persistence;
            procurementPlanner = new ProcurementPlanner(catalog);
            tradePlanningData = TradePlanningData.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "trade-planning.json"), catalog);
            tradePlanning = new TradePlanningCalculator(tradePlanningData);
            procurementCosting = new ProcurementCosting(catalog, procurementPlanner);
            // Shared confirmation has a separate real-dialog regression.
            procurementSharedPromptsEnabled = !fresh;
            state = fresh ? new ProgressState() : store.Load(catalog);
            state.Normalize(catalog);
            state.TradeSettings.Normalize(tradePlanningData);
            InitializeAuction(injectedService);
            InitializePip();
            selected = catalog.Trades.First(t => t.Station == station);
            Title = "밀레시안 장부 · 물물교환";
            Icon = LoadApplicationIcon();
            Width = 1400; Height = 940; MinWidth = 1100; MinHeight = 740;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = BackgroundColor;
            FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
            Foreground = Ink;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            BuildShell();
            AppMotion.WindowContent(this);
            delayedRefresh.Interval = TimeSpan.FromMilliseconds(50);
            delayedRefresh.Tick += delegate { if (Mouse.LeftButton == MouseButtonState.Pressed) return; delayedRefresh.Stop(); RefreshProgress(); };
            RenderAll();
            SizeChanged += delegate(object sender, SizeChangedEventArgs e) { if (summaryView && e.WidthChanged && summaryCardColumns != (ActualWidth >= 1280 ? 3 : 2)) RenderSummaryRows(); };
            if (!String.IsNullOrEmpty(store.Notice)) footerMessage.Text = store.Notice;
            if (!fresh && store.ProcurementPresetsMigrated)
            {
                Persist(); footerMessage.Text = "현재 재료 준비 방식을 1번 프리셋에 저장했습니다. 2~5번에는 다른 준비 방식을 지정할 수 있습니다.";
            }
            Closing += delegate { procurementMainClosing = true; delayedRefresh.Stop(); ClosePipChecklist(); if (sourcesDialog != null) sourcesDialog.Close(); if (!fresh) Persist(); };
        }

        static SolidColorBrush B(string hex) { return AppTheme.Brush(hex); }
        internal static BitmapFrame LoadApplicationIcon(int pixels = 256)
        {
            using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("MabinogiBarter.AppIcon.ico"))
            {
                if (stream == null) throw new InvalidDataException("프로그램 아이콘 리소스가 없습니다. 실행 파일을 다시 빌드해 주세요.");
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - pixels)).First(); frame.Freeze(); return frame;
            }
        }
        static TextBlock T(string text, double size, Brush color, bool bold)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = color, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        }
        static Border Box(UIElement child, Brush fill, double radius, Thickness padding)
        {
            return new Border { Child = child, Background = fill, CornerRadius = new CornerRadius(radius), Padding = padding };
        }
        static Button Btn(string text, Action action, bool primary)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 9, 14, 9), Foreground = primary ? AppTheme.OnAccent : Ink, Background = primary ? Green : AppTheme.Surface, BorderBrush = primary ? Green : Line, BorderThickness = new Thickness(1), FontSize = 12, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "ButtonSurface");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter); template.VisualTree = border;
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, B("#B58B35"), "ButtonSurface")); template.Triggers.Add(focus);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.38)); template.Triggers.Add(disabled);
            b.Template = template;
            b.Click += delegate { action(); };
            return b;
        }
        Brush Accent(string region)
        {
            return B(region == "오아시스" ? "#A47817" : region == "카루 숲" ? "#427950" : region == "페라 화산" ? "#79649D" : "#2D7886");
        }
        Brush Tint(string region)
        {
            return B(region == "오아시스" ? "#FBF3DE" : region == "카루 숲" ? "#EAF3E9" : region == "페라 화산" ? "#F0EAF7" : "#E7F3F4");
        }

        void BuildShell()
        {
            shell.Background = BackgroundColor;
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(202) });
            shell.ColumnDefinitions.Add(new ColumnDefinition());
            Content = CreateAuctionLayerHost(shell);
            var side = new Grid { Background = AppTheme.Surface };
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition());
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.Children.Add(side);
            var brand = new StackPanel { Margin = new Thickness(24, 29, 20, 32) };
            var mark = new Image { Source = LoadApplicationIcon(44), Width = 44, Height = 44, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.NearestNeighbor);
            brand.Children.Add(mark);
            var brandTitle = T("밀레시안 장부", 20, Ink, true); brandTitle.Margin = new Thickness(0, 16, 0, 5); brand.Children.Add(brandTitle);
            brand.Children.Add(T("물물교환 준비 노트", 11, Muted, false));
            side.Children.Add(brand);
            nav.Margin = new Thickness(13, 0, 13, 0); Grid.SetRow(nav, 1); side.Children.Add(nav);
            var bottom = new StackPanel { Margin = new Thickness(24, 20, 20, 25) };
            var darkMode = new CheckBox { Content = "다크모드", IsChecked = AppTheme.IsDark, Foreground = Ink, FontSize = 12, Margin = new Thickness(0, 0, 0, 14), Cursor = Cursors.Hand };
            darkMode.Click += delegate {
                try { AppTheme.SetDark(darkMode.IsChecked == true); }
                catch (Exception ex) { darkMode.IsChecked = AppTheme.IsDark; footerMessage.Text = "화면 모드를 저장하지 못했습니다: " + ex.Message; }
            };
            AutomationProperties.SetName(darkMode, "다크모드"); bottom.Children.Add(darkMode);
            var watermark = T("made by 하프_알베도", 11, Muted, false); watermark.Margin = new Thickness(0, 0, 0, 12); bottom.Children.Add(watermark);
            var credits = Btn("출처", ShowSources, false); credits.HorizontalAlignment = HorizontalAlignment.Left; credits.FontSize = 11; credits.Padding = new Thickness(11, 7, 11, 7); credits.Margin = new Thickness(0, 0, 0, 12);
            AutomationProperties.SetName(credits, "출처 모아보기"); bottom.Children.Add(credits);
            var appAssembly = typeof(MainWindow).Assembly;
            var displayVersion = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(appAssembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            bottom.Children.Add(T("v" + (displayVersion != null && !String.IsNullOrWhiteSpace(displayVersion.InformationalVersion)
                ? displayVersion.InformationalVersion : appAssembly.GetName().Version.ToString(3)), 10, Muted, true));
            Grid.SetRow(bottom, 2); side.Children.Add(bottom);
            var workspace = new Grid { Margin = new Thickness(30, 24, 30, 14) };
            workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            workspace.RowDefinitions.Add(new RowDefinition());
            workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(workspace, 1); shell.Children.Add(workspace);
            var header = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel();
            heading.Children.Add(T("BARTER PLANNER", 10, Green, true));
            title.FontSize = 28; title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 7, 0, 6); heading.Children.Add(title);
            subtitle.FontSize = 12; subtitle.Foreground = Muted; subtitle.TextWrapping = TextWrapping.Wrap; subtitle.Margin = new Thickness(0, 0, 16, 0); heading.Children.Add(subtitle); header.Children.Add(heading);
            var headerRight = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            saveStatus.FontSize = 11; saveStatus.Foreground = Green; saveStatus.Text = "●  로컬 자동 저장"; saveStatus.HorizontalAlignment = HorizontalAlignment.Right; saveStatus.Margin = new Thickness(0, 0, 8, 12); headerRight.Children.Add(saveStatus);
            headerRight.Children.Add(BuildAuctionHeader()); Grid.SetColumn(headerRight, 1); header.Children.Add(headerRight); workspace.Children.Add(header);
            var stats = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
            AddStat(stats, 0, "선택한 교역품", plannedStat, "교역품 선택 체크 기준");
            AddStat(stats, 1, "준비 완료", readyStat, "최종 교환 재료를 구비한 품목");
            AddStat(stats, 2, "재료 준비율", percentStat, "구비한 상위 품목도 반영한 준비율");
            Grid.SetRow(stats, 1); workspace.Children.Add(stats);
            Grid.SetRow(content, 2); workspace.Children.Add(content);
            var footer = new Grid { Margin = new Thickness(0, 13, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerMessage.FontSize = 11; footerMessage.Foreground = Muted; footerMessage.VerticalAlignment = VerticalAlignment.Center; footerMessage.TextWrapping = TextWrapping.Wrap;
            footerMessage.Text = "목표 수량과 준비 체크가 이 PC에 자동으로 저장됩니다."; footer.Children.Add(footerMessage);
            footerActions.Orientation = Orientation.Horizontal; Grid.SetColumn(footerActions, 1); footer.Children.Add(footerActions);
            Grid.SetRow(footer, 3); workspace.Children.Add(footer);
        }
        void AddStat(Grid grid, int col, string label, TextBlock value, string caption)
        {
            var panel = new StackPanel(); panel.Children.Add(T(label, 12, Muted, false));
            value.FontSize = 26; value.FontWeight = FontWeights.SemiBold; value.Margin = new Thickness(0, 5, 0, 5); panel.Children.Add(value);
            panel.Children.Add(T(caption, 10, Muted, false));
            var border = Box(panel, AppTheme.Surface, 11, new Thickness(19, 14, 19, 14)); border.BorderBrush = Line; border.BorderThickness = new Thickness(1); border.Margin = new Thickness(0, 0, col < 2 ? 12 : 0, 0); Grid.SetColumn(border, col); grid.Children.Add(border);
        }
        void RenderAll()
        {
            RenderNav(); RenderStats();
            title.Text = summaryView ? "재료 준비" : stationOverview ? "교역 계획" : station + " 교역소";
            subtitle.Text = summaryView ? "구매·제작 방식을 정하고, 구비한 재료를 체크하세요." : stationOverview ? "교역소별 품목과 수량을 정하고, 예상 비용을 확인하세요." : "교환할 품목과 수량을 정하고, 필요한 재료를 확인하세요.";
            if (summaryView) RenderSummary();
            else { RenderPlanner(); if (procurementDialog != null) { procurementPlan = procurementPlanner.Build(state); RenderProcurementDetail(); } }
        }
        void RenderNav()
        {
            nav.Children.Clear();
            var label = T("교역 준비", 10, Muted, true); label.Margin = new Thickness(11, 0, 0, 12); nav.Children.Add(label);
            nav.Children.Add(BuildNavigationButton("교역 계획", "품목 · 수량 · 운송", "plan", ShowStationHub, !summaryView));
            nav.Children.Add(BuildNavigationButton("재료 준비", "구매 · 제작 · 구비", "materials", ShowSummary, summaryView));
            nav.Children.Add(BuildNavigationButton("PIP 체크리스트", "게임 위에 작게 띄우기", "pip", ShowPipChecklist, false));
            nav.Children.Add(BuildNavigationButton("시장 통계", "판매량 · 매물 현황", "market", ShowMarketStatistics, false));
            var divider = new Border { Height = 1, Background = Line, Margin = new Thickness(11, 18, 11, 17) }; nav.Children.Add(divider);
            var settingsLabel = T("앱 설정", 10, Muted, true); settingsLabel.Margin = new Thickness(11, 0, 0, 7); nav.Children.Add(settingsLabel);
            nav.Children.Add(BuildNavigationButton("진행 상태 복원", "", "settings", ShowProgressHistory, false));
        }
        void RenderStats(ProcurementPlan currentPlan = null)
        {
            procurementPlan = currentPlan ?? procurementPlanner.Build(state);
            procurementReadinessSteps = ProcurementReadiness.GetSteps(procurementPlan, state);
            var active = catalog.Trades.Where(t => Calculator.EffectiveTarget(state, t) > 0).ToList();
            int ready = active.Count(IsPlannedTradeReady);
            var readiness = ProcurementReadiness.GetStatus(procurementReadinessSteps);
            AppMotion.SetText(plannedStat, active.Count + "종"); AppMotion.SetText(readyStat, ready + " / " + active.Count);
            AppMotion.SetText(percentStat, ProcurementPercentText(readiness.Percent));
            percentStat.ToolTip = "직접 구비 " + readiness.ExplicitReady + "단계 · 상위 품목 구비로 충족 " + readiness.CoveredReady + "단계. 공유 재료는 완료한 사용분만 준비율에 반영합니다. 하위 재료의 실제 구비 체크 기록은 별도로 유지합니다.";
            UpdateStationValues();
        }
        bool IsPlannedTradeReady(TradeItem trade)
        {
            return ProcurementReadiness.IsTradeReady(state, trade, procurementReadinessSteps);
        }
        public void ShowStation(string name)
        {
            AppMotion.Transition(content, delegate {
                bool changed = station != name;
                station = name; summaryView = false; stationOverview = false;
                if (changed || selected == null) selected = catalog.Trades.First(t => t.Station == name);
                detailScroll.ScrollToTop();
                RenderAll();
            });
        }
        public void ShowSummary() { AppMotion.Transition(content, delegate { summaryView = true; summaryTab = 0; summaryQuery = ""; RenderAll(); }); }
        void RenderPlanner()
        {
            if (stationOverview) { RenderStationHub(); return; }
            stationValueUpdates.Clear();
            if (tradeScroll != null) tradeScroll.Content = null;
            content.Children.Clear(); content.ColumnDefinitions.Clear(); content.RowDefinitions.Clear();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(267) }); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); content.ColumnDefinitions.Add(new ColumnDefinition());
            var stationHeader = BuildStationDetailHeader(); Grid.SetColumnSpan(stationHeader, 3); content.Children.Add(stationHeader);
            var left = new Grid(); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition());
            var stationHeading = new Grid { Margin = new Thickness(0, 0, 0, 12) }; stationHeading.ColumnDefinitions.Add(new ColumnDefinition()); stationHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stationHeading.Children.Add(T(station, 17, Accent(station), true)); var all = T("교역품 5종", 10, Muted, false); Grid.SetColumn(all, 1); stationHeading.Children.Add(all); left.Children.Add(stationHeading);
            var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 13) };
            presets.Children.Add(Btn("모두 선택", delegate { Preset(5); }, false)); presets.Children.Add(Btn("3~5티어", delegate { ApplyStationHighTiers(); }, false)); presets.Children.Add(Btn("해제", delegate { Preset(0); }, false));
            foreach (Button b in presets.Children) { b.Padding = new Thickness(11, 6, 11, 6); b.FontSize = 11; }
            Grid.SetRow(presets, 1); left.Children.Add(presets);
            tradeScroll = new ScrollViewer { Content = tradeList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(tradeScroll, 2); left.Children.Add(tradeScroll);
            Grid.SetRow(left, 1); content.Children.Add(left); Grid.SetColumn(detail, 2); Grid.SetRow(detail, 1); content.Children.Add(detail);
            RenderTradeList(); RenderDetail();
            RefreshStationValueViews();
        }
        void RenderTradeList()
        {
            double offset = tradeScroll == null ? 0 : tradeScroll.VerticalOffset;
            tradeList.Children.Clear(); tradeControls.Clear(); stationTradeStatusUpdates.Clear();
            foreach (var trade in catalog.Trades.Where(t => t.Station == station))
            {
                var item = trade;
                var card = new StackPanel();
                var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var choose = new CheckBox { Content = "TIER " + item.Tier, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Accent(item.Station), IsChecked = Calculator.IsSelected(state, item), Cursor = Cursors.Hand, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "이번 교역에 포함할 품목 선택" };
                choose.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; IncludeTrade(item, choose.IsChecked == true); }; tradeControls[item.Id] = choose; top.Children.Add(choose);
                var statusLabel = T("", 10, Muted, true); Grid.SetColumn(statusLabel, 1); top.Children.Add(statusLabel); card.Children.Add(top);
                var name = ItemLabel(item.Name, T(item.Name, 14, Ink, true), 36); name.Margin = new Thickness(0, 9, 0, 10); card.Children.Add(name);
                var bottom = new Grid(); bottom.ColumnDefinitions.Add(new ColumnDefinition()); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var quantityLabel = T("", 10, Muted, false); bottom.Children.Add(quantityLabel);
                var viewed = T("상세 보는 중 →", 10, Accent(item.Station), true); viewed.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(viewed, 1); bottom.Children.Add(viewed); card.Children.Add(bottom);
                var button = Btn("", delegate { if (selected != null && selected.Id == item.Id) return; AppMotion.Transition(detail, delegate { selected = item; detailScroll.ScrollToTop(); RefreshStationValueViews(); RenderDetail(); }); }, false); button.Content = card; button.Padding = new Thickness(15, 13, 15, 13); button.Margin = new Thickness(0, 0, 0, 10); button.Tag = "trade-selection-card:" + item.Id;
                AutomationProperties.SetName(button, item.Name + " 교역품 상세");
                Action update = delegate {
                    bool included = Calculator.EffectiveTarget(state, item) > 0;
                    choose.IsChecked = Calculator.IsSelected(state, item);
                    statusLabel.Text = included ? IsPlannedTradeReady(item) ? "선택됨 · 준비 완료" : "선택됨" : "선택 안 함";
                    statusLabel.Foreground = included ? Green : Muted;
                    button.Background = included ? B("#E8F3EC") : AppTheme.Surface;
                    button.BorderBrush = included ? Green : Line;
                    quantityLabel.Text = "목표 " + Calculator.Target(state, item) + "개 / 최대 " + item.Limit + "개";
                    viewed.Visibility = selected != null && selected.Id == item.Id ? Visibility.Visible : Visibility.Hidden;
                    AutomationProperties.SetItemStatus(button, included ? "선택됨" : "선택 안 함");
                };
                stationTradeStatusUpdates.Add(update); update();
                tradeList.Children.Add(button);
            }
            var hint = T("초록색 카드는 이번 교역에 선택한 품목입니다.\n카드를 누르면 상세 내용을 볼 수 있습니다.\n위 빠른 선택은 현재 교역소에 적용됩니다.", 10, Muted, false); hint.Margin = new Thickness(3, 5, 0, 0); hint.LineHeight = 18; tradeList.Children.Add(hint);
            if (tradeScroll != null) tradeScroll.ScrollToVerticalOffset(offset);
        }
        void RenderDetail()
        {
            TradeItem currentTrade = selected;
            int target = Calculator.Target(state, currentTrade);
            bool reuseHeader = detailHeaderTrade != null && detailHeaderTrade.Id == currentTrade.Id && detailHeaderBox != null && updateDetailHeader != null && (detailHeaderBox.Parent == null || detailHeaderBox.Parent == detail);
            if (!reuseHeader)
            {
                detail.Children.Clear(); detail.RowDefinitions.Clear();
                detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); detail.RowDefinitions.Add(new RowDefinition());
                var header = new StackPanel();
                var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var labels = new StackPanel(); labels.Children.Add(T(station + "  /  " + currentTrade.Tier + "티어", 10, Accent(station), true));
                var name = ItemLabel(currentTrade.Name, T(currentTrade.Name, 23, Ink, true), 40); name.Margin = new Thickness(0, 7, 0, 0); labels.Children.Add(name); heading.Children.Add(labels); header.Children.Add(heading);
                var inputRow = new Grid { Margin = new Thickness(0, 19, 0, 0) }; inputRow.ColumnDefinitions.Add(new ColumnDefinition()); inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var targetLabel = new StackPanel(); targetLabel.Children.Add(T("교환 목표 수량", 12, Ink, true)); targetLabel.Children.Add(T("주간 최대 " + currentTrade.Limit + "개", 10, Muted, false)); inputRow.Children.Add(targetLabel);
                var stepper = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var minus = BuildQuantityStepButton(false, delegate { SetTarget(currentTrade, Calculator.Target(state, currentTrade) - 1); }); stepper.Children.Add(minus);
                var input = new TextBox { Text = target.ToString(), Width = 56, Height = 36, Margin = new Thickness(6, 0, 6, 0), FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, BorderBrush = Line, BorderThickness = new Thickness(1), Background = AppTheme.Surface, ToolTip = "0~" + currentTrade.Limit + " 입력 후 Enter" };
                AutomationProperties.SetName(input, "교환 목표 수량");
                Action<bool> commit = delegate(bool refresh) { int v; if (int.TryParse(input.Text, out v) && v >= 0 && v <= currentTrade.Limit) { if (v != Calculator.Target(state, currentTrade)) { SetTarget(currentTrade, v, refresh); if (!refresh) delayedRefresh.Start(); } } else { input.Text = Calculator.Target(state, currentTrade).ToString(); footerMessage.Text = "목표 수량은 0~" + currentTrade.Limit + " 사이의 정수로 입력하세요."; } };
                input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { commit(true); e.Handled = true; } };
                input.LostKeyboardFocus += delegate { commit(false); }; stepper.Children.Add(input);
                var plus = BuildQuantityStepButton(true, delegate { SetTarget(currentTrade, Calculator.Target(state, currentTrade) + 1); }); plus.Margin = new Thickness(0, 0, 9, 0); stepper.Children.Add(plus);
                input.TextChanged += delegate { int typed; if (Int32.TryParse(input.Text, out typed)) { minus.IsEnabled = typed > 0; plus.IsEnabled = typed < currentTrade.Limit; } };
                var maximum = Btn("최대", delegate { SetTarget(currentTrade, currentTrade.Limit); }, false); maximum.Margin = new Thickness(0); maximum.Height = 36; maximum.Padding = new Thickness(12, 0, 12, 0); stepper.Children.Add(maximum); Grid.SetColumn(stepper, 1); inputRow.Children.Add(stepper); header.Children.Add(inputRow);
                detailHeaderBox = Box(header, AppTheme.Surface, 11, new Thickness(24, 20, 24, 20)); detailHeaderBox.BorderBrush = Line; detailHeaderBox.BorderThickness = new Thickness(1); detailHeaderBox.Margin = new Thickness(0, 0, 0, 14);
                detailHeaderTrade = currentTrade;
                int renderedTarget = target;
                updateDetailHeader = delegate {
                    int value = Calculator.Target(state, currentTrade);
                    // Keep an unfinished numeric edit intact when an unrelated value refreshes.
                    if (!input.IsKeyboardFocusWithin || value != renderedTarget) input.Text = value.ToString();
                    renderedTarget = value;
                    minus.IsEnabled = value > 0; plus.IsEnabled = value < currentTrade.Limit;
                    maximum.IsEnabled = value < currentTrade.Limit;
                };
            }
            if (detail.RowDefinitions.Count == 0) { detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); detail.RowDefinitions.Add(new RowDefinition()); }
            if (detailHeaderBox.Parent == null) detail.Children.Add(detailHeaderBox);
            updateDetailHeader();
            var materials = new StackPanel();
            materials.Children.Add(BuildTradeForecast(selected, target));
            if (!Calculator.IsSelected(state, selected)) { var excluded = Box(T("이번 준비에서 제외한 품목입니다. 왼쪽 품목 체크를 켜면 재료 준비 목록에 포함됩니다.", 12, Muted, false), B("#EDF0EE"), 8, new Thickness(15)); excluded.Margin = new Thickness(0, 0, 0, 12); materials.Children.Add(excluded); }
            foreach (var group in selected.Groups) materials.Children.Add(GroupCard(group, target));
            var memo = T("목표 수량에 필요한 재료입니다. 준비 방식 설정과 구비 체크는 재료 준비에서 진행하세요.", 10, Muted, false); memo.Margin = new Thickness(3, 2, 3, 15); materials.Children.Add(memo);
            detailScroll.Content = materials; detailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; detailScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; detailScroll.Padding = new Thickness(0, 0, 5, 0); Grid.SetRow(detailScroll, 1); if (detailScroll.Parent == null) detail.Children.Add(detailScroll);
            quantityActions["decrease"] = delegate { SetTarget(selected, Calculator.Target(state, selected) - 1); };
            quantityActions["increase"] = delegate { SetTarget(selected, Calculator.Target(state, selected) + 1); };
        }
        Border GroupCard(MaterialGroup group, int target)
        {
            if (group.IsPurchased) return PurchaseCard(group, target);
            var panel = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(ItemLabel(group.Name, T(group.Name, 14, Ink, true), 28));
            var quantity = T(Calculator.FormatQuantity(group.PerTrade * target) + "개", 16, Accent(station), true); Grid.SetColumn(quantity, 1); header.Children.Add(quantity); panel.Children.Add(header);
            foreach (var line in group.Lines)
            {
                var row = new Grid { Margin = new Thickness(line.Depth * 18, 0, 0, 0), MinHeight = 38 };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = T(line.Name + (procurementPlanner.IsNpcPurchase(line.Name) ? "  · NPC 구매" : ""), 12, Ink, false);
                text.Margin = new Thickness(0, 6, 8, 6); if (!String.IsNullOrEmpty(line.Note)) text.ToolTip = line.Note;
                var materialName = ItemLabel(line.Name, text, 24); row.Children.Add(materialName);
                string amount = (line.Components != null && line.Components.Count > 1 ? "각 " : "") + Calculator.FormatQuantity(Calculator.Quantity(line, target));
                var quantities = new StackPanel { MaxWidth = 185, Margin = new Thickness(8, 6, 0, 6), VerticalAlignment = VerticalAlignment.Center };
                var count = T(amount + "개", 12, Ink, true); count.TextAlignment = TextAlignment.Right; quantities.Children.Add(count);
                if (!String.IsNullOrEmpty(line.AlternateName))
                {
                    var raw = ItemLabel(line.AlternateName, T(line.AlternateName + " " + Calculator.FormatQuantity(Calculator.AlternateQuantity(line, target)), 10, Muted, false), 18);
                    raw.Margin = new Thickness(0, 4, 0, 0); raw.ToolTip = "직접 제작할 때 필요한 원재료"; quantities.Children.Add(raw);
                }
                Grid.SetColumn(quantities, 1); row.Children.Add(quantities);
                var border = new Border { Child = row, BorderBrush = B("#EEF1EF"), BorderThickness = new Thickness(0, 1, 0, 0) }; panel.Children.Add(border);
            }
            if (group.Lines.Any(l => l.CheckId == "T41"))
            {
                var shared = T("실리엔 공통 준비량 " + Calculator.FormatQuantity(Calculator.SharedSilien(catalog, state)) + "개 · 칼리다 사용 품목 합계", 10, B("#2D7886"), false); shared.Margin = new Thickness(0, 10, 0, 0); panel.Children.Add(shared);
            }
            if (group.OutputPerBatch > 1 && target > 0)
            {
                decimal batches = Calculator.BatchCount(group, target);
                var batchNote = T("제작 " + Calculator.FormatQuantity(batches) + "회  ·  결과 " + Calculator.FormatQuantity(batches * group.OutputPerBatch) + "개  ·  잔여 " + Calculator.FormatQuantity(Calculator.Surplus(group, target)) + "개", 11, Green, true);
                batchNote.Margin = new Thickness(0, 11, 0, 0); panel.Children.Add(batchNote);
            }
            var notes = group.Lines.Select(l => l.Note).Concat(new [] { group.Note }).Where(n => !String.IsNullOrEmpty(n)).Distinct().ToList();
            foreach (var note in notes) { var label = T(note, 10, Muted, false); label.Margin = new Thickness(0, 9, 0, 0); label.LineHeight = 17; panel.Children.Add(label); }
            var result = Box(panel, AppTheme.Surface, 10, new Thickness(20, 16, 20, 14)); result.BorderBrush = Line; result.BorderThickness = new Thickness(1); result.Margin = new Thickness(0, 0, 0, 12); return result;
        }
        Border PurchaseCard(MaterialGroup group, int target)
        {
            var panel = new StackPanel();
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = T(group.Name, 14, Ink, true);
            var purchaseName = ItemLabel(group.Name, name, 28); header.Children.Add(purchaseName);
            var quantity = T(Calculator.FormatQuantity(group.PerTrade * target) + "개", 16, Accent(station), true); Grid.SetColumn(quantity, 1); header.Children.Add(quantity); panel.Children.Add(header);
            var note = T("완제품 구매", 11, Green, true); note.Margin = new Thickness(0, 11, 0, 0); panel.Children.Add(note);
            var box = Box(panel, AppTheme.Surface, 10, new Thickness(20, 18, 20, 18)); box.BorderBrush = Line; box.BorderThickness = new Thickness(1); box.Margin = new Thickness(0, 0, 0, 12); return box;
        }
        void RefreshProgress()
        {
            if (summaryView || stationOverview) { RenderAll(); return; }
            double offset = detailScroll.VerticalOffset;
            RenderStats(); RenderDetail();
            detailScroll.ScrollToVerticalOffset(offset);
            if (procurementDialog != null) RenderProcurementDetail();
        }
        void SetTarget(TradeItem trade, int value, bool refresh = true, bool save = true)
        {
            value = Math.Max(0, Math.Min(trade.Limit, value));
            if (Calculator.Target(state, trade) == value) return;
            decimal sharedBefore = Calculator.SharedSilien(catalog, state);
            state.Targets[trade.Id] = value;
            if (value == 0) Calculator.SetSelected(state, trade, false);
            bool sharedIncreased = Calculator.SharedSilien(catalog, state) > sharedBefore;
            foreach (var line in trade.Groups.SelectMany(g => g.Lines))
            {
                if (line.CheckId == "T41" && !sharedIncreased) continue;
                Calculator.SetChecked(state, line, false);
            }
            undo = null; footerActions.Children.Clear();
            footerMessage.Text = trade.Name + "의 수량을 변경했습니다. 구비한 수량보다 필요량이 늘어난 준비 단계는 다시 미완료로 표시합니다.";
            if (save) Persist(); if (refresh) RefreshProgress();
        }
        void Preset(int maxTier)
        {
            foreach (var trade in catalog.Trades.Where(t => t.Station == station))
            {
                bool include = trade.Tier <= maxTier;
                if (include && Calculator.Target(state, trade) == 0) state.Targets[trade.Id] = trade.DefaultQuantity;
                Calculator.SetSelected(state, trade, include);
            }
            undo = null; footerActions.Children.Clear(); footerMessage.Text = station + "의 교역품 선택을 변경했습니다. 설정한 수량은 유지됩니다.";
            Persist(); RefreshProgress();
        }
        void IncludeTrade(TradeItem trade, bool include)
        {
            if (include && Calculator.Target(state, trade) == 0) state.Targets[trade.Id] = trade.DefaultQuantity;
            Calculator.SetSelected(state, trade, include); undo = null; footerActions.Children.Clear();
            footerMessage.Text = trade.Name + (include ? "을(를) 재료 준비 목록에 포함했습니다." : "을(를) 재료 준비 목록에서 제외했습니다.");
            Persist(); RefreshProgress();
        }
        void ResetWeek()
        {
            if (!SaveProgressCheckpoint("주간 리셋 전")) return;
            undo = StateStore.Copy(state); state.ResetChecks(); Persist(); RenderAll();
            footerMessage.Text = "모든 재료의 준비 체크를 초기화했습니다. 품목 선택과 목표 수량은 유지됩니다.";
            footerActions.Children.Clear(); footerActions.Children.Add(Btn("리셋 되돌리기", UndoReset, false));
        }
        void UndoReset()
        {
            if (undo == null) return; state = undo; undo = null; footerActions.Children.Clear(); Persist(); RenderAll(); footerMessage.Text = "주간 리셋 전 상태로 복원했습니다.";
        }
        void Persist()
        {
            try { ProcurementPresets.SaveCurrent(state, procurementPlanner); store.Save(state); RefreshProcurementPresetControls(); saveStatus.Text = "●  저장됨 " + DateTime.Now.ToString("HH:mm"); saveStatus.Foreground = Green; }
            catch (Exception ex) { saveStatus.Text = "저장 실패"; saveStatus.Foreground = B("#BD493A"); footerMessage.Text = "저장 폴더에 쓸 수 없습니다. " + ex.Message; }
        }
        public void SavePreview(string file)
        {
            UpdateLayout(); Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {}));
            var bitmap = new RenderTargetBitmap((int)shell.ActualWidth, (int)shell.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(shell); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var f = File.Create(file)) encoder.Save(f);
        }
        public void SaveScenarioPreviews(string directory)
        {
            var original = StateStore.Copy(state);
            foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, false);
            foreach (var id in new [] { "C6", "C31", "K15", "K38" }) Calculator.SetSelected(state, catalog.Trades.First(t => t.Id == id), true);
            ShowStation("오아시스"); SavePreview(Path.Combine(directory, "prototype-split-potions.png"));
            ShowStation("카루 숲"); SavePreview(Path.Combine(directory, "prototype-shrimp.png"));
            ShowStation("페라 화산"); selected = catalog.Trades.First(t => t.Id == "K15"); state.Targets[selected.Id] = 1; RenderAll();
            SavePreview(Path.Combine(directory, "prototype-batch.png"));
            ShowSummary(); SavePreview(Path.Combine(directory, "prototype-summary.png"));
            ShowStation("카루 숲"); selected = catalog.Trades.First(t => t.Id == "C44"); Width = 1100; Height = 740; RenderAll();
            SavePreview(Path.Combine(directory, "prototype-compact.png"));
            Width = 1400; Height = 940; state = original; station = "오아시스"; selected = catalog.Trades.First(t => t.Station == station); RenderAll();
        }
        public string RunWorkflowChecks()
        {
            var original = StateStore.Copy(state);
            ShowStation(station);
            tradeControls[selected.Id].IsChecked = true;
            tradeControls[selected.Id].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (!Calculator.IsSelected(state, selected)) throw new Exception("Trade selection checkbox failed");
            var first = selected.Groups.First().Lines.First();
            // Seed a legacy saved check to verify compatibility without exposing editing UI.
            Calculator.SetChecked(state, first, true);
            SetTarget(selected, 10);
            if (Calculator.Target(state, selected) != 10 || Calculator.IsChecked(state, first)) throw new Exception("UI target change failed");
            foreach (var line in selected.Groups.SelectMany(g => g.Lines)) Calculator.SetChecked(state, line, true);
            Persist();
            if (!Calculator.IsTradeReady(state, selected)) throw new Exception("Mark all failed");
            var reload = store.Load(catalog);
            if (!Calculator.IsSelected(reload, selected) || Calculator.Target(reload, selected) != 10 || !Calculator.IsTradeReady(reload, selected)) throw new Exception("Persistence round trip failed");
            IncludeTrade(selected, false);
            if (Calculator.Target(state, selected) != 10 || !Calculator.IsChecked(state, first) || Calculator.Summarize(catalog, state, false).Count != 0) throw new Exception("Selection separation failed");
            IncludeTrade(selected, true);
            ResetWeek();
            if (Calculator.Target(state, selected) != 10 || Calculator.IsTradeReady(state, selected)) throw new Exception("Week reset failed");
            UndoReset();
            if (!Calculator.IsTradeReady(state, selected)) throw new Exception("Undo reset failed");
            Preset(3);
            if (catalog.Trades.Any(t => t.Station == station && Calculator.IsSelected(state, t) != (t.Tier <= 3)) || Calculator.Target(state, selected) != 10) throw new Exception("Tier preset failed");
            var salmon = catalog.Trades.First(t => t.Id == "K38");
            var bath = catalog.Trades.First(t => t.Id == "K42");
            Calculator.SetSelected(state, salmon, false); Calculator.SetSelected(state, bath, true);
            var shared = bath.Groups.SelectMany(g => g.Lines).First(l => l.CheckId == "T41"); Calculator.SetChecked(state, shared, true);
            SetTarget(salmon, 14);
            if (!Calculator.IsChecked(state, shared) || Calculator.SharedSilien(catalog, state) != 30) throw new Exception("Unselected target invalidated shared preparation");
            ShowSummary(); summaryTab = 1; RenderSummary(); summaryTab = 2; RenderSummary(); ShowStation(station);
            foreach (var item in catalog.Trades)
            {
                ShowStation(item.Station); selected = item; RenderAll(); UpdateLayout();
                if (AuctionTestChildren<CheckBox>(detail).Any()) throw new Exception("Station materials must be read-only: " + item.Name);
                if (AuctionTestChildren<Button>(detail).Any(b => Convert.ToString(b.Content).Contains("표 재료") || Convert.ToString(b.Content).Contains("구매 / 제작 설정"))) throw new Exception("Station material edit action remains");
            }
            state = original; station = "오아시스"; selected = catalog.Trades.First(t => t.Station == station); Persist(); RenderAll();
            return "PASS UI workflow: trade selection checkbox, independent preparation, target change, autosave/reload, deselection preserves quantities and readiness, summary excludes deselected items, week reset/undo, selection presets, 20 read-only trade material screens, summary view switching. Source XLSX was not changed.";
        }
    }
}
