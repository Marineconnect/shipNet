/*
    Adds persistent integration audit logs for invoice RabbitMQ publish and PDF receive/upload events.

    Safe to run more than once.
    Rollback note:
      DROP TABLE [dbo].[TblInvoiceIntegrationLog];
    Only run rollback after exporting any audit data that must be retained.
*/

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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_InvoiceId_CreatedAt' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
    CREATE INDEX [IX_TblInvoiceIntegrationLog_InvoiceId_CreatedAt] ON [dbo].[TblInvoiceIntegrationLog]([InvoiceId], [CreatedAtUtc] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_InvoiceCode_CreatedAt' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
    CREATE INDEX [IX_TblInvoiceIntegrationLog_InvoiceCode_CreatedAt] ON [dbo].[TblInvoiceIntegrationLog]([InvoiceCode], [CreatedAtUtc] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_EventType_CreatedAt' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
    CREATE INDEX [IX_TblInvoiceIntegrationLog_EventType_CreatedAt] ON [dbo].[TblInvoiceIntegrationLog]([EventType], [CreatedAtUtc] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_MessageId' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
    CREATE INDEX [IX_TblInvoiceIntegrationLog_MessageId] ON [dbo].[TblInvoiceIntegrationLog]([MessageId]) WHERE [MessageId] IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_CorrelationId' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
    CREATE INDEX [IX_TblInvoiceIntegrationLog_CorrelationId] ON [dbo].[TblInvoiceIntegrationLog]([CorrelationId]) WHERE [CorrelationId] IS NOT NULL;
