using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Store.Shared.Common.Persistence;

public static class DatabaseStartup
{
    /// <summary>
    /// Applies the service's EF Core migrations at startup (creating the database schema on a clean clone), then
    /// runs the optional <paramref name="seed"/> callback. Retries while SQL Server is still starting.
    /// Never drops or deletes anything.
    /// <para>
    /// If the project has no migrations yet, the schema is created with EnsureCreated so the system still runs.
    /// Generate the initial migration (see README, "Database migrations") and then reset the volume once with
    /// <c>docker compose down -v</c>, because EnsureCreated does not record migration history.
    /// </para>
    /// </summary>
    public static async Task InitializeDatabaseAsync<TContext>(
        this WebApplication app,
        Func<TContext, IServiceProvider, CancellationToken, Task>? seed = null,
        int maxAttempts = 15)
        where TContext : DbContext
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
        var ct = app.Lifetime.ApplicationStopping;
        var name = typeof(TContext).Name;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();

                if (db.Database.GetMigrations().Any())
                {
                    var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                    await db.Database.MigrateAsync(ct);
                    logger.LogInformation("{Context}: applied {Count} pending migration(s) {Migrations}", name, pending.Count, pending);
                }
                else
                {
                    logger.LogWarning(
                        "{Context}: no EF Core migrations found, creating the schema with EnsureCreated. " +
                        "Generate the initial migration (README, 'Database migrations').", name);
                    await db.Database.EnsureCreatedAsync(ct);
                }

                if (seed is not null)
                {
                    await seed(db, scope.ServiceProvider, ct);
                }

                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "{Context}: database not ready (attempt {Attempt}/{Max}); retrying in 5s", name, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// SQL Server datetime2 has no time zone, so values come back with DateTimeKind.Unspecified.
    /// Every DateTime in this solution is UTC; this marks them as such when read.
    /// Call from <c>ConfigureConventions</c>.
    /// </summary>
    public static ModelConfigurationBuilder UseUtcDateTimes(this ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        return builder;
    }
}

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
