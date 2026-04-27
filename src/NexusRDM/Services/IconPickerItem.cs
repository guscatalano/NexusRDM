using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NexusRDM.Services;

/// <summary>
/// One row in the connection-editor's icon picker. Wraps a glyph + label
/// with an <see cref="IsSelected"/> flag the XAML can light up so the
/// chosen icon is unmissable. Color of the rendered glyph follows the
/// editor's currently-picked icon color (set externally via
/// <see cref="ApplyColor"/>) so users get a live preview while
/// experimenting.
/// </summary>
public sealed partial class IconPickerItem : ObservableObject
{
    public string Glyph { get; }
    public string Name  { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderThickness))]
    [NotifyPropertyChangedFor(nameof(BorderBrush))]
    [NotifyPropertyChangedFor(nameof(Background))]
    private bool _isSelected;

    [ObservableProperty] private Brush _glyphBrush =
        new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xE8, 0xF0));

    public IconPickerItem(string glyph, string name)
    {
        Glyph = glyph;
        Name  = name;
    }

    public Thickness BorderThickness => IsSelected ? new Thickness(2) : new Thickness(1);

    public Brush BorderBrush =>
        Application.Current?.Resources is { } r
            ? (Brush)(IsSelected
                ? r["NxAccent"]
                : r["NxBrd"])
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    public Brush Background =>
        Application.Current?.Resources is { } r
            ? (Brush)(IsSelected
                ? r["NxBg3"]
                : r["NxBg2"])
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    /// <summary>Update the colour the glyph is painted in. Called by
    /// the editor whenever the user changes the icon-colour swatch.</summary>
    public void ApplyColor(Color color) => GlyphBrush = new SolidColorBrush(color);
}
