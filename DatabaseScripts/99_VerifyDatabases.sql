/*
  Read-only verification queries. Run as sa after `docker compose up` and a few test orders:

    docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -i /scripts/99_VerifyDatabases.sql

  Or open it in SSMS / Azure Data Studio connected to localhost,14333.
*/
SET NOCOUNT ON;

PRINT '== 1. Databases and owning logins';
SELECT d.name AS [Database], d.state_desc AS [State], d.create_date AS [Created]
FROM sys.databases d
WHERE d.name IN (N'CatalogDb', N'InventoryDb', N'OrderDb', N'PaymentDb', N'AuthDb')
ORDER BY d.name;

SELECT name AS [Login], default_database_name AS [DefaultDatabase], is_disabled AS [Disabled]
FROM sys.server_principals
WHERE name IN (N'catalog_svc', N'inventory_svc', N'order_svc', N'payment_svc', N'auth_svc');
GO

PRINT '== 2. Applied EF Core migrations per database';
SELECT 'CatalogDb' AS [Database], MigrationId FROM CatalogDb.dbo.__EFMigrationsHistory
UNION ALL SELECT 'InventoryDb', MigrationId FROM InventoryDb.dbo.__EFMigrationsHistory
UNION ALL SELECT 'OrderDb', MigrationId FROM OrderDb.dbo.__EFMigrationsHistory
UNION ALL SELECT 'PaymentDb', MigrationId FROM PaymentDb.dbo.__EFMigrationsHistory
UNION ALL SELECT 'AuthDb', MigrationId FROM AuthDb.dbo.__EFMigrationsHistory;
GO

PRINT '== 3. Tables per database';
SELECT 'CatalogDb' AS [Database], TABLE_NAME FROM CatalogDb.INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'
UNION ALL SELECT 'InventoryDb', TABLE_NAME FROM InventoryDb.INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'
UNION ALL SELECT 'OrderDb', TABLE_NAME FROM OrderDb.INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'
UNION ALL SELECT 'PaymentDb', TABLE_NAME FROM PaymentDb.INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'
UNION ALL SELECT 'AuthDb', TABLE_NAME FROM AuthDb.INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'
ORDER BY 1, 2;
GO

PRINT '== 4. Unique indexes (idempotency and duplicate protection)';
USE InventoryDb;
SELECT 'InventoryDb' AS [Database], t.name AS [Table], i.name AS [Index], i.is_unique AS [Unique]
FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
WHERE i.is_unique = 1 AND i.is_primary_key = 0;
USE OrderDb;
SELECT 'OrderDb' AS [Database], t.name AS [Table], i.name AS [Index], i.is_unique AS [Unique]
FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
WHERE i.is_unique = 1 AND i.is_primary_key = 0;
USE PaymentDb;
SELECT 'PaymentDb' AS [Database], t.name AS [Table], i.name AS [Index], i.is_unique AS [Unique]
FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
WHERE i.is_unique = 1 AND i.is_primary_key = 0;
GO

PRINT '== 5. No foreign key points outside its own database (cross-database FKs are impossible in SQL Server; listed for completeness)';
USE OrderDb;
SELECT 'OrderDb' AS [Database], fk.name AS [ForeignKey], OBJECT_NAME(fk.parent_object_id) AS [From], OBJECT_NAME(fk.referenced_object_id) AS [To], fk.delete_referential_action_desc AS [OnDelete]
FROM sys.foreign_keys fk;
USE InventoryDb;
SELECT 'InventoryDb', fk.name, OBJECT_NAME(fk.parent_object_id), OBJECT_NAME(fk.referenced_object_id), fk.delete_referential_action_desc FROM sys.foreign_keys fk;
USE PaymentDb;
SELECT 'PaymentDb', fk.name, OBJECT_NAME(fk.parent_object_id), OBJECT_NAME(fk.referenced_object_id), fk.delete_referential_action_desc FROM sys.foreign_keys fk;
GO

PRINT '== 6. Seed data';
SELECT COUNT(*) AS [CatalogProducts] FROM CatalogDb.dbo.Products;
SELECT p.Name, p.Price, i.QuantityOnHand, i.QuantityReserved, i.QuantityOnHand - i.QuantityReserved AS [Available]
FROM CatalogDb.dbo.Products p
LEFT JOIN InventoryDb.dbo.Inventory i ON i.ProductId = p.Id   -- verification only; services never join across databases
ORDER BY p.Name;
SELECT r.Name AS [Role], u.Email FROM AuthDb.dbo.AspNetUserRoles ur
JOIN AuthDb.dbo.AspNetRoles r ON r.Id = ur.RoleId
JOIN AuthDb.dbo.AspNetUsers u ON u.Id = ur.UserId
ORDER BY r.Name, u.Email;
SELECT FailureMode, UpdatedAtUtc FROM PaymentDb.dbo.PaymentSettings;
GO

PRINT '== 7. Latest orders with their saga outcome';
SELECT TOP (10) o.Id, o.Status, o.SagaStep, o.TotalAmount, o.SimulatePaymentFailure, o.CancellationReason, o.CreatedAtUtc
FROM OrderDb.dbo.Orders o ORDER BY o.CreatedAtUtc DESC;
GO

PRINT '== 8. Status history of the most recent order';
SELECT h.OccurredAtUtc, h.Status, h.Note, h.CorrelationId
FROM OrderDb.dbo.OrderStatusHistory h
WHERE h.OrderId = (SELECT TOP (1) Id FROM OrderDb.dbo.Orders ORDER BY CreatedAtUtc DESC)
ORDER BY h.OccurredAtUtc;
GO

PRINT '== 9. Payments and refunds';
SELECT TOP (10) p.OrderId, p.Status, p.Amount, p.FailureReason, p.ProviderReference, r.Id AS RefundId, r.Amount AS RefundAmount
FROM PaymentDb.dbo.Payments p LEFT JOIN PaymentDb.dbo.Refunds r ON r.PaymentId = p.Id
ORDER BY p.CreatedAtUtc DESC;
GO

PRINT '== 10. Reservations: Released rows prove compensation restored stock';
SELECT TOP (20) OrderId, ProductId, Quantity, Status, ReleasedAtUtc, CommittedAtUtc
FROM InventoryDb.dbo.StockReservations ORDER BY CreatedAtUtc DESC;
GO

PRINT '== 11. Idempotency guard and outbox';
SELECT 'InventoryDb' AS [Database], ConsumerName, COUNT(*) AS Processed FROM InventoryDb.dbo.ProcessedMessages GROUP BY ConsumerName
UNION ALL SELECT 'OrderDb', ConsumerName, COUNT(*) FROM OrderDb.dbo.ProcessedMessages GROUP BY ConsumerName
UNION ALL SELECT 'PaymentDb', ConsumerName, COUNT(*) FROM PaymentDb.dbo.ProcessedMessages GROUP BY ConsumerName;
SELECT 'InventoryDb' AS [Database], Status, COUNT(*) AS Messages FROM InventoryDb.dbo.OutboxMessages GROUP BY Status
UNION ALL SELECT 'OrderDb', Status, COUNT(*) FROM OrderDb.dbo.OutboxMessages GROUP BY Status
UNION ALL SELECT 'PaymentDb', Status, COUNT(*) FROM PaymentDb.dbo.OutboxMessages GROUP BY Status;
-- Duplicates would show here; the unique index makes this return no rows:
SELECT EventId, ConsumerName, COUNT(*) AS Copies FROM OrderDb.dbo.ProcessedMessages GROUP BY EventId, ConsumerName HAVING COUNT(*) > 1;
GO
