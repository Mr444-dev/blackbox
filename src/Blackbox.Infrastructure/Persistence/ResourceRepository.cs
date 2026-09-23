using System.Data;
using Blackbox.Core.Domain;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Blackbox.Infrastructure.Persistence;

public interface IResourceRepository
{
    Task<Resource?> GetByIdForUpdateAsync(string resourceId, CancellationToken cancellationToken = default);
    Task UpdateAsync(Resource resource, CancellationToken cancellationToken = default);
    Task AddReservationAsync(ResourceReservation reservation, CancellationToken cancellationToken = default);
}

public class ResourceRepository : IResourceRepository
{
    private readonly BlackboxDbContext _dbContext;

    public ResourceRepository(BlackboxDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Resource?> GetByIdForUpdateAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        if (!_dbContext.Database.IsRelational())
        {
            return await _dbContext.Resources.FirstOrDefaultAsync(r => r.Id == resourceId, cancellationToken);
        }

        var connection = _dbContext.Database.GetDbConnection();
        var currentTransaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        const string sql = """
            SELECT id, name, available_quantity AS AvailableQuantity, allocated_quantity AS AllocatedQuantity, version, updated_at AS UpdatedAt
            FROM resources
            WHERE id = @ResourceId
            FOR UPDATE;
            """;

        var command = new CommandDefinition(
            sql,
            new { ResourceId = resourceId },
            transaction: currentTransaction,
            cancellationToken: cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Resource>(command);
    }

    public async Task UpdateAsync(Resource resource, CancellationToken cancellationToken = default)
    {
        if (!_dbContext.Database.IsRelational())
        {
            _dbContext.Resources.Update(resource);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var connection = _dbContext.Database.GetDbConnection();
        var currentTransaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();

        const string sql = """
            UPDATE resources
            SET available_quantity = @AvailableQuantity,
                allocated_quantity = @AllocatedQuantity,
                version = @Version,
                updated_at = @UpdatedAt
            WHERE id = @Id;
            """;

        var command = new CommandDefinition(
            sql,
            new
            {
                resource.AvailableQuantity,
                resource.AllocatedQuantity,
                resource.Version,
                resource.UpdatedAt,
                resource.Id
            },
            transaction: currentTransaction,
            cancellationToken: cancellationToken);

        await connection.ExecuteAsync(command);
    }

    public async Task AddReservationAsync(ResourceReservation reservation, CancellationToken cancellationToken = default)
    {
        await _dbContext.ResourceReservations.AddAsync(reservation, cancellationToken);
    }
}
