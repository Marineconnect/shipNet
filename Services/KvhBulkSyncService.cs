using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public static class KvhBatchTypes
{
    public const string SyncSelected = "SYNC_SELECTED";
    public const string SyncAll = "SYNC_ALL";
    public const string SyncMissing = "SYNC_MISSING";
    public const string RetryFailed = "RETRY_FAILED";
}

public static class KvhBatchStatuses
{
    public const string Created = "CREATED";
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string CompletedWithErrors = "COMPLETED_WITH_ERRORS";
    public const string Failed = "FAILED";
    public const string CancelRequested = "CANCEL_REQUESTED";
    public const string Cancelled = "CANCELLED";
}

public static class KvhBatchItemStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Success = "SUCCESS";
    public const string Empty = "EMPTY";
    public const string Failed = "FAILED";
    public const string RetryWait = "RETRY_WAIT";
    public const string Skipped = "SKIPPED";
    public const string Cancelled = "CANCELLED";
}

public sealed class KvhBulkSyncService(
    IConfiguration configuration,
    IKvhSubscriptionService kvhSubscriptionService,
    IOptions<KvhBulkSyncOptions> options,
    ILogger<KvhBulkSyncService> logger) : IKvhBulkSyncService
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime _nextAllowedRequestUtc = DateTime.MinValue;
    private readonly KvhBulkSyncOptions settings = options.Value;
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task<KvhBatchCreateResult> CreateBatchAsync(
        KvhBatchCreateRequest request,
        int? userId,
        string requestedBy,
        int? allowedTenantId,
        int? allowedDeviceId,
        CancellationToken cancellationToken = default)
    {
        var mode = NormalizeMode(request.Mode);
        if (string.IsNullOrWhiteSpace(mode))
        {
            return Fail("invalid_batch_mode", "Invalid KVH sync batch mode.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await HasBatchTablesAsync(connection, cancellationToken))
        {
            return Fail("schema_missing", "KVH bulk sync tables are missing. Run Database/Scripts/20260730_AddKvhBulkSubscriptionSync.sql first.");
        }

        var deviceIds = await ResolveDeviceIdsAsync(connection, request, mode, allowedTenantId, allowedDeviceId, cancellationToken);
        if (deviceIds.Count == 0)
        {
            return Fail("empty_batch", "No devices are available for this KVH sync batch.");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string insertBatch = """
                INSERT INTO [dbo].[TblKvhSyncBatch]
                    ([BatchType], [Status], [TenantId], [TotalItems], [PendingItems], [RequestedByUserId], [RequestedBy], [CreatedAtUtc])
                OUTPUT INSERTED.[ID]
                VALUES
                    (@batchType, @status, @tenantId, @totalItems, @pendingItems, @requestedByUserId, @requestedBy, SYSUTCDATETIME())
                """;
            await using var batchCommand = new SqlCommand(insertBatch, connection, transaction);
            batchCommand.Parameters.Add("@batchType", SqlDbType.NVarChar, 50).Value = mode;
            batchCommand.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = KvhBatchStatuses.Queued;
            batchCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)(request.TenantId ?? allowedTenantId) ?? DBNull.Value;
            batchCommand.Parameters.Add("@totalItems", SqlDbType.Int).Value = deviceIds.Count;
            batchCommand.Parameters.Add("@pendingItems", SqlDbType.Int).Value = deviceIds.Count;
            batchCommand.Parameters.Add("@requestedByUserId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
            batchCommand.Parameters.Add("@requestedBy", SqlDbType.NVarChar, 250).Value = string.IsNullOrWhiteSpace(requestedBy) ? DBNull.Value : requestedBy.Trim();
            var batchId = Convert.ToInt64(await batchCommand.ExecuteScalarAsync(cancellationToken));

            const string insertItem = """
                INSERT INTO [dbo].[TblKvhSyncBatchItem]
                    ([BatchId], [DeviceId], [Status], [AttemptCount], [MaxAttemptCount], [NextAttemptAtUtc])
                VALUES
                    (@batchId, @deviceId, @status, 0, @maxAttempts, SYSUTCDATETIME())
                """;
            foreach (var deviceId in deviceIds)
            {
                await using var itemCommand = new SqlCommand(insertItem, connection, transaction);
                itemCommand.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
                itemCommand.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
                itemCommand.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = KvhBatchItemStatuses.Pending;
                itemCommand.Parameters.Add("@maxAttempts", SqlDbType.Int).Value = Math.Max(1, settings.MaxAttempts);
                await itemCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new KvhBatchCreateResult { Success = true, BatchId = batchId, TotalItems = deviceIds.Count, Message = $"Created KVH sync batch #{batchId} with {deviceIds.Count} device(s)." };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<KvhSyncBatchDetail?> GetBatchAsync(long batchId, int? allowedTenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await HasBatchTablesAsync(connection, cancellationToken))
        {
            return null;
        }

        const string batchSql = """
            SELECT TOP 1 *
            FROM [dbo].[TblKvhSyncBatch]
            WHERE [ID] = @batchId AND (@tenantId IS NULL OR [TenantId] = @tenantId)
            """;
        await using var batchCommand = new SqlCommand(batchSql, connection);
        batchCommand.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        batchCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        KvhSyncBatchDetail? detail = null;
        await using (var reader = await batchCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                detail = MapBatch<KvhSyncBatchDetail>(reader);
            }
        }

        if (detail is null)
        {
            return null;
        }

        const string itemSql = """
            SELECT TOP 200 *
            FROM [dbo].[TblKvhSyncBatchItem]
            WHERE [BatchId] = @batchId
            ORDER BY [ID]
            """;
        await using var itemCommand = new SqlCommand(itemSql, connection);
        itemCommand.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            detail.Items.Add(MapItem(itemReader));
        }

        return detail;
    }

    public async Task<IReadOnlyList<KvhSyncBatchSummaryViewModel>> GetRecentBatchesAsync(int? allowedTenantId, CancellationToken cancellationToken = default)
    {
        var batches = new List<KvhSyncBatchSummaryViewModel>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await HasBatchTablesAsync(connection, cancellationToken))
        {
            return batches;
        }

        const string query = """
            SELECT TOP 10 *
            FROM [dbo].[TblKvhSyncBatch]
            WHERE (@tenantId IS NULL OR [TenantId] = @tenantId)
            ORDER BY [CreatedAtUtc] DESC, [ID] DESC
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            batches.Add(MapBatch<KvhSyncBatchSummaryViewModel>(reader));
        }

        return batches;
    }

    public async Task RequestCancelAsync(long batchId, int? allowedTenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string query = """
            UPDATE [dbo].[TblKvhSyncBatch]
            SET [Status] = @status, [CancelRequestedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @batchId
              AND (@tenantId IS NULL OR [TenantId] = @tenantId)
              AND [Status] IN ('CREATED', 'QUEUED', 'RUNNING')
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = KvhBatchStatuses.CancelRequested;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ProcessPendingItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await HasBatchTablesAsync(connection, cancellationToken))
        {
            return;
        }

        await CancelRequestedBatchesAsync(connection, cancellationToken);
        var items = await ClaimItemsAsync(connection, cancellationToken);
        var maxConcurrency = Math.Clamp(settings.MaxConcurrentRequests, 1, 20);
        using var concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = items.Select(async item =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                await ThrottleAsync(cancellationToken);
                await ProcessItemAsync(item, cancellationToken);
            }
            finally
            {
                concurrencyGate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task ProcessItemAsync(KvhSyncBatchItemViewModel item, CancellationToken cancellationToken)
    {
        try
        {
            var result = await kvhSubscriptionService.SyncDeviceSubscriptionAsync(item.DeviceId, null, null, cancellationToken);
            var status = result.Success
                ? KvhBatchItemStatuses.Success
                : result.ErrorCode == "kvh_subscription_empty" ? KvhBatchItemStatuses.Empty : KvhBatchItemStatuses.Failed;
            await CompleteItemAsync(item, status, result.ReturnedCount, result.TrafficId, result.ErrorCode, result.Message, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "KVH bulk sync item failed. BatchId={BatchId}; ItemId={ItemId}; DeviceId={DeviceId}", item.BatchId, item.Id, item.DeviceId);
            await CompleteItemAsync(item, KvhBatchItemStatuses.Failed, null, item.TrafficId, "worker_exception", ex.GetBaseException().Message, cancellationToken);
        }
    }

    private async Task CompleteItemAsync(KvhSyncBatchItemViewModel item, string status, int? returnedCount, string trafficId, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var finalStatus = status;
        DateTime? nextAttempt = null;

        if (status == KvhBatchItemStatuses.Failed && item.AttemptCount < Math.Max(1, settings.MaxAttempts) && IsRetryable(errorCode))
        {
            finalStatus = KvhBatchItemStatuses.RetryWait;
            nextAttempt = DateTime.UtcNow.AddSeconds(BuildRetryDelaySeconds(item.AttemptCount));
        }

        const string updateItem = """
            UPDATE [dbo].[TblKvhSyncBatchItem]
            SET [Status] = @status,
                [TrafficId] = NULLIF(@trafficId, ''),
                [ReturnedCount] = @returnedCount,
                [ErrorCode] = NULLIF(@errorCode, ''),
                [ErrorMessage] = NULLIF(@errorMessage, ''),
                [NextAttemptAtUtc] = @nextAttemptAtUtc,
                [CompletedAtUtc] = CASE WHEN @isTerminal = 1 THEN SYSUTCDATETIME() ELSE NULL END
            WHERE [ID] = @id
            """;
        await using var command = new SqlCommand(updateItem, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = item.Id;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = finalStatus;
        command.Parameters.Add("@trafficId", SqlDbType.NVarChar, 200).Value = trafficId ?? string.Empty;
        command.Parameters.Add("@returnedCount", SqlDbType.Int).Value = (object?)returnedCount ?? DBNull.Value;
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode ?? string.Empty;
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = errorMessage ?? string.Empty;
        command.Parameters.Add("@nextAttemptAtUtc", SqlDbType.DateTime2).Value = (object?)nextAttempt ?? DBNull.Value;
        command.Parameters.Add("@isTerminal", SqlDbType.Bit).Value = finalStatus != KvhBatchItemStatuses.RetryWait;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await RefreshBatchCountersAsync(connection, item.BatchId, cancellationToken);
    }

    private async Task<IReadOnlyList<KvhSyncBatchItemViewModel>> ClaimItemsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var items = new List<KvhSyncBatchItemViewModel>();
        const string query = """
            ;WITH claim AS
            (
                SELECT TOP (@batchSize) i.*
                FROM [dbo].[TblKvhSyncBatchItem] i WITH (UPDLOCK, READPAST, ROWLOCK)
                INNER JOIN [dbo].[TblKvhSyncBatch] b WITH (UPDLOCK, ROWLOCK) ON b.[ID] = i.[BatchId]
                WHERE i.[Status] IN ('PENDING', 'RETRY_WAIT')
                  AND (i.[NextAttemptAtUtc] IS NULL OR i.[NextAttemptAtUtc] <= SYSUTCDATETIME())
                  AND b.[Status] IN ('CREATED', 'QUEUED', 'RUNNING')
                ORDER BY i.[NextAttemptAtUtc], i.[ID]
            )
            UPDATE claim
            SET [Status] = 'PROCESSING',
                [AttemptCount] = [AttemptCount] + 1,
                [StartedAtUtc] = COALESCE([StartedAtUtc], SYSUTCDATETIME()),
                [CompletedAtUtc] = NULL
            OUTPUT INSERTED.*;

            UPDATE b
            SET [Status] = 'RUNNING',
                [StartedAtUtc] = COALESCE([StartedAtUtc], SYSUTCDATETIME())
            FROM [dbo].[TblKvhSyncBatch] b
            WHERE EXISTS (SELECT 1 FROM [dbo].[TblKvhSyncBatchItem] i WHERE i.[BatchId] = b.[ID] AND i.[Status] = 'PROCESSING');
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@batchSize", SqlDbType.Int).Value = Math.Clamp(settings.BatchSize, 1, 100);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapItem(reader));
        }

        return items;
    }

    private async Task RefreshBatchCountersAsync(SqlConnection connection, long batchId, CancellationToken cancellationToken)
    {
        const string query = """
            ;WITH counts AS
            (
                SELECT [BatchId],
                    SUM(CASE WHEN [Status] IN ('PENDING', 'RETRY_WAIT') THEN 1 ELSE 0 END) AS PendingItems,
                    SUM(CASE WHEN [Status] = 'PROCESSING' THEN 1 ELSE 0 END) AS ProcessingItems,
                    SUM(CASE WHEN [Status] = 'SUCCESS' THEN 1 ELSE 0 END) AS SuccessItems,
                    SUM(CASE WHEN [Status] = 'FAILED' THEN 1 ELSE 0 END) AS FailedItems,
                    SUM(CASE WHEN [Status] = 'EMPTY' THEN 1 ELSE 0 END) AS EmptyItems,
                    SUM(CASE WHEN [Status] = 'SKIPPED' THEN 1 ELSE 0 END) AS SkippedItems,
                    SUM(CASE WHEN [Status] = 'CANCELLED' THEN 1 ELSE 0 END) AS CancelledItems,
                    COUNT(1) AS TotalItems
                FROM [dbo].[TblKvhSyncBatchItem]
                WHERE [BatchId] = @batchId
                GROUP BY [BatchId]
            )
            UPDATE b
            SET [TotalItems] = c.[TotalItems],
                [PendingItems] = c.[PendingItems],
                [ProcessingItems] = c.[ProcessingItems],
                [SuccessItems] = c.[SuccessItems],
                [FailedItems] = c.[FailedItems],
                [EmptyItems] = c.[EmptyItems],
                [SkippedItems] = c.[SkippedItems],
                [Status] = CASE
                    WHEN c.[PendingItems] = 0 AND c.[ProcessingItems] = 0 AND c.[CancelledItems] = c.[TotalItems] THEN 'CANCELLED'
                    WHEN c.[PendingItems] = 0 AND c.[ProcessingItems] = 0 AND c.[FailedItems] > 0 THEN 'COMPLETED_WITH_ERRORS'
                    WHEN c.[PendingItems] = 0 AND c.[ProcessingItems] = 0 THEN 'COMPLETED'
                    ELSE 'RUNNING'
                END,
                [CompletedAtUtc] = CASE WHEN c.[PendingItems] = 0 AND c.[ProcessingItems] = 0 THEN SYSUTCDATETIME() ELSE NULL END
            FROM [dbo].[TblKvhSyncBatch] b
            INNER JOIN counts c ON c.[BatchId] = b.[ID]
            WHERE b.[ID] = @batchId
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CancelRequestedBatchesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE i
            SET [Status] = 'CANCELLED', [CompletedAtUtc] = SYSUTCDATETIME()
            FROM [dbo].[TblKvhSyncBatchItem] i
            INNER JOIN [dbo].[TblKvhSyncBatch] b ON b.[ID] = i.[BatchId]
            WHERE b.[Status] = 'CANCEL_REQUESTED'
              AND i.[Status] IN ('PENDING', 'RETRY_WAIT');

            UPDATE b
            SET [Status] = 'CANCELLED', [CompletedAtUtc] = SYSUTCDATETIME()
            FROM [dbo].[TblKvhSyncBatch] b
            WHERE b.[Status] = 'CANCEL_REQUESTED'
              AND NOT EXISTS (SELECT 1 FROM [dbo].[TblKvhSyncBatchItem] i WHERE i.[BatchId] = b.[ID] AND i.[Status] IN ('PENDING', 'RETRY_WAIT', 'PROCESSING'));
            """;
        await using var command = new SqlCommand(query, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<int>> ResolveDeviceIdsAsync(SqlConnection connection, KvhBatchCreateRequest request, string mode, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        var requestedIds = request.DeviceIds.Distinct().ToList();
        var query = mode switch
        {
            KvhBatchTypes.RetryFailed => """
                SELECT DISTINCT i.[DeviceId]
                FROM [dbo].[TblKvhSyncBatchItem] i
                INNER JOIN [dbo].[TblDevices] d ON d.[ID] = i.[DeviceId]
                WHERE i.[BatchId] = @sourceBatchId AND i.[Status] = 'FAILED'
                  AND NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NOT NULL
                  AND (@allowedTenantId IS NULL OR d.[TenantID] = @allowedTenantId)
                  AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
                """,
            KvhBatchTypes.SyncMissing => """
                SELECT DISTINCT d.[ID]
                FROM [dbo].[TblDevices] d
                OUTER APPLY (SELECT TOP 1 l.[ID] FROM [dbo].[TblKvhSubscriptionSyncLog] l WHERE l.[DeviceId] = d.[ID] ORDER BY l.[StartedAtUtc] DESC, l.[ID] DESC) lastLog
                WHERE NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NOT NULL
                  AND (@allowedTenantId IS NULL OR d.[TenantID] = @allowedTenantId)
                  AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
                  AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
                  AND (lastLog.[ID] IS NULL OR NOT EXISTS (SELECT 1 FROM [dbo].[TblKvhSubscription] s WHERE s.[DeviceId] = d.[ID] AND s.[IsCurrent] = 1))
                """,
            KvhBatchTypes.SyncAll => """
                SELECT DISTINCT d.[ID]
                FROM [dbo].[TblDevices] d
                WHERE NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NOT NULL
                  AND (@allowedTenantId IS NULL OR d.[TenantID] = @allowedTenantId)
                  AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
                  AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
                """,
            _ => BuildSelectedDevicesQuery(requestedIds)
        };

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)request.TenantId ?? DBNull.Value;
        command.Parameters.Add("@sourceBatchId", SqlDbType.BigInt).Value = (object?)request.SourceBatchId ?? DBNull.Value;
        for (var index = 0; index < requestedIds.Count; index++)
        {
            command.Parameters.Add($"@deviceId{index}", SqlDbType.Int).Value = requestedIds[index];
        }

        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Convert.ToInt32(reader[0]));
        }

        return result;
    }

    private static string BuildSelectedDevicesQuery(IReadOnlyList<int> requestedIds)
    {
        if (requestedIds.Count == 0)
        {
            return """
                SELECT TOP 0 d.[ID]
                FROM [dbo].[TblDevices] d
                """;
        }

        var parameterList = string.Join(", ", Enumerable.Range(0, requestedIds.Count).Select(index => $"@deviceId{index}"));
        return $"""
            SELECT DISTINCT d.[ID]
            FROM [dbo].[TblDevices] d
            WHERE d.[ID] IN ({parameterList})
              AND NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NOT NULL
              AND (@allowedTenantId IS NULL OR d.[TenantID] = @allowedTenantId)
              AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
            """;
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        var requestsPerMinute = Math.Clamp(settings.RequestsPerMinute, 1, 600);
        var spacing = TimeSpan.FromMilliseconds(60_000d / requestsPerMinute);
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            if (_nextAllowedRequestUtc > now)
            {
                await Task.Delay(_nextAllowedRequestUtc - now, cancellationToken);
            }

            _nextAllowedRequestUtc = DateTime.UtcNow.Add(spacing);
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static bool IsRetryable(string errorCode) =>
        errorCode.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        errorCode.Contains("429", StringComparison.OrdinalIgnoreCase) ||
        errorCode.Contains("5", StringComparison.OrdinalIgnoreCase) ||
        errorCode.Contains("usage_failed", StringComparison.OrdinalIgnoreCase) ||
        errorCode.Contains("list_failed", StringComparison.OrdinalIgnoreCase);

    private int BuildRetryDelaySeconds(int attemptCount)
    {
        var baseDelay = Math.Max(5, settings.RetryBaseDelaySeconds);
        var factor = Math.Pow(2, Math.Max(0, attemptCount - 1));
        var jitter = Random.Shared.Next(0, Math.Max(2, baseDelay / 5));
        return (int)Math.Min(600, (baseDelay * factor) + jitter);
    }

    private static string NormalizeMode(string mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is KvhBatchTypes.SyncSelected or KvhBatchTypes.SyncAll or KvhBatchTypes.SyncMissing or KvhBatchTypes.RetryFailed
            ? normalized
            : string.Empty;
    }

    private static async Task<bool> HasBatchTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string query = "SELECT CASE WHEN OBJECT_ID(N'[dbo].[TblKvhSyncBatch]', N'U') IS NOT NULL AND OBJECT_ID(N'[dbo].[TblKvhSyncBatchItem]', N'U') IS NOT NULL THEN 1 ELSE 0 END";
        await using var command = new SqlCommand(query, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static KvhBatchCreateResult Fail(string errorCode, string message) => new() { Success = false, ErrorCode = errorCode, Message = message };

    private static T MapBatch<T>(SqlDataReader reader) where T : KvhSyncBatchSummaryViewModel, new() => new()
    {
        Id = Convert.ToInt64(reader["ID"]),
        BatchType = reader["BatchType"]?.ToString() ?? string.Empty,
        Status = reader["Status"]?.ToString() ?? string.Empty,
        TotalItems = Convert.ToInt32(reader["TotalItems"]),
        PendingItems = Convert.ToInt32(reader["PendingItems"]),
        ProcessingItems = Convert.ToInt32(reader["ProcessingItems"]),
        SuccessItems = Convert.ToInt32(reader["SuccessItems"]),
        FailedItems = Convert.ToInt32(reader["FailedItems"]),
        EmptyItems = Convert.ToInt32(reader["EmptyItems"]),
        SkippedItems = Convert.ToInt32(reader["SkippedItems"]),
        CreatedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["CreatedAtUtc"]), DateTimeKind.Utc),
        StartedAtUtc = reader["StartedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["StartedAtUtc"]), DateTimeKind.Utc),
        CompletedAtUtc = reader["CompletedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["CompletedAtUtc"]), DateTimeKind.Utc),
        ErrorMessage = reader["ErrorMessage"]?.ToString() ?? string.Empty
    };

    private static KvhSyncBatchItemViewModel MapItem(SqlDataReader reader) => new()
    {
        Id = Convert.ToInt64(reader["ID"]),
        BatchId = Convert.ToInt64(reader["BatchId"]),
        DeviceId = Convert.ToInt32(reader["DeviceId"]),
        TerminalId = reader["TerminalId"]?.ToString() ?? string.Empty,
        TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
        Status = reader["Status"]?.ToString() ?? string.Empty,
        AttemptCount = Convert.ToInt32(reader["AttemptCount"]),
        NextAttemptAtUtc = reader["NextAttemptAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["NextAttemptAtUtc"]), DateTimeKind.Utc),
        StartedAtUtc = reader["StartedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["StartedAtUtc"]), DateTimeKind.Utc),
        CompletedAtUtc = reader["CompletedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["CompletedAtUtc"]), DateTimeKind.Utc),
        ReturnedCount = reader["ReturnedCount"] == DBNull.Value ? null : Convert.ToInt32(reader["ReturnedCount"]),
        ErrorCode = reader["ErrorCode"]?.ToString() ?? string.Empty,
        ErrorMessage = reader["ErrorMessage"]?.ToString() ?? string.Empty
    };
}
