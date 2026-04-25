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

    [ObservableProperty][NotifyDataErrorInfo][Required(ErrorMessage = "Name is required")][MaxLength(200)]
    private string _displayName = string.Empty;

    [ObservableProperty][NotifyDataErrorInfo][Required(ErrorMessage = "Host is required")][MaxLength(512)]
    private string _host = string.Empty;

    [ObservableProperty] private int                _port     = 22;
    [ObservableProperty] private ConnectionProtocol _protocol = ConnectionProtocol.Ssh;
    [ObservableProperty] private string             _tags     = string.Empty;
    [ObservableProperty] private Guid?              _groupId;

    partial void OnProtocolChanged(ConnectionProtocol value)
    {
        Port = value == ConnectionProtocol.Ssh ? 22 : 3389;
        OnPropertyChanged(nameof(SelectedProtocolOption));
    }

    [ObservableProperty] private string  _username       = string.Empty;
    [ObservableProperty] private string  _password       = string.Empty;
    [ObservableProperty] private bool    _saveCredential = true;
    [ObservableProperty] private string? _credentialKey;

    [ObservableProperty] private SshAuthMethod _sshAuthMethod    = SshAuthMethod.Password;
    [ObservableProperty] private string        _privateKeyPath   = string.Empty;
    [ObservableProperty] private int           _keepAliveSeconds = 30;

    [ObservableProperty] private int    _rdpWidth      = 1920;
    [ObservableProperty] private int    _rdpHeight     = 1080;
    [ObservableProperty] private bool   _rdpFullScreen = false;
    [ObservableProperty] private bool   _rdpClipboard  = true;
    [ObservableProperty] private bool   _rdpDrives     = false;
    [ObservableProperty] private string _rdpDomain     = string.Empty;

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
        if (existing is null) return;

        DisplayName   = existing.DisplayName;
        Host          = existing.Host;
        // Protocol must be set before Port — OnProtocolChanged resets Port to the
        // protocol default (22/3389), and would clobber the persisted port otherwise.
        Protocol      = existing.Protocol;
        Port          = existing.Port;
        Tags          = existing.Tags;
        GroupId       = existing.GroupId;
        CredentialKey = existing.CredentialKey;

        // Reflect whether a credential is currently in the vault rather than
        // always defaulting to true.
        SaveCredential = !string.IsNullOrEmpty(existing.CredentialKey);

        // Hydrate username/password from the vault if a key was previously saved,
        // so the user can see (and tweak) what was stored rather than re-typing.
        if (vault is not null && !string.IsNullOrEmpty(CredentialKey))
        {
            try
            {
                if (vault.Load(CredentialKey) is { } cred)
                {
                    Username = cred.Username;
                    Password = cred.Password;
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
            RdpWidth      = rdp.Width;
            RdpHeight     = rdp.Height;
            RdpFullScreen = rdp.FullScreen;
            RdpClipboard  = rdp.RedirectClipboard;
            RdpDrives     = rdp.RedirectDrives;
            RdpDomain     = rdp.Domain ?? string.Empty;
        }
    }

    public async Task LoadGroupsAsync() =>
        Groups = [.. await _svc.GetGroupsAsync()];

    public async Task<bool> TrySaveAsync(ICredentialVault vault)
    {
        ValidateAllProperties();
        if (HasErrors) { ErrorMessage = "Please fix the highlighted fields."; return false; }

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
        CredentialKey   = credKey,
        RdpSettingsJson = Protocol == ConnectionProtocol.Rdp
            ? System.Text.Json.JsonSerializer.Serialize(new RdpOptions
              { Width = RdpWidth, Height = RdpHeight, FullScreen = RdpFullScreen,
                RedirectClipboard = RdpClipboard, RedirectDrives = RdpDrives,
                Domain = string.IsNullOrWhiteSpace(RdpDomain) ? null : RdpDomain })
            : null,
        SshSettingsJson = Protocol == ConnectionProtocol.Ssh
            ? System.Text.Json.JsonSerializer.Serialize(new SshOptions
              { AuthMethod = SshAuthMethod,
                PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyPath) ? null : PrivateKeyPath,
                KeepAliveSeconds = KeepAliveSeconds })
            : null
    };
}
