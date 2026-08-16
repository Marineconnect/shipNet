using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class KvhPaymentResumeService(
    IConfiguration configuration,
    IKvhSubscriptionService kvhSubscriptionService,
    IDeviceActivityLogService activityLogService,
    ILogger<KvhPaymentResumeService> logger) : IKvhPaymentResumeService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public Task<KvhPaymentResumeResult> HandlePaidSubscriptionAsync(
        int subscriptionId,
        string source,
        int? userId,
        string performedBy,
        string referenceType,
        string referenceId,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        HandlePaidSubscriptionAsync(new KvhPaymentResumeRequest
        {
            SubscriptionId = subscriptionId,
            Source = source,
            UserId = userId,
            PerformedBy = performedBy,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            CorrelationId = correlationId
        }, cancellationToken);

    public async Task<KvhPaymentResumeResult> HandlePaidSubscriptionAsync(KvhPaymentResumeRequest request, CancellationToken cancellationToken = default)
    {
        var context = await GetSubscriptionContextAsync(request.SubscriptionId, request.AllowedTenantId, request.AllowedDeviceId, cancellationToken);
        if (context is null)
        {
            return new KvhPaymentResumeResult
            {
                Success = false,
                SubscriptionId = request.SubscriptionId,
                ErrorCode = "subscription_not_found",
                Message = "Subscription was not found or is outside the allowed scope."
            };
        }

        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? Guid.NewGuid().ToString("N") : request.CorrelationId.Trim();
        try
        {
            var syncResult = await kvhSubscriptionService.SyncDeviceSubscriptionAsync(context.DeviceId, request.AllowedTenantId, request.AllowedDeviceId, cancellationToken);
            if (!syncResult.Success)
            {
                await WriteAsync(context, request, DeviceActivityActions.SubscriptionResumeSkipped, DeviceActivityStatuses.Skipped, null, null,
                    "KVH resume skipped because subscription sync failed.",
                    DeviceActivityLogEntry.ToSafeJson(new { syncResult.ErrorCode, syncResult.Message }),
                    correlationId,
                    cancellationToken,
                    eventKey: $"{DeviceActivityActions.SubscriptionResumeSkipped}:{context.DeviceId}:{request.ReferenceId}:sync_failed");
                return new KvhPaymentResumeResult
                {
                    Success = false,
                    Skipped = true,
                    SubscriptionId = context.SubscriptionId,
                    DeviceId = context.DeviceId,
                    ErrorCode = syncResult.ErrorCode,
                    Message = syncResult.Message
                };
            }

            var current = await GetCurrentKvhSubscriptionAsync(context.DeviceId, cancellationToken);
            if (current is null)
            {
                await WriteAsync(context, request, DeviceActivityActions.SubscriptionResumeSkipped, DeviceActivityStatuses.Skipped, null, null,
                    "KVH resume skipped because no current subscription was found.",
                    DeviceActivityLogEntry.ToSafeJson(new { syncResult.ReturnedCount, reason = "current_subscription_missing" }),
                    correlationId,
                    cancellationToken,
                    eventKey: $"{DeviceActivityActions.SubscriptionResumeSkipped}:{context.DeviceId}:{request.ReferenceId}:current_missing");
                return new KvhPaymentResumeResult
                {
                    Success = true,
                    Skipped = true,
                    SubscriptionId = context.SubscriptionId,
                    DeviceId = context.DeviceId,
                    Message = "No current KVH subscription was found."
                };
            }

            if (await HasPendingResumeCommandAsync(context.DeviceId, current.KvhSubscriptionId, cancellationToken))
            {
                await WriteAsync(context, request, DeviceActivityActions.SubscriptionResumeSkipped, DeviceActivityStatuses.Skipped, current.Status, "resume_pending",
                    "KVH resume skipped because a resume command is already pending.",
                    DeviceActivityLogEntry.ToSafeJson(new { current.KvhSubscriptionId, current.Status, reason = "pending_resume_command" }),
                    correlationId,
                    cancellationToken,
                    eventKey: $"{DeviceActivityActions.SubscriptionResumeSkipped}:{context.DeviceId}:{current.KvhSubscriptionId}:{request.ReferenceId}:pending_resume");
                return new KvhPaymentResumeResult
                {
                    Success = true,
                    Skipped = true,
                    SubscriptionId = context.SubscriptionId,
                    DeviceId = context.DeviceId,
                    KvhSubscriptionId = current.KvhSubscriptionId,
                    KvhStatus = current.Status,
                    Message = "A KVH resume command is already pending."
                };
            }

            if (!IsPaused(current.Status))
            {
                await WriteAsync(context, request, DeviceActivityActions.SubscriptionResumeSkipped, DeviceActivityStatuses.Skipped, current.Status, current.Status,
                    "KVH resume skipped because subscription is already active or not paused.",
                    DeviceActivityLogEntry.ToSafeJson(new { current.KvhSubscriptionId, current.Status, reason = "subscription_not_paused" }),
                    correlationId,
                    cancellationToken,
                    eventKey: $"{DeviceActivityActions.SubscriptionResumeSkipped}:{context.DeviceId}:{current.KvhSubscriptionId}:{request.ReferenceId}:not_paused");
                return new KvhPaymentResumeResult
                {
                    Success = true,
                    Skipped = true,
                    SubscriptionId = context.SubscriptionId,
                    DeviceId = context.DeviceId,
                    KvhSubscriptionId = current.KvhSubscriptionId,
                    KvhStatus = current.Status,
                    Message = "KVH subscription is not paused."
                };
            }

            var submit = await kvhSubscriptionService.ResumeAsync(
                new KvhSolutionCommandRequest { DeviceId = context.DeviceId, KvhSubscriptionId = current.KvhSubscriptionId },
                request.UserId,
                request.PerformedBy,
                request.AllowedTenantId,
                request.AllowedDeviceId,
                cancellationToken);

            if (!submit.Success)
            {
                await WriteAsync(context, request, DeviceActivityActions.SubscriptionResumeFailed, DeviceActivityStatuses.Failed, current.Status, current.Status,
                    "KVH subscription resume failed after paid invoice.",
                    DeviceActivityLogEntry.ToSafeJson(new { submit.ErrorCode, submit.Message, submit.HttpStatusCode, submit.CommandId }),
                    correlationId,
                    cancellationToken,
                    eventKey: submit.CommandId.HasValue ? $"{DeviceActivityActions.SubscriptionResumeFailed}:{context.DeviceId}:{current.KvhSubscriptionId}:{submit.CommandId.Value}" : null);
                return new KvhPaymentResumeResult
                {
                    Success = false,
                    SubscriptionId = context.SubscriptionId,
                    DeviceId = context.DeviceId,
                    KvhSubscriptionId = current.KvhSubscriptionId,
                    KvhStatus = current.Status,
                    CommandId = submit.CommandId,
                    ErrorCode = submit.ErrorCode,
                    Message = submit.Message
                };
            }

            await WriteAsync(context, request, DeviceActivityActions.SubscriptionResumeRequested, DeviceActivityStatuses.Requested, current.Status, "resume_requested",
                "KVH subscription resume command submitted after paid invoice.",
                DeviceActivityLogEntry.ToSafeJson(new { submit.CommandId, submit.JobId, submit.HttpStatusCode }),
                correlationId,
                cancellationToken,
                "KVH_COMMAND",
                submit.CommandId?.ToString(),
                eventKey: submit.CommandId.HasValue ? $"{DeviceActivityActions.SubscriptionResumeRequested}:{context.DeviceId}:{current.KvhSubscriptionId}:{submit.CommandId.Value}" : null);

            return new KvhPaymentResumeResult
            {
                Success = true,
                ResumeSubmitted = true,
                SubscriptionId = context.SubscriptionId,
                DeviceId = context.DeviceId,
                KvhSubscriptionId = current.KvhSubscriptionId,
                KvhStatus = current.Status,
                CommandId = submit.CommandId,
                JobId = submit.JobId,
                Message = submit.Message
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Failed to handle paid subscription KVH resume. SubscriptionId={SubscriptionId}", request.SubscriptionId);
            await WriteAsync(context, request, DeviceActivityActions.SubscriptionResumeFailed, DeviceActivityStatuses.Failed, null, null,
                "KVH subscription resume failed after paid invoice.",
                DeviceActivityLogEntry.ToSafeJson(new { error = exception.GetBaseException().Message }),
                correlationId,
                cancellationToken);
            return new KvhPaymentResumeResult
            {
                Success = false,
                SubscriptionId = context.SubscriptionId,
                DeviceId = context.DeviceId,
                ErrorCode = exception.GetBaseException().GetType().Name,
                Message = exception.GetBaseException().Message
            };
        }
    }

    public async Task<KvhPaymentResumePrecheckResult> PrecheckAsync(int invoiceId, int subscriptionId, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        var context = await GetSubscriptionContextAsync(subscriptionId, allowedTenantId, allowedDeviceId, cancellationToken);
        if (context is null || !await InvoiceBelongsToSubscriptionAsync(invoiceId, subscriptionId, cancellationToken))
        {
            return new KvhPaymentResumePrecheckResult { Success = false, Message = "Invoice or subscription was not found." };
        }

        var syncResult = await kvhSubscriptionService.SyncDeviceSubscriptionAsync(context.DeviceId, allowedTenantId, allowedDeviceId, cancellationToken);
        if (!syncResult.Success)
        {
            return new KvhPaymentResumePrecheckResult
            {
                Success = false,
                Message = syncResult.Message,
                DeviceId = context.DeviceId,
                DeviceName = context.DeviceName,
                VesselName = context.VesselName,
                KitNumber = context.KitNumber
            };
        }

        var current = await GetCurrentKvhSubscriptionAsync(context.DeviceId, cancellationToken);
        return new KvhPaymentResumePrecheckResult
        {
            Success = true,
            DeviceId = context.DeviceId,
            DeviceName = context.DeviceName,
            VesselName = context.VesselName,
            KitNumber = context.KitNumber,
            KvhStatus = current?.Status ?? string.Empty,
            CanResume = current is not null && IsPaused(current.Status)
        };
    }

    private async Task WriteAsync(
        SubscriptionContext context,
        KvhPaymentResumeRequest request,
        string action,
        string status,
        string? oldValue,
        string? newValue,
        string summary,
        string detailJson,
        string correlationId,
        CancellationToken cancellationToken,
        string? referenceTypeOverride = null,
        string? referenceIdOverride = null,
        string? eventKey = null)
    {
        await activityLogService.WriteAsync(new DeviceActivityLogEntry
        {
            DeviceId = context.DeviceId,
            TenantId = context.TenantId,
            Category = DeviceActivityCategories.Subscription,
            Action = action,
            Status = status,
            OldValue = oldValue,
            NewValue = newValue,
            Summary = summary,
            DetailJson = detailJson,
            Source = request.Source,
            ActorType = string.IsNullOrWhiteSpace(request.ActorType) ? ResolveActorType(request.Source) : request.ActorType,
            UserId = request.UserId,
            PerformedBy = request.PerformedBy,
            ReferenceType = referenceTypeOverride ?? request.ReferenceType,
            ReferenceId = referenceIdOverride ?? request.ReferenceId,
            CorrelationId = correlationId,
            EventKey = eventKey
        }, cancellationToken);
    }

    private async Task<SubscriptionContext?> GetSubscriptionContextAsync(int subscriptionId, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 s.[ID], s.[DeviceId], s.[TenantId], s.[VesselName],
                   d.[DeviceName], COALESCE(NULLIF(d.[KITNumber], N''), NULLIF(s.[KitId], N''), d.[KITID], N'') AS [KitNumber]
            FROM [dbo].[TblMonthlySubscription] s
            INNER JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
            WHERE s.[ID] = @subscriptionId
              AND (@tenantId IS NULL OR s.[TenantId] = @tenantId)
              AND (@deviceId IS NULL OR s.[DeviceId] = @deviceId)
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SubscriptionContext(
            Convert.ToInt32(reader["ID"]),
            Convert.ToInt32(reader["DeviceId"]),
            Convert.ToInt32(reader["TenantId"]),
            reader["DeviceName"]?.ToString() ?? string.Empty,
            reader["VesselName"]?.ToString() ?? string.Empty,
            reader["KitNumber"]?.ToString() ?? string.Empty);
    }

    private async Task<KvhCurrentSubscription?> GetCurrentKvhSubscriptionAsync(int deviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 [ID], [TrafficId], [Region], [Status]
            FROM [dbo].[TblKvhSubscription]
            WHERE [DeviceId] = @deviceId AND [IsCurrent] = 1
            ORDER BY [LastSeenAtUtc] DESC, [ID] DESC
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new KvhCurrentSubscription(
            Convert.ToInt64(reader["ID"]),
            reader["TrafficId"]?.ToString() ?? string.Empty,
            reader["Region"]?.ToString() ?? string.Empty,
            reader["Status"]?.ToString() ?? string.Empty);
    }

    private async Task<bool> HasPendingResumeCommandAsync(int deviceId, long kvhSubscriptionId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT COUNT(1)
            FROM [dbo].[TblKvhCommand]
            WHERE [DeviceId] = @deviceId
              AND [KvhSubscriptionId] = @kvhSubscriptionId
              AND [CommandType] = N'SUBSCRIPTION_RESUME'
              AND [CommandStatus] IN (N'SUBMITTING', N'SUBMITTED', N'PENDING', N'WAITING', N'VERIFYING', N'UNKNOWN')
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@kvhSubscriptionId", SqlDbType.BigInt).Value = kvhSubscriptionId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;
    }

    private async Task<bool> InvoiceBelongsToSubscriptionAsync(int invoiceId, int subscriptionId, CancellationToken cancellationToken)
    {
        const string query = "SELECT COUNT(1) FROM [dbo].[TblSubscriptionInvoice] WHERE [ID] = @invoiceId AND [SubscriptionId] = @subscriptionId";
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;
    }

    private static bool IsPaused(string status) =>
        status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase);

    private static string ResolveRequestedSummary(string source) =>
        source.Equals(DeviceActivitySources.BankTransfer, StringComparison.OrdinalIgnoreCase)
            ? "KVH subscription resume requested after bank transfer payment."
            : "KVH subscription resume requested after invoice was marked paid.";

    private static string ResolveActorType(string source) =>
        source.Equals(DeviceActivitySources.NinePayIpn, StringComparison.OrdinalIgnoreCase)
            ? DeviceActivityActorTypes.PaymentProvider
            : DeviceActivityActorTypes.User;

    private sealed record SubscriptionContext(int SubscriptionId, int DeviceId, int TenantId, string DeviceName, string VesselName, string KitNumber);
    private sealed record KvhCurrentSubscription(long KvhSubscriptionId, string TrafficId, string Region, string Status);
}
