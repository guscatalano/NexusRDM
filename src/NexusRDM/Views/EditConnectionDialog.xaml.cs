using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

public sealed partial class EditConnectionDialog : ContentDialog
{
    public EditConnectionViewModel ViewModel { get; }
    private readonly ICredentialVault _vault;

    public EditConnectionDialog() : this(null) { }

    public EditConnectionDialog(ConnectionProfile? existing)
    {
        _vault    = App.Services.GetRequiredService<ICredentialVault>();
        var svc   = App.Services.GetRequiredService<IConnectionService>();
        ViewModel = new EditConnectionViewModel(svc, existing);

        InitializeComponent();
        XamlRoot = App.MainWin.Content.XamlRoot;
        _ = ViewModel.LoadGroupsAsync();
    }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            bool ok = await ViewModel.TrySaveAsync(_vault);
            if (!ok) args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Helper: show, await, and return the saved profile or null if cancelled.
    /// </summary>
    public static async Task<ConnectionProfile?> ShowForNewAsync() =>
        await ShowCoreAsync(null);

    public static async Task<ConnectionProfile?> ShowForEditAsync(ConnectionProfile existing) =>
        await ShowCoreAsync(existing);

    private static async Task<ConnectionProfile?> ShowCoreAsync(ConnectionProfile? existing)
    {
        var dlg    = new EditConnectionDialog(existing);
        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary ? dlg.ViewModel.SavedProfile : null;
    }
}
