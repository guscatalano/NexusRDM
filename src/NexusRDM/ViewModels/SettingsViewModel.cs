using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace NexusRDM.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private int    _themeIndex      = 0; // 0=System 1=Light 2=Dark
    [ObservableProperty] private string _defaultSshUser  = string.Empty;
    [ObservableProperty] private int    _defaultSshPort  = 22;
    [ObservableProperty] private int    _defaultRdpPort  = 3389;
    [ObservableProperty] private bool   _saveWindowSize  = true;

    /// <summary>Safe for unpackaged apps — Package.Current throws without identity.</summary>
    public string AppVersion
    {
        get
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "1.0.0-dev"; }
        }
    }

    public SettingsViewModel() => Load();

    private void Load()
    {
        var s = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
        if (s.TryGetValue("ThemeIndex",    out var t))  ThemeIndex     = (int)t;
        if (s.TryGetValue("SshUser",       out var u))  DefaultSshUser = (string)u;
        if (s.TryGetValue("SshPort",       out var sp)) DefaultSshPort = (int)sp;
        if (s.TryGetValue("RdpPort",       out var rp)) DefaultRdpPort = (int)rp;
        if (s.TryGetValue("SaveWinSize",   out var sw)) SaveWindowSize = (bool)sw;
    }

    [RelayCommand]
    private void Save()
    {
        var s = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
        s["ThemeIndex"]  = ThemeIndex;
        s["SshUser"]     = DefaultSshUser;
        s["SshPort"]     = DefaultSshPort;
        s["RdpPort"]     = DefaultRdpPort;
        s["SaveWinSize"] = SaveWindowSize;

        var theme = ThemeIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        if (App.MainWin.Content is FrameworkElement root)
            root.RequestedTheme = theme;
    }
}
