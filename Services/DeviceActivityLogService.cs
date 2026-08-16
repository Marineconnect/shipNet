using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class DeviceActivityLogService(
    IConfiguration configuration,
    ILogger<DeviceActivityLogService> logger) : IDeviceActivityLogService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task WriteAsync(DeviceActivityLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.DeviceId <= 0 || string.IsNullOrWhiteSpace(entry.Category) ||
            string.IsNullOrWhiteSpace(entry.Action) || string.IsNullOrWhiteSpace(entry.Status))
        {
            logger.LogWarning("Skipped invalid device activity log entry. DeviceId={DeviceId}; Category={Category}; Action={Action}; Status={Status}",
                entry.DeviceId, entry.Category, entry.Action, entry.Status);
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        const string query = """
            INSERT INTO [dbo].[TblDeviceActivityLog]
                ([DeviceId], [TenantId], [Category], [Action], [Status], [OldValue], [NewValue], [Summary], [DetailJson], [Source],
                 [UserId], [PerformedBy], [ReferenceType], [ReferenceId], [CorrelationId], [CreatedAtUtc])
            VALUES
                (@deviceId, @tenantId, @category, @action, @status, @oldValue, @newValue, @summary, @detailJson, @source,
                 @userId, @performedBy, @referenceType, @referenceId, @correlationId, @createdAtUtc)
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = entry.DeviceId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)entry.TenantId ?? DBNull.Value;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = Trim(entry.Category, 50);
        command.Parameters.Add("@action", SqlDbType.NVarChar, 100).Value = Trim(entry.Action, 100);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = Trim(entry.Status, 30);
        command.Parameters.Add("@oldValue", SqlDbType.NVarChar, 500).Value = DbText(entry.OldValue, 500);
        command.Parameters.Add("@newValue", SqlDbType.NVarChar, 500).Value = DbText(entry.NewValue, 500);
        command.Parameters.Add("@summary", SqlDbType.NVarChar, 500).Value = Trim(string.IsNullOrWhiteSpace(entry.Summary) ? entry.Action : entry.Summary, 500);
        command.Parameters.Add("@detailJson", SqlDbType.NVarChar, -1).Value = DbText(DeviceActivitySanitizer.Sanitize(entry.DetailJson));
        command.Parameters.Add("@source", SqlDbType.NVarChar, 50).Value = DbText(entry.Source, 50);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)entry.UserId ?? DBNull.Value;
        command.Parameters.Add("@performedBy", SqlDbType.NVarChar, 250).Value = DbText(entry.PerformedBy, 250);
        command.Parameters.Add("@referenceType", SqlDbType.NVarChar, 50).Value = DbText(entry.ReferenceType, 50);
        command.Parameters.Add("@referenceId", SqlDbType.NVarChar, 100).Value = DbText(entry.ReferenceId, 100);
        command.Parameters.Add("@correlationId", SqlDbType.NVarChar, 100).Value = DbText(entry.CorrelationId, 100);
        command.Parameters.Add("@createdAtUtc", SqlDbType.DateTime2).Value = entry.CreatedAtUtc ?? DateTime.UtcNow;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeviceActivityPageResult> GetDeviceActivityAsync(
        int deviceId,
        DeviceActivityFilter filter,
        int page,
        int pageSize,
        int? allowedTenantId = null,
        int? allowedDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = pageSize is >= 1 and <= 100 ? pageSize : 50;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        if (!await CanAccessDeviceAsync(connection, deviceId, allowedTenantId, allowedDeviceId, cancellationToken))
        {
            return new DeviceActivityPageResult { DeviceId = deviceId, Page = page, PageSize = pageSize };
        }

        var category = NormalizeFilter(filter.Category);
        var status = NormalizeFilter(filter.Status);
        var fromUtc = filter.DateFrom?.Date;
        var toUtcExclusive = filter.DateTo?.Date.AddDays(1);
        var offset = (page - 1) * pageSize;

        const string query = """
            CREATE TABLE #Activity(
                [RowId] bigint IDENTITY(1,1) NOT NULL,
                [Id] bigint NOT NULL,
                [TimeUtc] datetime2 NOT NULL,
                [Category] nvarchar(50) NOT NULL,
                [Action] nvarchar(100) NOT NULL,
                [Status] nvarchar(30) NOT NULL,
                [OldValue] nvarchar(500) NULL,
                [NewValue] nvarchar(500) NULL,
                [Summary] nvarchar(500) NOT NULL,
                [DetailJson] nvarchar(max) NULL,
                [Source] nvarchar(50) NULL,
                [PerformedBy] nvarchar(250) NULL,
                [ReferenceType] nvarchar(50) NULL,
                [ReferenceId] nvarchar(100) NULL,
                [CorrelationId] nvarchar(100) NULL,
                [IsLegacy] bit NOT NULL
            );

            INSERT INTO #Activity
                ([Id], [TimeUtc], [Category], [Action], [Status], [OldValue], [NewValue], [Summary], [DetailJson], [Source], [PerformedBy], [ReferenceType], [ReferenceId], [CorrelationId], [IsLegacy])
            SELECT [ID], [CreatedAtUtc], [Category], [Action], [Status], [OldValue], [NewValue], [Summary], [DetailJson], [Source], [PerformedBy], [ReferenceType], [ReferenceId], [CorrelationId], CAST(0 AS bit)
            FROM [dbo].[TblDeviceActivityLog]
            WHERE [DeviceId] = @deviceId
              AND (@category IS NULL OR [Category] = @category)
              AND (@status IS NULL OR [Status] = @status)
              AND (@fromUtc IS NULL OR [CreatedAtUtc] >= @fromUtc)
              AND (@toUtc IS NULL OR [CreatedAtUtc] < @toUtc);

            IF OBJECT_ID(N'[dbo].[TblAudit]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO #Activity
                    ([Id], [TimeUtc], [Category], [Action], [Status], [Summary], [Source], [PerformedBy], [ReferenceType], [ReferenceId], [IsLegacy])
                SELECT CAST(a.[ID] AS bigint), CONVERT(datetime2, a.[LogDate]), N'SYSTEM', UPPER(ISNULL(a.[LogAction], N'AUDIT')), N'Succeeded',
                       LEFT(ISNULL(a.[LogDetail], N''), 500), N'LEGACY_TblAudit', NULL, N'AUDIT', CONVERT(nvarchar(100), a.[ID]), CAST(1 AS bit)
                FROM [dbo].[TblAudit] a
                WHERE a.[IDDevice] = @deviceId
                  AND (@category IS NULL OR @category = N'SYSTEM')
                  AND (@status IS NULL OR @status = N'Succeeded')
                  AND (@fromUtc IS NULL OR CONVERT(datetime2, a.[LogDate]) >= @fromUtc)
                  AND (@toUtc IS NULL OR CONVERT(datetime2, a.[LogDate]) < @toUtc)
                  AND NOT EXISTS (
                    SELECT 1 FROM [dbo].[TblDeviceActivityLog] l
                    WHERE l.[DeviceId] = @deviceId
                      AND l.[ReferenceType] = N'AUDIT'
                      AND l.[ReferenceId] = CONVERT(nvarchar(100), a.[ID])
                  );
            END;

            IF OBJECT_ID(N'[dbo].[TblKvhCommand]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO #Activity
                    ([Id], [TimeUtc], [Category], [Action], [Status], [OldValue], [NewValue], [Summary], [DetailJson], [Source], [PerformedBy], [ReferenceType], [ReferenceId], [CorrelationId], [IsLegacy])
                SELECT c.[ID], c.[RequestedAtUtc],
                       CASE WHEN c.[CommandType] IN (N'WIFI_UPDATE', N'REBOOT') THEN N'NETWORKING' WHEN c.[CommandType] = N'DATA_OPT_IN' THEN N'DATA' ELSE N'SUBSCRIPTION' END,
                       c.[CommandType],
                       CASE WHEN c.[CommandStatus] IN (N'FAILED', N'TIMEOUT', N'VERIFICATION_MISMATCH', N'VERIFICATION_TIMEOUT') THEN N'Failed'
                            WHEN c.[CommandStatus] IN (N'COMPLETED', N'VERIFIED', N'SUCCESS') THEN N'Succeeded'
                            WHEN c.[CommandStatus] IN (N'SUBMITTED', N'SUBMITTING') THEN N'Requested'
                            ELSE N'Pending' END,
                       NULL, c.[JobStatus],
                       CONCAT(N'KVH command ', c.[CommandType], N' ', LOWER(c.[CommandStatus]), N'.'),
                       LEFT(COALESCE(NULLIF(c.[ErrorMessage], N''), c.[SubmitResponseJson], c.[JobResponseJson], N''), 8000),
                       N'LEGACY_TblKvhCommand', c.[RequestedBy], N'KVH_COMMAND', CONVERT(nvarchar(100), c.[ID]), COALESCE(NULLIF(c.[JobId], N''), CONVERT(nvarchar(100), c.[ID])), CAST(1 AS bit)
                FROM [dbo].[TblKvhCommand] c
                WHERE c.[DeviceId] = @deviceId
                  AND (@category IS NULL OR @category = CASE WHEN c.[CommandType] IN (N'WIFI_UPDATE', N'REBOOT') THEN N'NETWORKING' WHEN c.[CommandType] = N'DATA_OPT_IN' THEN N'DATA' ELSE N'SUBSCRIPTION' END)
                  AND (@fromUtc IS NULL OR c.[RequestedAtUtc] >= @fromUtc)
                  AND (@toUtc IS NULL OR c.[RequestedAtUtc] < @toUtc)
                  AND NOT EXISTS (
                    SELECT 1 FROM [dbo].[TblDeviceActivityLog] l
                    WHERE l.[DeviceId] = @deviceId
                      AND l.[ReferenceType] = N'KVH_COMMAND'
                      AND l.[ReferenceId] = CONVERT(nvarchar(100), c.[ID])
                  );
            END;

            IF OBJECT_ID(N'[dbo].[TblDeviceDataOptInHistory]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO #Activity
                    ([Id], [TimeUtc], [Category], [Action], [Status], [OldValue], [NewValue], [Summary], [DetailJson], [Source], [PerformedBy], [ReferenceType], [ReferenceId], [CorrelationId], [IsLegacy])
                SELECT h.[ID], h.[PerformedAtUtc], N'DATA',
                       CASE WHEN h.[NewStatus] = 1 THEN N'DATA_OPT_IN_COMPLETED' ELSE N'DATA_OPT_OUT_COMPLETED' END,
                       CASE WHEN h.[ApiSuccess] = 1 THEN N'Succeeded' ELSE N'Failed' END,
                       CASE WHEN h.[OldStatus] = 1 THEN N'on' WHEN h.[OldStatus] = 0 THEN N'off' ELSE NULL END,
                       CASE WHEN h.[NewStatus] = 1 THEN N'on' ELSE N'off' END,
                       CASE WHEN h.[NewStatus] = 1 THEN N'Data opt-in changed.' ELSE N'Data opt-out changed.' END,
                       LEFT(ISNULL(h.[ApiResponse], N''), 8000), N'LEGACY_TblDeviceDataOptInHistory', h.[PerformedBy], N'DATA_OPT_IN_HISTORY', CONVERT(nvarchar(100), h.[ID]), h.[JobId], CAST(1 AS bit)
                FROM [dbo].[TblDeviceDataOptInHistory] h
                WHERE h.[DeviceId] = @deviceId
                  AND (@category IS NULL OR @category = N'DATA')
                  AND (@status IS NULL OR @status = CASE WHEN h.[ApiSuccess] = 1 THEN N'Succeeded' ELSE N'Failed' END)
                  AND (@fromUtc IS NULL OR h.[PerformedAtUtc] >= @fromUtc)
                  AND (@toUtc IS NULL OR h.[PerformedAtUtc] < @toUtc)
                  AND NOT EXISTS (
                    SELECT 1 FROM [dbo].[TblDeviceActivityLog] l
                    WHERE l.[DeviceId] = @deviceId
                      AND l.[ReferenceType] = N'DATA_OPT_IN_HISTORY'
                      AND l.[ReferenceId] = CONVERT(nvarchar(100), h.[ID])
                  );
            END;

            SELECT COUNT(1) FROM #Activity WHERE (@status IS NULL OR [Status] = @status);

            SELECT [Id], [TimeUtc], [Category], [Action], [Status], [OldValue], [NewValue], [Summary], [DetailJson], [Source], [PerformedBy], [ReferenceType], [ReferenceId], [CorrelationId], [IsLegacy]
            FROM #Activity
            WHERE (@status IS NULL OR [Status] = @status)
            ORDER BY [TimeUtc] DESC, [Id] DESC, [IsLegacy]
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 50).Value = (object?)category ?? DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@fromUtc", SqlDbType.DateTime2).Value = (object?)fromUtc ?? DBNull.Value;
        command.Parameters.Add("@toUtc", SqlDbType.DateTime2).Value = (object?)toUtcExclusive ?? DBNull.Value;
        command.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;

        var result = new DeviceActivityPageResult { DeviceId = deviceId, Page = page, PageSize = pageSize };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            result.TotalItems = Convert.ToInt32(reader[0]);
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Items.Add(new DeviceActivityItem
                {
                    Id = Convert.ToInt64(reader["Id"]),
                    TimeUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["TimeUtc"]), DateTimeKind.Utc),
                    Category = ReadText(reader, "Category"),
                    Action = ReadText(reader, "Action"),
                    Status = ReadText(reader, "Status"),
                    OldValue = ReadText(reader, "OldValue"),
                    NewValue = ReadText(reader, "NewValue"),
                    Summary = ReadText(reader, "Summary"),
                    DetailJson = DeviceActivitySanitizer.Sanitize(ReadText(reader, "DetailJson")),
                    Source = ReadText(reader, "Source"),
                    PerformedBy = ReadText(reader, "PerformedBy"),
                    ReferenceType = ReadText(reader, "ReferenceType"),
                    ReferenceId = ReadText(reader, "ReferenceId"),
                    CorrelationId = ReadText(reader, "CorrelationId"),
                    IsLegacy = reader["IsLegacy"] != DBNull.Value && Convert.ToBoolean(reader["IsLegacy"])
                });
            }
        }

        return result;
    }

    private static async Task<bool> CanAccessDeviceAsync(SqlConnection connection, int deviceId, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT COUNT(1)
            FROM [dbo].[TblDevices]
            WHERE [ID] = @deviceId
              AND (@tenantId IS NULL OR [TenantID] = @tenantId)
              AND (@allowedDeviceId IS NULL OR [ID] = @allowedDeviceId)
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;
    }

    public static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string query = """
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
                    [UserId] int NULL,
                    [PerformedBy] nvarchar(250) NULL,
                    [ReferenceType] nvarchar(50) NULL,
                    [ReferenceId] nvarchar(100) NULL,
                    [CorrelationId] nvarchar(100) NULL,
                    [CreatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_TblDeviceActivityLog_CreatedAtUtc] DEFAULT SYSUTCDATETIME()
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Device_CreatedAtUtc' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
                CREATE INDEX [IX_TblDeviceActivityLog_Device_CreatedAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [CreatedAtUtc] DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Device_Category_CreatedAtUtc' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
                CREATE INDEX [IX_TblDeviceActivityLog_Device_Category_CreatedAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [Category], [CreatedAtUtc] DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Device_Status_CreatedAtUtc' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
                CREATE INDEX [IX_TblDeviceActivityLog_Device_Status_CreatedAtUtc] ON [dbo].[TblDeviceActivityLog]([DeviceId], [Status], [CreatedAtUtc] DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_CorrelationId' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
                CREATE INDEX [IX_TblDeviceActivityLog_CorrelationId] ON [dbo].[TblDeviceActivityLog]([CorrelationId]) WHERE [CorrelationId] IS NOT NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TblDeviceActivityLog_Reference' AND object_id = OBJECT_ID(N'[dbo].[TblDeviceActivityLog]'))
                CREATE INDEX [IX_TblDeviceActivityLog_Reference] ON [dbo].[TblDeviceActivityLog]([ReferenceType], [ReferenceId]) WHERE [ReferenceType] IS NOT NULL AND [ReferenceId] IS NOT NULL;
            """;
        await using var command = new SqlCommand(query, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? NormalizeFilter(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Equals("All", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static object DbText(string? value, int maxLength = 0)
    {
        value = DeviceActivitySanitizer.Sanitize(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return DBNull.Value;
        }

        return maxLength > 0 ? Trim(value, maxLength) : value;
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string ReadText(SqlDataReader reader, string columnName) =>
        reader[columnName] == DBNull.Value ? string.Empty : reader[columnName]?.ToString() ?? string.Empty;
}
