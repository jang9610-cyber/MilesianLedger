using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        enum WorkspacePage { Trade, Market, Settlement }
        WorkspacePage workspacePage = WorkspacePage.Trade;
        Grid mainWorkspace;
        FrameworkElement plannerHeader, plannerStats, plannerFooter;
        MarketStatisticsView marketView;
        AuctionSettlementView settlementView;

        void ShowMarketStatistics() { SwitchWorkspace(WorkspacePage.Market); }
        void ShowAuctionSettlement() { SwitchWorkspace(WorkspacePage.Settlement); }

        void SwitchWorkspace(WorkspacePage page)
        {
            if (procurementMainClosing || workspacePage == page) return;
            AppMotion.Transition(content, delegate { workspacePage = page; RenderAll(); });
        }

        void SetWorkspaceChrome()
        {
            bool trade = workspacePage == WorkspacePage.Trade;
            plannerHeader.Visibility = plannerStats.Visibility = plannerFooter.Visibility = trade ? Visibility.Visible : Visibility.Collapsed;
            mainWorkspace.Margin = trade ? new Thickness(30, 24, 30, 14) : new Thickness(24, 22, 24, 20);
        }

        MarketSnapshotClient WorkspaceMarketClient(out string message)
        {
            var config = AuctionProxyConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "auction-proxy.json"));
            message = config.StatusMessage;
            return config.IsConfigured ? MarketSnapshotClient.ForBaseUri(config.BaseUri) : null;
        }

        void RenderWorkspacePage()
        {
            FrameworkElement page;
            if (workspacePage == WorkspacePage.Market) {
                if (marketView == null) {
                    string message; var client = WorkspaceMarketClient(out message);
                    marketView = new MarketStatisticsView(client, message,
                        Path.Combine(Path.GetDirectoryName(store.FilePath), "market-watchlist.json"));
                }
                page = marketView;
            } else {
                if (settlementView == null) {
                    settlementView = new AuctionSettlementView();
                    string message; var client = WorkspaceMarketClient(out message);
                    if (client != null) settlementView.MarketPanel.Configure(client);
                    else settlementView.MarketPanel.Configure(null, null, message);
                }
                page = settlementView;
            }
            // Keep each page instance alive across navigation so manual inputs,
            // local filters and the selected distribution method remain intact.
            if (content.Children.Contains(page)) return;
            content.Children.Clear(); content.ColumnDefinitions.Clear(); content.RowDefinitions.Clear();
            content.Children.Add(page);
        }

        void DisposeWorkspacePages()
        {
            if (marketView != null) marketView.Dispose();
            if (settlementView != null) settlementView.Dispose();
        }
    }
}
