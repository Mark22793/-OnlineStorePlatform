using Microsoft.AspNetCore.Identity;
using Store.Shared.Common.Security;

namespace ApiGateway.Data;

/// <summary>
/// Creates the Admin and Customer roles and the demo accounts. Credentials come from configuration
/// (Seed__AdminEmail etc. in .env); nothing is hard-coded. Safe to run on every start: existing roles,
/// users and memberships are left as they are.
/// </summary>
public sealed class AuthSeeder(
    RoleManager<IdentityRole<Guid>> roles,
    UserManager<ApplicationUser> users,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<AuthSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct)
    {
        foreach (var role in new[] { StoreRoles.Admin, StoreRoles.Customer })
        {
            ct.ThrowIfCancellationRequested();
            if (!await roles.RoleExistsAsync(role))
            {
                var result = await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() });
                EnsureSucceeded(result, $"create role {role}");
                logger.LogInformation("Created role {Role}", role);
            }
        }

        await SeedUserAsync(configuration["Seed:AdminEmail"], configuration["Seed:AdminPassword"], "Store Admin", StoreRoles.Admin);
        await SeedUserAsync(configuration["Seed:CustomerEmail"], configuration["Seed:CustomerPassword"], "Demo Customer", StoreRoles.Customer);
    }

    private async Task SeedUserAsync(string? email, string? password, string displayName, string role)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogInformation("Seed account for role {Role} not configured; skipping", role);
            return;
        }

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            };
            EnsureSucceeded(await users.CreateAsync(user, password), $"create seed user {email}");
            logger.LogInformation("Seeded {Role} account {Email}", role, email);
        }

        if (!await users.IsInRoleAsync(user, role))
        {
            EnsureSucceeded(await users.AddToRoleAsync(user, role), $"add {email} to {role}");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not {action}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}
