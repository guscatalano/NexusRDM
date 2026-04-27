using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.ComponentModel.DataAnnotations;

namespace NexusRDM.ViewModels;

public sealed partial class EditConnectionViewModel : ObservableValidator
{
    private readonly IConnectionService _svc;
    private readonly Guid?              _existingId;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(200)]
    [NotifyPropertyChangedFor(nameof(DisplayNameError))]
    [NotifyPropertyChangedFor(nameof(HasDisplayNameError))]
    [NotifyPropertyChangedFor(nameof(DisplayNameErrorVisibility))]
    private string _displayName = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Host is required")]
    [MaxLength(512)]
    [NotifyPropertyChangedFor(nameof(HostError))]
    [NotifyPropertyChangedFor(nameof(HasHostError))]
    [NotifyPropertyChangedFor(nameof(HostErrorVisibility))]
    private string _host = string.Empty;

    /// <summary>WinUI's stock TextBox doesn't bind to <c>INotifyDataErrorInfo</c>,
    /// so we surface validation results as plain string + visibility
    /// properties the XAML can light up.</summary>
    public string DisplayNameError => FirstError(nameof(DisplayName));
    public bool   HasDisplayNameError => !string.IsNullOrEmpty(DisplayNameError);
    public Visibility DisplayNameErrorVisibility =>
        HasDisplayNameError ? Visibility.Visible : Visibility.Collapsed;

    public string HostError => FirstError(nameof(Host));
    public bool   HasHostError => !string.IsNullOrEmpty(HostError);
    public Visibility HostErrorVisibility =>
        HasHostError ? Visibility.Visible : Visibility.Collapsed;

    private string FirstError(string propertyName) =>
        GetErrors(propertyName).OfType<ValidationResult>().FirstOrDefault()?.ErrorMessage ?? string.Empty;

    /// <summary>Subscribed in the constructor — ObservableValidator
    /// raises <see cref="ObservableValidator.ErrorsChanged"/> when
    /// validation state changes; mirror that into our derived
    /// XAML-friendly properties so the inline error TextBlock under
    /// each field re-renders without manual bookkeeping.</summary>
    private void OnViewModelErrorsChanged(object? sender, System.ComponentModel.DataErrorsChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DisplayName))
        {
            OnPropertyChanged(nameof(DisplayNameError));
            OnPropertyChanged(nameof(HasDisplayNameError));
            OnPropertyChanged(nameof(DisplayNameErrorVisibility));
        }
        else if (e.PropertyName == nameof(Host))
        {
            OnPropertyChanged(nameof(HostError));
            OnPropertyChanged(nameof(HasHostError));
            OnPropertyChanged(nameof(HostErrorVisibility));
        }
    }

    [ObservableProperty] private int                _port     = 22;
    [ObservableProperty] private ConnectionProtocol _protocol = ConnectionProtocol.Ssh;
    [ObservableProperty] private string             _tags     = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGroupOption))]
    private Guid?                                   _groupId;

    /// <summary>Codepoint of the glyph chosen for this connection. Used
    /// by the connections tree to render a per-row icon. Empty means
    /// "use the protocol default" (set by <see cref="NexusRDM.Services.ConnectionIcons.DefaultFor"/>).</summary>
    [ObservableProperty] private string             _iconGlyph = string.Empty;

    /// <summary>Catalog the picker iterates; each entry is rendered as
    /// a small button in the edit panel.</summary>
    public IReadOnlyList<NexusRDM.Services.IconChoice> IconChoices { get; } =
        NexusRDM.Services.ConnectionIcons.All;

    /// <summary>Group picker entries — populated by <see cref="LoadGroupsAsync"/>
    /// from the existing list of <see cref="Group"/>s plus a synthetic
    /// "(none)" option for connections that live at the tree root.</summary>
    public System.Collections.ObjectModel.ObservableCollection<GroupPickItem> GroupOptions { get; } = new();

    /// <summary>Two-way binding for the Group ComboBox. Maps to the
    /// underlying <see cref="GroupId"/> field.</summary>
    public GroupPickItem? SelectedGroupOption
    {
        get => GroupOptions.FirstOrDefault(o => o.Id == GroupId)
               ?? GroupOptions.FirstOrDefault(o => o.Id == null);
        // Setter mutates GroupId — its [NotifyPropertyChangedFor]
        // re-raises SelectedGroupOption for us. Calling OnPropertyChanged
        // here directly would loop the TwoWay ComboBox binding into a
        // setter→notify→setter stack overflow.
        set { if (value?.Id != GroupId) GroupId = value?.Id; }
    }

    partial void OnProtocolChanged(ConnectionProtocol value)
    {
        Port = value == ConnectionProtocol.Ssh ? 22 : 3389;
        OnPropertyChanged(nameof(SelectedProtocolOption));
    }

    [ObservableProperty] private string  _username       = string.Empty;
    [ObservableProperty] private string  _password       = string.Empty;

    /// <summary>True (default) → password gets persisted to Windows Credential
    /// Manager on Save. The editor exposes only the inverse opt-out toggle
    /// ("Don't save — prompt at connect time") to keep the common path
    /// frictionless; flip via that checkbox.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialStatusText))]
    private bool _saveCredential = true;

    [ObservableProperty] private string? _credentialKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordBoxVisibility))]
    [NotifyPropertyChangedFor(nameof(PasswordTextBoxVisibility))]
    private bool _showPassword;

    /// <summary>True iff the existing profile already has credentials in the
    /// Windows Credential Manager. Drives the "currently saved" status line
    /// in the editor so the user knows whether a password is on file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialStatusText))]
    private bool _credentialSaved;

    public Visibility PasswordBoxVisibility     => ShowPassword ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PasswordTextBoxVisibility => ShowPassword ? Visibility.Visible    : Visibility.Collapsed;

    public string CredentialStatusText => SaveCredential
        ? (CredentialSaved
            ? "Password is saved in Windows Credential Manager."
            : "Password will be saved to Windows Credential Manager.")
        : "Nexus won't save the password — it will prompt at connect time.";


    [ObservableProperty] private SshAuthMethod _sshAuthMethod    = SshAuthMethod.Password;
    [ObservableProperty] private string        _privateKeyPath   = string.Empty;
    [ObservableProperty] private int           _keepAliveSeconds = 30;

    // ── RDP: Display ────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRdpResolutionOption))]
    private RdpDefaultResolution _rdpPreferredResolution = RdpDefaultResolution.MatchMonitor;
    [ObservableProperty] private int    _rdpWidth      = 1920;
    [ObservableProperty] private int    _rdpHeight     = 1080;
    [ObservableProperty] private bool   _rdpFullScreen = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRdpColorDepthOption))]
    private RdpColorDepth _rdpColorDepth = RdpColorDepth.Colors32Bit;

    // ── RDP: Audio ──────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRdpAudioOption))]
    private RdpAudioMode _rdpAudioMode = RdpAudioMode.PlayOnClient;
    [ObservableProperty] private bool   _rdpAudioCapture = false;

    // ── RDP: Redirections ───────────────────────────────────────────
    [ObservableProperty] private bool   _rdpClipboard       = true;
    [ObservableProperty] private bool   _rdpDrives          = false;
    [ObservableProperty] private bool   _rdpPrinters        = false;
    [ObservableProperty] private bool   _rdpSmartCards      = false;
    [ObservableProperty] private bool   _rdpPorts           = false;
    [ObservableProperty] private bool   _rdpDevices         = false;
    [ObservableProperty] private bool   _rdpPosDevices      = false;

    // ── RDP: Gateway ────────────────────────────────────────────────
    [ObservableProperty] private string _rdpGatewayServer   = string.Empty;
    [ObservableProperty] private string _rdpGatewayUsername = string.Empty;
    [ObservableProperty] private string _rdpGatewayDomain   = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRdpGatewayUsageOption))]
    private RdpGatewayUsage _rdpGatewayUsage = RdpGatewayUsage.NoUse;

    // ── RDP: Connection ─────────────────────────────────────────────
    [ObservableProperty] private string _rdpDomain          = string.Empty;
    [ObservableProperty] private bool   _rdpAdminConsole    = false;
    [ObservableProperty] private string _rdpLoadBalanceInfo = string.Empty;
    [ObservableProperty] private bool   _rdpAutoReconnect   = true;

    // ── RDP: Authentication ─────────────────────────────────────────
    [ObservableProperty] private bool   _rdpEnableCredSsp   = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRdpAuthLevelOption))]
    private RdpAuthenticationLevel _rdpAuthLevel = RdpAuthenticationLevel.WarnIfNoAuth;
    [ObservableProperty] private bool   _rdpPromptForCreds  = false;

    // ── RDP: Performance ────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRdpNetworkOption))]
    private RdpNetworkType _rdpNetworkType = RdpNetworkType.Auto;
    [ObservableProperty] private bool   _rdpDesktopBackground   = false;
    [ObservableProperty] private bool   _rdpVisualStyles        = false;
    [ObservableProperty] private bool   _rdpFontSmoothing       = true;
    [ObservableProperty] private bool   _rdpMenuAnimations      = false;
    [ObservableProperty] private bool   _rdpWindowDrag          = false;
    [ObservableProperty] private bool   _rdpDesktopComposition  = false;
    [ObservableProperty] private bool   _rdpBitmapCaching       = true;

    // ── RDP: Keyboard ───────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRdpKeyboardHookOption))]
    private RdpKeyboardHook _rdpKeyboardHook = RdpKeyboardHook.RemoteOnFullScreen;

    // ── RDP: Connection bar ─────────────────────────────────────────
    [ObservableProperty] private bool   _rdpConnectionBar    = true;
    [ObservableProperty] private bool   _rdpPinConnectionBar = false;

    // Catalog properties for the matching ComboBoxes in EditConnectionPanel.xaml.
    public IReadOnlyList<NamedOption<RdpColorDepth>> RdpColorDepthOptions { get; } =
    [
        new("8-bit",   RdpColorDepth.Colors8Bit),
        new("15-bit",  RdpColorDepth.Colors15Bit),
        new("16-bit",  RdpColorDepth.Colors16Bit),
        new("24-bit",  RdpColorDepth.Colors24Bit),
        new("32-bit (best)", RdpColorDepth.Colors32Bit),
    ];

    public IReadOnlyList<NamedOption<RdpAudioMode>> RdpAudioOptions { get; } =
    [
        new("Play on this computer", RdpAudioMode.PlayOnClient),
        new("Play on remote",        RdpAudioMode.PlayOnServer),
        new("Do not play",           RdpAudioMode.NoPlayback),
    ];

    public IReadOnlyList<NamedOption<RdpGatewayUsage>> RdpGatewayUsageOptions { get; } =
    [
        new("Don't use a gateway",      RdpGatewayUsage.NoUse),
        new("Always use a gateway",     RdpGatewayUsage.Direct),
        new("Detect gateway settings",  RdpGatewayUsage.Detect),
        new("Use defaults",             RdpGatewayUsage.Default),
    ];

    public IReadOnlyList<NamedOption<RdpNetworkType>> RdpNetworkOptions { get; } =
    [
        // Order is "Auto first, then ramp up by speed" for UX even
        // though the underlying enum values follow the OCX's
        // CONNECTION_TYPE_* numbering (Modem=1 … Auto=7).
        new("Auto-detect",          RdpNetworkType.Auto),
        new("Modem (56 Kbps)",      RdpNetworkType.Modem),
        new("Low broadband",        RdpNetworkType.LowBroadband),
        new("Satellite",            RdpNetworkType.Satellite),
        new("High broadband",       RdpNetworkType.HighBroadband),
        new("WAN",                  RdpNetworkType.Wan),
        new("LAN (10 Mbps+)",       RdpNetworkType.Lan),
    ];

    public IReadOnlyList<NamedOption<RdpKeyboardHook>> RdpKeyboardHookOptions { get; } =
    [
        new("Apply locally",                 RdpKeyboardHook.LocalOnly),
        new("Apply on remote",               RdpKeyboardHook.RemoteAlways),
        new("Apply on remote in full screen",RdpKeyboardHook.RemoteOnFullScreen),
    ];

    public IReadOnlyList<NamedOption<RdpAuthenticationLevel>> RdpAuthLevelOptions { get; } =
    [
        new("Don't require server auth",         RdpAuthenticationLevel.NoAuthRequired),
        new("Require server authentication",     RdpAuthenticationLevel.AuthRequired),
        new("Warn if server can't be authenticated", RdpAuthenticationLevel.WarnIfNoAuth),
    ];

    // SelectedItem wrappers — ComboBox.SelectedValue + enum is unreliable
    // in WinUI 3 (the binding races against ItemsSource resolution and
    // can land empty), so we expose paired NamedOption properties and
    // bind via SelectedItem instead. Each wrapper's setter pushes the
    // enum back into the underlying [ObservableProperty] field; the
    // field's NotifyPropertyChangedFor re-raises the wrapper.

    /// <summary>Resolution presets — same set the global Settings page
    /// uses, so the per-connection override and the app default speak
    /// the same vocabulary.</summary>
    public IReadOnlyList<NamedOption<RdpDefaultResolution>> RdpResolutionOptions { get; } =
    [
        new("Match current monitor",  RdpDefaultResolution.MatchMonitor),
        new("Match panel size",       RdpDefaultResolution.MatchPanel),
        new("1024 × 768",             RdpDefaultResolution.Res1024x768),
        new("1280 × 720",             RdpDefaultResolution.Res1280x720),
        new("1366 × 768",             RdpDefaultResolution.Res1366x768),
        new("1600 × 900",             RdpDefaultResolution.Res1600x900),
        new("1920 × 1080",            RdpDefaultResolution.Res1920x1080),
        new("2560 × 1440",            RdpDefaultResolution.Res2560x1440),
        new("3840 × 2160",            RdpDefaultResolution.Res3840x2160),
    ];

    public NamedOption<RdpDefaultResolution>? SelectedRdpResolutionOption
    {
        get => RdpResolutionOptions.FirstOrDefault(o => o.Value == RdpPreferredResolution);
        set { if (value is not null) RdpPreferredResolution = value.Value; }
    }

    public NamedOption<RdpColorDepth>? SelectedRdpColorDepthOption
    {
        get => RdpColorDepthOptions.FirstOrDefault(o => o.Value == RdpColorDepth);
        set { if (value is not null) RdpColorDepth = value.Value; }
    }

    public NamedOption<RdpAudioMode>? SelectedRdpAudioOption
    {
        get => RdpAudioOptions.FirstOrDefault(o => o.Value == RdpAudioMode);
        set { if (value is not null) RdpAudioMode = value.Value; }
    }

    public NamedOption<RdpGatewayUsage>? SelectedRdpGatewayUsageOption
    {
        get => RdpGatewayUsageOptions.FirstOrDefault(o => o.Value == RdpGatewayUsage);
        set { if (value is not null) RdpGatewayUsage = value.Value; }
    }

    public NamedOption<RdpAuthenticationLevel>? SelectedRdpAuthLevelOption
    {
        get => RdpAuthLevelOptions.FirstOrDefault(o => o.Value == RdpAuthLevel);
        set { if (value is not null) RdpAuthLevel = value.Value; }
    }

    public NamedOption<RdpNetworkType>? SelectedRdpNetworkOption
    {
        get => RdpNetworkOptions.FirstOrDefault(o => o.Value == RdpNetworkType);
        set { if (value is not null) RdpNetworkType = value.Value; }
    }

    public NamedOption<RdpKeyboardHook>? SelectedRdpKeyboardHookOption
    {
        get => RdpKeyboardHookOptions.FirstOrDefault(o => o.Value == RdpKeyboardHook);
        set { if (value is not null) RdpKeyboardHook = value.Value; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private bool _isBusy = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ErrorVisibility))]
    private string _errorMessage = string.Empty;

    /// <summary>Drives the error banner visibility cleanly without .Length chains in x:Bind.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

    public ConnectionProfile? SavedProfile { get; private set; }
    public bool   IsEditing => _existingId.HasValue;
    public string Title     => IsEditing ? "Edit Connection" : "New Connection";
    public List<Group> Groups { get; private set; } = [];

    public sealed record NamedOption<T>(string Display, T Value);

    public IReadOnlyList<NamedOption<ConnectionProtocol>> ProtocolOptions { get; } =
    [
        new("SSH", ConnectionProtocol.Ssh),
        new("RDP", ConnectionProtocol.Rdp),
    ];

    public IReadOnlyList<NamedOption<SshAuthMethod>> SshAuthOptions { get; } =
    [
        new("Password",              SshAuthMethod.Password),
        new("Private key",           SshAuthMethod.PrivateKey),
        new("Keyboard-interactive",  SshAuthMethod.KeyboardInteractive),
    ];

    /// <summary>SelectedItem-style binding helpers; setters tolerate transient nulls during list refresh.</summary>
    public NamedOption<ConnectionProtocol>? SelectedProtocolOption
    {
        get => ProtocolOptions.FirstOrDefault(o => o.Value == Protocol);
        set { if (value is not null) Protocol = value.Value; }
    }

    public NamedOption<SshAuthMethod>? SelectedSshAuthOption
    {
        get => SshAuthOptions.FirstOrDefault(o => o.Value == SshAuthMethod);
        set { if (value is not null) SshAuthMethod = value.Value; }
    }

    partial void OnSshAuthMethodChanged(SshAuthMethod value) =>
        OnPropertyChanged(nameof(SelectedSshAuthOption));

    public EditConnectionViewModel(IConnectionService svc, ConnectionProfile? existing = null, ICredentialVault? vault = null)
    {
        _svc        = svc;
        _existingId = existing?.Id;

        // ObservableValidator's ErrorsChanged fires whenever a property's
        // validation results change; we mirror that into the derived
        // *Error / *ErrorVisibility properties the XAML binds to.
        ErrorsChanged += OnViewModelErrorsChanged;

        if (existing is null) return;

        DisplayName   = existing.DisplayName;
        Host          = existing.Host;
        // Protocol must be set before Port — OnProtocolChanged resets Port to the
        // protocol default (22/3389), and would clobber the persisted port otherwise.
        Protocol      = existing.Protocol;
        Port          = existing.Port;
        Tags          = existing.Tags;
        GroupId       = existing.GroupId;
        IconGlyph     = existing.IconGlyph ?? string.Empty;
        CredentialKey = existing.CredentialKey;

        // SaveCredential defaults to true (the common case). Only opt out
        // when the existing profile is explicitly configured for prompts —
        // i.e. it has no key persisted, indicating "no saved credential".
        SaveCredential = !string.IsNullOrEmpty(existing.CredentialKey);

        // Hydrate username/password from the vault if a key was previously saved,
        // so the user can see (and tweak) what was stored rather than re-typing.
        // Also flips CredentialSaved iff a vault entry actually exists, which
        // drives the "currently saved" status line in the editor.
        if (vault is not null && !string.IsNullOrEmpty(CredentialKey))
        {
            try
            {
                if (vault.Load(CredentialKey) is { } cred)
                {
                    Username        = cred.Username;
                    Password        = cred.Password;
                    CredentialSaved = true;
                }
            }
            catch { /* missing/corrupt entry — leave fields empty, user can re-enter */ }
        }

        if (existing.Protocol == ConnectionProtocol.Ssh)
        {
            var ssh = existing.SshSettings();
            SshAuthMethod    = ssh.AuthMethod;
            PrivateKeyPath   = ssh.PrivateKeyPath ?? string.Empty;
            KeepAliveSeconds = ssh.KeepAliveSeconds;
        }
        else
        {
            var rdp = existing.RdpSettings();
            RdpPreferredResolution = rdp.PreferredResolution;
            RdpWidth          = rdp.Width;
            RdpHeight         = rdp.Height;
            RdpFullScreen     = rdp.FullScreen;
            RdpColorDepth     = rdp.ColorDepth;
            RdpAudioMode      = rdp.AudioMode;
            RdpAudioCapture   = rdp.AudioCapture;
            RdpClipboard      = rdp.RedirectClipboard;
            RdpDrives         = rdp.RedirectDrives;
            RdpPrinters       = rdp.RedirectPrinters;
            RdpSmartCards     = rdp.RedirectSmartCards;
            RdpPorts          = rdp.RedirectPorts;
            RdpDevices        = rdp.RedirectDevices;
            RdpPosDevices     = rdp.RedirectPOSDevices;
            RdpGatewayServer  = rdp.GatewayServer   ?? string.Empty;
            RdpGatewayUsername= rdp.GatewayUsername ?? string.Empty;
            RdpGatewayDomain  = rdp.GatewayDomain   ?? string.Empty;
            RdpGatewayUsage   = rdp.GatewayUsageMethod;
            RdpDomain         = rdp.Domain ?? string.Empty;
            RdpAdminConsole   = rdp.AdminConsole;
            RdpLoadBalanceInfo= rdp.LoadBalanceInfo ?? string.Empty;
            RdpAutoReconnect  = rdp.AutoReconnect;
            RdpEnableCredSsp  = rdp.EnableCredSspSupport;
            RdpAuthLevel      = rdp.AuthenticationLevel;
            RdpPromptForCreds = rdp.PromptForCredentials;
            RdpNetworkType    = rdp.NetworkType;
            RdpDesktopBackground   = rdp.DesktopBackground;
            RdpVisualStyles        = rdp.VisualStyles;
            RdpFontSmoothing       = rdp.FontSmoothing;
            RdpMenuAnimations      = rdp.MenuAnimations;
            RdpWindowDrag          = rdp.WindowDrag;
            RdpDesktopComposition  = rdp.DesktopComposition;
            RdpBitmapCaching       = rdp.BitmapCaching;
            RdpKeyboardHook        = rdp.KeyboardHookMode;
            RdpConnectionBar       = rdp.ConnectionBar;
            RdpPinConnectionBar    = rdp.PinConnectionBar;
        }
    }

    public async Task LoadGroupsAsync()
    {
        Groups = [.. await _svc.GetGroupsAsync()];
        // Populate the picker too — connection panel binds the Group
        // ComboBox to GroupOptions, with a leading "(none)" option for
        // root-level connections. Re-emit SelectedGroupOption so the
        // ComboBox picks the right item now that the list exists.
        GroupOptions.Clear();
        GroupOptions.Add(new GroupPickItem(null, "(none)"));
        foreach (var g in Groups.OrderBy(x => x.Name))
            GroupOptions.Add(new GroupPickItem(g.Id, g.Name));
        OnPropertyChanged(nameof(SelectedGroupOption));
    }

    public async Task<bool> TrySaveAsync(ICredentialVault vault)
    {
        ValidateAllProperties();
        if (HasErrors)
        {
            // Build a concise summary so the banner names the offending
            // fields too, on top of the inline messages under each box.
            var fields = new List<string>();
            if (HasDisplayNameError) fields.Add("Name");
            if (HasHostError)        fields.Add("Host");
            ErrorMessage = fields.Count == 0
                ? "Please fix the highlighted fields."
                : "Missing or invalid: " + string.Join(", ", fields);
            return false;
        }

        IsBusy = true; ErrorMessage = string.Empty;
        try
        {
            string? credKey = CredentialKey;
            if (SaveCredential && !string.IsNullOrWhiteSpace(Username))
            {
                credKey = credKey ?? $"{Host.Trim()}:{Port}:{Username.Trim()}";
                vault.Save(credKey, Username.Trim(), Password);
            }

            var profile = BuildProfile(credKey);
            if (_existingId.HasValue) { profile.Id = _existingId.Value; await _svc.UpdateAsync(profile); SavedProfile = profile; }
            else SavedProfile = await _svc.CreateAsync(profile);
            return true;
        }
        catch (Exception ex) { ErrorMessage = ex.Message; return false; }
        finally { IsBusy = false; }
    }

    private ConnectionProfile BuildProfile(string? credKey) => new()
    {
        DisplayName     = DisplayName.Trim(),
        Host            = Host.Trim(),
        Port            = Port,
        Protocol        = Protocol,
        Tags            = Tags.Trim(),
        GroupId         = GroupId,
        IconGlyph       = string.IsNullOrWhiteSpace(IconGlyph) ? null : IconGlyph,
        CredentialKey   = credKey,
        RdpSettingsJson = Protocol == ConnectionProtocol.Rdp
            ? System.Text.Json.JsonSerializer.Serialize(new RdpOptions
              {
                  PreferredResolution = RdpPreferredResolution,
                  Width             = RdpWidth,
                  Height            = RdpHeight,
                  FullScreen        = RdpFullScreen,
                  ColorDepth        = RdpColorDepth,
                  AudioMode         = RdpAudioMode,
                  AudioCapture      = RdpAudioCapture,
                  RedirectClipboard = RdpClipboard,
                  RedirectDrives    = RdpDrives,
                  RedirectPrinters  = RdpPrinters,
                  RedirectSmartCards= RdpSmartCards,
                  RedirectPorts     = RdpPorts,
                  RedirectDevices   = RdpDevices,
                  RedirectPOSDevices= RdpPosDevices,
                  GatewayServer     = NullIfBlank(RdpGatewayServer),
                  GatewayUsername   = NullIfBlank(RdpGatewayUsername),
                  GatewayDomain     = NullIfBlank(RdpGatewayDomain),
                  GatewayUsageMethod= RdpGatewayUsage,
                  Domain            = NullIfBlank(RdpDomain),
                  AdminConsole      = RdpAdminConsole,
                  LoadBalanceInfo   = NullIfBlank(RdpLoadBalanceInfo),
                  AutoReconnect     = RdpAutoReconnect,
                  EnableCredSspSupport = RdpEnableCredSsp,
                  AuthenticationLevel  = RdpAuthLevel,
                  PromptForCredentials = RdpPromptForCreds,
                  NetworkType       = RdpNetworkType,
                  DesktopBackground = RdpDesktopBackground,
                  VisualStyles      = RdpVisualStyles,
                  FontSmoothing     = RdpFontSmoothing,
                  MenuAnimations    = RdpMenuAnimations,
                  WindowDrag        = RdpWindowDrag,
                  DesktopComposition= RdpDesktopComposition,
                  BitmapCaching     = RdpBitmapCaching,
                  KeyboardHookMode  = RdpKeyboardHook,
                  ConnectionBar     = RdpConnectionBar,
                  PinConnectionBar  = RdpPinConnectionBar,
              })
            : null,
        SshSettingsJson = Protocol == ConnectionProtocol.Ssh
            ? System.Text.Json.JsonSerializer.Serialize(new SshOptions
              { AuthMethod = SshAuthMethod,
                PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyPath) ? null : PrivateKeyPath,
                KeepAliveSeconds = KeepAliveSeconds })
            : null
    };

    private static string? NullIfBlank(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
