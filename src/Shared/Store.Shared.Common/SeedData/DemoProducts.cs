namespace Store.Shared.Common.SeedData;

/// <summary>
/// The demo catalogue, defined once so CatalogDb (products) and InventoryDb (stock) are seeded with the same
/// product IDs. The services stay independent: each copies the values into its own database through its own
/// migration (EF Core HasData); neither reads the other's database.
/// </summary>
public static class DemoProducts
{
    /// <summary>Fixed timestamp for seed rows, so the migration is deterministic.</summary>
    public static readonly DateTime SeededAtUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly IReadOnlyList<DemoProduct> All =
    [
        new(new Guid("a1b2c3d4-0001-4000-8000-000000000001"), "Wireless Mouse", "Ergonomic 2.4 GHz wireless mouse with silent clicks and a 12-month battery.", 899.00m, 100),
        new(new Guid("a1b2c3d4-0002-4000-8000-000000000002"), "Mechanical Keyboard", "Tenkeyless mechanical keyboard with hot-swappable brown switches and white backlight.", 3499.00m, 60),
        new(new Guid("a1b2c3d4-0003-4000-8000-000000000003"), "27-inch QHD Monitor", "27-inch 2560x1440 IPS monitor, 144 Hz, with height-adjustable stand.", 15999.00m, 25),
        new(new Guid("a1b2c3d4-0004-4000-8000-000000000004"), "USB-C Hub 7-in-1", "Aluminium hub with HDMI 4K, 3x USB-A, SD/microSD and 100 W power delivery.", 1599.00m, 80),
        new(new Guid("a1b2c3d4-0005-4000-8000-000000000005"), "Noise-Cancelling Headphones", "Over-ear Bluetooth headphones with active noise cancelling and 30-hour battery.", 7999.00m, 40),
        new(new Guid("a1b2c3d4-0006-4000-8000-000000000006"), "1080p Webcam", "Full HD webcam with dual microphones, auto light correction and privacy shutter.", 2299.00m, 70),
        new(new Guid("a1b2c3d4-0007-4000-8000-000000000007"), "Portable SSD 1 TB", "USB 3.2 Gen 2 portable SSD, up to 1,050 MB/s, shock resistant.", 5499.00m, 50),
        new(new Guid("a1b2c3d4-0008-4000-8000-000000000008"), "Laptop Stand", "Foldable aluminium laptop stand with six height settings.", 1199.00m, 90),
        new(new Guid("a1b2c3d4-0009-4000-8000-000000000009"), "Smart Power Strip", "Wi-Fi power strip with four outlets, two USB ports and surge protection.", 1899.00m, 45),
        new(new Guid("a1b2c3d4-0010-4000-8000-000000000010"), "Desk Lamp LED", "Dimmable LED desk lamp with five colour temperatures and a wireless charging base.", 2499.00m, 35),
    ];
}

public sealed record DemoProduct(Guid Id, string Name, string Description, decimal Price, int InitialStock);
