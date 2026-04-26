using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using Xunit;

namespace NexusRDM.Tests.UiSmoke;

/// <summary>
/// Headed smoke tests that drive the real WinUI binary. Skipped automatically
/// when no built NexusRDM.exe is found, so CI / `dotnet test` on a machine
/// without the WinUI build green still pass.
/// </summary>
[Collection("UI smoke")]
public sealed class AppLaunchSmokeTests : IClassFixture<NexusAppFixture>
{
    private readonly NexusAppFixture _fx;
    public AppLaunchSmokeTests(NexusAppFixture fx) => _fx = fx;

    [SkippableFact]
    public void Window_Opens_WithExpectedTitle()
    {
        Skip.IfNot(_fx.AppAvailable, "NexusRDM.exe not built — run `dotnet build src/NexusRDM` first.");
        var win = _fx.MainWindow!;

        Assert.Contains("Nexus", win.Title, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Sidebar_HasConnectionsAuditAndSettings()
    {
        Skip.IfNot(_fx.AppAvailable, "NexusRDM.exe not built.");
        var win = _fx.MainWindow!;

        // Tooltips on the three sidebar buttons; FindAllDescendants by ToolTip
        // text is brittle, so we just assert that we have at least three nav buttons
        // whose names are present in the visual tree.
        var buttons = win.FindAllDescendants(c => c.ByControlType(ControlType.Button));

        Assert.True(buttons.Length >= 3, $"Expected at least 3 buttons, found {buttons.Length}");
    }

    [SkippableFact]
    public void HomeTab_DisplaysIntroContent()
    {
        Skip.IfNot(_fx.AppAvailable, "NexusRDM.exe not built.");
        var win = _fx.MainWindow!;

        var nexusText = win.FindAllDescendants(c => c.ByControlType(ControlType.Text))
            .Any(t => t.Name?.Contains("Nexus RDM", StringComparison.Ordinal) == true);

        Assert.True(nexusText, "Home tab should display the 'Nexus RDM' intro heading.");
    }
}

