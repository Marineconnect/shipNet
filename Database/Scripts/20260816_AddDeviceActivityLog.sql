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
        [OccurredAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_OccurredAtUtc] DEFAULT SYSUTCDATETIME(),
        [RecordedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_RecordedAtUtc] DEFAULT SYSUTCDATETIME(),
        [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_CreatedAtUtc] DEFAULT SYSUTCDATETIME()
    );
END;

IF COL_LENGTH(N'[dbo].[TblDeviceActivityLog]', N'ActorType') IS NULL
    ALTER TABLE [dbo].[TblDeviceActivityLog] ADD [ActorType] nvarchar(30) NULL;

IF COL_LENGTH(N'[dbo].[TblDeviceActivityLog]', N'EventKey') IS NULL
    ALTER TABLE [dbo].[TblDeviceActivityLog] ADD [EventKey] nvarchar(250) NULL;

IF COL_LENGTH(N'[dbo].[TblDeviceActivityLog]', N'OccurredAtUtc') IS NULL
    ALTER TABLE [dbo].[TblDeviceActivityLog] ADD [OccurredAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_OccurredAtUtc_Existing] DEFAULT SYSUTCDATETIME() WITH VALUES;

IF COL_LENGTH(N'[dbo].[TblDeviceActivityLog]', N'RecordedAtUtc') IS NULL
    ALTER TABLE [dbo].[TblDeviceActivityLog] ADD [RecordedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_RecordedAtUtc_Existing] DEFAULT SYSUTCDATETIME() WITH VALUES;

UPDATE [dbo].[TblDeviceActivityLog]
SET [OccurredAtUtc] = [CreatedAtUtc]
WHERE [OccurredAtUtc] IS NULL;

UPDATE [dbo].[TblDeviceActivityLog]
SET [RecordedAtUtc] = [CreatedAtUtc]
WHERE [RecordedAtUtc] IS NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Device_CreatedAtUtc' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_Device_CreatedAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [CreatedAtUtc] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Device_OccurredAtUtc' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_Device_OccurredAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [OccurredAtUtc] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Device_Category_CreatedAtUtc' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_Device_Category_CreatedAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [Category], [CreatedAtUtc] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Device_Status_CreatedAtUtc' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_Device_Status_CreatedAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [Status], [CreatedAtUtc] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_CorrelationId' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_CorrelationId] ON [dbo].[TblDeviceActivityLog]([CorrelationId]) WHERE [CorrelationId] IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Reference' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE INDEX [IX_TblDeviceActivityLog_Reference] ON [dbo].[TblDeviceActivityLog]([ReferenceType], [ReferenceId]) WHERE [ReferenceType] IS NOT NULL AND [ReferenceId] IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TblDeviceActivityLog_EventKey' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
    CREATE UNIQUE INDEX [UX_TblDeviceActivityLog_EventKey] ON [dbo].[TblDeviceActivityLog]([EventKey]) WHERE [EventKey] IS NOT NULL;
