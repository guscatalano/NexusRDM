using NexusRDM.Core.Models;
using NexusRDM.Services;
using NexusRDM.Tests.ViewModels.Fakes;
using NexusRDM.ViewModels;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Tests the typing pipeline: SshSessionView calls ViewModel.SendInputAsync with
/// raw bytes (from CharacterReceived or the special-key translator). The VM
/// must forward those bytes to the underlying ISshSession iff connected.
/// </summary>
public sealed class SshSessionViewModelTests
{
    [Fact]
    public async Task SendInputAsync_ForwardsBytes_WhenConnected()
    {
        var (vm, fake) = await BuildConnectedAsync();

        await vm.SendInputAsync(new byte[] { (byte)'l', (byte)'s' });

        Assert.Equal(new byte[] { (byte)'l', (byte)'s' }, fake.Sent.ToArray());
    }

    [Fact]
    public async Task SendInputAsync_DropsBytes_WhenNotConnected()
    {
        var (vm, fake) = Build(); // not yet connected

        await vm.SendInputAsync(new byte[] { (byte)'x' });

        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task SendInputAsync_ForwardsConsecutiveCalls_PreservingOrder()
    {
        // Simulates the user typing "ls -la\r" — six char calls + one Enter call.
        var (vm, fake) = await BuildConnectedAsync();

        await vm.SendInputAsync("l"u8.ToArray());
        await vm.SendInputAsync("s"u8.ToArray());
        await vm.SendInputAsync(" "u8.ToArray());
        await vm.SendInputAsync("-"u8.ToArray());
        await vm.SendInputAsync("l"u8.ToArray());
        await vm.SendInputAsync("a"u8.ToArray());
        await vm.SendInputAsync("\r"u8.ToArray());

        Assert.Equal("ls -la\r", fake.SentAsString());
    }

    [Fact]
    public async Task SendInputAsync_HandlesUtf8MultiByteCharacters()
    {
        // CharacterReceived hands us a single char (ô = U+00F4 = 0xC3 0xB4 in UTF-8).
        var (vm, fake) = await BuildConnectedAsync();

        await vm.SendInputAsync(System.Text.Encoding.UTF8.GetBytes("ô"));

        Assert.Equal(new byte[] { 0xC3, 0xB4 }, fake.Sent.ToArray());
    }

    [Fact]
    public async Task ResizeAsync_ForwardsDimensions_WhenConnected()
    {
        var (vm, fake) = await BuildConnectedAsync();

        await vm.ResizeAsync(120, 40);

        Assert.Equal((120, 40), fake.LastResize);
    }

    [Fact]
    public async Task ResizeAsync_NoOps_WhenNotConnected()
    {
        var (vm, fake) = Build();

        await vm.ResizeAsync(120, 40);

        Assert.Null(fake.LastResize);
    }

    [Fact]
    public async Task DataReceived_FromSession_RaisesVmEvent()
    {
        var (vm, fake) = await BuildConnectedAsync();
        byte[]? captured = null;
        vm.DataReceived += (_, data) => captured = data;

        fake.EmitData(new byte[] { 0x1B, (byte)'[', (byte)'A' });

        Assert.NotNull(captured);
        Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'A' }, captured);
    }

    [Fact]
    public async Task ConnectAsync_OnSuccess_FlipsStateAndStatus()
    {
        var (vm, fake) = Build();
        Assert.True(vm.IsConnecting);
        Assert.False(vm.IsConnected);

        await vm.ConnectAsync();

        Assert.True(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.Contains("Connected", vm.StatusMessage);
        Assert.True(fake.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_OnException_SetsFailedStatus()
    {
        var fake = new FakeSshSession { ConnectThrows = new InvalidOperationException("auth denied") };
        var vm   = new SshSessionViewModel(MakeProfile(), fake, new SessionManager());

        await vm.ConnectAsync();

        Assert.False(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.Contains("auth denied", vm.StatusMessage);
        Assert.StartsWith("Failed", vm.StatusMessage);
    }

    [Fact]
    public async Task SessionDisconnectedEvent_FlipsIsConnectedFalse()
    {
        var (vm, fake) = await BuildConnectedAsync();
        Assert.True(vm.IsConnected);

        await fake.DisconnectAsync(); // raises Disconnected

        Assert.False(vm.IsConnected);
        Assert.Equal("Disconnected", vm.StatusMessage);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (SshSessionViewModel vm, FakeSshSession fake) Build()
    {
        var fake = new FakeSshSession();
        var vm   = new SshSessionViewModel(MakeProfile(), fake, new SessionManager());
        return (vm, fake);
    }

    private static async Task<(SshSessionViewModel vm, FakeSshSession fake)> BuildConnectedAsync()
    {
        var (vm, fake) = Build();
        await vm.ConnectAsync();
        return (vm, fake);
    }

    private static ConnectionProfile MakeProfile() => new()
    {
        DisplayName = "test-host",
        Host        = "10.0.0.1",
        Port        = 22,
        Protocol    = ConnectionProtocol.Ssh,
    };
}
