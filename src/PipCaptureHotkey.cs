using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace MabinogiBarter
{
    // An injectable boundary keeps verification away from real global shortcuts.
    public interface IPipCaptureHotkeyBackend
    {
        bool Register(IntPtr window, int id, uint modifiers, uint virtualKey);
        bool Unregister(IntPtr window, int id);
        int LastError { get; }
    }

    public sealed class PipCaptureHotkey : IDisposable
    {
        public const int DefaultVirtualKey = 0x24; // Home, not a function/skill key.
        const int WmHotkey = 0x0312;
        const uint ModAlt = 0x0001, ModNoRepeat = 0x4000;
        static int nextId;
        readonly Window owner;
        readonly Action capture;
        readonly Action<string> statusChanged;
        readonly IPipCaptureHotkeyBackend backend;
        readonly HwndSourceHook hook;
        HwndSource source;
        IntPtr handle;
        int registeredId, registeredVirtualKey;
        bool disposed;

        public bool Enabled { get; private set; }
        public bool IsRegistered { get; private set; }
        public int VirtualKey { get; private set; }
        public string StatusText { get; private set; }

        public PipCaptureHotkey(Window owner, Action capture, Action<string> statusChanged)
            : this(owner, capture, statusChanged, new WindowsHotkeyBackend()) { }

        public PipCaptureHotkey(Window owner, Action capture, Action<string> statusChanged,
            IPipCaptureHotkeyBackend backend)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (capture == null) throw new ArgumentNullException("capture");
            if (backend == null) throw new ArgumentNullException("backend");
            owner.Dispatcher.VerifyAccess();
            this.owner = owner; this.capture = capture; this.statusChanged = statusChanged; this.backend = backend;
            VirtualKey = DefaultVirtualKey;
            StatusText = "전체 화면 촬영 단축키 꺼짐";
            hook = OnWindowMessage;
            owner.SourceInitialized += SourceInitialized;
            owner.Closed += WindowClosed;
            AttachExistingHandle();
        }

        public static bool IsAllowedVirtualKey(int virtualKey)
        { return virtualKey == 0x24 || virtualKey == 0x23 || virtualKey == 0x2D; }

        public static string ShortcutName(int virtualKey)
        {
            if (!IsAllowedVirtualKey(virtualKey)) throw new ArgumentOutOfRangeException("virtualKey");
            return "Alt+" + (virtualKey == 0x24 ? "Home" : virtualKey == 0x23 ? "End" : "Insert");
        }

        public void Configure(bool enabled, int virtualKey = DefaultVirtualKey)
        {
            owner.Dispatcher.VerifyAccess();
            if (disposed) throw new ObjectDisposedException("PipCaptureHotkey");
            if (!IsAllowedVirtualKey(virtualKey)) throw new ArgumentOutOfRangeException("virtualKey", "Alt+Home, Alt+End, Alt+Insert만 사용할 수 있습니다.");
            Enabled = enabled; VirtualKey = virtualKey;
            if (IsRegistered && enabled && registeredVirtualKey == virtualKey) { Publish(ShortcutName(virtualKey) + " · 전체 화면 한 번 촬영"); return; }
            UnregisterCurrent();
            if (!enabled) { Publish("전체 화면 촬영 단축키 꺼짐"); return; }
            AttachExistingHandle();
            if (handle == IntPtr.Zero || source == null || source.IsDisposed) {
                Publish(ShortcutName(virtualKey) + " · 창이 준비되면 사용할 수 있습니다."); return;
            }
            // New IDs reject queued notifications from an earlier key/registration.
            int id = 0x4000 | (Interlocked.Increment(ref nextId) & 0x3fff);
            if (backend.Register(handle, id, ModAlt | ModNoRepeat, (uint)virtualKey)) {
                registeredId = id; registeredVirtualKey = virtualKey; IsRegistered = true;
                Publish(ShortcutName(virtualKey) + " · 전체 화면 한 번 촬영");
            } else {
                Publish(ShortcutName(virtualKey) + (backend.LastError == 1409
                    ? " · 다른 앱에서 사용 중입니다. 다른 키를 선택하거나 다시 시도해 주세요."
                    : " · 단축키를 등록하지 못했습니다. 다른 키를 선택하거나 다시 시도해 주세요."));
            }
        }

        public void Retry() { Configure(Enabled, VirtualKey); }

        void SourceInitialized(object sender, EventArgs args)
        {
            if (disposed) return;
            AttachExistingHandle();
            if (Enabled) Configure(true, VirtualKey);
        }

        void AttachExistingHandle()
        {
            if (source != null || disposed) return;
            IntPtr existing = new WindowInteropHelper(owner).Handle;
            if (existing == IntPtr.Zero) return;
            HwndSource existingSource = HwndSource.FromHwnd(existing);
            if (existingSource == null || existingSource.IsDisposed) return;
            handle = existing; source = existingSource; source.AddHook(hook);
        }

        IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (ProcessMessage(hwnd, message, wParam, lParam)) handled = true;
            return IntPtr.Zero;
        }

        // Only our own HWND's current WM_HOTKEY registration can trigger capture.
        // This entry point also lets tests deliver synthetic messages without key injection.
        public bool ProcessMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
        {
            owner.Dispatcher.VerifyAccess();
            if (disposed || !Enabled || !IsRegistered || hwnd != handle || message != WmHotkey ||
                wParam.ToInt64() != registeredId) return false;
            long payload = lParam.ToInt64();
            int modifiers = (int)(payload & 0xffff), virtualKey = (int)((payload >> 16) & 0xffff);
            // WM_HOTKEY reports actual modifiers, without the registration-only NOREPEAT flag.
            if (modifiers != ModAlt || virtualKey != registeredVirtualKey) return false;
            capture(); return true;
        }

        void UnregisterCurrent()
        {
            if (!IsRegistered) return;
            int id = registeredId;
            IsRegistered = false; registeredId = 0; registeredVirtualKey = 0;
            backend.Unregister(handle, id);
        }

        void Publish(string status)
        {
            StatusText = status;
            if (statusChanged != null) statusChanged(status);
        }

        void WindowClosed(object sender, EventArgs args) { Dispose(); }

        public void Dispose()
        {
            owner.Dispatcher.VerifyAccess();
            if (disposed) return;
            disposed = true; Enabled = false;
            UnregisterCurrent();
            owner.SourceInitialized -= SourceInitialized; owner.Closed -= WindowClosed;
            if (source != null && !source.IsDisposed) source.RemoveHook(hook);
            source = null; handle = IntPtr.Zero;
            StatusText = "전체 화면 촬영 단축키 꺼짐";
        }

        sealed class WindowsHotkeyBackend : IPipCaptureHotkeyBackend
        {
            public int LastError { get; private set; }
            public bool Register(IntPtr window, int id, uint modifiers, uint virtualKey)
            {
                bool result = RegisterHotKey(window, id, modifiers, virtualKey);
                LastError = result ? 0 : Marshal.GetLastWin32Error(); return result;
            }
            public bool Unregister(IntPtr window, int id) { return UnregisterHotKey(window, id); }
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        }
    }
}
