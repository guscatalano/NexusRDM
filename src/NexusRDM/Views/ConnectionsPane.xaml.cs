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
    /// <summary>Right-click → "Open SFTP" on an SSH-protocol profile.
    /// Wired in MainWindow → OpenSftpTabAsync.</summary>
    public event EventHandler<ConnectionProfile>? OpenSftpRequested;
    public event EventHandler? CollapseRequested;

    public ConnectionsPane()
    {
        ViewModel = App.Services.GetRequiredService<ConnectionsViewModel>();
        InitializeComponent();
        _ = ViewModel.LoadAsync();

        // Force every group node to expand whenever demo mode flips
        // on — TwoWay binding means user collapses persist within a
        // session, but on entry to the demo we want the synthetic
        // tree fully visible without a manual click.
        try
        {
            var demo = App.Services.GetRequiredService<NexusRDM.Services.DemoModeService>();
            demo.IsActiveChanged += (_, _) =>
            {
                if (!demo.IsActive) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var n in EnumerateAllNodes(ViewModel.RootItems))
                    {
                        // Toggle through false to force PropertyChanged
                        // for nodes already at true.
                        if (n.IsExpanded) n.IsExpanded = false;
                        n.IsExpanded = true;
                    }
                });
            };
        }
        catch { /* DI not ready */ }
    }

    private static IEnumerable<ConnectionTreeNode> EnumerateAllNodes(
        IEnumerable<ConnectionTreeNode> roots)
    {
        foreach (var n in roots)
        {
            yield return n;
            foreach (var c in EnumerateAllNodes(n.Children)) yield return c;
        }
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

    /// <summary>Persist drag-reorder results. WinUI's TreeView
    /// mutates the in-memory tree on drop, but never writes back to
    /// our store — so without this handler, every drag was a visual
    /// no-op that vanished on next reload.
    ///
    /// The previous version gated on <c>args.DropResult == Move</c>
    /// and read <c>args.NewParentItem</c>. Both turned out to be
    /// unreliable for TreeView's in-tree reorder path: <c>DropResult</c>
    /// can be <c>None</c> for sibling reorders, and <c>NewParentItem</c>
    /// is null when dropping between items at the root. So we now
    /// reconcile the post-drop tree against the DB instead — walk
    /// every node, compare its actual tree-parent to its persisted
    /// GroupId / ParentId, write any differences. Reliable in every
    /// drop scenario at the cost of one tree-walk per drag.</summary>
    private async void ConnectionTree_DragItemsCompleted(
        TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        var svc = App.Services.GetRequiredService<NexusRDM.Core.Interfaces.IConnectionService>();
        var changed = false;

        // Connections: parent = the group node containing this row,
        // or null if at the root.
        foreach (var (node, parent) in EnumerateWithParent(ViewModel.RootItems, parentGroupId: null))
        {
            if (node.Profile is not { } profile) continue;
            if (profile.GroupId == parent) continue;
            try
            {
                profile.GroupId = parent;
                await svc.UpdateAsync(profile);
                changed = true;
            }
            catch (Exception ex)
            {
                NexusRDM.Services.CrashLogger.Log(ex, "tree drag persist (connection)");
            }
        }

        // Groups: load all groups once so we can look up the current
        // ParentId without a per-group round-trip. Then compare each
        // tree group node's actual parent to its persisted ParentId.
        try
        {
            var allGroups = (await svc.GetGroupsAsync()).ToDictionary(g => g.Id);
            foreach (var (node, parent) in EnumerateWithParent(ViewModel.RootItems, parentGroupId: null))
            {
                if (node.Profile is not null) continue;
                if (node.GroupId is not { } gid) continue;
                if (!allGroups.TryGetValue(gid, out var group)) continue;
                if (group.ParentId == parent) continue;

                // Cycle guard: refuse a move that would put the group
                // under itself or any of its descendants.
                if (parent is { } target && (target == gid || IsDescendantOf(target, gid)))
                    continue;

                group.ParentId = parent;
                await svc.UpdateGroupAsync(group);
                changed = true;
            }
        }
        catch (Exception ex)
        {
            NexusRDM.Services.CrashLogger.Log(ex, "tree drag persist (group)");
        }

        if (changed) await ViewModel.LoadAsync();
    }

    /// <summary>Yields every node in the tree paired with its
    /// effective parent GroupId. The recursion descends into a
    /// group's children using that group's id as the parent;
    /// connection nodes have empty children so we don't have to
    /// special-case them.</summary>
    internal static IEnumerable<(ConnectionTreeNode Node, Guid? ParentGroupId)>
        EnumerateWithParent(IEnumerable<ConnectionTreeNode> items, Guid? parentGroupId)
    {
        foreach (var n in items)
        {
            yield return (n, parentGroupId);
            // For group nodes, descend with this group's id as the
            // parent. For connection nodes, Children should be empty;
            // if anything ever leaks through, we keep the same parent
            // (a connection can't actually be a parent).
            var nextParent = n.Profile is null && n.GroupId is { } id ? id : parentGroupId;
            foreach (var deeper in EnumerateWithParent(n.Children, nextParent))
                yield return deeper;
        }
    }

    /// <summary>True if <paramref name="candidateChild"/> is anywhere
    /// inside the subtree rooted at <paramref name="ancestor"/>.
    /// Used to reject cyclic group moves before they hit the DB.</summary>
    internal static bool IsDescendantOf(Guid candidateChild, Guid ancestor,
        IEnumerable<ConnectionTreeNode>? roots = null)
    {
        roots ??= Array.Empty<ConnectionTreeNode>();
        foreach (var n in roots)
        {
            if (n.GroupId == ancestor) return ContainsGroup(n, candidateChild);
            if (IsDescendantOf(candidateChild, ancestor, n.Children)) return true;
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

    private bool IsDescendantOf(Guid candidateChild, Guid ancestor)
        => IsDescendantOf(candidateChild, ancestor, ViewModel.RootItems);

    private void ShowContextMenu(ConnectionTreeNode node, Windows.Foundation.Point pos)
    {
        var menu = new MenuFlyout();

        if (node.Profile is not null)
        {
            var connect = new MenuFlyoutItem { Text = "Connect", Icon = new FontIcon { Glyph = "\uE8AF" } };
            connect.Click += (_, _) => ConnectRequested?.Invoke(this, node.Profile);
            menu.Items.Add(connect);

            // "Open SFTP" only makes sense for SSH-protocol profiles —
            // the SFTP subsystem rides over the SSH transport.
            if (node.Profile.Protocol == NexusRDM.Core.Models.ConnectionProtocol.Ssh)
            {
                var sftp = new MenuFlyoutItem
                {
                    Text = "Open SFTP",
                    Icon = new FontIcon { Glyph = "" }, // FilesFolder
                };
                sftp.Click += (_, _) => OpenSftpRequested?.Invoke(this, node.Profile);
                menu.Items.Add(sftp);
            }
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
                // Hyper-V root group: manual Sync-now and start/stop
                // for the long-lived background-sync agent.
                menu.Items.Add(new MenuFlyoutSeparator());
                var sync = new MenuFlyoutItem
                {
                    Text = "Sync now (Hyper-V)",
                    Icon = new SymbolIcon(Symbol.Sync),
                };
                sync.Click += async (_, _) => await SyncHyperVRootAsync();
                menu.Items.Add(sync);

                var hv = App.Services.GetRequiredService<NexusRDM.Services.HyperVSyncService>();
                if (hv.IsBackgroundLoopRunning)
                {
                    var stop = new MenuFlyoutItem { Text = "Stop background sync" };
                    stop.Click += (_, _) => hv.StopBackgroundLoop();
                    menu.Items.Add(stop);
                }
                else
                {
                    var start = new MenuFlyoutItem { Text = "Start background sync (UAC)" };
                    start.Click += async (_, _) => await StartHyperVBackgroundLoopAsync();
                    menu.Items.Add(start);
                }
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
            await LogPowerActionAsync(profile, $"Proxmox {action}", $"UPID: {upid}");
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
            await LogPowerActionAsync(profile, $"Hyper-V {action}", $"WMI return code {rv}");
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

    private async Task StartHyperVBackgroundLoopAsync()
    {
        var hv = App.Services.GetRequiredService<NexusRDM.Services.HyperVSyncService>();
        try
        {
            await hv.StartBackgroundLoopAsync();
            await NexusRDM.Services.DialogHost.ShowAsync(new ContentDialog
            {
                Title           = "Background sync started",
                Content         = "The elevated agent is now running. Hyper-V state will refresh on the configured interval until the app closes.",
                CloseButtonText = "OK",
                XamlRoot        = XamlRoot,
            });
        }
        catch (OperationCanceledException)
        {
            // User cancelled UAC — no toast needed.
        }
        catch (Exception ex)
        {
            await NexusRDM.Services.DialogHost.ShowAsync(new ContentDialog
            {
                Title           = "Could not start background sync",
                Content         = ex.Message,
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

    /// <summary>Write a "PowerAction" audit row for user-triggered
    /// Start / Stop / Reboot / Save (etc.) operations. Best effort —
    /// audit failure must never undo the underlying action.</summary>
    private static async Task LogPowerActionAsync(
        NexusRDM.Core.Models.ConnectionProfile profile, string action, string? detail)
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<NexusRDM.Core.Interfaces.IAuditRepository>();
            await audit.LogAsync(new NexusRDM.Core.Models.AuditEntry
            {
                ConnectionId = profile.Id,
                DisplayName  = profile.DisplayName,
                Action       = NexusRDM.Core.Models.AuditAction.PowerAction,
                Detail       = detail is null ? action : $"{action} — {detail}",
            });
        }
        catch { }
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

        var formerExternalId = row.ExternalId;
        row.IsManaged        = false;
        row.ExternalSourceId = null;
        row.ExternalId       = null;
        await db.SaveChangesAsync();

        // Audit the detach so the user has a record of which row
        // stopped being managed and from which source. Lookup goes
        // through the same scope as the DB write so we don't
        // double-pay on DI resolution.
        try
        {
            var audit = scope.ServiceProvider.GetRequiredService<NexusRDM.Core.Interfaces.IAuditRepository>();
            var source = formerExternalId is { } id && id.StartsWith("hyperv:")
                ? "Hyper-V"
                : "Proxmox";
            await audit.LogAsync(new NexusRDM.Core.Models.AuditEntry
            {
                ConnectionId = profile.Id,
                DisplayName  = profile.DisplayName,
                Action       = NexusRDM.Core.Models.AuditAction.Detached,
                Detail       = $"Detached from {source} (was {formerExternalId ?? "managed"})",
            });
        }
        catch { /* non-fatal */ }

        await ViewModel.LoadAsync();
    }
}
