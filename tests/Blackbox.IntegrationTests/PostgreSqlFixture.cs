using Blackbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Blackbox.IntegrationTests;

public class PostgreSqlFixture : IAsyncLifetime
{
    public PostgreSqlContainer? Container { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;
    public bool IsDockerAvailable { get; private set; }
    public string? DockerUnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            Container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("blackbox_test_db")
                .WithUsername("test_user")
                .WithPassword("test_password")
                .Build();

            await Container.StartAsync();
            ConnectionString = Container.GetConnectionString();
            IsDockerAvailable = true;

            // Initialize schema using DbContext
            var options = new DbContextOptionsBuilder<BlackboxDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;

            await using var context = new BlackboxDbContext(options);
            await context.Database.EnsureCreatedAsync();

            // Seed initial test resources if not already present
            if (!await context.Resources.AnyAsync())
            {
                await context.Resources.AddRangeAsync(
                    new Core.Domain.Resource("SKU-LAPTOP-001", "High-End Laptop", 1),
                    new Core.Domain.Resource("SKU-PHONE-002", "Smartphone", 10),
                    new Core.Domain.Resource("SKU-DESK-003", "Standing Desk", 5)
                );
                await context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            IsDockerAvailable = false;
            DockerUnavailableReason = ex.Message;
        }
    }

    public async Task DisposeAsync()
    {
        if (Container != null)
        {
            await Container.DisposeAsync();
        }
    }

    public BlackboxDbContext CreateDbContext()
    {
        if (!IsDockerAvailable)
        {
            throw new InvalidOperationException(
                $"Docker is not currently available to start Testcontainers PostgreSQL. Reason: {DockerUnavailableReason}. " +
                $"Please ensure Docker Desktop is running.");
        }

        var options = new DbContextOptionsBuilder<BlackboxDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new BlackboxDbContext(options);
    }
}
