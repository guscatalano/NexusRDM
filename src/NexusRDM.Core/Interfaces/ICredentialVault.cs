namespace NexusRDM.Core.Interfaces;

/// <summary>
/// Thin abstraction over Windows Credential Manager.
/// Credentials are never stored in the database.
/// </summary>
public interface ICredentialVault
{
    /// <summary>Save or update a credential. Returns the key to store in ConnectionProfile.</summary>
    string Save(string key, string username, string password);

    /// <summary>Retrieve a stored credential. Returns null if not found.</summary>
    (string Username, string Password)? Load(string key);

    /// <summary>Remove a credential from the vault.</summary>
    void Delete(string key);

    /// <summary>List all NexusRDM-managed credential keys.</summary>
    IReadOnlyList<string> ListKeys();
}
