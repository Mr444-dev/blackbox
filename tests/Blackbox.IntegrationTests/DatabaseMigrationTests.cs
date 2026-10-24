using Blackbox.Core.Domain;
using Blackbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Blackbox.IntegrationTests;

public sealed class DatabaseMigrationTests : IClassFixture<PostgreSqlFixture>
{
    private const string BaselineMigrationId = "20261015193546_BaselineEnsureCreatedSchema";
    private readonly PostgreSqlFixture _fixture;

    public DatabaseMigrationTests(PostgreSqlFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task Migrate_creates_the_schema_and_can_run_again_without_changes()
    {
        var schema = $"migration_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await adminConnection.OpenAsync();

        await using (var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\" AUTHORIZATION \"test_user\"", adminConnection))
        {
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
            {
                SearchPath = schema
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<BlackboxDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            await using var dbContext = new BlackboxDbContext(options);
            await dbContext.Database.MigrateAsync();

            Assert.Contains(BaselineMigrationId, await dbContext.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await dbContext.Resources.CountAsync());

            await dbContext.Database.MigrateAsync();

            Assert.Contains(BaselineMigrationId, await dbContext.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", adminConnection);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    [DockerFact]
    public async Task Adoption_script_stamps_a_matching_EnsureCreated_schema()
    {
        var schema = $"adoption_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await adminConnection.OpenAsync();
        await using (var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\" AUTHORIZATION \"test_user\"", adminConnection))
        {
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
            {
                SearchPath = schema
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<BlackboxDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            await using var dbContext = new BlackboxDbContext(options);
            // Create the old EnsureCreated schema from the same baseline, then remove its
            // migration history table to reproduce the pre-migrations database state.
            await dbContext.GetService<IMigrator>().MigrateAsync(BaselineMigrationId);
            var existingWorkflow = WorkflowInstance.Create($"legacy-{Guid.NewGuid():N}", "{\"migration\":\"legacy\"}");
            await dbContext.Workflows.AddAsync(existingWorkflow);
            await dbContext.SaveChangesAsync();
            var existingWorkflowId = existingWorkflow.Id;
            await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");

            var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "adopt-ensurecreated-schema.sql");
            var script = await File.ReadAllTextAsync(scriptPath);
            var result = await _fixture.Container!.ExecAsync(
            [
                "psql",
                "-U", "test_user",
                "-d", "blackbox_test_db",
                "-v", "ON_ERROR_STOP=1",
                "-c", $"SET search_path TO \"{schema}\";{Environment.NewLine}{script}"
            ]);

            Assert.True(result.ExitCode == 0, $"stdout: {result.Stdout}{Environment.NewLine}stderr: {result.Stderr}");

            Assert.Contains(BaselineMigrationId, await dbContext.Database.GetAppliedMigrationsAsync());
            Assert.Single(await dbContext.Database.GetPendingMigrationsAsync());

            await dbContext.Database.MigrateAsync();

            Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
            Assert.Equal(1, await dbContext.Workflows.CountAsync(workflow => workflow.Id == existingWorkflowId));
            Assert.Equal(0, await dbContext.WorkflowEventProjections.CountAsync());
        }
        finally
        {
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", adminConnection);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }
}
