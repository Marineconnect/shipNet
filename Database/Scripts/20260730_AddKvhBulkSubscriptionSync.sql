/*
    Add KVH bulk subscription synchronization queue tables.
    - Idempotent.
    - Does not drop tables.
    - Does not delete existing data.
    - Run manually before enabling the bulk worker in production.
*/

IF OBJECT_ID(N'[dbo].[TblKvhSyncBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSyncBatch]
    (
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSyncBatch] PRIMARY KEY,
        [BatchType] nvarchar(50) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [TenantId] int NULL,
        [TotalItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_TotalItems] DEFAULT(0),
        [PendingItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_PendingItems] DEFAULT(0),
        [ProcessingItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_ProcessingItems] DEFAULT(0),
        [SuccessItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_SuccessItems] DEFAULT(0),
        [FailedItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_FailedItems] DEFAULT(0),
        [EmptyItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_EmptyItems] DEFAULT(0),
        [SkippedItems] int NOT NULL CONSTRAINT [DF_TblKvhSyncBatch_SkippedItems] DEFAULT(0),
        [RequestedByUserId] int NULL,
        [RequestedBy] nvarchar(250) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [CancelRequestedAtUtc] datetime2 NULL,
        [ErrorMessage] nvarchar(max) NULL
    );
END;

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionSyncLog]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionSyncLog]', N'HttpStatusCode') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionSyncLog]
    ADD [HttpStatusCode] int NULL;
END;

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionSyncLog]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionSyncLog]', N'SyncSource') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionSyncLog]
    ADD [SyncSource] nvarchar(50) NOT NULL
        CONSTRAINT [DF_TblKvhSubscriptionSyncLog_SyncSource] DEFAULT(N'PORTAL') WITH VALUES;
END;

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionSyncLog]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionSyncLog]', N'SyncSource') IS NOT NULL
BEGIN
    UPDATE [dbo].[TblKvhSubscriptionSyncLog]
    SET [SyncSource] = N'PORTAL'
    WHERE [SyncSource] IS NULL OR LTRIM(RTRIM([SyncSource])) = N'';
END;

IF OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSyncBatchItem]
    (
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
        [ReturnedCount] int NULL,
        [ErrorCode] nvarchar(100) NULL,
        [ErrorMessage] nvarchar(max) NULL,
        CONSTRAINT [FK_TblKvhSyncBatchItem_Batch]
            FOREIGN KEY ([BatchId])
            REFERENCES [dbo].[TblKvhSyncBatch]([ID])
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_TblKvhSyncBatchItem_Batch_Device'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]')
)
BEGIN
    CREATE UNIQUE INDEX [UX_TblKvhSyncBatchItem_Batch_Device]
    ON [dbo].[TblKvhSyncBatchItem]([BatchId], [DeviceId]);
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TblKvhSyncBatchItem_Status_NextAttempt'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]')
)
BEGIN
    CREATE INDEX [IX_TblKvhSyncBatchItem_Status_NextAttempt]
    ON [dbo].[TblKvhSyncBatchItem]([Status], [NextAttemptAtUtc]);
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TblKvhSyncBatch_Status_Created'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSyncBatch]')
)
BEGIN
    CREATE INDEX [IX_TblKvhSyncBatch_Status_Created]
    ON [dbo].[TblKvhSyncBatch]([Status], [CreatedAtUtc] DESC);
END;

SELECT
    OBJECT_ID(N'[dbo].[TblKvhSyncBatch]', N'U') AS TblKvhSyncBatchObjectId,
    OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]', N'U') AS TblKvhSyncBatchItemObjectId,
    (SELECT COUNT(1) FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]')) AS BatchItemIndexCount;
