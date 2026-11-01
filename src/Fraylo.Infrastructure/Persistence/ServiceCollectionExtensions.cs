using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fraylo.Infrastructure.Persistence.Interceptors;

namespace Fraylo.Infrastructure.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFrayloPersistence(
        this IServiceCollection services,
        string? databasePath = null)
    {
        var dbPath = databasePath ?? DatabasePathHelper.GetDefaultDatabasePath();
        var connectionString = DatabasePathHelper.BuildConnectionString(dbPath);

        services.AddSingleton(new SqlitePragmaInterceptor());
        services.AddSingleton<ISqliteDatabaseBootstrapper, SqliteDatabaseBootstrapper>();

        services.AddDbContextFactory<FrayloDbContext>((serviceProvider, options) =>
        {
            var interceptor = serviceProvider.GetRequiredService<SqlitePragmaInterceptor>();
            options.UseSqlite(connectionString)
                   .AddInterceptors(interceptor);
        });

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FrayloDbContext>>().CreateDbContext());

        return services;
    }
}
