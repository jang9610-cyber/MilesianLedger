using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;

namespace MabinogiBarter
{
    public sealed partial class PipChecklistWindow
    {
        public Func<string, FrameworkElement> AcquisitionContent { get; set; }
        Popup acquisitionPopup;
        ToolTip acquisitionTip;

        static void StyleReadyCheck(CheckBox check)
        {
            var template = new ControlTemplate(typeof(CheckBox));
            var grid = new FrameworkElementFactory(typeof(Grid)); grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var box = new FrameworkElementFactory(typeof(Border), "CheckSurface");
            box.SetValue(FrameworkElement.WidthProperty, 22d); box.SetValue(FrameworkElement.HeightProperty, 22d);
            box.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left); box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(5)); box.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            box.SetValue(Border.BorderBrushProperty, Muted); box.SetValue(Border.BackgroundProperty, AppTheme.Surface);
            var tick = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path), "CheckMark");
            tick.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 3,9 L 7,13 L 15,4"));
            tick.SetValue(System.Windows.Shapes.Shape.StrokeProperty, AppTheme.OnAccent); tick.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2.7d);
            tick.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round); tick.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            tick.SetValue(FrameworkElement.MarginProperty, new Thickness(1)); tick.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(tick); grid.AppendChild(box);
            var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.MarginProperty, new Thickness(30, 0, 0, 0)); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); grid.AppendChild(content);
            template.VisualTree = grid;
            var ready = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            ready.Setters.Add(new Setter(Border.BackgroundProperty, Green, "CheckSurface")); ready.Setters.Add(new Setter(Border.BorderBrushProperty, Green, "CheckSurface")); ready.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark")); template.Triggers.Add(ready);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BorderBrushProperty, Green, "CheckSurface")); template.Triggers.Add(hover);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .5)); template.Triggers.Add(disabled);
            check.Template = template;
        }

        FrameworkElement HelpBody(string key, string name)
        {
            var body = new StackPanel();
            ProcurementStep step;
            if (latest.TryGetValue(key, out step) && describe != null)
            {
                var plan = Label(describe(step), 13, Green, false); plan.TextWrapping = TextWrapping.Wrap; plan.LineHeight = 21; plan.Margin = new Thickness(0, 0, 0, 14); body.Children.Add(plan);
            }
            body.Children.Add(AcquisitionContent == null ? Label(name + " · 획득 정보가 없습니다.", 14, Ink, false) : AcquisitionContent(name));
            return new ScrollViewer { Content = body, Width = 306, MaxHeight = Math.Min(420, Math.Max(150, SystemParameters.WorkArea.Height - 130)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 8, 0) };
        }

        Button CreateAcquisitionHelp(string key, string name)
        {
            var button = SmallButton("?", delegate {}); button.Width = 26; button.Height = 28; button.Padding = new Thickness(0); button.FontSize = 15; button.Foreground = Green;
            AutomationProperties.SetName(button, "PIP " + name + " 획득 방법"); button.Tag = "pip-help:" + key;
            var tip = new ToolTip { Placement = PlacementMode.Right, PlacementTarget = button, Background = AppTheme.Surface, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(16), FontFamily = FontFamily, Focusable = false };
            tip.Opened += delegate { acquisitionTip = tip; tip.Content = HelpBody(key, name); };
            button.ToolTip = tip; ToolTipService.SetInitialShowDelay(button, 250); ToolTipService.SetShowDuration(button, 120000);
            button.Click += delegate(object sender, RoutedEventArgs e) {
                e.Handled = true;
                bool same = acquisitionPopup != null && acquisitionPopup.IsOpen && acquisitionPopup.PlacementTarget == button;
                CloseAcquisitionHelp(); if (same) return;
                var body = new StackPanel(); body.Children.Add(HelpBody(key, name));
                var close = SmallButton("닫기", CloseAcquisitionHelp); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(close);
                var popup = new Popup { Placement = PlacementMode.Right, PlacementTarget = button, StaysOpen = false, AllowsTransparency = true, Focusable = false,
                    Child = new Border { Child = body, Background = AppTheme.Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16) } };
                popup.PreviewGotKeyboardFocus += delegate(object o, KeyboardFocusChangedEventArgs a) { a.Handled = true; };
                ToolTipService.SetIsEnabled(button, false);
                popup.Closed += delegate { ToolTipService.SetIsEnabled(button, true); if (acquisitionPopup == popup) acquisitionPopup = null; };
                acquisitionPopup = popup; popup.IsOpen = true;
            };
            return button;
        }
        void CloseAcquisitionHelp()
        {
            if (acquisitionTip != null) { acquisitionTip.IsOpen = false; acquisitionTip = null; }
            var popup = acquisitionPopup; acquisitionPopup = null; if (popup != null) popup.IsOpen = false;
        }
    }
}
