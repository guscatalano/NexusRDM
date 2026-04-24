using System.Text.Json;

namespace NexusRDM.Core.Models;

public static class ConnectionProfileExtensions
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static RdpOptions RdpSettings(this ConnectionProfile p) =>
        p.RdpSettingsJson is null
            ? new RdpOptions()
            : JsonSerializer.Deserialize<RdpOptions>(p.RdpSettingsJson, _json) ?? new RdpOptions();

    public static SshOptions SshSettings(this ConnectionProfile p) =>
        p.SshSettingsJson is null
            ? new SshOptions()
            : JsonSerializer.Deserialize<SshOptions>(p.SshSettingsJson, _json) ?? new SshOptions();

    public static void SetRdpSettings(this ConnectionProfile p, RdpOptions opts) =>
        p.RdpSettingsJson = JsonSerializer.Serialize(opts, _json);

    public static void SetSshSettings(this ConnectionProfile p, SshOptions opts) =>
        p.SshSettingsJson = JsonSerializer.Serialize(opts, _json);

    public static string[] TagList(this ConnectionProfile p) =>
        string.IsNullOrWhiteSpace(p.Tags)
            ? []
            : p.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
