SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[dbo].[TblTransactionReupImportBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblTransactionReupImportBatch]
    (
        [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblTransactionReupImportBatch] PRIMARY KEY,
        [BatchCode] nvarchar(100) NOT NULL,
        [SourceType] nvarchar(40) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_SourceType] DEFAULT(N'EXCEL_IMPORT'),
        [OriginalFileName] nvarchar(260) NULL CONSTRAINT [DF_TblTransactionReupImportBatch_OriginalFileName] DEFAULT(N''),
        [StoredFileName] nvarchar(260) NULL CONSTRAINT [DF_TblTransactionReupImportBatch_StoredFileName] DEFAULT(N''),
        [StoredFilePath] nvarchar(500) NULL CONSTRAINT [DF_TblTransactionReupImportBatch_StoredFilePath] DEFAULT(N''),
        [FileSize] bigint NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_FileSize] DEFAULT(0),
        [ContentType] nvarchar(200) NULL CONSTRAINT [DF_TblTransactionReupImportBatch_ContentType] DEFAULT(N''),
        [FileExtension] nvarchar(20) NULL CONSTRAINT [DF_TblTransactionReupImportBatch_FileExtension] DEFAULT(N''),
        [FileSha256] varchar(64) NULL CONSTRAINT [DF_TblTransactionReupImportBatch_FileSha256] DEFAULT(''),
        [ImportedByUserId] int NULL,
        [ImportedByUsername] nvarchar(100) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_ImportedByUsername] DEFAULT(N''),
        [ImportedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_ImportedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [InvoiceStartNumber] int NULL CONSTRAINT [DF_TblTransactionReupImportBatch_InvoiceStartNumber] DEFAULT(0),
        [InvoiceEndNumber] int NULL CONSTRAINT [DF_TblTransactionReupImportBatch_InvoiceEndNumber] DEFAULT(0),
        [NextInvoiceNumber] int NULL CONSTRAINT [DF_TblTransactionReupImportBatch_NextInvoiceNumber] DEFAULT(0),
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
    CREATE TABLE [dbo].[TblTransactionReupImportItem]
    (
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

IF COL_LENGTH(N'dbo.TblTransactionReupImportBatch', N'FileSize') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ADD [FileSize] bigint NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_FileSize_Add] DEFAULT(0);
IF COL_LENGTH(N'dbo.TblTransactionReupImportBatch', N'FileSha256') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ADD [FileSha256] varchar(64) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_FileSha256_Add] DEFAULT('');
IF COL_LENGTH(N'dbo.TblTransactionReupImportBatch', N'SourceType') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ADD [SourceType] nvarchar(40) NOT NULL CONSTRAINT [DF_TblTransactionReupImportBatch_SourceType_Add] DEFAULT(N'EXCEL_IMPORT');
GO

IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'SourceOriginalRequestCode') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [SourceOriginalRequestCode] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_SourceOriginalRequestCode_Add] DEFAULT(N'');
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'SourceCreatedBy') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [SourceCreatedBy] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_SourceCreatedBy_Add] DEFAULT(N'');
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'BankName') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [BankName] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_BankName_Add] DEFAULT(N'');
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'NetAmountVnd') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [NetAmountVnd] decimal(19,2) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_NetAmountVnd_Add] DEFAULT(0);
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'RabbitExchange') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [RabbitExchange] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RabbitExchange_Add] DEFAULT(N'');
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'RabbitRoutingKey') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [RabbitRoutingKey] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RabbitRoutingKey_Add] DEFAULT(N'');
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'RabbitQueue') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [RabbitQueue] nvarchar(250) NOT NULL CONSTRAINT [DF_TblTransactionReupImportItem_RabbitQueue_Add] DEFAULT(N'');
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'SourceInvoiceId') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [SourceInvoiceId] int NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'PdfFileName') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [PdfFileName] nvarchar(260) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'PdfStorageKey') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [PdfStorageKey] nvarchar(500) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'PdfSize') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [PdfSize] bigint NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'PdfSha256') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [PdfSha256] varchar(64) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'PdfContentType') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [PdfContentType] nvarchar(100) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'PdfReceivedAtUtc') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [PdfReceivedAtUtc] datetime2(7) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'ErrorCode') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [ErrorCode] nvarchar(100) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'ErrorMessage') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [ErrorMessage] nvarchar(max) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'ProcessingStartedAtUtc') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [ProcessingStartedAtUtc] datetime2(7) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'WaitingPdfAtUtc') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [WaitingPdfAtUtc] datetime2(7) NULL;
IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'CompletedAtUtc') IS NULL
    ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [CompletedAtUtc] datetime2(7) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TblTransactionReupImportItem_Batch')
BEGIN
    ALTER TABLE [dbo].[TblTransactionReupImportItem]
    ADD CONSTRAINT [FK_TblTransactionReupImportItem_Batch]
        FOREIGN KEY ([BatchId]) REFERENCES [dbo].[TblTransactionReupImportBatch]([ID]) ON DELETE CASCADE;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblTransactionReupImportBatch_BatchCode' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportBatch]'))
    CREATE UNIQUE INDEX [UX_TblTransactionReupImportBatch_BatchCode] ON [dbo].[TblTransactionReupImportBatch]([BatchCode]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblTransactionReupImportItem_BatchId_RowNumber' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
    CREATE INDEX [IX_TblTransactionReupImportItem_BatchId_RowNumber] ON [dbo].[TblTransactionReupImportItem]([BatchId], [RowNumber], [ID]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblTransactionReupImportItem_BatchId_PublishStatus' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
    CREATE INDEX [IX_TblTransactionReupImportItem_BatchId_PublishStatus] ON [dbo].[TblTransactionReupImportItem]([BatchId], [PublishStatus], [ID]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblTransactionReupImportItem_SourceInvoiceId' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
    CREATE INDEX [IX_TblTransactionReupImportItem_SourceInvoiceId] ON [dbo].[TblTransactionReupImportItem]([SourceInvoiceId]) WHERE [SourceInvoiceId] IS NOT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblTransactionReupImportItem_PublishedTransaction' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
    DROP INDEX [UX_TblTransactionReupImportItem_PublishedTransaction] ON [dbo].[TblTransactionReupImportItem];
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblTransactionReupImportItem_PublishedInvoiceCode' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
    DROP INDEX [UX_TblTransactionReupImportItem_PublishedInvoiceCode] ON [dbo].[TblTransactionReupImportItem];
GO
