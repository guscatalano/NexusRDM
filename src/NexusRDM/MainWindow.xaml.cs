using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;

namespace NexusRDM;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        ConnectionsPane.ConnectRequested += OnConnectRequested;
    }

    private void NavView_SelectionChanged(NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        // Future: navigate to audit/settings pages
    }

    private void OnConnectRequested(object? sender, ConnectionProfile profile)
    {
        // Focus existing tab if already open
        foreach (var item in SessionTabs.TabItems.OfType<TabViewItem>())
        {
            if (item.Tag is Guid id && id == profile.Id)
            {
                SessionTabs.SelectedItem = item;
                return;
            }
        }

        var icon = profile.Protocol == ConnectionProtocol.Ssh
            ? Symbol.Globe
            : Symbol.Remote;

        var tab = new TabViewItem
        {
            Header     = profile.DisplayName,
            IconSource = new SymbolIconSource { Symbol = icon },
            Tag        = profile.Id,
            // M2/M3: replace with real SshSessionView / RdpSessionView
            Content    = new TextBlock
            {
                Text = $"Opening {profile.Protocol} → {profile.Host}:{profile.Port} …",
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
            }
        };

        SessionTabs.TabItems.Add(tab);
        SessionTabs.SelectedItem = tab;
    }

    private void SessionTabs_TabCloseRequested(TabView sender,
        TabViewTabCloseRequestedEventArgs args) =>
        sender.TabItems.Remove(args.Tab);
}
