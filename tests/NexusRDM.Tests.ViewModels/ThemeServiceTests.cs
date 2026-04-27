using NexusRDM.Services;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Covers the theme catalog plus the by-id resolver. <c>ThemeService.Apply</c>
/// itself isn't unit-tested here — it mutates <c>Application.Current.Resources</c>
/// which requires a WinUI dispatcher / Application instance that xunit
/// doesn't bring up. The smoke-test suite exercises Apply end-to-end.
/// </summary>
public sealed class ThemeServiceTests
{
    [Fact]
    public void All_IncludesAtLeastTheCoreSeven()
    {
        // Catalog can grow; this just guards against accidental shrinking.
        Assert.True(ThemeService.All.Count >= 7);
    }

    [Fact]
    public void Default_IsDraculaPalette()
    {
        // Default flipped from "dark" to "dracula" — most users prefer
        // the warmer palette out of the box.
        Assert.Equal("dracula", ThemeService.Default.Id);
        Assert.False(ThemeService.Default.IsLight);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    [InlineData("solarized-dark")]
    [InlineData("solarized-light")]
    [InlineData("nord")]
    [InlineData("dracula")]
    [InlineData("monokai")]
    public void ById_FindsKnownPalette(string id)
    {
        var t = ThemeService.ById(id);
        Assert.Equal(id, t.Id);
    }

    [Fact]
    public void ById_IsCaseInsensitive()
    {
        Assert.Equal("dracula", ThemeService.ById("DRACULA").Id);
        Assert.Equal("nord",    ThemeService.ById("Nord").Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-theme")]
    public void ById_FallsBackToDefault_OnUnknownInput(string? id)
    {
        Assert.Equal(ThemeService.Default.Id, ThemeService.ById(id).Id);
    }

    [Fact]
    public void LightThemes_AreFlaggedIsLight()
    {
        // The theme flag drives ElementTheme.Light/Dark for WinUI's own
        // glyphs — make sure both light palettes are correctly tagged.
        Assert.True (ThemeService.ById("light").IsLight);
        Assert.True (ThemeService.ById("solarized-light").IsLight);
        Assert.False(ThemeService.ById("solarized-dark").IsLight);
    }

    [Fact]
    public void EveryTheme_HasNonZeroAccent()
    {
        // A zero/transparent accent would render the AccentButton invisible.
        foreach (var t in ThemeService.All)
        {
            Assert.True(t.Accent.A > 0, $"{t.Id} has zero-alpha accent");
        }
    }

    [Fact]
    public void EveryTheme_DistinguishesBackgroundLayers()
    {
        // The four background layers (Bg0..3) must be visually distinct,
        // otherwise borders between panels collapse into a flat surface.
        foreach (var t in ThemeService.All)
        {
            var distinct = new HashSet<uint>
            {
                Pack(t.Bg0), Pack(t.Bg1), Pack(t.Bg2), Pack(t.Bg3),
            };
            Assert.True(distinct.Count >= 3,
                $"{t.Id} has near-identical background layers");
        }

        static uint Pack(Windows.UI.Color c) =>
            (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
    }
}
