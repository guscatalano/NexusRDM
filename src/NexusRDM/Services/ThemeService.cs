using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NexusRDM.Services;

/// <summary>
/// One Nexus color palette. The keys correspond to the <c>NxBg0..3</c>,
/// <c>NxTx1..3</c>, <c>NxAccent</c>, <c>NxSsh/NxRdp</c>, etc. tokens that
/// every view binds to. <see cref="ThemeService.Apply"/> walks these
/// values into the app's existing <c>SolidColorBrush</c> resources.
/// </summary>
public sealed record NxTheme(
    string Id,
    string DisplayName,
    bool   IsLight,
    Color  Bg0, Color Bg1, Color Bg2, Color Bg3,
    Color  Brd,
    Color  Tx1, Color Tx2, Color Tx3,
    Color  Accent, Color Accent2,
    Color  Ssh, Color Rdp,
    Color  Red, Color Yellow);

/// <summary>Applies an <see cref="NxTheme"/> to the app at runtime by
/// mutating each <c>SolidColorBrush.Color</c> in
/// <see cref="Application.Current.Resources"/>. The brushes are shared
/// instances referenced via <c>{StaticResource}</c>, so changing the
/// Color DP propagates to every consumer immediately.</summary>
public static class ThemeService
{
    public static IReadOnlyList<NxTheme> All { get; } = new[]
    {
        // Original — the prototype-v1 palette.
        new NxTheme(
            Id: "dark",  DisplayName: "Dark (default)", IsLight: false,
            Bg0: H("#1A1A1F"), Bg1: H("#22222A"), Bg2: H("#2A2A38"), Bg3: H("#2E2E3E"),
            Brd: A(0x30, 0xFF, 0xFF, 0xFF),
            Tx1: H("#E8E8F0"), Tx2: H("#808090"), Tx3: H("#404050"),
            Accent: H("#7C6EF7"), Accent2: H("#A599FF"),
            Ssh: H("#3DD68C"),    Rdp:     H("#4DA6FF"),
            Red: H("#FF6B6B"),    Yellow:  H("#F0A732")),

        // Re-tuned light theme. Not a literal inverse — the dark theme's
        // mid-greys would smear together on white, so the surface ramp
        // is stretched and the accent darkened so violet still reads as
        // "active" against a paper background.
        new NxTheme(
            Id: "light", DisplayName: "Light", IsLight: true,
            Bg0: H("#FAFAFC"), Bg1: H("#FFFFFF"), Bg2: H("#F0F0F4"), Bg3: H("#E4E4EC"),
            Brd: A(0x28, 0x00, 0x00, 0x00),
            Tx1: H("#1B1B22"), Tx2: H("#55556B"), Tx3: H("#9090A0"),
            Accent: H("#5448D6"), Accent2: H("#7164EB"),
            Ssh: H("#1F9755"),    Rdp:     H("#1B6FCB"),
            Red: H("#C82F2F"),    Yellow:  H("#A86E08")),

        new NxTheme(
            Id: "solarized-dark", DisplayName: "Solarized Dark", IsLight: false,
            Bg0: H("#002B36"), Bg1: H("#073642"), Bg2: H("#0E4753"), Bg3: H("#155563"),
            Brd: A(0x30, 0xFF, 0xFF, 0xFF),
            Tx1: H("#FDF6E3"), Tx2: H("#93A1A1"), Tx3: H("#586E75"),
            Accent: H("#268BD2"), Accent2: H("#6FB8E6"),
            Ssh: H("#859900"),    Rdp:     H("#268BD2"),
            Red: H("#DC322F"),    Yellow:  H("#B58900")),

        new NxTheme(
            Id: "solarized-light", DisplayName: "Solarized Light", IsLight: true,
            Bg0: H("#FDF6E3"), Bg1: H("#EEE8D5"), Bg2: H("#E0DAC4"), Bg3: H("#D6CFB8"),
            Brd: A(0x40, 0x07, 0x36, 0x42),
            Tx1: H("#073642"), Tx2: H("#586E75"), Tx3: H("#93A1A1"),
            Accent: H("#268BD2"), Accent2: H("#6FB8E6"),
            Ssh: H("#859900"),    Rdp:     H("#268BD2"),
            Red: H("#DC322F"),    Yellow:  H("#B58900")),

        new NxTheme(
            Id: "nord", DisplayName: "Nord", IsLight: false,
            Bg0: H("#2E3440"), Bg1: H("#3B4252"), Bg2: H("#434C5E"), Bg3: H("#4C566A"),
            Brd: A(0x30, 0xFF, 0xFF, 0xFF),
            Tx1: H("#ECEFF4"), Tx2: H("#D8DEE9"), Tx3: H("#7B8794"),
            Accent: H("#88C0D0"), Accent2: H("#8FBCBB"),
            Ssh: H("#A3BE8C"),    Rdp:     H("#5E81AC"),
            Red: H("#BF616A"),    Yellow:  H("#EBCB8B")),

        new NxTheme(
            Id: "dracula", DisplayName: "Dracula", IsLight: false,
            Bg0: H("#282A36"), Bg1: H("#343746"), Bg2: H("#44475A"), Bg3: H("#525569"),
            Brd: A(0x30, 0xFF, 0xFF, 0xFF),
            Tx1: H("#F8F8F2"), Tx2: H("#BFBFBF"), Tx3: H("#6272A4"),
            Accent: H("#BD93F9"), Accent2: H("#D6B5FF"),
            Ssh: H("#50FA7B"),    Rdp:     H("#8BE9FD"),
            Red: H("#FF5555"),    Yellow:  H("#F1FA8C")),

        // The "Custom" entry is a placeholder for the dropdown. Its
        // colors aren't applied directly — when the user selects this
        // theme, SettingsViewModel rebuilds a fresh NxTheme from the
        // user-edited swatches and feeds that to ThemeService.Apply.
        new NxTheme(
            Id: "custom",  DisplayName: "Custom", IsLight: false,
            Bg0: H("#1A1A1F"), Bg1: H("#22222A"), Bg2: H("#2A2A38"), Bg3: H("#2E2E3E"),
            Brd: A(0x30, 0xFF, 0xFF, 0xFF),
            Tx1: H("#E8E8F0"), Tx2: H("#808090"), Tx3: H("#404050"),
            Accent: H("#7C6EF7"), Accent2: H("#A599FF"),
            Ssh: H("#3DD68C"),    Rdp:     H("#4DA6FF"),
            Red: H("#FF6B6B"),    Yellow:  H("#F0A732")),

        new NxTheme(
            Id: "monokai", DisplayName: "Monokai", IsLight: false,
            Bg0: H("#272822"), Bg1: H("#3B3A32"), Bg2: H("#49483E"), Bg3: H("#5A584D"),
            Brd: A(0x30, 0xFF, 0xFF, 0xFF),
            Tx1: H("#F8F8F2"), Tx2: H("#A59F85"), Tx3: H("#75715E"),
            Accent: H("#A6E22E"), Accent2: H("#C5F06A"),
            Ssh: H("#A6E22E"),    Rdp:     H("#66D9EF"),
            Red: H("#F92672"),    Yellow:  H("#FD971F")),
    };

    public static NxTheme Default => ById("dracula");

    public static NxTheme ById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Default;
        foreach (var t in All)
            if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
        return Default;
    }

    /// <summary>Mutates the shared <c>SolidColorBrush</c> resources in
    /// place so every existing binding repaints. Also flips
    /// <c>FrameworkElement.RequestedTheme</c> on every open window so
    /// WinUI's own controls (TextBox glyphs, etc.) follow.</summary>
    public static void Apply(NxTheme t)
    {
        if (Application.Current?.Resources is not { } r) return;

        Set(r, "NxBg0", t.Bg0);
        Set(r, "NxBg1", t.Bg1);
        Set(r, "NxBg2", t.Bg2);
        Set(r, "NxBg3", t.Bg3);
        Set(r, "NxBrd", t.Brd);
        Set(r, "NxTx1", t.Tx1);
        Set(r, "NxTx2", t.Tx2);
        Set(r, "NxTx3", t.Tx3);
        Set(r, "NxAccent",  t.Accent);
        Set(r, "NxAccent2", t.Accent2);
        Set(r, "NxSsh",     t.Ssh);
        Set(r, "NxRdp",     t.Rdp);
        Set(r, "NxRed",     t.Red);
        Set(r, "NxYellow",  t.Yellow);

        // Surface- and control-level overrides (the raw resource keys
        // App.xaml seeds for ContentDialog, TextBox, ComboBox, Button,
        // CheckBox, etc.). Refreshing them keeps the WinUI defaults in
        // step with the active palette.
        Set(r, "NavigationViewContentBackground",         t.Bg0);
        Set(r, "TabViewBackground",                       t.Bg1);
        Set(r, "TabViewItemHeaderBackground",             t.Bg1);
        Set(r, "TabViewItemHeaderBackgroundSelected",     t.Bg0);
        Set(r, "TabViewItemHeaderForeground",             t.Tx2);
        Set(r, "TabViewItemHeaderForegroundSelected",     t.Tx1);

        Set(r, "ContentDialogBackground",                 t.Bg1);
        Set(r, "ContentDialogForeground",                 t.Tx1);
        Set(r, "ContentDialogBorderBrush",                t.Brd);
        Set(r, "ContentDialogTopOverlay",                 t.Bg1);
        Set(r, "ContentDialogSeparatorBorderBrush",       t.Brd);

        Set(r, "TextControlBackground",                   t.Bg2);
        Set(r, "TextControlBackgroundPointerOver",        t.Bg3);
        Set(r, "TextControlBackgroundFocused",            t.Bg2);
        Set(r, "TextControlBackgroundDisabled",           t.Bg1);
        Set(r, "TextControlForeground",                   t.Tx1);
        Set(r, "TextControlForegroundPointerOver",        t.Tx1);
        Set(r, "TextControlForegroundFocused",            t.Tx1);
        Set(r, "TextControlForegroundDisabled",           t.Tx3);
        Set(r, "TextControlBorderBrush",                  t.Brd);
        Set(r, "TextControlBorderBrushPointerOver",       t.Accent);
        Set(r, "TextControlBorderBrushFocused",           t.Accent);
        Set(r, "TextControlPlaceholderForeground",        t.Tx3);
        Set(r, "TextControlPlaceholderForegroundPointerOver", t.Tx2);
        Set(r, "TextControlPlaceholderForegroundFocused", t.Tx2);
        Set(r, "TextControlHeaderForeground",             t.Tx2);
        Set(r, "TextControlButtonForeground",             t.Tx2);
        Set(r, "TextControlButtonForegroundPointerOver",  t.Tx1);

        Set(r, "ComboBoxBackground",                      t.Bg2);
        Set(r, "ComboBoxBackgroundPointerOver",           t.Bg3);
        Set(r, "ComboBoxBackgroundPressed",               t.Bg3);
        Set(r, "ComboBoxBackgroundFocused",               t.Bg2);
        Set(r, "ComboBoxBackgroundDisabled",              t.Bg1);
        Set(r, "ComboBoxForeground",                      t.Tx1);
        Set(r, "ComboBoxForegroundPointerOver",           t.Tx1);
        Set(r, "ComboBoxForegroundPressed",               t.Tx1);
        Set(r, "ComboBoxForegroundFocused",               t.Tx1);
        Set(r, "ComboBoxBorderBrush",                     t.Brd);
        Set(r, "ComboBoxBorderBrushPointerOver",          t.Accent);
        Set(r, "ComboBoxBorderBrushFocused",              t.Accent);
        Set(r, "ComboBoxDropDownBackground",              t.Bg2);
        Set(r, "ComboBoxDropDownBackgroundPointerOver",   t.Bg2);
        Set(r, "ComboBoxDropDownBorderBrush",             t.Brd);
        Set(r, "ComboBoxItemBackgroundPointerOver",       t.Bg3);
        Set(r, "ComboBoxItemBackgroundSelected",          t.Bg3);
        Set(r, "ComboBoxItemBackgroundSelectedPointerOver", t.Bg3);
        Set(r, "ComboBoxItemForeground",                  t.Tx1);
        Set(r, "ComboBoxItemForegroundSelected",          t.Tx1);
        Set(r, "ComboBoxPlaceHolderForeground",           t.Tx3);

        Set(r, "ButtonBackground",                        t.Bg2);
        Set(r, "ButtonBackgroundPointerOver",             t.Bg3);
        Set(r, "ButtonBackgroundPressed",                 t.Bg3);
        Set(r, "ButtonForeground",                        t.Tx1);
        Set(r, "ButtonForegroundPointerOver",             t.Tx1);
        Set(r, "ButtonForegroundPressed",                 t.Tx1);
        Set(r, "ButtonBorderBrush",                       t.Brd);
        Set(r, "ButtonBorderBrushPointerOver",            t.Brd);

        Set(r, "AccentButtonBackground",                  t.Accent);
        Set(r, "AccentButtonBackgroundPointerOver",       t.Accent2);
        Set(r, "AccentButtonBackgroundPressed",           t.Accent);
        Set(r, "AccentButtonBorderBrush",                 t.Accent);

        Set(r, "CheckBoxForegroundUnchecked",             t.Tx2);
        Set(r, "CheckBoxForegroundUncheckedPointerOver",  t.Tx1);
        Set(r, "CheckBoxForegroundChecked",               t.Tx1);
        Set(r, "CheckBoxCheckBackgroundFillChecked",      t.Accent);
        Set(r, "CheckBoxCheckBackgroundStrokeUnchecked",  t.Brd);
        Set(r, "CheckBoxCheckBackgroundStrokeChecked",    t.Accent);

        Set(r, "InfoBarBorderBrush",                      t.Brd);
        Set(r, "InfoBarTitleForeground",                  t.Tx1);
        Set(r, "InfoBarMessageForeground",                t.Tx1);

        // ElementTheme nudges WinUI's own (untouched) glyph/icon brushes.
        var elemTheme = t.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        if (App.MainWin?.Content is FrameworkElement fe) fe.RequestedTheme = elemTheme;
        foreach (var w in App.SecondaryWindows.ToArray())
            if (w.Content is FrameworkElement fe2) fe2.RequestedTheme = elemTheme;
    }

    private static void Set(ResourceDictionary r, string key, Color c)
    {
        if (r.TryGetValue(key, out var v) && v is SolidColorBrush b)
        {
            b.Color = c;
        }
        else
        {
            // Fallback: install a fresh brush so first-time callers
            // (e.g., a new resource key not seeded in App.xaml) still
            // pick up the right color.
            r[key] = new SolidColorBrush(c);
        }
    }

    private static Color H(string hex)
    {
        // Accept #RRGGBB or #AARRGGBB.
        var s = hex.TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        var argb = Convert.ToUInt32(s, 16);
        return Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >>  8) & 0xFF),
            (byte)( argb        & 0xFF));
    }

    private static Color A(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}
