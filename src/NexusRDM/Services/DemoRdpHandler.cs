using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Services;

/// <summary>
/// Decorator over the real <see cref="IRdpHandler"/> that returns a
/// <see cref="DemoRdpSession"/> while
/// <see cref="DemoModeService.IsActive"/> is true. Outside demo mode
/// every call is a pass-through, so production behaviour is
/// untouched.
/// </summary>
internal sealed class DemoRdpHandler : IRdpHandler
{
    private readonly IRdpHandler     _real;
    private readonly DemoModeService _demo;

    public DemoRdpHandler(IRdpHandler real, DemoModeService demo)
    {
        _real = real;
        _demo = demo;
    }

    public IRdpSession CreateSession(ConnectionProfile profile, string username, string password)
    {
        if (_demo.IsActive)
            return new DemoRdpSession(profile);
        return _real.CreateSession(profile, username, password);
    }
}
