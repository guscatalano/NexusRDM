using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

public sealed partial class EditConnectionPanel : UserControl
{
    public EditConnectionViewModel ViewModel { get; }

    private readonly ICredentialVault _vault;
    private readonly TaskCompletionSource<ConnectionProfile?> _tcs = new();

    public Task<ConnectionProfile?> Result => _tcs.Task;

    public EditConnectionPanel(ConnectionProfile? existing)
    {
        _vault    = App.Services.GetRequiredService<ICredentialVault>();
        var svc   = App.Services.GetRequiredService<IConnectionService>();
        ViewModel = new EditConnectionViewModel(svc, existing, _vault);

        InitializeComponent();
        _ = ViewModel.LoadGroupsAsync();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.TrySaveAsync(_vault))
            _tcs.TrySetResult(ViewModel.SavedProfile);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) =>
        _tcs.TrySetResult(null);

    // Click outside the panel (on the scrim) → cancel.
    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) =>
        _tcs.TrySetResult(null);

    // Swallow taps inside the panel so they don't bubble to the scrim.
    private void OnPanelTapped(object sender, TappedRoutedEventArgs e) =>
        e.Handled = true;
}
