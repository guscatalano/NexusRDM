using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using Renci.SshNet;

namespace NexusRDM.Core.Protocols;

/// <summary>
/// Factory for <see cref="SftpSession"/>. Mirrors <see cref="SshHandler"/>
/// for credential resolution — uses the same vault-backed
/// <c>storedPassword</c>, <c>keyPassphrase</c>, and terminal-prompt
/// callback so opening SFTP for an already-configured profile
/// requires no additional dialogs.
///
/// Auth construction is duplicated from <see cref="SshHandler"/> rather
/// than extracted into a shared helper because:
///   • the resolver is lazy / async (needs to prompt during auth, with
///     retry on bad password — same control flow but for SFTP),
///   • the two factories have slightly different lifecycle expectations
///     (SftpClient retries are simpler — no shell to re-attach),
///   • a future refactor could pull the method-builder out into a
///     shared class if a third backend ever needs it.
/// </summary>
public sealed class SftpHandler : ISftpHandler
{
    public ISftpSession CreateSessionForProfile(
        ConnectionProfile profile,
        string username,
        string? storedPassword,
        string? keyPassphrase,
        SshKeyboardPromptHandler? onPrompt)
    {
        async Task<SftpClient> Factory(CancellationToken ct)
        {
            var resolvedUser     = username;
            var resolvedPassword = storedPassword;
            var methods          = new List<AuthenticationMethod>();

            switch (profile.SshAuthMode)
            {
                case SshAuthMode.PrivateKey:
                case SshAuthMode.KeyThenPrompt:
                    if (!string.IsNullOrEmpty(profile.SshKeyFilePath) && File.Exists(profile.SshKeyFilePath))
                    {
                        var kf = string.IsNullOrEmpty(keyPassphrase)
                            ? new PrivateKeyFile(profile.SshKeyFilePath!)
                            : new PrivateKeyFile(profile.SshKeyFilePath!, keyPassphrase);
                        methods.Add(new PrivateKeyAuthenticationMethod(resolvedUser, kf));
                    }
                    if (profile.SshAuthMode == SshAuthMode.KeyThenPrompt)
                    {
                        if (string.IsNullOrEmpty(resolvedPassword) && onPrompt is not null)
                            resolvedPassword = await onPrompt(
                                $"{resolvedUser}@{profile.Host} SFTP password: ", true, ct);
                        if (resolvedPassword is not null)
                            methods.Add(new PasswordAuthenticationMethod(resolvedUser, resolvedPassword));
                        if (onPrompt is not null)
                            methods.Add(BuildInteractive(resolvedUser, onPrompt));
                    }
                    break;

                case SshAuthMode.ServerPrompt:
                    if (onPrompt is not null)
                    {
                        methods.Add(BuildInteractive(resolvedUser, onPrompt));
                        var pw = await onPrompt(
                            $"{resolvedUser}@{profile.Host} SFTP password: ", true, ct);
                        if (pw is not null)
                            methods.Add(new PasswordAuthenticationMethod(resolvedUser, pw));
                    }
                    break;

                case SshAuthMode.Stored:
                default:
                    if (string.IsNullOrEmpty(resolvedPassword) && onPrompt is not null)
                        resolvedPassword = await onPrompt(
                            $"{resolvedUser}@{profile.Host} SFTP password: ", true, ct);
                    methods.Add(new PasswordAuthenticationMethod(
                        resolvedUser, resolvedPassword ?? string.Empty));
                    if (onPrompt is not null)
                        methods.Add(BuildInteractive(resolvedUser, onPrompt));
                    break;
            }

            if (methods.Count == 0)
                throw new InvalidOperationException(
                    $"SFTP auth mode {profile.SshAuthMode} produced no usable methods.");

            var info = new ConnectionInfo(profile.Host, profile.Port, resolvedUser, methods.ToArray())
                       { Timeout = TimeSpan.FromSeconds(20) };
            return new SftpClient(info);
        }

        return new SftpSession(profile.Id, username ?? string.Empty, Factory);
    }

    private static KeyboardInteractiveAuthenticationMethod BuildInteractive(
        string username, SshKeyboardPromptHandler onPrompt)
    {
        var method = new KeyboardInteractiveAuthenticationMethod(username);
        method.AuthenticationPrompt += (sender, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                var task = onPrompt(prompt.Request, !prompt.IsEchoed, CancellationToken.None);
                prompt.Response = task.GetAwaiter().GetResult() ?? string.Empty;
            }
        };
        return method;
    }
}
