namespace Fraylo.Infrastructure.Persistence;

public interface ISqliteDatabaseBootstrapper
{
    Task<DatabaseBootstrapResult> BootstrapAsync(string? databasePath = null, CancellationToken cancellationToken = default);
}
