using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NexusRDM.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TODO: swap visible section based on selected nav item
    }

    /// <summary>Opens an OS file picker filtered to a single extension
    /// and returns the chosen path (or null on cancel). Unpackaged
    /// WinUI 3 requires <c>InitializeWithWindow</c> against the main
    /// HWND or PickSingleFileAsync throws.</summary>
    private async System.Threading.Tasks.Task<string?> PickFileAsync(string ext)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWin));
        picker.FileTypeFilter.Add(ext);
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void BrowseMstscExe_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(".exe");
        if (path is not null) ViewModel.MstscExePath = path;
    }

    private async void BrowseMstscAx_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(".dll");
        if (path is not null) ViewModel.MstscAxPath = path;
    }
}
