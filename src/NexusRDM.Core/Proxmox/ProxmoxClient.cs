using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Core.Proxmox;

/// <summary>
/// HttpClient-backed implementation of <see cref="IProxmoxClient"/>.
///
/// Auth strategies:
///   - <see cref="ProxmoxAuthMode.ApiToken"/>: static
///     <c>Authorization: PVEAPIToken=USER@REALM!TOKENID=SECRET</c>
///     header on every request. Preferred — no expiry, revocable from
///     the Proxmox UI without disturbing the underlying account.
///   - <see cref="ProxmoxAuthMode.Password"/>: <c>POST /access/ticket</c>
///     once to get a session ticket + CSRF token, then send the ticket
///     as a cookie on subsequent reads. Refreshes lazily on 401.
///
/// Each instance owns one <see cref="HttpClient"/> + handler pair so
/// the per-source <c>IgnoreTlsErrors</c> setting is respected without
/// tainting a shared handler. We capture the server cert thumbprint
/// during validation so the caller can pin/diff it across syncs.
/// </summary>
internal sealed class ProxmoxClient : IProxmoxClient, IDisposable
{
    private readonly ProxmoxSource    _source;
    private readonly ICredentialVault _vault;
    private readonly HttpClient       _http;
    private readonly HttpClientHandler _handler;

    private string? _ticket;
    private string? _csrf;

    public string? LastSeenCertThumbprint { get; private set; }

    public ProxmoxClient(ProxmoxSource source, ICredentialVault vault)
        : this(source, vault, handler: null) { }

    /// <summary>Test seam: lets unit tests inject a fake
    /// <see cref="HttpMessageHandler"/> so we don't open real sockets.
    /// Production callers go through the parameterless overload, which
    /// builds a real <see cref="HttpClientHandler"/> with TLS-pinning
    /// hooks below.</summary>
    internal ProxmoxClient(ProxmoxSource source, ICredentialVault vault, HttpMessageHandler? handler)
    {
        _source = source;
        _vault  = vault;

        if (handler is HttpClientHandler hch)
        {
            _handler = hch;
            ConfigureCertCallback(_handler);
        }
        else if (handler is not null)
        {
            // Test handler (e.g. DelegatingHandler stub). Wrap so we
            // have something to dispose; cert pinning is a no-op since
            // there's no TLS.
            _handler = new HttpClientHandler();
        }
        else
        {
            _handler = new HttpClientHandler();
            ConfigureCertCallback(_handler);
        }

        _http = new HttpClient(handler ?? _handler)
        {
            BaseAddress = new Uri(NormalizeBaseUrl(source.BaseUrl)),
            Timeout     = TimeSpan.FromSeconds(20),
        };

        if (source.AuthMode == ProxmoxAuthMode.ApiToken)
        {
            // PVEAPIToken header is set once and lives for the client's
            // lifetime. Format: USER@REALM!TOKENID=SECRET
            var secret = vault.Load(ProxmoxVault.KeyFor(source))?.Password
                ?? throw new InvalidOperationException(
                    $"Proxmox source '{source.Name}' has no token secret in the vault " +
                    $"(expected key '{ProxmoxVault.KeyFor(source)}').");
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("PVEAPIToken", $"{source.AuthUser}={secret}");
        }
    }

    private void ConfigureCertCallback(HttpClientHandler h)
    {
        h.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
        {
            if (cert is not null)
            {
                using var sha = SHA256.Create();
                LastSeenCertThumbprint = Convert.ToHexString(sha.ComputeHash(cert.RawData));
            }
            return errors == System.Net.Security.SslPolicyErrors.None || _source.IgnoreTlsErrors;
        };
    }

    public async Task<ProxmoxVersion> GetVersionAsync(CancellationToken ct = default)
    {
        var env = await SendAsync<ProxmoxEnvelope<ProxmoxVersion>>(HttpMethod.Get, "/api2/json/version", ct).ConfigureAwait(false);
        return env.Data ?? new ProxmoxVersion();
    }

    public async Task<IReadOnlyList<ProxmoxClusterResource>> GetClusterResourcesAsync(CancellationToken ct = default)
    {
        var env = await SendAsync<ProxmoxEnvelope<List<ProxmoxClusterResource>>>(
            HttpMethod.Get, "/api2/json/cluster/resources?type=vm", ct).ConfigureAwait(false);
        return env.Data ?? new List<ProxmoxClusterResource>();
    }

    public async Task<ProxmoxAgentNetwork?> TryGetQemuAgentNetworkAsync(
        string node, int vmid, CancellationToken ct = default)
    {
        // Use a try/catch rather than 500-checking — PVE's "agent not
        // running" path is the most common failure here and there's no
        // useful action the caller can take beyond falling back. Other
        // errors (auth, network) we still want to bubble up, but for
        // discovery we treat them all as "no IP available" and let the
        // sync engine keep going.
        var path = $"/api2/json/nodes/{Uri.EscapeDataString(node)}/qemu/{vmid}/agent/network-get-interfaces";
        try
        {
            var env = await SendAsync<ProxmoxEnvelope<ProxmoxAgentNetwork>>(HttpMethod.Get, path, ct).ConfigureAwait(false);
            return env.Data;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ProxmoxVmConfig?> TryGetVmConfigAsync(
        string node, string type, int vmid, CancellationToken ct = default)
    {
        if (type != "qemu" && type != "lxc") return null;
        var path = $"/api2/json/nodes/{Uri.EscapeDataString(node)}/{type}/{vmid}/config";
        try
        {
            var env = await SendAsync<ProxmoxEnvelope<ProxmoxVmConfig>>(HttpMethod.Get, path, ct).ConfigureAwait(false);
            return env.Data;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> PowerActionAsync(
        string node, string type, int vmid,
        ProxmoxPowerAction action, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(node)) throw new ArgumentException("node is required.", nameof(node));
        if (type != "qemu" && type != "lxc")
            throw new ArgumentException($"type must be 'qemu' or 'lxc', got '{type}'.", nameof(type));
        if (action == ProxmoxPowerAction.Reset && type == "lxc")
            throw new InvalidOperationException("Reset is not supported on LXC containers.");

        var verb = action switch
        {
            ProxmoxPowerAction.Start    => "start",
            ProxmoxPowerAction.Shutdown => "shutdown",
            ProxmoxPowerAction.Reboot   => "reboot",
            ProxmoxPowerAction.Stop     => "stop",
            ProxmoxPowerAction.Reset    => "reset",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        var path = $"/api2/json/nodes/{Uri.EscapeDataString(node)}/{type}/{vmid}/status/{verb}";
        // PVE returns the UPID in the data field on success. We surface
        // it as a string so the caller can later poll task status.
        var env = await SendAsync<ProxmoxEnvelope<string>>(HttpMethod.Post, path, ct).ConfigureAwait(false);
        return env.Data ?? string.Empty;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>>
        GetAccessPermissionsAsync(CancellationToken ct = default)
    {
        // Proxmox returns { "data": { "/path": { "Perm.X": 1, ... }, ... } }
        // — a nested map keyed by ACL path. We unwrap the envelope and
        // narrow the inner dictionary type for downstream callers.
        var env = await SendAsync<ProxmoxEnvelope<Dictionary<string, Dictionary<string, int>>>>(
            HttpMethod.Get, "/api2/json/access/permissions", ct).ConfigureAwait(false);
        var raw = env.Data ?? new Dictionary<string, Dictionary<string, int>>();
        return raw.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, int>)kv.Value);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, CancellationToken ct)
    {
        // Lazy-auth for password mode. Token mode set the header in ctor
        // and never needs to refresh.
        if (_source.AuthMode == ProxmoxAuthMode.Password && _ticket is null)
            await IssueTicketAsync(ct).ConfigureAwait(false);

        using var req = BuildRequest(method, path);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && _source.AuthMode == ProxmoxAuthMode.Password)
        {
            // Ticket likely expired (default lifetime 2h). Re-auth once.
            _ticket = null;
            await IssueTicketAsync(ct).ConfigureAwait(false);
            using var retry = BuildRequest(method, path);
            using var resp2 = await _http.SendAsync(retry, ct).ConfigureAwait(false);
            return await ParseAsync<T>(resp2, ct).ConfigureAwait(false);
        }

        return await ParseAsync<T>(resp, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        if (_source.AuthMode == ProxmoxAuthMode.Password && _ticket is not null)
        {
            req.Headers.Add("Cookie", $"PVEAuthCookie={_ticket}");
            if (!IsSafeMethod(method) && _csrf is not null)
                req.Headers.Add("CSRFPreventionToken", _csrf);
        }
        return req;
    }

    private static bool IsSafeMethod(HttpMethod m) =>
        m == HttpMethod.Get || m == HttpMethod.Head || m == HttpMethod.Options;

    private async Task IssueTicketAsync(CancellationToken ct)
    {
        var secret = _vault.Load(ProxmoxVault.KeyFor(_source))?.Password
            ?? throw new InvalidOperationException(
                $"Proxmox source '{_source.Name}' has no password in the vault.");

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("username", $"{_source.AuthUser}@{_source.Realm}"),
            new KeyValuePair<string,string>("password", secret),
        });

        using var resp = await _http.PostAsync("/api2/json/access/ticket", form, ct).ConfigureAwait(false);
        var env = await ParseAsync<ProxmoxEnvelope<ProxmoxTicket>>(resp, ct).ConfigureAwait(false);
        _ticket = env.Data?.Ticket
            ?? throw new InvalidOperationException("Proxmox ticket response had no ticket field.");
        _csrf   = env.Data?.CsrfPreventionToken;
    }

    private static async Task<T> ParseAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Proxmox {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 500)}");
        }
        var parsed = await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false);
        return parsed ?? throw new InvalidOperationException("Proxmox returned an empty body.");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        // Proxmox uses snake_case for some fields and ints-as-strings
        // sporadically; tolerate both.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private static string NormalizeBaseUrl(string url)
    {
        var trimmed = (url ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0) throw new ArgumentException("Proxmox BaseUrl is empty.");
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = "https://" + trimmed;
        return trimmed + "/";
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}

internal sealed class ProxmoxClientFactory : IProxmoxClientFactory
{
    private readonly ICredentialVault _vault;
    public ProxmoxClientFactory(ICredentialVault vault) => _vault = vault;
    public IProxmoxClient Create(ProxmoxSource source) => new ProxmoxClient(source, _vault);
}
