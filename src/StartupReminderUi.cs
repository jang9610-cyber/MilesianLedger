using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed class StartupReminderWindow : Window
    {
        readonly DispatcherTimer animation;
        readonly Image illustration;
        bool finished;
        public Button ConfirmButton { get; private set; }
        public Button DismissButton { get; private set; }
        public int FrameIndex { get; private set; }
        public bool IsAnimating { get { return animation.IsEnabled; } }

        public StartupReminderWindow()
        {
            AppMotion.Initialize();
            Title = "밀레시안 장부 · 출발 전 확인";
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
            message.Children.Add(Label("교역 출발 전 확인", 11, "#226C54", true));
            var question = Label("그랜드마스터 상인으로\n전환하셨나요?", 23, "#202D35", true);
            question.Margin = new Thickness(0, 9, 0, 8); message.Children.Add(question);
            message.Children.Add(Label("게임에서 전환 여부를 확인한 뒤 시작하세요.", 11, "#667B71", false));
            ConfirmButton = BuildButton("확인 · 시작하기", true); ConfirmButton.Height = 46;
            ConfirmButton.IsDefault = true; ConfirmButton.Margin = new Thickness(0, 18, 0, 0);
            AutomationProperties.SetName(ConfirmButton, "확인 · 시작하기");
            ConfirmButton.Click += delegate { if (finished) return; finished = true; ConfirmButton.IsEnabled = false; DialogResult = true; };
            message.Children.Add(ConfirmButton);
            body.Children.Add(new Border { Child = message, Background = Paint("#FAFDFB"), CornerRadius = new CornerRadius(18),
                BorderBrush = Paint("#BED5C7"), BorderThickness = new Thickness(1), Padding = new Thickness(26, 22, 26, 23),
                Effect = new DropShadowEffect { Color = Color.FromRgb(26, 61, 44), BlurRadius = 18, ShadowDepth = 5, Opacity = .18 } });
            Content = new Viewbox { Child = body, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(22),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            animation = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            animation.Tick += AdvanceFrame;
            Loaded += delegate { if (AppMotion.Enabled) animation.Start(); ConfirmButton.Focus(); };
            Closed += delegate { finished = true; animation.Stop(); animation.Tick -= AdvanceFrame; };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
            AppMotion.WindowContent(this);
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
        public static Window PrepareMainWindow(Application app, StartupReminderWindow reminder, Func<Window> createMain)
        {
            // Keep the dispatcher alive between the reminder and the real main window.
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
