namespace NexusRDM.Core.Models;

public enum ConnectionProtocol
{
    Rdp,
    Ssh
}

public enum SshAuthMethod
{
    Password,
    PrivateKey,
    KeyboardInteractive
}

public enum RdpColorDepth
{
    Colors8Bit  = 8,
    Colors15Bit = 15,
    Colors16Bit = 16,
    Colors24Bit = 24,
    Colors32Bit = 32
}

public enum RdpAudioMode
{
    PlayOnClient  = 0,
    PlayOnServer  = 1,
    NoPlayback    = 2
}
