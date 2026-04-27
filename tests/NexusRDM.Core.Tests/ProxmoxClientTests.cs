using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Core.Proxmox;
using Xunit;

namespace NexusRDM.Core.Tests;

/// <summary>
/// Drives <see cref="ProxmoxClient"/> through a fake
/// <see cref="HttpMessageHandler"/> so we can assert the wire-level
/// behaviour (auth header shape, ticket cookie, 401 retry) without an
/// actual Proxmox cluster on the LAN.
/// </summary>
public sealed class ProxmoxClientTests
{
    [Fact]
    public async Task ApiToken_AddsPveApiTokenHeaderOnEveryRequest()
    {
        var src = NewSource(ProxmoxAuthMode.ApiToken, user: "root@pam!nexus");
        var vault = new FakeVault().With(ProxmoxVault.KeyFor(src), "the-secret-uuid");
        var handler = new FakeHandler(_ => Json("""{"data":{"version":"8.2.0","release":"8","repoid":"abc"}}"""));

        using var client = new ProxmoxClient(src, vault, handler);
        var v = await client.GetVersionAsync();

        Assert.Equal("8.2.0", v.Version);
        var auth = handler.Requests[0].Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("PVEAPIToken", auth!.Scheme);
        Assert.Equal("root@pam!nexus=the-secret-uuid", auth.Parameter);
    }

    [Fact]
    public async Task Password_IssuesTicketThenSendsCookieOnSubsequentRequests()
    {
        var src = NewSource(ProxmoxAuthMode.Password, user: "root", realm: "pam");
        var vault = new FakeVault().With(ProxmoxVault.KeyFor(src), "hunter2");
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api2/json/access/ticket")
                return Json("""{"data":{"ticket":"PVE:abc","CSRFPreventionToken":"csrf123","username":"root@pam"}}""");
            return Json("""{"data":[]}""");
        });

        using var client = new ProxmoxClient(src, vault, handler);
        _ = await client.GetClusterResourcesAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api2/json/access/ticket", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/api2/json/cluster/resources", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains(handler.Requests[1].Headers.GetValues("Cookie"),
            v => v.Contains("PVEAuthCookie=PVE:abc"));
    }

    [Fact]
    public async Task Password_OnUnauthorized_ReissuesTicketAndRetriesOnce()
    {
        var src = NewSource(ProxmoxAuthMode.Password, user: "root", realm: "pam");
        var vault = new FakeVault().With(ProxmoxVault.KeyFor(src), "hunter2");

        var ticketCalls = 0;
        var firstResourceCall = true;
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api2/json/access/ticket")
            {
                ticketCalls++;
                return Json("{\"data\":{\"ticket\":\"PVE:t" + ticketCalls + "\",\"CSRFPreventionToken\":\"c\",\"username\":\"root@pam\"}}");
            }
            if (firstResourceCall)
            {
                firstResourceCall = false;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            return Json("""{"data":[]}""");
        });

        using var client = new ProxmoxClient(src, vault, handler);
        var rows = await client.GetClusterResourcesAsync();

        Assert.Empty(rows);
        Assert.Equal(2, ticketCalls);
        Assert.Equal(4, handler.Requests.Count); // ticket, resources(401), ticket, resources(ok)
    }

    [Fact]
    public async Task ParsesMixedQemuAndLxcResources()
    {
        var src = NewSource(ProxmoxAuthMode.ApiToken, user: "root@pam!t");
        var vault = new FakeVault().With(ProxmoxVault.KeyFor(src), "s");
        var handler = new FakeHandler(_ => Json("""
        {"data":[
          {"type":"qemu","vmid":100,"node":"pve1","name":"win-dev","status":"running","tags":"prod;nexus:rdp","id":"qemu/100"},
          {"type":"lxc","vmid":201,"node":"pve2","name":"db","status":"stopped","tags":"","id":"lxc/201"}
        ]}
        """));

        using var client = new ProxmoxClient(src, vault, handler);
        var rows = await client.GetClusterResourcesAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal("qemu",    rows[0].Type);
        Assert.Equal(100,       rows[0].Vmid);
        Assert.Equal("running", rows[0].Status);
        Assert.Contains("nexus:rdp", rows[0].Tags!);
        Assert.Equal("lxc",     rows[1].Type);
        Assert.Equal(201,       rows[1].Vmid);
    }

    [Fact]
    public async Task ErrorBodyIsSurfacedInException()
    {
        var src = NewSource(ProxmoxAuthMode.ApiToken, user: "root@pam!t");
        var vault = new FakeVault().With(ProxmoxVault.KeyFor(src), "s");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("permission denied: 'VM.Audit'"),
        });

        using var client = new ProxmoxClient(src, vault, handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVersionAsync());
        Assert.Contains("403",                              ex.Message);
        Assert.Contains("permission denied: 'VM.Audit'",    ex.Message);
    }

    [Fact]
    public void ApiToken_MissingSecretInVault_Throws()
    {
        var src = NewSource(ProxmoxAuthMode.ApiToken, user: "root@pam!t");
        var vault = new FakeVault(); // intentionally empty
        var handler = new FakeHandler(_ => Json("{}"));
        var ex = Assert.Throws<InvalidOperationException>(() => new ProxmoxClient(src, vault, handler));
        Assert.Contains(ProxmoxVault.KeyFor(src), ex.Message);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static ProxmoxSource NewSource(ProxmoxAuthMode mode, string user, string realm = "pam") => new()
    {
        Id       = Guid.NewGuid(),
        Name     = "test",
        BaseUrl  = "https://pve.test:8006",
        AuthMode = mode,
        AuthUser = user,
        Realm    = realm,
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            // Buffer the request because the client disposes it after Send.
            Requests.Add(CloneRequest(req));
            return Task.FromResult(respond(req));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage src)
        {
            var dst = new HttpRequestMessage(src.Method, src.RequestUri);
            foreach (var h in src.Headers) dst.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return dst;
        }
    }

    private sealed class FakeVault : ICredentialVault
    {
        private readonly Dictionary<string, (string User, string Pass)> _store = new();
        public FakeVault With(string key, string pass)
        {
            _store[key] = ("ignored", pass);
            return this;
        }
        public string Save(string key, string username, string password)
        {
            _store[key] = (username, password);
            return key;
        }
        public (string Username, string Password)? Load(string key) =>
            _store.TryGetValue(key, out var v) ? v : null;
        public void Delete(string key) => _store.Remove(key);
        public IReadOnlyList<string> ListKeys() => _store.Keys.ToList();
    }
}
