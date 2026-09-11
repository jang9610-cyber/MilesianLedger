using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        AuctionService auction;
        AuctionSettings auctionSettings;
        Button auctionRefreshButton;
        Button auctionAllRefreshButton;
        TextBlock auctionStatus;
        TextBlock auctionSummaryStatus;
        CancellationTokenSource auctionCancellation;
        bool auctionClosing;
        bool auctionRefreshing;
        bool auctionHasInjectedService;
        Task auctionRefreshTask = Task.FromResult(0);

        void InitializeAuction(AuctionService injected)
        {
            string directory = Path.GetDirectoryName(store.FilePath);
            auctionSettings = AuctionSettings.Load(Path.Combine(directory, "auction-settings.json"));
            auctionHasInjectedService = injected != null;
            auction = injected ?? new AuctionService(directory, auctionSettings);
            Closing += delegate { auctionClosing = true; if (auctionCancellation != null) auctionCancellation.Cancel(); CloseAuctionLoading(); };
            Closed += delegate { auction.Dispose(); };
        }

        UIElement BuildAuctionHeader()
        {
            var panel = new StackPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            auctionAllRefreshButton = Btn("전체 시세 갱신", delegate { auctionRefreshTask = RefreshAuctionFromButtonAsync(true); }, false);
            auctionAllRefreshButton.ToolTip = "교역품 선택이나 구매·제작·직접 확보 설정과 관계없이 모든 교환재료와 지원하는 제작법의 하위재료를 조회합니다. NPC 구매품은 제외합니다.";
            actions.Children.Add(auctionAllRefreshButton);
            auctionRefreshButton = Btn("구매품목만 갱신", delegate { auctionRefreshTask = RefreshAuctionFromButtonAsync(false); }, true);
            auctionRefreshButton.ToolTip = "현재 선택한 교역 계획의 경매장 구매 목록만 조회합니다. 제작 완성품·직접 확보품·상세창 미리보기 재료·NPC 구매품은 제외합니다.";
            actions.Children.Add(auctionRefreshButton);
            actions.Children.Add(Btn("주간 리셋", ResetWeek, false)); panel.Children.Add(actions);
            auctionStatus = T(auction.IsConfigured ? "버튼으로만 조회 · 자동 갱신 없음" : auction.ConfigurationMessage, 10, Muted, false);
            if (!String.IsNullOrEmpty(auction.Notice)) auctionStatus.Text = auction.Notice;
            auctionStatus.HorizontalAlignment = HorizontalAlignment.Right; auctionStatus.MaxWidth = 285; auctionStatus.Margin = new Thickness(0, 8, 8, 0); panel.Children.Add(auctionStatus);
            return panel;
        }

        async Task RefreshAuctionFromButtonAsync(bool allMaterials = false)
        {
            // Even an offline host invoking a button outside the dispatcher
            // loop must capture the UI context for completion and cleanup.
            if (!Dispatcher.CheckAccess() || !(SynchronizationContext.Current is DispatcherSynchronizationContext))
            {
                await Dispatcher.InvokeAsync(new Func<Task>(delegate { return RefreshAuctionFromButtonAsync(allMaterials); })).Task.Unwrap().ConfigureAwait(false);
                return;
            }
            // The ONLY UI-to-network entry point. No render, search, selection,
            // startup, settings-save or local timer may call this method.
            if (auctionRefreshing)
            {
                return; // Two refresh buttons never create overlapping requests.
            }
            // Freeze the chosen scope at the explicit click, never on preview.
            var names = allMaterials ? procurementPlanner.GetAllQuoteNames() : GetAuctionMaterialSnapshot();
            string scope = allMaterials ? "전체 시세" : "구매품목 시세";
            if (names.Length == 0) { auctionStatus.Text = "현재 계획에 경매장 구매품목이 없습니다."; return; }
            if (!auction.IsConfigured)
            {
                auctionStatus.Text = auction.ConfigurationMessage;
                footerMessage.Text = "시세 서버 연결을 준비 중입니다. 교역 계획과 체크리스트는 계속 사용할 수 있습니다.";
                return;
            }
            auctionRefreshing = true;
            auctionCancellation = new CancellationTokenSource();
            UpdateAuctionRefreshButtons();
            auctionStatus.Text = scope + " · " + names.Length + "종 조회 시작";
            var currentCancellation = auctionCancellation;
            var progress = new Progress<AuctionRefreshProgress>(p => {
                // A test host or a caller outside the dispatcher loop may have no
                // SynchronizationContext. Always marshal WPF changes explicitly.
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                    if (auctionClosing || !auctionRefreshing || !Object.ReferenceEquals(auctionCancellation, currentCancellation)) return;
                    auctionStatus.Text = p.Completed + " / " + p.Total + "종 · " + p.Requests + "회 요청\n" + p.Material;
                    if (auctionLoading != null) auctionLoading.Report(p);
                }));
            });
            try
            {
                ShowAuctionLoading(names.Length, scope);
                AuctionRefreshResult result = await auction.RefreshAsync(names, progress, auctionCancellation.Token);
                if (!auctionClosing)
                {
                    string status = result.Cancelled ? "갱신 중지" : result.FailedMaterials > 0 ? "갱신 종료" : "갱신 완료";
                    auctionStatus.Text = scope + " " + status + " · " + result.UpdatedMaterials + "종 갱신 · " + result.Requests + "회 요청" + (result.FailedMaterials > 0 ? "\n실패·미갱신 " + result.FailedMaterials + "종" : "");
                    if (!String.IsNullOrEmpty(result.StoppedReason)) footerMessage.Text = result.StoppedReason;
                    else if (!String.IsNullOrEmpty(auction.Notice)) footerMessage.Text = auction.Notice;
                    else if (result.FailedMaterials > 0) footerMessage.Text = "일부 재료를 갱신하지 못했습니다. 가격의 조회 상태를 확인하세요.";
                    else footerMessage.Text = "경매장 가격을 저장했습니다. 이후 화면 변경에는 저장된 가격을 사용합니다.";
                    if (summaryView) RenderSummary();
                    UpdateStationValues();
                }
            }
            catch (OperationCanceledException) { if (!auctionClosing) auctionStatus.Text = "갱신을 중지했습니다."; }
            catch { if (!auctionClosing) { auctionStatus.Text = "갱신하지 못했습니다. 잠시 후 버튼으로 다시 시도하세요."; footerMessage.Text = "이전에 저장한 가격이 있으면 유지합니다."; } }
            finally
            {
                auctionRefreshing = false;
                CloseAuctionLoading();
                if (auctionCancellation != null) auctionCancellation.Dispose();
                auctionCancellation = null;
                if (!auctionClosing)
                {
                    UpdateAuctionRefreshButtons();
                    if (procurementDialog != null) RenderProcurementDetail();
                }
            }
        }

        string[] GetAuctionMaterialSnapshot()
        {
            return procurementPlanner.Build(state).Purchases.Where(m => m.Quantity > 0).Select(m => m.Name)
                .Where(name => !String.IsNullOrWhiteSpace(name) && !procurementPlanner.IsNpcPurchase(name))
                .Distinct(StringComparer.Ordinal).ToArray();
        }

        static string Gold(decimal value) { return value.ToString("#,0.##", CultureInfo.InvariantCulture) + " G"; }
        void UpdateAuctionRefreshButtons()
        {
            foreach (var button in new[] { auctionRefreshButton, auctionAllRefreshButton, procurementDetailRefreshButton, procurementDetailAllRefreshButton })
                if (button != null) button.IsEnabled = !auctionRefreshing;
            RefreshPipInteractivity();
        }
        static string QuoteStatus(AuctionQuote q)
        {
            if (q == null) return "미조회";
            if (q.Status == "empty") return "매물 없음";
            if (q.Status == "partial") return "일부 매물";
            if (q.Status == "error") return q.UnitPrice.HasValue ? "이전 가격 · 갱신 실패" : "조회 실패";
            if (q.Status == "cancelled") return q.UnitPrice.HasValue ? "이전 가격 · 중지" : "조회 중지";
            if (q.Status == "skipped") return q.UnitPrice.HasValue ? "이전 가격 · 미갱신" : "미갱신";
            return "저장된 가격";
        }
        static string QuoteTooltip(AuctionQuote q)
        {
            if (q == null) return "전체 시세 또는 구매품목만 갱신 버튼으로 조회하세요. 검색이나 품목 선택으로는 요청하지 않습니다.";
            return "경매장 검색명: " + q.SearchName + "\n" + QuoteStatus(q) +
                (q.PriceUtc.HasValue ? "\n가격 조회: " + q.PriceUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "") +
                "\n조회 매물: " + q.ListingCount + "건 / " + q.Pages + "페이지" +
                "\n조회된 매물 중 가장 낮은 개당 가격입니다. 필요한 수량 전체를 이 가격에 살 수 있다는 뜻은 아닙니다." +
                (String.IsNullOrEmpty(q.Message) ? "" : "\n" + q.Message);
        }
        UIElement AuctionPriceCell(string name, decimal quantity, bool total)
        {
            AuctionQuote quote = auction.GetQuote(name);
            var panel = new StackPanel { Margin = new Thickness(6, 9, 10, 9), VerticalAlignment = VerticalAlignment.Center, ToolTip = QuoteTooltip(quote) };
            bool priced = quote != null && quote.UnitPrice.HasValue;
            var price = T(priced ? Gold(quote.UnitPrice.Value * quantity) : total ? "—" : QuoteStatus(quote), 11, priced ? Ink : Muted, priced); price.TextAlignment = TextAlignment.Right; panel.Children.Add(price);
            if (!total && priced)
            {
                var status = T(QuoteStatus(quote), 9, quote.Status == "ok" ? Muted : B("#A16E25"), false); status.TextAlignment = TextAlignment.Right; status.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(status);
                if (quote.PriceUtc.HasValue) { var stamp = T(quote.PriceUtc.Value.ToLocalTime().ToString("MM-dd HH:mm"), 9, Muted, false); stamp.TextAlignment = TextAlignment.Right; panel.Children.Add(stamp); }
            }
            return panel;
        }
        void UpdateAuctionSummary(List<MaterialTotal> materials)
        {
            if (auctionSummaryStatus == null) return;
            int count = 0; decimal sum = 0; int incomplete = 0;
            foreach (var m in materials)
            {
                AuctionQuote q = auction.GetQuote(m.Name);
                if (q == null || !q.UnitPrice.HasValue) continue;
                count++; sum += q.UnitPrice.Value * m.Quantity;
                if (q.Status != "ok") incomplete++;
            }
            auctionSummaryStatus.Text = materials.Count == 0 ? "현재 계획에서 구매할 재료가 없습니다." : count == 0 ? "구매품목만 갱신 버튼을 누르면 구매품의 조회 단가와 예상 금액을 표시합니다." :
                "구매품 가격 확인 " + count + " / " + materials.Count + "종 · 확인된 구매 예상 합계 " + Gold(sum) + (incomplete > 0 ? " (이전·일부 조회 가격 포함)" : "");
            auctionSummaryStatus.ToolTip = "현재 계획에서 구매할 재료 수량 × 조회된 매물의 최저 개당 가격입니다. 직접 제작하는 완성품과 직접 확보할 재료는 구매 합계에 넣지 않습니다. 미조회·매물 없음은 합계에서 제외합니다. API 반영 지연이나 매물 수량에 따라 실제 구매 비용과 다를 수 있습니다.";
        }
        string AuctionExportColumns(MaterialTotal material)
        {
            AuctionQuote q = auction.GetQuote(material.Name);
            if (q == null || !q.UnitPrice.HasValue) return "\t\t\t" + QuoteStatus(q);
            return "\t" + Gold(q.UnitPrice.Value) + "\t" + Gold(q.UnitPrice.Value * material.Quantity) + "\t" + QuoteStatus(q) + (q.PriceUtc.HasValue ? " " + q.PriceUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "");
        }

        public string RunAuctionUiChecks(Func<int> requests, string directory)
        {
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Auction checks require an isolated store and offline service.");
            if (requests() != 0) throw new Exception("Startup requested HTTP.");
            var original = StateStore.Copy(state); int originalTab = summaryTab;
            try
            {
                state = new ProgressState(); state.Normalize(catalog);
                foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, false);
                ShowSummary();
                auctionRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAuctionTestTask();
                if (requests() != 0 || auctionLoading != null) throw new Exception("Empty purchase scope requested HTTP or opened loading.");
                if (AuctionTestChildren<PasswordBox>(this).Any() ||
                    AuctionTestChildren<Button>(this).Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "경매장 API 설정 메뉴"))
                    throw new Exception("Desktop API credential settings must not be exposed.");
                if (!auction.IsConfigured || requests() != 0) throw new Exception("Injected proxy must be ready without requesting data at startup.");
                var allNames = procurementPlanner.GetAllQuoteNames();
                if (allNames.Length < 80 || allNames.Length != allNames.Distinct().Count() || allNames.Any(procurementPlanner.IsNpcPurchase)) throw new Exception("Full scope missing recipes, duplicated names or included NPC goods.");
                foreach (string name in new[] { "매듭끈", "가는 실뭉치", "굵은 실뭉치", "거미줄", "나무판", "스태미나 500 포션", "아라트의 결정", "축복의 포션", "실리엔 결정" })
                    if (!allNames.Contains(name)) throw new Exception("Full scope omitted " + name);
                // Full refresh works with no selected trades and leaves all choices unchanged.
                string unchanged = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state);
                auctionAllRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAuctionTestTask();
                if (requests() != allNames.Length || unchanged != new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state)) throw new Exception("Full refresh request scope or plan preservation failed.");
                foreach (string name in allNames) if (auction.GetQuote(name) == null || auction.GetQuote(name).UnitPrice != 100m) throw new Exception("Full scope price missing: " + name);
                var selectedTrade = catalog.Trades.First(t => t.Id == "C6"); IncludeTrade(selectedTrade, true); SetTarget(selectedTrade, 2);
                SetProcurementChoice("매듭끈", procurementPlanner.GetRecipes("매듭끈").First().Id);
                var purchaseNames = GetAuctionMaterialSnapshot();
                if (!new HashSet<string>(purchaseNames).SetEquals(new[] { "가는 실뭉치", "굵은 실뭉치", "스태미나 500 포션" })) throw new Exception("Purchase scope must exclude crafted parent.");
                int before = requests();
                OpenProcurementDetail("매듭끈"); procurementDialog.UpdateLayout();
                if (requests() != before || !new HashSet<string>(purchaseNames).SetEquals(GetAuctionMaterialSnapshot())) throw new Exception("Preview expanded purchase scope or requested HTTP.");
                procurementDetailRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAuctionTestTask();
                if (requests() != before + purchaseNames.Length) throw new Exception("Detail purchase button did not honor purchase-only scope.");
                procurementDialog.Close();
                SetProcurementChoice("가는 실뭉치", procurementPlanner.GetRecipes("가는 실뭉치").First().Id);
                SetProcurementChoice("거미줄", "acquire");
                var nestedNames = GetAuctionMaterialSnapshot();
                if (nestedNames.Contains("매듭끈") || nestedNames.Contains("가는 실뭉치") || nestedNames.Contains("거미줄") || !nestedNames.Contains("굵은 실뭉치")) throw new Exception("Purchase scope included crafted or directly acquired nodes.");
                if (!purchaseNames.Contains("가는 실뭉치")) throw new Exception("Earlier snapshot mutated with the plan.");
                before = requests();
                auctionRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAuctionTestTask();
                if (requests() != before + nestedNames.Length) throw new Exception("Header purchase refresh queried inactive descendants.");
                // Detail full refresh must ignore both selections and acquisition choices.
                OpenProcurementDetail("매듭끈"); procurementDialog.UpdateLayout();
                unchanged = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state); before = requests();
                procurementDetailAllRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAuctionTestTask();
                if (requests() != before + allNames.Length || unchanged != new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state)) throw new Exception("Detail full refresh changed plan or omitted comparison prices.");
                if (!new HashSet<string>(allNames).SetEquals(procurementPlanner.GetAllQuoteNames())) throw new Exception("Full scope depends on choices.");
                var plan = procurementPlanner.Build(state); summaryTab = 1; RenderSummary(); UpdateAuctionSummary(plan.Purchases);
                if (!auctionSummaryStatus.Text.Contains(Gold(plan.Purchases.Sum(p => p.Quantity * 100m)))) throw new Exception("Full comparison prices inflated purchase totals.");
                if (!auctionRefreshButton.IsEnabled || !auctionAllRefreshButton.IsEnabled || !procurementDetailRefreshButton.IsEnabled || !procurementDetailAllRefreshButton.IsEnabled) throw new Exception("Refresh buttons did not reset.");
                procurementDialog.Width = 650; procurementDialog.UpdateLayout(); SaveProcurementDialogPreview(Path.Combine(directory, "auction-dual-detail.png"));
                procurementDialog.Close(); Width = 1100; Height = 740; ShowStationHub(); UpdateLayout(); SavePreview(Path.Combine(directory, "auction-dual-header.png"));
                before = requests();
                foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, false);
                ShowSummary(); summarySearch.Text = "포션"; summarySearch.Text = "";
                auctionRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAuctionTestTask();
                if (requests() != before || auctionLoading != null) throw new Exception("Empty purchase refresh or ordinary UI requested HTTP.");
                return "PASS auction UI: two explicit scopes; full refresh covers " + allNames.Length + " names across all recipe alternatives even without selection; purchase-only excludes crafted/direct/NPC/preview items; header and detail buttons agree; state and purchase sums preserved; no automatic HTTP.";
            }
            finally { if (procurementDialog != null) procurementDialog.Close(); state = original; summaryTab = originalTab; summaryQuery = ""; Persist(); }
        }

        void PumpAuctionTestTask()
        {
            if (!auctionRefreshTask.IsCompleted)
            {
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; DateTime until = DateTime.UtcNow.AddSeconds(10);
                timer.Tick += delegate { if (auctionRefreshTask.IsCompleted || DateTime.UtcNow > until) frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            }
            if (!auctionRefreshTask.IsCompleted) throw new Exception("Offline auction UI test timed out.");
            auctionRefreshTask.GetAwaiter().GetResult();
        }
        static IEnumerable<TControl> AuctionTestChildren<TControl>(DependencyObject root) where TControl : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i); var match = child as TControl;
                if (match != null) yield return match;
                foreach (var descendant in AuctionTestChildren<TControl>(child)) yield return descendant;
            }
        }
    }
}
