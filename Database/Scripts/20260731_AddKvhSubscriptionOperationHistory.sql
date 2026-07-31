SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationBatch]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscriptionOperationBatch]
    (
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
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscriptionOperationItem]
    (
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
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubOperationItem_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubOperationItem_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME()),
        [SubmittedAtUtc] datetime2 NULL,
        [JobCompletedAtUtc] datetime2 NULL,
        [VerifiedAtUtc] datetime2 NULL,
        CONSTRAINT [FK_KvhSubOperationItem_Batch] FOREIGN KEY ([BatchId]) REFERENCES [dbo].[TblKvhSubscriptionOperationBatch]([ID]),
        CONSTRAINT [UQ_KvhSubOperationItem_Batch_Kit] UNIQUE ([BatchId], [KitNumberNormalized])
    );
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'HttpStatusCode') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [HttpStatusCode] int NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'SubmitResponseJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [SubmitResponseJson] nvarchar(max) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'OperationLogJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [OperationLogJson] nvarchar(max) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationAudit]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscriptionOperationAudit]
    (
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSubscriptionOperationAudit] PRIMARY KEY,
        [BatchId] bigint NULL,
        [ItemId] bigint NULL,
        [Action] nvarchar(80) NOT NULL,
        [PerformedByUserId] int NULL,
        [PerformedBy] nvarchar(250) NULL,
        [Message] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubscriptionOperationAudit_CreatedAtUtc] DEFAULT(SYSUTCDATETIME())
    );
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhCommand]', N'U') IS NOT NULL
   AND OBJECT_ID(N'[dbo].[FK_KvhSubOperationItem_KvhCommand]', N'F') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem]
    ADD CONSTRAINT [FK_KvhSubOperationItem_KvhCommand]
        FOREIGN KEY ([KvhCommandId]) REFERENCES [dbo].[TblKvhCommand]([ID]);
END
GO

IF OBJECT_ID(N'[dbo].[TblDevices]', N'U') IS NOT NULL
   AND OBJECT_ID(N'[dbo].[FK_KvhSubOperationItem_Device]', N'F') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem]
    ADD CONSTRAINT [FK_KvhSubOperationItem_Device]
        FOREIGN KEY ([DeviceId]) REFERENCES [dbo].[TblDevices]([ID]);
END
GO

IF OBJECT_ID(N'[dbo].[TblTenant]', N'U') IS NOT NULL
   AND OBJECT_ID(N'[dbo].[FK_KvhSubOperationBatch_Tenant]', N'F') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationBatch]
    ADD CONSTRAINT [FK_KvhSubOperationBatch_Tenant]
        FOREIGN KEY ([TenantId]) REFERENCES [dbo].[TblTenant]([ID]);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KvhSubOperationItem_Status_NextSubmit' AND object_id = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]'))
    CREATE INDEX [IX_KvhSubOperationItem_Status_NextSubmit] ON [dbo].[TblKvhSubscriptionOperationItem]([Status], [NextSubmitAtUtc]) INCLUDE ([BatchId], [DeviceId], [KvhCommandId]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KvhSubOperationItem_JobStatus_NextPoll' AND object_id = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]'))
    CREATE INDEX [IX_KvhSubOperationItem_JobStatus_NextPoll] ON [dbo].[TblKvhSubscriptionOperationItem]([JobStatus], [NextPollAtUtc]) INCLUDE ([BatchId], [KvhCommandId]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KvhSubOperationItem_Verification_Next' AND object_id = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]'))
    CREATE INDEX [IX_KvhSubOperationItem_Verification_Next] ON [dbo].[TblKvhSubscriptionOperationItem]([VerificationStatus], [NextVerificationAtUtc]) INCLUDE ([BatchId], [KvhCommandId]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KvhSubOperationItem_Batch_Status' AND object_id = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]'))
    CREATE INDEX [IX_KvhSubOperationItem_Batch_Status] ON [dbo].[TblKvhSubscriptionOperationItem]([BatchId], [Status]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KvhSubOperationBatch_Status_Created' AND object_id = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationBatch]'))
    CREATE INDEX [IX_KvhSubOperationBatch_Status_Created] ON [dbo].[TblKvhSubscriptionOperationBatch]([Status], [CreatedAtUtc] DESC);
GO
