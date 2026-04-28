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

    /// <summary>Persist drag-reorder results. WinUI's TreeView mutates
    /// the in-memory ObservableCollection<ConnectionTreeNode> when the
    /// user drops, but the underlying ConnectionProfile.GroupId in
    /// the DB stays stale until we write it. This handler walks the
    /// dropped items, computes each one's new parent group from
    /// <c>NewParentItem</c>, and pushes the change through
    /// IConnectionService — the same path the editor's Save button
    /// uses, so the round-trip behavior is identical.</summary>
    private async void ConnectionTree_DragItemsCompleted(
        TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            return;

        // Resolve the target group id once. If the drop target is a
        // connection, walk up to the connection's actual parent group
        // — TreeView lets you drop on leaves, but our model can't
        // nest connections inside connections.
        var newParent = args.NewParentItem as ConnectionTreeNode;
        Guid? newGroupId;
        if (newParent is null)                 newGroupId = null;
        else if (newParent.Profile is null)    newGroupId = newParent.GroupId; // group node
        else                                   newGroupId = ParentGroupIdOf(newParent); // connection → its parent group

        var svc = App.Services.GetRequiredService<NexusRDM.Core.Interfaces.IConnectionService>();
        var changed = false;

        foreach (var raw in args.Items)
        {
            if (raw is not ConnectionTreeNode item) continue;

            try
            {
                if (item.Profile is { } profile)
                {
                    if (profile.GroupId == newGroupId) continue;
                    profile.GroupId = newGroupId;
                    await svc.UpdateAsync(profile);
                    changed = true;
                }
                else if (item.GroupId is { } groupId)
                {
                    // Group move. Block cycles: dropping a group
                    // under itself or any descendant would orphan the
                    // tree. Also block dropping onto a connection
                    // (we'd be attaching a group to a leaf).
                    if (newParent?.Profile is not null) continue;
                    if (newGroupId == groupId) continue;
                    if (newGroupId is { } target && IsDescendantOf(target, groupId)) continue;

                    var allGroups = await svc.GetGroupsAsync();
                    var group = allGroups.FirstOrDefault(g => g.Id == groupId);
                    if (group is null) continue;
                    if (group.ParentId == newGroupId) continue;
                    group.ParentId = newGroupId;
                    await svc.UpdateGroupAsync(group);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                NexusRDM.Services.CrashLogger.Log(ex, "tree drag-drop persist");
            }
        }

        // Reload from DB so the visible tree matches the persisted
        // state — especially after errors, where TreeView's in-memory
        // move may have happened but the DB write didn't.
        if (changed) await ViewModel.LoadAsync();
    }

    /// <summary>Walks RootItems looking for the group node whose
    /// <c>Children</c> contains <paramref name="needle"/>; returns
    /// that group's Id (or null if needle is at the root).</summary>
    private Guid? ParentGroupIdOf(ConnectionTreeNode needle)
    {
        return Find(ViewModel.RootItems, needle)?.GroupId;

        static ConnectionTreeNode? Find(IEnumerable<ConnectionTreeNode> within, ConnectionTreeNode target)
        {
            foreach (var n in within)
            {
                if (n.Children.Contains(target)) return n;
                var deeper = Find(n.Children, target);
                if (deeper is not null) return deeper;
            }
            return null;
        }
    }

    /// <summary>True if <paramref name="candidateChild"/> is the same
    /// as, or a descendant of, <paramref name="ancestor"/>. Used to
    /// reject cyclic group moves before they hit the DB.</summary>
    private bool IsDescendantOf(Guid candidateChild, Guid ancestor)
    {
        var queue = new Queue<ConnectionTreeNode>();
        foreach (var n in ViewModel.RootItems) queue.Enqueue(n);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.GroupId == ancestor)
            {
                // Found the ancestor — check whether candidateChild
                // is anywhere in its subtree.
                return ContainsGroup(node, candidateChild);
            }
            foreach (var c in node.Children) queue.Enqueue(c);
        }
        return false;

        static bool ContainsGroup(ConnectionTreeNode root, Guid id)
        {
            if (root.GroupId == id) return true;
            foreach (var c in root.Children)
                if (ContainsGroup(c, id)) return true;
            return false;
        }
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

                if (IsHyperV(node))
                {
                    // Hyper-V branch: WMI RequestStateChange power
                    // semantics. vmconnect.exe is the equivalent of
                    // the Proxmox web console.
                    var hvPower = new MenuFlyoutSubItem { Text = "Power" };
                    AddHyperVPowerItem(hvPower, node, "Start",                 NexusRDM.Services.HyperVPowerAction.Start);
                    AddHyperVPowerItem(hvPower, node, "Shutdown (graceful)",   NexusRDM.Services.HyperVPowerAction.Shutdown);
                    AddHyperVPowerItem(hvPower, node, "Reboot (hard reset)",   NexusRDM.Services.HyperVPowerAction.Reboot);
                    AddHyperVPowerItem(hvPower, node, "Stop (hard power-off)", NexusRDM.Services.HyperVPowerAction.Stop, danger: true);
                    AddHyperVPowerItem(hvPower, node, "Save state",            NexusRDM.Services.HyperVPowerAction.Save);
                    menu.Items.Add(hvPower);

                    var vmconnect = new MenuFlyoutItem { Text = "Open in vmconnect" };
                    vmconnect.Click += (_, _) => OpenVmConnect(node);
                    menu.Items.Add(vmconnect);

                    var hvDetach = new MenuFlyoutItem { Text = "Detach from Hyper-V" };
                    hvDetach.Click += async (_, _) => await DetachManagedAsync(node);
                    menu.Items.Add(hvDetach);
                    goto AfterManaged;
                }

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

                AfterManaged: ;
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
            else if (node.IsHyperVRoot)
            {
                // Hyper-V root group: Sync-now triggers the local WMI
                // enumeration. Mirrors the Proxmox root behaviour.
                menu.Items.Add(new MenuFlyoutSeparator());
                var sync = new MenuFlyoutItem
                {
                    Text = "Sync now (Hyper-V)",
                    Icon = new SymbolIcon(Symbol.Sync),
                };
                sync.Click += async (_, _) => await SyncHyperVRootAsync();
                menu.Items.Add(sync);
            }
            else if (!node.IsExternallyManaged)
            {
                // Delete group: nullifies the FK on every connection in
                // it (they fall to ungrouped — see ConnectionRepository
                // wired to DeleteBehavior.SetNull). Hidden for any
                // externally-managed group (Proxmox source roots, the
                // Discovered folder) — those are owned by their
                // respective services and have their own removal paths
                // (delete the source / disable scheduled scans).
                menu.Items.Add(new MenuFlyoutSeparator());
                var del = new MenuFlyoutItem
                {
                    Text = "Delete group",
                    Icon = new SymbolIcon(Symbol.Delete),
                };
                del.Click += async (_, _) => await DeleteGroupAsync(node);
                menu.Items.Add(del);
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

    private async Task DeleteGroupAsync(ConnectionTreeNode node)
    {
        if (node.GroupId is not { } groupId) return;

        // Refuse on sub-groups: the FK between groups is Restrict, so a
        // direct delete would just throw. Recursive cascade is doable
        // but error-prone — the user is better served by deleting from
        // the inside out, which keeps the blast radius visible.
        var hasSubGroups = node.Children.Any(c => c.GroupId is not null);
        if (hasSubGroups)
        {
            var blocked = new ContentDialog
            {
                Title             = "Group has sub-groups",
                Content           = $"\"{node.DisplayName}\" contains nested groups. Delete those first, then try again.",
                CloseButtonText   = "OK",
                XamlRoot          = XamlRoot,
            };
            await NexusRDM.Services.DialogHost.ShowAsync(blocked);
            return;
        }

        var connectionCount = node.Children.Count(c => c.Profile is not null);
        var msg = connectionCount == 0
            ? $"Delete the group \"{node.DisplayName}\"?"
            : $"Delete the group \"{node.DisplayName}\"? Its {connectionCount} connection(s) will move to the top level (no group). The connections themselves are kept.";

        var confirm = new ContentDialog
        {
            Title             = "Delete group?",
            Content           = msg,
            PrimaryButtonText = "Delete",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };
        if (await NexusRDM.Services.DialogHost.ShowAsync(confirm) != ContentDialogResult.Primary)
            return;

        var svc = App.Services.GetRequiredService<NexusRDM.Core.Interfaces.IConnectionService>();
        try { await svc.DeleteGroupAsync(groupId); }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title           = "Could not delete group",
                Content         = ex.Message,
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await NexusRDM.Services.DialogHost.ShowAsync(dlg);
            return;
        }

        await ViewModel.LoadAsync();
    }

    // ── Hyper-V right-click helpers ──────────────────────────────────────

    private static bool IsHyperV(ConnectionTreeNode node) =>
        node.Profile is { ExternalId: { } id }
        && id.StartsWith("hyperv:", StringComparison.Ordinal);

    private void AddHyperVPowerItem(MenuFlyoutSubItem parent, ConnectionTreeNode node,
        string label, NexusRDM.Services.HyperVPowerAction action, bool danger = false)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += async (_, _) => await RunHyperVPowerAsync(node, action, danger);
        parent.Items.Add(item);
    }

    private async Task RunHyperVPowerAsync(
        ConnectionTreeNode node, NexusRDM.Services.HyperVPowerAction action, bool danger)
    {
        if (node.Profile is not { ExternalId: { } extId } profile) return;
        var vmId = extId.StartsWith("hyperv:") ? extId.Substring("hyperv:".Length) : extId;

        if (danger)
        {
            var confirm = new ContentDialog
            {
                Title             = $"{action} \"{profile.DisplayName}\"?",
                Content           = "Hard power-off cuts the VM without giving the guest a chance to flush. Use Shutdown unless the guest is unresponsive.",
                PrimaryButtonText = action.ToString(),
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = XamlRoot,
            };
            if (await NexusRDM.Services.DialogHost.ShowAsync(confirm) != ContentDialogResult.Primary)
                return;
        }

        var client = App.Services.GetRequiredService<NexusRDM.Services.HyperVClient>();
        try
        {
            // Power actions go through the elevated sidekick (one UAC
            // prompt per action). The in-process direct path no
            // longer exists — System.Management can't load inside
            // a WinUI 3 host.
            var rv = await client.RequestStateChangeAsync(vmId, action);
            // Per the spec: 0 = completed synchronously, 4096 = job
            // started (the guest will transition shortly). Anything
            // else is a Hyper-V error code; surfacing it lets advanced
            // users grep MSDN if they care.
            var msg = rv switch
            {
                0    => $"{action} completed.",
                4096 => $"{action} job started.",
                _    => $"{action} returned WMI code {rv}.",
            };
            await NexusRDM.Services.DialogHost.ShowAsync(new ContentDialog
            {
                Title           = $"{action} {profile.DisplayName}",
                Content         = msg,
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            });
        }
        catch (Exception ex)
        {
            await NexusRDM.Services.DialogHost.ShowAsync(new ContentDialog
            {
                Title           = $"{action} failed",
                Content         = ex.Message,
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            });
        }
    }

    /// <summary>Launches Microsoft's bundled <c>vmconnect.exe</c>
    /// pointed at the VM. The Hyper-V Manager equivalent of the
    /// Proxmox web console — works regardless of guest IP / network.</summary>
    private void OpenVmConnect(ConnectionTreeNode node)
    {
        if (node.Profile is not { } profile) return;
        try
        {
            // vmconnect takes <server> <vm-name>; localhost since this
            // build is local-only. Quoting the name handles spaces.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "vmconnect.exe",
                Arguments       = $"localhost \"{profile.DisplayName}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _ = NexusRDM.Services.DialogHost.ShowAsync(new ContentDialog
            {
                Title           = "Could not open vmconnect",
                Content         = ex.Message + "\n\nvmconnect.exe ships with the Hyper-V tools — install 'Hyper-V Management Tools' if it's missing.",
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            });
        }
    }

    private async Task SyncHyperVRootAsync()
    {
        var svc = App.Services.GetRequiredService<NexusRDM.Services.HyperVSyncService>();
        try
        {
            // Tree-driven sync = manual = elevated agent.
            var r = await svc.SyncAsync();
            var dlg = new ContentDialog
            {
                Title           = "Hyper-V sync complete",
                Content         = r.IsSuccess ? r.ToString() : (r.Error ?? "Unknown error"),
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            };
            await NexusRDM.Services.DialogHost.ShowAsync(dlg);
        }
        catch (Exception ex)
        {
            await NexusRDM.Services.DialogHost.ShowAsync(new ContentDialog
            {
                Title           = "Hyper-V sync failed",
                Content         = ex.Message,
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            });
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
