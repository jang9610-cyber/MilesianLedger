using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MabinogiBarter
{
    public struct PipRawKeyboardPacket
    {
        public ushort MakeCode, Flags, VirtualKey;
        public uint Message;
        // A normal mouse/HID message can share this HWND and must not clear held keyboard modifiers.
        public bool IsOtherDevice;
    }
    public struct PipRawKeyboardRegistration { public bool Exists; public IntPtr Window; public uint Flags; }
    public interface IPipCaptureHotkeyBackend
    {
        bool TryGetKeyboardRegistration(out PipRawKeyboardRegistration registration);
        bool RegisterKeyboard(IntPtr window);
        bool RemoveKeyboard();
        bool TryReadKeyboard(IntPtr input, out PipRawKeyboardPacket packet);
        int LastError { get; }
    }

    public sealed class PipCaptureHotkey : IDisposable
    {
        public const int DefaultVirtualKey = 0x24;
        public const uint RawKeyboardFlags = 0x0100 | 0x2000; // INPUTSINK | DEVNOTIFY; no exclusive/legacy suppression flags.
        const int WmInput = 0x00ff, WmInputDeviceChange = 0x00fe;
        readonly Window owner;
        readonly Action capture;
        readonly Action<string> statusChanged;
        readonly IPipCaptureHotkeyBackend backend;
        readonly HwndSourceHook hook;
        HwndSource source;
        IntPtr handle;
        bool disposed, ownsRegistration;
        bool leftAlt, rightAlt, leftControl, rightControl, leftShift, rightShift, leftWindows, rightWindows, triggerDown;
        public bool Enabled { get; private set; }
        public bool IsRegistered { get; private set; }
        public int VirtualKey { get; private set; }
        public string StatusText { get; private set; }
        public int CaptureShortcutCount { get; private set; }
        public DateTime? LastCaptureShortcutUtc { get; private set; }

        public PipCaptureHotkey(Window owner, Action capture, Action<string> statusChanged)
            : this(owner, capture, statusChanged, new WindowsRawInputBackend()) { }
        public PipCaptureHotkey(Window owner, Action capture, Action<string> statusChanged, IPipCaptureHotkeyBackend backend)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (capture == null) throw new ArgumentNullException("capture");
            if (backend == null) throw new ArgumentNullException("backend");
            owner.Dispatcher.VerifyAccess();
            this.owner = owner; this.capture = capture; this.statusChanged = statusChanged; this.backend = backend;
            VirtualKey = DefaultVirtualKey; StatusText = "전체 화면 촬영 단축키 꺼짐";
            hook = OnWindowMessage;
            owner.SourceInitialized += SourceInitialized; owner.Closed += WindowClosed;
            AttachExistingHandle();
        }
        public static bool IsAllowedVirtualKey(int key) { return key == 0x24 || key == 0x23 || key == 0x2D; }
        public static string ShortcutName(int key)
        {
            if (!IsAllowedVirtualKey(key)) throw new ArgumentOutOfRangeException("key");
            return "Alt+" + (key == 0x24 ? "Home" : key == 0x23 ? "End" : "Insert");
        }
        public void Configure(bool enabled, int virtualKey = DefaultVirtualKey)
        {
            owner.Dispatcher.VerifyAccess();
            if (disposed) throw new ObjectDisposedException("PipCaptureHotkey");
            if (!IsAllowedVirtualKey(virtualKey)) throw new ArgumentOutOfRangeException("virtualKey", "Alt+Home, Alt+End, Alt+Insert만 사용할 수 있습니다.");
            bool changed = enabled != Enabled || virtualKey != VirtualKey;
            bool sameKey = virtualKey == VirtualKey;
            Enabled = enabled; VirtualKey = virtualKey;
            if (changed) ResetKeyState(sameKey);
            if (!enabled) {
                bool released = ReleaseRegistration();
                Publish(released ? "전체 화면 촬영 단축키 꺼짐" : "단축키 입력 해제를 확인하지 못했습니다. PIP를 닫았다가 다시 열어 주세요."); return;
            }
            AttachExistingHandle();
            if (handle == IntPtr.Zero || source == null || source.IsDisposed) {
                Publish(ShortcutName(virtualKey) + " · 창이 준비되면 사용할 수 있습니다."); return;
            }
            PipRawKeyboardRegistration current;
            if (!backend.TryGetKeyboardRegistration(out current)) {
                IsRegistered = false; ResetKeyState(true);
                Publish("이 앱의 키보드 입력 등록 상태를 확인하지 못했습니다. 다시 연결해 주세요."); return;
            }
            if (current.Exists) {
                if (ownsRegistration && current.Window == handle && current.Flags == RawKeyboardFlags) {
                    IsRegistered = true; PublishReady(); return;
                }
                // A device class has only one target HWND per process. Never replace another feature's target.
                ownsRegistration = false; IsRegistered = false; ResetKeyState(true);
                Publish("이 앱의 다른 기능에서 키보드 입력을 사용 중입니다. 해당 기능을 닫은 뒤 다시 연결해 주세요."); return;
            }
            ownsRegistration = false; IsRegistered = false; ResetKeyState(true);
            if (!backend.RegisterKeyboard(handle)) {
                Publish(ShortcutName(virtualKey) + " · Raw Input을 등록하지 못했습니다. 다시 연결해 주세요."); return;
            }
            ownsRegistration = true; IsRegistered = true; PublishReady();
        }
        public void Retry() { owner.Dispatcher.VerifyAccess(); ResetKeyState(true); Configure(Enabled, VirtualKey); }
        void SourceInitialized(object sender, EventArgs args)
        { if (!disposed) { AttachExistingHandle(); if (Enabled) Configure(true, VirtualKey); } }
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
            ProcessMessage(hwnd, message, wParam, lParam);
            // RIM_INPUT must reach DefWindowProc for native cleanup. Never mark WM_INPUT handled.
            return IntPtr.Zero;
        }
        // Returns a shortcut match, not a native handled flag. Tests supply packets through a fake backend.
        public bool ProcessMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
        {
            owner.Dispatcher.VerifyAccess();
            if (disposed || !Enabled || !IsRegistered || hwnd != handle) return false;
            if (message == WmInputDeviceChange) { ResetKeyState(true); PublishReady(); return false; }
            if (message != WmInput || lParam == IntPtr.Zero || (wParam.ToInt64() != 0 && wParam.ToInt64() != 1)) return false;
            PipRawKeyboardPacket packet;
            // Native memory has already been freed when TryReadKeyboard returns.
            if (!backend.TryReadKeyboard(lParam, out packet)) { if (!packet.IsOtherDevice) ResetKeyState(true); return false; }
            return ProcessKeyboard(packet);
        }
        bool ProcessKeyboard(PipRawKeyboardPacket packet)
        {
            if (!ValidKeyboardPacket(packet)) { ResetKeyState(true); return false; }
            bool down = (packet.Flags & 1) == 0;
            int key = packet.VirtualKey;
            if (key == 0x12) key = (packet.Flags & 2) != 0 ? 0xA5 : 0xA4;
            if (key == 0x11) key = (packet.Flags & 2) != 0 ? 0xA3 : 0xA2;
            if (key == 0x10) key = packet.MakeCode == 0x36 ? 0xA1 : 0xA0;
            switch (key) {
                case 0xA4: leftAlt = down; return false; case 0xA5: rightAlt = down; return false;
                case 0xA2: leftControl = down; return false; case 0xA3: rightControl = down; return false;
                case 0xA0: leftShift = down; return false; case 0xA1: rightShift = down; return false;
                case 0x5B: leftWindows = down; return false; case 0x5C: rightWindows = down; return false;
            }
            // Other keys are not stored, translated to text, or logged.
            if (key != VirtualKey) return false;
            bool wasDown = triggerDown; triggerDown = down;
            if (!down || wasDown || !(leftAlt || rightAlt) || leftControl || rightControl || leftShift || rightShift || leftWindows || rightWindows) return false;
            if (CaptureShortcutCount < Int32.MaxValue) CaptureShortcutCount++;
            LastCaptureShortcutUtc = DateTime.UtcNow;
            Publish(ShortcutName(VirtualKey) + " 입력 수신 · " + CaptureShortcutCount + "회 · " + LastCaptureShortcutUtc.Value.ToLocalTime().ToString("HH:mm:ss"));
            if (!disposed && Enabled && IsRegistered) capture();
            return true;
        }
        void ResetKeyState(bool preserveHeldTrigger)
        {
            leftAlt = rightAlt = leftControl = rightControl = leftShift = rightShift = leftWindows = rightWindows = false;
            // Resetting a held key to false would make its next repeat appear to be a fresh press.
            if (!preserveHeldTrigger) triggerDown = false;
        }
        bool ReleaseRegistration()
        {
            IsRegistered = false; ResetKeyState(true);
            if (!ownsRegistration) return true;
            PipRawKeyboardRegistration current;
            if (!backend.TryGetKeyboardRegistration(out current)) return false;
            if (!current.Exists || current.Window != handle || current.Flags != RawKeyboardFlags) { ownsRegistration = false; return true; }
            if (!backend.RemoveKeyboard()) return false;
            ownsRegistration = false; return true;
        }
        void PublishReady() { Publish(ShortcutName(VirtualKey) + " · Raw Input 대기 · Alt와 지정 키를 뗀 뒤 눌러 주세요."); }
        void Publish(string status) { StatusText = status; if (statusChanged != null) statusChanged(status); }
        void WindowClosed(object sender, EventArgs args) { Dispose(); }
        public void Dispose()
        {
            owner.Dispatcher.VerifyAccess();
            if (disposed) return;
            disposed = true; Enabled = false; ReleaseRegistration();
            owner.SourceInitialized -= SourceInitialized; owner.Closed -= WindowClosed;
            if (source != null && !source.IsDisposed) source.RemoveHook(hook);
            source = null; handle = IntPtr.Zero; StatusText = "전체 화면 촬영 단축키 꺼짐";
        }
        static bool ValidKeyboardPacket(PipRawKeyboardPacket packet)
        {
            if (packet.IsOtherDevice || packet.VirtualKey == 0 || packet.VirtualKey >= 255 || packet.MakeCode == 255 || (packet.Flags & ~7) != 0) return false;
            return (packet.Flags & 1) != 0 ? packet.Message == 0x101 || packet.Message == 0x105 : packet.Message == 0x100 || packet.Message == 0x104;
        }
        public static bool TryParseKeyboard(byte[] bytes, int pointerSize, out PipRawKeyboardPacket packet)
        {
            packet = new PipRawKeyboardPacket();
            if (bytes == null || (pointerSize != 4 && pointerSize != 8)) return false;
            int headerSize = 8 + pointerSize * 2;
            if (bytes.Length < headerSize || bytes.Length > 4096) return false;
            uint size = BitConverter.ToUInt32(bytes, 4);
            if (size != bytes.Length) return false;
            ulong inputCode = pointerSize == 8 ? BitConverter.ToUInt64(bytes, 8 + pointerSize) : BitConverter.ToUInt32(bytes, 8 + pointerSize);
            if (inputCode > 1) return false;
            uint type = BitConverter.ToUInt32(bytes, 0);
            if (type == 0 || type == 2) { packet.IsOtherDevice = true; return false; }
            if (type != 1 || size < headerSize + 16) return false;
            if (BitConverter.ToUInt16(bytes, headerSize + 4) != 0) return false;
            packet.MakeCode = BitConverter.ToUInt16(bytes, headerSize); packet.Flags = BitConverter.ToUInt16(bytes, headerSize + 2);
            packet.VirtualKey = BitConverter.ToUInt16(bytes, headerSize + 6); packet.Message = BitConverter.ToUInt32(bytes, headerSize + 8);
            return ValidKeyboardPacket(packet);
        }
        sealed class WindowsRawInputBackend : IPipCaptureHotkeyBackend
        {
            public int LastError { get; private set; }
            public bool TryGetKeyboardRegistration(out PipRawKeyboardRegistration registration)
            {
                registration = new PipRawKeyboardRegistration();
                uint count = 0, entrySize = (uint)Marshal.SizeOf(typeof(RawInputDevice));
                uint queried = GetRegisteredRawInputDevices(IntPtr.Zero, ref count, entrySize);
                if (queried == UInt32.MaxValue && Marshal.GetLastWin32Error() != 122) { LastError = Marshal.GetLastWin32Error(); return false; }
                if (count == 0) return true;
                if (count > 256) { LastError = 87; return false; }
                IntPtr buffer = Marshal.AllocHGlobal(checked((int)(count * entrySize)));
                try {
                    uint capacity = count, read = GetRegisteredRawInputDevices(buffer, ref count, entrySize);
                    if (read == UInt32.MaxValue || read > capacity) { LastError = Marshal.GetLastWin32Error(); return false; }
                    for (int i = 0; i < read; i++) {
                        var entry = (RawInputDevice)Marshal.PtrToStructure(IntPtr.Add(buffer, checked(i * (int)entrySize)), typeof(RawInputDevice));
                        if (entry.UsagePage == 1 && entry.Usage == 6) {
                            registration = new PipRawKeyboardRegistration { Exists = true, Window = entry.Target, Flags = entry.Flags }; break;
                        }
                    }
                    LastError = 0; return true;
                } finally { Marshal.FreeHGlobal(buffer); }
            }
            public bool RegisterKeyboard(IntPtr window) { return SetKeyboardRegistration(window, RawKeyboardFlags); }
            public bool RemoveKeyboard() { return SetKeyboardRegistration(IntPtr.Zero, 0x0001); } // REMOVE requires NULL target.
            bool SetKeyboardRegistration(IntPtr window, uint flags)
            {
                var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = flags, Target = window };
                bool result = RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(RawInputDevice)));
                LastError = result ? 0 : Marshal.GetLastWin32Error(); return result;
            }
            public bool TryReadKeyboard(IntPtr input, out PipRawKeyboardPacket packet)
            {
                packet = new PipRawKeyboardPacket();
                uint size = 0, headerSize = (uint)(8 + IntPtr.Size * 2);
                if (GetRawInputData(input, 0x10000003, IntPtr.Zero, ref size, headerSize) != 0 || size < headerSize) return false;
                if (size > 4096) return ReadLargeOtherDeviceHeader(input, size, headerSize, out packet);
                IntPtr buffer = Marshal.AllocHGlobal((int)size); byte[] bytes = null;
                try {
                    uint capacity = size, read = GetRawInputData(input, 0x10000003, buffer, ref size, headerSize);
                    if (read == UInt32.MaxValue || read != size || read > capacity || read < headerSize) return false;
                    bytes = new byte[read]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    return TryParseKeyboard(bytes, IntPtr.Size, out packet);
                } finally {
                    if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                    Marshal.FreeHGlobal(buffer);
                }
            }
            bool ReadLargeOtherDeviceHeader(IntPtr input, uint totalSize, uint headerSize, out PipRawKeyboardPacket packet)
            {
                packet = new PipRawKeyboardPacket();
                IntPtr header = Marshal.AllocHGlobal((int)headerSize);
                try {
                    uint size = headerSize, read = GetRawInputData(input, 0x10000005, header, ref size, headerSize);
                    if (read != headerSize || size != headerSize) return false;
                    uint type = unchecked((uint)Marshal.ReadInt32(header, 0));
                    if (unchecked((uint)Marshal.ReadInt32(header, 4)) == totalSize && (type == 0 || type == 2)) packet.IsOtherDevice = true;
                    return false;
                } finally { Marshal.FreeHGlobal(header); }
            }
            [StructLayout(LayoutKind.Sequential)]
            struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
            [DllImport("user32.dll", SetLastError = true)]
            static extern uint GetRegisteredRawInputDevices(IntPtr devices, ref uint count, uint size);
            [DllImport("user32.dll", SetLastError = true)]
            static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        }
    }
}
