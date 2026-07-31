SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'CurrentSubscriptionStatus') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [CurrentSubscriptionStatus] nvarchar(80) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'CurrentScheduledAction') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [CurrentScheduledAction] nvarchar(120) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'CurrentScheduleId') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [CurrentScheduleId] nvarchar(200) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'CurrentScheduledEffectiveDateUtc') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [CurrentScheduledEffectiveDateUtc] datetime2 NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'CurrentScheduledCreatedAtUtc') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [CurrentScheduledCreatedAtUtc] datetime2 NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'OperationStatus') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [OperationStatus] nvarchar(80) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'LastSubscriptionCheckedAtUtc') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [LastSubscriptionCheckedAtUtc] datetime2 NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'ReconciliationStatus') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [ReconciliationStatus] nvarchar(80) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'ReconciliationMessage') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [ReconciliationMessage] nvarchar(max) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscriptionOperationItem]', N'SubscriptionResponseJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscriptionOperationItem] ADD [SubscriptionResponseJson] nvarchar(max) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KvhSubOperationItem_WaitingEffective' AND object_id = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]'))
BEGIN
    CREATE INDEX [IX_KvhSubOperationItem_WaitingEffective]
        ON [dbo].[TblKvhSubscriptionOperationItem]([Status], [NextVerificationAtUtc])
        INCLUDE ([BatchId], [DeviceId], [TrafficId], [Region], [KvhSubscriptionId]);
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_KvhSubOperationItem_OperationStatus_Effective' AND object_id = OBJECT_ID(N'[dbo].[TblKvhSubscriptionOperationItem]'))
BEGIN
    CREATE INDEX [IX_KvhSubOperationItem_OperationStatus_Effective]
        ON [dbo].[TblKvhSubscriptionOperationItem]([OperationStatus], [CurrentScheduledEffectiveDateUtc], [LastSubscriptionCheckedAtUtc])
        INCLUDE ([BatchId], [DeviceId], [TrafficId], [Region], [KvhSubscriptionId]);
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscription]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscription]', N'ScheduledCreatedAtUtc') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscription] ADD [ScheduledCreatedAtUtc] datetime2 NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscription]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[dbo].[TblKvhSubscription]', N'ScheduledRawJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblKvhSubscription] ADD [ScheduledRawJson] nvarchar(max) NULL;
END
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscription]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblKvhSubscription_Schedule_Effective' AND object_id = OBJECT_ID(N'[dbo].[TblKvhSubscription]'))
BEGIN
    CREATE INDEX [IX_TblKvhSubscription_Schedule_Effective]
        ON [dbo].[TblKvhSubscription]([ScheduledAction], [ScheduledEffectiveDateUtc], [LastSeenAtUtc])
        INCLUDE ([DeviceId], [TrafficId], [Region], [ScheduleId]);
END
GO
