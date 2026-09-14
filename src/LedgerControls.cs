using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MabinogiBarter
{
    // Shared trade, market and settlement chrome. Values, events and sizing remain
    // owned by each view; animation uses the same interruptible AppMotion hooks.
    public static partial class LedgerControls
    {
        static readonly Brush Ink = AppTheme.Brush("#202D35"), Muted = AppTheme.Brush("#728087"),
            Line = AppTheme.Brush("#E2E8E5"), Green = AppTheme.Brush("#226C54");
        static ControlTemplate inputTemplate, buttonTemplate;

        public static StackPanel PageHeading(string eyebrow, TextBlock title, TextBlock subtitle)
        {
            var heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = eyebrow, FontSize = 10, Foreground = Green,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            title.FontSize = 28; title.FontWeight = FontWeights.SemiBold; title.Foreground = Ink;
            title.Margin = new Thickness(0, 7, 0, 6); title.TextWrapping = TextWrapping.Wrap;
            subtitle.FontSize = 12; subtitle.Foreground = Muted; subtitle.TextWrapping = TextWrapping.Wrap;
            subtitle.Margin = new Thickness(0, 0, 16, 0);
            heading.Children.Add(title); heading.Children.Add(subtitle); return heading;
        }

        public static void StyleTextInput(TextBox input)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (inputTemplate == null) {
                var template = new ControlTemplate(typeof(TextBox));
                var border = new FrameworkElementFactory(typeof(Border), "Frame");
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                border.SetBinding(Border.BackgroundProperty, Templated("Background"));
                border.SetBinding(Border.BorderBrushProperty, Templated("BorderBrush"));
                var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost"); border.AppendChild(host); template.VisualTree = border;
                var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Border.BorderBrushProperty, AppTheme.Brush("#91B4A4"), "Frame")); template.Triggers.Add(hover);
                var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
                focus.Setters.Add(new Setter(Border.BorderBrushProperty, Green, "Frame")); template.Triggers.Add(focus);
                inputTemplate = template;
            }
            input.Background = AppTheme.Surface; input.Foreground = Ink; input.BorderBrush = Line;
            input.BorderThickness = new Thickness(1); input.CaretBrush = Ink; input.SelectionBrush = Green;
            input.Template = inputTemplate;
        }

        public static void StyleButton(Button button)
        {
            if (button == null) throw new ArgumentNullException("button");
            AppMotion.Initialize();
            if (buttonTemplate == null) {
                var template = new ControlTemplate(typeof(Button));
                var border = new FrameworkElementFactory(typeof(Border), "ButtonSurface");
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
                border.SetBinding(Border.BackgroundProperty, Templated("Background"));
                border.SetBinding(Border.BorderBrushProperty, Templated("BorderBrush"));
                border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                border.SetBinding(Border.PaddingProperty, Templated("Padding"));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, Templated("HorizontalContentAlignment"));
                presenter.SetBinding(ContentPresenter.VerticalAlignmentProperty, Templated("VerticalContentAlignment"));
                border.AppendChild(presenter); template.VisualTree = border;
                var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
                focus.Setters.Add(new Setter(Border.BorderBrushProperty, AppTheme.Brush("#B58B35"), "ButtonSurface")); template.Triggers.Add(focus);
                var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
                disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .38)); template.Triggers.Add(disabled);
                buttonTemplate = template;
            }
            button.Template = buttonTemplate;
        }

        static Binding Templated(string property) { return new Binding(property) { RelativeSource = RelativeSource.TemplatedParent }; }
    }
}
