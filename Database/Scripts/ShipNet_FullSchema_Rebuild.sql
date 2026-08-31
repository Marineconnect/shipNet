/*
    ShipNet full schema rebuild script
    Generated from application services and Database/Scripts migrations.

    Usage:
    - Run against a new/empty SQL Server database selected in SSMS/sqlcmd.
    - This script creates missing tables, indexes, common foreign keys, and default system settings.
    - It does not drop existing objects and does not restore lost business data.
    - Secrets, KVH tokens, and user passwords are intentionally not seeded.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[dbo].[TblTenant]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTenant](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenant] PRIMARY KEY,
        [TenantName] nvarchar(250) NOT NULL,
        [Email] nvarchar(350) NULL,
        [PhoneNumber] nvarchar(50) NULL,
        [Description] nvarchar(1000) NULL,
        [Logo] nvarchar(550) NULL,
        [Address] nvarchar(550) NULL,
        [Created_Date] datetime NULL,
        [Created_By] nvarchar(50) NULL,
        [Updated_Date] datetime NULL,
        [Updated_By] nvarchar(50) NULL
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblDevices]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblDevices](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblDevices] PRIMARY KEY,
        [DeviceName] nvarchar(250) NOT NULL,
        [DeviceCode] nvarchar(250) NULL,
        [VesselName] nvarchar(250) NOT NULL,
        [TenantID] int NULL,
        [TokenString] nvarchar(max) NULL,
        [TokenExpiredTime] datetime NULL,
        [Availability] nvarchar(100) NULL,
        [LastUpdateTime] datetime NULL,
        [LastSysnTime] datetime NULL,
        [KITID] nvarchar(250) NULL,
        [KITNumber] nvarchar(250) NULL,
        [UsageData] decimal(18,2) NULL,
        [PriorityData] decimal(18,2) NULL,
        [OverageData] decimal(18,2) NULL,
        [Latitude] nvarchar(100) NULL,
        [Longitude] nvarchar(100) NULL,
        [SystemType] nvarchar(100) NULL,
        [ServiceLine] nvarchar(250) NULL,
        [PlanName] nvarchar(255) NULL,
        [TrafficId] nvarchar(200) NULL,
        [KvhUsageLastSyncUtc] datetime2 NULL,
        [KvhSubscriptionStatus] nvarchar(80) NULL,
        [KvhSubscriptionPlan] nvarchar(255) NULL,
        [KvhSubscriptionRegion] nvarchar(120) NULL,
        [KvhSubscriptionScheduledAction] nvarchar(120) NULL,
        [KvhSubscriptionScheduleId] nvarchar(200) NULL,
        [KvhSubscriptionEffectiveDateUtc] datetime2 NULL,
        [KvhSubscriptionLastSyncUtc] datetime2 NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblDevices_TenantID' AND [object_id] = OBJECT_ID(N'[dbo].[TblDevices]'))
    CREATE INDEX [IX_TblDevices_TenantID] ON [dbo].[TblDevices]([TenantID]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblDevices_DeviceCode' AND [object_id] = OBJECT_ID(N'[dbo].[TblDevices]'))
    CREATE INDEX [IX_TblDevices_DeviceCode] ON [dbo].[TblDevices]([DeviceCode]) WHERE [DeviceCode] IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblDevices_KITNumber' AND [object_id] = OBJECT_ID(N'[dbo].[TblDevices]'))
    CREATE INDEX [IX_TblDevices_KITNumber] ON [dbo].[TblDevices]([KITNumber]) WHERE [KITNumber] IS NOT NULL;
GO

IF OBJECT_ID(N'[dbo].[TblMRUser]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblMRUser](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblMRUser] PRIMARY KEY,
        [USName] nvarchar(50) NOT NULL,
        [USPass] nvarchar(150) NOT NULL,
        [DisplayName] nvarchar(250) NOT NULL,
        [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblMRUser_Status] DEFAULT(N'active'),
        [Lastonlinetime] datetime NULL,
        [IPAccess] nvarchar(100) NULL,
        [LastUpdatePassword] datetime NULL,
        [Avatar] nvarchar(550) NULL,
        [UserType] nvarchar(50) NULL,
        [TenantID] int NULL,
        [DeviceID] int NULL,
        [Phone] nvarchar(50) NULL,
        [Email] nvarchar(50) NULL,
        [IdentificationNumber] nvarchar(50) NULL,
        [IsViewOnly] bit NOT NULL CONSTRAINT [DF_TblMRUser_IsViewOnly] DEFAULT(0),
        [CanManageTransactions] bit NOT NULL CONSTRAINT [DF_TblMRUser_CanManageTransactions] DEFAULT(0)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblMRUser_USName' AND [object_id] = OBJECT_ID(N'[dbo].[TblMRUser]'))
    CREATE UNIQUE INDEX [UX_TblMRUser_USName] ON [dbo].[TblMRUser]([USName]);
GO

IF OBJECT_ID(N'[dbo].[TblSettings]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblSettings](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblSettings] PRIMARY KEY,
        [SettingCode] nvarchar(100) NOT NULL,
        [SettingValue] nvarchar(max) NULL,
        [Description] nvarchar(500) NULL,
        [Created_Date] datetime NULL,
        [Updated_Date] datetime NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblSettings_SettingCode' AND [object_id] = OBJECT_ID(N'[dbo].[TblSettings]'))
    CREATE UNIQUE INDEX [UX_TblSettings_SettingCode] ON [dbo].[TblSettings]([SettingCode]);
GO

IF OBJECT_ID(N'[dbo].[TblAudit]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblAudit](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblAudit] PRIMARY KEY,
        [IDUser] int NULL,
        [LogDate] datetime NOT NULL CONSTRAINT [DF_TblAudit_LogDate] DEFAULT(GETDATE()),
        [LogAction] nvarchar(100) NOT NULL,
        [LogDetail] nvarchar(max) NULL,
        [IDDevice] int NULL
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblAuditLog]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblAuditLog](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblAuditLog] PRIMARY KEY,
        [UserId] int NULL,
        [DeviceId] int NULL,
        [LogAction] nvarchar(100) NOT NULL,
        [LogDetail] nvarchar(max) NULL,
        [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblAuditLog_Created_Date] DEFAULT(GETDATE())
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblSystemSetting]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblSystemSetting](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblSystemSetting] PRIMARY KEY,
        [Category] nvarchar(100) NOT NULL,
        [SettingCode] nvarchar(100) NOT NULL,
        [DisplayName] nvarchar(250) NOT NULL,
        [SettingValue] nvarchar(2000) NOT NULL CONSTRAINT [DF_TblSystemSetting_SettingValue] DEFAULT(N''),
        [IsSecret] bit NOT NULL CONSTRAINT [DF_TblSystemSetting_IsSecret] DEFAULT(0),
        [Description] nvarchar(500) NULL,
        [DisplayOrder] int NOT NULL CONSTRAINT [DF_TblSystemSetting_DisplayOrder] DEFAULT(0),
        [Created_Date] datetime NULL,
        [Created_By] nvarchar(100) NULL,
        [Updated_Date] datetime NULL,
        [Updated_By] nvarchar(100) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblSystemSetting_SettingCode' AND [object_id] = OBJECT_ID(N'[dbo].[TblSystemSetting]'))
    CREATE UNIQUE INDEX [UX_TblSystemSetting_SettingCode] ON [dbo].[TblSystemSetting]([SettingCode]);
GO

INSERT INTO [dbo].[TblSystemSetting]
    ([Category], [SettingCode], [DisplayName], [SettingValue], [IsSecret], [Description], [DisplayOrder], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
SELECT source.[Category], source.[SettingCode], source.[DisplayName], source.[SettingValue], source.[IsSecret], source.[Description], source.[DisplayOrder], GETDATE(), N'system', GETDATE(), N'system'
FROM (VALUES
    (N'System', N'system_default_currency', N'System default currency', N'VND', CAST(0 AS bit), N'Default reference currency used by billing and payment calculations.', 1),
    (N'9Pay', N'ninepay_transaction_fee_vnd', N'9Pay transaction fee (VND)', N'4400', CAST(0 AS bit), N'Transaction fee added to the QR payment total.', 2),
    (N'9Pay', N'ninepay_qr_expire_hours', N'9Pay QR expiry hours', N'72', CAST(0 AS bit), N'Number of hours a generated 9Pay QR remains valid.', 3),
    (N'Invoice', N'invoice_po_number', N'Invoice PO number', N'', CAST(0 AS bit), N'Optional PO number included in invoice messages sent to RabbitMQ.', 4),
    (N'Invoice', N'invoice_sequence_start', N'Invoice sequence start', N'00236', CAST(0 AS bit), N'Starting number for invoice codes. The sequence is padded to 5 digits.', 5),
    (N'KVH', N'kvh_auto_resume_enabled', N'KVH auto resume enabled', N'true', CAST(0 AS bit), N'Enable or disable automatic KVH resume when an invoice becomes paid.', 6)
) AS source([Category], [SettingCode], [DisplayName], [SettingValue], [IsSecret], [Description], [DisplayOrder])
WHERE NOT EXISTS (
    SELECT 1
    FROM [dbo].[TblSystemSetting] existing
    WHERE existing.[SettingCode] = source.[SettingCode]
);
GO

IF OBJECT_ID(N'[dbo].[TblCurrencyExchangeRate]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblCurrencyExchangeRate](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblCurrencyExchangeRate] PRIMARY KEY,
        [FromCurrency] nvarchar(10) NOT NULL,
        [ToCurrency] nvarchar(10) NOT NULL,
        [Rate] decimal(18,6) NOT NULL,
        [EffectiveDate] date NOT NULL,
        [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblCurrencyExchangeRate_Status] DEFAULT(N'active'),
        [Created_Date] datetime NULL,
        [Created_By] nvarchar(100) NULL,
        [Updated_Date] datetime NULL,
        [Updated_By] nvarchar(100) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblCurrencyExchangeRate_PairDate' AND [object_id] = OBJECT_ID(N'[dbo].[TblCurrencyExchangeRate]'))
    CREATE UNIQUE INDEX [UX_TblCurrencyExchangeRate_PairDate] ON [dbo].[TblCurrencyExchangeRate]([FromCurrency], [ToCurrency], [EffectiveDate]);
GO

IF OBJECT_ID(N'[dbo].[TblPricingPlan]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblPricingPlan](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblPricingPlan] PRIMARY KEY,
        [PlanName] nvarchar(250) NOT NULL,
        [PlanCode] nvarchar(100) NOT NULL,
        [CostPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_CostPrice] DEFAULT(0),
        [ResellerPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_ResellerPrice] DEFAULT(0),
        [FinalPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_FinalPrice] DEFAULT(0),
        [BaseData] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_BaseData] DEFAULT(0),
        [CostOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_CostOverChargePrice] DEFAULT(0),
        [ResellerOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_ResellerOverChargePrice] DEFAULT(0),
        [FinalOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPricingPlan_FinalOverChargePrice] DEFAULT(0),
        [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblPricingPlan_Status] DEFAULT(N'active'),
        [Created_Date] datetime NULL,
        [Created_By] nvarchar(50) NULL,
        [Updated_Date] datetime NULL,
        [Updated_By] nvarchar(50) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblPricingPlan_PlanCode' AND [object_id] = OBJECT_ID(N'[dbo].[TblPricingPlan]'))
    CREATE UNIQUE INDEX [UX_TblPricingPlan_PlanCode] ON [dbo].[TblPricingPlan]([PlanCode]);
GO

IF OBJECT_ID(N'[dbo].[TblTenantPricing]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTenantPricing](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenantPricing] PRIMARY KEY,
        [TenantId] int NOT NULL,
        [PricingPlanId] int NOT NULL,
        [ResellerPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblTenantPricing_ResellerPrice] DEFAULT(0),
        [FinalPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblTenantPricing_FinalPrice] DEFAULT(0),
        [ResellerOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblTenantPricing_ResellerOverChargePrice] DEFAULT(0),
        [FinalOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblTenantPricing_FinalOverChargePrice] DEFAULT(0),
        [Created_Date] datetime NULL,
        [Created_By] nvarchar(50) NULL,
        [Updated_Date] datetime NULL,
        [Updated_By] nvarchar(50) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblTenantPricing_Tenant_Plan' AND [object_id] = OBJECT_ID(N'[dbo].[TblTenantPricing]'))
    CREATE UNIQUE INDEX [UX_TblTenantPricing_Tenant_Plan] ON [dbo].[TblTenantPricing]([TenantId], [PricingPlanId]);
GO

IF OBJECT_ID(N'[dbo].[TblDevicePricing]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblDevicePricing](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblDevicePricing] PRIMARY KEY,
        [DeviceId] int NOT NULL,
        [TenantId] int NOT NULL,
        [PricingPlanId] int NOT NULL,
        [ResellerPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblDevicePricing_ResellerPrice] DEFAULT(0),
        [FinalPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblDevicePricing_FinalPrice] DEFAULT(0),
        [ResellerOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblDevicePricing_ResellerOverChargePrice] DEFAULT(0),
        [FinalOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblDevicePricing_FinalOverChargePrice] DEFAULT(0),
        [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblDevicePricing_Status] DEFAULT(N'active'),
        [Created_Date] datetime NULL,
        [Created_By] nvarchar(50) NULL,
        [Updated_Date] datetime NULL,
        [Updated_By] nvarchar(50) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblDevicePricing_Device_Plan' AND [object_id] = OBJECT_ID(N'[dbo].[TblDevicePricing]'))
    CREATE UNIQUE INDEX [UX_TblDevicePricing_Device_Plan] ON [dbo].[TblDevicePricing]([DeviceId], [PricingPlanId]);
GO

IF OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblMonthlySubscription](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblMonthlySubscription] PRIMARY KEY,
        [TenantId] int NOT NULL,
        [DeviceId] int NOT NULL,
        [PricingPlanId] int NOT NULL,
        [TenantName] nvarchar(250) NULL,
        [VesselName] nvarchar(250) NOT NULL,
        [KitId] nvarchar(250) NULL,
        [PlanName] nvarchar(250) NOT NULL,
        [PlanCode] nvarchar(100) NULL,
        [SubscriptionType] nvarchar(50) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_SubscriptionType] DEFAULT(N'SUBSCRIPTION'),
        [UsageMonth] date NOT NULL,
        [PurchasedDate] datetime NOT NULL CONSTRAINT [DF_TblMonthlySubscription_PurchasedDate] DEFAULT(GETDATE()),
        [StartDate] date NOT NULL,
        [EndDate] date NOT NULL,
        [NextBillingDate] date NULL,
        [DataLimitGb] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_DataLimitGb] DEFAULT(0),
        [BasePlanPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_BasePlanPrice] DEFAULT(0),
        [CostPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_CostPrice] DEFAULT(0),
        [SubscriptionDays] int NOT NULL CONSTRAINT [DF_TblMonthlySubscription_SubscriptionDays] DEFAULT(0),
        [SubscriptionPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_SubscriptionPrice] DEFAULT(0),
        [CostOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_CostOverChargePrice] DEFAULT(0),
        [OverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_OverChargePrice] DEFAULT(0),
        [TotalTopUpGb] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_TotalTopUpGb] DEFAULT(0),
        [TotalInvoiceAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_TotalInvoiceAmount] DEFAULT(0),
        [TotalPaid] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_TotalPaid] DEFAULT(0),
        [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_Status] DEFAULT(N'pending_payment'),
        [Created_Date] datetime NULL,
        [Created_By] nvarchar(50) NULL,
        [Updated_Date] datetime NULL,
        [Updated_By] nvarchar(50) NULL
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblSubscriptionInvoice]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblSubscriptionInvoice](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblSubscriptionInvoice] PRIMARY KEY,
        [SubscriptionId] int NOT NULL,
        [InvoiceNumber] nvarchar(100) NOT NULL,
        [ReceiptNumber] nvarchar(100) NULL,
        [PoNumber] nvarchar(100) NULL,
        [InvoiceType] nvarchar(50) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_InvoiceType] DEFAULT(N'SUBSCRIPTION'),
        [Description] nvarchar(500) NULL,
        [DataGb] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_DataGb] DEFAULT(0),
        [CostPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_CostPrice] DEFAULT(0),
        [BuyPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_BuyPrice] DEFAULT(0),
        [SalePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_SalePrice] DEFAULT(0),
        [MarginAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_MarginAmount] DEFAULT(0),
        [Amount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_Amount] DEFAULT(0),
        [PaidAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_PaidAmount] DEFAULT(0),
        [RefundAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_RefundAmount] DEFAULT(0),
        [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_Status] DEFAULT(N'pending'),
        [CreatedAt] datetime NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_CreatedAt] DEFAULT(GETDATE()),
        [CompletedAt] datetime NULL,
        [Created_Date] datetime NULL,
        [Created_By] nvarchar(50) NULL,
        [Updated_Date] datetime NULL,
        [Updated_By] nvarchar(50) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblSubscriptionInvoice_InvoiceNumber' AND [object_id] = OBJECT_ID(N'[dbo].[TblSubscriptionInvoice]'))
    CREATE UNIQUE INDEX [UX_TblSubscriptionInvoice_InvoiceNumber] ON [dbo].[TblSubscriptionInvoice]([InvoiceNumber]);
GO

IF OBJECT_ID(N'[dbo].[TblPaymentTransaction]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblPaymentTransaction](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblPaymentTransaction] PRIMARY KEY,
        [Provider] nvarchar(50) NOT NULL,
        [InvoiceId] int NULL,
        [SubscriptionId] int NULL,
        [InvoiceNumber] nvarchar(100) NOT NULL,
        [ProviderPaymentNo] nvarchar(100) NULL,
        [ProviderStatus] nvarchar(50) NULL,
        [Status] nvarchar(50) NOT NULL,
        [OrderAmountUsd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_OrderAmountUsd] DEFAULT(0),
        [ExchangeRateVndPerUsd] decimal(18,6) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_ExchangeRateVndPerUsd] DEFAULT(0),
        [ConvertedAmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_ConvertedAmountVnd] DEFAULT(0),
        [TransactionFeeVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_TransactionFeeVnd] DEFAULT(0),
        [AmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_AmountVnd] DEFAULT(0),
        [PaymentUrl] nvarchar(max) NULL,
        [Currency] nvarchar(10) NULL,
        [Method] nvarchar(50) NULL,
        [Description] nvarchar(500) NULL,
        [FailureReason] nvarchar(500) NULL,
        [RawResultBase64] nvarchar(max) NULL,
        [RawResultJson] nvarchar(max) NULL,
        [RawChecksum] nvarchar(200) NULL,
        [ChecksumValid] bit NOT NULL CONSTRAINT [DF_TblPaymentTransaction_ChecksumValid] DEFAULT(1),
        [ProviderCreatedAt] datetime NULL,
        [CompletedAt] datetime NULL,
        [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblPaymentTransaction_Created_Date] DEFAULT(GETDATE()),
        [Updated_Date] datetime NOT NULL CONSTRAINT [DF_TblPaymentTransaction_Updated_Date] DEFAULT(GETDATE())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblPaymentTransaction_ProviderPaymentNo' AND [object_id] = OBJECT_ID(N'[dbo].[TblPaymentTransaction]'))
    CREATE INDEX [IX_TblPaymentTransaction_ProviderPaymentNo] ON [dbo].[TblPaymentTransaction]([Provider], [ProviderPaymentNo]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblPaymentTransaction_InvoiceNumber' AND [object_id] = OBJECT_ID(N'[dbo].[TblPaymentTransaction]'))
    CREATE INDEX [IX_TblPaymentTransaction_InvoiceNumber] ON [dbo].[TblPaymentTransaction]([Provider], [InvoiceNumber]);
GO

IF OBJECT_ID(N'[dbo].[TblNinePayIpnLog]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblNinePayIpnLog](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblNinePayIpnLog] PRIMARY KEY,
        [ReceivedAt] datetime NOT NULL CONSTRAINT [DF_TblNinePayIpnLog_ReceivedAt] DEFAULT(GETUTCDATE()),
        [HttpMethod] nvarchar(10) NULL,
        [Path] nvarchar(300) NULL,
        [Source] nvarchar(50) NULL,
        [ProviderInvoiceNo] nvarchar(100) NULL,
        [PaymentNo] nvarchar(100) NULL,
        [ProviderStatus] nvarchar(50) NULL,
        [ProcessStatus] nvarchar(50) NULL,
        [ProcessMessage] nvarchar(500) NULL,
        [ResultBase64] nvarchar(max) NULL,
        [Checksum] nvarchar(200) NULL,
        [RawPayload] nvarchar(max) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblNinePayIpnLog_ProviderInvoiceNo' AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayIpnLog]'))
    CREATE INDEX [IX_TblNinePayIpnLog_ProviderInvoiceNo] ON [dbo].[TblNinePayIpnLog]([ProviderInvoiceNo], [ReceivedAt]);
GO

IF OBJECT_ID(N'[dbo].[TblNinePayQrSession]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblNinePayQrSession](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblNinePayQrSession] PRIMARY KEY,
        [InvoiceId] int NOT NULL,
        [SubscriptionId] int NOT NULL,
        [InvoiceNumber] nvarchar(100) NOT NULL,
        [ProviderInvoiceNo] nvarchar(100) NOT NULL,
        [ProviderPaymentNo] nvarchar(100) NULL,
        [ProviderStatus] nvarchar(50) NULL,
        [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Status] DEFAULT(N'Pending'),
        [AmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_AmountVnd] DEFAULT(0),
        [Currency] nvarchar(10) NULL,
        [Method] nvarchar(50) NULL,
        [Description] nvarchar(500) NULL,
        [Channel] nvarchar(50) NULL,
        [Created_By] nvarchar(100) NULL,
        [TransferFeeVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_TransferFeeVnd] DEFAULT(0),
        [BankAccountNo] nvarchar(100) NULL,
        [TransferContent] nvarchar(500) NULL,
        [IpnPaymentNo] nvarchar(100) NULL,
        [IpnReceivedAt] datetime NULL,
        [IpnProcessStatus] nvarchar(50) NULL,
        [IpnProcessMessage] nvarchar(500) NULL,
        [IpnChecksum] nvarchar(200) NULL,
        [IpnResultBase64] nvarchar(max) NULL,
        [IpnRawJson] nvarchar(max) NULL,
        [PaidAt] datetime NULL,
        [QrStartedAt] datetime NOT NULL,
        [QrExpiresAt] datetime NOT NULL,
        [ProviderResponseJson] nvarchar(max) NULL,
        [DebugLog] nvarchar(max) NULL,
        [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Created_Date] DEFAULT(GETDATE()),
        [Updated_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Updated_Date] DEFAULT(GETDATE())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblNinePayQrSession_Invoice_Active' AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayQrSession]'))
    CREATE INDEX [IX_TblNinePayQrSession_Invoice_Active] ON [dbo].[TblNinePayQrSession]([InvoiceId], [Status], [QrExpiresAt]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblNinePayQrSession_ProviderInvoiceNo' AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayQrSession]'))
    CREATE INDEX [IX_TblNinePayQrSession_ProviderInvoiceNo] ON [dbo].[TblNinePayQrSession]([ProviderInvoiceNo]);
GO

IF OBJECT_ID(N'[dbo].[TblNinePayQrSessionInvoice]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblNinePayQrSessionInvoice](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblNinePayQrSessionInvoice] PRIMARY KEY,
        [QrSessionId] int NOT NULL,
        [InvoiceId] int NOT NULL,
        [SubscriptionId] int NOT NULL,
        [InvoiceNumber] nvarchar(100) NOT NULL,
        [AmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_AmountVnd] DEFAULT(0),
        [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Status] DEFAULT(N'Pending'),
        [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Created_Date] DEFAULT(GETDATE()),
        [Updated_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Updated_Date] DEFAULT(GETDATE())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblNinePayQrSessionInvoice_QrSessionId' AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayQrSessionInvoice]'))
    CREATE INDEX [IX_TblNinePayQrSessionInvoice_QrSessionId] ON [dbo].[TblNinePayQrSessionInvoice]([QrSessionId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblNinePayQrSessionInvoice_Invoice_Active' AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayQrSessionInvoice]'))
    CREATE INDEX [IX_TblNinePayQrSessionInvoice_Invoice_Active] ON [dbo].[TblNinePayQrSessionInvoice]([InvoiceId], [Status]);
GO

IF OBJECT_ID(N'[dbo].[TblDeviceDataOptInHistory]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblDeviceDataOptInHistory](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblDeviceDataOptInHistory] PRIMARY KEY,
        [DeviceId] int NOT NULL,
        [UserId] int NULL,
        [PerformedBy] nvarchar(250) NOT NULL,
        [PerformedAtUtc] datetime2 NOT NULL,
        [OldStatus] bit NULL,
        [NewStatus] bit NOT NULL,
        [ApiSuccess] bit NOT NULL,
        [HttpStatusCode] int NULL,
        [ApiResponse] nvarchar(max) NULL,
        [JobId] nvarchar(200) NULL,
        [KvhCommandId] bigint NULL,
        [JobStatus] nvarchar(30) NULL,
        [VerificationStatus] nvarchar(30) NULL,
        [CompletedAtUtc] datetime2 NULL,
        [VerifiedAtUtc] datetime2 NULL
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblKvhCommand]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhCommand](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhCommand] PRIMARY KEY,
        [DeviceId] int NOT NULL,
        [TerminalId] nvarchar(200) NOT NULL,
        [KvhDeviceId] nvarchar(200) NULL,
        [TrafficId] nvarchar(200) NULL,
        [Region] nvarchar(120) NULL,
        [ScheduleId] nvarchar(200) NULL,
        [KvhSubscriptionId] bigint NULL,
        [CooldownUntilUtc] datetime2 NULL,
        [CommandType] nvarchar(50) NOT NULL,
        [RequestedValue] nvarchar(max) NULL,
        [JobId] nvarchar(200) NULL,
        [HttpStatusCode] int NULL,
        [CommandStatus] nvarchar(30) NOT NULL,
        [JobStatus] nvarchar(30) NULL,
        [VerificationStatus] nvarchar(30) NULL,
        [RequestJson] nvarchar(max) NULL,
        [SubmitResponseJson] nvarchar(max) NULL,
        [JobResponseJson] nvarchar(max) NULL,
        [VerificationResponseJson] nvarchar(max) NULL,
        [RequestedByUserId] int NULL,
        [RequestedBy] nvarchar(250) NULL,
        [RequestedAtUtc] datetime2 NOT NULL,
        [LastPolledAtUtc] datetime2 NULL,
        [NextPollAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [VerifiedAtUtc] datetime2 NULL,
        [PollCount] int NOT NULL CONSTRAINT [DF_TblKvhCommand_PollCount] DEFAULT(0),
        [MaxPollCount] int NOT NULL CONSTRAINT [DF_TblKvhCommand_MaxPollCount] DEFAULT(40),
        [ErrorCode] nvarchar(100) NULL,
        [ErrorMessage] nvarchar(max) NULL,
        [VerificationAttemptCount] int NOT NULL CONSTRAINT [DF_TblKvhCommand_VerificationAttemptCount] DEFAULT(0),
        [NextVerificationAtUtc] datetime2 NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblKvhCommand_JobStatus_NextPoll' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhCommand]'))
    CREATE INDEX [IX_TblKvhCommand_JobStatus_NextPoll] ON [dbo].[TblKvhCommand]([JobStatus], [NextPollAtUtc]) INCLUDE ([DeviceId], [CommandType], [JobId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblKvhCommand_DeviceId_RequestedAt' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhCommand]'))
    CREATE INDEX [IX_TblKvhCommand_DeviceId_RequestedAt] ON [dbo].[TblKvhCommand]([DeviceId], [RequestedAtUtc] DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblKvhCommand_JobId' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhCommand]'))
    CREATE UNIQUE INDEX [UX_TblKvhCommand_JobId] ON [dbo].[TblKvhCommand]([JobId]) WHERE [JobId] IS NOT NULL;
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscription]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscription](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSubscription] PRIMARY KEY,
        [DeviceId] int NOT NULL,
        [TerminalId] nvarchar(200) NOT NULL,
        [TrafficId] nvarchar(200) NOT NULL,
        [SubscriptionKey] nvarchar(450) NOT NULL,
        [Status] nvarchar(80) NULL,
        [PlanName] nvarchar(255) NULL,
        [PlanJson] nvarchar(max) NULL,
        [OptInStatus] nvarchar(80) NULL,
        [OptInJson] nvarchar(max) NULL,
        [ScheduledAction] nvarchar(120) NULL,
        [ScheduleId] nvarchar(200) NULL,
        [ScheduledEffectiveDateUtc] datetime2 NULL,
        [ScheduledCreatedAtUtc] datetime2 NULL,
        [ScheduledRawJson] nvarchar(max) NULL,
        [Region] nvarchar(120) NULL,
        [Proration] decimal(18,4) NULL,
        [AllowanceGb] decimal(18,4) NULL,
        [EffectiveDateUtc] datetime2 NULL,
        [RawSubscriptionJson] nvarchar(max) NULL,
        [IsCurrent] bit NOT NULL CONSTRAINT [DF_TblKvhSubscription_IsCurrent] DEFAULT(1),
        [FirstSeenAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubscription_FirstSeenAtUtc] DEFAULT(SYSUTCDATETIME()),
        [LastSeenAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubscription_LastSeenAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubscription_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblKvhSubscription_Device_Traffic_Region' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscription]'))
    CREATE UNIQUE INDEX [UX_TblKvhSubscription_Device_Traffic_Region] ON [dbo].[TblKvhSubscription]([DeviceId], [TrafficId], [Region]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblKvhSubscription_Device_Current' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscription]'))
    CREATE INDEX [IX_TblKvhSubscription_Device_Current] ON [dbo].[TblKvhSubscription]([DeviceId], [IsCurrent], [Region]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblKvhSubscription_Schedule_Effective' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscription]'))
    CREATE INDEX [IX_TblKvhSubscription_Schedule_Effective] ON [dbo].[TblKvhSubscription]([ScheduledAction], [ScheduledEffectiveDateUtc], [LastSeenAtUtc]) INCLUDE ([DeviceId], [TrafficId], [Region], [ScheduleId]);
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionSyncLog]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscriptionSyncLog](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSubscriptionSyncLog] PRIMARY KEY,
        [DeviceId] int NOT NULL,
        [TerminalId] nvarchar(200) NULL,
        [TrafficId] nvarchar(200) NULL,
        [StartedAtUtc] datetime2 NOT NULL,
        [CompletedAtUtc] datetime2 NULL,
        [Success] bit NOT NULL,
        [HttpStatusCode] int NULL,
        [SyncSource] nvarchar(50) NOT NULL CONSTRAINT [DF_TblKvhSubscriptionSyncLog_SyncSource] DEFAULT(N'PORTAL'),
        [ErrorCode] nvarchar(100) NULL,
        [ErrorMessage] nvarchar(max) NULL,
        [ResponseJson] nvarchar(max) NULL,
        [ReturnedCount] int NOT NULL CONSTRAINT [DF_TblKvhSubscriptionSyncLog_ReturnedCount] DEFAULT(0)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblKvhSubscriptionSyncLog_Device_Started' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscriptionSyncLog]'))
    CREATE INDEX [IX_TblKvhSubscriptionSyncLog_Device_Started] ON [dbo].[TblKvhSubscriptionSyncLog]([DeviceId], [StartedAtUtc] DESC);
GO

IF OBJECT_ID(N'[dbo].[TblKvhSyncBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSyncBatch](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSyncBatch] PRIMARY KEY,
        [BatchType] nvarchar(50) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [TenantId] int NULL,
        [TotalItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_TotalItems] DEFAULT(0),
        [PendingItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_PendingItems] DEFAULT(0),
        [RunningItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_RunningItems] DEFAULT(0),
        [SucceededItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_SucceededItems] DEFAULT(0),
        [FailedItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_FailedItems] DEFAULT(0),
        [SkippedItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_SkippedItems] DEFAULT(0),
        [RequestedByUserId] int NULL,
        [RequestedBy] nvarchar(250) NULL,
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [CancelRequestedAtUtc] datetime2 NULL,
        [ErrorMessage] nvarchar(max) NULL
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSyncBatchItem](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSyncBatchItem] PRIMARY KEY,
        [BatchId] bigint NOT NULL,
        [DeviceId] int NOT NULL,
        [TerminalId] nvarchar(200) NULL,
        [TrafficId] nvarchar(200) NULL,
        [Status] nvarchar(30) NOT NULL,
        [AttemptCount] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatchItem_AttemptCount] DEFAULT(0),
        [MaxAttemptCount] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatchItem_MaxAttemptCount] DEFAULT(3),
        [NextAttemptAtUtc] datetime2 NULL,
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [ReturnedCount] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatchItem_ReturnedCount] DEFAULT(0),
        [ErrorCode] nvarchar(100) NULL,
        [ErrorMessage] nvarchar(max) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblKvhSyncBatchItem_Batch_Device' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]'))
    CREATE UNIQUE INDEX [UX_TblKvhSyncBatchItem_Batch_Device] ON [dbo].[TblKvhSyncBatchItem]([BatchId], [DeviceId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblKvhSyncBatchItem_Status_NextAttempt' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]'))
    CREATE INDEX [IX_TblKvhSyncBatchItem_Status_NextAttempt] ON [dbo].[TblKvhSyncBatchItem]([Status], [NextAttemptAtUtc]) INCLUDE ([BatchId], [DeviceId]);
GO

IF OBJECT_ID(N'[dbo].[TblDeviceActivityLog]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblDeviceActivityLog](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblDeviceActivityLog] PRIMARY KEY,
        [DeviceId] int NOT NULL,
        [TenantId] int NULL,
        [Category] nvarchar(50) NOT NULL,
        [Action] nvarchar(100) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [OldValue] nvarchar(500) NULL,
        [NewValue] nvarchar(500) NULL,
        [Summary] nvarchar(500) NOT NULL,
        [DetailJson] nvarchar(max) NULL,
        [Source] nvarchar(50) NULL,
        [ActorType] nvarchar(30) NULL,
        [UserId] int NULL,
        [PerformedBy] nvarchar(250) NULL,
        [ReferenceType] nvarchar(50) NULL,
        [ReferenceId] nvarchar(100) NULL,
        [CorrelationId] nvarchar(100) NULL,
        [EventKey] nvarchar(250) NULL,
        [OccurredAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_OccurredAtUtc] DEFAULT(SYSUTCDATETIME()),
        [RecordedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_RecordedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_CreatedAtUtc] DEFAULT(SYSUTCDATETIME())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblDeviceActivityLog_Device_CreatedAtUtc' AND [object_id] = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_Device_CreatedAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [CreatedAtUtc] DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblDeviceActivityLog_Device_OccurredAtUtc' AND [object_id] = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_Device_OccurredAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [OccurredAtUtc] DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblDeviceActivityLog_CorrelationId' AND [object_id] = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_CorrelationId] ON [dbo].[TblDeviceActivityLog]([CorrelationId]) WHERE [CorrelationId] IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblDeviceActivityLog_EventKey' AND [object_id] = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE UNIQUE INDEX [UX_TblDeviceActivityLog_EventKey] ON [dbo].[TblDeviceActivityLog]([EventKey]) WHERE [EventKey] IS NOT NULL;
GO

IF OBJECT_ID(N'[dbo].[TblInvoicePdf]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblInvoicePdf](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblInvoicePdf] PRIMARY KEY,
        [InvoiceId] int NOT NULL,
        [InvoiceCode] nvarchar(100) NOT NULL,
        [FileName] nvarchar(255) NOT NULL,
        [OriginalFileName] nvarchar(255) NULL,
        [StorageKey] nvarchar(500) NOT NULL,
        [ContentType] nvarchar(100) NOT NULL,
        [FileSize] bigint NOT NULL,
        [Sha256] char(64) NOT NULL,
        [Version] int NOT NULL,
        [IsCurrent] bit NOT NULL CONSTRAINT [DF_TblInvoicePdf_IsCurrent] DEFAULT(1),
        [SourceSystem] nvarchar(100) NULL,
        [ExternalReference] nvarchar(200) NULL,
        [UploadedByUserId] int NULL,
        [UploadedBy] nvarchar(100) NULL,
        [UploadedAtUtc] datetime2(0) NOT NULL CONSTRAINT [DF_TblInvoicePdf_UploadedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(0) NOT NULL CONSTRAINT [DF_TblInvoicePdf_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [DeletedAtUtc] datetime2(0) NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_TblInvoicePdf_IsDeleted] DEFAULT(0)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoicePdf_InvoiceId' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]'))
    CREATE INDEX [IX_TblInvoicePdf_InvoiceId] ON [dbo].[TblInvoicePdf]([InvoiceId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblInvoicePdf_Current' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]'))
    CREATE UNIQUE INDEX [UX_TblInvoicePdf_Current] ON [dbo].[TblInvoicePdf]([InvoiceId], [IsCurrent]) WHERE [IsCurrent] = 1 AND [IsDeleted] = 0;
GO

IF OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblInvoiceIntegrationLog](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblInvoiceIntegrationLog] PRIMARY KEY,
        [InvoiceId] int NULL,
        [InvoiceCode] nvarchar(100) NOT NULL,
        [TransactionCode] nvarchar(200) NULL,
        [EventType] nvarchar(50) NOT NULL,
        [Direction] nvarchar(20) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [SourceSystem] nvarchar(100) NULL,
        [TargetSystem] nvarchar(100) NULL,
        [RabbitExchange] nvarchar(200) NULL,
        [RabbitRoutingKey] nvarchar(200) NULL,
        [RabbitQueue] nvarchar(200) NULL,
        [MessageId] nvarchar(200) NULL,
        [CorrelationId] nvarchar(200) NULL,
        [PayloadJson] nvarchar(max) NULL,
        [FileOriginalName] nvarchar(255) NULL,
        [FileStoredName] nvarchar(255) NULL,
        [FileSize] bigint NULL,
        [FileVersion] int NULL,
        [HttpStatusCode] int NULL,
        [ErrorCode] nvarchar(100) NULL,
        [ErrorMessage] nvarchar(max) NULL,
        [StartedAtUtc] datetime2(0) NOT NULL,
        [CompletedAtUtc] datetime2(0) NULL,
        [DurationMs] bigint NULL,
        [CreatedAtUtc] datetime2(0) NOT NULL CONSTRAINT [DF_TblInvoiceIntegrationLog_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [CreatedBy] nvarchar(100) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_InvoiceCode_CreatedAt' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
    CREATE INDEX [IX_TblInvoiceIntegrationLog_InvoiceCode_CreatedAt] ON [dbo].[TblInvoiceIntegrationLog]([InvoiceCode], [CreatedAtUtc] DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_MessageId' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
    CREATE INDEX [IX_TblInvoiceIntegrationLog_MessageId] ON [dbo].[TblInvoiceIntegrationLog]([MessageId]) WHERE [MessageId] IS NOT NULL;
GO

IF OBJECT_ID(N'[dbo].[TblTransactionReupImportBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTransactionReupImportBatch](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTransactionReupImportBatch] PRIMARY KEY,
        [BatchCode] nvarchar(100) NOT NULL,
        [SourceType] nvarchar(40) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_SourceType] DEFAULT(N'EXCEL_IMPORT'),
        [OriginalFileName] nvarchar(260) NULL,
        [StoredFileName] nvarchar(260) NULL,
        [StoredFilePath] nvarchar(500) NULL,
        [FileSize] bigint NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_FileSize] DEFAULT(0),
        [ContentType] nvarchar(200) NULL,
        [FileExtension] nvarchar(20) NULL,
        [FileSha256] varchar(64) NULL,
        [ImportedByUserId] int NULL,
        [ImportedByUsername] nvarchar(100) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_ImportedByUsername] DEFAULT(N''),
        [ImportedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_ImportedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [InvoiceStartNumber] int NULL,
        [InvoiceEndNumber] int NULL,
        [NextInvoiceNumber] int NULL,
        [TotalRows] int NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_TotalRows] DEFAULT(0),
        [ValidRows] int NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_ValidRows] DEFAULT(0),
        [PublishedRows] int NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_PublishedRows] DEFAULT(0),
        [FailedRows] int NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_FailedRows] DEFAULT(0),
        [SkippedRows] int NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_SkippedRows] DEFAULT(0),
        [DuplicateRows] int NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_DuplicateRows] DEFAULT(0),
        [Status] nvarchar(40) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_Status] DEFAULT(N'Pending'),
        [CreatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME())
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTransactionReupImportItem](
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTransactionReupImportItem] PRIMARY KEY,
        [BatchId] int NOT NULL,
        [SourceInvoiceId] int NULL,
        [RowNumber] int NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RowNumber] DEFAULT(0),
        [SourceTransactionCode] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_SourceTransactionCode] DEFAULT(N''),
        [SourceRequestCode] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_SourceRequestCode] DEFAULT(N''),
        [SourceOriginalRequestCode] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_SourceOriginalRequestCode] DEFAULT(N''),
        [SourceCreatedBy] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_SourceCreatedBy] DEFAULT(N''),
        [TransactionType] nvarchar(100) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_TransactionType] DEFAULT(N''),
        [PaymentMethod] nvarchar(100) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_PaymentMethod] DEFAULT(N''),
        [BankName] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_BankName] DEFAULT(N''),
        [GrossAmountVnd] decimal(19,2) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_GrossAmountVnd] DEFAULT(0),
        [ProcessingFeeVnd] decimal(19,2) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_ProcessingFeeVnd] DEFAULT(0),
        [TransferContent] nvarchar(1000) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_TransferContent] DEFAULT(N''),
        [FeeBearer] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_FeeBearer] DEFAULT(N''),
        [NetAmountVnd] decimal(19,2) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_NetAmountVnd] DEFAULT(0),
        [SourceStatus] nvarchar(100) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_SourceStatus] DEFAULT(N''),
        [SourceCreatedAt] datetime2(7) NULL,
        [SourceUpdatedAt] datetime2(7) NULL,
        [ValidationStatus] nvarchar(30) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_ValidationStatus] DEFAULT(N'Pending'),
        [PublishStatus] nvarchar(30) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_PublishStatus] DEFAULT(N'Pending'),
        [InvoiceYear] int NULL,
        [InvoiceSequence] int NULL,
        [InvoiceCode] nvarchar(100) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_InvoiceCode] DEFAULT(N''),
        [ExpectedPdfFileName] nvarchar(150) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_ExpectedPdfFileName] DEFAULT(N''),
        [PayloadJson] nvarchar(max) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_PayloadJson] DEFAULT(N''),
        [PdfFileName] nvarchar(260) NULL,
        [PdfStorageKey] nvarchar(500) NULL,
        [PdfSize] bigint NULL,
        [PdfSha256] varchar(64) NULL,
        [PdfContentType] nvarchar(100) NULL,
        [PdfReceivedAtUtc] datetime2(7) NULL,
        [ErrorCode] nvarchar(100) NULL,
        [ErrorMessage] nvarchar(max) NULL,
        [ProcessingStartedAtUtc] datetime2(7) NULL,
        [WaitingPdfAtUtc] datetime2(7) NULL,
        [CompletedAtUtc] datetime2(7) NULL,
        [RabbitMessageId] nvarchar(100) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RabbitMessageId] DEFAULT(N''),
        [RabbitCorrelationId] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RabbitCorrelationId] DEFAULT(N''),
        [RabbitExchange] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RabbitExchange] DEFAULT(N''),
        [RabbitRoutingKey] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RabbitRoutingKey] DEFAULT(N''),
        [RabbitQueue] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RabbitQueue] DEFAULT(N''),
        [PublishMessage] nvarchar(max) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_PublishMessage] DEFAULT(N''),
        [PublishLogs] nvarchar(max) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_PublishLogs] DEFAULT(N''),
        [PublishAttemptCount] int NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_PublishAttemptCount] DEFAULT(0),
        [PublishedAtUtc] datetime2(7) NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblTransactionReupImportBatch_BatchCode' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportBatch]'))
    CREATE UNIQUE INDEX [UX_TblTransactionReupImportBatch_BatchCode] ON [dbo].[TblTransactionReupImportBatch]([BatchCode]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblTransactionReupImportItem_BatchId_RowNumber' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
    CREATE INDEX [IX_TblTransactionReupImportItem_BatchId_RowNumber] ON [dbo].[TblTransactionReupImportItem]([BatchId], [RowNumber], [ID]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblTransactionReupImportItem_BatchId_PublishStatus' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
    CREATE INDEX [IX_TblTransactionReupImportItem_BatchId_PublishStatus] ON [dbo].[TblTransactionReupImportItem]([BatchId], [PublishStatus], [ID]);
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscriptionOperationBatch](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSubscriptionOperationBatch] PRIMARY KEY,
        [BatchCode] nvarchar(50) NOT NULL,
        [BatchName] nvarchar(250) NOT NULL,
        [OperationType] nvarchar(30) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [TenantId] int NULL,
        [Description] nvarchar(max) NULL,
        [ScheduledStartAtUtc] datetime2 NULL,
        [TotalItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_TotalItems] DEFAULT(0),
        [DraftItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_DraftItems] DEFAULT(0),
        [ReadyItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_ReadyItems] DEFAULT(0),
        [QueuedItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_QueuedItems] DEFAULT(0),
        [SubmittingItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_SubmittingItems] DEFAULT(0),
        [PendingItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_PendingItems] DEFAULT(0),
        [JobSuccessItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_JobSuccessItems] DEFAULT(0),
        [JobFailedItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_JobFailedItems] DEFAULT(0),
        [VerifyingItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_VerifyingItems] DEFAULT(0),
        [VerifiedItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_VerifiedItems] DEFAULT(0),
        [VerificationMismatchItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_VerificationMismatchItems] DEFAULT(0),
        [SkippedItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_SkippedItems] DEFAULT(0),
        [CancelledItems] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_CancelledItems] DEFAULT(0),
        [RequestedByUserId] int NULL,
        [RequestedBy] nvarchar(250) NULL,
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubOperationBatch_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [CancelRequestedAtUtc] datetime2 NULL,
        [ErrorMessage] nvarchar(max) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [UQ_TblKvhSubOperationBatch_BatchCode] UNIQUE ([BatchCode])
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscriptionOperationItem](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSubscriptionOperationItem] PRIMARY KEY,
        [BatchId] bigint NOT NULL,
        [DeviceId] int NULL,
        [KitNumber] nvarchar(200) NOT NULL,
        [KitNumberNormalized] AS UPPER(LTRIM(RTRIM([KitNumber]))) PERSISTED,
        [TerminalId] nvarchar(200) NULL,
        [TrafficId] nvarchar(200) NULL,
        [Region] nvarchar(100) NULL,
        [KvhSubscriptionId] bigint NULL,
        [OperationType] nvarchar(30) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [KvhCommandId] bigint NULL,
        [JobId] nvarchar(200) NULL,
        [JobStatus] nvarchar(40) NULL,
        [VerificationStatus] nvarchar(40) NULL,
        [AttemptCount] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationItem_AttemptCount] DEFAULT(0),
        [MaxAttemptCount] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationItem_MaxAttemptCount] DEFAULT(3),
        [PollCount] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationItem_PollCount] DEFAULT(0),
        [VerificationAttemptCount] int NOT NULL CONSTRAINT [DF_TblKvhSubOperationItem_VerificationAttemptCount] DEFAULT(0),
        [NextSubmitAtUtc] datetime2 NULL,
        [NextPollAtUtc] datetime2 NULL,
        [NextVerificationAtUtc] datetime2 NULL,
        [ImportedRowNumber] int NULL,
        [ImportSource] nvarchar(50) NULL,
        [Note] nvarchar(max) NULL,
        [ErrorCode] nvarchar(100) NULL,
        [ErrorMessage] nvarchar(max) NULL,
        [HttpStatusCode] int NULL,
        [SubmitResponseJson] nvarchar(max) NULL,
        [OperationLogJson] nvarchar(max) NULL,
        [CurrentSubscriptionStatus] nvarchar(80) NULL,
        [CurrentScheduledAction] nvarchar(120) NULL,
        [CurrentScheduleId] nvarchar(200) NULL,
        [CurrentScheduledEffectiveDateUtc] datetime2 NULL,
        [CurrentScheduledCreatedAtUtc] datetime2 NULL,
        [OperationStatus] nvarchar(80) NULL,
        [LastSubscriptionCheckedAtUtc] datetime2 NULL,
        [ReconciliationStatus] nvarchar(80) NULL,
        [ReconciliationMessage] nvarchar(max) NULL,
        [SubscriptionResponseJson] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubOperationItem_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubOperationItem_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [SubmittedAtUtc] datetime2 NULL,
        [JobCompletedAtUtc] datetime2 NULL,
        [VerifiedAtUtc] datetime2 NULL,
        CONSTRAINT [UQ_KvhSubOperationItem_Batch_Kit] UNIQUE ([BatchId], [KitNumberNormalized])
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationAudit]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscriptionOperationAudit](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSubscriptionOperationAudit] PRIMARY KEY,
        [BatchId] bigint NULL,
        [ItemId] bigint NULL,
        [Action] nvarchar(80) NOT NULL,
        [PerformedByUserId] int NULL,
        [PerformedBy] nvarchar(250) NULL,
        [Message] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubscriptionOperationAudit_CreatedAtUtc] DEFAULT(SYSUTCDATETIME())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_KvhSubOperationItem_Status_NextSubmit' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]'))
    CREATE INDEX [IX_KvhSubOperationItem_Status_NextSubmit] ON [dbo].[TblKvhSubscriptionOperationItem]([Status], [NextSubmitAtUtc]) INCLUDE ([BatchId], [DeviceId], [KvhCommandId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_KvhSubOperationItem_WaitingEffective' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]'))
    CREATE INDEX [IX_KvhSubOperationItem_WaitingEffective] ON [dbo].[TblKvhSubscriptionOperationItem]([Status], [NextVerificationAtUtc]) INCLUDE ([BatchId], [DeviceId], [TrafficId], [Region], [KvhSubscriptionId]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TblDevices_TblTenant')
    ALTER TABLE [dbo].[TblDevices] WITH NOCHECK ADD CONSTRAINT [FK_TblDevices_TblTenant] FOREIGN KEY ([TenantID]) REFERENCES [dbo].[TblTenant]([ID]);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TblTenantPricing_TblTenant')
    ALTER TABLE [dbo].[TblTenantPricing] WITH NOCHECK ADD CONSTRAINT [FK_TblTenantPricing_TblTenant] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[TblTenant]([ID]);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TblTenantPricing_TblPricingPlan')
    ALTER TABLE [dbo].[TblTenantPricing] WITH NOCHECK ADD CONSTRAINT [FK_TblTenantPricing_TblPricingPlan] FOREIGN KEY ([PricingPlanId]) REFERENCES [dbo].[TblPricingPlan]([ID]);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TblTransactionReupImportItem_Batch')
    ALTER TABLE [dbo].[TblTransactionReupImportItem] WITH NOCHECK ADD CONSTRAINT [FK_TblTransactionReupImportItem_Batch] FOREIGN KEY ([BatchId]) REFERENCES [dbo].[TblTransactionReupImportBatch]([ID]) ON DELETE CASCADE;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_KvhSyncBatchItem_Batch')
    ALTER TABLE [dbo].[TblKvhSyncBatchItem] WITH NOCHECK ADD CONSTRAINT [FK_KvhSyncBatchItem_Batch] FOREIGN KEY ([BatchId]) REFERENCES [dbo].[TblKvhSyncBatch]([ID]) ON DELETE CASCADE;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_KvhSubOperationItem_Batch')
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] WITH NOCHECK ADD CONSTRAINT [FK_KvhSubOperationItem_Batch] FOREIGN KEY ([BatchId]) REFERENCES [dbo].[TblKvhSubscriptionOperationBatch]([ID]);
GO

IF OBJECT_ID(N'[dbo].[TblTenantCommissionPayment]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTenantCommissionPayment](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenantCommissionPayment] PRIMARY KEY,
        [TenantId] int NOT NULL,
        [PaymentDate] date NOT NULL,
        [PeriodFrom] date NULL,
        [PeriodTo] date NULL,
        [Amount] decimal(18,2) NOT NULL,
        [SourceMode] nvarchar(30) NOT NULL,
        [Note] nvarchar(1000) NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_TblTenantCommissionPayment_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        [CreatedByUserId] int NULL,
        [CreatedBy] nvarchar(250) NULL,
        CONSTRAINT [CK_TblTenantCommissionPayment_Amount] CHECK ([Amount] > 0),
        CONSTRAINT [CK_TblTenantCommissionPayment_Period] CHECK ([PeriodFrom] IS NULL OR [PeriodTo] IS NULL OR [PeriodFrom] <= [PeriodTo]),
        CONSTRAINT [CK_TblTenantCommissionPayment_SourceMode] CHECK ([SourceMode] IN (N'manual', N'billing_cycles'))
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblTenantCommissionPaymentItem]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTenantCommissionPaymentItem](
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenantCommissionPaymentItem] PRIMARY KEY,
        [PaymentId] bigint NOT NULL,
        [SubscriptionId] int NOT NULL,
        [CommissionAmount] decimal(18,2) NOT NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_TblTenantCommissionPaymentItem_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [CK_TblTenantCommissionPaymentItem_CommissionAmount] CHECK ([CommissionAmount] > 0)
    );
END;
GO

IF OBJECT_ID(N'[dbo].[TblTenant]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TenantCommissionPayment_Tenant')
    ALTER TABLE [dbo].[TblTenantCommissionPayment] WITH NOCHECK ADD CONSTRAINT [FK_TenantCommissionPayment_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[TblTenant]([ID]);
GO

IF OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TenantCommissionPaymentItem_Subscription')
    ALTER TABLE [dbo].[TblTenantCommissionPaymentItem] WITH NOCHECK ADD CONSTRAINT [FK_TenantCommissionPaymentItem_Subscription] FOREIGN KEY ([SubscriptionId]) REFERENCES [dbo].[TblMonthlySubscription]([ID]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TenantCommissionPaymentItem_Payment')
    ALTER TABLE [dbo].[TblTenantCommissionPaymentItem] WITH NOCHECK ADD CONSTRAINT [FK_TenantCommissionPaymentItem_Payment] FOREIGN KEY ([PaymentId]) REFERENCES [dbo].[TblTenantCommissionPayment]([ID]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TenantCommissionPaymentItem_SubscriptionId' AND [object_id] = OBJECT_ID(N'[dbo].[TblTenantCommissionPaymentItem]'))
    CREATE UNIQUE INDEX [UX_TenantCommissionPaymentItem_SubscriptionId] ON [dbo].[TblTenantCommissionPaymentItem]([SubscriptionId]);
GO

PRINT N'ShipNet schema rebuild completed. Create users, tenants, devices, pricing plans, KVH credentials, and historical data separately.';
GO
