using System;
using System.Windows;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed class StartupLoadingWindow : Window
    {
        readonly DispatcherTimer animation;
        readonly Image illustration;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly Func<CancellationToken, Task<string>> load;
        readonly TextBlock status;
        readonly ProgressBar progress;
        bool started, closed;
        public bool LoadingCompleted { get; private set; }
        public string StatusText { get { return status.Text; } }
        public Button DismissButton { get; private set; }
        public int FrameIndex { get; private set; }
        public bool IsAnimating { get { return animation.IsEnabled; } }

        public StartupLoadingWindow(Func<CancellationToken, Task<string>> load)
        {
            if (load == null) throw new ArgumentNullException("load");
            this.load = load;
            AppMotion.Initialize();
            Title = "밀레시안 장부 · 경매장 정보 불러오기";
            Icon = MainWindow.LoadApplicationIcon();
            WindowStyle = WindowStyle.None; AllowsTransparency = true;
            Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            Width = Math.Min(460, Math.Max(280, SystemParameters.WorkArea.Width - 40));
            Height = Math.Min(650, Math.Max(320, SystemParameters.WorkArea.Height - 32));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Malgun Gothic");
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            var body = new StackPanel { Width = 400 };
            DismissButton = BuildButton("×", false); DismissButton.Width = 30; DismissButton.Height = 30;
            DismissButton.HorizontalAlignment = HorizontalAlignment.Right;
            DismissButton.ToolTip = "앱 실행 취소 (Esc)";
            AutomationProperties.SetName(DismissButton, "앱 실행 취소");
            DismissButton.Click += delegate { Close(); };
            body.Children.Add(DismissButton);
            illustration = new Image { Source = AuctionLoadingOverlay.ArtworkFrames[0], Width = 380, Height = 380, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(illustration, BitmapScalingMode.NearestNeighbor);
            AutomationProperties.SetName(illustration, "교역 마차 애니메이션");
            body.Children.Add(illustration);
            var message = new StackPanel();
            message.Children.Add(Label("밀레시안 장부", 11, "#226C54", true));
            var heading = Label("경매장 정보 불러오기", 23, "#202D35", true);
            heading.Margin = new Thickness(0, 9, 0, 8); message.Children.Add(heading);
            status = Label("서버에 저장된 공통 시세를 확인하고 있습니다.", 12, "#667B71", false);
            message.Children.Add(status);
            progress = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100, Height = 6,
                Foreground = Paint("#226C54"), Background = Paint("#E7F0EA"), Margin = new Thickness(0, 18, 0, 0) };
            AutomationProperties.SetName(progress, "경매장 정보 로딩 진행도");
            message.Children.Add(progress);
            body.Children.Add(new Border { Child = message, Background = Paint("#FAFDFB"), CornerRadius = new CornerRadius(18),
                BorderBrush = Paint("#BED5C7"), BorderThickness = new Thickness(1), Padding = new Thickness(26, 22, 26, 23),
                Effect = new DropShadowEffect { Color = Color.FromRgb(26, 61, 44), BlurRadius = 18, ShadowDepth = 5, Opacity = .18 } });
            Content = new Viewbox { Child = body, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(22),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            animation = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            animation.Tick += AdvanceFrame;
            Loaded += BeginLoading;
            Closed += delegate { closed = true; lifetime.Cancel(); animation.Stop(); animation.Tick -= AdvanceFrame; };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
            AppMotion.WindowContent(this);
        }

        async void BeginLoading(object sender, RoutedEventArgs e)
        {
            if (started) return;
            started = true;
            if (AppMotion.Enabled) animation.Start();
            try {
                string message;
                try { message = await Task.Run(() => load(lifetime.Token), lifetime.Token); }
                catch (OperationCanceledException) { if (closed) return; message = "연결 시간이 초과되었습니다. 저장된 정보로 시작합니다."; }
                catch (Exception ex) {
                    if (ex is OutOfMemoryException || ex is StackOverflowException) throw;
                    message = "경매장 정보를 불러오지 못했습니다. 저장된 정보로 시작합니다.";
                }
                if (closed) return;
                LoadingCompleted = true;
                status.Text = message + "\n잠시 후 장부를 엽니다.";
                progress.IsIndeterminate = false; progress.Value = 100;
                // Keep the artwork timer running for the full post-load delay.
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                if (!closed) DialogResult = true;
            } catch (OperationCanceledException) { /* Closing the splash cancels startup. */ }
        }

        void AdvanceFrame(object sender, EventArgs e)
        {
            FrameIndex = (FrameIndex + 1) % AuctionLoadingOverlay.ArtworkFrames.Length;
            illustration.Source = AuctionLoadingOverlay.ArtworkFrames[FrameIndex];
        }
        static Brush Paint(string value) { return AppTheme.Brush(value); }
        static TextBlock Label(string text, double size, string color, bool bold)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Paint(color), TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        }
        static Button BuildButton(string text, bool primary)
        {
            var button = new Button { Content = text, FontSize = primary ? 15 : 19, FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand, Background = Paint(primary ? "#226C54" : "#FAFDFB"), Foreground = primary ? AppTheme.OnAccent : Paint("#536D60") };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "Surface");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(primary ? 10 : 15));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(2)); border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter); template.VisualTree = border;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Paint(primary ? "#19543F" : "#E7F0EA"), "Surface")); template.Triggers.Add(hover);
            var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, Paint("#B9D8C8"), "Surface")); template.Triggers.Add(focus);
            button.Template = template; return button;
        }
    }

    public static class StartupSequence
    {
        public static async Task<string> LoadMarketAsync(Catalog catalog, string directory, CancellationToken token)
        {
            var config = AuctionProxyConfig.Load(Path.Combine(directory, "auction-proxy.json"));
            if (!config.IsConfigured) return "경매장 서버가 설정되지 않았습니다. 저장된 정보로 시작합니다.";
            using (var service = new AuctionService(directory, AuctionSettings.Load(Path.Combine(directory, "auction-settings.json")))) {
                var result = await service.RefreshAsync(new ProcurementPlanner(catalog).GetAllQuoteNames(), null, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var data = MarketSnapshotClient.ForBaseUri(config.BaseUri).CachedData;
                if (!String.IsNullOrEmpty(result.StoppedReason))
                    return data != null ? "최신 정보 확인에 실패했습니다. 저장된 시세로 시작합니다." : "경매장 연결에 실패했습니다. 메인 화면에서 다시 갱신할 수 있습니다.";
                return "경매장 정보를 불러왔습니다.";
            }
        }

        public static Window PrepareMainWindow(Application app, StartupLoadingWindow reminder, Func<Window> createMain)
        {
            // Keep the dispatcher alive between the loading screen and the main window.
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.MainWindow = reminder;
            if (reminder.ShowDialog() != true) { app.Shutdown(); return null; }
            var main = createMain();
            app.MainWindow = main;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return main;
        }
    }
}
