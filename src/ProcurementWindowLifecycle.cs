using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        bool procurementMainClosing;
        int procurementOwnerRestoreAttempts;

        void AttachProcurementWindowLifecycle(Window dialog)
        {
            AppMotion.WindowContent(dialog);
            var owner = dialog.Owner;
            bool returnToOwner = false;
            // Close once. IsCancel would run a second DialogCancelCommand after
            // a button's Click handler, and does not close a modeless window on Esc.
            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key != Key.Escape || e.Handled) return;
                if (auctionLoading != null && Object.ReferenceEquals(auctionLoadingOwner, dialog))
                {
                    e.Handled = true; auctionLoading.RequestStop(); return;
                }
                e.Handled = true; dialog.Close();
            };
            dialog.Closing += delegate { returnToOwner = dialog.IsActive && !procurementMainClosing; };
            dialog.Closed += delegate {
                if (!returnToOwner || owner == null) return;
                // Finish native close/deactivation before returning to the owner.
                // Inactive background closes and app shutdown must not steal focus.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate {
                    if (procurementMainClosing || !owner.IsVisible || !owner.IsEnabled || owner.WindowState == WindowState.Minimized) return;
                    if (Application.Current.Windows.Cast<Window>().Any(w => w != owner && w.IsActive)) return;
                    if (!owner.IsActive) { procurementOwnerRestoreAttempts++; owner.Activate(); }
                }));
            };
        }
    }
}
