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

    public ConnectionsPane()
    {
        ViewModel = App.Services.GetRequiredService<ConnectionsViewModel>();
        InitializeComponent();
        _ = ViewModel.LoadAsync();
    }

    private void ConnectionTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is ConnectionTreeNode { Profile: { } profile })
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
