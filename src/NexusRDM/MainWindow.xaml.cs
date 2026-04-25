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
        SetTitleBar(AppTitleBar);
        SetTitleBarColors();
        ConnectionsPane.ConnectRequested += OnConnectRequested;

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
        var bg0    = (SolidColorBrush)Application.Current.Resources["NxBg0"];
        var bg1    = (SolidColorBrush)Application.Current.Resources["NxBg1"];
        var brd    = (SolidColorBrush)Application.Current.Resources["NxBrd"];
        var tx1    = (SolidColorBrush)Application.Current.Resources["NxTx1"];
        var tx2    = (SolidColorBrush)Application.Current.Resources["NxTx2"];
        var tx3    = (SolidColorBrush)Application.Current.Resources["NxTx3"];
        var ssh    = (SolidColorBrush)Application.Current.Resources["NxSsh"];
        var rdp    = (SolidColorBrush)Application.Current.Resources["NxRdp"];
        var accent = (SolidColorBrush)Application.Current.Resources["NxAccent"];

        var root = new Grid { Background = bg0, Padding = new Thickness(40, 32, 40, 32) };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment           = HorizontalAlignment.Center,
        };

        var stack = new StackPanel { Spacing = 18, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        titleRow.Children.Add(new FontIcon { Glyph = "", FontSize = 28, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(new TextBlock { Text = "Nexus RDM", FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(600), Foreground = tx1, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(titleRow);

        stack.Children.Add(new TextBlock
        {
            Text = "A unified remote-desktop manager for SSH and RDP sessions. " +
                   "Define your connections once, store credentials securely in the Windows Credential Manager, " +
                   "and jump into a tabbed session with a single click.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = tx2,
        });

        stack.Children.Add(SectionHeader("GET STARTED", tx3));
        stack.Children.Add(StepCard(bg1, brd, accent, tx1, tx2, "1", "Add a connection",
            "Click “New” in the left pane. Pick SSH or RDP, fill in host/port, and choose how to authenticate."));
        stack.Children.Add(StepCard(bg1, brd, accent, tx1, tx2, "2", "Save credentials",
            "Enter username/password and tick “Save to Windows Credential Manager.” Your secrets never live in plain text."));
        stack.Children.Add(StepCard(bg1, brd, accent, tx1, tx2, "3", "Open a session",
            "Click any connection in the tree. SSH opens in a terminal tab; RDP launches the embedded Remote Desktop control."));

        stack.Children.Add(SectionHeader("PROTOCOLS", tx3));
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        legend.Children.Add(LegendDot(ssh, "SSH — terminal sessions", tx2));
        legend.Children.Add(LegendDot(rdp, "RDP — remote desktop",    tx2));
        stack.Children.Add(legend);

        stack.Children.Add(SectionHeader("TIPS", tx3));
        stack.Children.Add(BulletLine("•  Use the search box (Ctrl+F) to filter the connection tree.", tx2));
        stack.Children.Add(BulletLine("•  Group connections to keep environments organised.",          tx2));
        stack.Children.Add(BulletLine("•  The Audit pane records every session open, close, and error.", tx2));
        stack.Children.Add(BulletLine("•  Settings let you change theme and default ports.",            tx2));

        scroller.Content = stack;
        root.Children.Add(scroller);

        SessionTabs.TabItems.Add(new TabViewItem
        {
            Header     = "Home",
            Tag        = "welcome",
            Content    = root,
            IconSource = new SymbolIconSource { Symbol = Symbol.Home }
        });
    }

    private static TextBlock SectionHeader(string text, SolidColorBrush fg) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = new Windows.UI.Text.FontWeight(600),
        Foreground = fg,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private static Border StepCard(SolidColorBrush bg, SolidColorBrush brd, SolidColorBrush accent,
                                   SolidColorBrush tx1, SolidColorBrush tx2,
                                   string number, string title, string body)
    {
        var card = new Border
        {
            Background      = bg,
            BorderBrush     = brd,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(14, 12, 14, 12),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var num = new TextBlock
        {
            Text = number,
            FontSize = 16,
            FontWeight = new Windows.UI.Text.FontWeight(600),
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(num, 0);

        var inner = new StackPanel { Spacing = 2 };
        inner.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = new Windows.UI.Text.FontWeight(600), Foreground = tx1 });
        inner.Children.Add(new TextBlock { Text = body, FontSize = 12, Foreground = tx2, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(inner, 1);

        grid.Children.Add(num);
        grid.Children.Add(inner);
        card.Child = grid;
        return card;
    }

    private static StackPanel LegendDot(SolidColorBrush dot, string label, SolidColorBrush fg)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static TextBlock BulletLine(string text, SolidColorBrush fg) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = fg,
        TextWrapping = TextWrapping.Wrap,
    };

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

    public async Task<ConnectionProfile?> ShowEditConnectionPanelAsync(ConnectionProfile? existing)
    {
        var panel = new EditConnectionPanel(existing);
        OverlayHost.Children.Clear();
        OverlayHost.Children.Add(panel);
        OverlayHost.Visibility = Visibility.Visible;
        try
        {
            return await panel.Result;
        }
        finally
        {
            OverlayHost.Visibility = Visibility.Collapsed;
            OverlayHost.Children.Clear();
        }
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
