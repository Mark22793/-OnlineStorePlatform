namespace ApiGateway.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HMAC-SHA256 signing key. At least 32 characters. Supplied via the Jwt__Key environment variable.</summary>
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "store-gateway";
    public string Audience { get; set; } = "store-platform";
    public int ExpiryMinutes { get; set; } = 60;
}
