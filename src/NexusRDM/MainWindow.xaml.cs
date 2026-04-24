using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;
using NexusRDM.ViewModels;
using NexusRDM.Views;

namespace NexusRDM;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly SessionManager _sessions;
    private readonly ISshHandler    _ssh;
    private readonly ICredentialVault _vault;

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _sessions = App.Services.GetRequiredService<SessionManager>();
        _ssh      = App.Services.GetRequiredService<ISshHandler>();
        _vault    = App.Services.GetRequiredService<ICredentialVault>();

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        ConnectionsPane.ConnectRequested += OnConnectRequested;
    }

    private void NavView_SelectionChanged(NavigationView sender,
        NavigationViewSelectionChangedEventArgs args) { /* TODO M4: nav */ }

    private async void OnConnectRequested(object? sender, ConnectionProfile profile)
    {
        // Focus existing tab if already open
        foreach (var item in SessionTabs.TabItems.OfType<TabViewItem>())
        {
            if (item.Tag is Guid id && id == profile.Id)
            { SessionTabs.SelectedItem = item; return; }
        }

        if (profile.Protocol == ConnectionProtocol.Ssh)
            await OpenSshTabAsync(profile);
        else
            OpenRdpPlaceholderTab(profile);   // M3 will replace this
    }

    private async Task OpenSshTabAsync(ConnectionProfile profile)
    {
        // Resolve credentials
        string username = string.Empty, password = string.Empty;
        if (profile.CredentialKey is not null)
        {
            var cred = _vault.Load(profile.CredentialKey);
            if (cred is not null) { username = cred.Value.Username; password = cred.Value.Password; }
        }

        if (string.IsNullOrEmpty(username))
        {
            // Prompt — simple dialog for now
            var dlg = new CredentialPromptDialog { XamlRoot = Content.XamlRoot };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            username = dlg.Username;
            password = dlg.Password;
        }

        var session  = _ssh.CreateSession(profile, username, password);
        var entry    = _sessions.AddSsh(profile, session);
        var vm       = new SshSessionViewModel(profile, session, _sessions);
        var view     = new SshSessionView(vm);

        var tab = new TabViewItem
        {
            Header     = profile.DisplayName,
            IconSource = new SymbolIconSource { Symbol = Symbol.Globe },
            Tag        = profile.Id,
            Content    = view
        };

        SessionTabs.TabItems.Add(tab);
        SessionTabs.SelectedItem = tab;
    }

    private void OpenRdpPlaceholderTab(ConnectionProfile profile)
    {
        var tab = new TabViewItem
        {
            Header     = profile.DisplayName,
            IconSource = new SymbolIconSource { Symbol = Symbol.Remote },
            Tag        = profile.Id,
            Content    = new TextBlock
            {
                Text = "RDP support coming in M3",
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        SessionTabs.TabItems.Add(tab);
        SessionTabs.SelectedItem = tab;
    }

    private async void SessionTabs_TabCloseRequested(TabView sender,
        TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is Guid id)
        {
            var entry = _sessions.FindByConnectionId(id);
            if (entry is not null) await _sessions.CloseAsync(entry);
        }
        sender.TabItems.Remove(args.Tab);
    }
}
