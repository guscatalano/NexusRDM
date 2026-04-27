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
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

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
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                    await ViewModel.DeleteConnectionCommand.ExecuteAsync(node);
            };
            menu.Items.Add(del);
        }
        else
        {
            var add = new MenuFlyoutItem { Text = "New connection in group…", Icon = new SymbolIcon(Symbol.Add) };
            add.Click += async (_, _) => await ViewModel.NewConnectionCommand.ExecuteAsync(null);
            menu.Items.Add(add);
        }

        menu.ShowAt(ConnectionTree, pos);
    }
}
