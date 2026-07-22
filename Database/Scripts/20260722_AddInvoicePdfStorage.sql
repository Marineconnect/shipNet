/*
    Add invoice PDF metadata storage for ShipNet invoice integration.

    Rollback notes:
    - Drop UX_TblInvoicePdf_Current, UX_TblInvoicePdf_Version, IX_TblInvoicePdf_* first.
    - Drop dbo.TblInvoicePdf only if stored PDF metadata is no longer needed.
    - Physical PDF files under InvoicePdfStorage:RootPath are not removed by this rollback.
*/

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

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TblInvoicePdf_InvoiceId'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]')
)
    CREATE INDEX [IX_TblInvoicePdf_InvoiceId] ON [dbo].[TblInvoicePdf]([InvoiceId]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TblInvoicePdf_InvoiceCode'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]')
)
    CREATE INDEX [IX_TblInvoicePdf_InvoiceCode] ON [dbo].[TblInvoicePdf]([InvoiceCode]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TblInvoicePdf_Sha256'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]')
)
    CREATE INDEX [IX_TblInvoicePdf_Sha256] ON [dbo].[TblInvoicePdf]([Sha256]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_TblInvoicePdf_Current'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]')
)
    CREATE UNIQUE INDEX [UX_TblInvoicePdf_Current]
    ON [dbo].[TblInvoicePdf]([InvoiceId])
    WHERE [IsCurrent] = 1 AND [IsDeleted] = 0;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_TblInvoicePdf_Version'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]')
)
    CREATE UNIQUE INDEX [UX_TblInvoicePdf_Version]
    ON [dbo].[TblInvoicePdf]([InvoiceId], [Version]);
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = N'FK_TblInvoicePdf_TblSubscriptionInvoice'
      AND [parent_object_id] = OBJECT_ID(N'[dbo].[TblInvoicePdf]')
)
BEGIN
    ALTER TABLE [dbo].[TblInvoicePdf] WITH NOCHECK
    ADD CONSTRAINT [FK_TblInvoicePdf_TblSubscriptionInvoice]
    FOREIGN KEY ([InvoiceId]) REFERENCES [dbo].[TblSubscriptionInvoice]([ID]);
END;
GO

SELECT
    OBJECT_ID(N'[dbo].[TblInvoicePdf]', N'U') AS TblInvoicePdfObjectId,
    COUNT(1) AS CurrentPdfCount
FROM [dbo].[TblInvoicePdf]
WHERE [IsCurrent] = 1 AND [IsDeleted] = 0;
GO
