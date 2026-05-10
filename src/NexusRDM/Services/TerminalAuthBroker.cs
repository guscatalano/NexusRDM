using System.Text;

namespace NexusRDM.Services;

/// <summary>
/// Routes SSH keyboard-interactive auth prompts through the terminal
/// instead of a modal dialog. While a prompt is active, the broker
/// owns the terminal: it writes the prompt text via
/// <see cref="OutputToTerminal"/> (consumed by the view, fed into the
/// VtNetCore-backed <c>TerminalControl</c>), and consumes keystrokes
/// via <see cref="OnUserInput"/> until the user presses Enter. The
/// View checks <see cref="IsActive"/> on every keystroke and routes
/// to either the broker (auth) or the SSH session (shell).
///
/// Echo handling matches a real terminal:
///   • Echoable prompts (e.g. PAM "Username:") echo the typed char.
///   • Masked prompts (passwords / OTPs) write nothing — same as
///     stock <c>ssh</c>. Backspace is honoured but invisible while
///     masked. We deliberately don't echo asterisks; the bashlike
///     "no feedback" feel is the standard for SSH password entry.
/// </summary>
public sealed class TerminalAuthBroker
{
    private readonly object _lock = new();
    private TaskCompletionSource<string?>? _pending;
    private StringBuilder? _buffer;
    private bool _masked;

    /// <summary>True while a prompt round is in flight. The view
    /// reads this to decide whether keystrokes go to the broker or
    /// to the SSH session.</summary>
    public bool IsActive => Volatile.Read(ref _pending) is not null;

    /// <summary>Raised whenever the broker wants bytes painted on the
    /// terminal — both prompt text from the server and echo of typed
    /// chars during auth. The view forwards into
    /// <c>TerminalControl.Feed</c>.</summary>
    public event EventHandler<byte[]>? OutputToTerminal;

    /// <summary>Called by SSH.NET (via the
    /// <see cref="Core.Interfaces.SshKeyboardPromptHandler"/> delegate)
    /// when the server sends a prompt. Returns when the user presses
    /// Enter. Cancellation propagates from the SSH connect token.</summary>
    public Task<string?> PromptAsync(string text, bool masked, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pending = tcs;
            _buffer  = new StringBuilder();
            _masked  = masked;
        }

        // Push the prompt text onto the terminal. SSH prompts often
        // arrive with a trailing space but no newline (PuTTY and ssh
        // leave them inline so the user types on the same row); we
        // don't reformat — pass through what the server sent.
        var bytes = Encoding.UTF8.GetBytes(text);
        OutputToTerminal?.Invoke(this, bytes);

        ct.Register(() =>
        {
            // Connection cancelled mid-auth — release the waiter.
            TaskCompletionSource<string?>? toCancel = null;
            lock (_lock)
            {
                if (_pending == tcs)
                {
                    toCancel = _pending;
                    _pending = null;
                    _buffer  = null;
                }
            }
            toCancel?.TrySetCanceled(ct);
        });

        return tcs.Task;
    }

    /// <summary>Consume a chunk of bytes typed by the user while a
    /// prompt is active. Returns true if the broker absorbed the
    /// input (caller should NOT also forward to SSH); false if the
    /// broker isn't listening (caller proceeds normally).</summary>
    public bool OnUserInput(byte[] data)
    {
        TaskCompletionSource<string?>? tcs;
        StringBuilder? buf;
        bool masked;
        lock (_lock)
        {
            if (_pending is null || _buffer is null) return false;
            tcs    = _pending;
            buf    = _buffer;
            masked = _masked;
        }

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];

            if (b == 0x0d || b == 0x0a) // Enter
            {
                // CRLF the user's row regardless of mask state — the
                // server's response will paint on the next line.
                OutputToTerminal?.Invoke(this, "\r\n"u8.ToArray());
                string response;
                lock (_lock)
                {
                    response = buf.ToString();
                    if (_pending == tcs)
                    {
                        _pending = null;
                        _buffer  = null;
                    }
                }
                tcs!.TrySetResult(response);
                return true;
            }

            if (b == 0x7f || b == 0x08) // Backspace / DEL
            {
                if (buf.Length > 0)
                {
                    buf.Length--;
                    // Both echoable and masked prompts erase the last
                    // visible glyph — the masked path was previously
                    // silent, but users expect to see the asterisk
                    // count shrink so they know the character was
                    // dropped. \b \b = move-back, write space (clears
                    // the cell), move-back again.
                    OutputToTerminal?.Invoke(this, "\b \b"u8.ToArray());
                }
                continue;
            }

            if (b == 0x03) // Ctrl+C — abort the auth round
            {
                OutputToTerminal?.Invoke(this, "\r\n"u8.ToArray());
                lock (_lock)
                {
                    if (_pending == tcs)
                    {
                        _pending = null;
                        _buffer  = null;
                    }
                }
                tcs!.TrySetResult(null);
                return true;
            }

            if (b >= 0x20 && b < 0x7f)
            {
                buf.Append((char)b);
                // Echo the actual char for echoable prompts, '*' for
                // masked prompts. Asterisk feedback gives the user a
                // visual count + lets backspace look meaningful.
                OutputToTerminal?.Invoke(
                    this,
                    masked ? "*"u8.ToArray() : new[] { b });
                continue;
            }

            // Anything else (arrows, function keys, multi-byte UTF-8
            // sequences mid-stream) is intentionally dropped during
            // auth — same restriction as ssh.exe's password prompt.
        }

        return true;
    }
}
