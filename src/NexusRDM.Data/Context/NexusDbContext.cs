using Microsoft.EntityFrameworkCore;
using NexusRDM.Core.Models;

namespace NexusRDM.Data.Context;

public sealed class NexusDbContext : DbContext
{
    public NexusDbContext(DbContextOptions<NexusDbContext> options) : base(options) { }

    public DbSet<ConnectionProfile> Connections { get; set; } = null!;
    public DbSet<Group>             Groups      { get; set; } = null!;
    public DbSet<AuditEntry>        AuditLog    { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<ConnectionProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Host).IsRequired().HasMaxLength(512);
            e.HasOne(x => x.Group)
             .WithMany(g => g.Connections)
             .HasForeignKey(x => x.GroupId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        mb.Entity<Group>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.HasOne(x => x.Parent)
             .WithMany(g => g.Children)
             .HasForeignKey(x => x.ParentId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.HasIndex(x => x.ConnectionId);
            e.HasIndex(x => x.OccurredAt);
        });
    }
}
