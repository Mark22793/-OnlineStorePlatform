using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Store.Shared.Common.Persistence;

namespace ApiGateway.Data;

/// <summary>ASP.NET Core Identity user (table AspNetUsers) with a display name.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// AuthDb: the standard ASP.NET Core Identity schema (AspNetUsers, AspNetRoles, AspNetUserRoles,
/// AspNetUserClaims, AspNetUserLogins, AspNetUserTokens, AspNetRoleClaims).
/// Owned by the API gateway (login auth_svc), because the gateway hosts register/login and issues the JWTs.
/// No service reads it; services learn who the caller is only from the validated token (X-User-* headers).
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.UseUtcDateTimes();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder); // Identity tables, keys and indexes

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            user.Property(u => u.CreatedAtUtc).HasColumnType("datetime2");
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef</c> (InitialAuthCreate).</summary>
public sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("AUTH_DB_DESIGN_CONNECTION")
            ?? "Server=localhost,14333;Database=AuthDb;Integrated Security=false;TrustServerCertificate=True";
        return new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options);
    }
}
