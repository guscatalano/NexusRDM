using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NexusRDM.Services;

/// <summary>
/// One row in the Custom-theme editor: human label, the underlying
/// <see cref="NxTheme"/> field key (Bg0/Tx1/Accent/etc.), and the
/// editable <see cref="Color"/>. The <see cref="Brush"/> is exposed
/// for quick swatch binding without spinning up a converter.
/// </summary>
public sealed partial class CustomColorEntry : ObservableObject
{
    public string Name { get; }
    public string Key  { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    private Color _color;

    public SolidColorBrush Brush => new(Color);

    public CustomColorEntry(string name, string key, Color initial)
    {
        Name   = name;
        Key    = key;
        _color = initial;
    }
}
