using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        static ControlTemplate StationComboTemplate;
        static Style StationComboItemStyle;

        // Keep ComboBox's selection, focus, capture, keyboard and automation logic.
        // This helper only replaces its noneditable visual template and item style.
        static void StyleStationCombo(ComboBox combo, string placeholder)
        {
            if (combo == null) throw new ArgumentNullException("combo");
            AppMotion.Dropdown(combo);
            if (StationComboTemplate == null)
                StationComboTemplate = (ControlTemplate)XamlReader.Parse(StationComboTemplateXaml);
            if (StationComboItemStyle == null)
                StationComboItemStyle = (Style)XamlReader.Parse(StationComboItemStyleXaml);

            // Per-control resources keep arbitrary placeholder text out of XAML
            // and leave Tag, bindings, item templates and selection values intact.
            combo.Resources["StationComboPlaceholder"] = placeholder ?? "";
            combo.Resources["StationComboSurface"] = AppTheme.Surface;
            combo.Resources["StationComboLine"] = Line;
            combo.Resources["StationComboGreen"] = Green;
            combo.Resources["StationComboInk"] = Ink;
            combo.Resources["StationComboMuted"] = Muted;
            combo.Resources["StationComboHover"] = B("#EEF6F1");
            combo.Resources["StationComboSelected"] = B("#DDEDE5");
            combo.Resources["StationComboDisabled"] = B("#F4F6F5");
            combo.Background = AppTheme.Surface;
            combo.Foreground = Ink;
            combo.BorderBrush = Line;
            combo.BorderThickness = new Thickness(1);
            combo.Padding = new Thickness(12, 0, 34, 0);
            combo.Height = 36;
            combo.MinHeight = 36;
            combo.FontSize = 12;
            combo.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            combo.VerticalContentAlignment = VerticalAlignment.Center;
            combo.IsEditable = false;
            combo.IsReadOnly = true;
            combo.FocusVisualStyle = null;
            combo.SnapsToDevicePixels = true;
            ScrollViewer.SetCanContentScroll(combo, true);
            combo.ItemContainerStyle = StationComboItemStyle;
            combo.Template = StationComboTemplate;
        }

        const string StationComboTemplateXaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                 TargetType='{x:Type ComboBox}'>
  <Grid x:Name='StationComboRoot' SnapsToDevicePixels='True'>
    <Border x:Name='StationComboChrome'
            Background='{TemplateBinding Background}'
            BorderBrush='{TemplateBinding BorderBrush}'
            BorderThickness='{TemplateBinding BorderThickness}'
            CornerRadius='8' IsHitTestVisible='False'/>
    <ToggleButton x:Name='StationComboToggle' Focusable='False' IsTabStop='False'
                  ClickMode='Press' Background='Transparent'
                  IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
      <ToggleButton.Template>
        <ControlTemplate TargetType='{x:Type ToggleButton}'>
          <Border Background='{TemplateBinding Background}' CornerRadius='8'/>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter x:Name='StationComboSelection' IsHitTestVisible='False'
                      Margin='{TemplateBinding Padding}' ClipToBounds='True'
                      HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                      VerticalAlignment='{TemplateBinding VerticalContentAlignment}'
                      Content='{TemplateBinding SelectionBoxItem}'
                      ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                      ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'
                      ContentStringFormat='{TemplateBinding SelectionBoxItemStringFormat}'
                      SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}'>
      <ContentPresenter.Resources>
        <Style TargetType='{x:Type TextBlock}'>
          <Setter Property='TextWrapping' Value='NoWrap'/>
          <Setter Property='TextTrimming' Value='CharacterEllipsis'/>
        </Style>
      </ContentPresenter.Resources>
    </ContentPresenter>
    <TextBlock x:Name='StationComboPlaceholderText' IsHitTestVisible='False'
               Text='{DynamicResource StationComboPlaceholder}'
               Foreground='{DynamicResource StationComboMuted}'
               Margin='{TemplateBinding Padding}' VerticalAlignment='Center'
               TextTrimming='CharacterEllipsis' TextWrapping='NoWrap' Visibility='Collapsed'/>
    <Path x:Name='StationComboArrow' IsHitTestVisible='False'
          Data='M 0 0 L 4 4 L 8 0' Width='9' Height='5' Stretch='Uniform'
          Stroke='{DynamicResource StationComboMuted}' StrokeThickness='1.6'
          StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'
          HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,13,0'/>
    <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}'
           Placement='Bottom' PlacementTarget='{Binding RelativeSource={RelativeSource TemplatedParent}}'
           AllowsTransparency='True' Focusable='False' PopupAnimation='Slide' VerticalOffset='4'>
      <Border x:Name='StationComboMenu' Background='{DynamicResource StationComboSurface}'
              BorderBrush='{DynamicResource StationComboLine}' BorderThickness='1'
              CornerRadius='8' Padding='4' SnapsToDevicePixels='True'
              MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}'>
        <ScrollViewer x:Name='StationComboMenuScroll' Focusable='False'
                      CanContentScroll='{TemplateBinding ScrollViewer.CanContentScroll}'
                      HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'>
          <ItemsPresenter x:Name='ItemsPresenter' SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}'
                          KeyboardNavigation.DirectionalNavigation='Contained'/>
        </ScrollViewer>
      </Border>
    </Popup>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property='SelectedIndex' Value='-1'>
      <Setter TargetName='StationComboSelection' Property='Visibility' Value='Collapsed'/>
      <Setter TargetName='StationComboPlaceholderText' Property='Visibility' Value='Visible'/>
    </Trigger>
    <Trigger Property='IsMouseOver' Value='True'>
      <Setter TargetName='StationComboChrome' Property='BorderBrush' Value='{DynamicResource StationComboGreen}'/>
      <Setter TargetName='StationComboArrow' Property='Stroke' Value='{DynamicResource StationComboGreen}'/>
    </Trigger>
    <Trigger Property='IsKeyboardFocusWithin' Value='True'>
      <Setter TargetName='StationComboChrome' Property='BorderBrush' Value='{DynamicResource StationComboGreen}'/>
      <Setter TargetName='StationComboArrow' Property='Stroke' Value='{DynamicResource StationComboGreen}'/>
    </Trigger>
    <Trigger Property='IsDropDownOpen' Value='True'>
      <Setter TargetName='StationComboChrome' Property='BorderBrush' Value='{DynamicResource StationComboGreen}'/>
      <Setter TargetName='StationComboArrow' Property='Stroke' Value='{DynamicResource StationComboGreen}'/>
    </Trigger>
    <Trigger Property='IsEnabled' Value='False'>
      <Setter TargetName='StationComboRoot' Property='Opacity' Value='0.55'/>
      <Setter TargetName='StationComboChrome' Property='Background' Value='{DynamicResource StationComboDisabled}'/>
      <Setter TargetName='StationComboChrome' Property='BorderBrush' Value='{DynamicResource StationComboLine}'/>
      <Setter TargetName='StationComboArrow' Property='Stroke' Value='{DynamicResource StationComboMuted}'/>
    </Trigger>
    <Trigger Property='HasItems' Value='False'>
      <Setter TargetName='StationComboMenu' Property='MinHeight' Value='36'/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";

        const string StationComboItemStyleXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type ComboBoxItem}'>
  <Setter Property='Foreground' Value='{DynamicResource StationComboInk}'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='BorderBrush' Value='Transparent'/>
  <Setter Property='BorderThickness' Value='1'/>
  <Setter Property='Padding' Value='8,6'/>
  <Setter Property='MinHeight' Value='32'/>
  <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
  <Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='SnapsToDevicePixels' Value='True'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type ComboBoxItem}'>
        <Border x:Name='StationComboItemChrome' CornerRadius='5'
                Background='{TemplateBinding Background}'
                BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
                Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
          <ContentPresenter ContentSource='Content' RecognizesAccessKey='True'
                            HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                            VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsSelected' Value='True'>
            <Setter TargetName='StationComboItemChrome' Property='Background' Value='{DynamicResource StationComboSelected}'/>
            <Setter Property='Foreground' Value='{DynamicResource StationComboGreen}'/>
          </Trigger>
          <Trigger Property='IsHighlighted' Value='True'>
            <Setter TargetName='StationComboItemChrome' Property='Background' Value='{DynamicResource StationComboHover}'/>
            <Setter Property='Foreground' Value='{DynamicResource StationComboGreen}'/>
          </Trigger>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='StationComboItemChrome' Property='Background' Value='{DynamicResource StationComboHover}'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocusWithin' Value='True'>
            <Setter TargetName='StationComboItemChrome' Property='BorderBrush' Value='{DynamicResource StationComboGreen}'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter TargetName='StationComboItemChrome' Property='Opacity' Value='0.45'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
    }
}
