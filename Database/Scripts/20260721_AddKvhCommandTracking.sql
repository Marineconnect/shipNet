/*
    Add KVH asynchronous command tracking.
    Safe to run multiple times. It does not drop or delete existing production data.
*/

IF OBJECT_ID(N'[dbo].[TblKvhCommand]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhCommand]
    (
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhCommand] PRIMARY KEY,

        [DeviceId] int NOT NULL,
        [TerminalId] nvarchar(200) NOT NULL,
        [KvhDeviceId] nvarchar(200) NULL,

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

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TblKvhCommand_JobStatus_NextPoll'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhCommand]')
)
BEGIN
    CREATE INDEX [IX_TblKvhCommand_JobStatus_NextPoll]
    ON [dbo].[TblKvhCommand] ([JobStatus], [NextPollAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TblKvhCommand_DeviceId_RequestedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhCommand]')
)
BEGIN
    CREATE INDEX [IX_TblKvhCommand_DeviceId_RequestedAt]
    ON [dbo].[TblKvhCommand] ([DeviceId], [RequestedAtUtc] DESC);
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TblKvhCommand_TerminalId_RequestedAt'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhCommand]')
)
BEGIN
    CREATE INDEX [IX_TblKvhCommand_TerminalId_RequestedAt]
    ON [dbo].[TblKvhCommand] ([TerminalId], [RequestedAtUtc] DESC);
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_TblKvhCommand_JobId'
      AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhCommand]')
)
BEGIN
    CREATE UNIQUE INDEX [UX_TblKvhCommand_JobId]
    ON [dbo].[TblKvhCommand] ([JobId])
    WHERE [JobId] IS NOT NULL AND [JobId] <> '';
END;
GO

IF OBJECT_ID(N'[dbo].[TblDevices]', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.foreign_keys
       WHERE [name] = N'FK_TblKvhCommand_TblDevices'
         AND [parent_object_id] = OBJECT_ID(N'[dbo].[TblKvhCommand]')
   )
BEGIN
    ALTER TABLE [dbo].[TblKvhCommand] WITH NOCHECK
    ADD CONSTRAINT [FK_TblKvhCommand_TblDevices]
        FOREIGN KEY ([DeviceId]) REFERENCES [dbo].[TblDevices]([ID]);
END;
GO

IF OBJECT_ID(N'[dbo].[TblDeviceDataOptInHistory]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'[dbo].[TblDeviceDataOptInHistory]', N'KvhCommandId') IS NULL
        ALTER TABLE [dbo].[TblDeviceDataOptInHistory] ADD [KvhCommandId] bigint NULL;

    IF COL_LENGTH(N'[dbo].[TblDeviceDataOptInHistory]', N'JobStatus') IS NULL
        ALTER TABLE [dbo].[TblDeviceDataOptInHistory] ADD [JobStatus] nvarchar(30) NULL;

    IF COL_LENGTH(N'[dbo].[TblDeviceDataOptInHistory]', N'VerificationStatus') IS NULL
        ALTER TABLE [dbo].[TblDeviceDataOptInHistory] ADD [VerificationStatus] nvarchar(30) NULL;

    IF COL_LENGTH(N'[dbo].[TblDeviceDataOptInHistory]', N'CompletedAtUtc') IS NULL
        ALTER TABLE [dbo].[TblDeviceDataOptInHistory] ADD [CompletedAtUtc] datetime2 NULL;

    IF COL_LENGTH(N'[dbo].[TblDeviceDataOptInHistory]', N'VerifiedAtUtc') IS NULL
        ALTER TABLE [dbo].[TblDeviceDataOptInHistory] ADD [VerifiedAtUtc] datetime2 NULL;
END;
GO

/*
Rollback notes:
- Drop IX_TblKvhCommand_* indexes and FK_TblKvhCommand_TblDevices first.
- Drop TblKvhCommand only if the stored audit/command data is no longer needed.
- Keep TblDeviceDataOptInHistory extension columns unless you have exported or archived them.
*/
