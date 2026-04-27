using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

public sealed partial class ConnectionsPane : UserControl
{
    public ConnectionsViewModel ViewModel { get; }
    public event EventHandler<ConnectionProfile>? ConnectRequested;
    public event EventHandler? CollapseRequested;

    public ConnectionsPane()
    {
        ViewModel = App.Services.GetRequiredService<ConnectionsViewModel>();
        InitializeComponent();
        _ = ViewModel.LoadAsync();
    }

    private void ConnectionTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // TreeView.ItemInvoked fires once per single click. When the user
        // has set click behavior to DoubleClick, swallow this and let the
        // DoubleTapped handler fire instead.
        if (NexusRDM.ViewModels.SettingsStore.ReadClickBehavior() != Core.Models.ConnectionClickBehavior.SingleClick)
            return;
        if (args.InvokedItem is ConnectionTreeNode { Profile: { } profile })
            ConnectRequested?.Invoke(this, profile);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) =>
        CollapseRequested?.Invoke(this, EventArgs.Empty);

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        // Build a tiny inline dialog rather than dragging in a separate
        // page — name + optional parent-group is the entire surface.
        var groups = await ViewModel.LoadGroupsForPickerAsync();

        var nameBox = new TextBox
        {
            PlaceholderText = "Group name",
            Header          = "Name",
        };
        var parentBox = new ComboBox
        {
            Header                    = "Parent (optional)",
            HorizontalAlignment       = HorizontalAlignment.Stretch,
            ItemsSource               = groups,
            DisplayMemberPath         = "DisplayName",
        };

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(nameBox);
        stack.Children.Add(parentBox);

        var dlg = new ContentDialog
        {
            Title             = "New group",
            Content           = stack,
            PrimaryButtonText = "Create",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot,
        };
        if (await NexusRDM.Services.DialogHost.ShowAsync(dlg) != ContentDialogResult.Primary) return;

        var name = nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var parent = parentBox.SelectedItem as NexusRDM.ViewModels.GroupPickItem;
        await ViewModel.CreateGroupAsync(name, parent?.Id);
    }

    private void ConnectionTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (NexusRDM.ViewModels.SettingsStore.ReadClickBehavior() != Core.Models.ConnectionClickBehavior.DoubleClick)
            return;
        if (e.OriginalSource is FrameworkElement { DataContext: ConnectionTreeNode { Profile: { } profile } })
            ConnectRequested?.Invoke(this, profile);
    }

    private void ConnectionTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ConnectionTreeNode node })
            ShowContextMenu(node, e.GetPosition(ConnectionTree));
    }

    private void ShowContextMenu(ConnectionTreeNode node, Windows.Foundation.Point pos)
    {
        var menu = new MenuFlyout();

        if (node.Profile is not null)
        {
            var connect = new MenuFlyoutItem { Text = "Connect", Icon = new FontIcon { Glyph = "\uE8AF" } };
            connect.Click += (_, _) => ConnectRequested?.Invoke(this, node.Profile);
            menu.Items.Add(connect);
            menu.Items.Add(new MenuFlyoutSeparator());

            var edit = new MenuFlyoutItem { Text = "Edit…", Icon = new SymbolIcon(Symbol.Edit) };
            edit.Click += async (_, _) => await ViewModel.EditConnectionCommand.ExecuteAsync(node);
            menu.Items.Add(edit);

            var del = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
            del.Click += async (_, _) =>
            {
                var dlg = new ContentDialog
                {
                    Title = "Delete connection",
                    Content = $"Delete \"{node.DisplayName}\"? This cannot be undone.",
                    PrimaryButtonText = "Delete",
                    CloseButtonText   = "Cancel",
                    DefaultButton     = ContentDialogButton.Close,
                    XamlRoot          = XamlRoot
                };
                if (await NexusRDM.Services.DialogHost.ShowAsync(dlg) == ContentDialogResult.Primary)
                    await ViewModel.DeleteConnectionCommand.ExecuteAsync(node);
            };
            menu.Items.Add(del);

            // Detach: the row stops being managed by Proxmox and becomes
            // a normal connection. Re-syncing the source will not bring
            // it back unless the user re-imports manually.
            if (node.IsManaged)
            {
                menu.Items.Add(new MenuFlyoutSeparator());

                // Power actions live in a sub-menu so they don't crowd
                // out Edit/Delete on the top level. Cluster-side errors
                // (403 if the token lacks VM.PowerMgmt, 500 if PVE
                // refuses an illegal transition) bubble up via the
                // result dialog.
                var power = new MenuFlyoutSubItem { Text = "Power" };
                AddPowerItem(power, node, "Start",                   NexusRDM.Core.Proxmox.ProxmoxPowerAction.Start);
                AddPowerItem(power, node, "Shutdown (graceful)",     NexusRDM.Core.Proxmox.ProxmoxPowerAction.Shutdown);
                AddPowerItem(power, node, "Reboot (graceful)",       NexusRDM.Core.Proxmox.ProxmoxPowerAction.Reboot);
                AddPowerItem(power, node, "Stop (hard power-off)",   NexusRDM.Core.Proxmox.ProxmoxPowerAction.Stop, danger: true);
                AddPowerItem(power, node, "Reset (hard, QEMU only)", NexusRDM.Core.Proxmox.ProxmoxPowerAction.Reset, danger: true);
                menu.Items.Add(power);

                // Web console (PVE noVNC). v1 opens in the user's
                // default browser — embedded WebView2 + ticket plumbing
                // is a future enhancement gated on auth-mode support.
                var console = new MenuFlyoutItem { Text = "Open Web Console (browser)" };
                console.Click += async (_, _) => await OpenWebConsoleAsync(node);
                menu.Items.Add(console);

                var detach = new MenuFlyoutItem
                {
                    Text = "Detach from Proxmox",
                    Icon = new FontIcon { Glyph = "" }, // Disconnect-ish
                };
                detach.Click += async (_, _) => await DetachManagedAsync(node);
                menu.Items.Add(detach);
            }
        }
        else
        {
            var add = new MenuFlyoutItem { Text = "New connection in group…", Icon = new SymbolIcon(Symbol.Add) };
            add.Click += async (_, _) => await ViewModel.NewConnectionCommand.ExecuteAsync(null);
            menu.Items.Add(add);

            // Sync now: only on the root group of a registered Proxmox
            // source. Updates the cluster's managed rows in place.
            if (node.IsProxmoxSourceRoot)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var sync = new MenuFlyoutItem
                {
                    Text = "Sync now (Proxmox)",
                    Icon = new SymbolIcon(Symbol.Sync),
                };
                sync.Click += async (_, _) => await SyncProxmoxRootAsync(node);
                menu.Items.Add(sync);
            }
        }

        menu.ShowAt(ConnectionTree, pos);
    }

    private void AddPowerItem(MenuFlyoutSubItem parent, ConnectionTreeNode node,
        string label, NexusRDM.Core.Proxmox.ProxmoxPowerAction action, bool danger = false)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += async (_, _) => await RunPowerActionAsync(node, action, danger);
        parent.Items.Add(item);
    }

    private async Task RunPowerActionAsync(
        ConnectionTreeNode node, NexusRDM.Core.Proxmox.ProxmoxPowerAction action, bool danger)
    {
        if (node.Profile is not { } profile) return;

        // For hard / disruptive transitions (Stop, Reset) we double-check
        // before firing — the user can't easily undo a hard power-off
        // and we don't want a reflexive miss-click to drop a running VM.
        if (danger)
        {
            var confirm = new ContentDialog
            {
                Title             = $"{action} \"{profile.DisplayName}\"?",
                Content           = action == NexusRDM.Core.Proxmox.ProxmoxPowerAction.Stop
                    ? "Hard power-off cuts the VM without giving the guest a chance to flush. Use Shutdown unless the guest is unresponsive."
                    : "Hard reset is equivalent to a reset-button press. The guest sees a cold reboot.",
                PrimaryButtonText = action.ToString(),
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = XamlRoot,
            };
            if (await NexusRDM.Services.DialogHost.ShowAsync(confirm) != ContentDialogResult.Primary)
                return;
        }

        var power = App.Services.GetRequiredService<NexusRDM.Services.ProxmoxPowerService>();
        try
        {
            var upid = await power.InvokeAsync(profile, action);
            var dlg = new ContentDialog
            {
                Title           = $"{action} triggered",
                Content         = $"{profile.DisplayName}\n\nUPID: {upid}\n\n" +
                                  $"Track progress in Proxmox under Tasks. The connections list refreshes on the next sync.",
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await NexusRDM.Services.DialogHost.ShowAsync(dlg);
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title           = $"{action} failed",
                Content         = ex.Message,
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await NexusRDM.Services.DialogHost.ShowAsync(dlg);
        }
    }

    private async Task OpenWebConsoleAsync(ConnectionTreeNode node)
    {
        if (node.Profile is not { } profile) return;
        var svc = App.Services.GetRequiredService<NexusRDM.Services.ProxmoxConsoleService>();
        try { await svc.OpenAsync(profile); }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title           = "Could not open console",
                Content         = ex.Message,
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await NexusRDM.Services.DialogHost.ShowAsync(dlg);
        }
    }

    private async Task SyncProxmoxRootAsync(ConnectionTreeNode node)
    {
        if (node.ProxmoxSourceId is not { } sourceId) return;
        var sync = App.Services.GetRequiredService<NexusRDM.Services.ProxmoxSyncService>();
        try
        {
            var result = await sync.SyncAsync(sourceId);
            // Tree refreshes via the SourceSynced event the VM listens to.
            // Surface a quick toast-equivalent dialog with the count.
            var dlg = new ContentDialog
            {
                Title           = "Proxmox sync complete",
                Content         = $"{node.DisplayName}: {result}",
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await NexusRDM.Services.DialogHost.ShowAsync(dlg);
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title           = "Proxmox sync failed",
                Content         = ex.Message,
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await NexusRDM.Services.DialogHost.ShowAsync(dlg);
        }
    }

    private async Task DetachManagedAsync(ConnectionTreeNode node)
    {
        if (node.Profile is not { } profile) return;

        var confirm = new ContentDialog
        {
            Title             = "Detach from Proxmox?",
            Content           = $"\"{profile.DisplayName}\" will become a manual connection. " +
                                "Future syncs of its source cluster will no longer update or delete it.",
            PrimaryButtonText = "Detach",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };
        if (await NexusRDM.Services.DialogHost.ShowAsync(confirm) != ContentDialogResult.Primary)
            return;

        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusRDM.Data.Context.NexusDbContext>();
        var row = await db.Connections.FirstOrDefaultAsync(c => c.Id == profile.Id);
        if (row is null) return;

        row.IsManaged        = false;
        row.ExternalSourceId = null;
        row.ExternalId       = null;
        await db.SaveChangesAsync();

        await ViewModel.LoadAsync();
    }
}
