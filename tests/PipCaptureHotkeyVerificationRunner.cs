using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using MabinogiBarter;

public static class PipCaptureHotkeyVerificationRunner
{
    static int checks;
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            VerifyKeysAndEdges(); VerifyModifierSides(); VerifyLifetimeAndOwnership(); VerifyParser(); VerifyMessagesAndFailures();
            string summary = "PASS " + checks + " Raw Input shortcut checks. Fake registration/read backend and synthetic 32/64-bit keyboard packets only; no real keyboard registration, key input, desktop capture, or game access.";
            Console.WriteLine(summary); File.WriteAllText(Path.Combine(args[0], "report.txt"), summary, Encoding.UTF8);
            app.Shutdown(); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void VerifyKeysAndEdges()
    {
        using (var f = new Fixture(false)) {
            Check(!f.Hotkey.Enabled && !f.Hotkey.IsRegistered && f.Hotkey.VirtualKey == 0x24, "default disabled Home");
            Check(f.Backend.RegisterCount == 0 && f.Backend.QueryCount == 0, "default off never registers or enumerates input");
            f.Hotkey.Configure(true);
            Check(!f.Hotkey.IsRegistered && f.Hotkey.StatusText.Contains("창이 준비"), "registration deferred before HWND");
            f.EnsureHandle();
            Check(f.Hotkey.IsRegistered && f.Backend.RegisterCount == 1, "source initialization registers once");
            Check(f.Backend.Current.Window == f.Handle && f.Backend.Current.Flags == 0x2100, "own HWND INPUTSINK DEVNOTIFY only");
            Check(!f.Send(0x24), "Home alone never captures"); f.Send(0x24, true);
            f.Send(0x12); Check(f.Send(0x24), "Alt then Home triggers");
            Check(f.Captures == 1 && f.Hotkey.CaptureShortcutCount == 1 && f.Hotkey.LastCaptureShortcutUtc.HasValue, "shortcut diagnostics updated");
            Check(f.StatusBeforeCapture.Contains("Alt+Home 입력 수신"), "diagnostic reported before callback");
            for (int i = 0; i < 20; i++) Check(!f.Send(0x24), "held Home repeat suppressed");
            Check(f.Captures == 1, "held shortcut captured exactly once");
            f.Send(0x24, true); Check(f.Send(0x24), "Home release rearms while Alt stays held");
            f.Send(0x12, true); f.Send(0x12); Check(!f.Send(0x24), "repressing Alt while Home held does not manufacture Home edge");
            f.Send(0x24, true); Check(f.Send(0x24), "next genuine Home press accepted");
            f.Send(0x24, true); f.Send(0x12, true);
            f.Send(0x24); f.Send(0x12); Check(!f.Send(0x24), "Home before Alt needs release first");
            f.Send(0x24, true); Check(f.Send(0x24, false, 2), "extended E0 Home accepted");
            f.Send(0x24, true, 2); Check(f.Send(0x24), "nonextended Home virtual key accepted");
            f.Hotkey.Retry(); f.Send(0x12); Check(!f.Send(0x24), "retry preserves held trigger repeat suppression");
            f.Send(0x24, true); Check(f.Send(0x24), "retry resumes after release");
            f.DeviceChange(); f.Send(0x12); Check(!f.Send(0x24), "device change preserves held trigger guard");
            f.Send(0x24, true); Check(f.Send(0x24), "device change resumes with fresh trigger");
            int count = f.Captures;
            f.Hotkey.Configure(false); f.Send(0x12); f.Send(0x24);
            Check(f.Captures == count && !f.Hotkey.IsRegistered, "disabled ignores raw input");
            f.Hotkey.Configure(true); f.Send(0x24, true); f.Send(0x12, true);
            Check(f.Captures == count, "key releases after toggle never capture");
            f.Send(0x12); Check(f.Send(0x24), "fresh gesture after toggle works");
            f.Send(0x24, true); f.Hotkey.Configure(true, 0x23); f.Send(0x12);
            Check(!f.Send(0x24) && f.Send(0x23), "End selection ignores old Home");
            f.Send(0x23, true); f.Hotkey.Configure(true, 0x2D); f.Send(0x12);
            Check(f.Send(0x2D), "Insert selection works");
            Check(f.Backend.RegisterCount == 2, "key changes do not duplicate process registration");
            foreach (int key in new[] { 0, 0x41, 0x70, 0x77, 0x78, 0x79, 0x7B, 0x2E }) {
                Check(!PipCaptureHotkey.IsAllowedVirtualKey(key), "unapproved key rejected");
                try { f.Hotkey.Configure(true, key); throw new Exception("Unapproved key accepted"); }
                catch (ArgumentOutOfRangeException) { checks++; }
            }
        }
    }
    static void VerifyModifierSides()
    {
        using (var f = new Fixture()) {
            f.Send(0x12); f.Send(0x12, false, 2); f.Send(0x12, true);
            Check(f.Send(0x24), "right Alt survives left Alt release"); f.Send(0x24, true);
            f.Send(0x12, true, 2); Check(!f.Send(0x24), "both Alt released blocks"); f.Send(0x24, true);
            f.Send(0xA4); Check(f.Send(0x24), "explicit left Alt VK accepted"); f.Send(0x24, true); f.Send(0xA4, true);
            f.Send(0xA5); Check(f.Send(0x24), "explicit right Alt VK accepted"); f.Send(0x24, true); f.Send(0xA5, true);
            foreach (int modifier in new[] { 0xA2, 0xA3, 0xA0, 0xA1, 0x5B, 0x5C }) {
                f.Send(0xA4); f.Send(modifier); Check(!f.Send(0x24), "extra modifier blocks exact Alt chord");
                f.Send(modifier, true); Check(!f.Send(0x24), "modifier release with held Home does not capture");
                f.Send(0x24, true); Check(f.Send(0x24), "fresh Home after guard release works");
                f.Send(0x24, true); f.Send(0xA4, true);
            }
            f.Send(0x12); f.Send(0x11); f.Send(0x11, false, 2); f.Send(0x11, true);
            Check(!f.Send(0x24), "right Ctrl survives left Ctrl release"); f.Send(0x24, true); f.Send(0x11, true, 2);
            f.Send(0x10, false, 0, 0x2A); f.Send(0x10, false, 0, 0x36); f.Send(0x10, true, 0, 0x2A);
            Check(!f.Send(0x24), "right Shift survives left Shift release"); f.Send(0x24, true); f.Send(0x10, true, 0, 0x36);
            Check(f.Send(0x24), "both Shift released allows shortcut"); f.Send(0x24, true);
            f.Send(0x11); f.Send(0x12, false, 2); Check(!f.Send(0x24), "AltGr Ctrl+right Alt does not capture");
            f.Send(0x24, true); f.Send(0x11, true); f.Send(0x12, true, 2);
            int count = f.Captures; DateTime? time = f.Hotkey.LastCaptureShortcutUtc;
            for (int key = 0x41; key <= 0x5A; key++) { f.Send(key); f.Send(key, true); }
            Check(f.Captures == count && f.Hotkey.LastCaptureShortcutUtc == time, "ordinary typing changes neither capture count nor timestamp");
        }
    }
    static void VerifyLifetimeAndOwnership()
    {
        var backend = new FakeBackend(); var first = new Fixture(true, backend); var second = new Fixture(true, backend);
        Check(first.Hotkey.IsRegistered && !second.Hotkey.IsRegistered && backend.RegisterCount == 1, "second helper cannot clobber keyboard owner");
        Check(second.Hotkey.StatusText.Contains("다른 기능"), "ownership conflict status");
        second.Dispose(); Check(backend.RemoveCount == 0 && backend.Current.Window == first.Handle, "nonowner dispose cannot unregister first helper");
        first.Dispose(); Check(backend.RemoveCount == 1 && !backend.Current.Exists, "owner dispose releases keyboard");
        first.Dispose(); Check(backend.RemoveCount == 1, "dispose idempotent");
        using (var f = new Fixture()) {
            f.Backend.Current = new PipRawKeyboardRegistration { Exists = true, Window = new IntPtr(999), Flags = PipCaptureHotkey.RawKeyboardFlags };
            f.Hotkey.Configure(false);
            Check(f.Backend.RemoveCount == 0 && f.Backend.Current.Window == new IntPtr(999), "replacement owner's registration preserved on disable");
        }
        using (var f = new Fixture()) {
            f.Backend.Current = new PipRawKeyboardRegistration { Exists = true, Window = f.Handle, Flags = 0x100 };
            f.Hotkey.Configure(false); Check(f.Backend.RemoveCount == 0, "changed flags at same HWND are not removed");
        }
        using (var f = new Fixture()) {
            var sameWindow = new PipCaptureHotkey(f.Window, delegate { throw new Exception("Nonowner captured"); }, null, f.Backend);
            sameWindow.Configure(true);
            Check(!sameWindow.IsRegistered && f.Backend.RegisterCount == 1, "second helper on same HWND cannot adopt or replace registration");
            sameWindow.Dispose(); Check(f.Backend.RemoveCount == 0 && f.Hotkey.IsRegistered, "same-HWND nonowner cannot remove owner registration");
        }
        using (var f = new Fixture(false)) {
            f.Hotkey.Configure(true); f.Hotkey.Dispose(); f.EnsureHandle();
            Check(f.Backend.RegisterCount == 0, "disposed deferred helper detaches SourceInitialized");
        }
        var closed = new Fixture(); closed.Window.Close();
        Check(!closed.Hotkey.IsRegistered && !closed.Hotkey.Enabled && closed.Backend.RemoveCount == 1, "window close releases subscription");
        Check(!closed.Send(0x24), "late input after close ignored"); closed.Dispose();
    }
    static void VerifyMessagesAndFailures()
    {
        using (var f = new Fixture()) {
            int reads = f.Backend.ReadCount;
            foreach (int message in new[] { 0x100, 0x104, 0x312 }) Check(!f.Hotkey.ProcessMessage(f.Handle, message, IntPtr.Zero, new IntPtr(1)), "ordinary/old hotkey messages ignored");
            Check(!f.Hotkey.ProcessMessage(IntPtr.Zero, 0xff, IntPtr.Zero, new IntPtr(1)), "foreign HWND ignored");
            Check(!f.Hotkey.ProcessMessage(f.Handle, 0xff, new IntPtr(2), new IntPtr(1)), "invalid raw input code ignored");
            Check(!f.Hotkey.ProcessMessage(f.Handle, 0xff, IntPtr.Zero, IntPtr.Zero), "null HRAWINPUT ignored");
            Check(f.Backend.ReadCount == reads, "invalid messages never read native data");
            f.Send(0x12);
            f.Backend.Pending = Packet(0x24); bool handled = false;
            var hook = (HwndSourceHook)typeof(PipCaptureHotkey).GetField("hook", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Hotkey);
            hook(f.Handle, 0xff, IntPtr.Zero, new IntPtr(1), ref handled);
            Check(f.Captures == 1 && !handled, "foreground raw input captured without blocking DefWindowProc cleanup");
            f.Send(0x24, true); f.Backend.ReadFailure = true; f.Send(0x12); f.Backend.ReadFailure = false;
            Check(!f.Send(0x24), "unreadable packet clears stale Alt state"); f.Send(0x24, true);
            f.Send(0x12); f.Backend.Pending = new PipRawKeyboardPacket { IsOtherDevice = true };
            Check(!f.Hotkey.ProcessMessage(f.Handle, 0xff, new IntPtr(1), new IntPtr(1)), "other raw device is ignored");
            Check(f.Send(0x24), "mouse or HID packet between Alt and Home preserves modifiers"); f.Send(0x24, true);
            f.Hotkey.ProcessMessage(f.Handle, 0x1c, IntPtr.Zero, IntPtr.Zero);
            f.Hotkey.ProcessMessage(f.Handle, 0x08, IntPtr.Zero, IntPtr.Zero);
            Check(f.Send(0x24), "focus loss does not clear INPUTSINK Alt state");
        }
        using (var f = new Fixture(false)) {
            f.EnsureHandle(); f.Backend.RegisterFailure = true; f.Hotkey.Configure(true);
            Check(!f.Hotkey.IsRegistered && f.Hotkey.StatusText.Contains("등록하지 못"), "registration failure actionable");
            f.Backend.RegisterFailure = false; f.Hotkey.Retry(); Check(f.Hotkey.IsRegistered, "failed registration retry");
            f.Backend.RemoveFailure = true; f.Hotkey.Configure(false);
            Check(!f.Hotkey.IsRegistered && f.Hotkey.StatusText.Contains("해제를 확인"), "remove failure disables callbacks and reports failure");
            f.Backend.RemoveFailure = false; f.Hotkey.Configure(false); Check(!f.Backend.Current.Exists, "disable retries owned registration cleanup");
        }
        using (var f = new Fixture(false)) {
            f.EnsureHandle(); f.Backend.QueryFailure = true; f.Hotkey.Configure(true);
            Check(!f.Hotkey.IsRegistered && f.Backend.RegisterCount == 0 && f.Hotkey.StatusText.Contains("상태를 확인"), "query failure cannot overwrite unknown registration");
        }
    }
    static void VerifyParser()
    {
        PipRawKeyboardPacket parsed;
        foreach (int pointer in new[] { 4, 8 }) {
            byte[] bytes = Bytes(Packet(0x24), pointer);
            Check(PipCaptureHotkey.TryParseKeyboard(bytes, pointer, out parsed) && parsed.VirtualKey == 0x24 && parsed.MakeCode == 0x47, "native keyboard header architecture layout");
            bytes = Bytes(Packet(0x12, true, 2), pointer);
            Check(PipCaptureHotkey.TryParseKeyboard(bytes, pointer, out parsed) && parsed.Flags == 3, "extended release parsing");
            byte[] bad = (byte[])bytes.Clone(); bad[0] = 0; Check(!PipCaptureHotkey.TryParseKeyboard(bad, pointer, out parsed) && parsed.IsOtherDevice, "mouse type distinguished from invalid keyboard");
            bad[0] = 2; Check(!PipCaptureHotkey.TryParseKeyboard(bad, pointer, out parsed) && parsed.IsOtherDevice, "HID type distinguished from invalid keyboard");
            bad = (byte[])bytes.Clone(); bad[8 + pointer] = 2;
            Check(!PipCaptureHotkey.TryParseKeyboard(bad, pointer, out parsed) && !parsed.IsOtherDevice, "invalid header input code rejected");
            bad = (byte[])bytes.Clone(); bad[4]--; Check(!PipCaptureHotkey.TryParseKeyboard(bad, pointer, out parsed), "header size mismatch rejected");
            bad = new byte[bytes.Length - 1]; Array.Copy(bytes, bad, bad.Length); Check(!PipCaptureHotkey.TryParseKeyboard(bad, pointer, out parsed), "short keyboard packet rejected");
            bad = Bytes(Packet(255), pointer); Check(!PipCaptureHotkey.TryParseKeyboard(bad, pointer, out parsed), "fake overrun key rejected");
            bad = Bytes(Packet(0x24, false, 8), pointer); Check(!PipCaptureHotkey.TryParseKeyboard(bad, pointer, out parsed), "unknown keyboard flags rejected");
            var inconsistent = Packet(0x24); inconsistent.Message = 0x101;
            Check(!PipCaptureHotkey.TryParseKeyboard(Bytes(inconsistent, pointer), pointer, out parsed), "break/make message mismatch rejected");
            bad = (byte[])bytes.Clone(); bad[8 + pointer * 2 + 4] = 1;
            Check(!PipCaptureHotkey.TryParseKeyboard(bad, pointer, out parsed), "reserved keyboard field rejected");
        }
        Check(!PipCaptureHotkey.TryParseKeyboard(null, 8, out parsed), "null packet rejected");
        Check(!PipCaptureHotkey.TryParseKeyboard(new byte[5000], 8, out parsed), "oversize packet bounded");
        Check(!PipCaptureHotkey.TryParseKeyboard(new byte[40], 16, out parsed), "unsupported pointer width rejected");
    }
    static PipRawKeyboardPacket Packet(int key, bool up = false, int flags = 0, int make = 0x47)
    { return new PipRawKeyboardPacket { VirtualKey = (ushort)key, Flags = (ushort)(flags | (up ? 1 : 0)), MakeCode = (ushort)make, Message = (uint)(up ? 0x105 : 0x104) }; }
    static byte[] Bytes(PipRawKeyboardPacket packet, int pointer)
    {
        int header = 8 + pointer * 2; byte[] bytes = new byte[header + 16];
        Array.Copy(BitConverter.GetBytes((uint)1), 0, bytes, 0, 4); Array.Copy(BitConverter.GetBytes((uint)bytes.Length), 0, bytes, 4, 4);
        Array.Copy(BitConverter.GetBytes(packet.MakeCode), 0, bytes, header, 2); Array.Copy(BitConverter.GetBytes(packet.Flags), 0, bytes, header + 2, 2);
        Array.Copy(BitConverter.GetBytes(packet.VirtualKey), 0, bytes, header + 6, 2); Array.Copy(BitConverter.GetBytes(packet.Message), 0, bytes, header + 8, 4); return bytes;
    }
    static void Check(bool condition, string text) { if (!condition) throw new Exception(text); checks++; }
    sealed class Fixture : IDisposable
    {
        public readonly Window Window = new Window(); public readonly FakeBackend Backend; public readonly PipCaptureHotkey Hotkey;
        public IntPtr Handle; public int Captures; public string StatusBeforeCapture;
        public Fixture(bool enabled = true, FakeBackend backend = null) {
            Backend = backend ?? new FakeBackend();
            Hotkey = new PipCaptureHotkey(Window, delegate { Check(!Backend.InRead, "callback runs after raw reader cleanup"); StatusBeforeCapture = Hotkey.StatusText; Captures++; }, null, Backend);
            if (enabled) { Hotkey.Configure(true); EnsureHandle(); }
        }
        public void EnsureHandle() { Handle = new WindowInteropHelper(Window).EnsureHandle(); }
        public bool Send(int key, bool up = false, int flags = 0, int make = 0x47) {
            Backend.Pending = Packet(key, up, flags, make); return Hotkey.ProcessMessage(Handle, 0xff, new IntPtr(1), new IntPtr(1));
        }
        public void DeviceChange() { Hotkey.ProcessMessage(Handle, 0xfe, new IntPtr(2), new IntPtr(1)); }
        public void Dispose() { Hotkey.Dispose(); Window.Close(); }
    }
    sealed class FakeBackend : IPipCaptureHotkeyBackend
    {
        public PipRawKeyboardRegistration Current; public PipRawKeyboardPacket Pending;
        public int RegisterCount, RemoveCount, ReadCount, QueryCount;
        public bool RegisterFailure, QueryFailure, RemoveFailure, ReadFailure, InRead;
        public int LastError { get { return 5; } }
        public bool TryGetKeyboardRegistration(out PipRawKeyboardRegistration registration) { QueryCount++; registration = Current; return !QueryFailure; }
        public bool RegisterKeyboard(IntPtr window) {
            RegisterCount++; if (Current.Exists) throw new Exception("Clobbered existing registration");
            if (RegisterFailure) return false;
            Current = new PipRawKeyboardRegistration { Exists = true, Window = window, Flags = PipCaptureHotkey.RawKeyboardFlags }; return true;
        }
        public bool RemoveKeyboard() { RemoveCount++; if (!Current.Exists) throw new Exception("Removed unowned registration"); if (RemoveFailure) return false; Current = new PipRawKeyboardRegistration(); return true; }
        public bool TryReadKeyboard(IntPtr input, out PipRawKeyboardPacket packet) { InRead = true; ReadCount++; packet = Pending; InRead = false; return !ReadFailure && !packet.IsOtherDevice; }
    }
}
