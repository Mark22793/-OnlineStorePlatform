using System.Text;
using ApiGateway.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ApiGateway.Auth;

public sealed record IssuedToken(string AccessToken, DateTime ExpiresAtUtc, int ExpiresInSeconds);

public interface ITokenService
{
    /// <summary>Signs a JWT for <paramref name="user"/> carrying sub, email, name and role.</summary>
    IssuedToken Issue(ApplicationUser user, string role);
}

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider time) : ITokenService
{
    /// <summary>Authentication scheme name. ocelot.json lists it in AuthenticationProviderKeys.</summary>
    public const string Scheme = "Bearer";

    /// <summary>Claim type for the role. Kept short ("role") so Ocelot's RouteClaimsRequirement can match it.</summary>
    public const string RoleClaim = "role";

    public IssuedToken Issue(ApplicationUser user, string role)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(o.ExpiryMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Email] = user.Email ?? string.Empty,
                [JwtRegisteredClaimNames.Name] = user.DisplayName,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                [RoleClaim] = role,
            },
            SigningCredentials = new SigningCredentials(CreateKey(o.Key), SecurityAlgorithms.HmacSha256),
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new IssuedToken(token, expires, (int)(expires - now).TotalSeconds);
    }

    public static SymmetricSecurityKey CreateKey(string key) => new(Encoding.UTF8.GetBytes(key));

    /// <summary>
    /// The gateway both issues and validates tokens, so key, issuer and audience come from the same Jwt settings.
    /// Throws at startup if the key is missing or too short.
    /// </summary>
    public static TokenValidationParameters CreateValidationParameters(JwtOptions o)
    {
        if (o.Key.Length < 32)
        {
            throw new InvalidOperationException("Jwt:Key must be at least 32 characters (set the Jwt__Key environment variable).");
        }

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = o.Issuer,
            ValidateAudience = true,
            ValidAudience = o.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = CreateKey(o.Key),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Name,
            RoleClaimType = RoleClaim,
        };
    }
}
