using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MabinogiBarter;

class LocalOcrVerificationRunner
{
    static int checks;
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string output = args[0];
            string[] names = { "거미줄", "가는 실뭉치", "경매장 수수료 10% 할인 쿠폰" };
            foreach (string appearance in new[] { "light", "dark", "outlined" })
            {
                BitmapSource image = MakeImage(names, appearance);
                Save(image, Path.Combine(output, appearance + ".png"));
                LocalOcrResult result = LocalOcrEngine.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(appearance + ": " + String.Join(" | ", result.Lines));
                Check(result.Language.StartsWith("ko", StringComparison.OrdinalIgnoreCase), appearance + " Korean recognizer");
                string recognized = String.Join(" ", result.Lines).Replace(" ", "");
                foreach (string name in names) Check(recognized.Contains(name.Replace(" ", "")), appearance + " recognizes " + name);
                Check(result.Lines.Count >= names.Length, appearance + " separate item lines");
            }
            VerifyRecognizedMarketBatch(output);
            BitmapSource blank = MakeImage(new string[0], "dark");
            Check(LocalOcrEngine.RecognizeAsync(blank, CancellationToken.None).GetAwaiter().GetResult().Lines.Count == 0, "blank crop has no fabricated text");
            using (CancellationTokenSource cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { LocalOcrEngine.RecognizeAsync(blank, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancelled recognition completed"); }
                catch (OperationCanceledException) { checks++; }
            }
            try { LocalOcrEngine.RecognizeAsync(null, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Null input accepted"); }
            catch (ArgumentNullException) { checks++; }
            BitmapSource tiny = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            try { LocalOcrEngine.RecognizeAsync(tiny, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Tiny input accepted"); }
            catch (InvalidOperationException e) { Check(e.Message.Contains("영역"), "tiny crop guidance"); }
            File.WriteAllText(Path.Combine(output, "report.txt"), "PASS " + checks + " checks. Synthetic images only; no desktop capture, network, external process, or image upload.");
            Console.WriteLine("PASS " + checks + " local OCR checks"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    static void VerifyRecognizedMarketBatch(string output)
    {
        // A local snapshot fixture exercises the same matcher as PIP without downloading market data.
        DateTime fetched = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        MarketSnapshotData cached = new MarketSnapshotData {
            Quotes = new Dictionary<string, MarketSnapshotQuote>(),
            Items24h = new List<MarketSnapshotItem>(), Items7d = new List<MarketSnapshotItem>(),
            ListingsFetchedUtc = fetched
        };
        string[] itemNames = { "거미줄", "가는 실뭉치", "경매장 수수료 10% 할인 쿠폰", "나무장작" };
        decimal?[] prices = { 149m, 200m, 220000m, null };
        for (int i = 0; i < itemNames.Length; i++) {
            cached.Quotes[itemNames[i]] = new MarketSnapshotQuote {
                Name = itemNames[i], UnitPrice = prices[i], Quantity = prices[i].HasValue ? 3 : 0,
                ListingCount = prices[i].HasValue ? 1 : 0, FetchedUtc = fetched
            };
            cached.Items24h.Add(new MarketSnapshotItem { Name = itemNames[i], Category = "기타 재료", PriceComparable = true });
        }
        string unknown = "수수께끼의 드랍 물건";
        string[] cropLines = { itemNames[0], itemNames[1], itemNames[0], itemNames[2], itemNames[3], unknown };
        BitmapSource image = MakeImage(cropLines, "outlined");
        Save(image, Path.Combine(output, "market-batch.png"));
        LocalOcrResult recognized = LocalOcrEngine.RecognizeAsync(image, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("market batch OCR: " + String.Join(" | ", recognized.Lines));
        Check(recognized.Lines.Count == cropLines.Length, "batch OCR retains separate duplicate and unknown label lines");
        List<OcrMarketMatch> rows = new OcrMarketMatcher(cached).Resolve(recognized.Lines);
        Check(rows.Count == 5, "recognized duplicate collapsed while known and unknown items remain");
        for (int i = 0; i < itemNames.Length; i++) {
            OcrMarketMatch row = rows.Single(r => r.Text == itemNames[i]);
            Check(row.Exact && row.Candidates.Count == 1, "real OCR resolves exact fixture identity: " + itemNames[i]);
            Check(row.Candidates[0].Name == itemNames[i] && row.Candidates[0].UnitPrice == prices[i], "real OCR receives exact cached minimum: " + itemNames[i]);
        }
        Check(rows.Count(r => r.Text == "거미줄") == 1, "real OCR duplicate creates one search result");
        OcrMarketMatch unlisted = rows.Single(r => r.Text == "나무장작");
        Check(!unlisted.Candidates[0].HasListing && !unlisted.Candidates[0].UnitPrice.HasValue, "known unlisted item remains without fabricated price");
        OcrMarketMatch missing = rows.Single(r => !r.Exact);
        Check(missing.Candidates.Count == 0 && missing.Text.Replace(" ", "") == unknown.Replace(" ", ""), "unknown OCR item stays editable without guessed price or tradeability claim");
        Check(cached.Quotes["거미줄"].UnitPrice == 149m && cached.Quotes.Count == 4, "OCR matching does not modify shared snapshot");
    }
    static BitmapSource MakeImage(string[] names, string appearance)
    {
        const int width = 480;
        int height = Math.Max(155, names.Length * 43 + 25);
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(appearance == "light" ? Brushes.White : new SolidColorBrush(Color.FromRgb(35, 42, 31)), null, new Rect(0, 0, width, height));
            for (int index = 0; index < names.Length; index++)
            {
                FormattedText text = new FormattedText(names[index], CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                    new Typeface("Malgun Gothic"), 16, appearance == "light" ? Brushes.Black : Brushes.White, 1.0);
                Point position = new Point(20, 15 + index * 43);
                if (appearance == "outlined") drawing.DrawGeometry(null, new Pen(Brushes.Black, 2.0), text.BuildGeometry(position));
                drawing.DrawText(text, position);
            }
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    static void Save(BitmapSource bitmap, string path)
    {
        PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (Stream stream = File.Create(path)) encoder.Save(stream);
    }
}
