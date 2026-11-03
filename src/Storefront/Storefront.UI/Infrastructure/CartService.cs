using System.Text.Json;

namespace Storefront.UI.Infrastructure;

public sealed record CartLine(Guid ProductId, string Name, decimal UnitPrice, int Quantity)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>
/// Shopping cart kept in the user's session. Prices shown here are for display; the Order service re-reads the
/// real price from Catalog when the order is placed.
/// </summary>
public sealed class CartService(IHttpContextAccessor accessor)
{
    private const string SessionKey = "cart";
    public const int MaxQuantityPerLine = 100;

    private ISession Session => accessor.HttpContext?.Session ?? throw new InvalidOperationException("No session is available.");

    public IReadOnlyList<CartLine> Lines
    {
        get
        {
            var json = Session.GetString(SessionKey);
            return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<CartLine>>(json) ?? [];
        }
    }

    public int Count => Lines.Sum(l => l.Quantity);

    public decimal Total => Lines.Sum(l => l.LineTotal);

    public void Add(Guid productId, string name, decimal unitPrice, int quantity)
    {
        var lines = Lines.ToList();
        var index = lines.FindIndex(l => l.ProductId == productId);
        if (index >= 0)
        {
            var existing = lines[index];
            lines[index] = existing with { Quantity = Math.Min(MaxQuantityPerLine, existing.Quantity + quantity), Name = name, UnitPrice = unitPrice };
        }
        else
        {
            lines.Add(new CartLine(productId, name, unitPrice, Math.Clamp(quantity, 1, MaxQuantityPerLine)));
        }

        Save(lines);
    }

    public void Update(Guid productId, int quantity)
    {
        var lines = Lines.ToList();
        var index = lines.FindIndex(l => l.ProductId == productId);
        if (index < 0)
        {
            return;
        }

        if (quantity <= 0)
        {
            lines.RemoveAt(index);
        }
        else
        {
            lines[index] = lines[index] with { Quantity = Math.Min(MaxQuantityPerLine, quantity) };
        }

        Save(lines);
    }

    public void Remove(Guid productId) => Update(productId, 0);

    public void Clear() => Session.Remove(SessionKey);

    private void Save(List<CartLine> lines) => Session.SetString(SessionKey, JsonSerializer.Serialize(lines));
}
