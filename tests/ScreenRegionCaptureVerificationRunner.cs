using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MabinogiBarter;

// Compile together with ScreenRegionCapture.cs. All image pixels are synthetic;
// this runner never shows a window or calls the desktop capture path.
public static class ScreenRegionCaptureVerificationRunner
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Near(double actual, double expected, string message) { Check(Math.Abs(actual - expected) < .000001, message); }
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, message);
    }
    static void Geometry()
    {
        var desktop = new Int32Rect(-2560, -1440, 6400, 3600);
        Int32Rect crop;
        Check(ScreenRegionCaptureGeometry.TryGetCrop(desktop, new Point(-1920, -1080), new Point(-1760, -960), out crop), "negative-origin selection rejected");
        Check(crop.Equals(new Int32Rect(640, 360, 160, 120)), "negative monitor origin mixed with WPF DIP coordinates");
        Int32Rect reversed;
        Check(ScreenRegionCaptureGeometry.TryGetCrop(desktop, new Point(-1760, -960), new Point(-1920, -1080), out reversed)
            && reversed.Equals(crop), "reverse drag changes selected pixels");
        foreach (double dpiScale in new[] { 1d, 1.25d, 1.5d, 2d }) {
            var mapped = ScreenRegionCaptureGeometry.ToViewRect(crop, new Size(desktop.Width, desktop.Height), new Size(desktop.Width / dpiScale, desktop.Height / dpiScale));
            Near(mapped.X * dpiScale, 640, "125/150 percent left alignment");
            Near(mapped.Y * dpiScale, 360, "125/150 percent top alignment");
            Near(mapped.Width * dpiScale, 160, "125/150 percent width alignment");
            Near(mapped.Height * dpiScale, 120, "125/150 percent height alignment");
        }
        // Even unequal view transforms draw the physical crop at the correct
        // source-pixel location; physical mouse coordinates do not use this map.
        var nonuniform = ScreenRegionCaptureGeometry.ToViewRect(crop, new Size(6400, 3600), new Size(3200, 2400));
        Near(nonuniform.X, 320, "nonuniform view X"); Near(nonuniform.Y, 240, "nonuniform view Y");
        Near(nonuniform.Width, 80, "nonuniform view width"); Near(nonuniform.Height, 80, "nonuniform view height");
        Check(ScreenRegionCaptureGeometry.TryGetCrop(desktop, new Point(-999999, -999999), new Point(999999, 999999), out crop)
            && crop.Equals(new Int32Rect(0, 0, 6400, 3600)), "drag outside all monitor bounds must clamp to snapshot");
        Check(!ScreenRegionCaptureGeometry.TryGetCrop(desktop, new Point(-999999, -999999), new Point(-999998, -999998), out crop)
            && crop.Width == 0 && crop.Height == 0, "wholly off-desktop drag fabricated a region");
        var simple = new Int32Rect(0, 0, 100, 100);
        Check(ScreenRegionCaptureGeometry.TryGetCrop(simple, new Point(1.2, 2.3), new Point(17.1, 18.2), out crop)
            && crop.Equals(new Int32Rect(1, 2, 17, 17)), "fractional physical coverage must floor leading and ceil trailing pixels");
        Check(ScreenRegionCaptureGeometry.TryGetCrop(simple, new Point(0, 0), new Point(16, 16), out crop), "exact 16-pixel minimum rejected");
        foreach (Point end in new[] { new Point(15, 16), new Point(16, 15), new Point(0, 0), new Point(0, 100), new Point(100, 0) })
            Check(!ScreenRegionCaptureGeometry.TryGetCrop(simple, new Point(0, 0), end, out crop), "too-small selection accepted: " + end);
        foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
            Check(!ScreenRegionCaptureGeometry.TryGetCrop(simple, new Point(invalid, 0), new Point(50, 50), out crop) && crop.IsEmpty,
                "non-finite pointer coordinate accepted");
        ScreenRegionCaptureGeometry.ValidateDesktop(new Int32Rect(0, 0, 8000, 8000));
        Reject(() => ScreenRegionCaptureGeometry.ValidateDesktop(new Int32Rect(0, 0, 8001, 8000)), "64 MP bound missing");
        Reject(() => ScreenRegionCaptureGeometry.ValidateDesktop(new Int32Rect(Int32.MaxValue - 5, 0, 16, 16)), "virtual desktop right edge overflow accepted");
        Reject(() => ScreenRegionCaptureGeometry.ValidateDesktop(new Int32Rect(0, Int32.MaxValue - 5, 16, 16)), "virtual desktop bottom edge overflow accepted");
        Reject(() => ScreenRegionCaptureGeometry.ValidateDesktop(new Int32Rect(0, 0, 0, 100)), "zero desktop width accepted");
        Reject(() => ScreenRegionCaptureGeometry.ToViewRect(new Int32Rect(0, 0, 16, 16), new Size(100, 100), new Size(0, 100)), "zero transform accepted");
        Reject(() => ScreenRegionCaptureGeometry.ToViewRect(new Int32Rect(90, 0, 16, 16), new Size(100, 100), new Size(100, 100)), "out-of-source crop transform accepted");
        Reject(() => ScreenRegionCaptureGeometry.ToViewRect(new Int32Rect(0, 0, 16, 16), new Size(100, 100), new Size(Double.NaN, 100)), "NaN transform accepted");
        Reject(() => ScreenRegionCaptureGeometry.ToViewRect(new Int32Rect(0, 0, 0, 0), new Size(Double.Epsilon, 100), new Size(Double.MaxValue, 100)), "overflowing transform accepted");
    }
    static void SyntheticPixels()
    {
        const int width = 128, height = 96, stride = width * 4;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
            int p = y * stride + x * 4;
            pixels[p] = (byte)x; pixels[p + 1] = (byte)y; pixels[p + 2] = (byte)(x ^ y); pixels[p + 3] = 255;
        }
        var source = new WriteableBitmap(width, height, 144, 144, PixelFormats.Bgra32, null);
        source.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        var rectangle = new Int32Rect(17, 29, 32, 40);
        var result = ScreenRegionCaptureGeometry.CopyCrop(source, rectangle);
        Check(result.IsFrozen && !(result is CroppedBitmap) && result.PixelWidth == 32 && result.PixelHeight == 40,
            "crop must be frozen detached pixels, not a full-source-retaining CroppedBitmap");
        Check(result.DpiX == 96 && result.DpiY == 96, "selected physical pixels must not be resampled by source DPI");
        var copied = new byte[32 * 40 * 4]; result.CopyPixels(copied, 32 * 4, 0);
        for (int y = 0; y < 40; y++) for (int x = 0; x < 32; x++) {
            int p = (y * 32 + x) * 4;
            Check(copied[p] == x + 17 && copied[p + 1] == y + 29 && copied[p + 2] == ((x + 17) ^ (y + 29)) && copied[p + 3] == 255,
                "crop pixel mismatch at " + x + "," + y);
        }
        source.WritePixels(new Int32Rect(0, 0, width, height), new byte[pixels.Length], stride, 0);
        var after = new byte[copied.Length]; result.CopyPixels(after, 32 * 4, 0);
        for (int i = 0; i < copied.Length; i++) Check(after[i] == copied[i], "cropped result retained mutable source storage");
        Exception workerFailure = null;
        var thread = new Thread(new ThreadStart(delegate {
            try { var buffer = new byte[copied.Length]; result.CopyPixels(buffer, 32 * 4, 0); }
            catch (Exception ex) { workerFailure = ex; }
        }));
        thread.Start(); thread.Join(); Check(workerFailure == null, "frozen crop cannot be consumed by background OCR");
        foreach (var invalid in new[] { new Int32Rect(0, 0, 15, 40), new Int32Rect(0, 0, 32, 15), new Int32Rect(120, 0, 16, 16), new Int32Rect(-1, 0, 16, 16) })
            Reject(() => ScreenRegionCaptureGeometry.CopyCrop(source, invalid), "invalid copy rectangle accepted: " + invalid);
        Reject(() => ScreenRegionCaptureGeometry.CopyCrop(null, rectangle), "null crop source accepted");
        var bgr = BitmapSource.Create(32, 32, 120, 120, PixelFormats.Bgr24, null, new byte[32 * 32 * 3], 32 * 3);
        bgr.Freeze(); var narrow = ScreenRegionCaptureGeometry.CopyCrop(bgr, new Int32Rect(1, 1, 17, 16));
        Check(narrow.IsFrozen && narrow.PixelWidth == 17 && narrow.Format == PixelFormats.Bgr24, "non-four-byte-aligned 24-bit crop rejected");
    }
    static void EarlyCancellation()
    {
        var owner = new Window();
        using (var cancel = new CancellationTokenSource()) {
            cancel.Cancel();
            var result = ScreenRegionCapture.CaptureAsync(owner, cancel.Token);
            Check(result.IsCompleted && !result.IsCanceled && result.Result == null, "pre-canceled capture must return null");
        }
        Check(ScreenRegionCapture.CaptureAsync(owner, CancellationToken.None).Result == null, "unloaded owner should not start desktop capture");
        Check(new WindowInteropHelper(owner).Handle == IntPtr.Zero && !owner.IsVisible && owner.WindowState == WindowState.Normal,
            "early cancellation created/shows an HWND or changes the owner state");
        Reject(() => ScreenRegionCapture.CaptureAsync(null, CancellationToken.None), "null capture owner accepted");
    }
    [STAThread] public static int Main()
    {
        try {
            Geometry(); SyntheticPixels(); EarlyCancellation();
            Console.WriteLine("PASS " + checks + " synthetic capture geometry/pixel assertions: negative monitors, 100/125/150/200% DPI, reverse/clamped drags, 16px minimum, 64MP maximum, invalid transforms, detached frozen pixels, background access, and early cancellation.");
            Console.WriteLine("No desktop capture, visible window, game access, file image, clipboard, or network operation was performed.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
