SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[dbo].[TblTransactionReupImportBatch]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.TblTransactionReupImportBatch', N'SourceType') IS NULL
    BEGIN
        ALTER TABLE [dbo].[TblTransactionReupImportBatch]
            ADD [SourceType] nvarchar(40) NOT NULL
                CONSTRAINT [DF_TblTransactionReupImportBatch_SourceType] DEFAULT(N'EXCEL_IMPORT');
    END;

    UPDATE [dbo].[TblTransactionReupImportBatch]
    SET [SourceType] = N'EXCEL_IMPORT'
    WHERE NULLIF([SourceType], N'') IS NULL;

    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [OriginalFileName] nvarchar(260) NULL;
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [StoredFileName] nvarchar(260) NULL;
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [StoredFilePath] nvarchar(500) NULL;
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [ContentType] nvarchar(200) NULL;
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [FileExtension] nvarchar(20) NULL;
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [FileSha256] varchar(64) NULL;
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [InvoiceStartNumber] int NULL;
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [InvoiceEndNumber] int NULL;
    ALTER TABLE [dbo].[TblTransactionReupImportBatch] ALTER COLUMN [NextInvoiceNumber] int NULL;
END;
GO

IF OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.TblTransactionReupImportItem', N'SourceInvoiceId') IS NULL
    BEGIN
        ALTER TABLE [dbo].[TblTransactionReupImportItem] ADD [SourceInvoiceId] int NULL;
    END;
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
END;
GO

IF OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblTransactionReupImportItem_BatchId_PublishStatus' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
BEGIN
    CREATE INDEX [IX_TblTransactionReupImportItem_BatchId_PublishStatus]
        ON [dbo].[TblTransactionReupImportItem]([BatchId], [PublishStatus], [ID]);
END;
GO

IF OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblTransactionReupImportItem_SourceInvoiceId' AND [object_id] = OBJECT_ID(N'[dbo].[TblTransactionReupImportItem]'))
BEGIN
    CREATE INDEX [IX_TblTransactionReupImportItem_SourceInvoiceId]
        ON [dbo].[TblTransactionReupImportItem]([SourceInvoiceId])
        WHERE [SourceInvoiceId] IS NOT NULL;
END;
GO
