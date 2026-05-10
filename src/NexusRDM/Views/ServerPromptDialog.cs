using Microsoft.UI.Xaml.Controls;

namespace NexusRDM.Views;

/// <summary>
/// Renders a single keyboard-interactive prompt from the SSH server.
/// Shown by <c>OpenSshTabAsync</c> for connections in
/// <c>ServerPrompt</c> / <c>KeyThenPrompt</c> auth modes — one dialog
/// per round of prompts. Server-driven so the title carries the host
/// name and the body is the literal challenge string the server sent
/// (e.g. "Password:", "Verification code:", "Token from your phone:").
///
/// <paramref name="masked"/> mirrors SSH.NET's <c>!IsEchoed</c> hint:
/// true → render via <see cref="PasswordBox"/> (the typical
/// password / OTP case), false → plain <see cref="TextBox"/> for
/// echoable challenges (rare, but PAM modules do use them).
/// </summary>
public sealed partial class ServerPromptDialog : ContentDialog
{
    public string Response { get; private set; } = string.Empty;

    public ServerPromptDialog(string host, string promptText, bool masked)
    {
        Title             = $"{host} — server prompt";
        PrimaryButtonText = "Send";
        CloseButtonText   = "Cancel";
        DefaultButton     = ContentDialogButton.Primary;

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(promptText) ? "Server requested input." : promptText,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            MaxWidth = 360,
        });

        if (masked)
        {
            var pw = new PasswordBox();
            stack.Children.Add(pw);
            PrimaryButtonClick += (_, _) => Response = pw.Password;
        }
        else
        {
            var tb = new TextBox();
            stack.Children.Add(tb);
            PrimaryButtonClick += (_, _) => Response = tb.Text;
        }

        Content = stack;
    }
}
