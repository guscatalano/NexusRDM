using System.Text;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Services;

/// <summary>
/// Demo-mode <see cref="ISshSession"/> backing. Emits canned shell
/// output instead of opening a real network connection, so demo mode
/// can show off the terminal pane (and so the recorder can capture
/// an SSH session without needing a real host). On
/// <see cref="ConnectAsync"/> we flush a login banner, MOTD, and a
/// prompt; on <see cref="SendAsync"/> we echo the input as the
/// remote PTY would, then respond to a small set of canned commands
/// (ls / uptime / whoami / clear). Any other input gets a generic
/// "command not found" line so the terminal still feels live.
/// </summary>
internal sealed class DemoSshSession : ISshSession
{
    // ANSI escape introducer. Using  so the source file stays
    // 7-bit ASCII and editors don't mangle the byte.
    private const string Esc = "";

    private readonly ConnectionProfile _profile;
    private readonly string            _username;
    private readonly StringBuilder     _lineBuffer = new();
    private CancellationTokenSource?   _idleCts;

    public DemoSshSession(ConnectionProfile profile, string username)
    {
        _profile  = profile;
        _username = string.IsNullOrWhiteSpace(username) ? "demo" : username;
    }

    public Guid ConnectionId => _profile.Id;
    public bool IsConnected  { get; private set; }

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler?         Disconnected;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;

        // Push the banner asynchronously so the UI can subscribe to
        // DataReceived between Connect and the first byte. Real SSH
        // sessions also see banner-after-connect, so this preserves
        // the "first frame is empty" timing the VM expects.
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(120, token);
            Emit(
                $"{Esc}[1;32mWelcome to Ubuntu 22.04.4 LTS{Esc}[0m (GNU/Linux 5.15.0 x86_64)\r\n" +
                "\r\n" +
                " * Documentation:  https://help.ubuntu.com\r\n" +
                " * Management:     https://landscape.canonical.com\r\n" +
                " * Support:        https://ubuntu.com/advantage\r\n" +
                "\r\n" +
                $"Last login: {DateTime.Now:ddd MMM d HH:mm:ss yyyy} from 10.0.0.42\r\n");
            await Task.Delay(80, token);
            EmitPrompt();
        }, token);

        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.CompletedTask;

        // PTY echo: just push the bytes back so the user sees what
        // they typed. Track the in-progress line so we can run
        // canned responses on Enter.
        Emit(data);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0x0d || b == 0x0a) // CR or LF — Enter
            {
                Emit("\r\n");
                RunCommand(_lineBuffer.ToString().Trim());
                _lineBuffer.Clear();
            }
            else if (b == 0x7f || b == 0x08) // backspace
            {
                if (_lineBuffer.Length > 0)
                    _lineBuffer.Length--;
            }
            else if (b >= 0x20 && b < 0x7f) // printable ASCII
            {
                _lineBuffer.Append((char)b);
            }
        }
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DisconnectAsync()
    {
        if (!IsConnected) return Task.CompletedTask;
        IsConnected = false;
        try { _idleCts?.Cancel(); } catch { }
        Disconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { _idleCts?.Cancel(); } catch { }
        _idleCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RunCommand(string cmd)
    {
        if (string.IsNullOrEmpty(cmd))
        {
            EmitPrompt();
            return;
        }

        // Tiny canned fixture set — covers what the recorder needs
        // and what a curious user is likely to type while exploring.
        switch (cmd.Split(' ')[0])
        {
            case "ls":
                Emit(
                    $"{Esc}[1;34mDocuments{Esc}[0m  " +
                    $"{Esc}[1;34mDownloads{Esc}[0m  " +
                    $"{Esc}[1;34mPictures{Esc}[0m  " +
                    $"{Esc}[1;32mdeploy.sh{Esc}[0m  " +
                    "notes.md  README.md\r\n");
                break;
            case "pwd":
                Emit($"/home/{_username}\r\n");
                break;
            case "whoami":
                Emit($"{_username}\r\n");
                break;
            case "uname":
                Emit("Linux\r\n");
                break;
            case "uptime":
                Emit($" {DateTime.Now:HH:mm:ss} up 4 days,  2:13,  1 user,  load average: 0.08, 0.04, 0.01\r\n");
                break;
            case "date":
                Emit($"{DateTime.Now:ddd MMM d HH:mm:ss zzz yyyy}\r\n");
                break;
            case "clear":
                Emit($"{Esc}[2J{Esc}[H");
                break;
            case "echo":
                Emit(cmd.Length > 5 ? cmd[5..] + "\r\n" : "\r\n");
                break;
            case "exit":
            case "logout":
                Emit("logout\r\n");
                _ = DisconnectAsync();
                return;
            default:
                Emit($"-bash: {cmd.Split(' ')[0]}: command not found\r\n");
                break;
        }
        EmitPrompt();
    }

    private void EmitPrompt()
    {
        var host = _profile.Host?.Split('.')[0] ?? "host";
        Emit($"{Esc}[1;32m{_username}@{host}{Esc}[0m:{Esc}[1;34m~{Esc}[0m$ ");
    }

    private void Emit(string s) => Emit(Encoding.UTF8.GetBytes(s));
    private void Emit(byte[] data) => DataReceived?.Invoke(this, data);
}
