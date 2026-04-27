namespace NexusRDM.Views;

/// <summary>
/// Common surface for session views (SSH terminal, RDP form host) so
/// global hotkeys can drive full-screen and pop-out without caring which
/// protocol is active.
/// </summary>
public interface ISessionView
{
    void ToggleFullScreen();
    void PopOut();
}
