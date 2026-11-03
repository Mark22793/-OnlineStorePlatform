using ApiGateway.Data;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.JsonWebTokens;
using Store.Shared.Common.Exceptions;
using Store.Shared.Common.Security;

namespace ApiGateway.Auth;

/// <summary>
/// Register and log in, served by the gateway itself (not routed by Ocelot), backed by ASP.NET Core Identity in
/// AuthDb. Contract: contracts/auth-v1.yaml.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("auth/v{version:apiVersion}")]
[Produces("application/json")]
public sealed class AuthController(
    UserManager<ApplicationUser> users,
    ITokenService tokens,
    TimeProvider time,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>Fixed-window limiter on register and login (see Program.cs).</summary>
    public const string RateLimitPolicy = "auth";

    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicy)]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var email = request.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
        };

        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
            {
                throw new ConflictException("An account with this email already exists.");
            }

            foreach (var error in created.Errors)
            {
                ModelState.AddModelError(error.Code, error.Description);
            }

            return ValidationProblem(ModelState);
        }

        // Self-registration always creates a Customer; Admins are seeded from configuration.
        var addedToRole = await users.AddToRoleAsync(user, StoreRoles.Customer);
        if (!addedToRole.Succeeded)
        {
            throw new InvalidOperationException("Could not assign the Customer role: " + string.Join("; ", addedToRole.Errors.Select(e => e.Description)));
        }

        logger.LogInformation("Registered user {UserId} as {Role}", user.Id, StoreRoles.Customer);
        return StatusCode(StatusCodes.Status201Created, UserResponse.From(user, StoreRoles.Customer));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicy)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Hash anyway so unknown emails and wrong passwords take similar time.
            users.PasswordHasher.HashPassword(new ApplicationUser(), request.Password);
            return InvalidCredentials();
        }

        if (await users.IsLockedOutAsync(user))
        {
            logger.LogWarning("Login for locked-out user {UserId}", user.Id);
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Account locked",
                detail: "Too many failed attempts. Try again in a few minutes.");
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            logger.LogWarning("Failed login for user {UserId}", user.Id);
            return InvalidCredentials();
        }

        await users.ResetAccessFailedCountAsync(user);

        var role = await PrimaryRoleAsync(user);
        var token = tokens.Issue(user, role);
        logger.LogInformation("Issued token for user {UserId} ({Role})", user.Id, role);

        return Ok(new LoginResponse(token.AccessToken, "Bearer", token.ExpiresInSeconds, token.ExpiresAtUtc, UserResponse.From(user, role)));
    }

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<UserResponse> Me()
    {
        var subject = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? throw new UnauthenticatedException("The token has no subject.");
        var user = await users.FindByIdAsync(subject)
            ?? throw new NotFoundException("The account for this token no longer exists.");

        return UserResponse.From(user, await PrimaryRoleAsync(user));
    }

    private async Task<string> PrimaryRoleAsync(ApplicationUser user)
    {
        var roles = await users.GetRolesAsync(user);
        return roles.Contains(StoreRoles.Admin) ? StoreRoles.Admin : StoreRoles.Customer;
    }

    private ObjectResult InvalidCredentials() =>
        Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials", detail: "Email or password is incorrect.");
}
