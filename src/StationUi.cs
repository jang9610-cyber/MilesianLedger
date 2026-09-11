using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        bool stationOverview = true;
        bool stationOptionsExpanded;
        bool stationDetailBudgetExpanded;
        readonly List<Action> stationValueUpdates = new List<Action>();
        readonly List<Action> stationTradeStatusUpdates = new List<Action>();
        readonly Dictionary<string, Button> stationCardButtons = new Dictionary<string, Button>();
        readonly Dictionary<string, ComboBox> stationOptionControls = new Dictionary<string, ComboBox>();
        readonly Dictionary<string, Button> stationPresetButtons = new Dictionary<string, Button>();
        TradePlanningData tradePlanningData;
        TradePlanningCalculator tradePlanning;
        ProcurementCosting procurementCosting;
        ProcurementCostReport stationCosts;
        ScrollViewer stationHubScroll;
        ComboBox savedPresetControl;
        TextBox stationPresetName;
        TextBlock stationOptionsStatus;

        public void ShowStationHub()
        {
            AppMotion.Transition(content, delegate { delayedRefresh.Stop(); summaryView = false; stationOverview = true; RenderAll(); });
        }

        void UpdateStationValues()
        {
            if (tradePlanning == null || procurementCosting == null) return;
            if (state.TradeSettings == null) state.TradeSettings = new TradePlanningSettings();
            state.TradeSettings.Normalize(tradePlanningData);
            stationCosts = procurementCosting.Build(state, name => {
                var quote = auction.GetQuote(name); return quote == null ? (decimal?)null : quote.UnitPrice;
            }, procurementPlan);
            RefreshStationValueViews();
            RefreshPipChecklist();
        }

        void RefreshStationValueViews()
        {
            foreach (var update in stationValueUpdates) update();
            foreach (var update in stationTradeStatusUpdates) update();
        }

        static string StationCostText(ProcurementCostSummary cost)
        {
            return cost.UnknownCount == 0 ? Gold(cost.KnownCost) : Gold(cost.KnownCost) + " + 미조회 " + cost.UnknownCount + "종";
        }

        void RenderStationHub()
        {
            if (tradeScroll != null) tradeScroll.Content = null;
            stationValueUpdates.Clear(); stationCardButtons.Clear(); stationOptionControls.Clear(); stationPresetButtons.Clear();
            content.Children.Clear(); content.ColumnDefinitions.Clear(); content.RowDefinitions.Clear();
            var body = new StackPanel { Margin = new Thickness(0, 0, 7, 0) };
            body.Children.Add(BuildStationOptions());
            body.Children.Add(BuildStationBudget(null));
            var cards = new Grid(); cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.RowDefinitions.Add(new RowDefinition()); cards.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < stations.Length; i++)
            {
                var card = BuildStationCard(stations[i]); card.Margin = new Thickness(i % 2 == 0 ? 0 : 7, 0, i % 2 == 0 ? 7 : 0, 14);
                Grid.SetRow(card, i / 2); Grid.SetColumn(card, i % 2); cards.Children.Add(card);
            }
            body.Children.Add(cards);
            var note = T("NPC 구매(새우·설탕·마늘)는 비용에서 제외합니다. 교역소별 비용은 공통 재료와 묶음 제작 비용을 사용 비율로 나눈 금액입니다.", 10, Muted, false);
            note.Margin = new Thickness(3, 0, 3, 10); body.Children.Add(note);
            stationHubScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            content.Children.Add(stationHubScroll); RefreshStationValueViews();
        }

        FrameworkElement BuildStationBudget(string region)
        {
            var panel = new StackPanel();
            var head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(T(region == null ? "이번 교역의 비용과 예상 결과" : region + " · 선택한 품목의 예상 비용", 13, Ink, true));
            var link = Btn("재료 준비 →", ShowSummary, false); link.FontSize = 10; link.Padding = new Thickness(8, 5, 8, 5); link.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(link, 1); head.Children.Add(link); panel.Children.Add(head);
            var row = new Grid { Margin = new Thickness(0, 13, 0, 10) }; var values = new List<TextBlock>();
            string[] labels = { "전부 완제품 구매", "내 준비 방식으로 드는 골드" };
            for (int i = 0; i < labels.Length; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var column = new StackPanel { Margin = new Thickness(0, 0, i < labels.Length - 1 ? 12 : 0, 0) }; column.Children.Add(T(labels[i], 10, Muted, false));
                var value = T("", 17, i == 1 ? Green : Ink, true); value.Margin = new Thickness(0, 5, 0, 0); values.Add(value); column.Children.Add(value); Grid.SetColumn(column, i); row.Children.Add(column);
            }
            panel.Children.Add(row);
            var result = T("", 11, Green, true); var cargo = T("", 10, Muted, false); cargo.Margin = new Thickness(0, 5, 0, 0); panel.Children.Add(result); panel.Children.Add(cargo);
            panel.ToolTip = "내 준비 방식으로 드는 골드는 구매·제작·직접 확보 설정을 반영한 전체 예상 비용입니다. NPC 구매 비용은 제외합니다. 판매 결과는 참고 평균가 기준이며 실제 지출 기록은 아닙니다.";
            stationValueUpdates.Add(delegate {
                if (stationCosts == null) return;
                var cost = region == null ? null : stationCosts.Stations[region];
                var all = region == null ? stationCosts.AllBuy : cost.AllBuy;
                var planned = region == null ? stationCosts.Planned : cost.Planned;
                values[0].Text = StationCostText(all); values[1].Text = StationCostText(planned);
                var forecast = tradePlanning.Summarize(catalog, state, state.TradeSettings, region);
                result.Text = "평균가 기준 판매 " + Gold(forecast.GrossGold) + "  ·  두카트 " + Calculator.FormatQuantity(forecast.GrossDucats)
                    + (planned.UnknownCount == 0 ? "  ·  예상 골드 손익 " + Gold(forecast.GrossGold - planned.KnownCost) : "  ·  손익은 가격 조회 후 표시");
                cargo.Text = forecast.Capacity.IsValid ? "필요 " + forecast.Slots + "칸 / 무게 " + Calculator.FormatQuantity(forecast.Weight) + "  ·  1회 적재 " + forecast.Capacity.Slots + "칸 / " + Calculator.FormatQuantity(forecast.Capacity.Weight)
                    + "  ·  최소 " + forecast.MinimumTrips + "회 이상 (칸·무게 기준, 경로 미포함)" : forecast.Capacity.Warning;
            });
            var box = Box(panel, AppTheme.Surface, 10, new Thickness(19, 15, 19, 15)); box.BorderBrush = Line; box.BorderThickness = new Thickness(1); box.Margin = new Thickness(0, 0, 0, 14); return box;
        }

        Button BuildStationCard(string region)
        {
            var panel = new StackPanel();
            var head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(T(region, 21, Accent(region), true)); var arrow = T("설정하기  →", 11, Accent(region), true); Grid.SetColumn(arrow, 1); head.Children.Add(arrow); panel.Children.Add(head);
            var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 13) };
            foreach (var trade in catalog.Trades.Where(t => t.Station == region).OrderBy(t => t.Tier))
            {
                icons.Children.Add(BuildStationTradeIcon(trade));
            }
            panel.Children.Add(icons); var selection = T("", 12, Ink, true); panel.Children.Add(selection);
            var cost = T("", 12, Accent(region), true); cost.Margin = new Thickness(0, 9, 0, 0); panel.Children.Add(cost);
            var extra = T("", 10, Muted, false); extra.Margin = new Thickness(0, 5, 0, 0); panel.Children.Add(extra);
            var button = Btn("", delegate { ShowStation(region); }, false); button.Content = panel; button.Padding = new Thickness(19); button.Background = Tint(region); button.BorderBrush = Tint(region); button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(button, region + " 교역소 카드"); stationCardButtons[region] = button;
            stationValueUpdates.Add(delegate {
                var trades = catalog.Trades.Where(t => t.Station == region && Calculator.EffectiveTarget(state, t) > 0).ToList();
                selection.Text = "선택 " + trades.Count + " / 5종  ·  교환 " + trades.Sum(t => Calculator.EffectiveTarget(state, t)) + "개  ·  준비 완료 " + trades.Count(IsPlannedTradeReady) + "종";
                cost.Text = "준비 비용  " + StationCostText(stationCosts.Stations[region].Planned);
                extra.Text = "추가 지출  " + StationCostText(stationCosts.Stations[region].Remaining);
            });
            return button;
        }

        FrameworkElement BuildStationTradeIcon(TradeItem trade)
        {
            var icon = new Grid { Width = 48, Height = 48, Margin = new Thickness(0, 0, 8, 0), Tag = "station-trade-icon:" + trade.Id };
            var image = new Image { Source = itemIcons.Get(trade.Name), Width = 30, Height = 30, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            var frame = new Border { Child = image, Width = 44, Height = 44, CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
            icon.Children.Add(frame);
            var check = T("✓", 10, AppTheme.OnAccent, true); check.HorizontalAlignment = HorizontalAlignment.Center;
            var badge = new Border { Child = check, Width = 17, Height = 17, Background = Green,
                BorderBrush = AppTheme.Surface, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            icon.Children.Add(badge);
            AutomationProperties.SetName(icon, trade.Name + " 선택 상태");
            Action update = delegate {
                int quantity = Calculator.EffectiveTarget(state, trade); bool included = quantity > 0;
                frame.Background = included ? B("#E6F2E9") : B("#FFFFFF");
                frame.BorderBrush = included ? Green : B("#DDE4DF");
                image.Opacity = included ? 1 : 0.45;
                badge.Visibility = included ? Visibility.Visible : Visibility.Hidden;
                string status = included ? "선택됨" : "선택 안 함";
                icon.ToolTip = trade.Name + " · " + trade.Tier + "티어\n" + (included ? "이번 교역에 포함 · " + quantity + "개" : "이번 교역에서 제외");
                AutomationProperties.SetItemStatus(icon, status);
                AutomationProperties.SetHelpText(icon, Convert.ToString(icon.ToolTip));
            };
            stationValueUpdates.Add(update); update(); return icon;
        }

        FrameworkElement BuildStationDetailHeader()
        {
            var panel = new StackPanel(); var routes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            routes.Children.Add(Btn("← 교역 계획", ShowStationHub, false));
            foreach (string name in stations)
            {
                string region = name; var button = Btn(region, delegate { ShowStation(region); }, false);
                button.Padding = new Thickness(11, 7, 11, 7); button.FontSize = 11;
                button.Background = station == region ? Tint(region) : AppTheme.Surface; button.BorderBrush = station == region ? Accent(region) : Line;
                routes.Children.Add(button);
            }
            panel.Children.Add(routes);
            string currentStation = station;
            var heading = T("", 11, Green, true);
            stationValueUpdates.Add(delegate {
                var costs = stationCosts.Stations[currentStation];
                heading.Text = "준비 " + StationCostText(costs.Planned) + "  ·  추가 지출 " + StationCostText(costs.Remaining) + "  ·  판매·적재 상세 보기";
            });
            var budget = new Expander { Header = heading, Content = BuildStationBudget(station), IsExpanded = stationDetailBudgetExpanded,
                Padding = new Thickness(10), Background = AppTheme.Surface, BorderBrush = Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 13) };
            budget.Expanded += delegate { stationDetailBudgetExpanded = true; }; budget.Collapsed += delegate { stationDetailBudgetExpanded = false; };
            panel.Children.Add(budget); return panel;
        }

        FrameworkElement BuildTradeForecast(TradeItem trade, int quantity)
        {
            var estimate = tradePlanning.EstimateTrade(trade, quantity, state.TradeSettings);
            var info = T("참고 평균 판매가 " + Gold(estimate.UnitGold) + " / 개  ·  목표 수량 판매 " + Gold(estimate.GrossGold) + " + " + Calculator.FormatQuantity(estimate.GrossDucats) + " 두카트\n적재 " + estimate.Slots + "칸 · 무게 " + Calculator.FormatQuantity(estimate.Weight), 11, Green, false);
            info.LineHeight = 19; info.ToolTip = "참고 평균가에 교역 마스터리와 보증서를 적용한 예상값입니다. 실시간 판매가는 아닙니다. 설정은 교역 계획 화면에서 변경하세요.";
            var box = Box(info, B("#EAF3EE"), 8, new Thickness(14)); box.Margin = new Thickness(0, 0, 0, 12); return box;
        }

        void ApplyStationHighTiers()
        {
            foreach (var trade in catalog.Trades.Where(t => t.Station == station))
            {
                if (trade.Tier >= 3 && Calculator.Target(state, trade) == 0) state.Targets[trade.Id] = trade.DefaultQuantity;
                Calculator.SetSelected(state, trade, trade.Tier >= 3);
            }
            undo = null; footerActions.Children.Clear(); Persist(); RefreshProgress();
        }

        FrameworkElement BuildStationOptions()
        {
            var body = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            var options = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, -12, 0) };
            options.SizeChanged += delegate { options.Columns = options.ActualWidth >= 980 ? 5 : 3; };
            AddStationOption(options, "마스터리", "rank", tradePlanningData.Ranks, () => state.TradeSettings.RankId, value => state.TradeSettings.RankId = value);
            AddStationOption(options, "보증서", "warranty", tradePlanningData.Warranties, () => state.TradeSettings.WarrantyId, value => state.TradeSettings.WarrantyId = value);
            AddStationOption(options, "그랜드마스터 상인", "grandmaster", tradePlanningData.GrandMasters, () => state.TradeSettings.GrandMasterId, value => state.TradeSettings.GrandMasterId = value);
            AddStationOption(options, "운송수단", "ride", tradePlanningData.Rides, () => state.TradeSettings.RideId, value => state.TradeSettings.RideId = value);
            AddStationOption(options, "파트너", "partner", tradePlanningData.Partners, () => state.TradeSettings.PartnerId, value => state.TradeSettings.PartnerId = value);
            body.Children.Add(options);
            stationOptionsStatus = T("", 11, Green, false); body.Children.Add(stationOptionsStatus);
            stationValueUpdates.Add(delegate { var capacity = tradePlanning.Capacity(state.TradeSettings); stationOptionsStatus.Text = capacity.IsValid ? "운송 " + capacity.Slots + "칸 · 무게 " + Calculator.FormatQuantity(capacity.Weight) + "  /  판매 예상은 참고 평균가 기준입니다." : capacity.Warning; });
            body.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 16, 0, 16) });
            body.Children.Add(BuildStationPresetControls());
            var expander = new Expander { Header = T("교역 보너스 · 운송 설정 · 프리셋", 13, Ink, true), Content = body, IsExpanded = stationOptionsExpanded, FontWeight = FontWeights.Normal, Padding = new Thickness(0), Foreground = Ink, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            expander.Expanded += delegate { stationOptionsExpanded = true; }; expander.Collapsed += delegate { stationOptionsExpanded = false; };
            var card = Box(expander, AppTheme.Surface, 10, new Thickness(18)); card.BorderBrush = Line; card.BorderThickness = new Thickness(1); card.Margin = new Thickness(0, 0, 0, 14); return card;
        }

        void AddStationOption(Panel parent, string label, string key, List<TradePlanningOption> options, Func<string> get, Action<string> set)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, 12, 12) }; column.Children.Add(T(label, 11, Muted, false));
            var combo = new ComboBox { ItemsSource = options, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = get(), Margin = new Thickness(0, 6, 0, 0) };
            StyleStationCombo(combo, label + " 선택");
            combo.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("SelectedItem.Name") { Source = combo });
            AutomationProperties.SetName(combo, "교역 설정 " + key); stationOptionControls[key] = combo;
            combo.SelectionChanged += delegate(object sender, SelectionChangedEventArgs e) {
                // When SelectedValue initiates selection, WPF raises this event
                // before that property has received its new value. The event's
                // added item already identifies the option being selected.
                var chosen = e.AddedItems.OfType<TradePlanningOption>().FirstOrDefault();
                if (chosen == null || get() == chosen.Id) return;
                set(chosen.Id); undo = null; footerActions.Children.Clear(); Persist(); UpdateStationValues();
            };
            column.Children.Add(combo); parent.Children.Add(column);
        }

        void ApplyNamedStationPreset(string name)
        {
            ApplyStationQuantities(tradePlanning.CreatePreset(catalog, name), name);
        }

        void ApplyStationQuantities(Dictionary<string, int> quantities, string name)
        {
            if (!SaveProgressCheckpoint("교역 프리셋 " + name + " 적용 전")) return;
            foreach (var trade in catalog.Trades)
            {
                int quantity; if (!quantities.TryGetValue(trade.Id, out quantity)) quantity = 0;
                quantity = Math.Max(0, Math.Min(trade.Limit, quantity));
                if (quantity > 0) SetTarget(trade, quantity, false, false);
                Calculator.SetSelected(state, trade, quantity > 0);
            }
            delayedRefresh.Stop(); undo = null; footerActions.Children.Clear(); Persist(); RenderStats();
            if (procurementDialog != null) RenderProcurementDetail();
            footerMessage.Text = name + " 프리셋을 적용했습니다. 재료별 구매·제작 방식은 유지합니다.";
        }

        void SaveStationPreset()
        {
            string name = (stationPresetName.Text ?? "").Trim();
            if (name.Length == 0 || name.Length > 40) { footerMessage.Text = "프리셋 이름을 1~40자로 입력하세요."; return; }
            if (state.TradePresets.Any(p => String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) { footerMessage.Text = "이미 있는 프리셋 이름입니다. 다른 이름을 입력하세요."; return; }
            var item = new TradePlanPreset { Name = name, Quantities = catalog.Trades.ToDictionary(t => t.Id, t => Calculator.EffectiveTarget(state, t)) };
            state.TradePresets.Add(item); undo = null; footerActions.Children.Clear(); Persist(); savedPresetControl.ItemsSource = null; savedPresetControl.ItemsSource = state.TradePresets; savedPresetControl.SelectedItem = item; stationPresetName.Clear();
            RefreshStationPresetForm(); stationPresetFeedback.Text = "‘" + name + "’ 프리셋을 저장했습니다."; stationPresetFeedback.Foreground = Green;
            footerMessage.Text = name + "에 현재 품목 선택과 수량을 저장했습니다.";
        }

        void DeleteStationPreset()
        {
            var item = savedPresetControl.SelectedItem as TradePlanPreset; if (item == null) return;
            if (MessageBox.Show(this, "'" + item.Name + "' 프리셋을 삭제할까요?", "프리셋 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            state.TradePresets.Remove(item); undo = null; footerActions.Children.Clear(); Persist(); savedPresetControl.ItemsSource = null; savedPresetControl.ItemsSource = state.TradePresets;
            RefreshStationPresetForm();
        }
    }
}
