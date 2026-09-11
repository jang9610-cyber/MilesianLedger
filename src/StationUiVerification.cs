using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        public string RunStationUiChecks(Func<int> requests, string directory)
        {
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Station UI checks require a temporary store and offline injected service.");
            var original = StateStore.Copy(state); var originalUndo = undo; string originalStation = station; var originalSelected = selected;
            bool originalOverview = stationOverview, originalSummary = summaryView, originalOptions = stationOptionsExpanded, originalPrompts = procurementSharedPromptsEnabled;
            double originalWidth = Width, originalHeight = Height; int before = requests();
            try
            {
                if (procurementDialog != null) procurementDialog.Close(); procurementSharedPromptsEnabled = false;
                state = new ProgressState(); state.Normalize(catalog); Width = 1400; Height = 940; stationOptionsExpanded = false;
                ShowStationHub(); PumpProcurementInteractionLayout();
                if (stationCardButtons.Count != 4 || !stationOverview || summaryView) throw new Exception("Station hub must show four station cards.");
                if (AuctionTestChildren<Button>(nav).Count(b => AutomationProperties.GetName(b) == "교역 계획 메뉴") != 1 || AuctionTestChildren<Button>(nav).Any(b => stations.Contains(b.Content as string)))
                    throw new Exception("Sidebar must provide one unified station entry, not four separate entries.");
                foreach (string region in stations)
                {
                    ShowStationHub(); stationCardButtons[region].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpProcurementInteractionLayout();
                    if (stationOverview || summaryView || station != region || tradeControls.Count != 5) throw new Exception("Station card failed to open its five trade items: " + region);
                }
                var shrimpTrade = catalog.Trades.Single(t => t.Id == "C31");
                ShowStation(shrimpTrade.Station); selected = shrimpTrade; RenderAll();
                tradeControls[shrimpTrade.Id].IsChecked = true; tradeControls[shrimpTrade.Id].RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                SetProcurementChoice("새우 조련 미끼", procurementPlanner.GetRecipes("새우 조련 미끼").First().Id);
                if (!procurementPlan.Acquisitions.Any(r => r.Name == "설탕") || procurementPlan.Purchases.Any(r => ProcurementPlanner.NpcPurchaseNames.Contains(r.Name)))
                    throw new Exception("Station entry failed to integrate NPC-only bait ingredients.");
                OpenProcurementDetail("새우 조련 미끼"); PumpProcurementInteractionLayout();
                if (procurementDetailQuoteNames.Any(ProcurementPlanner.NpcPurchaseNames.Contains)) throw new Exception("Station preparation entry exposed NPC quote names.");
                procurementDialog.Close();
                ShowStationHub(); stationOptionsExpanded = true; RenderAll(); PumpProcurementInteractionLayout();
                if (stationOptionControls.Count != 5) throw new Exception("Five common trade/transport options must be available.");
                var namePlaceholder = AuctionTestChildren<TextBlock>(stationPresetName.Parent).FirstOrDefault(t => (t.Text ?? "").StartsWith("예:") && !t.IsHitTestVisible);
                if (stationPresetName.Text != "" || stationPresetName.MaxLength != 40 || namePlaceholder == null || namePlaceholder.Visibility != Visibility.Visible
                    || stationPresetButtons["save"].IsEnabled || savedPresetControl.IsEnabled || stationPresetButtons["load"].IsEnabled || stationPresetButtons["delete"].IsEnabled
                    || String.IsNullOrWhiteSpace(stationSavedPresetHint.Text))
                    throw new Exception("Empty preset form must show separate name guidance and disable saving and empty-list actions.");
                stationPresetName.BringIntoView(); PumpProcurementInteractionLayout(); SavePreview(Path.Combine(directory, "station-preset-empty.png"));
                stationPresetName.Text = "   ";
                if (stationPresetButtons["save"].IsEnabled || state.TradePresets.Count != 0) throw new Exception("Whitespace must not enable or create a named preset.");
                stationPresetName.Text = "주말 고티어 교역"; PumpProcurementInteractionLayout();
                if (!stationPresetButtons["save"].IsEnabled || namePlaceholder.Visibility != Visibility.Collapsed || stationPresetName.Text != "주말 고티어 교역"
                    || state.TradePresets.Count != 0)
                    throw new Exception("A valid typed name must replace the visual placeholder without saving it automatically.");
                SavePreview(Path.Combine(directory, "station-preset-entry.png")); stationPresetName.Clear();
                if (stationPresetButtons["save"].IsEnabled || namePlaceholder.Visibility != Visibility.Visible) throw new Exception("Clearing the name must restore its placeholder and disable saving.");
                decimal previousGross = tradePlanning.Summarize(catalog, state, state.TradeSettings).GrossGold;
                var rankControl = stationOptionControls["rank"]; var bestRank = tradePlanningData.Ranks.OrderByDescending(o => o.Percent).First();
                string previousRank = state.TradeSettings.RankId;
                Func<string> rankDiagnostic = delegate {
                    var chosen = rankControl.SelectedItem as TradePlanningOption;
                    return " before=" + previousGross + ", after=" + tradePlanning.Summarize(catalog, state, state.TradeSettings).GrossGold
                        + ", settings=" + state.TradeSettings.RankId + ", SelectedValue=" + rankControl.SelectedValue
                        + ", SelectedItem=" + (chosen == null ? "<null>" : chosen.Id + " / " + chosen.Name);
                };
                rankControl.SelectedValue = bestRank.Id;
                if (state.TradeSettings.RankId != bestRank.Id || tradePlanning.Summarize(catalog, state, state.TradeSettings).GrossGold <= previousGross)
                    throw new Exception("Rank selector failed to increase the reference sales estimate immediately." + rankDiagnostic());
                if (!AuctionTestChildren<TextBlock>(content).Any(t => (t.Text ?? "").Contains("평균가 기준 판매 " + Gold(tradePlanning.Summarize(catalog, state, state.TradeSettings).GrossGold))))
                    throw new Exception("Rank selector changed state without immediately updating visible sales text." + rankDiagnostic());
                if (store.Load(catalog).TradeSettings.RankId != bestRank.Id || !Object.ReferenceEquals(rankControl, stationOptionControls["rank"]))
                    throw new Exception("Rank selection did not immediately persist or preserve its control." + rankDiagnostic());
                rankControl.SelectedItem = tradePlanningData.Ranks.Single(o => o.Id == previousRank);
                if (state.TradeSettings.RankId != previousRank || tradePlanning.Summarize(catalog, state, state.TradeSettings).GrossGold != previousGross)
                    throw new Exception("SelectedItem rank change did not immediately restore the original estimate." + rankDiagnostic());
                rankControl.SelectedIndex = tradePlanningData.Ranks.IndexOf(bestRank);
                if (state.TradeSettings.RankId != bestRank.Id || tradePlanning.Summarize(catalog, state, state.TradeSettings).GrossGold <= previousGross)
                    throw new Exception("SelectedIndex rank change did not immediately restore the bonus estimate." + rankDiagnostic());
                var originalRankOption = tradePlanningData.Ranks.Single(o => o.Id == previousRank);
                CheckStationComboPopup(rankControl, originalRankOption, originalRankOption.Name);
                if (state.TradeSettings.RankId != previousRank || tradePlanning.Summarize(catalog, state, state.TradeSettings).GrossGold != previousGross)
                    throw new Exception("Selecting a real styled dropdown item failed to update the setting and sales estimate.");
                rankControl.SelectedValue = bestRank.Id;
                var limitedPartner = tradePlanningData.Partners.First(o => !String.IsNullOrEmpty(o.RequiredRideId));
                stationOptionControls["ride"].SelectedValue = tradePlanningData.Rides.First(o => o.Id != limitedPartner.RequiredRideId).Id;
                stationOptionControls["partner"].SelectedValue = limitedPartner.Id;
                if (tradePlanning.Capacity(state.TradeSettings).IsValid || !stationOptionsStatus.Text.Contains("알파카")) throw new Exception("Unsupported alpaca pairing lacks a clear warning.");
                stationOptionControls["ride"].SelectedValue = limitedPartner.RequiredRideId;
                if (!tradePlanning.Capacity(state.TradeSettings).IsValid || stationOptionsStatus.Text.Contains("함께")) throw new Exception("Correcting the transport pairing failed to clear the warning.");
                if (AuctionTestChildren<ComboBox>(content).Count() != 6 || AuctionTestChildren<TextBlock>(content).Any(t => (t.Text ?? "").Contains("프리셋 교역소")))
                    throw new Exception("Preset station selectors must be absent; only five common settings and the saved preset selector remain.");
                OpenProcurementDetail("새우 조련 미끼"); PumpProcurementInteractionLayout();
                stationPresetButtons["체리피커"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (procurementRootView != null || procurementDetailQuoteNames.Count != 0) throw new Exception("Preset left an excluded material's old detail and quote scope visible.");
                procurementDialog.Close();
                if (catalog.Trades.Count(t => Calculator.EffectiveTarget(state, t) > 0) != 12 || catalog.Trades.Any(t => Calculator.EffectiveTarget(state, t) != (t.Tier >= 3 ? t.Limit : 0)))
                    throw new Exception("Cherry-picker preset must select all four stations' upper three tiers at their weekly maximum.");
                var customQuantities = catalog.Trades.ToDictionary(t => t.Id, t => t.Tier == Array.IndexOf(stations, t.Station) + 1 ? t.Tier + 1 : 0);
                ApplyStationQuantities(customQuantities, "서로 다른 티어와 수량");
                var expected = catalog.Trades.ToDictionary(t => t.Id, t => Calculator.EffectiveTarget(state, t));
                stationPresetName.Text = "  검증용 교역  ";
                if (!stationPresetButtons["save"].IsEnabled) throw new Exception("A unique trimmed name must enable preset saving.");
                stationPresetButtons["save"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var savedPreset = savedPresetControl.SelectedItem as TradePlanPreset;
                if (state.TradePresets.Count != 1 || savedPreset == null || savedPreset.Name != "검증용 교역"
                    || !Object.ReferenceEquals(savedPreset, state.TradePresets[0]) || catalog.Trades.Any(t => savedPreset.Quantities[t.Id] != expected[t.Id])
                    || stationPresetName.Text != "" || stationPresetButtons["save"].IsEnabled || !savedPresetControl.IsEnabled
                    || !stationPresetButtons["load"].IsEnabled || !stationPresetButtons["delete"].IsEnabled
                    || !stationPresetFeedback.Text.Contains("검증용 교역") || !stationPresetFeedback.Text.Contains("저장"))
                    throw new Exception("Saving must persist the exact trimmed name and quantities, clear the entry, select the saved preset and confirm success.");
                stationPresetName.Text = "  검증용 교역  ";
                if (stationPresetButtons["save"].IsEnabled || !stationPresetFeedback.Text.Contains("같은 이름") || state.TradePresets.Count != 1)
                    throw new Exception("A duplicate name must show feedback and disable saving without changing the existing preset.");
                stationPresetName.Clear(); savedPresetControl.SelectedIndex = -1;
                if (!savedPresetControl.IsEnabled || stationPresetButtons["load"].IsEnabled || stationPresetButtons["delete"].IsEnabled)
                    throw new Exception("A populated list without a selection must allow choosing while disabling load and delete.");
                CheckStationComboPopup(savedPresetControl, savedPreset, savedPreset.Name);
                if (!stationPresetButtons["load"].IsEnabled || !stationPresetButtons["delete"].IsEnabled)
                    throw new Exception("Selecting a real saved-preset dropdown item must enable load and delete.");
                stationPresetButtons["성실한 상인"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (catalog.Trades.Count(t => Calculator.EffectiveTarget(state, t) > 0) != 20 || catalog.Trades.Any(t => Calculator.EffectiveTarget(state, t) != t.Limit)) throw new Exception("Diligent preset must fill all twenty items across four stations.");
                stationPresetButtons["극한의 한탕"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (catalog.Trades.Count(t => Calculator.EffectiveTarget(state, t) > 0) != 10 || catalog.Trades.Sum(t => Calculator.EffectiveTarget(state, t)) != 54
                    || catalog.Trades.Any(t => Calculator.EffectiveTarget(state, t) != tradePlanning.GetItem(t.Id).ExtremeQuantity)) throw new Exception("Extreme preset must replace the entire twenty-item selection with its fixed global quantities.");
                stationPresetButtons["load"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (catalog.Trades.Any(t => Calculator.EffectiveTarget(state, t) != expected[t.Id]) || procurementPlanner.GetChoice(state, "새우 조련 미끼") == "buy")
                    throw new Exception("Custom preset must restore selection/quantities without changing preparation choices.");
                var reloaded = store.Load(catalog);
                if (reloaded.TradePresets.Count != 1 || reloaded.TradeSettings.RankId != state.TradeSettings.RankId || reloaded.TradeSettings.RideId != state.TradeSettings.RideId)
                    throw new Exception("Trade options and custom presets failed their save/load round trip.");
                stationOptionsExpanded = false; RenderAll(); PumpProcurementInteractionLayout();
                if (stationCosts.Stations.Values.Sum(s => s.Planned.KnownCost) != stationCosts.Planned.KnownCost) throw new Exception("Station cards double-counted shared preparation costs.");
                foreach (var step in ProcurementReadiness.GetSteps(procurementPlan, state).Where(s => s.HasExchangeUse)) ProcurementReadiness.SetReady(state, step, true);
                RenderStats();
                if (stationCosts.Remaining.KnownCost != 0 || stationCosts.Remaining.UnknownCount != 0 || percentStat.Text != "100%") throw new Exception("All final items must eliminate additional spending and reach 100%.");
                foreach (var step in ProcurementReadiness.GetSteps(procurementPlan, state).Where(s => s.HasExchangeUse)) ProcurementReadiness.SetReady(state, step, false);
                RenderStats(); PumpProcurementInteractionLayout();
                SavePreview(Path.Combine(directory, "station-hub.png"));
                Width = 1100; Height = 740; RenderAll(); PumpProcurementInteractionLayout(); SavePreview(Path.Combine(directory, "station-hub-compact.png"));
                stationCardButtons["오아시스"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpProcurementInteractionLayout();
                SavePreview(Path.Combine(directory, "station-detail-compact.png"));
                if (content.ActualWidth <= 0 || detail.ActualWidth < 400 || detailScroll.ViewportHeight < 130) throw new Exception("Compact station details lost usable layout space.");
                stationOptionsExpanded = true; ShowStationHub(); PumpProcurementInteractionLayout();
                CheckStationPresetViewport(stationPresetName, stationPresetButtons["save"], stationPresetFeedback);
                SavePreview(Path.Combine(directory, "station-preset-save-compact.png"));
                CheckStationPresetViewport(savedPresetControl, stationPresetButtons["load"], stationPresetButtons["delete"], stationSavedPresetHint);
                SavePreview(Path.Combine(directory, "station-preset-load-compact.png"));
                Width = 1400; Height = 940; stationOptionsExpanded = true; ShowStationHub(); PumpProcurementInteractionLayout(); SavePreview(Path.Combine(directory, "station-options.png"));
                ResetWeek(); stationPresetName.Text = "리셋 후 새 프리셋"; stationPresetButtons["save"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                UndoReset();
                if (undo != null || footerActions.Children.Count != 0 || state.TradePresets.Count != 2) throw new Exception("Weekly undo discarded a newly saved preset.");
                ResetWeek(); var newRank = tradePlanningData.Ranks.First(o => o.Id != state.TradeSettings.RankId).Id;
                stationOptionControls["rank"].SelectedValue = newRank; UndoReset();
                if (undo != null || footerActions.Children.Count != 0 || state.TradeSettings.RankId != newRank) throw new Exception("Weekly undo discarded updated common settings.");
                // Old NPC purchase completions migrate once; unrelated choices stay untouched.
                var legacy = StateStore.Copy(state); legacy.ProcurementChoices["설탕"] = "buy";
                legacy.ProcurementReady["purchase:설탕"] = new ProcurementReadySnapshot { Name = "설탕", Kind = "purchase", Quantity = 5, Context = "purchase" };
                legacy.ProcurementReady["acquire:설탕"] = new ProcurementReadySnapshot { Name = "설탕", Kind = "acquire", Quantity = 2, Context = "acquire" };
                legacy.Normalize(catalog);
                if (legacy.ProcurementChoices.ContainsKey("설탕") || legacy.ProcurementReady.ContainsKey("purchase:설탕") || legacy.ProcurementReady["acquire:설탕"].Quantity != 5)
                    throw new Exception("NPC policy migration must preserve maximum prepared quantity without summing alternative modes.");
                var json = new JavaScriptSerializer(); string normalized = json.Serialize(legacy); legacy.Normalize(catalog);
                if (json.Serialize(legacy) != normalized) throw new Exception("Station/NPC state normalization is not idempotent.");
                if (requests() != before) throw new Exception("Station cards, cost updates, presets or options requested auction HTTP.");
                return "PASS station UI: one sidebar entry and four cards open all 20 trades; NPC bait and preparation paths integrate; five saved common options update in place; real styled dropdown popups and item containers retain selection; empty/valid/duplicate preset form states and exact saved quantities persist; three global presets preserve preparation choices; station costs add to global; final readiness yields zero additional cost; compact preset actions remain inside the scroll viewport; NPC migration preserves stock; zero implicit HTTP.";
            }
            finally
            {
                if (procurementDialog != null) procurementDialog.Close();
                state = original; undo = originalUndo; station = originalStation; selected = originalSelected; summaryView = originalSummary; stationOverview = originalOverview;
                stationOptionsExpanded = originalOptions; procurementSharedPromptsEnabled = originalPrompts; Width = originalWidth; Height = originalHeight; Persist(); RenderAll();
            }
        }

        void CheckStationComboPopup(ComboBox combo, object item, string displayName)
        {
            combo.BringIntoView(); combo.ApplyTemplate(); PumpProcurementInteractionLayout();
            var toggle = AuctionTestChildren<ToggleButton>(combo).FirstOrDefault();
            if (toggle == null) throw new Exception("Styled ComboBox must expose its standard dropdown toggle.");
            var toggleProvider = new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle) as IToggleProvider;
            var expandProvider = new ComboBoxAutomationPeer(combo).GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider;
            if (toggleProvider == null || expandProvider == null) throw new Exception("Styled ComboBox lost its standard toggle or expand/collapse pattern.");
            try
            {
                toggleProvider.Toggle(); PumpProcurementInteractionLayout();
                var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
                var container = combo.ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem;
                if (!combo.IsDropDownOpen || popup == null || !popup.IsOpen || popup.Child == null || container == null)
                    throw new Exception("Dropdown toggle must open a real popup with generated item containers.");
                container.ApplyTemplate(); container.IsSelected = true; PumpProcurementInteractionLayout();
                if (!Object.ReferenceEquals(combo.SelectedItem, item) || !AuctionTestChildren<TextBlock>(container).Any(t => t.Text == displayName))
                    throw new Exception("Styled dropdown item must retain its object selection and DisplayMemberPath label.");
                expandProvider.Collapse(); PumpProcurementInteractionLayout();
                if (combo.IsDropDownOpen || popup.IsOpen || !Object.ReferenceEquals(combo.SelectedItem, item))
                    throw new Exception("Closing a styled dropdown must retain its selected item.");
            }
            finally { combo.IsDropDownOpen = false; }
        }

        void CheckStationPresetViewport(params FrameworkElement[] controls)
        {
            var first = controls[0]; first.BringIntoView(); PumpProcurementInteractionLayout();
            stationHubScroll.ScrollToVerticalOffset(stationHubScroll.VerticalOffset + first.TranslatePoint(new Point(), stationHubScroll).Y - 30);
            PumpProcurementInteractionLayout();
            foreach (var control in controls)
            {
                var point = control.TranslatePoint(new Point(), stationHubScroll);
                if (control.ActualWidth <= 0 || control.ActualHeight <= 0 || point.X < -2 || point.Y < -2
                    || point.X + control.ActualWidth > stationHubScroll.ViewportWidth + 2 || point.Y + control.ActualHeight > stationHubScroll.ViewportHeight + 2)
                    throw new Exception("Expanded compact preset form clipped an action or feedback outside its scroll viewport.");
            }
        }
    }
}
