/*
  Run automatically by the sqlserver-init container (docker-compose.yml) as sa, before any service starts.

  Creates the five databases and one SQL login per owning service. Each login is db_owner of its own database
  only and has no user in any other database, so a service cannot read another service's data even though they
  share one SQL Server instance. This is the database-per-service rule enforced by SQL Server permissions.

  Tables are NOT created here: EF Core migrations own the schema and run at each service's startup.
  Passwords come from environment variables (sqlcmd scripting variables), set in .env. Never hard-code them.
  The script is idempotent: re-running it only re-applies the passwords.
*/
SET NOCOUNT ON;
GO

-------------------------------------------------------------------- databases
IF DB_ID(N'CatalogDb')   IS NULL CREATE DATABASE [CatalogDb];
IF DB_ID(N'InventoryDb') IS NULL CREATE DATABASE [InventoryDb];
IF DB_ID(N'OrderDb')     IS NULL CREATE DATABASE [OrderDb];
IF DB_ID(N'PaymentDb')   IS NULL CREATE DATABASE [PaymentDb];
IF DB_ID(N'AuthDb')      IS NULL CREATE DATABASE [AuthDb];
GO

-------------------------------------------------------------------- logins (server level)
IF SUSER_ID(N'catalog_svc') IS NULL
    CREATE LOGIN [catalog_svc] WITH PASSWORD = N'$(CATALOG_DB_PASSWORD)', DEFAULT_DATABASE = [CatalogDb], CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [catalog_svc] WITH PASSWORD = N'$(CATALOG_DB_PASSWORD)';

IF SUSER_ID(N'inventory_svc') IS NULL
    CREATE LOGIN [inventory_svc] WITH PASSWORD = N'$(INVENTORY_DB_PASSWORD)', DEFAULT_DATABASE = [InventoryDb], CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [inventory_svc] WITH PASSWORD = N'$(INVENTORY_DB_PASSWORD)';

IF SUSER_ID(N'order_svc') IS NULL
    CREATE LOGIN [order_svc] WITH PASSWORD = N'$(ORDER_DB_PASSWORD)', DEFAULT_DATABASE = [OrderDb], CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [order_svc] WITH PASSWORD = N'$(ORDER_DB_PASSWORD)';

IF SUSER_ID(N'payment_svc') IS NULL
    CREATE LOGIN [payment_svc] WITH PASSWORD = N'$(PAYMENT_DB_PASSWORD)', DEFAULT_DATABASE = [PaymentDb], CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [payment_svc] WITH PASSWORD = N'$(PAYMENT_DB_PASSWORD)';

IF SUSER_ID(N'auth_svc') IS NULL
    CREATE LOGIN [auth_svc] WITH PASSWORD = N'$(AUTH_DB_PASSWORD)', DEFAULT_DATABASE = [AuthDb], CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [auth_svc] WITH PASSWORD = N'$(AUTH_DB_PASSWORD)';
GO

-------------------------------------------------------------------- users: one owner per database
USE [CatalogDb];
IF USER_ID(N'catalog_svc') IS NULL CREATE USER [catalog_svc] FOR LOGIN [catalog_svc];
ALTER ROLE [db_owner] ADD MEMBER [catalog_svc];
GO

USE [InventoryDb];
IF USER_ID(N'inventory_svc') IS NULL CREATE USER [inventory_svc] FOR LOGIN [inventory_svc];
ALTER ROLE [db_owner] ADD MEMBER [inventory_svc];
GO

USE [OrderDb];
IF USER_ID(N'order_svc') IS NULL CREATE USER [order_svc] FOR LOGIN [order_svc];
ALTER ROLE [db_owner] ADD MEMBER [order_svc];
GO

USE [PaymentDb];
IF USER_ID(N'payment_svc') IS NULL CREATE USER [payment_svc] FOR LOGIN [payment_svc];
ALTER ROLE [db_owner] ADD MEMBER [payment_svc];
GO

USE [AuthDb];
IF USER_ID(N'auth_svc') IS NULL CREATE USER [auth_svc] FOR LOGIN [auth_svc];
ALTER ROLE [db_owner] ADD MEMBER [auth_svc];
GO

USE [master];
PRINT 'Databases and service logins are ready.';
GO
