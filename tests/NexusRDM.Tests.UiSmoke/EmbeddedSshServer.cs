using System.Net;
using System.Net.Sockets;
using System.Text;
using FxSsh;
using FxSsh.Services;

namespace NexusRDM.Tests.UiSmoke;

/// <summary>
/// In-process SSH server for UI smoke tests. Echoes every byte the client
/// types back on the same channel and records what it received so the test
/// can assert that keystrokes traversed the full UI → SSH pipeline.
/// </summary>
public sealed class EmbeddedSshServer : IDisposable
{
    private readonly SshServer _server;
    private readonly List<byte> _received = new();

    public int    Port     { get; }
    public string Username { get; } = "tester";
    public string Password { get; } = "swordfish";

    public byte[] ReceivedBytes
    {
        get { lock (_received) return _received.ToArray(); }
    }

    public string ReceivedText => Encoding.UTF8.GetString(ReceivedBytes);

    public EmbeddedSshServer()
    {
        Port = GetFreeLoopbackPort();
        _server = new SshServer(new StartingInfo(IPAddress.Loopback, Port, "SSH-2.0-NexusRDMTest"));
        // Modern Renci.SshNet rejects SHA-1 ssh-rsa, so offer ECDSA as well —
        // the client picks whichever is acceptable.
        _server.AddHostKey("ssh-rsa",             KeyGenerator.GenerateRsaKeyPem(2048));
        _server.AddHostKey("ecdsa-sha2-nistp256", KeyGenerator.GenerateECDsaKeyPem("nistp256"));

        _server.ConnectionAccepted += (_, session) =>
        {
            session.ServiceRegistered += (_, srv) =>
            {
                switch (srv)
                {
                    case UserauthService u:
                        u.Userauth += (_, args) =>
                            args.Result = args.Username == Username && args.Password == Password;
                        break;

                    case ConnectionService c:
                        c.PtyReceived   += (_, _) => { };
                        c.WindowChange  += (_, _) => { };
                        c.EnvReceived   += (_, _) => { };
                        c.CommandOpened += (_, args) =>
                        {
                            // Any channel type is fine — FxSsh's ShellType
                            // value isn't documented and varies; we just want
                            // the byte pipe.
                            var ch = args.Channel;
                            ch.DataReceived += (_, data) =>
                            {
                                lock (_received) _received.AddRange(data);
                                ch.SendData(data);
                            };
                            ch.SendData(Encoding.UTF8.GetBytes("nexus-test$ "));
                        };
                        break;
                }
            };
        };
    }

    public void Start() => _server.Start();

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _server.Stop(); } catch { /* best effort */ }
        _server.Dispose();
    }
}
