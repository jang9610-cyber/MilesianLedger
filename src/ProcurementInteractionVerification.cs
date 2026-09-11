using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        public string RunProcurementInteractionChecks(Func<int> requests, string directory)
        {
            // This check operates only on the injected offline service and temporary state.
            // It never loads, creates or deletes a user's auction credential.
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)),
                Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Procurement interaction checks require the offline temporary test window.");
            int before = requests();
            var original = StateStore.Copy(state);
            double originalWidth = Width, originalHeight = Height;
            int originalTab = summaryTab;
            string originalQuery = summaryQuery;
            try
            {
                if (procurementDialog != null) procurementDialog.Close();
                state = new ProgressState(); state.Normalize(catalog);
                foreach (var trade in catalog.Trades) Calculator.SetSelected(state, trade, true);
                Width = 1100; Height = 740; ShowSummary(); PumpProcurementInteractionLayout();
                var summaryScroll = procurementSummaryScroll;
                if (summaryScroll == null || summaryScroll.ScrollableHeight <= 0)
                    throw new Exception("Interaction test requires a scrollable all-selected summary.");
                summaryScroll.ScrollToVerticalOffset(summaryScroll.ScrollableHeight * 0.65);
                PumpProcurementInteractionLayout();
                double summaryOffset = summaryScroll.VerticalOffset;
                var summaryCards = procurementCardButtons.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

                OpenProcurementDetail("매듭끈");
                procurementDialog.WindowStartupLocation = WindowStartupLocation.Manual;
                procurementDialog.Left = -18000; procurementDialog.Top = -18000;
                procurementDialog.Height = 540;
                PumpProcurementInteractionLayout();
                if (!procurementDetailQuoteNames.Contains("거미줄") || !procurementDetailQuoteNames.Contains("양털"))
                    throw new Exception("Collapsed lazy previews must retain their complete quote names before child controls are built.");
                var fullPreviewNames = new HashSet<string>(procurementDetailQuoteNames, StringComparer.Ordinal);
                ExpandProcurementInteractionTree();
                var detailScroll = procurementDetailScroll;
                var acquire = ProcurementInteractionButton("양털 직접 확보 선택");
                var buy = ProcurementInteractionButton("양털 경매장 구매 선택");
                ScrollProcurementInteractionControl(acquire);
                double offset = detailScroll.VerticalOffset;
                double y = acquire.TranslatePoint(new Point(), detailScroll).Y;
                if (offset <= 0 || y < 0 || y + acquire.ActualHeight > detailScroll.ViewportHeight + 2)
                    throw new Exception("The lower material action must actually be visible in a scrolled detail viewport.");

                acquire.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpProcurementInteractionLayout();
                AssertProcurementInteractionPosition(detailScroll, acquire, offset, y, "lower acquisition choice");
                if (!Object.ReferenceEquals(acquire, ProcurementInteractionButton("양털 직접 확보 선택")) ||
                    !Object.ReferenceEquals(buy, ProcurementInteractionButton("양털 경매장 구매 선택")))
                    throw new Exception("Changing a material mode replaced its action controls.");
                buy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpProcurementInteractionLayout();
                AssertProcurementInteractionPosition(detailScroll, acquire, offset, y, "lower purchase choice");

                // A bounded burst catches queued stale layout restores without a wall-time threshold.
                for (int i = 0; i < 12; i++)
                    (i % 2 == 0 ? acquire : buy).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpProcurementInteractionLayout();
                AssertProcurementInteractionPosition(detailScroll, acquire, offset, y, "rapid repeated choices");
                if (procurementPlanner.GetChoice(state, "양털") != "buy")
                    throw new Exception("Rapid actions did not retain the last material choice.");
                AssertProcurementSummaryUnchanged(summaryScroll, summaryOffset, summaryCards);

                var foldedBranch = ProcurementInteractionParent<Expander>(acquire);
                if (foldedBranch == null) throw new Exception("Expected a lower-material accordion.");
                foldedBranch.IsExpanded = false;
                PumpProcurementInteractionLayout();
                ProcurementInteractionButton("가는 실뭉치 직접 제작 선택").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpProcurementInteractionLayout();
                if (foldedBranch.IsExpanded || !AuctionTestChildren<Expander>(procurementDialog).Contains(foldedBranch))
                    throw new Exception("An explicitly collapsed branch was replaced or reopened by a sibling choice.");
                if (!fullPreviewNames.SetEquals(procurementDetailQuoteNames))
                    throw new Exception("Accordion visibility changed the complete preview quote scope.");
                AssertProcurementSummaryUnchanged(summaryScroll, summaryOffset, summaryCards);

                // Navigation in the same modeless window must discard the previous item's offset.
                foldedBranch.IsExpanded = true; PumpProcurementInteractionLayout();
                procurementDetailScroll.ScrollToEnd(); PumpProcurementInteractionLayout();
                if (procurementDetailScroll.VerticalOffset <= 0)
                    throw new Exception("Navigation reset test did not start from a scrolled item.");
                string otherRoot = procurementPlan.Roots.First(n => n.Name != "매듭끈").Name;
                OpenProcurementDetail(otherRoot); PumpProcurementInteractionLayout();
                if (Math.Abs(procurementDetailScroll.VerticalOffset) > 2)
                    throw new Exception("Opening another material inherited the previous item's scroll offset.");
                procurementDialog.Close();
                OpenProcurementDetail("매듭끈");
                procurementDialog.Left = -18000; procurementDialog.Top = -18000;
                PumpProcurementInteractionLayout();
                if (Math.Abs(procurementDetailScroll.VerticalOffset) > 2)
                    throw new Exception("Reopening a material detail did not start at the top.");
                procurementDialog.Close();

                // A crafted gold plate keeps the nested ingot recipe dropdown scrollable.
                SetProcurementChoice("금판", procurementPlanner.GetRecipes("금판").First().Id);
                OpenProcurementDetail("금판");
                procurementDialog.Left = -18000; procurementDialog.Top = -18000; procurementDialog.Height = 540;
                ExpandProcurementInteractionTree();
                var dropdown = AuctionTestChildren<ComboBox>(procurementDialog).FirstOrDefault(c =>
                    (c.ItemsSource as IEnumerable<ProcurementRecipe>) != null &&
                    ((IEnumerable<ProcurementRecipe>)c.ItemsSource).Any(r => r.Id == "synthesis"));
                if (dropdown == null) throw new Exception("Expected the nested ingot recipe dropdown.");
                dropdown.SelectedValue = "ore"; PumpProcurementInteractionLayout();
                if (!AuctionTestChildren<ComboBox>(procurementDialog).Contains(dropdown))
                    throw new Exception("A recipe selection replaced its dropdown control.");
                ScrollProcurementInteractionControl(dropdown);
                var recipeScroll = procurementDetailScroll;
                double recipeOffset = recipeScroll.VerticalOffset;
                double recipeY = dropdown.TranslatePoint(new Point(), recipeScroll).Y;
                if (recipeOffset <= 0) throw new Exception("Recipe dropdown test must run below the detail's top.");
                dropdown.IsDropDownOpen = true; PumpProcurementInteractionLayout();
                dropdown.SelectedValue = "fragments"; dropdown.IsDropDownOpen = false;
                PumpProcurementInteractionLayout();
                AssertProcurementInteractionPosition(recipeScroll, dropdown, recipeOffset, recipeY, "recipe dropdown choice");
                for (int i = 0; i < 8; i++) dropdown.SelectedValue = i % 2 == 0 ? "ore" : "fragments";
                PumpProcurementInteractionLayout();
                AssertProcurementInteractionPosition(recipeScroll, dropdown, recipeOffset, recipeY, "rapid recipe dropdown choices");
                if (!AuctionTestChildren<ComboBox>(procurementDialog).Contains(dropdown) ||
                    !String.Equals(dropdown.SelectedValue as string, "fragments", StringComparison.Ordinal))
                    throw new Exception("Recipe dropdown identity or final selection changed during local updates.");
                if (requests() != before) throw new Exception("Scrolling, accordions or procurement choices sent auction HTTP.");
                return "PASS procurement interaction: lower buy/acquire actions and rapid recipe choices preserve controls and scroll positions; summary cards and scroll survive detail changes; explicit accordion collapse persists; new details start at the top; lazy views retain full preview quote scope; zero auction HTTP.";
            }
            finally
            {
                if (procurementDialog != null) procurementDialog.Close();
                state = original; Width = originalWidth; Height = originalHeight;
                summaryTab = originalTab; summaryQuery = originalQuery; Persist(); RenderAll();
            }
        }

        void PumpProcurementInteractionLayout()
        {
            // Two bounded layout turns flush deferred render/scroll callbacks, without sleeps.
            for (int i = 0; i < 2; i++)
            {
                UpdateLayout();
                if (procurementDialog != null) procurementDialog.UpdateLayout();
                Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
            }
        }

        void ExpandProcurementInteractionTree()
        {
            // Production preview depth is capped at 12. The extra turns allow lazy templates
            // to materialize; the guard prevents a regression from hanging the offline suite.
            for (int pass = 0; pass < 16; pass++)
            {
                PumpProcurementInteractionLayout();
                var expanders = AuctionTestChildren<Expander>(procurementDialog).ToList();
                if (expanders.Count > 512) throw new Exception("Unexpectedly large procurement preview tree.");
                var collapsed = expanders.Where(e => !e.IsExpanded).ToList();
                if (collapsed.Count == 0) return;
                foreach (var expander in collapsed) expander.IsExpanded = true;
            }
            throw new Exception("Lazy procurement preview did not settle within its depth cap.");
        }

        Button ProcurementInteractionButton(string automationName)
        {
            return AuctionTestChildren<Button>(procurementDialog).Single(b => AutomationProperties.GetName(b) == automationName);
        }

        void ScrollProcurementInteractionControl(FrameworkElement control)
        {
            PumpProcurementInteractionLayout();
            var scroll = procurementDetailScroll;
            double y = control.TranslatePoint(new Point(), scroll).Y;
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + y - scroll.ViewportHeight * 0.45));
            PumpProcurementInteractionLayout();
        }

        void AssertProcurementInteractionPosition(ScrollViewer scroll, FrameworkElement control, double offset, double y, string operation)
        {
            if (!Object.ReferenceEquals(scroll, procurementDetailScroll) ||
                !AuctionTestChildren<FrameworkElement>(procurementDialog).Contains(control))
                throw new Exception(operation + " replaced the persistent detail controls.");
            double nextY = control.TranslatePoint(new Point(), scroll).Y;
            if (Math.Abs(scroll.VerticalOffset - offset) > 2 || Math.Abs(nextY - y) > 2)
                throw new Exception(operation + " jumped: offset " + offset + " -> " + scroll.VerticalOffset + ", row Y " + y + " -> " + nextY + ".");
        }

        void AssertProcurementSummaryUnchanged(ScrollViewer scroll, double offset, Dictionary<string, Button> cards)
        {
            if (!Object.ReferenceEquals(scroll, procurementSummaryScroll) || Math.Abs(scroll.VerticalOffset - offset) > 2)
                throw new Exception("Changing a detail mode replaced or moved the main summary scroll.");
            foreach (var pair in cards)
            {
                Button current;
                if (!procurementCardButtons.TryGetValue(pair.Key, out current) || !Object.ReferenceEquals(pair.Value, current))
                    throw new Exception("Changing a detail mode rebuilt the root summary card: " + pair.Key);
            }
        }

        static TControl ProcurementInteractionParent<TControl>(DependencyObject child) where TControl : DependencyObject
        {
            for (var current = VisualTreeHelper.GetParent(child); current != null; current = VisualTreeHelper.GetParent(current))
            {
                var result = current as TControl;
                if (result != null) return result;
            }
            return null;
        }
    }
}
