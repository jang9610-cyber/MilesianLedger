using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace MabinogiBarter
{
    // Binding-backed shared colors stay live even inside sealed WPF templates.
    // Changing appearance never rebuilds a page or touches preparation progress.
    public static class AppTheme
    {
        sealed class Swatch : INotifyPropertyChanged
        {
            public Color Color { get; private set; }
            public event PropertyChangedEventHandler PropertyChanged;
            public void Set(Color value) { Color = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Color")); }
        }
        static readonly Dictionary<string, Swatch> colors = new Dictionary<string, Swatch>();
        static readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();
        static string settingsPath;
        static bool initialized;
        public static bool IsDark { get; private set; }
        public static SolidColorBrush Surface { get { return Brush("#FFFFFF"); } }
        public static SolidColorBrush OnAccent { get { return Brush("accent-text"); } }
        public static SolidColorBrush Brush(string key)
        {
            key = key.ToUpperInvariant();
            SolidColorBrush result;
            if (brushes.TryGetValue(key, out result)) return result;
            var swatch = new Swatch(); swatch.Set(Resolve(key));
            result = new SolidColorBrush();
            BindingOperations.SetBinding(result, SolidColorBrush.ColorProperty, new Binding("Color") { Source = swatch });
            colors.Add(key, swatch); brushes.Add(key, result); return result;
        }
        static Color Resolve(string key)
        {
            if (key == "ACCENT-TEXT") return Parse(IsDark ? "#10291F" : "#FFFFFF");
            Color c = Parse(key);
            if (!IsDark || c.A == 0) return c;
            switch (key)
            {
                case "#FFFFFF": return Parse("#222B2B");
                case "#F4F6F5": case "#F4F7F4": return Parse("#171E1E");
                case "#202D35": return Parse("#E6EEEB");
                case "#FBF3DE": return Parse("#343024");
                case "#EAF3E9": return Parse("#25372D");
                case "#F0EAF7": return Parse("#322C3D");
                case "#E7F3F4": return Parse("#25363C");
                case "#E2E8E5": case "#DCE5DF": return Parse("#3D4C47");
                case "#226C54": return Parse("#83CBAA");
                case "#19543F": return Parse("#A0DFBE");
            }
            double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            double light = (max + min) / 510.0;
            // Pale station colors keep their hue as dark tinted surfaces.
            if (light > .76) return Color.FromArgb(c.A, (byte)(29 + c.R * .075), (byte)(35 + c.G * .085), (byte)(35 + c.B * .085));
            // Labels, status colors and borders become readable on dark surfaces.
            double blend = light < .30 ? .65 : .43;
            return Color.FromArgb(c.A, (byte)(c.R + (240 - c.R) * blend), (byte)(c.G + (246 - c.G) * blend), (byte)(c.B + (243 - c.B) * blend));
        }
        static Color Parse(string key) { return (Color)ColorConverter.ConvertFromString(key); }
        public static void Initialize(string path)
        {
            settingsPath = path;
            bool dark = false;
            try { dark = File.Exists(path) && File.ReadAllText(path).Trim() == "dark"; } catch (IOException) {} catch (UnauthorizedAccessException) {}
            Apply(dark);
            if (initialized || Application.Current == null) return;
            initialized = true;
            var r = Application.Current.Resources;
            foreach (var type in new[] { typeof(Window), typeof(TextBox), typeof(PasswordBox), typeof(CheckBox), typeof(RadioButton), typeof(Expander), typeof(ListBox), typeof(ListBoxItem), typeof(ComboBox), typeof(ComboBoxItem), typeof(ToolTip) })
            {
                var style = new Style(type);
                style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#202D35")));
                if (type == typeof(TextBox) || type == typeof(PasswordBox) || type == typeof(ListBox) || type == typeof(ComboBox) || type == typeof(ToolTip))
                {
                    style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
                    style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#728087")));
                }
                r[type] = style;
            }
            r["LedgerScrollTrack"] = Brush("#F4F6F5");
            r["LedgerScrollThumb"] = Brush("#A4B2AA");
            r[typeof(ScrollBar)] = System.Windows.Markup.XamlReader.Parse(ScrollBarStyle);
            r[SystemColors.WindowBrushKey] = Surface;
            r[SystemColors.WindowTextBrushKey] = Brush("#202D35");
            r[SystemColors.ControlBrushKey] = Surface;
            r[SystemColors.ControlTextBrushKey] = Brush("#202D35");
            r[SystemColors.HighlightBrushKey] = Brush("#226C54");
            r[SystemColors.HighlightTextBrushKey] = OnAccent;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e) { SetTitleBar((Window)sender); }));
        }
        public static void SetDark(bool dark)
        {
            if (!String.IsNullOrEmpty(settingsPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                string temp = settingsPath + ".tmp"; File.WriteAllText(temp, dark ? "dark" : "light");
                if (File.Exists(settingsPath)) File.Replace(temp, settingsPath, null); else File.Move(temp, settingsPath);
            }
            Apply(dark);
        }
        static void Apply(bool dark)
        {
            IsDark = dark;
            foreach (var entry in colors) entry.Value.Set(Resolve(entry.Key));
            if (Application.Current != null) foreach (Window window in Application.Current.Windows) SetTitleBar(window);
        }
        // Track reserves half the system button metric for its minimum thumb length.
        // A Thumb.MinHeight alone overflows that allocation and clips its rounded end.
        const string ScrollBarStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:sys='clr-namespace:System;assembly=mscorlib' TargetType='{x:Type ScrollBar}'>
 <Setter Property='Width' Value='12'/>
 <Setter Property='Template'>
  <Setter.Value>
   <ControlTemplate TargetType='{x:Type ScrollBar}'>
    <Border Background='{DynamicResource LedgerScrollTrack}'>
     <Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}'>
      <Track.Resources>
       <sys:Double x:Key='{x:Static SystemParameters.VerticalScrollBarButtonHeightKey}'>48</sys:Double>
       <sys:Double x:Key='{x:Static SystemParameters.HorizontalScrollBarButtonWidthKey}'>48</sys:Double>
      </Track.Resources>
      <Track.DecreaseRepeatButton><RepeatButton x:Name='PageBefore' Command='ScrollBar.PageUpCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
      <Track.Thumb><Thumb x:Name='ScrollThumb'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Border Background='{DynamicResource LedgerScrollThumb}' CornerRadius='4' Margin='3,2'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
      <Track.IncreaseRepeatButton><RepeatButton x:Name='PageAfter' Command='ScrollBar.PageDownCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
     </Track>
    </Border>
    <ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/><Setter TargetName='PageBefore' Property='Command' Value='ScrollBar.PageLeftCommand'/><Setter TargetName='PageAfter' Property='Command' Value='ScrollBar.PageRightCommand'/></Trigger></ControlTemplate.Triggers>
   </ControlTemplate>
  </Setter.Value>
 </Setter>
 <Style.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='12'/></Trigger></Style.Triggers>
</Style>";
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        static void SetTitleBar(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            try { DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); } catch (DllNotFoundException) {} catch (EntryPointNotFoundException) {}
        }
    }
}
