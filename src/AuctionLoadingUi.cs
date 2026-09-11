using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MabinogiBarter
{
    // Play the approved GIF's four full transparent PNG frames. WPF's Image
    // does not animate GIFs itself; no browser, network or per-tick decoding.
    internal sealed class AuctionLoadingOverlay : Grid
    {
        static readonly Lazy<BitmapSource[]> artwork = new Lazy<BitmapSource[]>(LoadFrames);
        internal static BitmapSource[] ArtworkFrames { get { return artwork.Value; } }
        readonly DispatcherTimer animation;
        readonly Image illustration;
        readonly TextBlock heading, material, counts, percent, requests;
        readonly ProgressBar bar;
        readonly Action cancel;
        readonly int requestLimit;
        bool finishing, stopping;
        internal Button StopButton { get; private set; }
        internal int FrameIndex { get; private set; }
        internal bool IsAnimating { get { return animation.IsEnabled; } }
        internal double ProgressValue { get { return bar.Value; } }
        internal string CountsText { get { return counts.Text; } }
        internal string RequestsText { get { return requests.Text; } }

        internal AuctionLoadingOverlay(int total, int limit, Action cancelAction, string scope)
        {
            cancel = cancelAction; requestLimit = limit;
            Background = Brushes.Transparent;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            var body = new StackPanel { Width = 380, Margin = new Thickness(16) };
            var fit = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20), Child = body };
            Children.Add(fit);
            illustration = new Image { Source = artwork.Value[0], Width = 380, Height = 380, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(illustration, BitmapScalingMode.NearestNeighbor);
            body.Children.Add(illustration);
            heading = Label(scope + "를 확인하고 있어요", 19, "#202D35", true);
            heading.TextAlignment = TextAlignment.Center;
            heading.Margin = new Thickness(0, 8, 0, 16); body.Children.Add(heading);
            var progressLabels = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            counts = Label("0 / " + total + "종 확인", 13, "#202D35", true);
            percent = Label("0%", 17, "#226C54", true); percent.HorizontalAlignment = HorizontalAlignment.Right;
            progressLabels.Children.Add(counts); progressLabels.Children.Add(percent); body.Children.Add(progressLabels);
            bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 9,
                Background = Brush("#E8EDE9"), Foreground = Brush("#367D60") };
            var template = new ControlTemplate(typeof(ProgressBar));
            var track = new FrameworkElementFactory(typeof(Border)); track.Name = "PART_Track";
            track.SetValue(Border.BackgroundProperty, bar.Background); track.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var fill = new FrameworkElementFactory(typeof(Border)); fill.Name = "PART_Indicator";
            fill.SetValue(Border.BackgroundProperty, bar.Foreground); fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            fill.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            track.AppendChild(fill); template.VisualTree = track; bar.Template = template; body.Children.Add(bar);
            material = Label("선택한 재료를 준비하고 있어요", 12, "#52665D", false);
            material.TextWrapping = TextWrapping.NoWrap; material.TextTrimming = TextTrimming.CharacterEllipsis;
            material.TextAlignment = TextAlignment.Center;
            material.Margin = new Thickness(0, 12, 0, 6); body.Children.Add(material);
            requests = Label("요청 0 / " + limit + "회", 10, "#52665D", false);
            requests.TextAlignment = TextAlignment.Center; body.Children.Add(requests);
            StopButton = new Button { Content = "갱신 중지", Padding = new Thickness(17, 9, 17, 9),
                HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, Cursor = Cursors.Hand,
                Margin = new Thickness(0, 18, 0, 0),
                Background = Brush("#226C54"), Foreground = AppTheme.OnAccent, BorderBrush = Brush("#226C54"), BorderThickness = new Thickness(1) };
            var buttonTemplate = new ControlTemplate(typeof(Button));
            var buttonBorder = new FrameworkElementFactory(typeof(Border));
            buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            buttonBorder.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            buttonBorder.SetValue(Border.BorderBrushProperty, StopButton.BorderBrush); buttonBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var buttonContent = new FrameworkElementFactory(typeof(ContentPresenter));
            buttonContent.SetValue(FrameworkElement.MarginProperty, StopButton.Padding);
            buttonBorder.AppendChild(buttonContent); buttonTemplate.VisualTree = buttonBorder;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#19543F"))); buttonTemplate.Triggers.Add(hover);
            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(OpacityProperty, 0.6)); buttonTemplate.Triggers.Add(disabled);
            StopButton.Template = buttonTemplate; StopButton.Click += delegate { RequestStop(); }; body.Children.Add(StopButton);
            animation = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            animation.Tick += AdvanceFrame;
            Loaded += delegate { if (!finishing) { animation.Start(); StopButton.Focus(); } };
            Unloaded += delegate { animation.Stop(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Escape) { e.Handled = true; RequestStop(); }
            };
        }

        static Brush Brush(string color) { return AppTheme.Brush(color); }
        static TextBlock Label(string text, double size, string color, bool bold)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        }
        static BitmapSource[] LoadFrames()
        {
            var frames = new BitmapSource[4];
            for (int i = 0; i < frames.Length; i++)
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MabinogiBarter.Loading.frame-" + (i + 1).ToString("00") + ".png"))
                {
                    if (stream == null) throw new InvalidDataException("로딩 애니메이션이 없습니다. 실행 파일을 다시 빌드해 주세요.");
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    frame.Freeze(); frames[i] = frame;
                }
            return frames;
        }
        void AdvanceFrame(object sender, EventArgs e)
        {
            FrameIndex = (FrameIndex + 1) % artwork.Value.Length; illustration.Source = artwork.Value[FrameIndex];
        }
        internal void Report(AuctionRefreshProgress progress)
        {
            int total = Math.Max(0, progress.Total), done = Math.Max(0, Math.Min(total, progress.Completed));
            bar.Value = total == 0 ? 0 : 100.0 * done / total;
            counts.Text = done + " / " + total + "종 확인";
            percent.Text = ((int)Math.Floor(bar.Value)).ToString() + "%";
            requests.Text = "요청 " + Math.Max(0, progress.Requests) + " / " + requestLimit + "회";
            if (!stopping) { material.Text = progress.Material ?? ""; material.ToolTip = progress.Material; }
        }
        internal void RequestStop()
        {
            if (stopping || finishing) return;
            stopping = true; StopButton.IsEnabled = false; StopButton.Content = "중지 중…";
            heading.Text = "갱신을 마무리하고 있어요"; material.Text = "확인한 가격을 저장한 뒤 닫힙니다.";
            cancel();
        }
        internal void Finish()
        {
            if (finishing) return;
            finishing = true; animation.Stop(); animation.Tick -= AdvanceFrame;
        }
    }

    public sealed partial class MainWindow
    {
        AuctionLoadingOverlay auctionLoading;
        Grid auctionLoadingHost;
        Window auctionLoadingOwner;
        IInputElement auctionLoadingPreviousFocus;
        EventHandler auctionLoadingOwnerClosed;
        readonly List<Action> auctionLoadingRestore = new List<Action>();

        static Grid CreateAuctionLayerHost(UIElement content)
        {
            var host = new Grid(); host.Children.Add(content); return host;
        }
        void BlurAuctionSurface(UIElement surface)
        {
            var effect = surface.Effect; bool enabled = surface.IsEnabled;
            auctionLoadingRestore.Add(delegate { surface.Effect = effect; surface.IsEnabled = enabled; });
            surface.Effect = new BlurEffect { Radius = 14, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            surface.IsEnabled = false;
        }
        void ShowAuctionLoading(int total, string scope)
        {
            Window owner = procurementDialog != null && procurementDialog.IsVisible ? procurementDialog : this;
            auctionLoadingOwner = owner;
            auctionLoadingPreviousFocus = Keyboard.FocusedElement;
            auctionLoadingHost = (Grid)owner.Content;
            auctionLoading = new AuctionLoadingOverlay(total, Math.Max(1, Math.Min(AuctionSettings.RequestLimit, auctionSettings.MaxRequestsPerRefresh)), delegate {
                if (auctionCancellation != null) auctionCancellation.Cancel();
                if (!auctionClosing) auctionStatus.Text = "갱신을 중지하고 있습니다…";
            }, scope);
            BlurAuctionSurface(shell);
            if (owner != this) BlurAuctionSurface(auctionLoadingHost.Children[0]);
            auctionLoadingOwnerClosed = delegate {
                if (auctionCancellation != null) auctionCancellation.Cancel();
                CloseAuctionLoading();
            };
            owner.Closed += auctionLoadingOwnerClosed;
            Panel.SetZIndex(auctionLoading, 100);
            auctionLoadingHost.Children.Add(auctionLoading);
            AppMotion.Enter(auctionLoading);
        }
        void CloseAuctionLoading()
        {
            var dialog = auctionLoading; auctionLoading = null;
            if (dialog != null) dialog.Finish();
            if (auctionLoadingHost != null && dialog != null) auctionLoadingHost.Children.Remove(dialog);
            foreach (var restore in auctionLoadingRestore) restore();
            auctionLoadingRestore.Clear();
            if (!auctionClosing) AppMotion.Feedback(shell);
            var owner = auctionLoadingOwner;
            if (owner != null && auctionLoadingOwnerClosed != null) owner.Closed -= auctionLoadingOwnerClosed;
            var previous = auctionLoadingPreviousFocus as UIElement;
            if (!auctionClosing && owner != null && owner.IsActive && previous != null && previous.IsVisible && previous.IsEnabled)
                Keyboard.Focus(previous);
            auctionLoadingHost = null; auctionLoadingOwner = null;
            auctionLoadingOwnerClosed = null; auctionLoadingPreviousFocus = null;
        }
    }
}
