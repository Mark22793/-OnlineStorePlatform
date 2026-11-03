using System.ComponentModel.DataAnnotations;
using Catalog.API.Data;

namespace Catalog.API.Contracts;

// Mirrors contracts/catalog-v1.yaml (written first). Change the contract, then these.

public sealed record ProductResponse(
    Guid Id,
    string Name,
    string Description,
    decimal Price,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static ProductResponse From(Product p) =>
        new(p.Id, p.Name, p.Description, p.Price, p.IsActive, p.CreatedAtUtc, p.UpdatedAtUtc);
}

public sealed record ProductRequest(
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description,
    [Range(typeof(decimal), "0", "1000000")] decimal Price,
    bool IsActive = true);
