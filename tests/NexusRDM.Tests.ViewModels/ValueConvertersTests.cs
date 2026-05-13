using NexusRDM.Converters;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Tests the small WinUI IValueConverters that XAML bindings rely on.
/// These are pure functions (in / out) so they unit-test cleanly
/// without needing the dispatcher. Coverage focuses on the recently-
/// added BytesToHumanConverter (drives the SFTP Size column) since
/// it's where most edge cases live; the older converters get a
/// minimal smoke-check.
/// </summary>
public sealed class ValueConvertersTests
{
    // ── BytesToHumanConverter ────────────────────────────────────────

    [Theory]
    [InlineData(0L,              "—")]
    [InlineData(-1L,             "—")]
    [InlineData(-12345L,         "—")]
    [InlineData(1L,              "1 B")]
    [InlineData(1023L,           "1023 B")]
    [InlineData(1024L,           "1.0 KB")]
    [InlineData(1536L,           "1.5 KB")]
    [InlineData(1048575L,        "1024.0 KB")] // just under 1 MB
    [InlineData(1048576L,        "1.0 MB")]
    [InlineData(1572864L,        "1.5 MB")]
    [InlineData(1073741824L,     "1.00 GB")]
    [InlineData(1610612736L,     "1.50 GB")]
    public void BytesToHuman_FormatsLong(long input, string expected)
    {
        var conv = new BytesToHumanConverter();
        Assert.Equal(expected, conv.Convert(input, typeof(string), null!, null!));
    }

    [Theory]
    [InlineData(0,    "—")]
    [InlineData(1024, "1.0 KB")]
    public void BytesToHuman_AcceptsInt(int input, string expected)
    {
        // Local pane occasionally surfaces int (long backed by int
        // for small files). Verify the int → string path matches the
        // long → string path.
        var conv = new BytesToHumanConverter();
        Assert.Equal(expected, conv.Convert(input, typeof(string), null!, null!));
    }

    [Fact]
    public void BytesToHuman_NonNumericInput_TreatedAsZero()
    {
        var conv = new BytesToHumanConverter();
        Assert.Equal("—", conv.Convert(null!,      typeof(string), null!, null!));
        Assert.Equal("—", conv.Convert("hello",    typeof(string), null!, null!));
        Assert.Equal("—", conv.Convert(new object(), typeof(string), null!, null!));
    }

    [Fact]
    public void BytesToHuman_ConvertBack_Throws()
    {
        var conv = new BytesToHumanConverter();
        Assert.Throws<NotImplementedException>(() =>
            conv.ConvertBack("12 MB", typeof(long), null!, null!));
    }

    // ── InvertBoolConverter ──────────────────────────────────────────

    [Theory]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    public void InvertBool_FlipsBothWays(bool input, bool expected)
    {
        var conv = new InvertBoolConverter();
        Assert.Equal(expected, conv.Convert(input,    typeof(bool), null!, null!));
        Assert.Equal(expected, conv.ConvertBack(input, typeof(bool), null!, null!));
    }

    [Fact]
    public void InvertBool_NullInput_TreatedAsFalse_ReturnsTrue()
    {
        var conv = new InvertBoolConverter();
        // The implementation tests `value is true`; null fails that
        // pattern and returns true (the inverse-of-false branch).
        Assert.Equal(true, conv.Convert(null!, typeof(bool), null!, null!));
    }

    // ── NonZeroToBoolConverter ───────────────────────────────────────

    [Theory]
    [InlineData(0,   false)]
    [InlineData(1,   true)]
    [InlineData(42,  true)]
    [InlineData(-1,  false)] // negative counts as "no error"
    public void NonZeroToBool_OnlyPositiveIntsAreTrue(int input, bool expected)
    {
        var conv = new NonZeroToBoolConverter();
        Assert.Equal(expected, conv.Convert(input, typeof(bool), null!, null!));
    }
}
