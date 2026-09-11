using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MabinogiBarter
{
    // Focus the checklist when interacting, without discarding the first click.
    // Native changes apply only to our HWNDs; no automatic return to another app.
    public static class PipWindowBehavior
    {
        const int GwlExStyle = -20;
        const long NoActivate = 0x08000000, Transparent = 0x20, ToolWindow = 0x80, AppWindow = 0x40000;
        static readonly ConditionalWeakTable<Window, object> attached = new ConditionalWeakTable<Window, object>();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr window, int index, int value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLong64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLong64(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);

        public static void Attach(Window window)
        {
            object marker; if (attached.TryGetValue(window, out marker)) return;
            attached.Add(window, new object());
            window.Topmost = true; window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Focusable = true;
            // Take focus before interaction; neither activation clicks nor wheel events are eaten.
            window.MouseEnter += delegate { ActivateForInteraction(window); };
            window.PreviewMouseDown += delegate { ActivateForInteraction(window); };
            window.PreviewMouseWheel += delegate { ActivateForInteraction(window); };
            HwndSource source = null; HwndSourceHook hook = delegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
                if (message == 0x0021) { handled = true; return new IntPtr(1); } // MA_ACTIVATE: take focus and keep the click.
                return IntPtr.Zero;
            };
            window.SourceInitialized += delegate {
                var handle = new WindowInteropHelper(window).Handle;
                long style = IntPtr.Size == 8 ? GetWindowLong64(handle, GwlExStyle).ToInt64() : GetWindowLong32(handle, GwlExStyle);
                style = (style | ToolWindow) & ~(NoActivate | Transparent | AppWindow);
                if (IntPtr.Size == 8) SetWindowLong64(handle, GwlExStyle, new IntPtr(style));
                else SetWindowLong32(handle, GwlExStyle, (int)style);
                source = HwndSource.FromHwnd(handle); if (source != null) source.AddHook(hook);
                BringForward(window);
            };
            window.Closed += delegate { if (source != null && !source.IsDisposed) source.RemoveHook(hook); source = null; };
        }
        public static bool ActivateForInteraction(Window window)
        {
            if (!window.IsLoaded || !window.IsVisible || !IsNativeEnabled(window) || window.WindowState == WindowState.Minimized) return false;
            // Preserve a drag that began in another window.
            if (!window.IsActive && (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed)) return false;
            if (!window.IsActive && !window.Activate()) return false;
            if (!window.IsKeyboardFocusWithin) Keyboard.Focus(window);
            return window.IsActive;
        }

        public static void BringForward(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);
        }
        public static bool IsNativeEnabled(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            return window.IsEnabled && (handle == IntPtr.Zero || IsWindowEnabled(handle));
        }
        public static void PlaceBehindModal(Window window, bool behind)
        {
            window.Topmost = !behind;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetWindowPos(handle, new IntPtr(behind ? 1 : -1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        public static void ObserveEnabled(Window window, Action changed)
        {
            HwndSource source = null;
            HwndSourceHook hook = delegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
                if (message == 0x000A && !window.Dispatcher.HasShutdownStarted)
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, changed);
                return IntPtr.Zero;
            };
            window.SourceInitialized += delegate { source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle); if (source != null) source.AddHook(hook); };
            window.Closed += delegate { if (source != null && !source.IsDisposed) source.RemoveHook(hook); source = null; };
            window.IsEnabledChanged += delegate { changed(); };
        }
    }
}
