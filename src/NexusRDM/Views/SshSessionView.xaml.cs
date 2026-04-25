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

        HostLabel.Text = $"{vm.Host}";
        HostStatusLabel.Text = vm.DisplayName;

        ViewModel.DataReceived += (_, data) =>
            DispatcherQueue.TryEnqueue(() => Terminal.Feed(data));

        Terminal.UserInput += async (_, data) =>
            await ViewModel.SendInputAsync(data);

        Terminal.SizeChanged += async (_, _) =>
        {
            var (cols, rows) = Terminal.TerminalSize;
            SizeLabel.Text   = $"{cols}×{rows}";
            await ViewModel.ResizeAsync(cols, rows);
        };

        Loaded += async (_, _) => await ViewModel.ConnectAsync();
    }
}
