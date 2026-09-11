using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace MabinogiBarter
{
    public sealed class PipWindowSettings
    {
        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
        delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rectangle, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo information);

        public double Width { get; set; }
        public double Height { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }
        public int Tab { get; set; }
        public PipWindowSettings() { Width = 360; Height = 540; Tab = 1; }
        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        public void Normalize()
        {
            Width = Finite(Width) ? Math.Max(320, Math.Min(720, Width)) : 360;
            Height = Finite(Height) ? Math.Max(300, Math.Min(1000, Height)) : 540;
            if (Left.HasValue && !Finite(Left.Value)) Left = null;
            if (Top.HasValue && !Finite(Top.Value)) Top = null;
            Tab = Tab == 2 ? 2 : 1;
        }
        public static PipWindowSettings Load(string path)
        {
            PipWindowSettings result;
            try { result = File.Exists(path) ? new JavaScriptSerializer().Deserialize<PipWindowSettings>(File.ReadAllText(path)) : null; }
            catch (IOException) { result = null; }
            catch (UnauthorizedAccessException) { result = null; }
            catch (ArgumentException) { result = null; }
            catch (InvalidOperationException) { result = null; }
            result = result ?? new PipWindowSettings(); result.Normalize(); return result;
        }
        public void Apply(Window window)
        {
            Normalize(); var work = SystemParameters.WorkArea;
            double x = Left ?? work.Right - Width - 18, y = Top ?? work.Top + 65;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            ApplyBounds(window, x, y, Width, Height);
        }

        // Recover an existing singleton after a monitor disconnect without
        // reloading its saved tab, saved position, or previous dimensions.
        public static void EnsureVisible(Window window)
        {
            if (window == null) return;
            var bounds = window.WindowState == WindowState.Normal ? Rect.Empty : window.RestoreBounds;
            double width = bounds.IsEmpty ? window.Width : bounds.Width;
            double height = bounds.IsEmpty ? window.Height : bounds.Height;
            double x = bounds.IsEmpty ? window.Left : bounds.Left, y = bounds.IsEmpty ? window.Top : bounds.Top;
            if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
            ApplyBounds(window, x, y, width, height);
        }

        static void ApplyBounds(Window window, double x, double y, double width, double height)
        {
            var primary = SystemParameters.WorkArea;
            width = Finite(width) && width > 0 ? width : 360;
            height = Finite(height) && height > 0 ? height : 540;
            bool positionValid = Finite(x) && Finite(y);
            var selected = Rect.Empty;
            if (positionValid)
            {
                var header = new Rect(x, y, Math.Min(width, 160), 40);
                foreach (var work in MonitorWorkAreas(window))
                    if (work.Contains(header)) { selected = work; break; }
            }
            if (selected.IsEmpty)
            {
                selected = primary;
                x = primary.Right - Math.Min(width, primary.Width) - 18;
                y = primary.Top + 65;
            }
            // A virtual desktop's bounding rectangle can include empty gaps
            // between monitors. Constrain to one actual monitor work area.
            width = Math.Max(window.MinWidth, Math.Min(width, selected.Width));
            height = Math.Max(window.MinHeight, Math.Min(height, selected.Height));
            window.Width = width; window.Height = height;
            window.Left = Math.Max(selected.Left, Math.Min(x, selected.Right - width));
            window.Top = Math.Max(selected.Top, Math.Min(y, selected.Bottom - height));
        }

        static List<Rect> MonitorWorkAreas(Window window)
        {
            var nativeAreas = new List<Rect>();
            Rect nativePrimary = Rect.Empty;
            MonitorCallback callback = delegate(IntPtr monitor, IntPtr dc, IntPtr rectangle, IntPtr data) {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (GetMonitorInfo(monitor, ref info) && info.Work.Right > info.Work.Left && info.Work.Bottom > info.Work.Top)
                {
                    var area = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
                    nativeAreas.Add(area);
                    if ((info.Flags & 1) != 0) nativePrimary = area;
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            Matrix fromDevice = Matrix.Identity;
            var main = Application.Current == null ? null : Application.Current.MainWindow;
            var source = main == null ? null : PresentationSource.FromVisual(main);
            if (source == null || source.CompositionTarget == null) source = PresentationSource.FromVisual(window);
            if (source != null && source.CompositionTarget != null) fromDevice = source.CompositionTarget.TransformFromDevice;
            else if (!nativePrimary.IsEmpty)
            {
                // Also support restoration before a WPF HWND exists.
                var primary = SystemParameters.WorkArea;
                fromDevice.Scale(primary.Width / nativePrimary.Width, primary.Height / nativePrimary.Height);
            }
            var areas = new List<Rect>();
            foreach (var native in nativeAreas) areas.Add(Rect.Transform(native, fromDevice));
            if (areas.Count == 0) areas.Add(SystemParameters.WorkArea);
            return areas;
        }
        public void Save(string path)
        {
            Normalize(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            string pending = path + ".tmp";
            File.WriteAllText(pending, new JavaScriptSerializer().Serialize(this), new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(pending, path, path + ".bak"); else File.Move(pending, path);
        }
    }
}
