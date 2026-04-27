namespace NexusRDM.Core.Services;

/// <summary>
/// Tiny singleton event hub: every time a service writes to the audit
/// log it pings <see cref="EntryWritten"/>. The audit-log page
/// subscribes so its list refreshes without polling. Decoupled from the
/// repository to avoid leaking lifetime concerns (the repo is scoped
/// per DbContext; an event on a scoped service wouldn't fan out across
/// scopes).
/// </summary>
public interface IAuditNotifier
{
    event EventHandler? EntryWritten;
    void NotifyEntryWritten();
}

public sealed class AuditNotifier : IAuditNotifier
{
    public event EventHandler? EntryWritten;
    public void NotifyEntryWritten() => EntryWritten?.Invoke(this, EventArgs.Empty);
}
