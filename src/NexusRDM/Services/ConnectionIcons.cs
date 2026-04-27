namespace NexusRDM.Services;

/// <summary>
/// Curated set of Segoe Fluent Icons codepoints offered to the user
/// when picking a per-connection glyph. Stored as a four-character hex
/// string (e.g. <c>"E756"</c>) on <c>ConnectionProfile.IconGlyph</c> and
/// rendered with <see cref="Microsoft.UI.Xaml.Controls.FontIcon"/>.
/// </summary>
public sealed record IconChoice(string Glyph, string Name)
{
    public string Codepoint => Glyph;
}

public static class ConnectionIcons
{
    /// <summary>Hand-picked subset that covers most "what is this host"
    /// classifications: protocol clients, server roles, OS hints, lab
    /// scenarios. Add to taste — the picker just enumerates this list.</summary>
    public static readonly IReadOnlyList<IconChoice> All = new[]
    {
        new IconChoice("", "Remote desktop"),     // Remote
        new IconChoice("", "Command prompt"),     // CommandPrompt
        new IconChoice("", "Server"),             // Server
        new IconChoice("", "Database"),           // SQL / Database
        new IconChoice("", "Network"),            // Diagnostic
        new IconChoice("", "Storage"),            // Storage
        new IconChoice("", "VM / Hyper-V"),       // VirtualMachine
        new IconChoice("", "Cloud"),              // Cloud
        new IconChoice("", "Globe / web"),        // World
        new IconChoice("", "Lightning / lab"),    // LightningBolt
        new IconChoice("", "Settings / config"),  // Setting
        new IconChoice("", "Lock / vault"),       // Lock
        new IconChoice("", "Person / user"),      // Contact
        new IconChoice("", "Document / file"),    // Page
        new IconChoice("", "GPU / display"),      // FullScreen
        new IconChoice("", "Bug / dev"),          // BugReport (close enough)
        new IconChoice("", "Star / favorite"),    // Star
        new IconChoice("", "Tick / production"),  // CheckMark
    };

    /// <summary>Default glyph when a profile has no <c>IconGlyph</c> set —
    /// matches the home-page legend for the protocol.</summary>
    public static string DefaultFor(NexusRDM.Core.Models.ConnectionProtocol p) =>
        p == NexusRDM.Core.Models.ConnectionProtocol.Rdp ? "" : "";
}
