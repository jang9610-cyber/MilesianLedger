using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        Button BuildQuantityStepButton(bool increase, Action action)
        {
            var button = Btn("", action, false);
            button.Width = 36;
            button.Height = 36;
            button.MinWidth = 36;
            button.MinHeight = 36;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(0);
            button.Background = B("#EDF5F0");
            button.BorderBrush = B("#9BBBAB");
            button.Foreground = Green;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.FocusVisualStyle = null; // The template keeps a visible keyboard focus ring.
            button.ToolTip = increase ? "교환 수량 1개 늘리기" : "교환 수량 1개 줄이기";
            AutomationProperties.SetName(button, increase ? "교환 수량 늘리기" : "교환 수량 줄이기");

            var template = new ControlTemplate(typeof(Button));
            var root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(FrameworkElement.UseLayoutRoundingProperty, true);
            root.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var surface = new FrameworkElementFactory(typeof(Border), "StepperSurface");
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            surface.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            surface.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            root.AppendChild(surface);

            // Equal-sized vector strokes avoid font baseline and glyph-bearing differences.
            root.AppendChild(QuantityStepStroke("StepHorizontal", 14, 2));
            if (increase) root.AppendChild(QuantityStepStroke("StepVertical", 2, 14));

            var focus = new FrameworkElementFactory(typeof(Border), "StepperFocus");
            focus.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            focus.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            focus.SetValue(Border.BorderBrushProperty, Green);
            focus.SetValue(FrameworkElement.MarginProperty, new Thickness(3));
            focus.SetValue(UIElement.IsHitTestVisibleProperty, false);
            focus.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            root.AppendChild(focus);
            template.VisualTree = root;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, B("#DFEDE4"), "StepperSurface"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, B("#43836A"), "StepperSurface"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, B("#CADFD3"), "StepperSurface"));
            pressed.Setters.Add(new Setter(Border.BorderBrushProperty, Green, "StepperSurface"));
            template.Triggers.Add(pressed);
            var keyboard = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            keyboard.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "StepperFocus"));
            template.Triggers.Add(keyboard);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, B("#F1F4F2"), "StepperSurface"));
            disabled.Setters.Add(new Setter(Border.BorderBrushProperty, B("#D7E0DB"), "StepperSurface"));
            disabled.Setters.Add(new Setter(Shape.FillProperty, B("#9AA8A0"), "StepHorizontal"));
            if (increase) disabled.Setters.Add(new Setter(Shape.FillProperty, B("#9AA8A0"), "StepVertical"));
            template.Triggers.Add(disabled);
            button.Template = template;
            return button;
        }

        static FrameworkElementFactory QuantityStepStroke(string name, double width, double height)
        {
            var stroke = new FrameworkElementFactory(typeof(Rectangle), name);
            stroke.SetValue(FrameworkElement.WidthProperty, width);
            stroke.SetValue(FrameworkElement.HeightProperty, height);
            stroke.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            stroke.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            stroke.SetValue(UIElement.IsHitTestVisibleProperty, false);
            stroke.SetBinding(Shape.FillProperty, new Binding("Foreground") { RelativeSource = RelativeSource.TemplatedParent });
            return stroke;
        }
    }
}
