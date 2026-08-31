SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
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
BEGIN
    ALTER TABLE [dbo].[TblTenantCommissionPayment] WITH NOCHECK
    ADD CONSTRAINT [FK_TenantCommissionPayment_Tenant]
        FOREIGN KEY ([TenantId]) REFERENCES [dbo].[TblTenant]([ID]);
END;
GO

IF OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TenantCommissionPaymentItem_Subscription')
BEGIN
    ALTER TABLE [dbo].[TblTenantCommissionPaymentItem] WITH NOCHECK
    ADD CONSTRAINT [FK_TenantCommissionPaymentItem_Subscription]
        FOREIGN KEY ([SubscriptionId]) REFERENCES [dbo].[TblMonthlySubscription]([ID]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TenantCommissionPaymentItem_Payment')
BEGIN
    ALTER TABLE [dbo].[TblTenantCommissionPaymentItem]
    ADD CONSTRAINT [FK_TenantCommissionPaymentItem_Payment]
        FOREIGN KEY ([PaymentId]) REFERENCES [dbo].[TblTenantCommissionPayment]([ID]) ON DELETE CASCADE;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TenantCommissionPaymentItem_SubscriptionId' AND [object_id] = OBJECT_ID(N'[dbo].[TblTenantCommissionPaymentItem]'))
BEGIN
    CREATE UNIQUE INDEX [UX_TenantCommissionPaymentItem_SubscriptionId]
        ON [dbo].[TblTenantCommissionPaymentItem]([SubscriptionId]);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TenantCommissionPayment_Tenant_PaymentDate' AND [object_id] = OBJECT_ID(N'[dbo].[TblTenantCommissionPayment]'))
BEGIN
    CREATE INDEX [IX_TenantCommissionPayment_Tenant_PaymentDate]
        ON [dbo].[TblTenantCommissionPayment]([TenantId], [PaymentDate] DESC, [ID] DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TenantCommissionPayment_SourceMode' AND [object_id] = OBJECT_ID(N'[dbo].[TblTenantCommissionPayment]'))
BEGIN
    CREATE INDEX [IX_TenantCommissionPayment_SourceMode]
        ON [dbo].[TblTenantCommissionPayment]([SourceMode], [CreatedAt] DESC);
END;
GO
