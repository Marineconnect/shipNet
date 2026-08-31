SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'[dbo].[TblTenantCommissionPayment]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTenantCommissionPayment]
    (
        [ID] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenantCommissionPayment] PRIMARY KEY,
        [TenantId] INT NOT NULL,
        [PaymentDate] DATE NOT NULL,
        [PeriodFrom] DATE NULL,
        [PeriodTo] DATE NULL,
        [Amount] DECIMAL(18,2) NOT NULL,
        [SourceMode] NVARCHAR(20) NOT NULL,
        [Note] NVARCHAR(1000) NULL,
        [CreatedAt] DATETIME2(0) NOT NULL CONSTRAINT [DF_TblTenantCommissionPayment_CreatedAt] DEFAULT (SYSDATETIME()),
        [CreatedByUserId] INT NULL,
        [CreatedBy] NVARCHAR(250) NULL,
        CONSTRAINT [FK_TblTenantCommissionPayment_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[TblTenant]([ID]),
        CONSTRAINT [CK_TblTenantCommissionPayment_Amount] CHECK ([Amount] > 0),
        CONSTRAINT [CK_TblTenantCommissionPayment_Period] CHECK ([PeriodFrom] IS NULL OR [PeriodTo] IS NULL OR [PeriodFrom] <= [PeriodTo]),
        CONSTRAINT [CK_TblTenantCommissionPayment_SourceMode] CHECK ([SourceMode] IN (N'manual', N'billing_cycles'))
    );

    CREATE INDEX [IX_TblTenantCommissionPayment_TenantDate]
        ON [dbo].[TblTenantCommissionPayment]([TenantId], [PaymentDate] DESC, [ID] DESC);
END;

IF OBJECT_ID(N'[dbo].[TblTenantCommissionPaymentItem]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTenantCommissionPaymentItem]
    (
        [ID] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTenantCommissionPaymentItem] PRIMARY KEY,
        [PaymentId] BIGINT NOT NULL,
        [SubscriptionId] INT NOT NULL,
        [CommissionAmount] DECIMAL(18,2) NOT NULL,
        [CreatedAt] DATETIME2(0) NOT NULL CONSTRAINT [DF_TblTenantCommissionPaymentItem_CreatedAt] DEFAULT (SYSDATETIME()),
        CONSTRAINT [FK_TblTenantCommissionPaymentItem_Payment] FOREIGN KEY ([PaymentId]) REFERENCES [dbo].[TblTenantCommissionPayment]([ID]) ON DELETE CASCADE,
        CONSTRAINT [FK_TblTenantCommissionPaymentItem_Subscription] FOREIGN KEY ([SubscriptionId]) REFERENCES [dbo].[TblMonthlySubscription]([ID]),
        CONSTRAINT [CK_TblTenantCommissionPaymentItem_CommissionAmount] CHECK ([CommissionAmount] >= 0),
        CONSTRAINT [UQ_TblTenantCommissionPaymentItem_SubscriptionId] UNIQUE ([SubscriptionId])
    );

    CREATE INDEX [IX_TblTenantCommissionPaymentItem_PaymentId]
        ON [dbo].[TblTenantCommissionPaymentItem]([PaymentId]);
END;

COMMIT TRANSACTION;
