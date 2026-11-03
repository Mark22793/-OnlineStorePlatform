using System.ComponentModel.DataAnnotations;
using ApiGateway.Data;

namespace ApiGateway.Auth;

// DTOs mirror contracts/auth-v1.yaml. Change the contract first, then these.

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, RegularExpression(@"^(?=.*[A-Za-z])(?=.*\d).{8,128}$",
        ErrorMessage = "Password must be 8-128 characters and contain at least one letter and one digit.")] string Password,
    [Required, MinLength(1), MaxLength(100)] string DisplayName);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed record UserResponse(Guid Id, string Email, string DisplayName, string Role)
{
    public static UserResponse From(ApplicationUser user, string role) => new(user.Id, user.Email ?? string.Empty, user.DisplayName, role);
}

public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    DateTime ExpiresAtUtc,
    UserResponse User);
