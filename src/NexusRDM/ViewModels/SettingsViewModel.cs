using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomThemeSelected))]
    [NotifyPropertyChangedFor(nameof(CustomThemeVisibility))]
    private NxTheme _selectedTheme = ThemeService.Default;
    partial void OnSelectedThemeChanged(NxTheme value) => ApplySelectedTheme();

    /// <summary>True when the user picked the "Custom" palette — drives
    /// the visibility of the per-color editor below the dropdown.</summary>
    public bool IsCustomThemeSelected => SelectedTheme.Id == "custom";
    public Microsoft.UI.Xaml.Visibility CustomThemeVisibility =>
        IsCustomThemeSelected ? Microsoft.UI.Xaml.Visibility.Visible
                              : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>One row per <see cref="NxTheme"/> color slot. Editing a
    /// row's Color persists it (as hex under <c>CustomColor.{Key}</c>)
    /// and, if the active theme is Custom, re-applies the rebuilt
    /// palette so the change is visible immediately.</summary>
    public System.Collections.ObjectModel.ObservableCollection<CustomColorEntry> CustomColors { get; } = new();

    private void InitCustomColors()
    {
        if (CustomColors.Count > 0) return;
        // Seed defaults from the active default palette (Dracula) but
        // let any stored hex override take precedence — the user's last
        // edit sticks.
        var seed = ThemeService.Default;
        Add("Background 0",   "Bg0",     seed.Bg0);
        Add("Background 1",   "Bg1",     seed.Bg1);
        Add("Background 2",   "Bg2",     seed.Bg2);
        Add("Background 3",   "Bg3",     seed.Bg3);
        Add("Border",         "Brd",     seed.Brd);
        Add("Text primary",   "Tx1",     seed.Tx1);
        Add("Text secondary", "Tx2",     seed.Tx2);
        Add("Text tertiary",  "Tx3",     seed.Tx3);
        Add("Accent",         "Accent",  seed.Accent);
        Add("Accent (hover)", "Accent2", seed.Accent2);
        Add("SSH",            "Ssh",     seed.Ssh);
        Add("RDP",            "Rdp",     seed.Rdp);
        Add("Red",            "Red",     seed.Red);
        Add("Yellow",         "Yellow",  seed.Yellow);

        foreach (var e in CustomColors) e.PropertyChanged += OnCustomColorChanged;

        void Add(string label, string key, Windows.UI.Color fallback)
        {
            var initial = SettingsStore.ReadCustomColor(key, fallback);
            CustomColors.Add(new CustomColorEntry(label, key, initial));
        }
    }

    private void OnCustomColorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not CustomColorEntry row || e.PropertyName != nameof(CustomColorEntry.Color)) return;
        SettingsStore.WriteCustomColor(row.Key, row.Color);
        if (IsCustomThemeSelected) ApplySelectedTheme();
    }

    /// <summary>Either applies a built-in theme or rebuilds the Custom
    /// palette on the fly from <see cref="CustomColors"/>.</summary>
    private void ApplySelectedTheme()
    {
        if (SelectedTheme.Id == "custom")
        {
            ThemeService.Apply(BuildCustomThemeFromState());
        }
        else
        {
            ThemeService.Apply(SelectedTheme);
        }
    }

    private NxTheme BuildCustomThemeFromState()
    {
        Windows.UI.Color C(string key) =>
            CustomColors.FirstOrDefault(e => e.Key == key) is { } entry ? entry.Color
                                                                        : ThemeService.Default.Bg0;
        // Heuristic: if Bg0 is bright, treat as a light palette so
        // WinUI's built-in glyphs flip accordingly.
        var bg = C("Bg0");
        var luma = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return new NxTheme(
            Id: "custom", DisplayName: "Custom", IsLight: luma > 0.55,
            Bg0: C("Bg0"), Bg1: C("Bg1"), Bg2: C("Bg2"), Bg3: C("Bg3"),
            Brd: C("Brd"),
            Tx1: C("Tx1"), Tx2: C("Tx2"), Tx3: C("Tx3"),
            Accent: C("Accent"), Accent2: C("Accent2"),
            Ssh: C("Ssh"), Rdp: C("Rdp"),
            Red: C("Red"), Yellow: C("Yellow"));
    }
    // Default ssh-user / ports / save-window-size are read fresh by
    // their consumers (EditConnectionViewModel for ports, MainWindow
    // for window size persistence). No instant-apply hook needed —
    // the OnPropertyChanged auto-persist flushes them on each edit.
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
    /// Default 7 days; cleanup runs at app startup AND immediately when
    /// the user shortens the window (so the new cutoff applies without
    /// a restart).</summary>
    [ObservableProperty] private int _auditRetentionDays = 7;
    partial void OnAuditRetentionDaysChanged(int value)
    {
        try
        {
            // IAuditRepository is scoped (per DbContext) — open a fresh
            // scope, fire-and-forget the purge, dispose the scope.
            using var scope = App.Services?.CreateScope();
            var audit = scope?.ServiceProvider
                .GetService(typeof(NexusRDM.Core.Interfaces.IAuditRepository))
                as NexusRDM.Core.Interfaces.IAuditRepository;
            _ = audit?.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-Math.Max(1, value)));
        }
        catch { /* non-fatal */ }
    }

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

    /// <summary>Periodically ping every saved host and surface the
    /// result as a status icon in the connections tree.</summary>
    [ObservableProperty] private bool _pingEnabled         = false;
    /// <summary>How often to retry each host. Floored at 5s by the
    /// service so a busy network doesn't get hammered.</summary>
    [ObservableProperty] private int  _pingIntervalSeconds = 30;
    /// <summary>Show round-trip latency next to the ping icon.</summary>
    [ObservableProperty] private bool _pingShowLatency     = false;
    partial void OnPingEnabledChanged(bool value)        => SettingsStore.RaisePingSettingsChanged();
    partial void OnPingIntervalSecondsChanged(int value) => SettingsStore.RaisePingSettingsChanged();
    partial void OnPingShowLatencyChanged(bool value)    => SettingsStore.RaisePingSettingsChanged();

    /// <summary>Hotkey strings, e.g. <c>"Ctrl+Tab"</c>. Free-form so the
    /// user can wire any single combo; parsed by <see cref="SettingsStore.ParseHotkey"/>.
    /// MainWindow re-registers accelerators when these change.</summary>
    [ObservableProperty] private string _hotkeyNextTab    = "Ctrl+Tab";
    [ObservableProperty] private string _hotkeyPrevTab    = "Ctrl+Shift+Tab";
    [ObservableProperty] private string _hotkeyFullScreen = "F11";
    [ObservableProperty] private string _hotkeyPopOut     = "Ctrl+Shift+P";
    [ObservableProperty] private bool   _hotkeyNextTabEnabled    = true;
    [ObservableProperty] private bool   _hotkeyPrevTabEnabled    = true;
    [ObservableProperty] private bool   _hotkeyFullScreenEnabled = true;
    [ObservableProperty] private bool   _hotkeyPopOutEnabled     = true;
    partial void OnHotkeyNextTabChanged(string value)         => App.MainWin?.RebuildHotkeys();
    partial void OnHotkeyPrevTabChanged(string value)         => App.MainWin?.RebuildHotkeys();
    partial void OnHotkeyFullScreenChanged(string value)      => App.MainWin?.RebuildHotkeys();
    partial void OnHotkeyPopOutChanged(string value)          => App.MainWin?.RebuildHotkeys();
    partial void OnHotkeyNextTabEnabledChanged(bool value)    => App.MainWin?.RebuildHotkeys();
    partial void OnHotkeyPrevTabEnabledChanged(bool value)    => App.MainWin?.RebuildHotkeys();
    partial void OnHotkeyFullScreenEnabledChanged(bool value) => App.MainWin?.RebuildHotkeys();
    partial void OnHotkeyPopOutEnabledChanged(bool value)     => App.MainWin?.RebuildHotkeys();

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

    /// <summary>True while we're hydrating values from disk in the
    /// constructor. The OnPropertyChanged override skips its
    /// auto-persist while this is set, so initial bind-up doesn't
    /// thrash the store with redundant writes.</summary>
    private bool _loading;

    public SettingsViewModel()
    {
        _loading = true;
        try
        {
            // Custom-color rows are seeded BEFORE Load so the saved theme id
            // can pick "custom" without racing against an empty list.
            InitCustomColors();
            Load();
        }
        finally { _loading = false; }
    }

    /// <summary>Auto-persist every observable change. The Settings page
    /// no longer has a Save button — anything the user touches goes
    /// straight to the store and to whatever services consume it.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        // Derived/computed properties (IsCustomThemeSelected,
        // CustomThemeVisibility) still PropertyChange — re-writing the
        // full dict on those is harmless and cheap.
        try { PersistAll(); }
        catch { /* never let a write failure crash the UI thread */ }
    }

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
        if (s.TryGetValue("HotkeyNextTab",      out var h1)) HotkeyNextTab      = Convert.ToString(h1) ?? HotkeyNextTab;
        if (s.TryGetValue("HotkeyPrevTab",      out var h2)) HotkeyPrevTab      = Convert.ToString(h2) ?? HotkeyPrevTab;
        if (s.TryGetValue("HotkeyFullScreen",   out var h3)) HotkeyFullScreen   = Convert.ToString(h3) ?? HotkeyFullScreen;
        if (s.TryGetValue("HotkeyPopOut",       out var h4)) HotkeyPopOut       = Convert.ToString(h4) ?? HotkeyPopOut;
        if (s.TryGetValue("HotkeyNextTabEnabled",    out var e1)) HotkeyNextTabEnabled    = Convert.ToBoolean(e1);
        if (s.TryGetValue("HotkeyPrevTabEnabled",    out var e2)) HotkeyPrevTabEnabled    = Convert.ToBoolean(e2);
        if (s.TryGetValue("HotkeyFullScreenEnabled", out var e3)) HotkeyFullScreenEnabled = Convert.ToBoolean(e3);
        if (s.TryGetValue("HotkeyPopOutEnabled",     out var e4)) HotkeyPopOutEnabled     = Convert.ToBoolean(e4);
        if (s.TryGetValue("PingEnabled",         out var pe))  PingEnabled         = Convert.ToBoolean(pe);
        if (s.TryGetValue("PingIntervalSeconds", out var pi))  PingIntervalSeconds = Math.Max(5, Convert.ToInt32(pi));
        if (s.TryGetValue("PingShowLatency",     out var pl))  PingShowLatency     = Convert.ToBoolean(pl);
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

    /// <summary>Snapshots every observable into a fresh dictionary and
    /// hands it to <see cref="SettingsStore.Write"/>. Called from
    /// <see cref="OnPropertyChanged"/> on every change — there is no
    /// explicit Save button anymore.</summary>
    private void PersistAll()
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
            ["HotkeyNextTab"]      = HotkeyNextTab,
            ["HotkeyPrevTab"]      = HotkeyPrevTab,
            ["HotkeyFullScreen"]   = HotkeyFullScreen,
            ["HotkeyPopOut"]       = HotkeyPopOut,
            ["HotkeyNextTabEnabled"]    = HotkeyNextTabEnabled,
            ["HotkeyPrevTabEnabled"]    = HotkeyPrevTabEnabled,
            ["HotkeyFullScreenEnabled"] = HotkeyFullScreenEnabled,
            ["HotkeyPopOutEnabled"]     = HotkeyPopOutEnabled,
            ["PingEnabled"]         = PingEnabled,
            ["PingIntervalSeconds"] = PingIntervalSeconds,
            ["PingShowLatency"]     = PingShowLatency,
        });
    }

    /// <summary>Reads the persisted theme id from disk and applies it.
    /// Called from App startup right after MainWin is created so the
    /// chosen palette is in effect from the first frame instead of only
    /// after the user visits the Settings page.</summary>
    public static void ApplyPersistedTheme()
    {
        var s = SettingsStore.Read();
        s.TryGetValue("ThemeId", out var id);
        var idStr = Convert.ToString(id) ?? string.Empty;
        if (string.Equals(idStr, "custom", StringComparison.OrdinalIgnoreCase))
        {
            // Rebuild the Custom palette from the persisted swatches.
            ThemeService.Apply(BuildCustomThemeFromStore());
            return;
        }
        ThemeService.Apply(ThemeService.ById(idStr));
    }

    /// <summary>Static counterpart of <see cref="BuildCustomThemeFromState"/>
    /// that reads the persisted swatches without needing a live
    /// <see cref="SettingsViewModel"/> — used at startup before the page
    /// is created.</summary>
    private static NxTheme BuildCustomThemeFromStore()
    {
        var dark = ThemeService.ById("dark");
        Windows.UI.Color G(string key, Windows.UI.Color fb) => SettingsStore.ReadCustomColor(key, fb);
        var bg0 = G("Bg0", dark.Bg0);
        var luma = (0.299 * bg0.R + 0.587 * bg0.G + 0.114 * bg0.B) / 255.0;
        return new NxTheme(
            Id: "custom", DisplayName: "Custom", IsLight: luma > 0.55,
            Bg0: bg0,                    Bg1: G("Bg1", dark.Bg1),
            Bg2: G("Bg2", dark.Bg2),     Bg3: G("Bg3", dark.Bg3),
            Brd: G("Brd", dark.Brd),
            Tx1: G("Tx1", dark.Tx1),     Tx2: G("Tx2", dark.Tx2),
            Tx3: G("Tx3", dark.Tx3),
            Accent: G("Accent", dark.Accent), Accent2: G("Accent2", dark.Accent2),
            Ssh: G("Ssh", dark.Ssh),     Rdp:    G("Rdp", dark.Rdp),
            Red: G("Red", dark.Red),     Yellow: G("Yellow", dark.Yellow));
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
    /// <summary>Read each hotkey or fall back to its default. Used by
    /// MainWindow when it (re)builds keyboard accelerators.</summary>
    public static string ReadHotkey(string key, string fallback)
    {
        var s = Read();
        return s.TryGetValue(key, out var v) ? Convert.ToString(v) ?? fallback : fallback;
    }

    /// <summary>Reads the per-hotkey enable flag. Defaults to <c>true</c>
    /// so users get the bindings on a fresh install; toggling the
    /// checkbox in Settings flips this off.</summary>
    public static bool ReadHotkeyEnabled(string key)
    {
        var s = Read();
        if (!s.TryGetValue(key, out var v)) return true;
        try { return Convert.ToBoolean(v); }
        catch { return true; }
    }

    /// <summary>Parses <c>"Ctrl+Shift+Tab"</c> style strings into a
    /// (key, modifiers) pair. Modifiers are case-insensitive: Ctrl,
    /// Shift, Alt, Win. Returns null if the string can't be parsed.</summary>
    public static (Windows.System.VirtualKey Key, Windows.System.VirtualKeyModifiers Mods)? ParseHotkey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var mods = Windows.System.VirtualKeyModifiers.None;
        Windows.System.VirtualKey? key = null;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= Windows.System.VirtualKeyModifiers.Control; break;
                case "shift":   mods |= Windows.System.VirtualKeyModifiers.Shift;   break;
                case "alt":
                case "menu":    mods |= Windows.System.VirtualKeyModifiers.Menu;    break;
                case "win":
                case "windows": mods |= Windows.System.VirtualKeyModifiers.Windows; break;
                default:
                    if (Enum.TryParse<Windows.System.VirtualKey>(p, ignoreCase: true, out var k))
                        key = k;
                    break;
            }
        }
        return key is null ? null : (key.Value, mods);
    }

    /// <summary>Notifies listeners (ConnectionsViewModel) that one of
    /// the ping-related settings has changed. Avoids forcing a direct
    /// DI dependency from SettingsViewModel back into the connections
    /// VM; subscribers re-read the values.</summary>
    public static event EventHandler? PingSettingsChanged;
    public static void RaisePingSettingsChanged() =>
        PingSettingsChanged?.Invoke(null, EventArgs.Empty);

    public static bool ReadPingEnabled()
    {
        var s = Read();
        if (!s.TryGetValue("PingEnabled", out var v)) return false;
        try { return Convert.ToBoolean(v); } catch { return false; }
    }
    public static int ReadPingIntervalSeconds()
    {
        var s = Read();
        if (!s.TryGetValue("PingIntervalSeconds", out var v)) return 30;
        try { return Math.Max(5, Convert.ToInt32(v)); } catch { return 30; }
    }
    public static bool ReadPingShowLatency()
    {
        var s = Read();
        if (!s.TryGetValue("PingShowLatency", out var v)) return false;
        try { return Convert.ToBoolean(v); } catch { return false; }
    }

    /// <summary>Reads a custom-theme color from settings, or returns
    /// the supplied fallback when missing/garbage. Stored as
    /// <c>#AARRGGBB</c> hex strings under <c>CustomColor.{key}</c>.</summary>
    public static Windows.UI.Color ReadCustomColor(string key, Windows.UI.Color fallback)
    {
        var s = Read();
        if (!s.TryGetValue($"CustomColor.{key}", out var v)) return fallback;
        var hex = Convert.ToString(v) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            var argb = Convert.ToUInt32(hex, 16);
            return Windows.UI.Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >>  8) & 0xFF),
                (byte)( argb        & 0xFF));
        }
        catch { return fallback; }
    }

    public static void WriteCustomColor(string key, Windows.UI.Color color)
    {
        // Merge: read all, overwrite the one key, write back. The store
        // is small and writes are user-driven (a swatch click), so the
        // round-trip cost is negligible.
        var values = new Dictionary<string, object>(Read());
        values[$"CustomColor.{key}"] = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        Write(values);
    }

    public static int ReadFontSizeIndex()
    {
        var s = Read();
        if (!s.TryGetValue("FontSize", out var v)) return 1;
        try { return Math.Clamp(Convert.ToInt32(v), 0, 2); }
        catch { return 1; }
    }

    /// <summary>Applies the user's font-size choice to every text-bearing
    /// control in the visual tree by multiplying its <c>FontSize</c> by
    /// the picked scale. Originals are stashed in a per-element
    /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/>
    /// on first sight, so subsequent calls re-scale relative to the
    /// XAML-defined size — not the previously-scaled one. Avoids the
    /// ScaleTransform pitfall where the entire UI (including layout
    /// boxes, icons, padding) zooms in lockstep.</summary>
    public static void ApplyFontSize(int idx)
    {
        var scale = idx switch { 0 => 0.85, 2 => 1.15, _ => 1.0 };

        void Apply(Microsoft.UI.Xaml.Window? w)
        {
            if (w?.Content is Microsoft.UI.Xaml.FrameworkElement root)
            {
                // Drop any previous render-transform scale we may have
                // stamped on the window before the proper font-only path
                // existed. Without this, an upgrade keeps the broken zoom.
                root.RenderTransform = null;
                ScaleFontsRecursive(root, scale);
            }
        }

        Apply(App.MainWin);
        foreach (var w in App.SecondaryWindows.ToArray()) Apply(w);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Xaml.FrameworkElement, object> _originalFontSize = new();

    private static void ScaleFontsRecursive(Microsoft.UI.Xaml.FrameworkElement el, double scale)
    {
        // Capture-or-replay the original size, then write back the
        // scaled value. Only TextBlock and Control expose FontSize;
        // everything else just gets descended into.
        double? orig = el switch
        {
            Microsoft.UI.Xaml.Controls.TextBlock tb     => GetOrCapture(tb,  () => tb.FontSize),
            Microsoft.UI.Xaml.Controls.Control   c      => GetOrCapture(c,   () => c.FontSize),
            _ => null,
        };
        if (orig.HasValue)
        {
            switch (el)
            {
                case Microsoft.UI.Xaml.Controls.TextBlock tb: tb.FontSize = orig.Value * scale; break;
                case Microsoft.UI.Xaml.Controls.Control   c:  c.FontSize  = orig.Value * scale; break;
            }
        }

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(el);
        for (int i = 0; i < count; i++)
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(el, i) is Microsoft.UI.Xaml.FrameworkElement child)
                ScaleFontsRecursive(child, scale);
    }

    private static double GetOrCapture(Microsoft.UI.Xaml.FrameworkElement key, Func<double> read)
    {
        if (_originalFontSize.TryGetValue(key, out var v)) return (double)v;
        var current = read();
        _originalFontSize.Add(key, current);
        return current;
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
