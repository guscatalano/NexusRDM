namespace NexusRDM.Core.Models;

public enum AuditAction
{
    // Original actions (per-connection lifecycle).
    Connected, Disconnected, Failed, Created, Updated, Deleted,
    // Newer actions (per-integration / per-action). Append-only:
    // existing rows in the AuditLog table store the action as int,
    // so reordering would silently rewrite history.
    Synced,        // sync run completed (Proxmox / Hyper-V / Discovery)
    PowerAction,   // user triggered Start / Stop / Reboot / etc.
    Detached,      // user detached a row from its sync source
    FileTransfer,  // SFTP upload or download completed (Detail carries direction + paths + bytes)
}

public class AuditEntry
{
    public Guid        Id           { get; set; } = Guid.NewGuid();
    public Guid        ConnectionId { get; set; }
    public string      DisplayName  { get; set; } = string.Empty; // denormalised snapshot
    public AuditAction Action       { get; set; }
    public DateTime    OccurredAt   { get; set; } = DateTime.UtcNow;
    public string?     Detail       { get; set; }
}
