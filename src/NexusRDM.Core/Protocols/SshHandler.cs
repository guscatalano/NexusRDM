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

    public ISshSession CreateSessionForProfile(
        ConnectionProfile profile,
        string username,
        string? storedPassword,
        string? keyPassphrase,
        SshKeyboardPromptHandler? onPrompt)
    {
        // PuttyNg backend ignores AuthMode for now — its auth surface
        // is the PuTTYNG command line + Pageant, not SSH.NET. Hand off
        // to the existing factory which expects the raw password.
        var mode = _modeProvider();
        if (mode == SshLaunchMode.PuttyNg && _puttyNgFactory is not null)
            return _puttyNgFactory(profile, username, storedPassword ?? string.Empty);

        // Lazy + async SshClient construction. Async because for
        // Stored / KeyThenPrompt modes we may need to prompt for the
        // password via the terminal broker (when no password is
        // stored). We also register BOTH PasswordAuthenticationMethod
        // and KeyboardInteractiveAuthenticationMethod side-by-side —
        // some servers advertise only `password`, others only
        // `keyboard-interactive`; SSH.NET will pick whichever matches
        // the server's user-auth banner.
        async Task<SshClient> Factory(string resolvedUser, int attempt, CancellationToken fct)
        {
            var resolvedMethods = new List<AuthenticationMethod>();
            // On the first attempt we honour the stored password (so a
            // saved-creds connection logs in silently). On retries the
            // stored password was clearly wrong — drop it and force a
            // fresh prompt via the broker so the user can correct it.
            string? resolvedPassword = attempt == 0 ? storedPassword : null;

            switch (profile.SshAuthMode)
            {
                case SshAuthMode.PrivateKey:
                case SshAuthMode.KeyThenPrompt:
                    if (!string.IsNullOrEmpty(profile.SshKeyFilePath) && File.Exists(profile.SshKeyFilePath))
                    {
                        var kf = string.IsNullOrEmpty(keyPassphrase)
                            ? new PrivateKeyFile(profile.SshKeyFilePath!)
                            : new PrivateKeyFile(profile.SshKeyFilePath!, keyPassphrase);
                        resolvedMethods.Add(new PrivateKeyAuthenticationMethod(resolvedUser, kf));
                    }
                    if (profile.SshAuthMode == SshAuthMode.KeyThenPrompt)
                    {
                        // If key fails, prompt for password via terminal
                        // and add both Password + KeyboardInteractive
                        // (server advertises whichever it accepts).
                        if (string.IsNullOrEmpty(resolvedPassword) && onPrompt is not null)
                            resolvedPassword = await onPrompt(
                                $"{resolvedUser}@{profile.Host}'s password: ", true, fct);
                        if (resolvedPassword is not null)
                            resolvedMethods.Add(new PasswordAuthenticationMethod(resolvedUser, resolvedPassword));
                        if (onPrompt is not null)
                            resolvedMethods.Add(BuildInteractive(resolvedUser, onPrompt));
                    }
                    break;

                case SshAuthMode.ServerPrompt:
                    // Server-driven prompts for both password-style and
                    // keyboard-interactive-style auth. Keyboard-interactive
                    // covers the PAM path; we also add a PasswordAuthenticationMethod
                    // backed by a terminal-prompted secret in case the
                    // server only advertises `password`.
                    if (onPrompt is not null)
                    {
                        resolvedMethods.Add(BuildInteractive(resolvedUser, onPrompt));
                        var pw = await onPrompt(
                            $"{resolvedUser}@{profile.Host}'s password: ", true, fct);
                        if (pw is not null)
                            resolvedMethods.Add(new PasswordAuthenticationMethod(resolvedUser, pw));
                    }
                    break;

                case SshAuthMode.Stored:
                default:
                    // Stored: use the saved password; if missing,
                    // prompt via terminal. Always register both
                    // Password and KeyboardInteractive so SSH.NET
                    // can satisfy whichever method the server lists.
                    if (string.IsNullOrEmpty(resolvedPassword) && onPrompt is not null)
                        resolvedPassword = await onPrompt(
                            $"{resolvedUser}@{profile.Host}'s password: ", true, fct);
                    resolvedMethods.Add(new PasswordAuthenticationMethod(
                        resolvedUser, resolvedPassword ?? string.Empty));
                    if (onPrompt is not null)
                        resolvedMethods.Add(BuildInteractive(resolvedUser, onPrompt));
                    break;
            }

            if (resolvedMethods.Count == 0)
                throw new InvalidOperationException(
                    $"SSH auth mode {profile.SshAuthMode} produced no usable methods.");

            var info = new ConnectionInfo(profile.Host, profile.Port, resolvedUser, resolvedMethods.ToArray())
                       { Timeout = TimeSpan.FromSeconds(20) };
            return new SshClient(info);
        }

        return new SshSession(profile.Id, username ?? string.Empty, Factory, onPrompt);
    }

    private static KeyboardInteractiveAuthenticationMethod BuildInteractive(
        string username, SshKeyboardPromptHandler onPrompt)
    {
        var method = new KeyboardInteractiveAuthenticationMethod(username);
        method.AuthenticationPrompt += (sender, e) =>
        {
            // SSH.NET fires AuthenticationPrompt synchronously on the
            // connect thread. Each AuthenticationPromptEventArgs can
            // carry multiple prompts in one round (e.g. PAM bundles
            // user+password). We pump them through onPrompt sequentially.
            // Returning null from the handler leaves Response empty,
            // which SSH.NET interprets as auth failure for that round.
            foreach (var prompt in e.Prompts)
            {
                // .Wait() because AuthenticationPrompt is sync from
                // SSH.NET's perspective. The handler is expected to
                // marshal back to the UI thread itself.
                var task = onPrompt(prompt.Request, !prompt.IsEchoed, CancellationToken.None);
                prompt.Response = task.GetAwaiter().GetResult() ?? string.Empty;
            }
        };
        return method;
    }
}
