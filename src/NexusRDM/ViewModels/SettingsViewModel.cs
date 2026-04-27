using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using NexusRDM.Core.Models;
using NexusRDM.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NexusRDM.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Catalog of palettes — bound to the ComboBox in SettingsPage.</summary>
    public IReadOnlyList<NxTheme> Themes { get; } = ThemeService.All;

    [ObservableProperty] private NxTheme _selectedTheme = ThemeService.Default;
    partial void OnSelectedThemeChanged(NxTheme value) => ThemeService.Apply(value);
    [ObservableProperty] private string _defaultSshUser  = string.Empty;
    [ObservableProperty] private int    _defaultSshPort  = 22;
    [ObservableProperty] private int    _defaultRdpPort  = 3389;
    [ObservableProperty] private bool   _saveWindowSize  = true;

    /// <summary>0 = Mstsc (separate process), 1 = MstscAx (in-proc ActiveX),
    /// 2 = FreeRDP (not yet implemented). The order matches the ComboBox in
    /// SettingsPage.xaml — index also matches the underlying enum value.</summary>
    [ObservableProperty] private int _rdpModeIndex = (int)RdpLaunchMode.Mstsc;

    /// <summary>Default desktop resolution applied at connect time. Index
    /// matches the SettingsPage ComboBox and the <see cref="RdpDefaultResolution"/>
    /// enum. Defaults to "match current monitor".</summary>
    [ObservableProperty] private int _rdpResolutionIndex = (int)RdpDefaultResolution.MatchMonitor;

    /// <summary>Prompt the user before closing the app, the main window,
    /// or a session tab while at least one session is still live. The
    /// confirmation only fires once per top-level close action — closing
    /// the window suppresses per-tab confirmations during teardown.</summary>
    [ObservableProperty] private bool _confirmCloseActive = true;

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
        if (s.TryGetValue("ThemeId",     out var ti)) SelectedTheme = ThemeService.ById(Convert.ToString(ti));
        if (s.TryGetValue("SshUser",     out var u))  DefaultSshUser = Convert.ToString(u) ?? string.Empty;
        if (s.TryGetValue("SshPort",     out var sp)) DefaultSshPort = Convert.ToInt32(sp);
        if (s.TryGetValue("RdpPort",     out var rp)) DefaultRdpPort = Convert.ToInt32(rp);
        if (s.TryGetValue("SaveWinSize", out var sw)) SaveWindowSize = Convert.ToBoolean(sw);
        if (s.TryGetValue("RdpMode",     out var rm)) RdpModeIndex   = Convert.ToInt32(rm);
        if (s.TryGetValue("RdpRes",      out var rr)) RdpResolutionIndex = Convert.ToInt32(rr);
        if (s.TryGetValue("ConfirmCloseActive", out var cc)) ConfirmCloseActive = Convert.ToBoolean(cc);
    }

    [RelayCommand]
    private void Save()
    {
        SettingsStore.Write(new Dictionary<string, object>
        {
            ["ThemeId"]     = SelectedTheme.Id,
            ["SshUser"]     = DefaultSshUser,
            ["SshPort"]     = DefaultSshPort,
            ["RdpPort"]     = DefaultRdpPort,
            ["SaveWinSize"] = SaveWindowSize,
            ["RdpMode"]     = RdpModeIndex,
            ["RdpRes"]      = RdpResolutionIndex,
            ["ConfirmCloseActive"] = ConfirmCloseActive,
        });

        ThemeService.Apply(SelectedTheme);
    }

    /// <summary>Reads the persisted theme id from disk and applies it.
    /// Called from App startup right after MainWin is created so the
    /// chosen palette is in effect from the first frame instead of only
    /// after the user visits the Settings page.</summary>
    public static void ApplyPersistedTheme()
    {
        var s = SettingsStore.Read();
        s.TryGetValue("ThemeId", out var id);
        ThemeService.Apply(ThemeService.ById(Convert.ToString(id)));
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

    /// <summary>Persisted preference for prompting the user before
    /// closing while a session is live. Defaults to <c>true</c>.</summary>
    public static bool ReadConfirmCloseActive()
    {
        var s = Read();
        if (!s.TryGetValue("ConfirmCloseActive", out var v)) return true;
        try { return Convert.ToBoolean(v); }
        catch { return true; }
    }

    /// <summary>Reads the persisted default-resolution preference, falling
    /// back to "match the current monitor" if missing or invalid.</summary>
    public static RdpDefaultResolution ReadRdpDefaultResolution()
    {
        var s = Read();
        if (!s.TryGetValue("RdpRes", out var v)) return RdpDefaultResolution.MatchMonitor;
        try
        {
            var i = Convert.ToInt32(v);
            return Enum.IsDefined(typeof(RdpDefaultResolution), i)
                ? (RdpDefaultResolution)i
                : RdpDefaultResolution.MatchMonitor;
        }
        catch { return RdpDefaultResolution.MatchMonitor; }
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
