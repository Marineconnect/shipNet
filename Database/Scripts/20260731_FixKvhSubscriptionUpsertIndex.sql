SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[dbo].[TblKvhSubscription]', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE [name] = N'UX_TblKvhSubscription_Device_Traffic_Region'
          AND [object_id] = OBJECT_ID(N'[dbo].[TblKvhSubscription]')
   )
BEGIN
    CREATE UNIQUE INDEX [UX_TblKvhSubscription_Device_Traffic_Region]
    ON [dbo].[TblKvhSubscription] ([DeviceId], [TrafficId], [Region]);
END
GO

