using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class KvhJobService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IOptions<KvhJobMonitorOptions> monitorOptions,
    IKvhSubscriptionService kvhSubscriptionService,
    IDeviceActivityLogService deviceActivityLogService,
    ILogger<KvhJobService> logger) : IKvhJobService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task<IReadOnlyList<KvhCommand>> ClaimCommandsForPollingAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var commands = new List<KvhCommand>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string selectQuery = """
            SELECT TOP (@batchSize) *
            FROM [dbo].[TblKvhCommand] WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE [JobStatus] IN ('SUBMITTED', 'Pending', 'Unknown')
              AND [JobId] IS NOT NULL
              AND [JobId] <> ''
              AND ([NextPollAtUtc] IS NULL OR [NextPollAtUtc] <= SYSUTCDATETIME())
              AND [PollCount] < [MaxPollCount]
              AND [CommandStatus] NOT IN ('FAILED', 'TIMEOUT', 'VERIFIED', 'VERIFICATION_MISMATCH', 'VERIFICATION_TIMEOUT')
            ORDER BY [RequestedAtUtc], [ID]
            """;

        await using (var command = new SqlCommand(selectQuery, connection, transaction))
        {
            command.Parameters.Add("@batchSize", SqlDbType.Int).Value = batchSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                commands.Add(MapCommand(reader));
            }
        }

        if (commands.Count > 0)
        {
            const string claimQuery = """
                UPDATE [dbo].[TblKvhCommand]
                SET [LastPolledAtUtc] = SYSUTCDATETIME(),
                    [NextPollAtUtc] = DATEADD(second, @pollSeconds, SYSUTCDATETIME()),
                    [PollCount] = [PollCount] + 1,
                    [CommandStatus] = CASE WHEN [CommandStatus] = 'SUBMITTED' THEN 'PENDING' ELSE [CommandStatus] END
                WHERE [ID] IN ({0})
                """;
            var parameterNames = commands.Select((_, index) => $"@id{index}").ToArray();
            await using var claim = new SqlCommand(string.Format(claimQuery, string.Join(",", parameterNames)), connection, transaction);
            claim.Parameters.Add("@pollSeconds", SqlDbType.Int).Value = Math.Max(120, monitorOptions.Value.JobPollIntervalSeconds);
            for (var i = 0; i < commands.Count; i++)
            {
                claim.Parameters.Add(parameterNames[i], SqlDbType.BigInt).Value = commands[i].Id;
                commands[i].PollCount++;
                commands[i].LastPolledAtUtc = DateTime.UtcNow;
            }

            await claim.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return commands;
    }

    public async Task PollCommandAsync(KvhCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation(
                "Polling KVH job {JobId} for command {CommandId}, terminal {TerminalId}",
                command.JobId,
                command.Id,
                command.TerminalId);

            if (DateTime.UtcNow > command.RequestedAtUtc.AddMinutes(Math.Max(1, monitorOptions.Value.CommandTimeoutMinutes)) ||
                command.PollCount >= command.MaxPollCount)
            {
                await CompleteCommandAsync(command.Id, KvhCommandStatuses.Timeout, KvhJobStatuses.Unknown, null, null, KvhErrorCodes.JobTimeout, "KVH job polling timed out.", cancellationToken);
                return;
            }

            var token = await GetValidTokenAsync(command.DeviceId, command.TerminalId, cancellationToken);
            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                await ScheduleRetryAsync(command, KvhErrorCodes.TokenRefreshFailed, token.ErrorMessage, cancellationToken);
                return;
            }

            var jobResult = await RequestJobAsync(command.JobId, token.AccessToken, cancellationToken);
            if ((jobResult.HttpStatusCode == 401 || jobResult.HttpStatusCode == 403) && !token.Refreshed)
            {
                token = await RefreshTokenAsync(command.DeviceId, command.TerminalId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    jobResult = await RequestJobAsync(command.JobId, token.AccessToken, cancellationToken);
                }
            }

            if (jobResult.Timeout)
            {
                await ScheduleRetryAsync(command, string.Empty, "KVH job request timed out.", cancellationToken);
                return;
            }

            if (jobResult.HttpStatusCode == 404)
            {
                await CompleteCommandAsync(command.Id, KvhCommandStatuses.Failed, KvhJobStatuses.Unknown, jobResult.RawResponse, null, KvhErrorCodes.JobNotFound, "KVH job was not found.", cancellationToken);
                return;
            }

            if (jobResult.HttpStatusCode == 429 || jobResult.HttpStatusCode >= 500)
            {
                await ScheduleRetryAsync(command, KvhErrorCodes.JobApiError, $"KVH job API returned HTTP {jobResult.HttpStatusCode}.", cancellationToken);
                return;
            }

            if (!jobResult.ValidJson)
            {
                await CompleteCommandAsync(command.Id, KvhCommandStatuses.Failed, KvhJobStatuses.Unknown, jobResult.RawResponse, null, KvhErrorCodes.JobInvalidJson, "KVH job API returned invalid JSON.", cancellationToken);
                return;
            }

            if (jobResult.NormalizedStatus == KvhJobStatuses.Pending || jobResult.NormalizedStatus == KvhJobStatuses.Unknown)
            {
                await UpdatePendingAsync(command, jobResult, cancellationToken);
                return;
            }

            if (jobResult.NormalizedStatus == KvhJobStatuses.Failed)
            {
                await CompleteCommandAsync(command.Id, KvhCommandStatuses.Failed, KvhJobStatuses.Failed, jobResult.RawResponse, null, KvhErrorCodes.JobFailed, jobResult.Message, cancellationToken);
                return;
            }

            await VerifyCompletedJobAsync(command, token.AccessToken, jobResult.RawResponse, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to poll KVH command {CommandId}, job {JobId}, terminal {TerminalId}, type {CommandType}", command.Id, command.JobId, command.TerminalId, command.CommandType);
            await ScheduleRetryAsync(command, KvhErrorCodes.JobApiError, "Unexpected error while polling KVH job.", cancellationToken);
        }
    }

    private async Task VerifyCompletedJobAsync(KvhCommand command, string accessToken, string jobResponseJson, CancellationToken cancellationToken)
    {
        await SetVerifyingAsync(command.Id, KvhJobStatuses.Success, jobResponseJson, cancellationToken);
        var normalizedCommandType = command.CommandType.Trim().ToUpperInvariant();
        var result = normalizedCommandType switch
        {
            KvhCommandTypes.DataOptIn => await VerifyDataOptInAsync(command, accessToken, cancellationToken),
            KvhCommandTypes.WifiUpdate => await VerifyWifiAsync(command, accessToken, cancellationToken),
            KvhCommandTypes.Reboot => await VerifyRebootAsync(command, accessToken, cancellationToken),
            KvhCommandTypes.SubscriptionPause => await VerifySubscriptionCommandAsync(command, accessToken, "pause", cancellationToken),
            KvhCommandTypes.SubscriptionResume => await VerifySubscriptionCommandAsync(command, accessToken, "resume", cancellationToken),
            KvhCommandTypes.SubscriptionCancelSchedule => await VerifySubscriptionCommandAsync(command, accessToken, "cancel", cancellationToken),
            _ => VerificationResult.Mismatch("unsupported_command_type", $"Unsupported command type: {command.CommandType}.")
        };

        var finalStatus = result.Success
            ? (string.IsNullOrWhiteSpace(result.CommandStatus) ? KvhCommandStatuses.Verified : result.CommandStatus)
            : result.Timeout
                ? KvhCommandStatuses.VerificationTimeout
                : KvhCommandStatuses.VerificationMismatch;
        var verificationStatus = result.Success
            ? (string.IsNullOrWhiteSpace(result.VerificationStatus) ? KvhVerificationStatuses.Success : result.VerificationStatus)
            : result.Timeout
                ? KvhVerificationStatuses.Timeout
                : KvhVerificationStatuses.Mismatch;
        var errorCode = result.Success ? string.Empty : result.ErrorCode;
        var isSubscriptionCommand = normalizedCommandType is
            KvhCommandTypes.SubscriptionPause or
            KvhCommandTypes.SubscriptionResume or
            KvhCommandTypes.SubscriptionCancelSchedule;
        await CompleteCommandAsync(command.Id, finalStatus, KvhJobStatuses.Success, jobResponseJson, result.ResponseJson, errorCode, result.Message, cancellationToken, verificationStatus, writeActivity: !isSubscriptionCommand || !result.Success);

        if (result.Success && isSubscriptionCommand)
        {
            try
            {
                var syncResult = await kvhSubscriptionService.SyncForDeviceAsync(
                    command.DeviceId,
                    command.TerminalId,
                    accessToken,
                    command.TrafficId,
                    cancellationToken);

                if (!syncResult.Success)
                {
                    logger.LogWarning(
                        "KVH command {CommandId} succeeded, but subscription sync failed for device {DeviceId}. ErrorCode={ErrorCode}; Message={Message}",
                        command.Id,
                        command.DeviceId,
                        syncResult.ErrorCode,
                        syncResult.MessageEn);
                    await WriteSubscriptionCompletionAfterSyncSafeAsync(command, normalizedCommandType, false, "subscription_sync_failed", syncResult.MessageEn, cancellationToken);
                }
                else
                {
                    logger.LogInformation(
                        "Synced KVH subscriptions after successful command {CommandId} for device {DeviceId}. ReturnedCount={ReturnedCount}",
                        command.Id,
                        command.DeviceId,
                        syncResult.ReturnedCount);
                    await WriteSubscriptionCompletionAfterSyncSafeAsync(command, normalizedCommandType, true, string.Empty, string.Empty, cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "KVH command {CommandId} succeeded, but the post-command subscription sync threw an exception for device {DeviceId}.",
                    command.Id,
                    command.DeviceId);
                await WriteSubscriptionCompletionAfterSyncSafeAsync(command, normalizedCommandType, false, ex.GetBaseException().GetType().Name, ex.GetBaseException().Message, cancellationToken);
            }
        }
    }

    private async Task<VerificationResult> VerifyDataOptInAsync(KvhCommand command, string accessToken, CancellationToken cancellationToken)
    {
        var expected = TryReadRequestedBool(command.RequestedValue, "enabled");
        using var request = CreateKvhRequest(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(command.TerminalId)}/usage", accessToken);
        var response = await SendAsync(request, cancellationToken);
        if (!response.Success)
        {
            return VerificationResult.Mismatch(KvhErrorCodes.VerificationFailed, $"Usage API returned HTTP {response.HttpStatusCode}.", response.RawResponse);
        }

        try
        {
            using var document = JsonDocument.Parse(response.RawResponse);
            var actual = KvhJsonHelpers.FindBooleanValue(document.RootElement, "optin", "optIn", "dataOptIn");
            return expected.HasValue && actual == expected
                ? VerificationResult.Ok(response.RawResponse)
                : VerificationResult.Mismatch(KvhErrorCodes.VerificationMismatch, "Usage opt-in state does not match requested state.", response.RawResponse);
        }
        catch (JsonException)
        {
            return VerificationResult.Mismatch(KvhErrorCodes.VerificationFailed, "Usage API returned invalid JSON.", response.RawResponse);
        }
    }

    private async Task<VerificationResult> VerifyWifiAsync(KvhCommand command, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(command.TerminalId)}/devices/{Uri.EscapeDataString(command.KvhDeviceId)}/wifi", accessToken);
        var response = await SendAsync(request, cancellationToken);
        var raw = KvhJsonHelpers.MaskWifiSecrets(response.RawResponse);
        if (!response.Success)
        {
            return VerificationResult.Mismatch(KvhErrorCodes.VerificationFailed, $"WiFi API returned HTTP {response.HttpStatusCode}.", raw);
        }

        try
        {
            using var requestedDocument = JsonDocument.Parse(command.RequestedValue);
            using var actualDocument = JsonDocument.Parse(response.RawResponse);
            var expectedSsid = KvhJsonHelpers.FindStringValue(requestedDocument.RootElement, "ssid");
            var expectedEnabled = KvhJsonHelpers.FindBooleanValue(requestedDocument.RootElement, "enabled");
            var actualSsid = KvhJsonHelpers.FindStringValue(actualDocument.RootElement, "ssid", "wifiSsid", "wiFiSsid", "networkName", "name");
            var actualEnabled = KvhJsonHelpers.FindBooleanValue(actualDocument.RootElement, "enabled", "wifiEnabled", "wiFiEnabled", "isEnabled");
            var matches = string.Equals(expectedSsid, actualSsid, StringComparison.Ordinal) && (!expectedEnabled.HasValue || actualEnabled == expectedEnabled);
            return matches
                ? VerificationResult.Ok(raw)
                : VerificationResult.Mismatch(KvhErrorCodes.VerificationMismatch, "WiFi SSID or enabled state does not match requested values.", raw);
        }
        catch (JsonException)
        {
            return VerificationResult.Mismatch(KvhErrorCodes.VerificationFailed, "WiFi verification JSON is invalid.", raw);
        }
    }

    private async Task<VerificationResult> VerifyRebootAsync(KvhCommand command, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(command.TerminalId)}/status", accessToken);
        var response = await SendAsync(request, cancellationToken);
        if (!response.Success)
        {
            return VerificationResult.Mismatch(KvhErrorCodes.VerificationFailed, $"Status API returned HTTP {response.HttpStatusCode}.", response.RawResponse);
        }

        try
        {
            using var beforeDocument = JsonDocument.Parse(command.RequestedValue);
            using var afterDocument = JsonDocument.Parse(response.RawResponse);
            var beforeUptime = KvhJsonHelpers.FindLongValue(beforeDocument.RootElement, "UptimeSeconds", "uptimeSeconds", "uptime");
            var afterUptime = KvhJsonHelpers.FindLongValue(afterDocument.RootElement, "uptime", "uptimeSeconds", "up_time");
            if (beforeUptime.HasValue && afterUptime.HasValue && afterUptime.Value < beforeUptime.Value)
            {
                return VerificationResult.Ok(response.RawResponse);
            }

            var timeoutAt = command.RequestedAtUtc.AddMinutes(Math.Max(1, monitorOptions.Value.RebootVerificationTimeoutMinutes));
            return DateTime.UtcNow < timeoutAt
                ? VerificationResult.TimedOut("Reboot job succeeded, but reboot evidence is not visible yet.", response.RawResponse)
                : VerificationResult.Mismatch(KvhErrorCodes.VerificationMismatch, "Reboot job succeeded, but uptime/offline-online evidence was not observed.", response.RawResponse);
        }
        catch (JsonException)
        {
            return VerificationResult.Mismatch(KvhErrorCodes.VerificationFailed, "Status verification JSON is invalid.", response.RawResponse);
        }
    }

    private async Task<VerificationResult> VerifySubscriptionCommandAsync(KvhCommand command, string accessToken, string expectedAction, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.TrafficId))
        {
            return VerificationResult.Mismatch("kvh_traffic_id_missing", "Traffic ID is missing on the KVH command.");
        }

        using var request = CreateKvhRequest(HttpMethod.Get, $"https://api.mykvh.com/v3/subscriptions/{Uri.EscapeDataString(command.TrafficId)}", accessToken);
        var response = await SendAsync(request, cancellationToken);
        if (!response.Success)
        {
            return VerificationResult.Mismatch(KvhErrorCodes.SubscriptionVerificationFailed, $"Subscription list API returned HTTP {response.HttpStatusCode}.", response.RawResponse);
        }

        try
        {
            using var document = JsonDocument.Parse(response.RawResponse);
            var entries = ResolveSubscriptionArray(document.RootElement);
            foreach (var entry in entries)
            {
                var region = KvhJsonHelpers.FindStringValue(entry, "region", "region_code", "regionCode");
                if (!string.IsNullOrWhiteSpace(command.Region) && !string.Equals(region, command.Region, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var status = KvhJsonHelpers.FindStringValue(entry, "status", "state");
                var scheduled = KvhJsonHelpers.ResolveScheduledAction(entry);
                var scheduledAction = scheduled?.Type ?? KvhJsonHelpers.NormalizeScheduledAction(KvhJsonHelpers.FindStringValue(entry, "scheduled_action", "scheduledAction", "schedule_action", "action"));
                var scheduleId = scheduled?.ScheduleId ?? KvhJsonHelpers.FindStringValue(entry, "schedule_id", "scheduleId");

                if (expectedAction == "resume" &&
                    (status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
                     scheduledAction.Equals("RESUME", StringComparison.OrdinalIgnoreCase)))
                {
                    return status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)
                        ? VerificationResult.Ok(response.RawResponse, KvhCommandStatuses.Completed, KvhVerificationStatuses.VerifiedEffective)
                        : VerificationResult.Ok(response.RawResponse, KvhCommandStatuses.Completed, KvhVerificationStatuses.VerifiedScheduled);
                }

                if (expectedAction == "pause" &&
                    (scheduledAction.Equals("SUSPEND", StringComparison.OrdinalIgnoreCase) ||
                     status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) ||
                     status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase)))
                {
                    return status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) || status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase)
                        ? VerificationResult.Ok(response.RawResponse, KvhCommandStatuses.Completed, KvhVerificationStatuses.VerifiedEffective)
                        : VerificationResult.Ok(response.RawResponse, KvhCommandStatuses.Completed, KvhVerificationStatuses.VerifiedScheduled);
                }

                if (expectedAction == "cancel" &&
                    (string.IsNullOrWhiteSpace(command.ScheduleId) ||
                     !string.Equals(scheduleId, command.ScheduleId, StringComparison.OrdinalIgnoreCase)))
                {
                    return VerificationResult.Ok(response.RawResponse, KvhCommandStatuses.Completed, KvhVerificationStatuses.VerifiedEffective);
                }
            }

            return VerificationResult.Mismatch(KvhErrorCodes.SubscriptionVerificationFailed, "KVH subscription payload does not reflect the expected state or scheduled action.", response.RawResponse);
        }
        catch (JsonException)
        {
            return VerificationResult.Mismatch(KvhErrorCodes.SubscriptionVerificationFailed, "Subscription verification JSON is invalid.", response.RawResponse);
        }
    }

    private async Task<JobPollResult> RequestJobAsync(string jobId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Get, $"https://api.mykvh.com/v3/jobs/{Uri.EscapeDataString(jobId)}", accessToken);
        try
        {
            var response = await SendAsync(request, cancellationToken);
            if (!response.Success)
            {
                return new JobPollResult { HttpStatusCode = response.HttpStatusCode, RawResponse = response.RawResponse, ValidJson = true, NormalizedStatus = KvhJobStatuses.Unknown };
            }

            try
            {
                using var document = JsonDocument.Parse(response.RawResponse);
                var rawStatus = KvhJsonHelpers.FindStringValue(document.RootElement, "status", "jobStatus", "state");
                return new JobPollResult
                {
                    HttpStatusCode = response.HttpStatusCode,
                    RawResponse = response.RawResponse,
                    ValidJson = true,
                    NormalizedStatus = NormalizeJobStatus(rawStatus),
                    Message = KvhJsonHelpers.FindStringValue(document.RootElement, "message", "error", "errorMessage")
                };
            }
            catch (JsonException)
            {
                return new JobPollResult { HttpStatusCode = response.HttpStatusCode, RawResponse = response.RawResponse, ValidJson = false, NormalizedStatus = KvhJobStatuses.Unknown };
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new JobPollResult { Timeout = true, NormalizedStatus = KvhJobStatuses.Unknown };
        }
    }

    private async Task<KvhHttpResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        return new KvhHttpResponse
        {
            Success = response.IsSuccessStatusCode,
            HttpStatusCode = (int)response.StatusCode,
            RawResponse = await response.Content.ReadAsStringAsync(cancellationToken)
        };
    }

    private async Task<TokenResult> GetValidTokenAsync(int deviceId, string terminalId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string query = "SELECT TOP 1 [TokenString], [TokenExpiredTime] FROM [dbo].[TblDevices] WHERE [ID] = @deviceId";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new TokenResult { ErrorMessage = "Device not found." };
        }

        var token = reader["TokenString"]?.ToString() ?? string.Empty;
        DateTime? expires = reader["TokenExpiredTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["TokenExpiredTime"]);
        await reader.DisposeAsync();
        return string.IsNullOrWhiteSpace(token) || IsTokenExpired(expires)
            ? await RefreshTokenAsync(deviceId, terminalId, cancellationToken)
            : new TokenResult { AccessToken = token };
    }

    private async Task<TokenResult> RefreshTokenAsync(int deviceId, string terminalId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var credentials = await GetApiCredentialsAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            return new TokenResult { ErrorMessage = "Missing client_id or client_secret." };
        }

        var client = httpClientFactory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            client_id = credentials.ClientId,
            client_secret = credentials.ClientSecret,
            audience = "https://api.mykvh.com",
            grant_type = "jwt_bearer",
            scope = $"asset#{terminalId}"
        }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("https://mapi.mykvh.com/oauth/token", content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new TokenResult { Refreshed = true, ErrorMessage = $"Token API returned HTTP {(int)response.StatusCode}." };
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var accessToken = KvhJsonHelpers.FindStringValue(document.RootElement, "access_token");
            var expiresIn = KvhJsonHelpers.FindLongValue(document.RootElement, "expires_in") ?? 3600;
            var expiredAt = DateTime.UtcNow.AddSeconds(expiresIn);
            await UpdateDeviceTokenAsync(connection, deviceId, accessToken, expiredAt, cancellationToken);
            return new TokenResult { AccessToken = accessToken, Refreshed = true };
        }
        catch (JsonException)
        {
            return new TokenResult { Refreshed = true, ErrorMessage = "Token API returned invalid JSON." };
        }
    }

    private async Task UpdatePendingAsync(KvhCommand command, JobPollResult result, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [dbo].[TblKvhCommand]
            SET [CommandStatus] = @commandStatus,
                [JobStatus] = @jobStatus,
                [JobResponseJson] = @jobResponseJson,
                [NextPollAtUtc] = @nextPollAtUtc,
                [ErrorCode] = NULL,
                [ErrorMessage] = NULL
            WHERE [ID] = @id
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var dbCommand = new SqlCommand(query, connection);
        dbCommand.Parameters.Add("@id", SqlDbType.BigInt).Value = command.Id;
        dbCommand.Parameters.Add("@commandStatus", SqlDbType.NVarChar, 30).Value = KvhCommandStatuses.Pending;
        dbCommand.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = result.NormalizedStatus;
        dbCommand.Parameters.Add("@jobResponseJson", SqlDbType.NVarChar, -1).Value = result.RawResponse;
        dbCommand.Parameters.Add("@nextPollAtUtc", SqlDbType.DateTime2).Value = DateTime.UtcNow.Add(ResolveBackoff(command.PollCount));
        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ScheduleRetryAsync(KvhCommand command, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [dbo].[TblKvhCommand]
            SET [JobStatus] = @jobStatus,
                [NextPollAtUtc] = @nextPollAtUtc,
                [ErrorCode] = NULLIF(@errorCode, ''),
                [ErrorMessage] = NULLIF(@errorMessage, '')
            WHERE [ID] = @id
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var dbCommand = new SqlCommand(query, connection);
        dbCommand.Parameters.Add("@id", SqlDbType.BigInt).Value = command.Id;
        dbCommand.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = KvhJobStatuses.Unknown;
        dbCommand.Parameters.Add("@nextPollAtUtc", SqlDbType.DateTime2).Value = DateTime.UtcNow.Add(ResolveBackoff(command.PollCount));
        dbCommand.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode;
        dbCommand.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = errorMessage;
        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetVerifyingAsync(long commandId, string jobStatus, string jobResponseJson, CancellationToken cancellationToken)
    {
        const string query = "UPDATE [dbo].[TblKvhCommand] SET [CommandStatus] = @commandStatus, [JobStatus] = @jobStatus, [JobResponseJson] = @jobResponseJson, [VerificationStatus] = @verificationStatus WHERE [ID] = @id";
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@commandStatus", SqlDbType.NVarChar, 30).Value = KvhCommandStatuses.Verifying;
        command.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = jobStatus;
        command.Parameters.Add("@jobResponseJson", SqlDbType.NVarChar, -1).Value = jobResponseJson;
        command.Parameters.Add("@verificationStatus", SqlDbType.NVarChar, 30).Value = KvhVerificationStatuses.Pending;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CompleteCommandAsync(long commandId, string commandStatus, string jobStatus, string? jobResponseJson, string? verificationResponseJson, string? errorCode, string? errorMessage, CancellationToken cancellationToken, string? verificationStatus = null, bool writeActivity = true)
    {
        const string query = """
            UPDATE [dbo].[TblKvhCommand]
            SET [CommandStatus] = @commandStatus,
                [JobStatus] = @jobStatus,
                [VerificationStatus] = @verificationStatus,
                [JobResponseJson] = COALESCE(@jobResponseJson, [JobResponseJson]),
                [VerificationResponseJson] = @verificationResponseJson,
                [CompletedAtUtc] = COALESCE([CompletedAtUtc], SYSUTCDATETIME()),
                [VerifiedAtUtc] = CASE WHEN @verificationStatus IS NULL THEN [VerifiedAtUtc] ELSE SYSUTCDATETIME() END,
                [ErrorCode] = NULLIF(@errorCode, ''),
                [ErrorMessage] = NULLIF(@errorMessage, '')
            WHERE [ID] = @id
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@commandStatus", SqlDbType.NVarChar, 30).Value = commandStatus;
        command.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = jobStatus;
        command.Parameters.Add("@verificationStatus", SqlDbType.NVarChar, 30).Value = (object?)verificationStatus ?? DBNull.Value;
        command.Parameters.Add("@jobResponseJson", SqlDbType.NVarChar, -1).Value = (object?)jobResponseJson ?? DBNull.Value;
        command.Parameters.Add("@verificationResponseJson", SqlDbType.NVarChar, -1).Value = (object?)verificationResponseJson ?? DBNull.Value;
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode ?? string.Empty;
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = errorMessage ?? string.Empty;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (writeActivity)
        {
            await WriteCommandCompletionActivitySafeAsync(commandId, commandStatus, verificationStatus, errorCode, errorMessage, cancellationToken);
        }
    }

    private async Task WriteCommandCompletionActivitySafeAsync(long commandId, string commandStatus, string? verificationStatus, string? errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            const string query = """
                SELECT TOP 1 c.[DeviceId], c.[CommandType], c.[RequestedValue], c.[RequestedByUserId], c.[RequestedBy], c.[JobId], d.[TenantID]
                FROM [dbo].[TblKvhCommand] c
                LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = c.[DeviceId]
                WHERE c.[ID] = @id
                """;
            await using var lookup = new SqlCommand(query, connection);
            lookup.Parameters.Add("@id", SqlDbType.BigInt).Value = commandId;
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            var commandType = reader["CommandType"]?.ToString() ?? string.Empty;
            var success = commandStatus.Equals(KvhCommandStatuses.Completed, StringComparison.OrdinalIgnoreCase)
                || commandStatus.Equals(KvhCommandStatuses.Verified, StringComparison.OrdinalIgnoreCase)
                || commandStatus.Equals(KvhCommandStatuses.Success, StringComparison.OrdinalIgnoreCase);
            var requestedValue = reader["RequestedValue"]?.ToString() ?? string.Empty;
            var (category, action) = ResolveCompletionActivity(commandType, success, requestedValue);
            if (string.IsNullOrWhiteSpace(action))
            {
                return;
            }
            var oldValue = ResolveCompletionOldValue(commandType, requestedValue, success);
            var newValue = ResolveCompletionNewValue(commandType, requestedValue, success);

            await deviceActivityLogService.WriteAsync(new DeviceActivityLogEntry
            {
                DeviceId = Convert.ToInt32(reader["DeviceId"]),
                TenantId = reader["TenantID"] == DBNull.Value ? null : Convert.ToInt32(reader["TenantID"]),
                Category = category,
                Action = action,
                Status = success ? DeviceActivityStatuses.Succeeded : DeviceActivityStatuses.Failed,
                OldValue = oldValue,
                NewValue = newValue,
                Summary = success ? $"KVH command {commandType} completed." : $"KVH command {commandType} failed.",
                DetailJson = DeviceActivityLogEntry.ToSafeJson(new { commandId, commandType, commandStatus, verificationStatus, errorCode, errorMessage }),
                Source = DeviceActivitySources.KvhWorker,
                ActorType = DeviceActivityActorTypes.System,
                UserId = reader["RequestedByUserId"] == DBNull.Value ? null : Convert.ToInt32(reader["RequestedByUserId"]),
                PerformedBy = "KVH Worker",
                ReferenceType = "KVH_COMMAND",
                ReferenceId = commandId.ToString(),
                CorrelationId = reader["JobId"]?.ToString() ?? commandId.ToString(),
                EventKey = $"{action}:{Convert.ToInt32(reader["DeviceId"])}:{commandId}"
            }, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Failed to write KVH command completion activity. CommandId={CommandId}", commandId);
        }
    }

    private async Task WriteSubscriptionCompletionAfterSyncSafeAsync(KvhCommand command, string commandType, bool syncSucceeded, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        try
        {
            var current = syncSucceeded ? await GetCurrentKvhSubscriptionStateAsync(command.DeviceId, command.KvhSubscriptionId, cancellationToken) : null;
            var confirmed = commandType switch
            {
                KvhCommandTypes.SubscriptionResume => current is not null && IsActiveSubscriptionStatus(current.Status),
                KvhCommandTypes.SubscriptionPause => current is not null && IsPausedSubscriptionStatus(current.Status),
                KvhCommandTypes.SubscriptionCancelSchedule => current is not null && string.IsNullOrWhiteSpace(current.ScheduledAction),
                _ => false
            };
            var action = commandType switch
            {
                KvhCommandTypes.SubscriptionResume => confirmed ? DeviceActivityActions.SubscriptionResumed : DeviceActivityActions.SubscriptionResumeFailed,
                KvhCommandTypes.SubscriptionPause => confirmed ? DeviceActivityActions.SubscriptionPaused : DeviceActivityActions.SubscriptionPauseFailed,
                KvhCommandTypes.SubscriptionCancelSchedule => confirmed ? DeviceActivityActions.SubscriptionCancelScheduleCompleted : DeviceActivityActions.SubscriptionCancelScheduleFailed,
                _ => string.Empty
            };
            if (string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            await deviceActivityLogService.WriteAsync(new DeviceActivityLogEntry
            {
                DeviceId = command.DeviceId,
                TenantId = await GetDeviceTenantIdAsync(command.DeviceId, cancellationToken),
                Category = DeviceActivityCategories.Subscription,
                Action = action,
                Status = confirmed ? DeviceActivityStatuses.Succeeded : DeviceActivityStatuses.Failed,
                OldValue = confirmed ? ResolveSubscriptionOldState(commandType) : null,
                NewValue = confirmed ? ResolveSubscriptionNewState(commandType, current) : current?.Status,
                Summary = confirmed ? $"KVH subscription {commandType} completed after sync." : $"KVH subscription {commandType} could not be confirmed after sync.",
                DetailJson = DeviceActivityLogEntry.ToSafeJson(new { commandId = command.Id, commandType, command.JobId, command.KvhSubscriptionId, currentStatus = current?.Status, currentScheduledAction = current?.ScheduledAction, errorCode, errorMessage }),
                Source = DeviceActivitySources.KvhWorker,
                ActorType = DeviceActivityActorTypes.System,
                PerformedBy = "KVH Worker",
                ReferenceType = "KVH_COMMAND",
                ReferenceId = command.Id.ToString(),
                CorrelationId = string.IsNullOrWhiteSpace(command.JobId) ? command.Id.ToString() : command.JobId,
                EventKey = $"{action}:{command.DeviceId}:{command.Id}"
            }, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Failed to write KVH subscription completion activity after sync. CommandId={CommandId}", command.Id);
        }
    }

    private static (string Category, string Action) ResolveCompletionActivity(string commandType, bool success, string requestedValue)
    {
        var requestedEnabled = TryReadRequestedBool(requestedValue, "enabled");
        return commandType switch
        {
            KvhCommandTypes.WifiUpdate => (DeviceActivityCategories.Networking, success ? DeviceActivityActions.WifiUpdateCompleted : DeviceActivityActions.WifiUpdateFailed),
            KvhCommandTypes.Reboot => (DeviceActivityCategories.Networking, success ? DeviceActivityActions.RouterRebootCompleted : DeviceActivityActions.RouterRebootFailed),
            KvhCommandTypes.DataOptIn => (DeviceActivityCategories.Data, requestedEnabled == false
                ? success ? DeviceActivityActions.DataOptOutCompleted : DeviceActivityActions.DataOptOutFailed
                : success ? DeviceActivityActions.DataOptInCompleted : DeviceActivityActions.DataOptInFailed),
            KvhCommandTypes.SubscriptionResume => (DeviceActivityCategories.Subscription, DeviceActivityActions.SubscriptionResumeFailed),
            KvhCommandTypes.SubscriptionPause => (DeviceActivityCategories.Subscription, DeviceActivityActions.SubscriptionPauseFailed),
            KvhCommandTypes.SubscriptionCancelSchedule => (DeviceActivityCategories.Subscription, DeviceActivityActions.SubscriptionCancelScheduleFailed),
            _ => (string.Empty, string.Empty)
        };
    }

    private async Task<KvhSubscriptionState?> GetCurrentKvhSubscriptionStateAsync(int deviceId, long? kvhSubscriptionId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 [ID], [Status], [ScheduledAction]
            FROM [dbo].[TblKvhSubscription]
            WHERE [DeviceId] = @deviceId
              AND [IsCurrent] = 1
              AND (@kvhSubscriptionId IS NULL OR [ID] = @kvhSubscriptionId)
            ORDER BY [LastSeenAtUtc] DESC, [ID] DESC
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@kvhSubscriptionId", SqlDbType.BigInt).Value = (object?)kvhSubscriptionId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new KvhSubscriptionState(
            Convert.ToInt64(reader["ID"]),
            reader["Status"]?.ToString() ?? string.Empty,
            reader["ScheduledAction"]?.ToString() ?? string.Empty);
    }

    private async Task<int?> GetDeviceTenantIdAsync(int deviceId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT TOP 1 [TenantID] FROM [dbo].[TblDevices] WHERE [ID] = @deviceId", connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? null : Convert.ToInt32(scalar);
    }

    private static string? ResolveCompletionOldValue(string commandType, string requestedValue, bool success)
    {
        if (!success)
        {
            return null;
        }

        return commandType == KvhCommandTypes.DataOptIn ? null : null;
    }

    private static string? ResolveCompletionNewValue(string commandType, string requestedValue, bool success)
    {
        if (!success)
        {
            return null;
        }

        if (commandType == KvhCommandTypes.DataOptIn)
        {
            return TryReadRequestedBool(requestedValue, "enabled") == false ? "off" : "on";
        }

        return null;
    }

    private static string? ResolveSubscriptionOldState(string commandType) => commandType switch
    {
        KvhCommandTypes.SubscriptionResume => "paused",
        KvhCommandTypes.SubscriptionPause => "active",
        KvhCommandTypes.SubscriptionCancelSchedule => "cancel_scheduled",
        _ => null
    };

    private static string? ResolveSubscriptionNewState(string commandType, KvhSubscriptionState? current) => commandType switch
    {
        KvhCommandTypes.SubscriptionResume => "active",
        KvhCommandTypes.SubscriptionPause => "paused",
        KvhCommandTypes.SubscriptionCancelSchedule => string.IsNullOrWhiteSpace(current?.ScheduledAction) ? null : current.ScheduledAction,
        _ => current?.Status
    };

    private static bool IsActiveSubscriptionStatus(string status) =>
        status.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase);

    private static bool IsPausedSubscriptionStatus(string status) =>
        status.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("PAUSE", StringComparison.OrdinalIgnoreCase);

    private TimeSpan ResolveBackoff(int pollCount)
    {
        return TimeSpan.FromSeconds(Math.Max(120, monitorOptions.Value.JobPollIntervalSeconds));
    }

    private static bool? TryReadRequestedBool(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return KvhJsonHelpers.FindBooleanValue(document.RootElement, propertyName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeJobStatus(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return KvhJobStatuses.Unknown;
        if (value.Contains("success", StringComparison.OrdinalIgnoreCase) || value.Contains("complete", StringComparison.OrdinalIgnoreCase)) return KvhJobStatuses.Success;
        if (value.Contains("fail", StringComparison.OrdinalIgnoreCase) || value.Contains("error", StringComparison.OrdinalIgnoreCase)) return KvhJobStatuses.Failed;
        if (value.Contains("pending", StringComparison.OrdinalIgnoreCase) || value.Contains("running", StringComparison.OrdinalIgnoreCase) || value.Contains("process", StringComparison.OrdinalIgnoreCase)) return KvhJobStatuses.Pending;
        return KvhJobStatuses.Unknown;
    }

    private static IReadOnlyList<JsonElement> ResolveSubscriptionArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(item => item.Clone()).ToList();
        }

        foreach (var name in new[] { "subscriptions", "data", "items", "results" })
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out var child) &&
                child.ValueKind == JsonValueKind.Array)
            {
                return child.EnumerateArray().Select(item => item.Clone()).ToList();
            }
        }

        return root.ValueKind == JsonValueKind.Object ? [root.Clone()] : [];
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

    private async Task UpdateDeviceTokenAsync(SqlConnection connection, int deviceId, string? accessToken, DateTime? tokenExpiredTime, CancellationToken cancellationToken)
    {
        const string query = "UPDATE [dbo].[TblDevices] SET [TokenString] = @tokenString, [TokenExpiredTime] = @tokenExpiredTime WHERE [ID] = @id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@tokenString", SqlDbType.NVarChar, -1).Value = (object?)accessToken ?? DBNull.Value;
        command.Parameters.Add("@tokenExpiredTime", SqlDbType.DateTime2).Value = (object?)tokenExpiredTime ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static HttpRequestMessage CreateKvhRequest(HttpMethod method, string uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static bool IsTokenExpired(DateTime? tokenExpiredTime) =>
        !tokenExpiredTime.HasValue || DateTime.SpecifyKind(tokenExpiredTime.Value, DateTimeKind.Utc) <= DateTime.UtcNow;

    private static KvhCommand MapCommand(SqlDataReader reader) => new()
    {
        Id = Convert.ToInt64(reader["ID"]),
        DeviceId = Convert.ToInt32(reader["DeviceId"]),
        TerminalId = reader["TerminalId"]?.ToString() ?? string.Empty,
        KvhDeviceId = reader["KvhDeviceId"]?.ToString() ?? string.Empty,
        TrafficId = HasColumn(reader, "TrafficId") ? reader["TrafficId"]?.ToString() ?? string.Empty : string.Empty,
        Region = HasColumn(reader, "Region") ? reader["Region"]?.ToString() ?? string.Empty : string.Empty,
        ScheduleId = HasColumn(reader, "ScheduleId") ? reader["ScheduleId"]?.ToString() ?? string.Empty : string.Empty,
        KvhSubscriptionId = HasColumn(reader, "KvhSubscriptionId") && reader["KvhSubscriptionId"] != DBNull.Value ? Convert.ToInt64(reader["KvhSubscriptionId"]) : null,
        CooldownUntilUtc = HasColumn(reader, "CooldownUntilUtc") && reader["CooldownUntilUtc"] != DBNull.Value ? Convert.ToDateTime(reader["CooldownUntilUtc"]) : null,
        CommandType = reader["CommandType"]?.ToString() ?? string.Empty,
        RequestedValue = reader["RequestedValue"]?.ToString() ?? string.Empty,
        JobId = reader["JobId"]?.ToString() ?? string.Empty,
        CommandStatus = reader["CommandStatus"]?.ToString() ?? string.Empty,
        JobStatus = reader["JobStatus"]?.ToString() ?? string.Empty,
        RequestedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["RequestedAtUtc"]), DateTimeKind.Utc),
        PollCount = Convert.ToInt32(reader["PollCount"]),
        MaxPollCount = Convert.ToInt32(reader["MaxPollCount"])
    };

    private static bool HasColumn(IDataRecord reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class JobPollResult
    {
        public int? HttpStatusCode { get; set; }
        public string RawResponse { get; set; } = string.Empty;
        public bool ValidJson { get; set; }
        public bool Timeout { get; set; }
        public string NormalizedStatus { get; set; } = KvhJobStatuses.Unknown;
        public string Message { get; set; } = string.Empty;
    }

    private sealed class KvhHttpResponse
    {
        public bool Success { get; set; }
        public int? HttpStatusCode { get; set; }
        public string RawResponse { get; set; } = string.Empty;
    }

    private sealed class TokenResult
    {
        public string AccessToken { get; set; } = string.Empty;
        public bool Refreshed { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    private sealed record KvhSubscriptionState(long Id, string Status, string ScheduledAction);

    private sealed class VerificationResult
    {
        public bool Success { get; set; }
        public bool Timeout { get; set; }
        public string ErrorCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string ResponseJson { get; set; } = string.Empty;
        public string CommandStatus { get; set; } = string.Empty;
        public string VerificationStatus { get; set; } = string.Empty;

        public static VerificationResult Ok(string responseJson, string commandStatus = "", string verificationStatus = "") => new() { Success = true, ResponseJson = responseJson, CommandStatus = commandStatus, VerificationStatus = verificationStatus };
        public static VerificationResult Mismatch(string errorCode, string message, string responseJson = "") => new() { Success = false, ErrorCode = errorCode, Message = message, ResponseJson = responseJson };
        public static VerificationResult TimedOut(string message, string responseJson = "") => new() { Success = false, Timeout = true, ErrorCode = KvhErrorCodes.VerificationFailed, Message = message, ResponseJson = responseJson };
    }
}
