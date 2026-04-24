using Microsoft.UI.Xaml.Controls;

namespace NexusRDM.Views;

/// <summary>
/// Simple username/password prompt shown when a connection has no saved credential.
/// </summary>
public sealed partial class CredentialPromptDialog : ContentDialog
{
    public string Username { get; private set; } = string.Empty;
    public string Password { get; private set; } = string.Empty;

    public CredentialPromptDialog()
    {
        Title             = "Enter credentials";
        PrimaryButtonText = "Connect";
        CloseButtonText   = "Cancel";
        DefaultButton     = ContentDialogButton.Primary;

        var stack = new StackPanel { Spacing = 8 };
        var userBox = new TextBox    { Header = "Username", PlaceholderText = "user" };
        var passBox = new PasswordBox { Header = "Password" };
        stack.Children.Add(userBox);
        stack.Children.Add(passBox);
        Content = stack;

        PrimaryButtonClick += (_, _) =>
        {
            Username = userBox.Text;
            Password = passBox.Password;
        };
    }
}
