using CredentialManagement;
using NexusRDM.Core.Interfaces;

namespace NexusRDM.Core.Services;

/// <summary>
/// Wraps Windows Credential Manager. All NexusRDM credentials are stored
/// under the "NexusRDM/" prefix so they are easy to identify and enumerate.
/// Credentials are NEVER written to the SQLite database.
/// </summary>
public sealed class CredentialVault : ICredentialVault
{
    private const string Prefix = "NexusRDM/";

    public string Save(string key, string username, string password)
    {
        var fullKey = Prefix + key;
        using var cred = new Credential
        {
            Target          = fullKey,
            Username        = username,
            Password        = password,
            Type            = CredentialType.Generic,
            PersistanceType = PersistanceType.LocalComputer
        };
        cred.Save();
        return key;
    }

    public (string Username, string Password)? Load(string key)
    {
        using var cred = new Credential { Target = Prefix + key };
        return cred.Load() ? (cred.Username, cred.Password) : null;
    }

    public void Delete(string key)
    {
        using var cred = new Credential { Target = Prefix + key };
        cred.Delete();
    }

    public IReadOnlyList<string> ListKeys() =>
        CredentialSet.Load()
            .Where(c => c.Target.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(c => c.Target[Prefix.Length..])
            .ToList();
}
