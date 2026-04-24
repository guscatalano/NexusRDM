namespace NexusRDM.Core.Models;

public class Group
{
    public Guid    Id       { get; set; } = Guid.NewGuid();
    public string  Name     { get; set; } = string.Empty;
    public Guid?   ParentId { get; set; }
    public int     SortOrder { get; set; }

    // Navigation
    public Group?                   Parent      { get; set; }
    public ICollection<Group>       Children    { get; set; } = [];
    public ICollection<ConnectionProfile> Connections { get; set; } = [];
}
