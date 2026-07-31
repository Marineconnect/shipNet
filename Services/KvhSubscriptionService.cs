using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class KvhSubscriptionService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IOptions<KvhJobMonitorOptions> monitorOptions,
    IKvhCommandService kvhCommandService,
    ILogger<KvhSubscriptionService> logger) : IKvhSubscriptionService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task<KvhDeviceSyncResult> SyncDeviceSubscriptionAsync(int deviceId, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceAsync(connection, deviceId, allowedTenantId, allowedDeviceId, cancellationToken);
        if (device is null)
        {
            return ToDeviceSyncResult(Fail(deviceId, string.Empty, string.Empty, "device_not_found", "Device not found."));
        }

        if (string.IsNullOrWhiteSpace(device.TerminalId))
        {
            await InsertSyncLogAsync(connection, deviceId, string.Empty, string.Empty, false, "kvh_terminal_id_missing", "Terminal ID is unavailable.", null, 0, cancellationToken);
            return ToDeviceSyncResult(Fail(deviceId, string.Empty, string.Empty, "kvh_terminal_id_missing", "Terminal ID is unavailable."));
        }

        var accessToken = device.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken) || IsTokenExpired(device.TokenExpiredTime))
        {
            accessToken = await RefreshTokenAsync(connection, device.DeviceId, device.TerminalId, cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                await InsertSyncLogAsync(connection, deviceId, device.TerminalId, device.TrafficId, false, KvhErrorCodes.TokenRefreshFailed, "Could not refresh KVH token.", null, 0, cancellationToken);
                return ToDeviceSyncResult(Fail(deviceId, device.TerminalId, device.TrafficId, KvhErrorCodes.TokenRefreshFailed, "Could not refresh KVH token."));
            }
        }

        var usage = await RequestTerminalUsageAsync(device.TerminalId, accessToken, cancellationToken);
        if (!usage.Success && usage.HttpStatusCode == 401)
        {
            accessToken = await RefreshTokenAsync(connection, device.DeviceId, device.TerminalId, cancellationToken);
            usage = string.IsNullOrWhiteSpace(accessToken)
                ? usage
                : await RequestTerminalUsageAsync(device.TerminalId, accessToken, cancellationToken);
        }

        if (!usage.Success)
        {
                await InsertSyncLogAsync(connection, deviceId, device.TerminalId, device.TrafficId, false, usage.ErrorCode, usage.ErrorMessage, usage.RawResponse, 0, cancellationToken, httpStatusCode: usage.HttpStatusCode);
            return ToDeviceSyncResult(Fail(deviceId, device.TerminalId, device.TrafficId, usage.ErrorCode, usage.ErrorMessage, usage.RawResponse));
        }

        var trafficId = Normalize(usage.TrafficId);
        if (string.IsNullOrWhiteSpace(trafficId))
        {
            await InsertSyncLogAsync(connection, deviceId, device.TerminalId, string.Empty, false, "kvh_traffic_id_missing", "Traffic ID is unavailable.", usage.RawResponse, 0, cancellationToken, httpStatusCode: usage.HttpStatusCode);
            return ToDeviceSyncResult(Fail(deviceId, device.TerminalId, string.Empty, "kvh_traffic_id_missing", "Traffic ID is unavailable.", usage.RawResponse));
        }

        var result = await SyncForDeviceAsync(deviceId, device.TerminalId, accessToken, trafficId, cancellationToken);
        return ToDeviceSyncResult(result);
    }

    public async Task<KvhSubscriptionSyncResult> SyncForDeviceAsync(int deviceId, string terminalId, string accessToken, string? trafficId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceAsync(connection, deviceId, null, null, cancellationToken);
        if (device is null)
        {
            return Fail(deviceId, terminalId, trafficId ?? string.Empty, "device_not_found", "Device not found.");
        }

        var resolvedTrafficId = Normalize(trafficId);
        var usedStoredTrafficId = false;
        if (string.IsNullOrWhiteSpace(resolvedTrafficId))
        {
            resolvedTrafficId = Normalize(device.TrafficId);
            usedStoredTrafficId = !string.IsNullOrWhiteSpace(resolvedTrafficId);
        }

        if (string.IsNullOrWhiteSpace(resolvedTrafficId))
        {
            await InsertSyncLogAsync(connection, deviceId, terminalId, string.Empty, false, "kvh_traffic_id_missing", "Traffic ID is unavailable.", null, 0, cancellationToken);
            return Fail(deviceId, terminalId, string.Empty, "kvh_traffic_id_missing", "Traffic ID is unavailable.");
        }

        var startedAt = DateTime.UtcNow;
        var response = await SendKvhAsync(HttpMethod.Get, $"https://api.mykvh.com/v3/subscriptions/{Uri.EscapeDataString(resolvedTrafficId)}", accessToken, null, "kvh_subscription_list_failed", cancellationToken);
        if (!response.Success && response.HttpStatusCode == 401)
        {
            var refreshedToken = await RefreshTokenAsync(connection, deviceId, terminalId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(refreshedToken))
            {
                response = await SendKvhAsync(HttpMethod.Get, $"https://api.mykvh.com/v3/subscriptions/{Uri.EscapeDataString(resolvedTrafficId)}", refreshedToken, null, "kvh_subscription_list_failed", cancellationToken);
            }
        }

        if (!response.Success)
        {
            await InsertSyncLogAsync(connection, deviceId, terminalId, resolvedTrafficId, false, response.ErrorCode, response.ErrorMessage, response.RawResponse, 0, cancellationToken, startedAt, httpStatusCode: response.HttpStatusCode);
            return Fail(deviceId, terminalId, resolvedTrafficId, response.ErrorCode, response.ErrorMessage, response.RawResponse);
        }

        List<ParsedSubscription> entries;
        try
        {
            entries = ParseSubscriptionList(response.RawResponse, deviceId, terminalId, resolvedTrafficId);
        }
        catch (JsonException)
        {
            await InsertSyncLogAsync(connection, deviceId, terminalId, resolvedTrafficId, false, "kvh_subscription_list_failed", "Subscription API returned invalid JSON.", response.RawResponse, 0, cancellationToken, startedAt, httpStatusCode: response.HttpStatusCode);
            return Fail(deviceId, terminalId, resolvedTrafficId, "kvh_subscription_list_failed", "Subscription API returned invalid JSON.", response.RawResponse);
        }

        if (entries.Count == 0)
        {
            const string emptyMessage = "KVH returned no subscription entries for this Traffic ID.";
            await InsertSyncLogAsync(connection, deviceId, terminalId, resolvedTrafficId, true, string.Empty, emptyMessage, response.RawResponse, 0, cancellationToken, startedAt, httpStatusCode: response.HttpStatusCode);
            return new KvhSubscriptionSyncResult
            {
                Success = false,
                DeviceId = deviceId,
                TerminalId = terminalId,
                TrafficId = resolvedTrafficId,
                ReturnedCount = 0,
                CurrentCount = 0,
                RawResponse = response.RawResponse,
                UsedStoredTrafficId = usedStoredTrafficId,
                ErrorCode = "kvh_subscription_empty",
                Message = emptyMessage,
                MessageEn = emptyMessage
            };
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var deactivatedCount = await MarkDeviceSubscriptionsNotCurrentAsync(connection, transaction, deviceId, cancellationToken);
        foreach (var entry in entries)
        {
            await UpsertSubscriptionAsync(connection, transaction, entry, cancellationToken);
        }

        var summary = SelectSummary(entries);
        await UpdateDeviceSubscriptionSummaryAsync(connection, transaction, deviceId, resolvedTrafficId, summary, cancellationToken);
        await InsertSyncLogAsync(connection, deviceId, terminalId, resolvedTrafficId, true, string.Empty, string.Empty, response.RawResponse, entries.Count, cancellationToken, startedAt, transaction, response.HttpStatusCode);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Synced {Count} KVH subscriptions for DeviceId {DeviceId}, TerminalId {TerminalId}, TrafficId {TrafficId}", entries.Count, deviceId, terminalId, resolvedTrafficId);
        return new KvhSubscriptionSyncResult
        {
            Success = true,
            DeviceId = deviceId,
            TerminalId = terminalId,
            TrafficId = resolvedTrafficId,
            ReturnedCount = entries.Count,
            UpdatedCount = entries.Count,
            DeactivatedCount = deactivatedCount,
            CurrentCount = entries.Count,
            RawResponse = response.RawResponse,
            UsedStoredTrafficId = usedStoredTrafficId,
            Message = "KVH subscriptions synchronized.",
            MessageEn = "KVH subscriptions synchronized."
        };
    }

    public async Task<KvhSolutionPageResult> GetSolutionsAsync(KvhSolutionFilter filter, int page, int pageSize, int? allowedTenantId = null, int? allowedDeviceId = null, bool canManage = false, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is 20 or 50 or 100 or 140 ? pageSize : 20;
        filter.Search = NormalizeNullable(filter.Search);
        filter.Status = NormalizeNullable(filter.Status);
        filter.Region = NormalizeNullable(filter.Region);
        filter.SyncState = NormalizeNullable(filter.SyncState);
        if (allowedTenantId.HasValue)
        {
            filter.TenantId = allowedTenantId;
        }

        var items = new List<KvhSolutionListItemViewModel>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var where = BuildSolutionWhere(filter, allowedTenantId, allowedDeviceId);
        var countSql = $"""
            SELECT COUNT(DISTINCT d.[ID])
            FROM [dbo].[TblDevices] d
            LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = d.[TenantID]
            OUTER APPLY (
                SELECT TOP 1 *
                FROM [dbo].[TblKvhSubscription] sx
                WHERE sx.[DeviceId] = d.[ID]
                  AND sx.[IsCurrent] = 1
                  AND (@status IS NULL OR sx.[Status] = @status)
                  AND (@region IS NULL OR sx.[Region] = @region)
                ORDER BY sx.[LastSeenAtUtc] DESC, sx.[ID] DESC
            ) s
            OUTER APPLY (SELECT TOP 1 * FROM [dbo].[TblKvhSubscriptionSyncLog] l WHERE l.[DeviceId] = d.[ID] ORDER BY l.[StartedAtUtc] DESC, l.[ID] DESC) log
            OUTER APPLY (
                SELECT TOP 1 CAST(1 AS bit) AS [HasPendingCommand], [CooldownUntilUtc]
                FROM [dbo].[TblKvhCommand] c
                WHERE c.[DeviceId] = d.[ID]
                  AND c.[CommandType] IN ('SUBSCRIPTION_PAUSE', 'SUBSCRIPTION_RESUME', 'SUBSCRIPTION_CANCEL_SCHEDULE')
                  AND c.[CommandStatus] NOT IN ('FAILED', 'TIMEOUT', 'VERIFIED', 'VERIFICATION_MISMATCH', 'VERIFICATION_TIMEOUT')
                ORDER BY c.[RequestedAtUtc] DESC, c.[ID] DESC
            ) pending
            WHERE {where}
            """;
        await using (var countCommand = new SqlCommand(countSql, connection))
        {
            AddSolutionParameters(countCommand, filter, allowedTenantId, allowedDeviceId);
            var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0);
            var totalPages = pageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
            if (totalPages > 0 && page > totalPages)
            {
                page = totalPages;
            }

            var query = $"""
                SELECT d.[ID], d.[DeviceName], d.[DeviceCode], d.[VesselName], d.[TenantID], t.[TenantName], d.[KITNumber], d.[Availability], d.[LastUpdateTime], d.[TrafficId],
                       s.[ID] AS [KvhSubscriptionId], s.[Region], s.[PlanName], s.[Status], s.[ScheduledAction], s.[ScheduleId], s.[ScheduledEffectiveDateUtc],
                       s.[AllowanceGb], s.[LastSeenAtUtc], log.[StartedAtUtc] AS [LastSyncAtUtc], log.[Success] AS [LastSyncSuccess], log.[ErrorCode] AS [LastSyncErrorCode], log.[ReturnedCount] AS [LastSyncReturnedCount],
                       pending.[HasPendingCommand], pending.[CooldownUntilUtc]
                FROM [dbo].[TblDevices] d
                LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = d.[TenantID]
                OUTER APPLY (
                    SELECT TOP 1 *
                    FROM [dbo].[TblKvhSubscription] sx
                    WHERE sx.[DeviceId] = d.[ID]
                      AND sx.[IsCurrent] = 1
                      AND (@status IS NULL OR sx.[Status] = @status)
                      AND (@region IS NULL OR sx.[Region] = @region)
                    ORDER BY sx.[LastSeenAtUtc] DESC, sx.[ID] DESC
                ) s
                OUTER APPLY (SELECT TOP 1 * FROM [dbo].[TblKvhSubscriptionSyncLog] l WHERE l.[DeviceId] = d.[ID] ORDER BY l.[StartedAtUtc] DESC, l.[ID] DESC) log
                OUTER APPLY (
                    SELECT TOP 1 CAST(1 AS bit) AS [HasPendingCommand], [CooldownUntilUtc]
                    FROM [dbo].[TblKvhCommand] c
                    WHERE c.[DeviceId] = d.[ID]
                      AND c.[CommandType] IN ('SUBSCRIPTION_PAUSE', 'SUBSCRIPTION_RESUME', 'SUBSCRIPTION_CANCEL_SCHEDULE')
                      AND c.[CommandStatus] NOT IN ('FAILED', 'TIMEOUT', 'VERIFIED', 'VERIFICATION_MISMATCH', 'VERIFICATION_TIMEOUT')
                    ORDER BY c.[RequestedAtUtc] DESC, c.[ID] DESC
                ) pending
                WHERE {where}
                ORDER BY d.[VesselName], d.[DeviceCode], d.[ID]
                OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
                """;
            await using var command = new SqlCommand(query, connection);
            AddSolutionParameters(command, filter, allowedTenantId, allowedDeviceId);
            command.Parameters.Add("@offset", SqlDbType.Int).Value = (page - 1) * pageSize;
            command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(MapListItem(reader));
                }
            }

            var tenants = await GetTenantOptionsAsync(connection, allowedTenantId, cancellationToken);
            return new KvhSolutionPageResult
            {
                Items = items,
                Tenants = tenants.ToList(),
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = total,
                Filter = filter,
                IsTenantScoped = allowedTenantId.HasValue,
                CanManageSolutions = canManage
            };
        }
    }

    public async Task<KvhSolutionDetailViewModel?> GetSolutionDetailAsync(int deviceId, int? allowedTenantId = null, int? allowedDeviceId = null, bool canManage = false, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var device = await GetDeviceAsync(connection, deviceId, allowedTenantId, allowedDeviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var detail = new KvhSolutionDetailViewModel
        {
            DeviceId = device.DeviceId,
            DeviceName = device.DeviceName,
            TerminalId = device.TerminalId,
            VesselName = device.VesselName,
            TenantName = device.TenantName,
            KitNumber = device.KitNumber,
            KitId = device.KitId,
            ServiceLine = device.ServiceLine,
            Availability = device.Availability,
            LastUpdateTimeUtc = device.LastUpdateTimeUtc,
            TrafficId = device.TrafficId,
            CanManageSolutions = canManage
        };

        const string subscriptionSql = """
            SELECT *
            FROM [dbo].[TblKvhSubscription]
            WHERE [DeviceId] = @deviceId AND [IsCurrent] = 1
            ORDER BY [Region], [EffectiveDateUtc] DESC, [ID] DESC
            """;
        await using (var command = new SqlCommand(subscriptionSql, connection))
        {
            command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    detail.CurrentSubscriptions.Add(MapEntry(reader));
                }
            }
        }

        const string logSql = "SELECT TOP 20 * FROM [dbo].[TblKvhSubscriptionSyncLog] WHERE [DeviceId] = @deviceId ORDER BY [StartedAtUtc] DESC, [ID] DESC";
        await using (var command = new SqlCommand(logSql, connection))
        {
            command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    detail.SyncLogs.Add(new KvhSubscriptionSyncLogViewModel
                    {
                        Id = Convert.ToInt64(reader["ID"]),
                        StartedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["StartedAtUtc"]), DateTimeKind.Utc),
                        CompletedAtUtc = reader["CompletedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["CompletedAtUtc"]), DateTimeKind.Utc),
                        Success = reader["Success"] != DBNull.Value && Convert.ToBoolean(reader["Success"]),
                        ErrorCode = reader["ErrorCode"]?.ToString() ?? string.Empty,
                        ErrorMessage = reader["ErrorMessage"]?.ToString() ?? string.Empty,
                        TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
                        ReturnedCount = reader["ReturnedCount"] == DBNull.Value ? 0 : Convert.ToInt32(reader["ReturnedCount"]),
                        HttpStatusCode = reader["HttpStatusCode"] == DBNull.Value ? null : Convert.ToInt32(reader["HttpStatusCode"]),
                        SyncSource = reader["SyncSource"]?.ToString() ?? string.Empty,
                        ResponseJson = reader["ResponseJson"]?.ToString() ?? string.Empty
                    });
                }
            }
        }

        detail.RecentCommands = (await kvhCommandService.GetRecentCommandsAsync(deviceId, allowedTenantId, allowedDeviceId, cancellationToken)).ToList();
        return detail;
    }

    public Task<KvhCommandSubmitResult> PauseAsync(KvhSolutionCommandRequest request, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default) =>
        SubmitSubscriptionCommandAsync(request, KvhCommandTypes.SubscriptionPause, HttpMethod.Post, "pause", userId, requestedBy, allowedTenantId, allowedDeviceId, cancellationToken);

    public Task<KvhCommandSubmitResult> ResumeAsync(KvhSolutionCommandRequest request, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default) =>
        SubmitSubscriptionCommandAsync(request, KvhCommandTypes.SubscriptionResume, HttpMethod.Post, "resume", userId, requestedBy, allowedTenantId, allowedDeviceId, cancellationToken);

    public Task<KvhCommandSubmitResult> CancelScheduleAsync(KvhSolutionCommandRequest request, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default) =>
        SubmitSubscriptionCommandAsync(request, KvhCommandTypes.SubscriptionCancelSchedule, HttpMethod.Delete, "cancel", userId, requestedBy, allowedTenantId, allowedDeviceId, cancellationToken);

    private async Task<KvhCommandSubmitResult> SubmitSubscriptionCommandAsync(KvhSolutionCommandRequest request, string commandType, HttpMethod method, string action, int? userId, string requestedBy, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var context = await GetSubscriptionCommandContextAsync(connection, request, allowedTenantId, allowedDeviceId, cancellationToken);
        if (!context.Success)
        {
            return context.ToSubmitResult(request.DeviceId);
        }

        var cooldown = await CheckCooldownAsync(connection, context.TerminalId, context.TrafficId, cancellationToken);
        if (!cooldown.Allowed)
        {
            return new KvhCommandSubmitResult
            {
                Success = false,
                ErrorCode = "kvh_command_cooldown",
                Message = $"Please wait {cooldown.RemainingSeconds} seconds before sending another KVH subscription command.",
                MessageEn = $"Please wait {cooldown.RemainingSeconds} seconds before sending another KVH subscription command.",
                DeviceId = context.DeviceId,
                TerminalId = context.TerminalId,
                RemainingSeconds = cooldown.RemainingSeconds,
                NextAllowedAtUtc = cooldown.NextAllowedAtUtc
            };
        }

        var uri = commandType == KvhCommandTypes.SubscriptionCancelSchedule
            ? $"https://api.mykvh.com/v3/subscriptions/{Uri.EscapeDataString(context.TrafficId)}/regions/{Uri.EscapeDataString(context.Region)}/schedules/{Uri.EscapeDataString(context.ScheduleId)}"
            : $"https://api.mykvh.com/v3/subscriptions/{Uri.EscapeDataString(context.TrafficId)}/regions/{Uri.EscapeDataString(context.Region)}/{action}";
        var requestJson = JsonSerializer.Serialize(new { context.DeviceId, context.TerminalId, context.TrafficId, context.Region, context.ScheduleId, context.KvhSubscriptionId, action });
        var commandId = await InsertSubscriptionCommandAsync(connection, context, commandType, requestJson, userId, requestedBy, cooldown.NextAllowedAtUtc ?? DateTime.UtcNow.AddMinutes(5), cancellationToken);
        var response = await SendKvhAsync(method, uri, context.AccessToken, null, commandType == KvhCommandTypes.SubscriptionResume ? "kvh_resume_submit_failed" : "kvh_pause_submit_failed", cancellationToken);
        var jobId = KvhJsonHelpers.ExtractJobId(response.RawResponse);

        if (!response.Success || string.IsNullOrWhiteSpace(jobId))
        {
            await MarkSubmitFailedAsync(connection, commandId, response.HttpStatusCode, response.RawResponse, string.IsNullOrWhiteSpace(jobId) ? KvhErrorCodes.MissingJobId : response.ErrorCode, string.IsNullOrWhiteSpace(jobId) ? "KVH accepted the request but did not return a job id." : response.ErrorMessage, cancellationToken);
            return new KvhCommandSubmitResult
            {
                Success = false,
                ErrorCode = string.IsNullOrWhiteSpace(jobId) ? KvhErrorCodes.MissingJobId : response.ErrorCode,
                Message = string.IsNullOrWhiteSpace(jobId) ? "KVH accepted the request but did not return a job id." : response.ErrorMessage,
                MessageEn = string.IsNullOrWhiteSpace(jobId) ? "KVH accepted the request but did not return a job id." : response.ErrorMessage,
                DeviceId = context.DeviceId,
                TerminalId = context.TerminalId,
                CommandId = commandId,
                RawResponse = response.RawResponse,
                HttpStatusCode = response.HttpStatusCode
            };
        }

        await MarkSubmittedAsync(connection, commandId, jobId, response.HttpStatusCode, response.RawResponse, cancellationToken);
        return new KvhCommandSubmitResult
        {
            Success = true,
            Message = "KVH accepted the subscription command. The job is being monitored.",
            MessageEn = "KVH accepted the subscription command. The job is being monitored.",
            DeviceId = context.DeviceId,
            TerminalId = context.TerminalId,
            CommandId = commandId,
            JobId = jobId,
            RawResponse = response.RawResponse,
            HttpStatusCode = response.HttpStatusCode
        };
    }

    private async Task<CommandContext> GetSubscriptionCommandContextAsync(SqlConnection connection, KvhSolutionCommandRequest request, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 d.[ID], d.[DeviceCode], d.[TokenString], d.[TokenExpiredTime], s.[ID] AS [KvhSubscriptionId], s.[TrafficId], s.[Region], s.[ScheduleId]
            FROM [dbo].[TblKvhSubscription] s
            INNER JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
            WHERE d.[ID] = @deviceId
              AND s.[ID] = @subscriptionId
              AND s.[IsCurrent] = 1
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = request.DeviceId;
        command.Parameters.Add("@subscriptionId", SqlDbType.BigInt).Value = request.KvhSubscriptionId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        CommandContext context;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return CommandContext.Fail("kvh_subscription_not_found", "KVH subscription was not found or you do not have access.");
            }

            context = new CommandContext
            {
                Success = true,
                DeviceId = Convert.ToInt32(reader["ID"]),
                TerminalId = reader["DeviceCode"]?.ToString() ?? string.Empty,
                AccessToken = reader["TokenString"]?.ToString() ?? string.Empty,
                TokenExpiredTime = reader["TokenExpiredTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["TokenExpiredTime"]),
                KvhSubscriptionId = Convert.ToInt64(reader["KvhSubscriptionId"]),
                TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
                Region = reader["Region"]?.ToString() ?? string.Empty,
                ScheduleId = reader["ScheduleId"]?.ToString() ?? string.Empty
            };
        }

        if (string.IsNullOrWhiteSpace(context.TrafficId))
        {
            return CommandContext.Fail("kvh_traffic_id_missing", "Traffic ID is unavailable.", context.TerminalId);
        }

        if (string.IsNullOrWhiteSpace(context.Region))
        {
            return CommandContext.Fail("kvh_subscription_region_missing", "KVH subscription region is unavailable.", context.TerminalId);
        }

        if (string.IsNullOrWhiteSpace(context.AccessToken) || IsTokenExpired(context.TokenExpiredTime))
        {
            var token = await RefreshTokenAsync(connection, context.DeviceId, context.TerminalId, cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return CommandContext.Fail(KvhErrorCodes.TokenRefreshFailed, "Could not refresh KVH token.", context.TerminalId);
            }

            context.AccessToken = token;
        }

        return context;
    }

    private async Task<string> RefreshTokenAsync(SqlConnection connection, int deviceId, string terminalId, CancellationToken cancellationToken)
    {
        var credentials = await GetApiCredentialsAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            return string.Empty;
        }

        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            client_id = credentials.ClientId,
            client_secret = credentials.ClientSecret,
            audience = "https://api.mykvh.com",
            grant_type = "jwt_bearer",
            scope = $"asset#{terminalId}"
        }), Encoding.UTF8, "application/json");
        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync("https://mapi.mykvh.com/oauth/token", content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(raw);
        var accessToken = KvhJsonHelpers.FindStringValue(document.RootElement, "access_token");
        var expiresIn = KvhJsonHelpers.FindLongValue(document.RootElement, "expires_in") ?? 3600;
        const string update = "UPDATE [dbo].[TblDevices] SET [TokenString] = @token, [TokenExpiredTime] = @expires WHERE [ID] = @id";
        await using var command = new SqlCommand(update, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@token", SqlDbType.NVarChar, -1).Value = accessToken;
        command.Parameters.Add("@expires", SqlDbType.DateTime2).Value = DateTime.UtcNow.AddSeconds(expiresIn);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return accessToken;
    }

    private static List<ParsedSubscription> ParseSubscriptionList(string rawResponse, int deviceId, string terminalId, string trafficId)
    {
        using var document = JsonDocument.Parse(rawResponse);
        var items = ResolveSubscriptionArray(document.RootElement);
        var result = new List<ParsedSubscription>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var planElement = TryGetChild(item, "plan", "plan_details", "planDetails");
            var optInElement = TryGetChild(item, "opt_in", "optIn", "optin");
            var region = KvhJsonHelpers.FindStringValue(item, "region", "region_code", "regionCode");
            var planName = planElement.HasValue ? KvhJsonHelpers.FindStringValue(planElement.Value, "name", "plan_name", "planName", "code", "id") : KvhJsonHelpers.FindStringValue(item, "plan", "plan_name", "planName");
            var providerKey = KvhJsonHelpers.FindStringValue(item, "subscription_id", "subscriptionId", "id");
            var key = !string.IsNullOrWhiteSpace(providerKey)
                ? providerKey
                : $"{trafficId}:{region}:{Normalize(planName)}";
            result.Add(new ParsedSubscription
            {
                DeviceId = deviceId,
                TerminalId = terminalId,
                TrafficId = KvhJsonHelpers.FindStringValue(item, "traffic_id", "trafficId", "trafficID") is { Length: > 0 } itemTraffic ? itemTraffic : trafficId,
                SubscriptionKey = key,
                Status = KvhJsonHelpers.FindStringValue(item, "status", "state"),
                PlanName = planName,
                PlanJson = planElement.HasValue ? planElement.Value.GetRawText() : string.Empty,
                OptInStatus = optInElement.HasValue ? KvhJsonHelpers.FindStringValue(optInElement.Value, "status", "state", "enabled") : KvhJsonHelpers.FindStringValue(item, "opt_in_status", "optInStatus"),
                OptInJson = optInElement.HasValue ? optInElement.Value.GetRawText() : string.Empty,
                ScheduledAction = KvhJsonHelpers.FindStringValue(item, "scheduled_action", "scheduledAction", "schedule_action", "action"),
                ScheduleId = KvhJsonHelpers.FindStringValue(item, "schedule_id", "scheduleId"),
                ScheduledEffectiveDateUtc = TryFindDate(item, "scheduled_effective_date", "scheduledEffectiveDate", "schedule_effective_date", "schedule.effective_date", "schedule.effectiveDate"),
                Region = region,
                Proration = TryFindDecimal(item, "proration"),
                AllowanceGb = ResolveAllowanceGb(item),
                EffectiveDateUtc = TryFindDate(item, "effective_date", "effectiveDate"),
                RawSubscriptionJson = item.GetRawText()
            });
        }

        return result;
    }

    private static IReadOnlyList<JsonElement> ResolveSubscriptionArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(item => item.Clone()).ToList();
        }

        foreach (var name in new[] { "subscriptions", "items", "results" })
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array)
            {
                return child.EnumerateArray().Select(item => item.Clone()).ToList();
            }
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "subscriptions", "items", "results" })
            {
                if (data.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array)
                {
                    return child.EnumerateArray().Select(item => item.Clone()).ToList();
                }
            }
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
        {
            return dataArray.EnumerateArray().Select(item => item.Clone()).ToList();
        }

        return root.ValueKind == JsonValueKind.Object ? [root.Clone()] : [];
    }

    private static JsonElement? TryGetChild(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var child) && (child.ValueKind == JsonValueKind.Object || child.ValueKind == JsonValueKind.Array))
            {
                return child;
            }
        }

        return null;
    }

    private static DateTime? TryFindDate(JsonElement element, params string[] names)
    {
        var value = KvhJsonHelpers.FindStringValue(element, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcDateTime : null;
    }

    private static decimal? TryFindDecimal(JsonElement element, params string[] names)
    {
        var value = KvhJsonHelpers.FindStringValue(element, names);
        return decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static decimal? BytesToGb(decimal? value) => value.HasValue ? Math.Round(value.Value / 1_000_000_000m, 2) : null;

    private static decimal? ResolveAllowanceGb(JsonElement item)
    {
        var explicitGb = TryFindDecimal(item, "allowance_gb", "allowanceGb", "data_allowance_gb", "dataAllowanceGb");
        if (explicitGb.HasValue)
        {
            return Math.Round(explicitGb.Value, 2);
        }

        var explicitBytes = TryFindDecimal(item, "allowance_bytes", "allowanceBytes", "data_allowance_bytes", "dataAllowanceBytes");
        if (explicitBytes.HasValue)
        {
            return BytesToGb(explicitBytes);
        }

        if (item.TryGetProperty("allowance", out var allowance) && allowance.ValueKind == JsonValueKind.Object)
        {
            var value = TryFindDecimal(allowance, "value", "amount", "bytes", "gb");
            var unit = KvhJsonHelpers.FindStringValue(allowance, "unit", "units");
            if (!value.HasValue)
            {
                return null;
            }

            if (unit.Contains("byte", StringComparison.OrdinalIgnoreCase) || allowance.TryGetProperty("bytes", out _))
            {
                return BytesToGb(value);
            }

            if (unit.Equals("gb", StringComparison.OrdinalIgnoreCase) || unit.Equals("gigabyte", StringComparison.OrdinalIgnoreCase) || allowance.TryGetProperty("gb", out _))
            {
                return Math.Round(value.Value, 2);
            }

            return value.Value > 100_000m ? BytesToGb(value) : Math.Round(value.Value, 2);
        }

        var genericAllowance = TryFindDecimal(item, "allowance", "data_allowance", "dataAllowance");
        return genericAllowance.HasValue && genericAllowance.Value > 100_000m
            ? BytesToGb(genericAllowance)
            : genericAllowance.HasValue ? Math.Round(genericAllowance.Value, 2) : null;
    }

    private static ParsedSubscription? SelectSummary(IReadOnlyList<ParsedSubscription> entries)
    {
        return entries
            .OrderBy(entry => entry.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ? 0 :
                entry.Status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) || entry.Status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenByDescending(entry => entry.EffectiveDateUtc ?? DateTime.MinValue)
            .ThenBy(entry => entry.Region)
            .FirstOrDefault();
    }

    private static async Task<int> MarkDeviceSubscriptionsNotCurrentAsync(SqlConnection connection, SqlTransaction transaction, int deviceId, CancellationToken cancellationToken)
    {
        const string query = "UPDATE [dbo].[TblKvhSubscription] SET [IsCurrent] = 0 WHERE [DeviceId] = @deviceId";
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSubscriptionAsync(SqlConnection connection, SqlTransaction transaction, ParsedSubscription entry, CancellationToken cancellationToken)
    {
        const string query = """
            MERGE [dbo].[TblKvhSubscription] AS target
            USING (SELECT @deviceId AS [DeviceId], @subscriptionKey AS [SubscriptionKey]) AS source
            ON target.[DeviceId] = source.[DeviceId] AND target.[SubscriptionKey] = source.[SubscriptionKey]
            WHEN MATCHED THEN UPDATE SET
                [TerminalId] = @terminalId, [TrafficId] = @trafficId, [Status] = @status, [PlanName] = @planName, [PlanJson] = @planJson,
                [OptInStatus] = @optInStatus, [OptInJson] = @optInJson, [ScheduledAction] = @scheduledAction, [ScheduleId] = @scheduleId,
                [ScheduledEffectiveDateUtc] = @scheduledEffectiveDateUtc, [Region] = @region, [Proration] = @proration, [AllowanceGb] = @allowanceGb,
                [EffectiveDateUtc] = @effectiveDateUtc, [RawSubscriptionJson] = @rawSubscriptionJson, [IsCurrent] = 1, [LastSeenAtUtc] = SYSUTCDATETIME(), [UpdatedAtUtc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                ([DeviceId], [TerminalId], [TrafficId], [SubscriptionKey], [Status], [PlanName], [PlanJson], [OptInStatus], [OptInJson], [ScheduledAction], [ScheduleId],
                 [ScheduledEffectiveDateUtc], [Region], [Proration], [AllowanceGb], [EffectiveDateUtc], [RawSubscriptionJson], [IsCurrent], [FirstSeenAtUtc], [LastSeenAtUtc], [UpdatedAtUtc])
            VALUES
                (@deviceId, @terminalId, @trafficId, @subscriptionKey, @status, @planName, @planJson, @optInStatus, @optInJson, @scheduledAction, @scheduleId,
                 @scheduledEffectiveDateUtc, @region, @proration, @allowanceGb, @effectiveDateUtc, @rawSubscriptionJson, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        AddEntryParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddEntryParameters(SqlCommand command, ParsedSubscription entry)
    {
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = entry.DeviceId;
        command.Parameters.Add("@terminalId", SqlDbType.NVarChar, 200).Value = entry.TerminalId;
        command.Parameters.Add("@trafficId", SqlDbType.NVarChar, 200).Value = entry.TrafficId;
        command.Parameters.Add("@subscriptionKey", SqlDbType.NVarChar, 450).Value = entry.SubscriptionKey;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 80).Value = Db(entry.Status);
        command.Parameters.Add("@planName", SqlDbType.NVarChar, 255).Value = Db(entry.PlanName);
        command.Parameters.Add("@planJson", SqlDbType.NVarChar, -1).Value = Db(RedactJson(entry.PlanJson));
        command.Parameters.Add("@optInStatus", SqlDbType.NVarChar, 80).Value = Db(entry.OptInStatus);
        command.Parameters.Add("@optInJson", SqlDbType.NVarChar, -1).Value = Db(RedactJson(entry.OptInJson));
        command.Parameters.Add("@scheduledAction", SqlDbType.NVarChar, 120).Value = Db(entry.ScheduledAction);
        command.Parameters.Add("@scheduleId", SqlDbType.NVarChar, 200).Value = Db(entry.ScheduleId);
        command.Parameters.Add("@scheduledEffectiveDateUtc", SqlDbType.DateTime2).Value = Db(entry.ScheduledEffectiveDateUtc);
        command.Parameters.Add("@region", SqlDbType.NVarChar, 120).Value = Db(entry.Region);
        command.Parameters.Add("@proration", SqlDbType.Decimal).Value = Db(entry.Proration);
        command.Parameters.Add("@allowanceGb", SqlDbType.Decimal).Value = Db(entry.AllowanceGb);
        command.Parameters.Add("@effectiveDateUtc", SqlDbType.DateTime2).Value = Db(entry.EffectiveDateUtc);
        command.Parameters.Add("@rawSubscriptionJson", SqlDbType.NVarChar, -1).Value = Db(RedactJson(entry.RawSubscriptionJson));
    }

    private static async Task UpdateDeviceSubscriptionSummaryAsync(SqlConnection connection, SqlTransaction transaction, int deviceId, string trafficId, ParsedSubscription? summary, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [dbo].[TblDevices]
            SET [TrafficId] = CASE WHEN NULLIF(@trafficId, '') IS NULL THEN [TrafficId] ELSE @trafficId END,
                [KvhSubscriptionStatus] = @status,
                [KvhSubscriptionPlan] = @planName,
                [KvhSubscriptionRegion] = @region,
                [KvhSubscriptionScheduledAction] = @scheduledAction,
                [KvhSubscriptionScheduleId] = @scheduleId,
                [KvhSubscriptionEffectiveDateUtc] = @effectiveDateUtc,
                [KvhSubscriptionLastSyncUtc] = SYSUTCDATETIME()
            WHERE [ID] = @deviceId
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@trafficId", SqlDbType.NVarChar, 200).Value = trafficId;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 80).Value = Db(summary?.Status);
        command.Parameters.Add("@planName", SqlDbType.NVarChar, 255).Value = Db(summary?.PlanName);
        command.Parameters.Add("@region", SqlDbType.NVarChar, 120).Value = Db(summary?.Region);
        command.Parameters.Add("@scheduledAction", SqlDbType.NVarChar, 120).Value = Db(summary?.ScheduledAction);
        command.Parameters.Add("@scheduleId", SqlDbType.NVarChar, 200).Value = Db(summary?.ScheduleId);
        command.Parameters.Add("@effectiveDateUtc", SqlDbType.DateTime2).Value = Db(summary?.EffectiveDateUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSyncLogAsync(SqlConnection connection, int deviceId, string terminalId, string trafficId, bool success, string errorCode, string errorMessage, string? rawResponse, int returnedCount, CancellationToken cancellationToken, DateTime? startedAt = null, SqlTransaction? transaction = null, int? httpStatusCode = null)
    {
        const string query = """
            INSERT INTO [dbo].[TblKvhSubscriptionSyncLog]
                ([DeviceId], [TerminalId], [TrafficId], [StartedAtUtc], [CompletedAtUtc], [Success], [ErrorCode], [ErrorMessage], [ResponseJson], [ReturnedCount], [HttpStatusCode], [SyncSource])
            VALUES
                (@deviceId, @terminalId, @trafficId, @startedAt, SYSUTCDATETIME(), @success, NULLIF(@errorCode, ''), NULLIF(@errorMessage, ''), @responseJson, @returnedCount, @httpStatusCode, @syncSource)
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@terminalId", SqlDbType.NVarChar, 200).Value = Db(terminalId);
        command.Parameters.Add("@trafficId", SqlDbType.NVarChar, 200).Value = Db(trafficId);
        command.Parameters.Add("@startedAt", SqlDbType.DateTime2).Value = startedAt ?? DateTime.UtcNow;
        command.Parameters.Add("@success", SqlDbType.Bit).Value = success;
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode;
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = errorMessage;
        command.Parameters.Add("@responseJson", SqlDbType.NVarChar, -1).Value = Db(RedactJson(rawResponse ?? string.Empty));
        command.Parameters.Add("@returnedCount", SqlDbType.Int).Value = returnedCount;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)httpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@syncSource", SqlDbType.NVarChar, 50).Value = "PORTAL";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<KvhHttpResult> SendKvhAsync(HttpMethod method, string uri, string accessToken, string? bodyJson, string errorCode, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (bodyJson is not null)
        {
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            return new KvhHttpResult
            {
                Success = response.IsSuccessStatusCode,
                HttpStatusCode = (int)response.StatusCode,
                RawResponse = RedactJson(raw),
                ErrorCode = response.IsSuccessStatusCode ? string.Empty : errorCode,
                ErrorMessage = response.IsSuccessStatusCode ? string.Empty : $"KVH API returned HTTP {(int)response.StatusCode}."
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KvhHttpResult { Success = false, ErrorCode = errorCode, ErrorMessage = "KVH subscription request timed out." };
        }
    }

    private async Task<DeviceRow?> GetDeviceAsync(SqlConnection connection, int id, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 d.[ID], d.[DeviceName], d.[DeviceCode], d.[VesselName], d.[TenantID], t.[TenantName], d.[KITNumber], d.[KITID], d.[ServiceLine], d.[Availability], d.[LastUpdateTime], d.[TrafficId], d.[TokenString], d.[TokenExpiredTime]
            FROM [dbo].[TblDevices] d
            LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = d.[TenantID]
            WHERE d.[ID] = @id
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceRow
        {
            DeviceId = Convert.ToInt32(reader["ID"]),
            DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
            TerminalId = reader["DeviceCode"]?.ToString() ?? string.Empty,
            VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
            TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
            KitNumber = reader["KITNumber"]?.ToString() ?? string.Empty,
            KitId = reader["KITID"]?.ToString() ?? string.Empty,
            ServiceLine = reader["ServiceLine"]?.ToString() ?? string.Empty,
            Availability = reader["Availability"]?.ToString() ?? string.Empty,
            LastUpdateTimeUtc = reader["LastUpdateTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["LastUpdateTime"]),
            TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
            AccessToken = reader["TokenString"]?.ToString() ?? string.Empty,
            TokenExpiredTime = reader["TokenExpiredTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["TokenExpiredTime"])
        };
    }

    private async Task<KvhUsageResult> RequestTerminalUsageAsync(string terminalId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/usage");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new KvhUsageResult
                {
                    Success = false,
                    HttpStatusCode = (int)response.StatusCode,
                    RawResponse = RedactJson(raw),
                    ErrorCode = "kvh_terminal_usage_failed",
                    ErrorMessage = $"KVH terminal usage API returned HTTP {(int)response.StatusCode}."
                };
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                var trafficId = KvhJsonHelpers.FindStringValue(document.RootElement, "traffic_id", "trafficId", "trafficID", "trafficid");
                return new KvhUsageResult
                {
                    Success = true,
                    HttpStatusCode = (int)response.StatusCode,
                    RawResponse = RedactJson(raw),
                    TrafficId = trafficId
                };
            }
            catch (JsonException)
            {
                return new KvhUsageResult
                {
                    Success = false,
                    HttpStatusCode = (int)response.StatusCode,
                    RawResponse = RedactJson(raw),
                    ErrorCode = "kvh_terminal_usage_invalid_json",
                    ErrorMessage = "KVH terminal usage API returned invalid JSON."
                };
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KvhUsageResult
            {
                Success = false,
                ErrorCode = "kvh_terminal_usage_timeout",
                ErrorMessage = "KVH terminal usage request timed out."
            };
        }
    }

    private static string BuildSolutionWhere(KvhSolutionFilter filter, int? allowedTenantId, int? allowedDeviceId)
    {
        var clauses = new List<string>
        {
            "NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NOT NULL",
            "(@allowedTenantId IS NULL OR d.[TenantID] = @allowedTenantId)",
            "(@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)",
            "(@tenantId IS NULL OR d.[TenantID] = @tenantId)",
            "(@status IS NULL OR s.[Status] = @status)",
            "(@region IS NULL OR s.[Region] = @region)"
        };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add("(d.[DeviceCode] LIKE @search OR d.[DeviceName] LIKE @search OR d.[VesselName] LIKE @search OR d.[KITNumber] LIKE @search OR d.[TrafficId] LIKE @search)");
        }

        if (string.Equals(filter.SyncState, "missing_traffic", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("NULLIF(LTRIM(RTRIM(ISNULL(d.[TrafficId], ''))), '') IS NULL");
        }
        else if (string.Equals(filter.SyncState, "not_synced", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("log.[ID] IS NULL");
        }
        else if (string.Equals(filter.SyncState, "syncing", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("ISNULL(pending.[HasPendingCommand], 0) = 1");
        }
        else if (string.Equals(filter.SyncState, "success", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("log.[Success] = 1 AND ISNULL(log.[ReturnedCount], 0) > 0");
        }
        else if (string.Equals(filter.SyncState, "empty", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("log.[Success] = 1 AND ISNULL(log.[ReturnedCount], 0) = 0");
        }
        else if (string.Equals(filter.SyncState, "sync_failed", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("ISNULL(log.[Success], 1) = 0");
        }

        return string.Join(" AND ", clauses);
    }

    private static void AddSolutionParameters(SqlCommand command, KvhSolutionFilter filter, int? allowedTenantId, int? allowedDeviceId)
    {
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)filter.TenantId ?? DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 80).Value = (object?)filter.Status ?? DBNull.Value;
        command.Parameters.Add("@region", SqlDbType.NVarChar, 120).Value = (object?)filter.Region ?? DBNull.Value;
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            command.Parameters.Add("@search", SqlDbType.NVarChar, 260).Value = $"%{filter.Search}%";
        }
    }

    private static KvhSolutionListItemViewModel MapListItem(SqlDataReader reader) => new()
    {
        DeviceId = Convert.ToInt32(reader["ID"]),
        DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
        TerminalId = reader["DeviceCode"]?.ToString() ?? string.Empty,
        VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
        TenantId = reader["TenantID"] == DBNull.Value ? null : Convert.ToInt32(reader["TenantID"]),
        TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
        KitNumber = reader["KITNumber"]?.ToString() ?? string.Empty,
        Availability = reader["Availability"]?.ToString() ?? string.Empty,
        LastUpdateTimeUtc = reader["LastUpdateTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["LastUpdateTime"]),
        TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
        KvhSubscriptionId = reader["KvhSubscriptionId"] == DBNull.Value ? null : Convert.ToInt64(reader["KvhSubscriptionId"]),
        Region = reader["Region"]?.ToString() ?? string.Empty,
        PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
        Status = reader["Status"]?.ToString() ?? string.Empty,
        ScheduledAction = reader["ScheduledAction"]?.ToString() ?? string.Empty,
        ScheduleId = reader["ScheduleId"]?.ToString() ?? string.Empty,
        ScheduledEffectiveDateUtc = reader["ScheduledEffectiveDateUtc"] == DBNull.Value ? null : Convert.ToDateTime(reader["ScheduledEffectiveDateUtc"]),
        AllowanceGb = reader["AllowanceGb"] == DBNull.Value ? null : Convert.ToDecimal(reader["AllowanceGb"]),
        LastSeenAtUtc = reader["LastSeenAtUtc"] == DBNull.Value ? null : Convert.ToDateTime(reader["LastSeenAtUtc"]),
        LastSyncAtUtc = reader["LastSyncAtUtc"] == DBNull.Value ? null : Convert.ToDateTime(reader["LastSyncAtUtc"]),
        LastSyncStatus = reader["LastSyncSuccess"] == DBNull.Value
            ? "Not synced"
            : Convert.ToBoolean(reader["LastSyncSuccess"])
                ? reader["LastSyncReturnedCount"] != DBNull.Value && Convert.ToInt32(reader["LastSyncReturnedCount"]) == 0
                    ? "Empty"
                    : "Success"
                : "Failed",
        LastSyncErrorCode = reader["LastSyncErrorCode"]?.ToString() ?? string.Empty,
        HasPendingCommand = reader["HasPendingCommand"] != DBNull.Value && Convert.ToBoolean(reader["HasPendingCommand"]),
        CooldownUntilUtc = reader["CooldownUntilUtc"] == DBNull.Value ? null : Convert.ToDateTime(reader["CooldownUntilUtc"])
    };

    private static KvhSubscriptionEntryViewModel MapEntry(SqlDataReader reader) => new()
    {
        Id = Convert.ToInt64(reader["ID"]),
        SubscriptionKey = reader["SubscriptionKey"]?.ToString() ?? string.Empty,
        TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
        Region = reader["Region"]?.ToString() ?? string.Empty,
        Status = reader["Status"]?.ToString() ?? string.Empty,
        PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
        PlanJson = reader["PlanJson"]?.ToString() ?? string.Empty,
        OptInStatus = reader["OptInStatus"]?.ToString() ?? string.Empty,
        OptInJson = reader["OptInJson"]?.ToString() ?? string.Empty,
        ScheduledAction = reader["ScheduledAction"]?.ToString() ?? string.Empty,
        ScheduleId = reader["ScheduleId"]?.ToString() ?? string.Empty,
        ScheduledEffectiveDateUtc = reader["ScheduledEffectiveDateUtc"] == DBNull.Value ? null : Convert.ToDateTime(reader["ScheduledEffectiveDateUtc"]),
        AllowanceGb = reader["AllowanceGb"] == DBNull.Value ? null : Convert.ToDecimal(reader["AllowanceGb"]),
        Proration = reader["Proration"] == DBNull.Value ? null : Convert.ToDecimal(reader["Proration"]),
        EffectiveDateUtc = reader["EffectiveDateUtc"] == DBNull.Value ? null : Convert.ToDateTime(reader["EffectiveDateUtc"]),
        LastSeenAtUtc = reader["LastSeenAtUtc"] == DBNull.Value ? null : Convert.ToDateTime(reader["LastSeenAtUtc"]),
        RawSubscriptionJson = reader["RawSubscriptionJson"]?.ToString() ?? string.Empty
    };

    private async Task<(bool Allowed, int RemainingSeconds, DateTime? NextAllowedAtUtc)> CheckCooldownAsync(SqlConnection connection, string terminalId, string trafficId, CancellationToken cancellationToken)
    {
        var cooldownMinutes = Math.Max(5, monitorOptions.Value.TerminalCommandCooldownMinutes);
        const string query = """
            SELECT TOP 1 [RequestedAtUtc]
            FROM [dbo].[TblKvhCommand]
            WHERE ([TerminalId] = @terminalId OR [TrafficId] = @trafficId)
              AND [CommandStatus] NOT IN ('FAILED', 'TIMEOUT', 'VERIFICATION_MISMATCH', 'VERIFICATION_TIMEOUT')
            ORDER BY [RequestedAtUtc] DESC, [ID] DESC
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@terminalId", SqlDbType.NVarChar, 200).Value = terminalId;
        command.Parameters.Add("@trafficId", SqlDbType.NVarChar, 200).Value = trafficId;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            return (true, 0, DateTime.UtcNow.AddMinutes(cooldownMinutes));
        }

        var next = DateTime.SpecifyKind(Convert.ToDateTime(scalar), DateTimeKind.Utc).AddMinutes(cooldownMinutes);
        var remaining = (int)Math.Ceiling((next - DateTime.UtcNow).TotalSeconds);
        return remaining <= 0 ? (true, 0, DateTime.UtcNow.AddMinutes(cooldownMinutes)) : (false, remaining, next);
    }

    private async Task<long> InsertSubscriptionCommandAsync(SqlConnection connection, CommandContext context, string commandType, string requestJson, int? userId, string requestedBy, DateTime cooldownUntilUtc, CancellationToken cancellationToken)
    {
        const string query = """
            INSERT INTO [dbo].[TblKvhCommand]
                ([DeviceId], [TerminalId], [KvhDeviceId], [TrafficId], [Region], [ScheduleId], [KvhSubscriptionId], [CooldownUntilUtc],
                 [CommandType], [RequestedValue], [CommandStatus], [JobStatus], [VerificationStatus], [RequestJson], [RequestedByUserId], [RequestedBy], [RequestedAtUtc], [NextPollAtUtc], [MaxPollCount])
            OUTPUT INSERTED.[ID]
            VALUES
                (@deviceId, @terminalId, NULL, @trafficId, @region, @scheduleId, @kvhSubscriptionId, @cooldownUntilUtc,
                 @commandType, @requestedValue, @commandStatus, @jobStatus, @verificationStatus, @requestJson, @userId, @requestedBy, @requestedAtUtc, @nextPollAtUtc, @maxPollCount)
            """;
        var now = DateTime.UtcNow;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = context.DeviceId;
        command.Parameters.Add("@terminalId", SqlDbType.NVarChar, 200).Value = context.TerminalId;
        command.Parameters.Add("@trafficId", SqlDbType.NVarChar, 200).Value = context.TrafficId;
        command.Parameters.Add("@region", SqlDbType.NVarChar, 120).Value = context.Region;
        command.Parameters.Add("@scheduleId", SqlDbType.NVarChar, 200).Value = Db(context.ScheduleId);
        command.Parameters.Add("@kvhSubscriptionId", SqlDbType.BigInt).Value = context.KvhSubscriptionId;
        command.Parameters.Add("@cooldownUntilUtc", SqlDbType.DateTime2).Value = cooldownUntilUtc;
        command.Parameters.Add("@commandType", SqlDbType.NVarChar, 50).Value = commandType;
        command.Parameters.Add("@requestedValue", SqlDbType.NVarChar, -1).Value = requestJson;
        command.Parameters.Add("@commandStatus", SqlDbType.NVarChar, 30).Value = KvhCommandStatuses.Submitting;
        command.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = KvhJobStatuses.Submitted;
        command.Parameters.Add("@verificationStatus", SqlDbType.NVarChar, 30).Value = DBNull.Value;
        command.Parameters.Add("@requestJson", SqlDbType.NVarChar, -1).Value = requestJson;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@requestedBy", SqlDbType.NVarChar, 250).Value = string.IsNullOrWhiteSpace(requestedBy) ? "system" : requestedBy.Trim();
        command.Parameters.Add("@requestedAtUtc", SqlDbType.DateTime2).Value = now;
        command.Parameters.Add("@nextPollAtUtc", SqlDbType.DateTime2).Value = now.AddSeconds(Math.Max(1, monitorOptions.Value.InitialPollDelaySeconds));
        command.Parameters.Add("@maxPollCount", SqlDbType.Int).Value = Math.Max(1, monitorOptions.Value.MaxPollCount);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task MarkSubmittedAsync(SqlConnection connection, long commandId, string jobId, int? httpStatusCode, string rawResponse, CancellationToken cancellationToken)
    {
        const string query = "UPDATE [dbo].[TblKvhCommand] SET [CommandStatus] = @commandStatus, [JobStatus] = @jobStatus, [JobId] = @jobId, [HttpStatusCode] = @httpStatusCode, [SubmitResponseJson] = @rawResponse WHERE [ID] = @id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@commandStatus", SqlDbType.NVarChar, 30).Value = KvhCommandStatuses.Submitted;
        command.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = KvhJobStatuses.Submitted;
        command.Parameters.Add("@jobId", SqlDbType.NVarChar, 200).Value = jobId;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)httpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@rawResponse", SqlDbType.NVarChar, -1).Value = Db(RedactJson(rawResponse));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkSubmitFailedAsync(SqlConnection connection, long commandId, int? httpStatusCode, string rawResponse, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        const string query = "UPDATE [dbo].[TblKvhCommand] SET [CommandStatus] = @commandStatus, [JobStatus] = @jobStatus, [HttpStatusCode] = @httpStatusCode, [SubmitResponseJson] = @rawResponse, [CompletedAtUtc] = SYSUTCDATETIME(), [ErrorCode] = @errorCode, [ErrorMessage] = @errorMessage WHERE [ID] = @id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@commandStatus", SqlDbType.NVarChar, 30).Value = KvhCommandStatuses.Failed;
        command.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = KvhJobStatuses.Unknown;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)httpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@rawResponse", SqlDbType.NVarChar, -1).Value = Db(RedactJson(rawResponse));
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode;
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = errorMessage;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<DeviceTenantOptionViewModel>> GetTenantOptionsAsync(SqlConnection connection, int? allowedTenantId, CancellationToken cancellationToken)
    {
        const string query = "SELECT [ID], [TenantName] FROM [dbo].[TblTenant] WHERE (@tenantId IS NULL OR [ID] = @tenantId) ORDER BY [TenantName]";
        var tenants = new List<DeviceTenantOptionViewModel>();
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(new DeviceTenantOptionViewModel { Id = Convert.ToInt32(reader["ID"]), TenantName = reader["TenantName"]?.ToString() ?? string.Empty });
        }

        return tenants;
    }

    private async Task<(string ClientId, string ClientSecret)> GetApiCredentialsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string query = "SELECT [SettingCode], [SettingValue] FROM [dbo].[TblSettings] WHERE [SettingCode] IN ('client_id', 'client_secret')";
        string clientId = string.Empty;
        string clientSecret = string.Empty;
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader["SettingCode"]?.ToString();
            var value = reader["SettingValue"]?.ToString() ?? string.Empty;
            if (string.Equals(code, "client_id", StringComparison.OrdinalIgnoreCase)) clientId = value;
            if (string.Equals(code, "client_secret", StringComparison.OrdinalIgnoreCase)) clientSecret = value;
        }

        return (clientId, clientSecret);
    }

    private static KvhSubscriptionSyncResult Fail(int deviceId, string terminalId, string trafficId, string errorCode, string message, string rawResponse = "") => new()
    {
        Success = false,
        DeviceId = deviceId,
        TerminalId = terminalId,
        TrafficId = trafficId,
        ErrorCode = errorCode,
        Message = message,
        MessageEn = message,
        RawResponse = rawResponse
    };

    private static KvhDeviceSyncResult ToDeviceSyncResult(KvhSubscriptionSyncResult result) => new()
    {
        Success = result.Success,
        ErrorCode = result.ErrorCode,
        Message = result.Message,
        MessageEn = result.MessageEn,
        DeviceId = result.DeviceId,
        TerminalId = result.TerminalId,
        TrafficId = result.TrafficId,
        ReturnedCount = result.ReturnedCount,
        InsertedCount = result.InsertedCount,
        UpdatedCount = result.UpdatedCount,
        DeactivatedCount = result.DeactivatedCount,
        CurrentCount = result.CurrentCount,
        RawResponse = result.RawResponse,
        UsedStoredTrafficId = result.UsedStoredTrafficId
    };

    private static string RedactJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return rawJson;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(RedactElement(document.RootElement));
        }
        catch (JsonException)
        {
            return rawJson.Replace("access_token", "redacted_token", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object? RedactElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => IsTokenLike(property.Name) ? "***" : RedactElement(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(RedactElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool IsTokenLike(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    private static object Db(object? value) => value switch
    {
        null => DBNull.Value,
        string text when string.IsNullOrWhiteSpace(text) => DBNull.Value,
        _ => value
    };

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsTokenExpired(DateTime? tokenExpiredTime) => !tokenExpiredTime.HasValue || DateTime.SpecifyKind(tokenExpiredTime.Value, DateTimeKind.Utc) <= DateTime.UtcNow;

    private sealed class DeviceRow
    {
        public int DeviceId { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public string TerminalId { get; set; } = string.Empty;
        public string VesselName { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
        public string KitNumber { get; set; } = string.Empty;
        public string KitId { get; set; } = string.Empty;
        public string ServiceLine { get; set; } = string.Empty;
        public string Availability { get; set; } = string.Empty;
        public DateTime? LastUpdateTimeUtc { get; set; }
        public string TrafficId { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public DateTime? TokenExpiredTime { get; set; }
    }

    private sealed class ParsedSubscription
    {
        public int DeviceId { get; set; }
        public string TerminalId { get; set; } = string.Empty;
        public string TrafficId { get; set; } = string.Empty;
        public string SubscriptionKey { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string PlanName { get; set; } = string.Empty;
        public string PlanJson { get; set; } = string.Empty;
        public string OptInStatus { get; set; } = string.Empty;
        public string OptInJson { get; set; } = string.Empty;
        public string ScheduledAction { get; set; } = string.Empty;
        public string ScheduleId { get; set; } = string.Empty;
        public DateTime? ScheduledEffectiveDateUtc { get; set; }
        public string Region { get; set; } = string.Empty;
        public decimal? Proration { get; set; }
        public decimal? AllowanceGb { get; set; }
        public DateTime? EffectiveDateUtc { get; set; }
        public string RawSubscriptionJson { get; set; } = string.Empty;
    }

    private sealed class CommandContext
    {
        public bool Success { get; set; }
        public int DeviceId { get; set; }
        public string TerminalId { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public DateTime? TokenExpiredTime { get; set; }
        public long KvhSubscriptionId { get; set; }
        public string TrafficId { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string ScheduleId { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        public static CommandContext Fail(string errorCode, string message, string terminalId = "") => new() { Success = false, ErrorCode = errorCode, Message = message, TerminalId = terminalId };
        public KvhCommandSubmitResult ToSubmitResult(int deviceId) => new() { Success = false, ErrorCode = ErrorCode, Message = Message, MessageEn = Message, DeviceId = deviceId, TerminalId = TerminalId };
    }

    private sealed class KvhHttpResult
    {
        public bool Success { get; set; }
        public int? HttpStatusCode { get; set; }
        public string RawResponse { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    private sealed class KvhUsageResult
    {
        public bool Success { get; set; }
        public int? HttpStatusCode { get; set; }
        public string RawResponse { get; set; } = string.Empty;
        public string TrafficId { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
