using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
        var s = SettingsStore.Read();
        if (s.TryGetValue("ThemeIndex",  out var t))  ThemeIndex     = Convert.ToInt32(t);
        if (s.TryGetValue("SshUser",     out var u))  DefaultSshUser = Convert.ToString(u) ?? string.Empty;
        if (s.TryGetValue("SshPort",     out var sp)) DefaultSshPort = Convert.ToInt32(sp);
        if (s.TryGetValue("RdpPort",     out var rp)) DefaultRdpPort = Convert.ToInt32(rp);
        if (s.TryGetValue("SaveWinSize", out var sw)) SaveWindowSize = Convert.ToBoolean(sw);
    }

    [RelayCommand]
    private void Save()
    {
        SettingsStore.Write(new Dictionary<string, object>
        {
            ["ThemeIndex"]  = ThemeIndex,
            ["SshUser"]     = DefaultSshUser,
            ["SshPort"]     = DefaultSshPort,
            ["RdpPort"]     = DefaultRdpPort,
            ["SaveWinSize"] = SaveWindowSize,
        });

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

/// <summary>
/// Reads/writes app settings. Uses Windows.Storage.ApplicationData when the app has package
/// identity; otherwise falls back to %LocalAppData%\NexusRDM\settings.json (unpackaged mode).
/// </summary>
internal static class SettingsStore
{
    private static readonly Lazy<bool> _packaged = new(() =>
    {
        try { _ = Windows.Storage.ApplicationData.Current.LocalSettings.Values; return true; }
        catch { return false; }
    });

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusRDM", "settings.json");

    public static IReadOnlyDictionary<string, object> Read()
    {
        if (_packaged.Value)
        {
            var v = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            var dict = new Dictionary<string, object>(v.Count);
            foreach (var kv in v) dict[kv.Key] = kv.Value;
            return dict;
        }

        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, object>();
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, object>>(stream)
                   ?? new Dictionary<string, object>();
        }
        catch { return new Dictionary<string, object>(); }
    }

    public static void Write(IDictionary<string, object> values)
    {
        if (_packaged.Value)
        {
            var v = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            foreach (var kv in values) v[kv.Key] = kv.Value;
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var stream = File.Create(FilePath);
        JsonSerializer.Serialize(stream, values);
    }
}
