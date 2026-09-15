using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MabinogiBarter;

public static class StartupLoadingVerificationRunner
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(path)) encoder.Save(output);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            AppMotion.ReducedMotion = false;
            foreach (bool fail in new[] { false, true }) {
                var completion = new TaskCompletionSource<string>();
                int calls = 0, frameAtCompletion = -1; bool advanced = false, wasVisible = false, saved = false;
                long completedAt = -1; var watch = Stopwatch.StartNew();
                var splash = new StartupLoadingWindow(token => { Interlocked.Increment(ref calls); return completion.Task; });
                splash.Left = -18000; splash.Top = -18000; splash.WindowStartupLocation = WindowStartupLocation.Manual; splash.ShowInTaskbar = false;
                var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
                monitor.Tick += delegate {
                    if (watch.ElapsedMilliseconds > 120 && !completion.Task.IsCompleted) {
                        if (fail) completion.SetException(new IOException("offline fixture")); else completion.SetResult("경매장 정보를 불러왔습니다.");
                    }
                    if (splash.LoadingCompleted) {
                        if (completedAt < 0) { completedAt = watch.ElapsedMilliseconds; frameAtCompletion = splash.FrameIndex; }
                        if (splash.FrameIndex != frameAtCompletion) advanced = true;
                        if (!saved && watch.ElapsedMilliseconds - completedAt > 1200) { saved = true; Save(splash, Path.Combine(args[0], fail ? "startup-offline.png" : "startup-complete.png")); }
                        if (watch.ElapsedMilliseconds - completedAt > 1000 && splash.IsVisible && splash.IsAnimating) wasVisible = true;
                    }
                    if (watch.ElapsedMilliseconds > 9000) splash.Close();
                };
                monitor.Start(); bool? result = splash.ShowDialog(); monitor.Stop();
                Require(result == true && calls == 1, "Must load once and automatically continue.");
                Require(completedAt >= 0 && watch.ElapsedMilliseconds - completedAt >= 1950, "Must wait two seconds AFTER completion.");
                Require(advanced && wasVisible, "Artwork must keep animating throughout the completion delay.");
                Require(!splash.IsAnimating, "Closed splash must stop its timer.");
                if (fail) Require(splash.StatusText.Contains("저장된 정보"), "Failed download must explain fallback.");
            }
            Console.WriteLine("Success, fallback and animated completion delay passed.");
            foreach (bool duringDelay in new[] { false, true }) {
                bool cancelled = false;
                var pending = new TaskCompletionSource<string>();
                var splash = new StartupLoadingWindow(token => { token.Register(() => cancelled = true); return duringDelay ? Task.FromResult("완료") : pending.Task; });
                splash.Left = -18000; splash.Top = -18000; splash.WindowStartupLocation = WindowStartupLocation.Manual; splash.ShowInTaskbar = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += delegate { timer.Stop(); splash.Close(); }; timer.Start();
                Require(splash.ShowDialog() != true, "Close must cancel startup, including during post-load delay.");
                pending.TrySetResult("late result");
                Require(cancelled && !splash.IsAnimating, "Closed window must cancel loading and animation.");
            }
            Console.WriteLine("Cancellation passed.");
            // Real sequence must construct the main window only after successful delay.
            var ready = new StartupLoadingWindow(token => Task.FromResult("완료"));
            ready.Left = -18000; ready.Top = -18000; ready.WindowStartupLocation = WindowStartupLocation.Manual; ready.ShowInTaskbar = false;
            var main = StartupSequence.PrepareMainWindow(app, ready, () => new Window());
            Require(main != null && app.MainWindow == main && app.ShutdownMode == ShutdownMode.OnMainWindowClose, "Main-window lifetime must transfer after loading.");
            Console.WriteLine("Startup loading: success, offline fallback, 2-second delay, continuous artwork, cancellation and main lifetime passed.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
