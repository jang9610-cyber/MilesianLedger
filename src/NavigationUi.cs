using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        Button BuildNavigationButton(string name, string description, string icon, Action action, bool selectedPage)
        {
            bool compact = String.IsNullOrEmpty(description);
            var button = new Button {
                Background = selectedPage ? Green : compact ? AppTheme.Surface : B("#F5F7F6"),
                BorderBrush = selectedPage ? Green : Brushes.Transparent,
                BorderThickness = new Thickness(1), Padding = new Thickness(10, compact ? 10 : 15, 10, compact ? 10 : 15),
                Margin = new Thickness(0, 0, 0, compact ? 0 : 6), Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, FocusVisualStyle = null
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 23 : 32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 7 : 9) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var iconImage = new Image { Source = NavigationIcon(icon, selectedPage ? AppTheme.OnAccent : Green),
                Width = compact ? 18 : 20, Height = compact ? 18 : 20, VerticalAlignment = VerticalAlignment.Center };
            var iconTile = Box(iconImage, selectedPage ? B("#3C806A") : compact ? Brushes.Transparent : B("#E5EEE9"), 8, new Thickness(0));
            iconTile.Height = compact ? 23 : 32; iconTile.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(iconTile);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(T(name, compact ? 11 : 14, selectedPage ? AppTheme.OnAccent : Ink, true));
            if (!compact)
            {
                var hint = T(description, 10, selectedPage ? AppTheme.OnAccent : Muted, false);
                hint.Margin = new Thickness(0, 5, 0, 0); hint.TextWrapping = TextWrapping.NoWrap; words.Children.Add(hint);
            }
            Grid.SetColumn(words, 2); row.Children.Add(words); button.Content = row;

            var template = new ControlTemplate(typeof(Button));
            var focus = new FrameworkElementFactory(typeof(Border), "NavigationFocus");
            focus.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            focus.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            focus.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            var frame = new FrameworkElementFactory(typeof(Border), "NavigationFrame");
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            foreach (string property in new[] { "Background", "BorderBrush", "BorderThickness", "Padding" })
            {
                var target = property == "Background" ? Border.BackgroundProperty : property == "BorderBrush" ? Border.BorderBrushProperty
                    : property == "BorderThickness" ? Border.BorderThicknessProperty : Border.PaddingProperty;
                frame.SetBinding(target, new Binding(property) { RelativeSource = RelativeSource.TemplatedParent });
            }
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(presenter); focus.AppendChild(frame); template.VisualTree = focus;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, selectedPage ? B("#1C604A") : B("#EAF1ED"), "NavigationFrame"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, selectedPage ? B("#174E3D") : B("#DBE8E0"), "NavigationFrame"));
            template.Triggers.Add(pressed);
            var keyboard = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            keyboard.Setters.Add(new Setter(Border.BorderBrushProperty, B("#B58B35"), "NavigationFocus")); template.Triggers.Add(keyboard);
            button.Template = template;
            AutomationProperties.SetName(button, name + " 메뉴");
            AutomationProperties.SetHelpText(button, description);
            AutomationProperties.SetItemStatus(button, selectedPage ? "현재 화면" : "");
            button.Click += delegate { action(); };
            return button;
        }

        static DrawingImage NavigationIcon(string kind, Brush stroke)
        {
            string path = kind == "plan"
                ? "M3,5 L9,3 15,5 21,3 21,19 15,21 9,19 3,21 Z M9,3 L9,19 M15,5 L15,21"
                : kind == "materials"
                ? "M9,5 L21,5 M9,12 L21,12 M9,19 L21,19 M2,5 L4,7 7,3 M2,12 L4,14 7,10 M2,19 L4,21 7,17"
                : kind == "pip"
                ? "M3,4 L21,4 21,20 3,20 Z M11,12 L19,12 19,18 11,18 Z"
                : kind == "settlement"
                ? "M5,3 L19,3 19,21 5,21 Z M8,7 L16,7 M8,12 L10,12 M14,12 L16,12 M8,17 L10,17 M14,17 L16,17"
                : "M5,3 L5,21 M12,3 L12,21 M19,3 L19,21 M2,8 L8,8 M9,16 L15,16 M16,7 L22,7";
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
            var pen = new Pen(stroke, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawing.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(path)));
            var image = new DrawingImage(drawing); return image;
        }
    }
}
