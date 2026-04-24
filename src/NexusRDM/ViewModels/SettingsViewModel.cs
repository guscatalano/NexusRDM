using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace NexusRDM.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string ThemeKey = "AppTheme";

    [ObservableProperty] private int    _themeIndex   = 0;  // 0=System, 1=Light, 2=Dark
    [ObservableProperty] private string _defaultSshUser = string.Empty;
    [ObservableProperty] private int    _defaultSshPort  = 22;
    [ObservableProperty] private int    _defaultRdpPort  = 3389;
    [ObservableProperty] private bool   _saveWindowSize  = true;

    public string AppVersion =>
        Windows.ApplicationModel.Package.Current.Id.Version is var v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "dev";

    public SettingsViewModel() => LoadFromLocalSettings();

    private void LoadFromLocalSettings()
    {
        var local = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
        if (local.TryGetValue("ThemeIndex",      out var t)) ThemeIndex      = (int)t;
        if (local.TryGetValue("DefaultSshUser",  out var u)) DefaultSshUser  = (string)u;
        if (local.TryGetValue("DefaultSshPort",  out var sp)) DefaultSshPort = (int)sp;
        if (local.TryGetValue("DefaultRdpPort",  out var rp)) DefaultRdpPort = (int)rp;
        if (local.TryGetValue("SaveWindowSize",  out var sw)) SaveWindowSize  = (bool)sw;
    }

    [RelayCommand]
    private void Save()
    {
        var local = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
        local["ThemeIndex"]     = ThemeIndex;
        local["DefaultSshUser"] = DefaultSshUser;
        local["DefaultSshPort"] = DefaultSshPort;
        local["DefaultRdpPort"] = DefaultRdpPort;
        local["SaveWindowSize"] = SaveWindowSize;

        // Apply theme immediately
        var requested = ThemeIndex switch
        {
            1 => Microsoft.UI.Xaml.ElementTheme.Light,
            2 => Microsoft.UI.Xaml.ElementTheme.Dark,
            _ => Microsoft.UI.Xaml.ElementTheme.Default
        };
        if (App.MainWin.Content is Microsoft.UI.Xaml.FrameworkElement root)
            root.RequestedTheme = requested;
    }
}
