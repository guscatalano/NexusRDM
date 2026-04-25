using NexusRDM.Core.Interfaces;

namespace NexusRDM.Tests.ViewModels.Fakes;

/// <summary>In-memory ICredentialVault. Records every Save so VM tests can verify what got written.</summary>
public sealed class FakeCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, (string Username, string Password)> _store = new();
    public List<(string Key, string Username, string Password)> SaveLog { get; } = new();

    public string Save(string key, string username, string password)
    {
        SaveLog.Add((key, username, password));
        _store[key] = (username, password);
        return key;
    }

    public (string Username, string Password)? Load(string key) =>
        _store.TryGetValue(key, out var v) ? v : null;

    public void Delete(string key) => _store.Remove(key);

    public IReadOnlyList<string> ListKeys() => _store.Keys.ToList();
}
