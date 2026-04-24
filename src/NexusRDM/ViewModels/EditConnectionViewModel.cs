using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.ComponentModel.DataAnnotations;

namespace NexusRDM.ViewModels;

/// <summary>
/// Backs the Add/Edit connection ContentDialog.
/// Pass null to create a new connection; pass an existing profile to edit it.
/// </summary>
public sealed partial class EditConnectionViewModel : ObservableValidator
{
    private readonly IConnectionService _svc;
    private readonly Guid?              _existingId;

    // ── General ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(200)]
    private string _displayName = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Host is required")]
    [MaxLength(512)]
    private string _host = string.Empty;

    [ObservableProperty] private int                _port     = 22;
    [ObservableProperty] private ConnectionProtocol _protocol = ConnectionProtocol.Ssh;
    [ObservableProperty] private string             _tags     = string.Empty;
    [ObservableProperty] private Guid?              _groupId;

    partial void OnProtocolChanged(ConnectionProtocol value) =>
        Port = value == ConnectionProtocol.Ssh ? 22 : 3389;

    // ── Credentials ───────────────────────────────────────────────────────────

    [ObservableProperty] private string  _username       = string.Empty;
    [ObservableProperty] private string  _password       = string.Empty;
    [ObservableProperty] private bool    _saveCredential = true;
    [ObservableProperty] private string? _credentialKey;

    // ── SSH options ───────────────────────────────────────────────────────────

    [ObservableProperty] private SshAuthMethod _sshAuthMethod    = SshAuthMethod.Password;
    [ObservableProperty] private string        _privateKeyPath   = string.Empty;
    [ObservableProperty] private int           _keepAliveSeconds = 30;

    // ── RDP options ───────────────────────────────────────────────────────────

    [ObservableProperty] private int    _rdpWidth      = 1920;
    [ObservableProperty] private int    _rdpHeight     = 1080;
    [ObservableProperty] private bool   _rdpFullScreen = false;
    [ObservableProperty] private bool   _rdpClipboard  = true;
    [ObservableProperty] private bool   _rdpDrives     = false;
    [ObservableProperty] private string _rdpDomain     = string.Empty;

    // ── State / output ────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isBusy       = false;
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>Set after a successful save — code-behind reads this.</summary>
    public ConnectionProfile? SavedProfile { get; private set; }

    public bool   IsEditing => _existingId.HasValue;
    public string Title     => IsEditing ? "Edit Connection" : "New Connection";

    public List<Group> Groups { get; private set; } = [];

    // ── Constructor ───────────────────────────────────────────────────────────

    public EditConnectionViewModel(IConnectionService svc, ConnectionProfile? existing = null)
    {
        _svc        = svc;
        _existingId = existing?.Id;
        if (existing is null) return;

        DisplayName   = existing.DisplayName;
        Host          = existing.Host;
        Port          = existing.Port;
        Protocol      = existing.Protocol;
        Tags          = existing.Tags;
        GroupId       = existing.GroupId;
        CredentialKey = existing.CredentialKey;

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

    public async Task LoadGroupsAsync()
    {
        Groups = [.. await _svc.GetGroupsAsync()];
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    public async Task<bool> TrySaveAsync(ICredentialVault vault)
    {
        ValidateAllProperties();
        if (HasErrors)
        {
            ErrorMessage = "Please fix the highlighted fields.";
            return false;
        }

        IsBusy       = true;
        ErrorMessage = string.Empty;
        try
        {
            string? credKey = CredentialKey;
            if (SaveCredential && !string.IsNullOrWhiteSpace(Username))
            {
                credKey = credKey ?? $"{Host.Trim()}:{Port}:{Username.Trim()}";
                vault.Save(credKey, Username.Trim(), Password);
            }

            var profile = BuildProfile(credKey);

            if (_existingId.HasValue)
            {
                profile.Id = _existingId.Value;
                await _svc.UpdateAsync(profile);
                SavedProfile = profile;
            }
            else
            {
                SavedProfile = await _svc.CreateAsync(profile);
            }

            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ConnectionProfile BuildProfile(string? credKey) => new()
    {
        DisplayName   = DisplayName.Trim(),
        Host          = Host.Trim(),
        Port          = Port,
        Protocol      = Protocol,
        Tags          = Tags.Trim(),
        GroupId       = GroupId,
        CredentialKey = credKey,
        RdpSettingsJson = Protocol == ConnectionProtocol.Rdp
            ? System.Text.Json.JsonSerializer.Serialize(new RdpOptions
              {
                  Width             = RdpWidth,
                  Height            = RdpHeight,
                  FullScreen        = RdpFullScreen,
                  RedirectClipboard = RdpClipboard,
                  RedirectDrives    = RdpDrives,
                  Domain            = string.IsNullOrWhiteSpace(RdpDomain) ? null : RdpDomain
              })
            : null,
        SshSettingsJson = Protocol == ConnectionProtocol.Ssh
            ? System.Text.Json.JsonSerializer.Serialize(new SshOptions
              {
                  AuthMethod       = SshAuthMethod,
                  PrivateKeyPath   = string.IsNullOrWhiteSpace(PrivateKeyPath) ? null : PrivateKeyPath,
                  KeepAliveSeconds = KeepAliveSeconds
              })
            : null
    };
}
