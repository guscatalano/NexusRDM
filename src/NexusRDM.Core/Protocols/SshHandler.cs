using Renci.SshNet;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Core.Protocols;

public sealed class SshHandler : ISshHandler
{
    public ISshSession CreateSession(ConnectionProfile profile, string username, string password)
    {
        var auth   = new PasswordAuthenticationMethod(username, password);
        var info   = new ConnectionInfo(profile.Host, profile.Port, username, auth)
                     { Timeout = TimeSpan.FromSeconds(15) };
        return new SshSession(profile.Id, new SshClient(info));
    }

    public ISshSession CreateSessionWithKey(ConnectionProfile profile, string username,
        string privateKeyPath, string? passphrase = null)
    {
        var keyFile = passphrase is null
            ? new PrivateKeyFile(privateKeyPath)
            : new PrivateKeyFile(privateKeyPath, passphrase);

        var auth = new PrivateKeyAuthenticationMethod(username, keyFile);
        var info = new ConnectionInfo(profile.Host, profile.Port, username, auth)
                   { Timeout = TimeSpan.FromSeconds(15) };
        return new SshSession(profile.Id, new SshClient(info));
    }
}
