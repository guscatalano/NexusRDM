using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusRDM.ViewModels;
using Windows.Foundation;

namespace NexusRDM.Views;

public sealed partial class SshSessionView : UserControl
{
    public SshSessionViewModel ViewModel { get; }
    private bool _connectStarted;

    public SshSessionView(SshSessionViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        HostLabel.Text       = $"{vm.Host}";
        HostStatusLabel.Text = vm.DisplayName;

        // Make this UserControl the keyboard input target. Listening with
        // handledEventsToo guarantees we still see input even if a child marks
        // the event handled — which is how we keep typing working regardless of
        // which descendant currently holds focus.
        IsTabStop  = true;
        AddHandler(KeyDownEvent,
            new KeyEventHandler(OnAnyKeyDown), handledEventsToo: true);
        AddHandler(CharacterReceivedEvent,
            new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(OnAnyCharacterReceived),
            handledEventsToo: true);

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

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Always reclaim focus when the tab is shown — TabView re-attaches
        // content on activation and focus drifts to the tab strip otherwise.
        Focus(FocusState.Programmatic);

        // Connect exactly once per tab lifetime.
        if (_connectStarted) return;
        _connectStarted = true;

        WriteTrace($"[ Connecting to {ViewModel.Host} ... ]");
        try
        {
            await ViewModel.ConnectAsync();
            WriteTrace(ViewModel.IsConnected
                ? "[ Connected. Awaiting shell prompt... ]"
                : $"[ Connect returned but not connected: {ViewModel.StatusMessage} ]");
        }
        catch (Exception ex)
        {
            WriteTrace($"[ ConnectAsync threw: {ex.GetType().Name}: {ex.Message} ]");
        }
    }

    private async void OnAnyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var bytes = Terminal.TranslateSpecialKeyForView(e.Key);
        if (bytes is { Length: > 0 })
        {
            await ViewModel.SendInputAsync(bytes);
            e.Handled = true;
        }
    }

    private async void OnAnyCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (e.Character < 0x20 || e.Character == 0x7F) return;
        await ViewModel.SendInputAsync(Encoding.UTF8.GetBytes(new[] { e.Character }));
        e.Handled = true;
    }

    /// <summary>Push a synthetic line of text into the terminal renderer for diagnostics.</summary>
    private void WriteTrace(string line) =>
        Terminal.Feed(Encoding.UTF8.GetBytes(line + "\r\n"));
}
