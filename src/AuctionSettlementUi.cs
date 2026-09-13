using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        AuctionSettlementWindow settlementWindow;
        void ShowAuctionSettlement()
        {
            if (settlementWindow != null) { if (settlementWindow.WindowState == WindowState.Minimized) settlementWindow.WindowState = WindowState.Normal; settlementWindow.Activate(); return; }
            var window = new AuctionSettlementWindow { Owner = this, Icon = Icon, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var config = AuctionProxyConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "auction-proxy.json"));
            if (config.IsConfigured) {
                var client = MarketSnapshotClient.ForBaseUri(config.BaseUri);
                window.MarketPanel.Configure(client);
            } else window.MarketPanel.Configure(null, null, config.StatusMessage);
            settlementWindow = window;
            window.Closed += delegate { if (settlementWindow == window) settlementWindow = null; };
            window.Show();
        }
    }

    public sealed partial class AuctionSettlementWindow : Window
    {
        sealed class CouponView
        {
            public int Discount;
            public TextBox Price;
            public TextBlock Source, Market, Fee, Cost, Net, Share, Remainder, Badge;
            public Button Select, FollowMarket;
            public Border Card;
        }
        static readonly Brush Green = AppTheme.Brush("#226C54"), Ink = AppTheme.Brush("#202D35"), Muted = AppTheme.Brush("#748278"), Line = AppTheme.Brush("#DCE5DF");
        readonly Dictionary<int, CouponView> coupons = new Dictionary<int, CouponView>();
        readonly HashSet<int> manualPrices = new HashSet<int>();
        readonly Dictionary<int, decimal?> marketPrices = new Dictionary<int, decimal?>();
        readonly Dictionary<int, DateTime?> marketTimes = new Dictionary<int, DateTime?>();
        readonly TextBlock summary = Text("", 18, Green, true), status = Text("", 12, Muted, false), amountHint = Text("", 12, Muted, false);
        readonly TextBlock recommendation = Text("", 13, Green, true);
        readonly UniformGrid comparisons = new UniformGrid { Columns = 2 };
        bool fillingPrices;
        int selectedDiscount;
        AuctionSettlementInput currentInput;
        public AuctionSettlementReport CurrentReport { get; private set; }
        public SettlementMarketPanel MarketPanel { get; private set; }
        public TextBox GrossInput { get; private set; }
        public TextBox PeopleInput { get; private set; }
        public TextBox ExtraCostInput { get; private set; }
        public CheckBox PremiumControl { get; private set; }
        public Button CopyImageButton { get; private set; }
        public ScrollViewer BodyScroll { get; private set; }
        public Action<BitmapSource> ImageCopier { get; set; }
        public int SelectedDiscount { get { return selectedDiscount; } }
        public TextBox CouponPriceInput(int discount) { return coupons[discount].Price; }
        public Button CouponSelectButton(int discount) { return coupons[discount].Select; }
        public Button CouponMarketButton(int discount) { return coupons[discount].FollowMarket; }
        public TextBlock CouponMarketText(int discount) { return coupons[discount].Market; }
        public TextBlock CouponSourceText(int discount) { return coupons[discount].Source; }
        public Border CouponCard(int discount) { return coupons[discount].Card; }
        public TextBlock RecommendationText { get { return recommendation; } }

        public AuctionSettlementWindow()
        {
            Title = "밀레시안 장부 · 수수료·분배";
            Width = 1080; Height = 880; MinWidth = 780; MinHeight = 580;
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 30);
            Background = AppTheme.Brush("#F4F6F5"); Foreground = Ink; FontFamily = new FontFamily("Malgun Gothic");
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            ImageCopier = bitmap => Clipboard.SetImage(bitmap);
            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel(); heading.Children.Add(Text("수수료·분배", 26, Ink, true));
            heading.Children.Add(Text("실제 판매 금액으로 수수료와 함께 나눌 금액을 계산하세요.", 13, Muted, false)); header.Children.Add(heading);
            CopyImageButton = Button("분배표 이미지 복사", CopyReport, true); CopyImageButton.Margin = new Thickness(12, 0, 0, 0);
            AutomationProperties.SetName(CopyImageButton, "분배표 이미지 복사");
            Grid.SetColumn(CopyImageButton, 1); header.Children.Add(CopyImageButton); root.Children.Add(header);
            var body = new StackPanel();
            MarketPanel = new SettlementMarketPanel(); MarketPanel.SnapshotChanged = ApplyMarketPrices;
            body.Children.Add(Card(MarketPanel));
            body.Children.Add(BuildInputs());
            var summaryBody = new StackPanel(); summaryBody.Children.Add(summary);
            recommendation.Margin = new Thickness(0, 10, 0, 0); summaryBody.Children.Add(recommendation);
            var chosen = Card(summaryBody); chosen.Background = AppTheme.Brush("#EAF3E9"); body.Children.Add(chosen);
            var compareHeading = Text("쿠폰별 비용과 분배금", 17, Ink, true); compareHeading.Margin = new Thickness(4, 3, 0, 4); body.Children.Add(compareHeading);
            var couponHint = Text("쿠폰값은 불러온 시세 또는 직접 입력한 금액입니다. 보유 쿠폰은 0 G로 계산할 수 있습니다. 추천은 가격이 확인된 방식 중 분배금이 가장 큰 방법입니다.", 12, Muted, false);
            couponHint.Margin = new Thickness(4, 0, 0, 12); body.Children.Add(couponHint);
            foreach (int discount in new[] { 0, 10, 20, 30, 50, 100 }) comparisons.Children.Add(BuildCoupon(discount));
            body.Children.Add(comparisons);
            var note = Text("한 판매 건(묶음)의 총액과 쿠폰 1장 기준입니다. 여러 판매 건은 각각 계산하세요.\n예상 수수료는 소수점까지 계산합니다. 실제 수령 시 골드 단위 처리에 따라 차이가 날 수 있습니다.\n분배금은 1 G 미만을 내리고, 남는 금액을 따로 표시합니다. 보증 수표 발급 등 추가 비용은 기타 비용에 입력하세요.", 11, Muted, false);
            note.Margin = new Thickness(4, 6, 4, 8); body.Children.Add(note);
            BodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) };
            Grid.SetRow(BodyScroll, 1); root.Children.Add(BodyScroll);
            status.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(status, 2); root.Children.Add(status);
            Content = root;
            MarketPanel.ItemNameInput.TextChanged += delegate { ClearCopyStatus(); };
            Closed += delegate { MarketPanel.Dispose(); };
            AppMotion.WindowContent(this);
            Recalculate();
        }

        Border BuildInputs()
        {
            var body = new StackPanel();
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            var sale = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            sale.Children.Add(Text("실제 판매한 총액 (G)", 14, Ink, true));
            GrossInput = Input("", "실제 판매한 총액"); GrossInput.FontSize = 21; GrossInput.Height = 44;
            GrossInput.ToolTip = "판매를 마친 금액을 직접 입력합니다. 아이템 검색·시세 갱신은 이 값을 바꾸지 않습니다.";
            sale.Children.Add(GrossInput); amountHint.Margin = new Thickness(0, 5, 0, 8); sale.Children.Add(amountHint);
            var amounts = new WrapPanel();
            decimal[] values = { 100000000m, 50000000m, 10000000m, 5000000m, 1000000m, 100000m };
            string[] names = { "+1억", "+5000만", "+1000만", "+500만", "+100만", "+10만" };
            for (int i = 0; i < values.Length; i++) {
                decimal value = values[i]; var add = Button(names[i], delegate { AddAmount(GrossInput, value); }, false);
                add.Margin = new Thickness(0, 0, 5, 5); add.Padding = new Thickness(8, 4, 8, 4); amounts.Children.Add(add);
            }
            var clear = Button("금액 지우기", delegate { GrossInput.Clear(); GrossInput.Focus(); }, false); clear.Margin = new Thickness(0, 0, 5, 5); amounts.Children.Add(clear);
            sale.Children.Add(amounts); row.Children.Add(sale);
            var options = new StackPanel(); Grid.SetColumn(options, 1); row.Children.Add(options);
            PremiumControl = new CheckBox { Content = "프플 / 멤버십 수수료 할인 (4%)", FontSize = 13, Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
            PremiumControl.ToolTip = "기본 5% · 프리미엄 라이프 또는 콤비네이션 멤버십 혜택 적용 시 4%";
            AutomationProperties.SetName(PremiumControl, "프리미엄 수수료 할인"); options.Children.Add(PremiumControl);
            var small = new Grid(); small.ColumnDefinitions.Add(new ColumnDefinition()); small.ColumnDefinitions.Add(new ColumnDefinition());
            var people = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; people.Children.Add(Text("분배 인원 (본인 포함)", 12, Ink, true)); PeopleInput = Input("1", "분배 인원"); people.Children.Add(PeopleInput); small.Children.Add(people);
            var extra = new StackPanel(); extra.Children.Add(Text("기타 비용 (총액 · G)", 12, Ink, true)); ExtraCostInput = Input("0", "기타 비용 총액"); extra.Children.Add(ExtraCostInput); Grid.SetColumn(extra, 1); small.Children.Add(extra); options.Children.Add(small);
            var holy = Button("성수 제작비 +800만 G", delegate { AddAmount(ExtraCostInput, 8000000m); }, false);
            holy.Margin = new Thickness(0, 8, 0, 0); holy.ToolTip = "무리아스의 성수 1개 제작비 편의 입력값입니다. 실제 지출한 비용에 맞게 수정하세요."; options.Children.Add(holy);
            body.Children.Add(row);
            GrossInput.TextChanged += delegate { Recalculate(); };
            ExtraCostInput.TextChanged += delegate { Recalculate(); };
            PeopleInput.TextChanged += delegate { Recalculate(); };
            PremiumControl.Checked += delegate { Recalculate(); }; PremiumControl.Unchecked += delegate { Recalculate(); };
            return Card(body);
        }

        Border BuildCoupon(int discount)
        {
            var view = new CouponView { Discount = discount }; coupons.Add(discount, view);
            var body = new StackPanel();
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(Text(CouponName(discount), 15, Ink, true));
            view.Badge = Text("", 11, Green, true); view.Badge.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(view.Badge, 1); heading.Children.Add(view.Badge); body.Children.Add(heading);
            if (discount > 0) {
                var row = new Grid { Margin = new Thickness(0, 7, 0, 0) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var field = new StackPanel(); field.Children.Add(Text("쿠폰 1장 비용 (G)", 11, Muted, false)); view.Price = Input("", discount + "% 쿠폰 비용"); field.Children.Add(view.Price); row.Children.Add(field);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0) };
                var owned = Button("보유 · 0 G", delegate { view.Price.Text = "0"; }, false); owned.Margin = new Thickness(0, 0, 5, 0); buttons.Children.Add(owned);
                var quoted = Button("시세 따르기", delegate { manualPrices.Remove(discount); SetMarketPrice(view); Recalculate(); }, false); view.FollowMarket = quoted;
                quoted.ToolTip = "현재 저장된 최저가를 적용하고, 이후 공통 시세가 갱신되면 자동으로 반영합니다."; buttons.Children.Add(quoted); Grid.SetColumn(buttons, 1); row.Children.Add(buttons); body.Children.Add(row);
                view.Market = Text("현재 최저 매물가 미확인 · 시세 갱신으로 확인하세요.", 11, Muted, false); view.Market.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(view.Market);
                view.Source = Text("시세 미확인 · 직접 입력 가능", 11, Muted, false); view.Source.Margin = new Thickness(0, 4, 0, 6); body.Children.Add(view.Source);
                view.Price.TextChanged += delegate { if (fillingPrices) return; manualPrices.Add(discount); UpdateCouponSource(view); Recalculate(); };
            } else {
                var noCoupon = Text("쿠폰을 사용하지 않는 기본 정산입니다.", 12, Muted, false); noCoupon.Margin = new Thickness(0, 12, 0, 18); body.Children.Add(noCoupon);
            }
            view.Fee = Text("", 12, Muted, false); body.Children.Add(view.Fee);
            view.Cost = Text("", 12, Muted, false); body.Children.Add(view.Cost);
            view.Net = Text("", 13, Ink, true); view.Net.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(view.Net);
            view.Share = Text("", 18, Green, true); view.Share.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(view.Share);
            view.Remainder = Text("", 11, Muted, false); view.Remainder.Margin = new Thickness(0, 4, 0, 10); body.Children.Add(view.Remainder);
            view.Select = Button("이 방식으로 분배", delegate { selectedDiscount = discount; Recalculate(); }, false); body.Children.Add(view.Select);
            view.Card = Card(body); view.Card.BorderThickness = new Thickness(2); view.Card.Margin = new Thickness(0, 0, 10, 10); return view.Card;
        }

        void ApplyMarketPrices(MarketSnapshotData data)
        {
            foreach (int discount in new[] { 10, 20, 30, 50, 100 }) {
                string name = "경매장 수수료 " + discount + "% 할인 쿠폰";
                MarketSnapshotQuote quote = null;
                if (data != null && data.Quotes != null) data.Quotes.TryGetValue(name, out quote);
                marketPrices[discount] = quote != null && quote.ListingCount > 0 && quote.UnitPrice.HasValue && quote.UnitPrice.Value > 0 ? quote.UnitPrice : null;
                marketTimes[discount] = quote != null && quote.FetchedUtc != DateTime.MinValue ? (DateTime?)quote.FetchedUtc : data == null ? null : data.ListingsFetchedUtc;
                UpdateCouponMarket(coupons[discount]);
                if (!manualPrices.Contains(discount)) SetMarketPrice(coupons[discount]);
            }
            Recalculate();
        }

        void SetMarketPrice(CouponView view)
        {
            decimal? value; marketPrices.TryGetValue(view.Discount, out value);
            fillingPrices = true;
            try { view.Price.Text = value.HasValue ? value.Value.ToString("0.############################", CultureInfo.InvariantCulture) : ""; }
            finally { fillingPrices = false; }
            UpdateCouponSource(view);
        }

        void UpdateCouponMarket(CouponView view)
        {
            decimal? price; DateTime? time;
            marketPrices.TryGetValue(view.Discount, out price); marketTimes.TryGetValue(view.Discount, out time);
            view.Market.Text = "현재 최저 매물가 " + (price.HasValue ? Money(price.Value) : "미확인")
                + "\n" + (time.HasValue && time.Value != DateTime.MinValue ? time.Value.ToLocalTime().ToString("MM/dd HH:mm") + " 수집" : "수집 시각 미확인");
        }

        void UpdateCouponSource(CouponView view)
        {
            decimal value;
            bool manual = manualPrices.Contains(view.Discount);
            view.Source.Text = !manual ? "시세 자동 반영 · 직접 수정 가능"
                : TryMoney(view.Price.Text, out value) && value == 0 ? "보유 쿠폰 · 직접 입력 비용 0 G"
                : String.IsNullOrWhiteSpace(view.Price.Text) ? "직접 입력 대기 · 비용 미확인"
                : "직접 입력한 구매가 · 시세 갱신 시 유지";
        }

        void Recalculate()
        {
            if (coupons.Count != 6) return;
            CurrentReport = null; currentInput = null; CopyImageButton.IsEnabled = false;
            decimal gross, extra; int people;
            string error = !TryMoney(GrossInput.Text, out gross) ? "실제 판매한 총액을 입력하세요."
                : !TryMoney(ExtraCostInput.Text, out extra) ? "기타 비용에 0 이상의 금액을 입력하세요."
                : !Int32.TryParse(PeopleInput.Text, out people) || people < 1 || people > 1000 ? "분배 인원은 본인을 포함해 1~1,000명으로 입력하세요." : null;
            amountHint.Text = TryMoney(GrossInput.Text, out gross) ? Money(gross) + " · 직접 입력" : "실제 판매한 총액 입력 · 시세 자동 입력 없음";
            var input = new AuctionSettlementInput { Premium = PremiumControl.IsChecked == true };
            if (error == null) {
                TryMoney(GrossInput.Text, out gross); TryMoney(ExtraCostInput.Text, out extra); Int32.TryParse(PeopleInput.Text, out people);
                input.GrossAmount = gross; input.ExtraCost = extra; input.People = people;
                foreach (var view in coupons.Values.Where(c => c.Discount > 0)) {
                    decimal price;
                    if (String.IsNullOrWhiteSpace(view.Price.Text)) input.CouponPrices[view.Discount] = null;
                    else if (!TryMoney(view.Price.Text, out price)) { error = view.Discount + "% 쿠폰 비용을 확인하세요. 금액은 0 이상으로 입력합니다."; break; }
                    else input.CouponPrices[view.Discount] = price;
                }
            }
            if (error == null) {
                try { CurrentReport = AuctionSettlement.Calculate(input); currentInput = input; }
                catch (ArgumentException) { error = "금액은 0~1,000,000,000,000,000 G 범위로 입력하세요."; }
            }
            status.Text = error ?? "판매 금액은 수동 입력 · 시세는 참고용 · 이미지 복사로 분배표를 공유할 수 있습니다.";
            foreach (var view in coupons.Values) {
                var scenario = CurrentReport == null ? null : CurrentReport.Scenarios.First(s => s.DiscountPercent == view.Discount);
                bool selected = selectedDiscount == view.Discount;
                bool best = CurrentReport != null && CurrentReport.BestScenario != null && CurrentReport.BestScenario.DiscountPercent == view.Discount;
                view.Badge.Text = (best ? scenario.IsLoss ? "비용 최소" : "추천" : "") + (selected ? (best ? " · " : "") + "선택됨" : "");
                view.Card.BorderBrush = best ? Green : selected ? AppTheme.Brush("#547A98") : Line;
                view.Card.Background = best ? AppTheme.Brush("#EAF3E9") : AppTheme.Surface;
                AutomationProperties.SetItemStatus(view.Card, best ? "가장 유리한 분배 방식" : selected ? "선택한 분배 방식" : "비교 방식");
                view.Select.IsEnabled = scenario != null && scenario.IsKnown;
                view.Select.Content = selected ? "현재 분배 방식" : "이 방식으로 분배";
                view.Fee.Text = scenario == null ? "예상 수수료 —" : "예상 수수료 " + Money(scenario.Fee) + " (" + (scenario.EffectiveFeeRate * 100).ToString("0.##") + "%)";
                view.Cost.Text = scenario == null || !scenario.IsKnown ? "쿠폰값 포함 총비용 —" : "수수료 + 쿠폰 + 기타 " + Money(scenario.Fee + scenario.CouponPrice.Value + input.ExtraCost);
                view.Net.Text = scenario == null ? "분배할 총액 —" : !scenario.IsKnown ? "쿠폰 비용 입력 후 계산" : (scenario.IsLoss ? "부족한 금액 " : "분배할 총액 ") + Money(Math.Abs(scenario.NetAmount.Value));
                view.Share.Text = scenario == null || !scenario.IsKnown ? "1인당 —" : scenario.IsLoss ? "분배 가능 금액 없음" : "1인당 " + Money(scenario.PerPerson.Value);
                view.Remainder.Text = scenario != null && scenario.Remainder.HasValue ? "분배 후 남는 금액 " + Money(scenario.Remainder.Value) : " ";
            }
            var chosen = CurrentReport == null ? null : CurrentReport.Scenarios.First(s => s.DiscountPercent == selectedDiscount);
            if (chosen == null) summary.Text = "판매 금액과 인원을 입력하면 분배금이 표시됩니다.";
            else if (!chosen.IsKnown) summary.Text = CouponName(selectedDiscount) + " · 쿠폰 비용을 입력하세요.";
            else if (chosen.IsLoss) summary.Text = CouponName(selectedDiscount) + " · 비용이 판매 금액보다 " + Money(-chosen.NetAmount.Value) + " 많습니다.";
            else summary.Text = CouponName(selectedDiscount) + " · " + input.People + "명 분배\n1인당 " + Money(chosen.PerPerson.Value) + "  ·  남는 금액 " + Money(chosen.Remainder.Value);
            var recommended = CurrentReport == null ? null : CurrentReport.BestScenario;
            recommendation.Visibility = recommended == null ? Visibility.Collapsed : Visibility.Visible;
            if (recommended != null) {
                recommendation.Text = recommended.IsLoss ? "확인된 방식 모두 비용이 판매 금액보다 큽니다. 비용 최소: " + CouponName(recommended.DiscountPercent)
                    : "추천 · " + CouponName(recommended.DiscountPercent) + " · 1인당 " + Money(recommended.PerPerson.Value);
                if (CurrentReport.Scenarios.Any(s => !s.IsKnown)) recommendation.Text += " (가격이 확인된 방식 기준)";
                if (chosen != null && chosen.IsKnown && recommended.NetAmount > chosen.NetAmount)
                    recommendation.Text += "\n선택한 방식보다 분배할 총액 " + Money(recommended.NetAmount.Value - chosen.NetAmount.Value) + " 증가";
            }
            CopyImageButton.IsEnabled = chosen != null && chosen.IsKnown;
        }

        static bool TryMoney(string text, out decimal value)
        {
            return Decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 1000000000000000m;
        }
        void AddAmount(TextBox input, decimal amount)
        {
            decimal value;
            if (String.IsNullOrWhiteSpace(input.Text)) value = 0;
            else if (!TryMoney(input.Text, out value)) { status.Text = "현재 금액을 올바르게 입력한 뒤 더해 주세요."; return; }
            if (value + amount > 1000000000000000m) { status.Text = "입력 가능한 금액 범위를 초과했습니다."; return; }
            input.Text = (value + amount).ToString("0.############################", CultureInfo.InvariantCulture);
        }
        void ClearCopyStatus() { if (status.Text.StartsWith("분배표 이미지")) Recalculate(); }
        static string CouponName(int discount) { return discount == 0 ? "쿠폰 없음" : "수수료 " + discount + "% 할인 쿠폰"; }
        static string Money(decimal value) { return value.ToString("#,0.########", CultureInfo.InvariantCulture) + " G"; }

        static TextBlock Text(string value, double size, Brush color, bool bold)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = color, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        }
        static Border Card(UIElement child)
        {
            return new Border { Child = child, Padding = new Thickness(16), Background = AppTheme.Surface, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 12) };
        }
        static TextBox Input(string value, string name)
        {
            var field = new TextBox { Text = value, FontSize = 14, Height = 34, Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0, 5, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Background = AppTheme.Surface, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1) };
            AutomationProperties.SetName(field, name); return field;
        }
        static Button Button(string label, Action action, bool accent)
        {
            var button = new Button { Content = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Padding = new Thickness(11, 7, 11, 7), MinHeight = 32, Foreground = accent ? AppTheme.OnAccent : Ink, Background = accent ? Green : AppTheme.Surface, BorderBrush = accent ? Green : Line, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7)); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent }); border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent }); border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .82)); template.Triggers.Add(hover);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled);
            button.Template = template; button.Click += delegate { action(); }; return button;
        }
    }
}
