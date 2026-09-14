using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabinogiBarter
{
    // Only ordinary desktop pixels are read. No target application window,
    // process, memory, injected input, or global input hook is involved.
    public static class ScreenRegionCapture
    {
        static int running;

        public static Task<BitmapSource> CaptureAsync(Window owner, CancellationToken token)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (token.IsCancellationRequested || owner.Dispatcher.HasShutdownStarted || owner.Dispatcher.HasShutdownFinished)
                return Task.FromResult<BitmapSource>(null);
            if (!owner.Dispatcher.CheckAccess())
                return owner.Dispatcher.InvokeAsync(() => CaptureAsync(owner, token)).Task.Unwrap();
            return CaptureOnUiAsync(owner, token);
        }

        static async Task<BitmapSource> CaptureOnUiAsync(Window owner, CancellationToken token)
        {
            if (token.IsCancellationRequested || !owner.IsLoaded) return null;
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                throw new InvalidOperationException("이미 화면 영역을 선택하고 있습니다.");
            var windows = new List<WindowStateRecord>();
            BitmapSource desktop = null;
            RegionSelector selector = null;
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                EventHandler ownerClosed = delegate {
                    foreach (var record in windows) if (record.Window == owner) record.Closed = true;
                    if (selector != null) selector.CancelAndRelease();
                    lifetime.Cancel();
                };
                owner.Closed += ownerClosed;
                try {
                    // Capture the entire list before Hide changes owned-window
                    // visibility. Minimized windows never need hiding or showing.
                    if (Application.Current != null) foreach (Window window in Application.Current.Windows)
                        if (window.Dispatcher == owner.Dispatcher && window.IsVisible && window.WindowState != WindowState.Minimized)
                            windows.Add(new WindowStateRecord(window));
                    if (!windows.Any(record => record.Window == owner) && owner.IsVisible && owner.WindowState != WindowState.Minimized)
                        windows.Add(new WindowStateRecord(owner));
                    foreach (var record in windows.OrderByDescending(record => record.OwnerDepth)) record.Hide();
                    await owner.Dispatcher.InvokeAsync(delegate { }, DispatcherPriority.ContextIdle);
                    await Task.Delay(100, lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    FlushDesktop();
                    Int32Rect bounds;
                    using (new PhysicalDpiScope()) {
                        bounds = DesktopBounds();
                        desktop = ReadDesktop(bounds);
                    }
                    lifetime.Token.ThrowIfCancellationRequested();
                    selector = new RegionSelector(desktop, bounds);
                    desktop = null;
                    using (lifetime.Token.Register(delegate { selector.CancelFromAnyThread(); })) {
                        if (lifetime.IsCancellationRequested) return null;
                        selector.ShowAtPhysicalBounds();
                        return await selector.Completion;
                    }
                } catch (OperationCanceledException) { return null; }
                finally {
                    Exception restoreFailure = null;
                    try {
                        owner.Closed -= ownerClosed;
                        try { if (selector != null) selector.CancelAndRelease(); } catch (Exception ex) { restoreFailure = ex; }
                        desktop = null;
                        // One already-closed or failed window must not prevent
                        // restoration of the remaining windows or the guard.
                        foreach (var record in windows.OrderBy(record => record.OwnerDepth)) {
                            try { record.Restore(); } catch (Exception ex) { if (restoreFailure == null) restoreFailure = ex; }
                        }
                        var active = windows.FirstOrDefault(record => record.WasActive && !record.Closed);
                        try {
                            if (active != null && active.Window.IsVisible && active.Window.WindowState != WindowState.Minimized)
                                active.Window.Activate(); // Only our own previously active window.
                        } catch (Exception ex) { if (restoreFailure == null) restoreFailure = ex; }
                    } finally {
                        foreach (var record in windows) record.Detach();
                        Interlocked.Exchange(ref running, 0);
                    }
                    if (restoreFailure != null) throw new InvalidOperationException("화면 선택 후 앱 창을 복원하지 못했습니다.", restoreFailure);
                }
            }
        }

        sealed class WindowStateRecord
        {
            public readonly Window Window;
            public readonly WindowState State;
            public readonly bool WasActive;
            public readonly int OwnerDepth;
            public bool Closed;
            bool hidden;
            public WindowStateRecord(Window window)
            {
                Window = window; State = window.WindowState; WasActive = window.IsActive;
                for (var parent = window.Owner; parent != null; parent = parent.Owner) OwnerDepth++;
                window.Closed += WindowClosed;
            }
            void WindowClosed(object sender, EventArgs e) { Closed = true; }
            public void Hide()
            {
                if (Closed) return;
                // An owner may already have hidden this owned window.
                hidden = true;
                if (Window.IsVisible) Window.Hide();
            }
            public void Restore()
            {
                if (!hidden || Closed || Window.Dispatcher.HasShutdownStarted || Window.Dispatcher.HasShutdownFinished) return;
                bool activated = Window.ShowActivated;
                try {
                    Window.ShowActivated = false;
                    if (Window.WindowState != State) Window.WindowState = State;
                    if (!Window.IsVisible) Window.Show();
                } catch (InvalidOperationException) { if (!Closed) throw; }
                finally { if (!Closed) Window.ShowActivated = activated; }
            }
            public void Detach() { Window.Closed -= WindowClosed; }
        }

        sealed class RegionSelector : Window
        {
            readonly Int32Rect desktopBounds;
            readonly SelectionSurface surface;
            // Finish mouse/Closed event dispatch before the caller restores its
            // windows; an inline continuation could re-show an owner mid-close.
            readonly TaskCompletionSource<BitmapSource> completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            Point first;
            bool dragging, finished, ready, cancelPending;
            public Task<BitmapSource> Completion { get { return completion.Task; } }

            public RegionSelector(BitmapSource snapshot, Int32Rect bounds)
            {
                desktopBounds = bounds;
                WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
                AllowsTransparency = false; Background = Brushes.Black;
                ShowInTaskbar = false; ShowActivated = true; Topmost = true;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = 0; Top = 0; Width = 100; Height = 100;
                UseLayoutRounding = true; SnapsToDevicePixels = true;
                Title = "밀레시안 장부 · 아이템 이름 영역 선택";
                surface = new SelectionSurface(snapshot); Content = surface;
                surface.Cursor = Cursors.Cross; surface.Focusable = true;
                PreviewMouseLeftButtonDown += BeginSelection;
                PreviewMouseMove += MoveSelection;
                PreviewMouseLeftButtonUp += EndSelection;
                PreviewMouseDown += delegate(object sender, MouseButtonEventArgs e) {
                    if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right) return;
                    e.Handled = true; RequestCancel();
                };
                PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; RequestCancel(); };
                PreviewMouseUp += delegate(object sender, MouseButtonEventArgs e) {
                    if (!cancelPending || finished) return;
                    e.Handled = true;
                    if (!AnyButtonDown()) CancelAndRelease();
                };
                PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; RequestCancel(); } };
                LostMouseCapture += delegate { if ((dragging || cancelPending) && !finished) CancelAndRelease(); };
                Deactivated += delegate { if (!finished) CancelAndRelease(); };
                Closing += delegate { Complete(null, null, false); };
                Closed += delegate { Complete(null, null, false); };
                ContentRendered += delegate {
                    if (finished) return;
                    try {
                        ValidatePhysicalBounds(); ready = true;
                        if (!IsActive && !Activate()) { CancelAndRelease(); return; }
                        Keyboard.Focus(surface);
                    } catch (Exception ex) { Complete(null, ex, true); }
                };
            }

            public void ShowAtPhysicalBounds()
            {
                if (finished) return;
                using (new PhysicalDpiScope()) {
                    // A single opaque surface uses one physical-pixel image for
                    // all monitors. Place its client rectangle through Win32;
                    // WPF DIP sizes are used only when painting that rectangle.
                    new WindowInteropHelper(this).EnsureHandle();
                    Position();
                    Show();
                    Position();
                }
            }
            void Position()
            {
                if (!SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), desktopBounds.X, desktopBounds.Y,
                    desktopBounds.Width, desktopBounds.Height, 0x0010))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "화면 영역 선택 창을 배치하지 못했습니다.");
            }
            void ValidatePhysicalBounds()
            {
                using (new PhysicalDpiScope()) {
                    if (!desktopBounds.Equals(DesktopBounds())) throw new InvalidOperationException("모니터 구성이 바뀌었습니다. 다시 영역을 선택하세요.");
                    RECT rectangle; var origin = new POINT(); IntPtr handle = new WindowInteropHelper(this).Handle;
                    if (!GetClientRect(handle, out rectangle) || !ClientToScreen(handle, ref origin)
                        || origin.X != desktopBounds.X || origin.Y != desktopBounds.Y
                        || rectangle.Right - rectangle.Left != desktopBounds.Width || rectangle.Bottom - rectangle.Top != desktopBounds.Height)
                        throw new InvalidOperationException("화면 배율에 맞는 캡처 영역을 확인하지 못했습니다.");
                    if (surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
                        throw new InvalidOperationException("화면 영역 선택 창을 표시하지 못했습니다.");
                }
            }
            Point CursorPosition()
            {
                POINT point;
                if (!GetPhysicalCursorPos(out point)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return new Point(point.X, point.Y);
            }
            void BeginSelection(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (!ready || finished || cancelPending) return;
                try {
                    first = CursorPosition(); dragging = true;
                    if (!Mouse.Capture(surface, CaptureMode.Element)) { CancelAndRelease(); return; }
                    surface.SetSelection(new Int32Rect(), "아이템 이름이 보이는 영역을 드래그하세요 · Esc / 오른쪽 클릭: 취소");
                } catch (Exception ex) { Complete(null, ex, true); }
            }
            void MoveSelection(object sender, MouseEventArgs e)
            {
                if (!dragging || finished || cancelPending) return;
                e.Handled = true;
                if (e.LeftButton != MouseButtonState.Pressed) { CancelAndRelease(); return; }
                try {
                    Int32Rect crop; ScreenRegionCaptureGeometry.TryGetCrop(desktopBounds, first, CursorPosition(), out crop);
                    surface.SetSelection(crop, "아이템 이름만 포함하세요 · " + crop.Width + " × " + crop.Height + " px · Esc: 취소");
                } catch (Exception ex) { Complete(null, ex, true); }
            }
            void EndSelection(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (!dragging || finished || cancelPending) return;
                try {
                    ValidatePhysicalBounds();
                    Int32Rect crop; bool valid = ScreenRegionCaptureGeometry.TryGetCrop(desktopBounds, first, CursorPosition(), out crop);
                    ReleaseDrag();
                    if (!valid) { surface.SetSelection(crop, "가로·세로 16 px 이상을 드래그하세요 · Esc / 오른쪽 클릭: 취소"); return; }
                    Complete(ScreenRegionCaptureGeometry.CopyCrop(surface.Snapshot, crop), null, true);
                } catch (Exception ex) { Complete(null, ex, true); }
            }
            void ReleaseDrag()
            {
                dragging = false; cancelPending = false;
                if (Mouse.Captured == surface) Mouse.Capture(null);
            }
            static bool AnyButtonDown()
            {
                return Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed
                    || Mouse.MiddleButton == MouseButtonState.Pressed || Mouse.XButton1 == MouseButtonState.Pressed || Mouse.XButton2 == MouseButtonState.Pressed;
            }
            void RequestCancel()
            {
                if (finished) return;
                if (!IsVisible || !IsActive || !AnyButtonDown()) { CancelAndRelease(); return; }
                // Escape during a drag and right-click cancellation retain the
                // opaque surface and capture until their mouse-up is consumed.
                dragging = false; cancelPending = true;
                surface.SetSelection(Int32Rect.Empty, "마우스 버튼을 놓으면 영역 선택을 취소합니다.");
                if (Mouse.Captured != surface && !Mouse.Capture(surface, CaptureMode.Element)) CancelAndRelease();
            }
            public void CancelFromAnyThread()
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                if (Dispatcher.CheckAccess()) RequestCancel();
                else try { Dispatcher.BeginInvoke(new Action(RequestCancel)); } catch (InvalidOperationException) { }
            }
            public void CancelAndRelease() { Complete(null, null, true); }
            void Complete(BitmapSource result, Exception error, bool close)
            {
                if (finished) return;
                finished = true; ReleaseDrag(); surface.Release(); Content = null;
                if (close) Close();
                if (error != null) completion.TrySetException(error); else completion.TrySetResult(result);
            }
        }

        sealed class SelectionSurface : FrameworkElement
        {
            public BitmapSource Snapshot { get; private set; }
            Int32Rect selection;
            string instruction = "아이템 이름이 보이는 영역을 드래그하세요 · Esc / 오른쪽 클릭: 취소";
            public SelectionSurface(BitmapSource snapshot) { Snapshot = snapshot; }
            public void SetSelection(Int32Rect value, string message) { selection = value; instruction = message; InvalidateVisual(); }
            public void Release() { Snapshot = null; }
            protected override void OnRender(DrawingContext drawing)
            {
                base.OnRender(drawing);
                var snapshot = Snapshot; if (snapshot == null || ActualWidth <= 0 || ActualHeight <= 0) return;
                var area = new Rect(0, 0, ActualWidth, ActualHeight);
                drawing.DrawImage(snapshot, area);
                var dim = new SolidColorBrush(Color.FromArgb(85, 0, 0, 0));
                var chosen = ScreenRegionCaptureGeometry.ToViewRect(selection, new Size(snapshot.PixelWidth, snapshot.PixelHeight), RenderSize);
                if (chosen.IsEmpty || chosen.Width <= 0 || chosen.Height <= 0) drawing.DrawRectangle(dim, null, area);
                else {
                    drawing.DrawRectangle(dim, null, new Rect(0, 0, ActualWidth, chosen.Top));
                    drawing.DrawRectangle(dim, null, new Rect(0, chosen.Bottom, ActualWidth, Math.Max(0, ActualHeight - chosen.Bottom)));
                    drawing.DrawRectangle(dim, null, new Rect(0, chosen.Top, chosen.Left, chosen.Height));
                    drawing.DrawRectangle(dim, null, new Rect(chosen.Right, chosen.Top, Math.Max(0, ActualWidth - chosen.Right), chosen.Height));
                    drawing.DrawRectangle(null, new Pen(Brushes.LimeGreen, 2), chosen);
                }
                var text = new FormattedText(instruction, System.Globalization.CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                    new Typeface("Malgun Gothic"), 16, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                text.MaxTextWidth = Math.Max(100, Math.Min(680, ActualWidth - 48));
                // The hint stays near the pointer inside the opaque selector.
                // It never affects the copied source pixels.
                var local = Mouse.GetPosition(this);
                double x = Math.Max(16, Math.Min(ActualWidth - text.Width - 32, local.X + 20));
                double y = Math.Max(16, Math.Min(ActualHeight - text.Height - 32, local.Y + 24));
                drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 28, 38, 35)), null,
                    new Rect(x - 10, y - 7, text.Width + 20, text.Height + 14), 6, 6);
                drawing.DrawText(text, new Point(x, y));
            }
        }

        sealed class PhysicalDpiScope : IDisposable
        {
            readonly IntPtr previous;
            public PhysicalDpiScope()
            {
                try {
                    previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
                    if (previous == IntPtr.Zero) previous = SetThreadDpiAwarenessContext(new IntPtr(-3));
                    if (previous == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "물리 화면 배율을 확인하지 못했습니다.");
                } catch (EntryPointNotFoundException) { throw new PlatformNotSupportedException("화면 영역 선택에는 Windows 10 이상이 필요합니다."); }
            }
            public void Dispose() { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }

        static Int32Rect DesktopBounds()
        {
            var bounds = new Int32Rect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            ScreenRegionCaptureGeometry.ValidateDesktop(bounds); return bounds;
        }
        static void FlushDesktop()
        {
            try { DwmFlush(); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        static BitmapSource ReadDesktop(Int32Rect bounds)
        {
            IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, original = IntPtr.Zero;
            try {
                screen = GetDC(IntPtr.Zero); if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                memory = CreateCompatibleDC(screen); if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height); if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                original = SelectObject(memory, bitmap);
                if (original == IntPtr.Zero || original == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x00CC0020 | 0x40000000))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "일반 화면 캡처를 읽지 못했습니다.");
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze(); return source;
            }
            finally {
                if (memory != IntPtr.Zero && original != IntPtr.Zero && original != new IntPtr(-1)) SelectObject(memory, original);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetPhysicalCursorPos(out POINT point);
        [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(IntPtr window, out RECT rectangle);
        [DllImport("user32.dll", SetLastError = true)] static extern bool ClientToScreen(IntPtr window, ref POINT point);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
    }

    // Pure geometry and detached-pixel helpers are independently testable with
    // synthetic images. They never access the desktop, clipboard, or filesystem.
    public static class ScreenRegionCaptureGeometry
    {
        public const long MaximumPixels = 64000000;
        public const int MinimumSide = 16;

        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        public static void ValidateDesktop(Int32Rect bounds)
        {
            if (bounds.Width < MinimumSide || bounds.Height < MinimumSide || (long)bounds.Width * bounds.Height > MaximumPixels
                || (long)bounds.X + bounds.Width > Int32.MaxValue || (long)bounds.Y + bounds.Height > Int32.MaxValue)
                throw new ArgumentOutOfRangeException("bounds", "캡처 가능한 화면 크기는 최대 6,400만 픽셀입니다.");
        }
        public static bool TryGetCrop(Int32Rect desktop, Point first, Point last, out Int32Rect crop)
        {
            ValidateDesktop(desktop); crop = Int32Rect.Empty;
            if (!Finite(first.X) || !Finite(first.Y) || !Finite(last.X) || !Finite(last.Y)) return false;
            double left = Math.Max(desktop.X, Math.Min((double)desktop.X + desktop.Width, Math.Min(first.X, last.X)));
            double top = Math.Max(desktop.Y, Math.Min((double)desktop.Y + desktop.Height, Math.Min(first.Y, last.Y)));
            double right = Math.Max(desktop.X, Math.Min((double)desktop.X + desktop.Width, Math.Max(first.X, last.X)));
            double bottom = Math.Max(desktop.Y, Math.Min((double)desktop.Y + desktop.Height, Math.Max(first.Y, last.Y)));
            int x = (int)Math.Floor(left - desktop.X), y = (int)Math.Floor(top - desktop.Y);
            int endX = (int)Math.Ceiling(right - desktop.X), endY = (int)Math.Ceiling(bottom - desktop.Y);
            crop = new Int32Rect(x, y, endX - x, endY - y);
            return crop.Width >= MinimumSide && crop.Height >= MinimumSide;
        }
        public static Rect ToViewRect(Int32Rect crop, Size pixels, Size view)
        {
            if (pixels.IsEmpty || view.IsEmpty || !Finite(pixels.Width) || !Finite(pixels.Height) || !Finite(view.Width) || !Finite(view.Height)
                || pixels.Width <= 0 || pixels.Height <= 0 || view.Width <= 0 || view.Height <= 0
                || crop.X < 0 || crop.Y < 0 || (long)crop.X + crop.Width > pixels.Width || (long)crop.Y + crop.Height > pixels.Height)
                throw new ArgumentOutOfRangeException("view", "화면 좌표 변환이 올바르지 않습니다.");
            double scaleX = view.Width / pixels.Width, scaleY = view.Height / pixels.Height;
            double x = crop.X * scaleX, y = crop.Y * scaleY, width = crop.Width * scaleX, height = crop.Height * scaleY;
            if (!Finite(scaleX) || !Finite(scaleY) || !Finite(x) || !Finite(y) || !Finite(width) || !Finite(height))
                throw new ArgumentOutOfRangeException("view", "화면 좌표 변환이 올바르지 않습니다.");
            return new Rect(x, y, width, height);
        }
        public static BitmapSource CopyCrop(BitmapSource source, Int32Rect crop)
        {
            if (source == null) throw new ArgumentNullException("source");
            ValidateDesktop(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight));
            if (source.Format.BitsPerPixel < 1 || source.Format.BitsPerPixel > 32) throw new ArgumentException("지원하지 않는 화면 픽셀 형식입니다.", "source");
            if (crop.X < 0 || crop.Y < 0 || crop.Width < MinimumSide || crop.Height < MinimumSide
                || (long)crop.X + crop.Width > source.PixelWidth || (long)crop.Y + crop.Height > source.PixelHeight)
                throw new ArgumentOutOfRangeException("crop");
            int stride = checked((crop.Width * source.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[checked(stride * crop.Height)];
            source.CopyPixels(crop, pixels, stride, 0);
            // CroppedBitmap retains its Source; a fresh bitmap instead releases
            // the full desktop as soon as the selector drops its only reference.
            var result = BitmapSource.Create(crop.Width, crop.Height, 96, 96, source.Format, source.Palette, pixels, stride);
            result.Freeze(); return result;
        }
    }
}
