using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabinogiBarter
{
    public sealed partial class AuctionSettlementView
    {
        void CopyReport()
        {
            // Revalidate the current input rather than exporting a previous valid result.
            Recalculate();
            if (!CopyImageButton.IsEnabled) return;
            try {
                var bitmap = CreateReportImage();
                if (ImageCopier == null) throw new InvalidOperationException();
                ImageCopier(bitmap);
                status.Text = "분배표 이미지를 복사했습니다. 채팅창에 붙여넣기(Ctrl+V) 하세요.";
            } catch (ExternalException) { status.Text = "다른 프로그램이 클립보드를 사용 중입니다. 잠시 후 다시 복사하세요."; }
            catch (InvalidOperationException) { status.Text = "분배표 이미지를 복사하지 못했습니다. 입력값을 확인하고 다시 시도하세요."; }
        }

        public BitmapSource CreateReportImage()
        {
            Recalculate();
            if (CurrentReport == null || currentInput == null || !CopyImageButton.IsEnabled) throw new InvalidOperationException("A valid settlement is required.");
            var chosen = CurrentReport.Scenarios.First(s => s.DiscountPercent == selectedDiscount);
            // A detached, opaque report includes every row even when the live window
            // is scrolled; fixed print colors keep shared images readable in any theme.
            var body = new StackPanel();
            body.Children.Add(ReportText("밀레시안 장부 · 판매 분배표", 27, true));
            var name = ReportText(String.IsNullOrWhiteSpace(MarketPanel.ItemNameInput.Text) ? "판매 아이템 미입력" : MarketPanel.ItemNameInput.Text.Trim(), 21, true);
            name.Margin = new Thickness(0, 12, 0, 16); body.Children.Add(name);
            body.Children.Add(ReportText("판매 총액 " + Money(currentInput.GrossAmount) + "  ·  분배 인원 " + currentInput.People + "명 (본인 포함)", 16, true));
            body.Children.Add(ReportText((currentInput.Premium ? "프리미엄 할인 적용" : "프리미엄 할인 미적용") + " · 기본 수수료 " + (CurrentReport.BaseFeeRate * 100).ToString("0") + "%  ·  기타 비용 " + Money(currentInput.ExtraCost), 14, false));
            var result = new StackPanel();
            result.Children.Add(ReportText("선택한 방식 · " + CouponName(selectedDiscount), 16, true));
            result.Children.Add(ReportText("예상 수수료 " + Money(chosen.Fee) + "  ·  쿠폰 비용 " + Money(chosen.CouponPrice.Value), 14, false));
            if (chosen.IsLoss) result.Children.Add(ReportText("분배 가능 금액 없음 · 부족한 금액 " + Money(-chosen.NetAmount.Value), 24, true));
            else {
                var share = ReportText("1인당 " + Money(chosen.PerPerson.Value), 32, true); share.Margin = new Thickness(0, 8, 0, 4); result.Children.Add(share);
                result.Children.Add(ReportText("분배할 총액 " + Money(chosen.NetAmount.Value) + "  ·  분배 후 남는 금액 " + Money(chosen.Remainder.Value), 15, false));
            }
            body.Children.Add(new Border { Child = result, Padding = new Thickness(20), Margin = new Thickness(0, 18, 0, 20), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(232, 244, 236)) });
            var recommended = CurrentReport.BestScenario;
            if (recommended != null && !recommended.IsLoss && recommended.DiscountPercent != selectedDiscount) {
                var best = ReportText("추천 · " + CouponName(recommended.DiscountPercent) + " · 1인당 " + Money(recommended.PerPerson.Value)
                    + (CurrentReport.Scenarios.Any(s => !s.IsKnown) ? " (확인된 비용 기준)" : ""), 15, true);
                best.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(best);
            }
            body.Children.Add(ReportText("쿠폰별 비교 · 입력한 비용 기준", 17, true));
            var table = new Grid { Margin = new Thickness(0, 10, 0, 16) };
            foreach (int width in new[] { 145, 160, 160, 175, 200 }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            AddReportRow(table, 0, new[] { "쿠폰 할인", "쿠폰 비용", "예상 수수료", "분배할 총액", "1인당 분배금" }, true);
            int row = 1;
            foreach (var scenario in CurrentReport.Scenarios) {
                AddReportRow(table, row++, new[] {
                    (scenario.DiscountPercent == 0 ? "없음" : scenario.DiscountPercent + "%")
                        + (recommended == scenario && !scenario.IsLoss ? " · 추천" : "") + (scenario.DiscountPercent == selectedDiscount ? " · 선택" : ""),
                    scenario.CouponPrice.HasValue ? Money(scenario.CouponPrice.Value) : "미입력",
                    Money(scenario.Fee),
                    scenario.NetAmount.HasValue ? Money(scenario.NetAmount.Value) : "미확인",
                    scenario.PerPerson.HasValue ? Money(scenario.PerPerson.Value) : scenario.IsLoss ? "분배 없음" : "미확인"
                }, scenario.DiscountPercent == selectedDiscount || recommended == scenario, recommended == scenario && !scenario.IsLoss);
            }
            body.Children.Add(table);
            body.Children.Add(ReportText("판매 금액은 직접 입력한 값입니다. 한 판매 건(묶음)의 총액과 쿠폰 1장 기준입니다.\n수수료의 골드 단위 처리에 따라 실제 수령액이 달라질 수 있습니다. 분배금은 1 G 미만을 내립니다.", 12, false));
            var footer = ReportText(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  ·  made by 하프_알베도", 12, false); footer.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(footer);
            var page = new Border { Width = 960, Padding = new Thickness(40), Background = Brushes.White, Child = body };
            page.Measure(new Size(960, Double.PositiveInfinity));
            page.Arrange(new Rect(0, 0, 960, page.DesiredSize.Height)); page.UpdateLayout();
            var image = new RenderTargetBitmap(1440, (int)Math.Ceiling(page.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            image.Render(page); image.Freeze(); return image;
        }

        static TextBlock ReportText(string text, double size, bool bold)
        {
            return new TextBlock { Text = text, FontSize = size, FontFamily = new FontFamily("Malgun Gothic"), Foreground = new SolidColorBrush(Color.FromRgb(32, 45, 53)), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        }
        static void AddReportRow(Grid table, int number, string[] values, bool bold, bool recommended = false)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int col = 0; col < values.Length; col++) {
                var line = ReportText(values[col], number == 0 ? 12 : 13, bold); line.Margin = new Thickness(8, 9, 8, 9);
                var cell = new Border { Child = line, Background = number == 0 ? new SolidColorBrush(Color.FromRgb(241, 245, 243)) : recommended ? new SolidColorBrush(Color.FromRgb(232, 244, 236)) : Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(220, 229, 223)), BorderThickness = new Thickness(0, 0, 0, 1) };
                Grid.SetRow(cell, number); Grid.SetColumn(cell, col); table.Children.Add(cell);
            }
        }
    }
}
