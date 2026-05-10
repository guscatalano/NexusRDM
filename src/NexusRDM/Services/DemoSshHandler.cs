using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Core.Protocols;

namespace NexusRDM.Services;

/// <summary>
/// Decorator over the real <see cref="SshHandler"/> that returns a
/// <see cref="DemoSshSession"/> while <see cref="DemoModeService.IsActive"/>
/// is true. Lets the demo tree show off the terminal pane (canned
/// banner + prompt + a small command set) without requiring the
/// user to have a real SSH host reachable. Outside demo mode every
/// call is a straight pass-through to <see cref="SshHandler"/> — no
/// behaviour change in production.
/// </summary>
internal sealed class DemoSshHandler : ISshHandler
{
    private readonly SshHandler      _real;
    private readonly DemoModeService _demo;

    public DemoSshHandler(SshHandler real, DemoModeService demo)
    {
        _real = real;
        _demo = demo;
    }

    public ISshSession CreateSession(ConnectionProfile profile, string username, string password)
    {
        if (_demo.IsActive)
            return new DemoSshSession(profile, username);
        return _real.CreateSession(profile, username, password);
    }

    public ISshSession CreateSessionWithKey(ConnectionProfile profile, string username,
        string privateKeyPath, string? passphrase = null)
    {
        if (_demo.IsActive)
            return new DemoSshSession(profile, username);
        return _real.CreateSessionWithKey(profile, username, privateKeyPath, passphrase);
    }

    public ISshSession CreateSessionForProfile(
        ConnectionProfile profile,
        string username,
        string? storedPassword,
        string? keyPassphrase,
        SshKeyboardPromptHandler? onPrompt)
    {
        // Demo mode short-circuits all auth modes — there's no real
        // server to negotiate with. Synthetic banner + prompt only.
        if (_demo.IsActive)
            return new DemoSshSession(profile, username);
        return _real.CreateSessionForProfile(profile, username, storedPassword, keyPassphrase, onPrompt);
    }
}
