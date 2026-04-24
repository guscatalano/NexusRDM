using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusRDM.Data.Context;

namespace NexusRDM.Data;

/// <summary>
/// Lets `dotnet ef` create the DbContext at design time without needing
/// the WinUI startup project to build. Run migrations from the NexusRDM.Data directory.
/// </summary>
public sealed class NexusDbContextFactory : IDesignTimeDbContextFactory<NexusDbContext>
{
    public NexusDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<NexusDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new NexusDbContext(opts);
    }
}
