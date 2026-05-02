using Renci.SshNet;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Core.Protocols;

/// <summary>
/// Dispatches SSH session creation to the configured backend
/// (<see cref="SshLaunchMode"/>). Resolved at session-open time so
/// switching backends in Settings affects new tabs without a restart.
/// The PuTTYNG backend lives in the UI project (it embeds a Win32
/// window into the WinUI host) and is injected as a factory so this
/// class can stay in Core.
/// </summary>
public sealed class SshHandler : ISshHandler
{
    private readonly Func<SshLaunchMode> _modeProvider;
    private readonly Func<ConnectionProfile, string, string, ISshSession>? _puttyNgFactory;

    public SshHandler(
        Func<SshLaunchMode>? modeProvider = null,
        Func<ConnectionProfile, string, string, ISshSession>? puttyNgFactory = null)
    {
        _modeProvider   = modeProvider ?? (() => SshLaunchMode.Embedded);
        _puttyNgFactory = puttyNgFactory;
    }

    public ISshSession CreateSession(ConnectionProfile profile, string username, string password)
    {
        var mode = _modeProvider();
        if (mode == SshLaunchMode.PuttyNg && _puttyNgFactory is not null)
            return _puttyNgFactory(profile, username, password);

        // Fallback / default: embedded VtNetCore terminal via SSH.NET.
        var auth = new PasswordAuthenticationMethod(username, password);
        var info = new ConnectionInfo(profile.Host, profile.Port, username, auth)
                   { Timeout = TimeSpan.FromSeconds(15) };
        return new SshSession(profile.Id, new SshClient(info));
    }

    public ISshSession CreateSessionWithKey(ConnectionProfile profile, string username,
        string privateKeyPath, string? passphrase = null)
    {
        // Key auth always uses the embedded backend for now — PuTTYNG
        // handles keys via Pageant or -i, neither of which we wire up
        // automatically here. Adding key support to the PuTTYNG path
        // is a polish item.
        var keyFile = passphrase is null
            ? new PrivateKeyFile(privateKeyPath)
            : new PrivateKeyFile(privateKeyPath, passphrase);

        var auth = new PrivateKeyAuthenticationMethod(username, keyFile);
        var info = new ConnectionInfo(profile.Host, profile.Port, username, auth)
                   { Timeout = TimeSpan.FromSeconds(15) };
        return new SshSession(profile.Id, new SshClient(info));
    }
}
