using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        TextBlock stationPresetFeedback, stationSavedPresetHint;

        FrameworkElement BuildStationPresetControls()
        {
            var body = new StackPanel();
            var heading = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            heading.Children.Add(T("교역 프리셋", 13, Ink, true));
            var scope = T("4개 교역소의 선택과 수량을 한 번에 적용", 11, Muted, false);
            scope.Margin = new Thickness(12, 0, 0, 0); heading.Children.Add(scope); body.Children.Add(heading);
            var builtins = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 14) };
            string[] names = { "성실한 상인", "체리피커", "극한의 한탕" };
            string[] descriptions = { "모든 티어 · 최대 수량", "3~5티어 · 최대 수량", "3~5티어 · 한 칸씩" };
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i]; var label = new StackPanel();
                label.Children.Add(T(name, 12, Green, true));
                var description = T(descriptions[i], 10, Muted, false); description.Margin = new Thickness(0, 5, 0, 0); label.Children.Add(description);
                var button = Btn("", delegate { ApplyNamedStationPreset(name); }, false);
                button.Content = label; button.Padding = new Thickness(12, 10, 12, 10); button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.ToolTip = i == 2 ? "오아시스·카루 숲·페라 화산: 3·4티어 각 7개, 5티어 각 3개. 칼리다 호수: 5티어 3개."
                    : i == 0 ? "네 교역소의 모든 품목을 주간 최대 수량으로 선택합니다." : "네 교역소의 3~5티어 품목을 주간 최대 수량으로 선택합니다.";
                AutomationProperties.SetName(button, name + " 프리셋 적용"); stationPresetButtons[name] = button; builtins.Children.Add(button);
            }
            body.Children.Add(builtins);

            var cards = new Grid();
            cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var saveBody = new StackPanel();
            saveBody.Children.Add(T("새 프리셋 저장", 12, Ink, true));
            var saveIntro = T("자주 쓰는 교역 구성에 이름을 붙여 보관하세요.", 10, Muted, false); saveIntro.Margin = new Thickness(0, 5, 0, 13); saveBody.Children.Add(saveIntro);
            var nameLabel = new Label { Content = "프리셋 이름", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ink, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 6) };
            saveBody.Children.Add(nameLabel);
            var saveRow = new Grid(); saveRow.ColumnDefinitions.Add(new ColumnDefinition()); saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stationPresetName = new TextBox { MinWidth = 150, Height = 36, MaxLength = 40, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 11, Padding = new Thickness(11, 0, 11, 0), Foreground = Ink, Background = AppTheme.Surface, BorderBrush = Line, BorderThickness = new Thickness(1), ToolTip = "프리셋 이름을 1~40자로 입력하세요. 예: 주말 고티어 교역" };
            StyleStationPresetName(stationPresetName); nameLabel.Target = stationPresetName;
            AutomationProperties.SetName(stationPresetName, "프리셋 이름"); AutomationProperties.SetHelpText(stationPresetName, "저장할 교역 구성의 이름, 1~40자. 예: 주말 고티어 교역");
            var field = new Grid { Margin = new Thickness(0, 0, 8, 0) }; field.Children.Add(stationPresetName);
            var placeholder = T("예: 주말 고티어 교역", 11, Muted, false); placeholder.Margin = new Thickness(12, 0, 12, 0); placeholder.IsHitTestVisible = false; placeholder.TextWrapping = TextWrapping.NoWrap; placeholder.TextTrimming = TextTrimming.CharacterEllipsis; field.Children.Add(placeholder); saveRow.Children.Add(field);
            var save = Btn("현재 선택을 프리셋으로 저장", SaveStationPreset, true); save.Height = 36; save.Padding = new Thickness(11, 0, 11, 0); save.FontSize = 11; save.Margin = new Thickness(0); Grid.SetColumn(save, 1); saveRow.Children.Add(save); stationPresetButtons["save"] = save;
            saveBody.Children.Add(saveRow);
            stationPresetFeedback = T("최대 40자 · 품목 선택과 수량만 저장합니다.", 10, Muted, false); stationPresetFeedback.Margin = new Thickness(0, 8, 0, 0); stationPresetFeedback.MinHeight = 16; saveBody.Children.Add(stationPresetFeedback);
            stationPresetName.TextChanged += delegate { placeholder.Visibility = String.IsNullOrEmpty(stationPresetName.Text) ? Visibility.Visible : Visibility.Collapsed; RefreshStationPresetForm(); };

            var loadBody = new StackPanel();
            loadBody.Children.Add(T("저장한 프리셋 불러오기", 12, Ink, true));
            var loadIntro = T("저장한 구성을 골라 이번 교역에 적용하세요.", 10, Muted, false); loadIntro.Margin = new Thickness(0, 5, 0, 13); loadBody.Children.Add(loadIntro);
            var savedLabel = new Label { Content = "저장한 프리셋", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ink, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 6) }; loadBody.Children.Add(savedLabel);
            var loadRow = new Grid(); loadRow.ColumnDefinitions.Add(new ColumnDefinition()); loadRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); loadRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            savedPresetControl = new ComboBox { MinWidth = 140, ItemsSource = state.TradePresets, DisplayMemberPath = "Name", Margin = new Thickness(0, 0, 8, 0) };
            StyleStationCombo(savedPresetControl, "프리셋을 선택하세요"); savedLabel.Target = savedPresetControl; AutomationProperties.SetName(savedPresetControl, "저장한 프리셋 선택"); loadRow.Children.Add(savedPresetControl);
            var load = Btn("불러오기", delegate { var item = savedPresetControl.SelectedItem as TradePlanPreset; if (item != null) ApplyStationQuantities(item.Quantities, item.Name); }, false); load.Height = 36; load.FontSize = 11; load.Padding = new Thickness(11, 0, 11, 0); Grid.SetColumn(load, 1); loadRow.Children.Add(load); stationPresetButtons["load"] = load;
            var remove = Btn("삭제", DeleteStationPreset, false); remove.Height = 36; remove.FontSize = 11; remove.Padding = new Thickness(11, 0, 11, 0); remove.Margin = new Thickness(0); Grid.SetColumn(remove, 2); loadRow.Children.Add(remove); stationPresetButtons["delete"] = remove;
            loadBody.Children.Add(loadRow);
            stationSavedPresetHint = T("", 10, Muted, false); stationSavedPresetHint.Margin = new Thickness(0, 8, 0, 0); stationSavedPresetHint.MinHeight = 16; loadBody.Children.Add(stationSavedPresetHint);
            savedPresetControl.SelectionChanged += delegate { RefreshStationPresetForm(); };
            var saveCard = Box(saveBody, B("#F4F8F5"), 8, new Thickness(14));
            var loadCard = Box(loadBody, B("#F6F8F7"), 8, new Thickness(14));
            cards.Children.Add(saveCard); cards.Children.Add(loadCard);
            Action arrange = delegate {
                bool stacked = cards.ActualWidth > 0 && cards.ActualWidth < 950;
                Grid.SetColumnSpan(saveCard, stacked ? 2 : 1); Grid.SetColumnSpan(loadCard, stacked ? 2 : 1);
                Grid.SetColumn(loadCard, stacked ? 0 : 1); Grid.SetRow(loadCard, stacked ? 1 : 0);
                saveCard.Margin = stacked ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 6, 0);
                loadCard.Margin = stacked ? new Thickness(0) : new Thickness(6, 0, 0, 0);
            };
            cards.SizeChanged += delegate { arrange(); }; arrange(); body.Children.Add(cards); RefreshStationPresetForm(); return body;
        }

        static void StyleStationPresetName(TextBox input)
        {
            var template = new ControlTemplate(typeof(TextBox));
            var border = new FrameworkElementFactory(typeof(Border), "Frame"); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost"); border.AppendChild(host); template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BorderBrushProperty, B("#91B4A4"), "Frame")); template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderBrushProperty, Green, "Frame")); template.Triggers.Add(focus); input.Template = template;
        }

        void RefreshStationPresetForm()
        {
            if (stationPresetName == null || stationPresetFeedback == null || stationSavedPresetHint == null) return;
            string name = (stationPresetName.Text ?? "").Trim(); bool duplicate = state.TradePresets.Any(p => String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            stationPresetButtons["save"].IsEnabled = name.Length > 0 && name.Length <= 40 && !duplicate;
            stationPresetFeedback.Text = duplicate ? "같은 이름이 있어요. 다른 이름을 입력하세요." : "최대 40자 · 품목 선택과 수량만 저장합니다.";
            stationPresetFeedback.Foreground = duplicate ? B("#A4463C") : Muted;
            bool hasSelection = savedPresetControl.SelectedItem is TradePlanPreset;
            savedPresetControl.IsEnabled = state.TradePresets.Count > 0; stationPresetButtons["load"].IsEnabled = hasSelection; stationPresetButtons["delete"].IsEnabled = hasSelection;
            stationSavedPresetHint.Text = state.TradePresets.Count == 0 ? "아직 저장한 프리셋이 없어요. 이름을 입력해 저장해 보세요."
                : "저장된 프리셋 " + state.TradePresets.Count + "개 · 구매·제작 방식은 유지됩니다.";
        }
    }
}
