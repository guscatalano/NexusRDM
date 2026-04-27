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
    [ObservableProperty] private int _rdpModeIndex = (int)RdpLaunchMode.MstscAx;

    /// <summary>Default desktop resolution applied at connect time. Index
    /// matches the SettingsPage ComboBox and the <see cref="RdpDefaultResolution"/>
    /// enum. Defaults to "match current monitor".</summary>
    [ObservableProperty] private int _rdpResolutionIndex = (int)RdpDefaultResolution.MatchMonitor;

    /// <summary>Prompt the user before closing the app, the main window,
    /// or a session tab while at least one session is still live. The
    /// confirmation only fires once per top-level close action — closing
    /// the window suppresses per-tab confirmations during teardown.</summary>
    [ObservableProperty] private bool _confirmCloseActive = true;

    /// <summary>When on, surface developer-facing surfaces that are
    /// usually hidden — currently the "Copy visual tree" sidebar button.
    /// Off by default; flip it on while debugging UI bugs.</summary>
    [ObservableProperty] private bool _debugMode = false;
    partial void OnDebugModeChanged(bool value) => SettingsStore.ApplyDebugMode(value);

    /// <summary>How long audit-log entries are kept before being purged.
    /// Default 7 days; cleanup runs at app startup.</summary>
    [ObservableProperty] private int _auditRetentionDays = 7;

    /// <summary>Single- or double-click in the connections tree to
    /// activate. Default is single-click — fastest path to a session.</summary>
    [ObservableProperty] private int _clickBehaviorIndex = (int)ConnectionClickBehavior.SingleClick;

    /// <summary>Optional path to a custom <c>mstsc.exe</c>. Empty falls
    /// back to the system PATH lookup. Used by the Mstsc launch backend.</summary>
    [ObservableProperty] private string _mstscExePath = string.Empty;
    /// <summary>Optional path to a custom <c>mstscax.dll</c>. Empty uses
    /// the registered COM class. Currently this is only used by the
    /// Settings page validator — actually overriding the COM activation
    /// would require side-loading + manual <c>DllGetClassObject</c>,
    /// which is out of scope today.</summary>
    [ObservableProperty] private string _mstscAxPath  = string.Empty;
    partial void OnMstscAxPathChanged(string value)
    {
        // Reconfigure the SxS override on the fly so a fresh path
        // takes effect on the next session without an app restart.
        try { NexusRDM.RdpAx.MstscAxOverride.Configure(value); }
        catch { /* swallow — failure leaves the system mstscax in play */ }
    }
    [ObservableProperty] private string _mstscExeStatus = string.Empty;
    [ObservableProperty] private string _mstscAxStatus  = string.Empty;

    /// <summary>UI font size: 0=small, 1=medium (default), 2=large.
    /// Applied as a uniform scale on the main window content so we don't
    /// have to rebind every control's FontSize.</summary>
    [ObservableProperty] private int _fontSizeIndex = 1;
    partial void OnFontSizeIndexChanged(int value) => SettingsStore.ApplyFontSize(value);

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
        if (s.TryGetValue("DebugMode",          out var dm)) DebugMode          = Convert.ToBoolean(dm);
        if (s.TryGetValue("AuditRetentionDays", out var ar)) AuditRetentionDays = Math.Max(1, Convert.ToInt32(ar));
        if (s.TryGetValue("ClickBehavior",      out var cb)) ClickBehaviorIndex = Convert.ToInt32(cb);
        if (s.TryGetValue("MstscExePath",       out var mp)) MstscExePath       = Convert.ToString(mp) ?? string.Empty;
        if (s.TryGetValue("MstscAxPath",        out var ap)) MstscAxPath        = Convert.ToString(ap) ?? string.Empty;
        if (s.TryGetValue("FontSize",           out var fs)) FontSizeIndex      = Convert.ToInt32(fs);
    }

    [RelayCommand]
    private void ValidateMstscExe()
    {
        var (ok, msg) = SettingsStore.ValidateMstscExe(MstscExePath);
        MstscExeStatus = (ok ? "✔ " : "✘ ") + msg;
    }

    [RelayCommand]
    private void ValidateMstscAx()
    {
        // Validation now actually instantiates the COM class via
        // DllGetClassObject + IClassFactory::CreateInstance. That's a
        // strong signal the override DLL will work for real sessions —
        // it doesn't just check the file is a PE.
        var (ok, msg) = NexusRDM.RdpAx.MstscAxOverride.ValidateLoadsCom(MstscAxPath);
        MstscAxStatus = (ok ? "✔ " : "✘ ") + msg;
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
            ["DebugMode"]          = DebugMode,
            ["AuditRetentionDays"] = AuditRetentionDays,
            ["ClickBehavior"]      = ClickBehaviorIndex,
            ["MstscExePath"]       = MstscExePath,
            ["MstscAxPath"]        = MstscAxPath,
            ["FontSize"]           = FontSizeIndex,
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
        if (!s.TryGetValue("RdpMode", out var v)) return RdpLaunchMode.MstscAx;
        try
        {
            var i = Convert.ToInt32(v);
            return Enum.IsDefined(typeof(RdpLaunchMode), i)
                ? (RdpLaunchMode)i
                : RdpLaunchMode.MstscAx;
        }
        catch { return RdpLaunchMode.MstscAx; }
    }

    /// <summary>Persisted debug-mode flag — false by default. Read from
    /// MainWindow startup so the "Copy visual tree" button is hidden on
    /// fresh installs and only revealed when the user opts in.</summary>
    public static bool ReadDebugMode()
    {
        var s = Read();
        if (!s.TryGetValue("DebugMode", out var v)) return false;
        try { return Convert.ToBoolean(v); }
        catch { return false; }
    }

    /// <summary>Toggle debug-mode-driven UI on the live main window.
    /// Looks up the named developer affordances by their <c>x:Name</c>
    /// and flips Visibility — kept here so the toggle reacts instantly
    /// instead of waiting for the next launch.</summary>
    public static void ApplyDebugMode(bool on)
    {
        if (App.MainWin?.Content is not Microsoft.UI.Xaml.FrameworkElement root) return;
        if (root.FindName("BtnCopyVisualTree") is Microsoft.UI.Xaml.UIElement btn)
            btn.Visibility = on ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    /// <summary>Persisted preference for prompting the user before
    /// closing while a session is live. Defaults to <c>true</c>.</summary>
    public static int ReadAuditRetentionDays()
    {
        var s = Read();
        if (!s.TryGetValue("AuditRetentionDays", out var v)) return 7;
        try { return Math.Max(1, Convert.ToInt32(v)); }
        catch { return 7; }
    }

    public static ConnectionClickBehavior ReadClickBehavior()
    {
        var s = Read();
        if (!s.TryGetValue("ClickBehavior", out var v)) return ConnectionClickBehavior.SingleClick;
        try
        {
            var i = Convert.ToInt32(v);
            return Enum.IsDefined(typeof(ConnectionClickBehavior), i)
                ? (ConnectionClickBehavior)i : ConnectionClickBehavior.SingleClick;
        }
        catch { return ConnectionClickBehavior.SingleClick; }
    }

    /// <summary>Reads the persisted font-size index (defaults to medium).</summary>
    public static int ReadFontSizeIndex()
    {
        var s = Read();
        if (!s.TryGetValue("FontSize", out var v)) return 1;
        try { return Math.Clamp(Convert.ToInt32(v), 0, 2); }
        catch { return 1; }
    }

    /// <summary>Applies the font-size scale to the main window's
    /// content via a uniform <see cref="Microsoft.UI.Xaml.Media.ScaleTransform"/>.
    /// Cheap and avoids rebinding every <c>FontSize</c> in the
    /// codebase. 0=small (0.85×), 1=medium (1.0×), 2=large (1.15×).</summary>
    public static void ApplyFontSize(int idx)
    {
        if (App.MainWin?.Content is not Microsoft.UI.Xaml.FrameworkElement root) return;
        var scale = idx switch { 0 => 0.85, 2 => 1.15, _ => 1.0 };
        root.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = scale, ScaleY = scale };
    }

    public static string ReadMstscExePath() =>
        Read().TryGetValue("MstscExePath", out var v) ? Convert.ToString(v) ?? string.Empty : string.Empty;

    public static string ReadMstscAxPath() =>
        Read().TryGetValue("MstscAxPath", out var v) ? Convert.ToString(v) ?? string.Empty : string.Empty;

    /// <summary>Lightweight sanity check on a candidate <c>mstsc.exe</c>:
    /// existence + .exe extension + non-trivial size. Doesn't actually
    /// run the binary because mstsc /? pops a window.</summary>
    public static (bool Ok, string Message) ValidateMstscExe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (true, "Using system mstsc.exe (PATH lookup).");
        if (!System.IO.File.Exists(path))    return (false, "File not found.");
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, "Path is not an .exe.");
        try
        {
            var fi = new System.IO.FileInfo(path);
            if (fi.Length < 16 * 1024) return (false, "Suspiciously small for mstsc.exe.");
            return (true, $"Looks valid ({fi.Length:N0} bytes).");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Loads the candidate <c>mstscax.dll</c> with
    /// <c>LoadLibraryEx(LOAD_LIBRARY_AS_DATAFILE)</c> and frees it. A
    /// successful load means the file is at least a structurally valid
    /// PE for the current architecture; it doesn't prove COM
    /// registration but it's a strong "won't load" smoke screen.</summary>
    public static (bool Ok, string Message) ValidateMstscAx(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (true, "Using registered COM class (system mstscax).");
        if (!System.IO.File.Exists(path))    return (false, "File not found.");
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return (false, "Path is not a .dll.");
        var h = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (h == IntPtr.Zero)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return (false, $"LoadLibrary failed (Win32 error 0x{err:X}).");
        }
        FreeLibrary(h);
        return (true, "Loaded as data file successfully.");
    }

    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

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
