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
    private readonly IRdpHandler    _rdp;
    private readonly ICredentialVault _vault;

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _sessions = App.Services.GetRequiredService<SessionManager>();
        _ssh      = App.Services.GetRequiredService<ISshHandler>();
        _rdp      = App.Services.GetRequiredService<IRdpHandler>();
        _vault    = App.Services.GetRequiredService<ICredentialVault>();

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        ConnectionsPane.ConnectRequested += OnConnectRequested;
    }

    private void NavView_SelectionChanged(NavigationView sender,
        NavigationViewSelectionChangedEventArgs args) { /* TODO M4 */ }

    private async void OnConnectRequested(object? sender, ConnectionProfile profile)
    {
        // Focus if already open
        foreach (var item in SessionTabs.TabItems.OfType<TabViewItem>())
        {
            if (item.Tag is Guid id && id == profile.Id)
            { SessionTabs.SelectedItem = item; return; }
        }

        if (profile.Protocol == ConnectionProtocol.Ssh)
            await OpenSshTabAsync(profile);
        else
            await OpenRdpTabAsync(profile);
    }

    // ── SSH ───────────────────────────────────────────────────────────────────

    private async Task OpenSshTabAsync(ConnectionProfile profile)
    {
        var (username, password) = await ResolveCredentialsAsync(profile);
        if (username is null) return;

        var session = _ssh.CreateSession(profile, username, password!);
        _sessions.AddSsh(profile, session);
        var vm   = new SshSessionViewModel(profile, session, _sessions);
        var view = new SshSessionView(vm);

        AddTab(profile, Symbol.Globe, view);
    }

    // ── RDP ───────────────────────────────────────────────────────────────────

    private async Task OpenRdpTabAsync(ConnectionProfile profile)
    {
        var (username, _) = await ResolveCredentialsAsync(profile);
        if (username is null) return;

        // RDP session — password is passed via the .rdp file or Windows SSO
        var session = _rdp.CreateSession(profile, username, string.Empty);
        var vm      = new RdpSessionViewModel(profile, session, _sessions);
        var view    = new RdpSessionView(vm);

        AddTab(profile, Symbol.Remote, view);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private async Task<(string? Username, string? Password)> ResolveCredentialsAsync(
        ConnectionProfile profile)
    {
        if (profile.CredentialKey is not null)
        {
            var cred = _vault.Load(profile.CredentialKey);
            if (cred is not null) return (cred.Value.Username, cred.Value.Password);
        }

        var dlg = new CredentialPromptDialog { XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return (null, null);
        return (dlg.Username, dlg.Password);
    }

    private void AddTab(ConnectionProfile profile, Symbol icon, UIElement content)
    {
        var tab = new TabViewItem
        {
            Header     = profile.DisplayName,
            IconSource = new SymbolIconSource { Symbol = icon },
            Tag        = profile.Id,
            Content    = content
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
