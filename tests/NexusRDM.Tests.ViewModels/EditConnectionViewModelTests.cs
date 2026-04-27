using NexusRDM.Core.Models;
using NexusRDM.Tests.ViewModels.Fakes;
using NexusRDM.ViewModels;

namespace NexusRDM.Tests.ViewModels;

public sealed class EditConnectionViewModelTests
{
    // ── Defaults for a brand-new connection ──────────────────────────────────

    [Fact]
    public void New_DefaultsProtocolToSsh()
    {
        var vm = new EditConnectionViewModel(new FakeConnectionService());

        Assert.Equal(ConnectionProtocol.Ssh, vm.Protocol);
        Assert.Equal(22, vm.Port);
        Assert.False(vm.IsEditing);
        Assert.Equal("New Connection", vm.Title);
    }

    [Fact]
    public void New_SelectedProtocolOption_ResolvesToSshWrapper()
    {
        var vm = new EditConnectionViewModel(new FakeConnectionService());

        Assert.NotNull(vm.SelectedProtocolOption);
        Assert.Equal(ConnectionProtocol.Ssh, vm.SelectedProtocolOption!.Value);
        Assert.Equal("SSH", vm.SelectedProtocolOption.Display);
    }

    // ── Protocol change side effects ─────────────────────────────────────────

    [Fact]
    public void OnProtocolChanged_FromSshToRdp_UpdatesPortDefault()
    {
        var vm = new EditConnectionViewModel(new FakeConnectionService());

        vm.Protocol = ConnectionProtocol.Rdp;

        Assert.Equal(3389, vm.Port);
    }

    [Fact]
    public void SelectedProtocolOptionSetter_UpdatesProtocolEnum()
    {
        var vm = new EditConnectionViewModel(new FakeConnectionService());
        var rdp = vm.ProtocolOptions.Single(o => o.Value == ConnectionProtocol.Rdp);

        vm.SelectedProtocolOption = rdp;

        Assert.Equal(ConnectionProtocol.Rdp, vm.Protocol);
    }

    // ── Loading an existing profile (the bug-prone path) ─────────────────────

    [Fact]
    public void Edit_PreservesPersistedPort_NotProtocolDefault()
    {
        // Bug we hit: Protocol = existing.Protocol triggered OnProtocolChanged
        // which clobbered Port back to the protocol default.
        var existing = MakeRdp("svr", "10.0.0.1", port: 33890);

        var vm = new EditConnectionViewModel(new FakeConnectionService(), existing);

        Assert.Equal(33890, vm.Port);
        Assert.Equal(ConnectionProtocol.Rdp, vm.Protocol);
    }

    [Fact]
    public void Edit_PopulatesAllGeneralFields()
    {
        var existing = MakeSsh("web", "192.168.1.10");
        existing.Tags = "prod, web";

        var vm = new EditConnectionViewModel(new FakeConnectionService(), existing);

        Assert.True(vm.IsEditing);
        Assert.Equal("Edit Connection", vm.Title);
        Assert.Equal("web", vm.DisplayName);
        Assert.Equal("192.168.1.10", vm.Host);
        Assert.Equal("prod, web", vm.Tags);
    }

    [Fact]
    public void Edit_HydratesUsernameAndPasswordFromVault()
    {
        // Bug: vault wasn't being read on edit, fields stayed blank.
        var vault = new FakeCredentialVault();
        vault.Save("nx-key", "alice", "s3cret");

        var existing = MakeSsh("web", "10.0.0.1");
        existing.CredentialKey = "nx-key";

        var vm = new EditConnectionViewModel(new FakeConnectionService(), existing, vault);

        Assert.Equal("alice", vm.Username);
        Assert.Equal("s3cret", vm.Password);
    }

    [Fact]
    public void Edit_SaveCredentialReflectsActualVaultState()
    {
        // Bug: SaveCredential was always defaulted true regardless of state.
        var vault = new FakeCredentialVault();
        var existing = MakeSsh("no-creds", "10.0.0.2");
        existing.CredentialKey = null;

        var vm = new EditConnectionViewModel(new FakeConnectionService(), existing, vault);

        Assert.False(vm.SaveCredential);
    }

    [Fact]
    public void Edit_SaveCredentialTrue_WhenVaultHasEntry()
    {
        var vault = new FakeCredentialVault();
        vault.Save("k", "u", "p");
        var existing = MakeSsh("has-creds", "10.0.0.3");
        existing.CredentialKey = "k";

        var vm = new EditConnectionViewModel(new FakeConnectionService(), existing, vault);

        Assert.True(vm.SaveCredential);
    }

    [Fact]
    public void Edit_SshSettings_AreLoaded()
    {
        var existing = MakeSsh("k-auth", "10.0.0.4");
        existing.SshSettingsJson = System.Text.Json.JsonSerializer.Serialize(new SshOptions
        {
            AuthMethod       = SshAuthMethod.PrivateKey,
            PrivateKeyPath   = @"C:\keys\id_rsa",
            KeepAliveSeconds = 60,
        });

        var vm = new EditConnectionViewModel(new FakeConnectionService(), existing);

        Assert.Equal(SshAuthMethod.PrivateKey, vm.SshAuthMethod);
        Assert.Equal(@"C:\keys\id_rsa", vm.PrivateKeyPath);
        Assert.Equal(60, vm.KeepAliveSeconds);
    }

    // ── Save path (TrySaveAsync) ─────────────────────────────────────────────

    [Fact]
    public async Task Save_NewConnection_CallsCreate()
    {
        var svc   = new FakeConnectionService();
        var vault = new FakeCredentialVault();
        var vm = new EditConnectionViewModel(svc)
        {
            DisplayName = "fresh", Host = "1.2.3.4", Port = 22, Protocol = ConnectionProtocol.Ssh,
            Username = "u", Password = "p", SaveCredential = true,
        };

        var ok = await vm.TrySaveAsync(vault);

        Assert.True(ok);
        Assert.NotNull(svc.LastCreated);
        Assert.Null(svc.LastUpdated);
        Assert.Equal("fresh", svc.LastCreated!.DisplayName);
    }

    [Fact]
    public async Task Save_EditedConnection_CallsUpdate_NotCreate()
    {
        // Bug: edit flow was creating duplicates instead of updating.
        var svc   = new FakeConnectionService();
        var vault = new FakeCredentialVault();
        var existing = MakeSsh("old", "1.1.1.1");
        svc.Profiles.Add(existing);

        var vm = new EditConnectionViewModel(svc, existing, vault);
        vm.DisplayName = "renamed";

        var ok = await vm.TrySaveAsync(vault);

        Assert.True(ok);
        Assert.Null(svc.LastCreated);
        Assert.NotNull(svc.LastUpdated);
        Assert.Equal(existing.Id, svc.LastUpdated!.Id);
        Assert.Equal("renamed", svc.LastUpdated.DisplayName);
    }

    [Fact]
    public async Task Save_WhenSaveCredentialChecked_WritesToVault()
    {
        var svc   = new FakeConnectionService();
        var vault = new FakeCredentialVault();
        var vm = new EditConnectionViewModel(svc)
        {
            DisplayName = "n", Host = "h", Port = 22, Protocol = ConnectionProtocol.Ssh,
            Username = "alice", Password = "pw", SaveCredential = true,
        };

        await vm.TrySaveAsync(vault);

        Assert.Single(vault.SaveLog);
        Assert.Equal("alice", vault.SaveLog[0].Username);
        Assert.Equal("pw",    vault.SaveLog[0].Password);
    }

    [Fact]
    public async Task Save_ValidationFailure_DoesNotPersist()
    {
        var svc   = new FakeConnectionService();
        var vault = new FakeCredentialVault();
        var vm = new EditConnectionViewModel(svc); // empty DisplayName + Host → required-field errors

        var ok = await vm.TrySaveAsync(vault);

        Assert.False(ok);
        Assert.Null(svc.LastCreated);
        Assert.True(vm.HasError);
        Assert.NotEqual(string.Empty, vm.ErrorMessage);
    }

    [Fact]
    public async Task Save_ServiceThrows_SurfacesMessageInErrorMessage()
    {
        // Bug-driven: dialog must show the actual error string copy-pasteable.
        var svc = new FakeConnectionService
        {
            OnCreate = _ => throw new InvalidOperationException("DB locked")
        };
        var vm = new EditConnectionViewModel(svc)
        {
            DisplayName = "n", Host = "h", Port = 22, Protocol = ConnectionProtocol.Ssh
        };

        var ok = await vm.TrySaveAsync(new FakeCredentialVault());

        Assert.False(ok);
        Assert.Equal("DB locked", vm.ErrorMessage);
        Assert.True(vm.HasError);
    }

    // ── ErrorVisibility (the new bool→Visibility VM property) ────────────────

    [Fact]
    public void ErrorVisibility_TracksErrorMessage()
    {
        var vm = new EditConnectionViewModel(new FakeConnectionService());
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.ErrorVisibility);

        vm.ErrorMessage = "boom";
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.ErrorVisibility);

        vm.ErrorMessage = string.Empty;
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.ErrorVisibility);
    }

    // ── Validation surfacing (the per-field error/visibility properties) ─────

    [Fact]
    public async Task TrySave_NamesMissingFields_InErrorMessage()
    {
        // Recent fix: the banner used to say only "Please fix the
        // highlighted fields", but stock TextBox doesn't visualize
        // INotifyDataErrorInfo so users had no idea which were wrong.
        // It now lists the offending field names.
        var vm = new EditConnectionViewModel(new FakeConnectionService());
        var ok = await vm.TrySaveAsync(new FakeCredentialVault());

        Assert.False(ok);
        Assert.Contains("Name", vm.ErrorMessage);
        Assert.Contains("Host", vm.ErrorMessage);
    }

    [Fact]
    public async Task TrySave_PopulatesPerFieldErrorProperties()
    {
        var vm = new EditConnectionViewModel(new FakeConnectionService());

        await vm.TrySaveAsync(new FakeCredentialVault());

        Assert.True(vm.HasDisplayNameError);
        Assert.True(vm.HasHostError);
        Assert.NotEqual(string.Empty, vm.DisplayNameError);
        Assert.NotEqual(string.Empty, vm.HostError);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.DisplayNameErrorVisibility);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.HostErrorVisibility);
    }

    [Fact]
    public async Task TrySave_FixingFields_ClearsErrorVisibility()
    {
        var vm = new EditConnectionViewModel(new FakeConnectionService());
        await vm.TrySaveAsync(new FakeCredentialVault()); // make errors appear

        vm.DisplayName = "fixed";
        vm.Host        = "fixed.local";

        Assert.False(vm.HasDisplayNameError);
        Assert.False(vm.HasHostError);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.DisplayNameErrorVisibility);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.HostErrorVisibility);
    }

    // ── Group selector (no-recursion fix) ────────────────────────────────────

    [Fact]
    public async Task SelectedGroupOption_AssigningSameValue_DoesNotLoop()
    {
        // Regression: the setter used to fire a manual OnPropertyChanged
        // on SelectedGroupOption; the TwoWay ComboBox binding then
        // re-entered the setter → infinite recursion → StackOverflow.
        // Idempotent assignment + the [NotifyPropertyChangedFor] on
        // GroupId is the fix.
        var svc = new FakeConnectionService();
        svc.Groups.Add(new Group { Id = Guid.NewGuid(), Name = "Lab" });
        var vm = new EditConnectionViewModel(svc);
        await vm.LoadGroupsAsync();

        var lab = vm.GroupOptions.First(o => o.Id is not null);

        // Assigning the same option many times must not blow the stack.
        for (int i = 0; i < 1000; i++) vm.SelectedGroupOption = lab;

        Assert.Equal(lab.Id, vm.GroupId);
    }

    [Fact]
    public async Task SelectedGroupOption_TracksGroupIdChanges()
    {
        var labId = Guid.NewGuid();
        var svc = new FakeConnectionService();
        svc.Groups.Add(new Group { Id = labId, Name = "Lab" });
        var vm = new EditConnectionViewModel(svc);
        await vm.LoadGroupsAsync();

        // Mutating the underlying field re-publishes the wrapper via the
        // [NotifyPropertyChangedFor(nameof(SelectedGroupOption))] hookup.
        vm.GroupId = labId;
        Assert.Equal(labId, vm.SelectedGroupOption?.Id);

        vm.GroupId = null;
        Assert.Null(vm.SelectedGroupOption?.Id);
    }

    // ── Icon glyph round-trip ────────────────────────────────────────────────

    [Fact]
    public async Task Save_PersistsIconGlyph()
    {
        // Use  (Server) as a literal codepoint so the source-file
        // encoding can't accidentally collapse the glyph.
        var svc = new FakeConnectionService();
        var vm = new EditConnectionViewModel(svc)
        {
            DisplayName = "n", Host = "h", Port = 22, Protocol = ConnectionProtocol.Ssh,
            IconGlyph   = "",
        };

        await vm.TrySaveAsync(new FakeCredentialVault());

        Assert.Equal("", svc.LastCreated!.IconGlyph);
    }

    [Fact]
    public async Task Save_EmptyIconGlyph_PersistsAsNull()
    {
        // The model treats "no icon picked" as null — falling back to
        // empty would leave a phantom string in the DB.
        var svc = new FakeConnectionService();
        var vm = new EditConnectionViewModel(svc)
        {
            DisplayName = "n", Host = "h", Port = 22, Protocol = ConnectionProtocol.Ssh,
            IconGlyph   = "  ",
        };

        await vm.TrySaveAsync(new FakeCredentialVault());

        Assert.Null(svc.LastCreated!.IconGlyph);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ConnectionProfile MakeSsh(string name, string host, int port = 22) => new()
    {
        DisplayName = name, Host = host, Port = port, Protocol = ConnectionProtocol.Ssh
    };

    private static ConnectionProfile MakeRdp(string name, string host, int port = 3389) => new()
    {
        DisplayName = name, Host = host, Port = port, Protocol = ConnectionProtocol.Rdp
    };
}
