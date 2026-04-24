namespace NexusRDM.Core.Models;

public enum AuditAction { Connected, Disconnected, Failed, Created, Updated, Deleted }

public class AuditEntry
{
    public Guid        Id           { get; set; } = Guid.NewGuid();
    public Guid        ConnectionId { get; set; }
    public string      DisplayName  { get; set; } = string.Empty; // denormalised snapshot
    public AuditAction Action       { get; set; }
    public DateTime    OccurredAt   { get; set; } = DateTime.UtcNow;
    public string?     Detail       { get; set; }
}
