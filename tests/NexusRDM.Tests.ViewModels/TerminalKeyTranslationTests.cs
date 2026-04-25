using NexusRDM.Controls;
using Windows.System;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Pure-function tests for TerminalControl.TranslateSpecialKey — the table
/// that turns VirtualKey + modifiers into VT byte sequences. Catches
/// regressions in the mapping that would silently break typing in SSH.
/// </summary>
public sealed class TerminalKeyTranslationTests
{
    [Theory]
    [InlineData(VirtualKey.Enter,    new byte[] { 0x0D })]
    [InlineData(VirtualKey.Back,     new byte[] { 0x7F })]
    [InlineData(VirtualKey.Tab,      new byte[] { 0x09 })]
    [InlineData(VirtualKey.Escape,   new byte[] { 0x1B })]
    public void Translates_BasicControlKeys(VirtualKey key, byte[] expected)
    {
        Assert.Equal(expected, TerminalControl.TranslateSpecialKey(key, ctrl: false, shift: false));
    }

    [Theory]
    [InlineData(VirtualKey.Up,       new byte[] { 0x1B, (byte)'[', (byte)'A' })]
    [InlineData(VirtualKey.Down,     new byte[] { 0x1B, (byte)'[', (byte)'B' })]
    [InlineData(VirtualKey.Right,    new byte[] { 0x1B, (byte)'[', (byte)'C' })]
    [InlineData(VirtualKey.Left,     new byte[] { 0x1B, (byte)'[', (byte)'D' })]
    [InlineData(VirtualKey.Home,     new byte[] { 0x1B, (byte)'[', (byte)'H' })]
    [InlineData(VirtualKey.End,      new byte[] { 0x1B, (byte)'[', (byte)'F' })]
    [InlineData(VirtualKey.Delete,   new byte[] { 0x1B, (byte)'[', (byte)'3', (byte)'~' })]
    [InlineData(VirtualKey.PageUp,   new byte[] { 0x1B, (byte)'[', (byte)'5', (byte)'~' })]
    [InlineData(VirtualKey.PageDown, new byte[] { 0x1B, (byte)'[', (byte)'6', (byte)'~' })]
    public void Translates_ArrowAndNavigationKeys(VirtualKey key, byte[] expected)
    {
        Assert.Equal(expected, TerminalControl.TranslateSpecialKey(key, ctrl: false, shift: false));
    }

    [Theory]
    [InlineData(VirtualKey.A, 0x01)] // Ctrl-A → SOH
    [InlineData(VirtualKey.C, 0x03)] // Ctrl-C → ETX (interrupt)
    [InlineData(VirtualKey.D, 0x04)] // Ctrl-D → EOT (logout)
    [InlineData(VirtualKey.L, 0x0C)] // Ctrl-L → FF  (clear)
    [InlineData(VirtualKey.Z, 0x1A)] // Ctrl-Z → SUB (suspend)
    public void Translates_CtrlLetters_ToControlCodes(VirtualKey key, byte expected)
    {
        var bytes = TerminalControl.TranslateSpecialKey(key, ctrl: true, shift: false);
        Assert.Equal(new[] { expected }, bytes);
    }

    [Fact]
    public void Translates_PrintableLetterWithoutCtrl_ToEmpty()
    {
        // Printable keys are intentionally NOT translated here — they go through
        // CharacterReceived, which gives us the OS-mapped Unicode character.
        Assert.Empty(TerminalControl.TranslateSpecialKey(VirtualKey.A, ctrl: false, shift: false));
    }

    [Fact]
    public void Translates_UnknownKey_ToEmpty()
    {
        Assert.Empty(TerminalControl.TranslateSpecialKey(VirtualKey.F1, ctrl: false, shift: false));
    }
}
