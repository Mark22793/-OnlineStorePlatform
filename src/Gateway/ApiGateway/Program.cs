using System.Threading.RateLimiting;
using ApiGateway.Auth;
using ApiGateway.Data;
using ApiGateway.Middleware;
using Asp.Versioning;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Store.Shared.Common.Exceptions;
using Store.Shared.Common.Middleware;
using Store.Shared.Common.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ------------------------------------------------------------------------------
// ocelot.json routes to the docker compose service names. Env vars are re-added after it so they win
// (e.g. GlobalConfiguration__BaseUrl). Secrets (Jwt__Key, connection string) only ever come from env vars.
builder.Configuration
    .AddJsonFile("ocelot.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwt = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
var tokenValidation = TokenService.CreateValidationParameters(jwt); // fails fast if Jwt__Key is missing/short
builder.Services.Configure<JwtOptions>(jwtSection);

var authDb = builder.Configuration.GetConnectionString("AuthDb")
    ?? throw new InvalidOperationException("ConnectionStrings:AuthDb is not set (env var ConnectionStrings__AuthDb).");
var authPermitsPerMinute = builder.Configuration.GetValue("RateLimiting:AuthPermitsPerMinute", 5);

// ---- Security: JWT validated at the edge ---------------------------------------------------------
builder.Services
    .AddAuthentication(TokenService.Scheme)
    .AddJwtBearer(TokenService.Scheme, o =>
    {
        o.MapInboundClaims = false; // keep "sub"/"role" so RouteClaimsRequirement { "role": "Admin" } matches
        o.TokenValidationParameters = tokenValidation;
    });
builder.Services.AddAuthorization();

// ---- Users (ASP.NET Core Identity in AuthDb) and token issuing -----------------------------------
builder.Services.AddDbContext<AuthDbContext>(o => o.UseSqlServer(authDb, sql => sql.EnableRetryOnFailure()));
builder.Services
    .AddIdentityCore<ApplicationUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        o.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AuthDbContext>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<AuthSeeder>();

// ---- Cross-cutting policy: rate limit on register/login (per client IP, fixed window) ------------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests; // body filled in by GatewayProblemDetailsMiddleware
    o.AddPolicy(AuthController.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authPermitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        return ValueTask.CompletedTask;
    };
});

// ---- Ocelot + the gateway's own controllers ------------------------------------------------------
builder.Services.AddOcelot(builder.Configuration);

builder.Services
    .AddControllers()
    // Ocelot ships admin controllers (e.g. /configuration). They are never exposed here.
    .ConfigureApplicationPartManager(parts =>
    {
        foreach (var part in parts.ApplicationParts.Where(p => p.Name.StartsWith("Ocelot", StringComparison.Ordinal)).ToList())
        {
            parts.ApplicationParts.Remove(part);
        }
    });
builder.Services
    .AddApiVersioning(o =>
    {
        o.DefaultApiVersion = new ApiVersion(1, 0);
        o.ReportApiVersions = true;
        o.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddMvc();

builder.Services.AddStoreProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<AuthDbContext>("auth-db");

var app = builder.Build();

// ---- Pipeline ------------------------------------------------------------------------------------
app.UseCorrelationId();                                  // correlation ID is created here, at the edge
app.UseMiddleware<GatewayProblemDetailsMiddleware>();    // Problem Details for Ocelot's bare 401/403/404/429/502
app.UseMiddleware<TrustedHeaderSanitizerMiddleware>();   // callers cannot spoof X-User-* headers
app.UseExceptionHandler();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var swaggerEnabled = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled");
if (swaggerEnabled)
{
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/contracts/auth-v1.yaml", "Auth v1 (gateway)");
        o.DocumentTitle = "Store gateway - Auth";
    });
}

// Gateway-local endpoints (auth, health, contract) run here. Anything unmatched falls through to Ocelot.
// UseEndpoints is needed instead of top-level Map* calls because Ocelot is terminal middleware.
#pragma warning disable ASP0014
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapHealthChecks("/health");
    if (swaggerEnabled)
    {
        var contractPath = Path.Combine(AppContext.BaseDirectory, "contracts", "auth-v1.yaml");
        endpoints.MapGet("/swagger/contracts/auth-v1.yaml", () => Results.File(contractPath, "application/yaml"));
    }
});
#pragma warning restore ASP0014

// AuthDb: apply InitialAuthCreate (Identity schema), then seed roles and demo accounts.
await app.InitializeDatabaseAsync<AuthDbContext>((_, services, ct) => services.GetRequiredService<AuthSeeder>().SeedAsync(ct));

await app.UseOcelot();
await app.RunAsync();
