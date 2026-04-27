using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.Core.Models;
using NexusRDM.Core.Proxmox;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

/// <summary>
/// Add / edit dialog for a <see cref="ProxmoxSource"/>. Built in code
/// rather than XAML to keep the surface area small — this dialog never
/// gets templated or themed beyond what ContentDialog ships with.
///
/// On Save the dialog returns a populated <see cref="ProxmoxSourceRowVm"/>
/// plus the secret string the caller should hand to the credential
/// vault. The dialog never touches the vault directly so it can be
/// driven from tests without DI.
/// </summary>
public sealed class ProxmoxSourceEditDialog : ContentDialog
{
    private readonly TextBox     _name        = new() { Header = "Name", PlaceholderText = "homelab" };
    private readonly TextBox     _baseUrl     = new() { Header = "Base URL", PlaceholderText = "https://pve.lan:8006" };
    private readonly ComboBox    _authMode    = new() { Header = "Auth mode" };
    private readonly TextBox     _authUser    = new() { Header = "Username / Token id",
                                                        PlaceholderText = "root@pam!nexus" };
    private readonly TextBox     _realm       = new() { Header = "Realm (password mode)", PlaceholderText = "pam" };
    private readonly PasswordBox _secret      = new() { Header = "Secret" };
    private readonly CheckBox    _ignoreTls   = new() { Content = "Ignore TLS errors (self-signed certs)" };
    private readonly NumberBox   _syncEvery   = new() { Header = "Sync every (minutes)",
                                                        Minimum = 1, Maximum = 1440, Value = 15,
                                                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private readonly ComboBox    _defaultProto = new() { Header = "Default protocol" };
    private readonly TextBox     _defaultUser = new() { Header = "Default username (optional)",
                                                        PlaceholderText = "Administrator / root / ec2-user" };
    private readonly CheckBox    _enabled     = new() { Content = "Enabled (sync runs on schedule)", IsChecked = true };

    public ProxmoxSourceRowVm Result { get; private set; }
    public string             SecretText { get; private set; } = string.Empty;

    public ProxmoxSourceEditDialog(ProxmoxSourceRowVm? existing = null)
    {
        Title             = existing is null ? "Add Proxmox source" : "Edit Proxmox source";
        PrimaryButtonText = "Save";
        CloseButtonText   = "Cancel";
        DefaultButton     = ContentDialogButton.Primary;

        _authMode.Items.Add("API token");
        _authMode.Items.Add("Password (ticket)");
        _authMode.SelectedIndex = 0;

        _defaultProto.Items.Add("Auto (Windows → RDP, Linux → SSH, else Console)");
        _defaultProto.Items.Add("RDP");
        _defaultProto.Items.Add("SSH");
        _defaultProto.Items.Add("Console (noVNC)");
        _defaultProto.SelectedIndex = 0;

        Result = existing ?? new ProxmoxSourceRowVm();
        if (existing is not null)
        {
            _name.Text             = existing.Name;
            _baseUrl.Text          = existing.BaseUrl;
            _authMode.SelectedIndex = existing.AuthMode == ProxmoxAuthMode.Password ? 1 : 0;
            _authUser.Text         = existing.AuthUser;
            _realm.Text            = existing.Realm;
            _ignoreTls.IsChecked   = existing.IgnoreTlsErrors;
            _syncEvery.Value       = existing.SyncIntervalMinutes;
            _defaultProto.SelectedIndex = (int)existing.DefaultProtocol;
            _defaultUser.Text      = existing.DefaultUsername ?? string.Empty;
            _enabled.IsChecked     = existing.IsEnabled;
            _secret.PlaceholderText = "(unchanged — leave blank to keep)";
        }

        var stack = new StackPanel { Spacing = 8, Width = 420 };
        stack.Children.Add(_name);
        stack.Children.Add(_baseUrl);
        stack.Children.Add(_authMode);
        stack.Children.Add(_authUser);
        stack.Children.Add(_realm);
        stack.Children.Add(_secret);
        // Tokens default to Privsep=1, which means the token-user
        // (e.g. root@pam!nexus) needs its OWN permission row in
        // Datacenter → Permissions. Granting the role to the underlying
        // user only is the most common reason cluster/resources comes
        // back empty.
        stack.Children.Add(new TextBlock
        {
            Text = "API tokens need explicit ACLs in Datacenter → Permissions " +
                   "(role granted to the token, not just the underlying user). " +
                   "PVEAuditor on '/' is the minimum for read-only sync.",
            FontSize     = 11,
            Opacity      = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(_ignoreTls);
        stack.Children.Add(_syncEvery);
        stack.Children.Add(_defaultProto);
        stack.Children.Add(_defaultUser);
        stack.Children.Add(_enabled);
        Content = new ScrollViewer { Content = stack, MaxHeight = 540 };

        // Realm field is only meaningful for password-mode auth; show
        // it conditionally so the API-token form looks tidy.
        _authMode.SelectionChanged += (_, _) => UpdateRealmVisibility();
        UpdateRealmVisibility();

        PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)
             || string.IsNullOrWhiteSpace(_baseUrl.Text)
             || string.IsNullOrWhiteSpace(_authUser.Text))
            {
                args.Cancel = true; // keep the dialog open
                return;
            }

            Result.Name                = _name.Text.Trim();
            Result.BaseUrl             = _baseUrl.Text.Trim();
            Result.AuthMode            = _authMode.SelectedIndex == 1 ? ProxmoxAuthMode.Password : ProxmoxAuthMode.ApiToken;
            Result.AuthUser            = _authUser.Text.Trim();
            Result.Realm               = string.IsNullOrWhiteSpace(_realm.Text) ? "pam" : _realm.Text.Trim();
            Result.IgnoreTlsErrors     = _ignoreTls.IsChecked == true;
            Result.SyncIntervalMinutes = (int)_syncEvery.Value;
            Result.DefaultProtocol     = (ProxmoxDefaultProtocol)_defaultProto.SelectedIndex;
            Result.DefaultUsername     = string.IsNullOrWhiteSpace(_defaultUser.Text) ? null : _defaultUser.Text.Trim();
            Result.IsEnabled           = _enabled.IsChecked == true;
            SecretText                 = _secret.Password ?? string.Empty;
        };
    }

    private void UpdateRealmVisibility()
    {
        var isPassword = _authMode.SelectedIndex == 1;
        _realm.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
    }
}
