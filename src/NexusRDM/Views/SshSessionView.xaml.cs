using Microsoft.UI.Xaml.Controls;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

public sealed partial class SshSessionView : UserControl
{
    public SshSessionViewModel ViewModel { get; }

    public SshSessionView(SshSessionViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        // Wire VT data from VM into the terminal control
        ViewModel.DataReceived += (_, data) =>
            DispatcherQueue.TryEnqueue(() => Terminal.Feed(data));

        // Wire keyboard from terminal control back to the SSH session
        Terminal.UserInput += async (_, data) =>
            await ViewModel.SendInputAsync(data);

        // Wire resize
        Terminal.SizeChanged += async (_, _) =>
        {
            var (cols, rows) = Terminal.TerminalSize;
            SizeLabel.Text   = $"{cols}x{rows}";
            await ViewModel.ResizeAsync(cols, rows);
        };

        // Connect as soon as this view is loaded
        Loaded += async (_, _) => await ViewModel.ConnectAsync();
    }
}
