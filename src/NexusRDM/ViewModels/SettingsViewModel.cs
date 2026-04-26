using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using NexusRDM.Core.Models;
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

    /// <summary>0 = Mstsc (separate process), 1 = MstscAx (in-proc ActiveX),
    /// 2 = FreeRDP (not yet implemented). The order matches the ComboBox in
    /// SettingsPage.xaml — index also matches the underlying enum value.</summary>
    [ObservableProperty] private int _rdpModeIndex = (int)RdpLaunchMode.Mstsc;

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
        if (s.TryGetValue("RdpMode",     out var rm)) RdpModeIndex   = Convert.ToInt32(rm);
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
            ["RdpMode"]     = RdpModeIndex,
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
public static class SettingsStore
{
    /// <summary>Resolve the persisted RDP launch mode, defaulting to Mstsc.
    /// Called by the RdpHandler dispatcher each time a session is opened so
    /// changes from Settings take effect on the next new tab.</summary>
    public static RdpLaunchMode ReadRdpMode()
    {
        var s = Read();
        if (!s.TryGetValue("RdpMode", out var v)) return RdpLaunchMode.Mstsc;
        try
        {
            var i = Convert.ToInt32(v);
            return Enum.IsDefined(typeof(RdpLaunchMode), i)
                ? (RdpLaunchMode)i
                : RdpLaunchMode.Mstsc;
        }
        catch { return RdpLaunchMode.Mstsc; }
    }

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
            // Deserialize as JsonElement so we can flatten each value to its
            // underlying primitive. Plain Dictionary<string, object> would
            // hand back JsonElement instances — which Convert.ToInt32/ToBoolean
            // can't handle (JsonElement isn't IConvertible), causing every
            // numeric setting to silently fall back to its default.
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream);
            if (raw is null) return new Dictionary<string, object>();
            var dict = new Dictionary<string, object>(raw.Count);
            foreach (var kv in raw) dict[kv.Key] = Unwrap(kv.Value);
            return dict;
        }
        catch { return new Dictionary<string, object>(); }
    }

    private static object Unwrap(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var i) => i,
        JsonValueKind.Number when e.TryGetInt64(out var l) => l,
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonValueKind.Null   => string.Empty,
        _                    => e.ToString(),
    };

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
