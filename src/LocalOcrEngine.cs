using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MabinogiBarter
{
    public sealed class LocalOcrResult
    {
        public readonly List<string> Lines = new List<string>();
        public string Language;
    }

    // Reads only the bitmap supplied by the capture UI. Images never leave this process.
    public static class LocalOcrEngine
    {
        public const int MaximumCapturePixels = 16000000;
        const int MaximumPreparedPixels = 12000000;
        const int RecognitionTimeoutSeconds = 20;
        const string MissingLanguageMessage = "Windows의 한국어 OCR 언어 구성 요소가 필요합니다. Windows 설정에서 한국어 언어 팩(광학 문자 인식)을 설치한 뒤 앱을 다시 실행해 주세요.";
        const string UnavailableMessage = "이 PC에서 Windows OCR을 실행하지 못했습니다. Windows 10/11의 한국어 OCR 언어 구성 요소를 확인해 주세요. 직접 이름을 입력해 검색할 수도 있습니다.";

        public static async Task<LocalOcrResult> RecognizeAsync(BitmapSource bitmap, CancellationToken token)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");
            token.ThrowIfCancellationRequested();
            if (bitmap.PixelWidth < 2 || bitmap.PixelHeight < 2)
                throw new InvalidOperationException("글자가 포함되도록 촬영 영역을 조금 더 크게 지정해 주세요.");
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaximumCapturePixels)
                throw new InvalidOperationException("촬영 영역이 너무 큽니다. 아이템 이름이 있는 부분만 작게 지정해 주세요.");

            // The UI supplies a frozen screenshot; keep callers with other BitmapSources safe too.
            BitmapSource frozen = bitmap;
            if (!frozen.IsFrozen) { frozen = bitmap.CloneCurrentValue(); frozen.Freeze(); }
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(RecognitionTimeoutSeconds));
                try
                {
                    return await Task.Run(() => RecognizeCoreAsync(frozen, timeout.Token), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("문자 인식 시간이 길어져 중지했습니다. 아이템 이름 주변만 작게 촬영해 주세요.");
                }
                catch (TypeLoadException) { throw new InvalidOperationException(UnavailableMessage); }
                catch (FileNotFoundException) { throw new InvalidOperationException(UnavailableMessage); }
                catch (COMException) { throw new InvalidOperationException(UnavailableMessage); }
                catch (PlatformNotSupportedException) { throw new InvalidOperationException(UnavailableMessage); }
            }
        }

        static async Task<LocalOcrResult> RecognizeCoreAsync(BitmapSource source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Language korean = new Language("ko");
            if (!OcrEngine.IsLanguageSupported(korean)) throw new InvalidOperationException(MissingLanguageMessage);
            OcrEngine engine = OcrEngine.TryCreateFromLanguage(korean);
            if (engine == null) throw new InvalidOperationException(MissingLanguageMessage);

            int maximumDimension = checked((int)OcrEngine.MaxImageDimension);
            int width = source.PixelWidth, height = source.PixelHeight;
            double scale = 1.0;
            // Small in-game labels benefit from enlargement. Bound the allocation for large crops.
            if (width <= maximumDimension / 2 && height <= maximumDimension / 2 &&
                (long)width * height * 4 <= MaximumPreparedPixels) scale = 2.0;
            else if (width > maximumDimension || height > maximumDimension ||
                (long)width * height > MaximumPreparedPixels)
                scale = Math.Min((double)maximumDimension / Math.Max(width, height),
                    Math.Sqrt((double)MaximumPreparedPixels / ((long)width * height)));

            BitmapSource prepared = source;
            if (Math.Abs(scale - 1.0) > .001)
            {
                TransformedBitmap resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                resized.Freeze(); prepared = resized;
            }
            if (prepared.Format != PixelFormats.Bgra32)
            {
                FormatConvertedBitmap converted = new FormatConvertedBitmap(prepared, PixelFormats.Bgra32, null, 0);
                converted.Freeze(); prepared = converted;
            }
            token.ThrowIfCancellationRequested();
            width = prepared.PixelWidth; height = prepared.PixelHeight;
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(stride * height)];
            try
            {
                prepared.CopyPixels(pixels, stride, 0);
                // Screen alpha is not meaningful; OCR receives a completely opaque image.
                for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
                token.ThrowIfCancellationRequested();
                LocalOcrResult result = await ReadPixelsAsync(engine, pixels, width, height, token).ConfigureAwait(false);
                if (result.Lines.Count == 0)
                {
                    // White text over dark game scenery sometimes benefits from an inverse pass.
                    // This bounded fallback runs only when the first pass found no text.
                    for (int offset = 0; offset < pixels.Length; offset += 4)
                    {
                        pixels[offset] = (byte)(255 - pixels[offset]);
                        pixels[offset + 1] = (byte)(255 - pixels[offset + 1]);
                        pixels[offset + 2] = (byte)(255 - pixels[offset + 2]);
                    }
                    token.ThrowIfCancellationRequested();
                    result = await ReadPixelsAsync(engine, pixels, width, height, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                return result;
            }
            finally { Array.Clear(pixels, 0, pixels.Length); }
        }

        static async Task<LocalOcrResult> ReadPixelsAsync(OcrEngine engine, byte[] pixels, int width,
            int height, CancellationToken token)
        {
            using (SoftwareBitmap image = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore))
            {
                image.CopyFromBuffer(pixels.AsBuffer());
                OcrResult recognized = await engine.RecognizeAsync(image).AsTask(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                LocalOcrResult result = new LocalOcrResult { Language = engine.RecognizerLanguage.LanguageTag };
                int textLength = 0;
                foreach (OcrLine line in recognized.Lines)
                {
                    string value = (line.Text ?? "").Trim();
                    if (value.Length == 0) continue;
                    if (value.Length > 300) value = value.Substring(0, 300);
                    result.Lines.Add(value); textLength += value.Length;
                    if (result.Lines.Count >= 200 || textLength >= 20000) break;
                }
                return result;
            }
        }
    }
}
