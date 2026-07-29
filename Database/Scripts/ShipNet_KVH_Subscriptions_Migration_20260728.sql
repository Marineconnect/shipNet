/*
    KVH Solutions subscription persistence.
    Run manually before deploying the application version that reads these columns.
    This script is idempotent and does not delete historical production data.
*/

IF OBJECT_ID(N'[dbo].[TblDevices]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'[dbo].[TblDevices]', N'TrafficId') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [TrafficId] nvarchar(200) NULL;

    IF COL_LENGTH(N'[dbo].[TblDevices]', N'KvhUsageLastSyncUtc') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [KvhUsageLastSyncUtc] datetime2 NULL;

    IF COL_LENGTH(N'[dbo].[TblDevices]', N'KvhSubscriptionStatus') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [KvhSubscriptionStatus] nvarchar(80) NULL;

    IF COL_LENGTH(N'[dbo].[TblDevices]', N'KvhSubscriptionPlan') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [KvhSubscriptionPlan] nvarchar(255) NULL;

    IF COL_LENGTH(N'[dbo].[TblDevices]', N'KvhSubscriptionRegion') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [KvhSubscriptionRegion] nvarchar(120) NULL;

    IF COL_LENGTH(N'[dbo].[TblDevices]', N'KvhSubscriptionScheduledAction') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [KvhSubscriptionScheduledAction] nvarchar(120) NULL;

    IF COL_LENGTH(N'[dbo].[TblDevices]', N'KvhSubscriptionScheduleId') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [KvhSubscriptionScheduleId] nvarchar(200) NULL;

    IF COL_LENGTH(N'[dbo].[TblDevices]', N'KvhSubscriptionEffectiveDateUtc') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [KvhSubscriptionEffectiveDateUtc] datetime2 NULL;

    IF COL_LENGTH(N'[dbo].[TblDevices]', N'KvhSubscriptionLastSyncUtc') IS NULL
        ALTER TABLE [dbo].[TblDevices] ADD [KvhSubscriptionLastSyncUtc] datetime2 NULL;
END;
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscription]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscription]
    (
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSubscription] PRIMARY KEY,
        [DeviceId] int NOT NULL,
        [TerminalId] nvarchar(200) NOT NULL,
        [TrafficId] nvarchar(200) NOT NULL,
        [SubscriptionKey] nvarchar(450) NOT NULL,
        [Status] nvarchar(80) NULL,
        [PlanName] nvarchar(255) NULL,
        [PlanJson] nvarchar(max) NULL,
        [OptInStatus] nvarchar(80) NULL,
        [OptInJson] nvarchar(max) NULL,
        [ScheduledAction] nvarchar(120) NULL,
        [ScheduleId] nvarchar(200) NULL,
        [ScheduledEffectiveDateUtc] datetime2 NULL,
        [Region] nvarchar(120) NULL,
        [Proration] decimal(18,4) NULL,
        [AllowanceGb] decimal(18,4) NULL,
        [EffectiveDateUtc] datetime2 NULL,
        [RawSubscriptionJson] nvarchar(max) NULL,
        [IsCurrent] bit NOT NULL CONSTRAINT [DF_TblKvhSubscription_IsCurrent] DEFAULT(1),
        [FirstSeenAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubscription_FirstSeenAtUtc] DEFAULT(SYSUTCDATETIME()),
        [LastSeenAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubscription_LastSeenAtUtc] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblKvhSubscription_UpdatedAtUtc] DEFAULT(SYSUTCDATETIME())
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'UX_TblKvhSubscription_Device_Key' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscription]'))
    CREATE UNIQUE INDEX [UX_TblKvhSubscription_Device_Key] ON [dbo].[TblKvhSubscription] ([DeviceId], [SubscriptionKey]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblKvhSubscription_Device_Current' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscription]'))
    CREATE INDEX [IX_TblKvhSubscription_Device_Current] ON [dbo].[TblKvhSubscription] ([DeviceId], [IsCurrent], [Region]);
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionSyncLog]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TblKvhSubscriptionSyncLog]
    (
        [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblKvhSubscriptionSyncLog] PRIMARY KEY,
        [DeviceId] int NOT NULL,
        [TerminalId] nvarchar(200) NULL,
        [TrafficId] nvarchar(200) NULL,
        [StartedAtUtc] datetime2 NOT NULL,
        [CompletedAtUtc] datetime2 NULL,
        [Success] bit NOT NULL,
        [ErrorCode] nvarchar(100) NULL,
        [ErrorMessage] nvarchar(max) NULL,
        [ResponseJson] nvarchar(max) NULL,
        [ReturnedCount] int NOT NULL CONSTRAINT [DF_TblKvhSubscriptionSyncLog_ReturnedCount] DEFAULT(0)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblKvhSubscriptionSyncLog_Device_Started' AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscriptionSyncLog]'))
    CREATE INDEX [IX_TblKvhSubscriptionSyncLog_Device_Started] ON [dbo].[TblKvhSubscriptionSyncLog] ([DeviceId], [StartedAtUtc] DESC);
GO

IF OBJECT_ID(N'[dbo].[TblKvhCommand]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'[dbo].[TblKvhCommand]', N'TrafficId') IS NULL
        ALTER TABLE [dbo].[TblKvhCommand] ADD [TrafficId] nvarchar(200) NULL;

    IF COL_LENGTH(N'[dbo].[TblKvhCommand]', N'Region') IS NULL
        ALTER TABLE [dbo].[TblKvhCommand] ADD [Region] nvarchar(120) NULL;

    IF COL_LENGTH(N'[dbo].[TblKvhCommand]', N'ScheduleId') IS NULL
        ALTER TABLE [dbo].[TblKvhCommand] ADD [ScheduleId] nvarchar(200) NULL;

    IF COL_LENGTH(N'[dbo].[TblKvhCommand]', N'KvhSubscriptionId') IS NULL
        ALTER TABLE [dbo].[TblKvhCommand] ADD [KvhSubscriptionId] bigint NULL;

    IF COL_LENGTH(N'[dbo].[TblKvhCommand]', N'CooldownUntilUtc') IS NULL
        ALTER TABLE [dbo].[TblKvhCommand] ADD [CooldownUntilUtc] datetime2 NULL;
END;
GO

IF OBJECT_ID(N'[dbo].[TblDevices]', N'U') IS NOT NULL
   AND OBJECT_ID(N'[dbo].[TblKvhSubscription]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_TblKvhSubscription_TblDevices')
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscription] WITH NOCHECK
    ADD CONSTRAINT [FK_TblKvhSubscription_TblDevices] FOREIGN KEY ([DeviceId]) REFERENCES [dbo].[TblDevices]([ID]);
END;
GO

