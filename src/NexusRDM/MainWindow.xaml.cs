using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;
using NexusRDM.ViewModels;
using NexusRDM.Views;
using Windows.UI;

namespace NexusRDM;

public sealed partial class MainWindow : Window
{
    public MainViewModel  ViewModel { get; }
    private readonly SessionManager   _sessions;
    private readonly ISshHandler      _ssh;
    private readonly IRdpHandler      _rdp;
    private readonly ICredentialVault _vault;

    private enum NavSection { Connections, Audit, Settings }
    private NavSection _currentNav = NavSection.Connections;

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _sessions = App.Services.GetRequiredService<SessionManager>();
        _ssh      = App.Services.GetRequiredService<ISshHandler>();
        _rdp      = App.Services.GetRequiredService<IRdpHandler>();
        _vault    = App.Services.GetRequiredService<ICredentialVault>();

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBarColors();
        ConnectionsPane.ConnectRequested += OnConnectRequested;

        // Add welcome tab
        AddWelcomeTab();
    }

    private void SetTitleBarColors()
    {
        if (AppWindow.TitleBar is { } tb)
        {
            tb.ExtendsContentIntoTitleBar = true;
            var bg = Color.FromArgb(0xFF, 0x22, 0x22, 0x2A);
            tb.BackgroundColor         = bg;
            tb.InactiveBackgroundColor = bg;
            tb.ButtonBackgroundColor         = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor         = Color.FromArgb(0xFF, 0x80, 0x80, 0x90);
            tb.ButtonHoverBackgroundColor    = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
        }
    }

    private void AddWelcomeTab()
    {
        var content = new Grid { Background = (SolidColorBrush)Application.Current.Resources["NxBg0"] };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 12 };
        stack.Children.Add(new FontIcon { Glyph = "\uE968", FontSize = 40, Foreground = (SolidColorBrush)Application.Current.Resources["NxTx3"] });
        stack.Children.Add(new TextBlock { Text = "Nexus RDM", FontSize = 15, FontWeight = new Windows.UI.Text.FontWeight(500), Foreground = (SolidColorBrush)Application.Current.Resources["NxTx1"], HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = "Click any connection to open a session", FontSize = 12, Foreground = (SolidColorBrush)Application.Current.Resources["NxTx2"], HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(stack);

        SessionTabs.TabItems.Add(new TabViewItem
        {
            Header     = "Home",
            Tag        = "welcome",
            Content    = content,
            IconSource = new SymbolIconSource { Symbol = Symbol.Home }
        });
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void BtnNavConn_Click(object sender, RoutedEventArgs e) => ShowNav(NavSection.Connections);
    private void BtnNavAudit_Click(object sender, RoutedEventArgs e) => ShowNav(NavSection.Audit);
    private void BtnNavSettings_Click(object sender, RoutedEventArgs e) => ShowNav(NavSection.Settings);

    private void ShowNav(NavSection nav)
    {
        _currentNav = nav;
        ConnView.Visibility        = nav == NavSection.Connections ? Visibility.Visible : Visibility.Collapsed;
        AuditPage.Visibility       = nav == NavSection.Audit       ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageView.Visibility = nav == NavSection.Settings    ? Visibility.Visible : Visibility.Collapsed;

        var accentBg = (SolidColorBrush)Application.Current.Resources["NxBg3"];
        var clear    = new SolidColorBrush(Colors.Transparent);
        BtnNavConn.Background     = nav == NavSection.Connections ? accentBg : clear;
        BtnNavAudit.Background    = nav == NavSection.Audit       ? accentBg : clear;
        BtnNavSettings.Background = nav == NavSection.Settings    ? accentBg : clear;

        var onFg  = (SolidColorBrush)Application.Current.Resources["NxTx1"];
        var offFg = (SolidColorBrush)Application.Current.Resources["NxTx3"];
        SetNavIconFg(BtnNavConn,     nav == NavSection.Connections ? onFg : offFg);
        SetNavIconFg(BtnNavAudit,    nav == NavSection.Audit       ? onFg : offFg);
        SetNavIconFg(BtnNavSettings, nav == NavSection.Settings    ? onFg : offFg);
    }

    private static void SetNavIconFg(Button btn, SolidColorBrush brush)
    {
        if (btn.Content is IconElement icon) icon.Foreground = brush;
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    private async void OnConnectRequested(object? sender, ConnectionProfile profile)
    {
        ShowNav(NavSection.Connections);
        foreach (var item in SessionTabs.TabItems.OfType<TabViewItem>())
            if (item.Tag is Guid id && id == profile.Id)
            { SessionTabs.SelectedItem = item; return; }

        if (profile.Protocol == ConnectionProtocol.Ssh)
            await OpenSshTabAsync(profile);
        else
            await OpenRdpTabAsync(profile);
    }

    private async Task OpenSshTabAsync(ConnectionProfile profile)
    {
        var (username, password) = await ResolveCredentialsAsync(profile);
        if (username is null) return;
        var session = _ssh.CreateSession(profile, username, password!);
        _sessions.AddSsh(profile, session);
        var vm   = new SshSessionViewModel(profile, session, _sessions);
        AddSessionTab(profile, Symbol.Globe, new SshSessionView(vm),
            (SolidColorBrush)Application.Current.Resources["NxSsh"]);
    }

    private async Task OpenRdpTabAsync(ConnectionProfile profile)
    {
        var (username, _) = await ResolveCredentialsAsync(profile);
        if (username is null) return;
        var session = _rdp.CreateSession(profile, username, string.Empty);
        _sessions.AddRdp(profile, session);
        var vm   = new RdpSessionViewModel(profile, session, _sessions);
        AddSessionTab(profile, Symbol.Remote, new RdpSessionView(vm),
            (SolidColorBrush)Application.Current.Resources["NxRdp"]);
    }

    private void AddSessionTab(ConnectionProfile profile, Symbol icon, UIElement content, SolidColorBrush dotColor)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = dotColor });
        header.Children.Add(new TextBlock { Text = profile.DisplayName, FontSize = 12, Foreground = (SolidColorBrush)Application.Current.Resources["NxTx1"] });

        SessionTabs.TabItems.Add(new TabViewItem
        {
            Header     = header,
            Tag        = profile.Id,
            Content    = content,
            IconSource = new SymbolIconSource { Symbol = icon }
        });
        SessionTabs.SelectedItem = SessionTabs.TabItems.Last();
    }

    private async Task<(string? Username, string? Password)> ResolveCredentialsAsync(ConnectionProfile profile)
    {
        if (profile.CredentialKey is not null)
        {
            var c = _vault.Load(profile.CredentialKey);
            if (c is not null) return (c.Value.Username, c.Value.Password);
        }
        var dlg = new CredentialPromptDialog { XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return (null, null);
        return (dlg.Username, dlg.Password);
    }

    private async void SessionTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is Guid id)
        {
            var entry = _sessions.FindByConnectionId(id);
            if (entry is not null) await _sessions.CloseAsync(entry);
        }
        sender.TabItems.Remove(args.Tab);
    }
}
