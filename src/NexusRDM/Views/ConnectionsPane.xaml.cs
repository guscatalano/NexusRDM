using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

public sealed partial class ConnectionsPane : UserControl
{
    public ConnectionsViewModel ViewModel { get; }

    /// <summary>Raised when the user double-clicks or presses Enter on a connection node.</summary>
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
}
