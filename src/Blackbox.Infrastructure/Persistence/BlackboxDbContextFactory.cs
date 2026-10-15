using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Blackbox.Infrastructure.Persistence;

public sealed class BlackboxDbContextFactory : IDesignTimeDbContextFactory<BlackboxDbContext>
{
    public BlackboxDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLACKBOX_MIGRATIONS_CONNECTION_STRING")
            ?? "Host=localhost;Database=blackbox_design;Username=blackbox_design;Password=not-used-for-migration-scaffolding";

        var options = new DbContextOptionsBuilder<BlackboxDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new BlackboxDbContext(options);
    }
}
