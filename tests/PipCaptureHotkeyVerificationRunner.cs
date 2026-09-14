using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using MabinogiBarter;

public static class PipCaptureHotkeyVerificationRunner
{
    static int checks, captures;
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            VerifyDeferredAndMessages(); VerifyFailureAndRetry(); VerifyLifetime(); VerifyAllowedKeys();
            string summary = "PASS " + checks + " hotkey checks. Fake registration backend, hidden fixture HWNDs, synthetic messages only; no real global shortcut registration, keyboard input, desktop capture, or game access.";
            Console.WriteLine(summary); File.WriteAllText(Path.Combine(args[0], "report.txt"), summary, Encoding.UTF8);
            app.Shutdown(); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void VerifyDeferredAndMessages()
    {
        var backend = new FakeBackend(); var statuses = new List<string>(); var window = new Window();
        var hotkey = new PipCaptureHotkey(window, delegate { captures++; }, statuses.Add, backend);
        Check(!hotkey.Enabled && !hotkey.IsRegistered && hotkey.VirtualKey == 0x24, "default disabled Alt+Home");
        Check(backend.Registers.Count == 0, "construction does not register shortcut");
        hotkey.Configure(true);
        Check(hotkey.Enabled && !hotkey.IsRegistered && hotkey.StatusText.Contains("창이 준비"), "registration deferred until source exists");
        Check(backend.Registers.Count == 0, "pending enabled setting makes no native registration");
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        Check(hotkey.IsRegistered && backend.Registers.Count == 1, "source initialization registers exactly once");
        Call first = backend.Registers[0];
        Check(first.Window == hwnd && first.Modifiers == 0x4001 && first.VirtualKey == 0x24, "own HWND with ALT and NOREPEAT");
        Check(first.Id >= 0 && first.Id <= 0xBFFF, "application hotkey ID range");
        Check(statuses.Count > 0 && hotkey.StatusText.Contains("Alt+Home"), "registered status callback");
        int before = captures;
        Check(!hotkey.ProcessMessage(hwnd, 0x0100, new IntPtr(first.Id), Payload(0x24, 1)), "ordinary keydown ignored");
        Check(!hotkey.ProcessMessage(IntPtr.Zero, 0x0312, new IntPtr(first.Id), Payload(0x24, 1)), "other HWND ignored");
        Check(!hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(first.Id + 1), Payload(0x24, 1)), "wrong ID ignored");
        Check(!hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(first.Id), Payload(0x23, 1)), "wrong virtual key ignored");
        foreach (int modifiers in new[] { 0, 2, 3, 5, 9, 0x4001 })
            Check(!hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(first.Id), Payload(0x24, modifiers)), "unexpected modifier payload ignored");
        Check(captures == before, "invalid native messages never capture");
        Check(hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(first.Id), Payload(0x24, 1)) && captures == before + 1, "one current ALT Home notification invokes one callback");
        hotkey.Configure(true, 0x24); hotkey.Retry();
        Check(backend.Registers.Count == 1 && backend.Unregisters.Count == 0, "same valid setting avoids duplicate registration");
        hotkey.Configure(true, 0x23);
        Check(backend.Unregisters.Count == 1 && backend.Unregisters[0].Id == first.Id, "setting change releases former registration");
        Call second = backend.Registers[1];
        Check(second.Id != first.Id && second.VirtualKey == 0x23, "new key gets a fresh registration ID");
        Check(!hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(first.Id), Payload(0x24, 1)), "queued old-key notification ignored");
        Check(hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(second.Id), Payload(0x23, 1)), "new End notification accepted");
        hotkey.Configure(true, 0x24);
        Check(!hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(first.Id), Payload(0x24, 1)), "queued old Home notification ignored after choosing Home again");
        Call third = backend.Registers[2];
        hotkey.Configure(false);
        Check(!hotkey.Enabled && !hotkey.IsRegistered && hotkey.StatusText.Contains("꺼짐"), "disable updates state and Korean status");
        Check(backend.Unregisters.Count == 3 && backend.Active.Count == 0, "disable unregisters exactly active shortcut");
        Check(!hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(third.Id), Payload(0x24, 1)), "disabled notification ignored");
        hotkey.Configure(false); hotkey.Dispose(); hotkey.Dispose(); window.Close();
        Check(backend.Unregisters.Count == 3, "repeated disable dispose and close do not double unregister");
    }

    static void VerifyFailureAndRetry()
    {
        var window = new Window(); IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        var backend = new FakeBackend { FailNext = true, LastError = 1409 };
        var hotkey = new PipCaptureHotkey(window, delegate { captures++; }, null, backend);
        hotkey.Configure(true, 0x2D);
        Check(hotkey.Enabled && !hotkey.IsRegistered && hotkey.StatusText.Contains("다른 앱에서 사용 중"), "collision reported without pretending registered");
        Call failed = backend.Registers[0];
        Check(!hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(failed.Id), Payload(0x2D, 1)), "failed registration never captures");
        hotkey.Retry();
        Check(hotkey.IsRegistered && hotkey.StatusText.Contains("Alt+Insert"), "retry activates requested Insert after conflict clears");
        Check(backend.Registers.Count == 2 && backend.Unregisters.Count == 0, "failed registrations are not unregistered");
        backend.FailNext = true; backend.LastError = 5;
        hotkey.Configure(true, 0x23);
        Check(!hotkey.IsRegistered && hotkey.StatusText.Contains("등록하지 못했습니다"), "non-conflict failure has actionable status");
        Check(backend.Unregisters.Count == 1, "only prior successful registration released on subsequent failure");
        hotkey.Configure(false); hotkey.Dispose(); window.Close();
        Check(backend.Unregisters.Count == 1 && backend.Active.Count == 0, "failed disabled helper leaves no registered shortcut");
    }

    static void VerifyLifetime()
    {
        var backend = new FakeBackend(); var window = new Window();
        var hotkey = new PipCaptureHotkey(window, delegate { captures++; }, null, backend);
        hotkey.Configure(true); IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        Call call = backend.Registers[0]; window.Close();
        Check(!hotkey.IsRegistered && !hotkey.Enabled && backend.Unregisters.Count == 1, "window close disposes registration");
        Check(!hotkey.ProcessMessage(hwnd, 0x0312, new IntPtr(call.Id), Payload(0x24, 1)), "late notification after window close ignored");
        try { hotkey.Configure(true); throw new Exception("Disposed configuration accepted"); }
        catch (ObjectDisposedException) { checks++; }
        var pendingWindow = new Window(); var pendingBackend = new FakeBackend();
        var pending = new PipCaptureHotkey(pendingWindow, delegate { captures++; }, null, pendingBackend);
        pending.Configure(true); pending.Dispose(); new WindowInteropHelper(pendingWindow).EnsureHandle();
        Check(pendingBackend.Registers.Count == 0 && pendingBackend.Unregisters.Count == 0, "disposed deferred helper detaches source initialization");
        pendingWindow.Close();
    }

    static void VerifyAllowedKeys()
    {
        foreach (int key in new[] { 0x24, 0x23, 0x2D }) Check(PipCaptureHotkey.IsAllowedVirtualKey(key), "navigation shortcut allowed");
        var window = new Window(); var backend = new FakeBackend();
        var hotkey = new PipCaptureHotkey(window, delegate { captures++; }, null, backend);
        foreach (int key in new[] { 0, 0x41, 0x70, 0x77, 0x78, 0x79, 0x7B, 0x2E }) {
            Check(!PipCaptureHotkey.IsAllowedVirtualKey(key), "skill or unapproved key rejected");
            try { hotkey.Configure(true, key); throw new Exception("Unapproved key accepted"); }
            catch (ArgumentOutOfRangeException) { checks++; }
        }
        Check(!hotkey.Enabled && backend.Registers.Count == 0, "invalid choices never modify registration state");
        hotkey.Dispose(); window.Close();
    }
    static IntPtr Payload(int key, int modifiers) { return new IntPtr((key << 16) | modifiers); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }

    sealed class Call { public IntPtr Window; public int Id; public uint Modifiers, VirtualKey; }
    sealed class FakeBackend : IPipCaptureHotkeyBackend
    {
        public readonly List<Call> Registers = new List<Call>(), Unregisters = new List<Call>();
        public readonly HashSet<int> Active = new HashSet<int>();
        public bool FailNext;
        public int LastError { get; set; }
        public bool Register(IntPtr window, int id, uint modifiers, uint virtualKey) {
            Registers.Add(new Call { Window = window, Id = id, Modifiers = modifiers, VirtualKey = virtualKey });
            if (FailNext) { FailNext = false; return false; }
            if (!Active.Add(id)) throw new Exception("Registration ID reused while active");
            return true;
        }
        public bool Unregister(IntPtr window, int id) {
            if (!Active.Remove(id)) throw new Exception("Unregistered a failed, stale, or already released registration");
            Unregisters.Add(new Call { Window = window, Id = id }); return true;
        }
    }
}
