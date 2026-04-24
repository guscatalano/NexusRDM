using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NexusRDM.Core.Models;
using NexusRDM.Core.Services;
using NexusRDM.Data.Context;
using NexusRDM.Data.Repositories;
using Xunit;

namespace NexusRDM.Core.Tests;

/// <summary>
/// Integration tests for ConnectionService using an in-memory SQLite database.
/// A single SqliteConnection is kept open for the lifetime of each test instance
/// so that the in-memory schema persists across EF Core operations.
/// </summary>
public sealed class ConnectionServiceTests : IDisposable
{
    private readonly SqliteConnection      _conn;
    private readonly NexusDbContext        _db;
    private readonly ConnectionRepository  _repo;
    private readonly AuditRepository       _audit;
    private readonly ConnectionService     _svc;

    public ConnectionServiceTests()
    {
        // Open once and keep alive — in-memory SQLite is per-connection
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var opts = new DbContextOptionsBuilder<NexusDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db    = new NexusDbContext(opts);
        _db.Database.EnsureCreated();   // creates tables on the open connection
        _repo  = new ConnectionRepository(_db);
        _audit = new AuditRepository(_db);
        _svc   = new ConnectionService(_repo, _audit, NullLogger<ConnectionService>.Instance);
    }

    [Fact]
    public async Task CreateAsync_PersistsProfile()
    {
        var created = await _svc.CreateAsync(MakeSsh("web-01", "192.168.1.10"));
        Assert.NotEqual(Guid.Empty, created.Id);
        var loaded = await _svc.GetByIdAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal("web-01", loaded.DisplayName);
    }

    [Fact]
    public async Task CreateAsync_WritesAuditEntry()
    {
        var created = await _svc.CreateAsync(MakeSsh("db-01", "10.0.0.5"));
        var audit   = await _svc.GetRecentAuditAsync(10);
        Assert.Contains(audit, e => e.ConnectionId == created.Id && e.Action == AuditAction.Created);
    }

    [Fact]
    public async Task UpdateAsync_ChangesDisplayName()
    {
        var created = await _svc.CreateAsync(MakeSsh("old-name", "1.2.3.4"));
        created.DisplayName = "new-name";
        await _svc.UpdateAsync(created);
        var loaded = await _svc.GetByIdAsync(created.Id);
        Assert.Equal("new-name", loaded!.DisplayName);
    }

    [Fact]
    public async Task DeleteAsync_RemovesProfile()
    {
        var created = await _svc.CreateAsync(MakeSsh("to-delete", "1.2.3.4"));
        await _svc.DeleteAsync(created.Id);
        Assert.Null(await _svc.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task DeleteAsync_WritesAuditEntry()
    {
        var created = await _svc.CreateAsync(MakeSsh("audit-test", "1.2.3.4"));
        await _svc.DeleteAsync(created.Id);
        var audit = await _svc.GetRecentAuditAsync(20);
        Assert.Contains(audit, e => e.ConnectionId == created.Id && e.Action == AuditAction.Deleted);
    }

    [Fact]
    public async Task SearchAsync_FindsByDisplayName()
    {
        await _svc.CreateAsync(MakeSsh("prod-web", "10.0.0.1"));
        await _svc.CreateAsync(MakeSsh("dev-web",  "10.0.0.2"));
        await _svc.CreateAsync(MakeSsh("prod-db",  "10.0.0.3"));
        var results = await _svc.SearchAsync("prod");
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains("prod", r.DisplayName));
    }

    [Fact]
    public async Task SearchAsync_FindsByHost()
    {
        await _svc.CreateAsync(MakeSsh("alpha", "10.10.0.1"));
        await _svc.CreateAsync(MakeSsh("beta",  "192.168.0.1"));
        var results = await _svc.SearchAsync("10.10");
        Assert.Single(results);
        Assert.Equal("alpha", results[0].DisplayName);
    }

    [Fact]
    public async Task CreateGroupAsync_PersistsGroup()
    {
        var created = await _svc.CreateGroupAsync(new Group { Name = "Production", SortOrder = 0 });
        var groups  = await _svc.GetGroupsAsync();
        Assert.Contains(groups, g => g.Id == created.Id && g.Name == "Production");
    }

    [Fact]
    public async Task DeleteGroupAsync_NullifiesConnectionGroupId()
    {
        var group   = await _svc.CreateGroupAsync(new Group { Name = "Temp" });
        var profile = MakeSsh("server", "1.2.3.4");
        profile.GroupId = group.Id;
        var created = await _svc.CreateAsync(profile);
        await _svc.DeleteGroupAsync(group.Id);
        var loaded = await _svc.GetByIdAsync(created.Id);
        Assert.Null(loaded!.GroupId);
    }

    private static ConnectionProfile MakeSsh(string name, string host) => new()
    {
        DisplayName = name, Host = host, Port = 22, Protocol = ConnectionProtocol.Ssh
    };

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
