using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class KvhCommandService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IOptions<KvhJobMonitorOptions> monitorOptions,
    ILogger<KvhCommandService> logger) : IKvhCommandService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task<KvhCommandSubmitResult> SubmitDataOptInAsync(UpdateDeviceDataOptInRequest request, int? userId, string requestedBy, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        var context = await GetCommandContextAsync(request.Id, tenantId, allowedDeviceId, useRouterDevice: false, cancellationToken);
        if (!context.Success)
        {
            return context.ToSubmitResult(request.Id);
        }

        var usage = await RequestTerminalUsageAsync(context.TerminalId, context.AccessToken, cancellationToken);
        var oldStatus = usage.Success ? usage.DataOptInEnabled : null;
        if (oldStatus == request.Enabled)
        {
            return new KvhCommandSubmitResult
            {
                Success = false,
                Unchanged = true,
                ErrorCode = "status_unchanged",
                Message = "Thiet bi da o trang thai duoc chon.",
                MessageEn = "The terminal is already in the selected state.",
                DeviceId = request.Id,
                TerminalId = context.TerminalId,
                KvhDeviceId = context.KvhDeviceId,
                OldDataOptInStatus = oldStatus,
                NewDataOptInStatus = request.Enabled
            };
        }

        var requestJson = JsonSerializer.Serialize(new { enabled = request.Enabled });
        return await SubmitCommandAsync(
            context,
            KvhCommandTypes.DataOptIn,
            requestedValue: JsonSerializer.Serialize(new { enabled = request.Enabled, previous = oldStatus }),
            requestJson,
            userId,
            requestedBy,
            async token => await SendDataOptInAsync(context.TerminalId, context.AccessToken, request.Enabled, token),
            cancellationToken,
            oldStatus,
            request.Enabled);
    }

    public async Task<KvhCommandSubmitResult> SubmitWifiUpdateAsync(UpdateDeviceWifiRequest request, int? userId, string requestedBy, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        request.Ssid = request.Ssid.Trim();
        request.Password = request.Password.Trim();
        if (request.Id <= 0 || string.IsNullOrWhiteSpace(request.Ssid) || string.IsNullOrWhiteSpace(request.Password))
        {
            return new KvhCommandSubmitResult
            {
                Success = false,
                ErrorCode = "wifi_validation_required",
                Message = "SSID va mat khau WiFi la bat buoc.",
                MessageEn = "SSID and WiFi password are required.",
                DeviceId = request.Id
            };
        }

        var context = await GetCommandContextAsync(request.Id, tenantId, allowedDeviceId, useRouterDevice: true, cancellationToken);
        if (!context.Success)
        {
            return context.ToSubmitResult(request.Id);
        }

        var requestJson = JsonSerializer.Serialize(new { ssid = request.Ssid, password = "***", enabled = request.Enabled });
        var requestedValue = JsonSerializer.Serialize(new { ssid = request.Ssid, enabled = request.Enabled });
        return await SubmitCommandAsync(
            context,
            KvhCommandTypes.WifiUpdate,
            requestedValue,
            requestJson,
            userId,
            requestedBy,
            async token => await SendWifiUpdateAsync(context.TerminalId, context.KvhDeviceId, context.AccessToken, request.Ssid, request.Password, request.Enabled, token),
            cancellationToken);
    }

    public async Task<KvhCommandSubmitResult> SubmitRebootAsync(int id, int? userId, string requestedBy, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        var context = await GetCommandContextAsync(id, tenantId, allowedDeviceId, useRouterDevice: false, cancellationToken);
        if (!context.Success)
        {
            return context.ToSubmitResult(id);
        }

        var beforeStatus = await RequestTerminalStatusAsync(context.TerminalId, context.AccessToken, cancellationToken);
        var requestJson = JsonSerializer.Serialize(new { action = "reboot" });
        var requestedValue = JsonSerializer.Serialize(new { beforeStatus.RawResponse, beforeStatus.UptimeSeconds, requestedAtUtc = DateTime.UtcNow });
        return await SubmitCommandAsync(
            context,
            KvhCommandTypes.Reboot,
            requestedValue,
            requestJson,
            userId,
            requestedBy,
            async token => await SendRebootAsync(context.TerminalId, context.KvhDeviceId, context.AccessToken, token),
            cancellationToken);
    }

    public async Task<KvhCommandStatusDto?> GetCommandStatusAsync(long commandId, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string query = """
            SELECT TOP 1 c.*
            FROM [dbo].[TblKvhCommand] c
            INNER JOIN [dbo].[TblDevices] d ON d.[ID] = c.[DeviceId]
            WHERE c.[ID] = @id
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapStatusDto(reader) : null;
    }

    public async Task<IReadOnlyList<KvhCommandStatusDto>> GetRecentCommandsAsync(int deviceId, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        var items = new List<KvhCommandStatusDto>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string query = """
            SELECT TOP 20 c.*
            FROM [dbo].[TblKvhCommand] c
            INNER JOIN [dbo].[TblDevices] d ON d.[ID] = c.[DeviceId]
            WHERE c.[DeviceId] = @deviceId
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
            ORDER BY c.[RequestedAtUtc] DESC, c.[ID] DESC
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapStatusDto(reader));
        }

        return items;
    }

    private async Task<KvhCommandSubmitResult> SubmitCommandAsync(
        CommandContext context,
        string commandType,
        string requestedValue,
        string requestJson,
        int? userId,
        string requestedBy,
        Func<CancellationToken, Task<KvhHttpResult>> submit,
        CancellationToken cancellationToken,
        bool? oldDataOptInStatus = null,
        bool? newDataOptInStatus = null)
    {
        var cooldown = await CheckCooldownAsync(context.TerminalId, cancellationToken);
        if (!cooldown.Allowed)
        {
            return new KvhCommandSubmitResult
            {
                Success = false,
                ErrorCode = KvhErrorCodes.TerminalCommandCooldown,
                Message = $"Vui long doi {cooldown.RemainingSeconds} giay truoc khi gui lenh KVH tiep theo.",
                MessageEn = $"Please wait {cooldown.RemainingSeconds} seconds before sending another KVH command.",
                DeviceId = context.DeviceId,
                TerminalId = context.TerminalId,
                KvhDeviceId = context.KvhDeviceId,
                RemainingSeconds = cooldown.RemainingSeconds,
                NextAllowedAtUtc = cooldown.NextAllowedAtUtc,
                OldDataOptInStatus = oldDataOptInStatus,
                NewDataOptInStatus = newDataOptInStatus
            };
        }

        var commandId = await InsertCommandAsync(context, commandType, requestedValue, requestJson, userId, requestedBy, cancellationToken);
        logger.LogInformation("Submitting KVH command {CommandId} type {CommandType} for terminal {TerminalId}", commandId, commandType, context.TerminalId);

        var submitResult = await submit(cancellationToken);
        var rawResponse = commandType == KvhCommandTypes.WifiUpdate
            ? KvhJsonHelpers.MaskWifiSecrets(submitResult.RawResponse)
            : submitResult.RawResponse;
        var jobId = KvhJsonHelpers.ExtractJobId(rawResponse);

        if (!submitResult.Success)
        {
            await MarkSubmitFailedAsync(commandId, submitResult.HttpStatusCode, rawResponse, submitResult.ErrorCode, submitResult.ErrorMessage, cancellationToken);
            return new KvhCommandSubmitResult
            {
                Success = false,
                ErrorCode = submitResult.ErrorCode,
                Message = submitResult.ErrorMessage,
                MessageEn = submitResult.ErrorMessage,
                DeviceId = context.DeviceId,
                TerminalId = context.TerminalId,
                KvhDeviceId = context.KvhDeviceId,
                CommandId = commandId,
                HttpStatusCode = submitResult.HttpStatusCode,
                RawResponse = rawResponse,
                OldDataOptInStatus = oldDataOptInStatus,
                NewDataOptInStatus = newDataOptInStatus
            };
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            await MarkSubmitFailedAsync(commandId, submitResult.HttpStatusCode, rawResponse, KvhErrorCodes.MissingJobId, "KVH accepted the request but did not return a job id.", cancellationToken);
            return new KvhCommandSubmitResult
            {
                Success = false,
                ErrorCode = KvhErrorCodes.MissingJobId,
                Message = "KVH da tiep nhan request nhung khong tra ve Job ID.",
                MessageEn = "KVH accepted the request but did not return a job id.",
                DeviceId = context.DeviceId,
                TerminalId = context.TerminalId,
                KvhDeviceId = context.KvhDeviceId,
                CommandId = commandId,
                HttpStatusCode = submitResult.HttpStatusCode,
                RawResponse = rawResponse,
                OldDataOptInStatus = oldDataOptInStatus,
                NewDataOptInStatus = newDataOptInStatus
            };
        }

        await MarkSubmittedAsync(commandId, jobId, submitResult.HttpStatusCode, rawResponse, cancellationToken);
        return new KvhCommandSubmitResult
        {
            Success = true,
            Message = "KVH da tiep nhan lenh. He thong dang theo doi Job.",
            MessageEn = "KVH accepted the command. The job is being monitored.",
            DeviceId = context.DeviceId,
            TerminalId = context.TerminalId,
            KvhDeviceId = context.KvhDeviceId,
            CommandId = commandId,
            JobId = jobId,
            HttpStatusCode = submitResult.HttpStatusCode,
            RawResponse = rawResponse,
            OldDataOptInStatus = oldDataOptInStatus,
            NewDataOptInStatus = newDataOptInStatus
        };
    }

    private async Task<CommandContext> GetCommandContextAsync(int id, int? tenantId, int? allowedDeviceId, bool useRouterDevice, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var device = await GetDeviceAsync(connection, id, tenantId, allowedDeviceId, cancellationToken);
        if (device is null)
        {
            return CommandContext.Fail("device_not_found", "Khong tim thay thiet bi hoac ban khong co quyen truy cap.", "The device was not found or you do not have access.");
        }

        var terminalId = device.DeviceCode.Trim();
        var kvhDeviceId = device.KitId.Trim();
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(kvhDeviceId))
        {
            return CommandContext.Fail("missing_wifi_identifiers", "Thieu terminal hoac KVH device id.", "Terminal or KVH device id is missing.", terminalId, kvhDeviceId);
        }

        var accessToken = device.TokenString;
        if (string.IsNullOrWhiteSpace(accessToken) || IsTokenExpired(device.TokenExpiredTime))
        {
            var credentials = await GetApiCredentialsAsync(connection, cancellationToken);
            if (string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
            {
                return CommandContext.Fail("missing_api_credentials", "Thieu client_id hoac client_secret trong TblSettings.", "Missing client_id or client_secret in TblSettings.", terminalId, kvhDeviceId);
            }

            var token = await RequestDeviceTokenAsync(credentials.ClientId, credentials.ClientSecret, terminalId, cancellationToken);
            if (!token.Success || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return CommandContext.Fail(KvhErrorCodes.TokenRefreshFailed, token.Message, token.MessageEn, terminalId, kvhDeviceId);
            }

            accessToken = token.AccessToken;
            await UpdateDeviceTokenAsync(connection, id, token.AccessToken, token.ExpiredTime, cancellationToken);
        }

        if (useRouterDevice)
        {
            var routerDevice = await ResolveRouterDeviceIdAsync(terminalId, accessToken, cancellationToken);
            if (!routerDevice.Success)
            {
                return CommandContext.Fail(routerDevice.ErrorCode, routerDevice.ErrorMessage, routerDevice.ErrorMessage, terminalId, kvhDeviceId);
            }

            kvhDeviceId = routerDevice.DeviceId;
        }

        return new CommandContext
        {
            Success = true,
            DeviceId = id,
            TerminalId = terminalId,
            KvhDeviceId = kvhDeviceId,
            AccessToken = accessToken
        };
    }

    private async Task<(bool Allowed, int RemainingSeconds, DateTime? NextAllowedAtUtc)> CheckCooldownAsync(string terminalId, CancellationToken cancellationToken)
    {
        var cooldownMinutes = Math.Max(1, monitorOptions.Value.TerminalCommandCooldownMinutes);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string query = """
            SELECT TOP 1 [RequestedAtUtc]
            FROM [dbo].[TblKvhCommand]
            WHERE [TerminalId] = @terminalId
              AND [CommandStatus] NOT IN ('FAILED', 'TIMEOUT', 'VERIFICATION_MISMATCH', 'VERIFICATION_TIMEOUT')
            ORDER BY [RequestedAtUtc] DESC, [ID] DESC
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@terminalId", SqlDbType.NVarChar, 200).Value = terminalId;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            return (true, 0, null);
        }

        var lastRequestedAt = DateTime.SpecifyKind(Convert.ToDateTime(scalar), DateTimeKind.Utc);
        var nextAllowedAt = lastRequestedAt.AddMinutes(cooldownMinutes);
        var remaining = (int)Math.Ceiling((nextAllowedAt - DateTime.UtcNow).TotalSeconds);
        return remaining <= 0 ? (true, 0, null) : (false, remaining, nextAllowedAt);
    }

    private async Task<long> InsertCommandAsync(CommandContext context, string commandType, string requestedValue, string requestJson, int? userId, string requestedBy, CancellationToken cancellationToken)
    {
        const string query = """
            INSERT INTO [dbo].[TblKvhCommand]
                ([DeviceId], [TerminalId], [KvhDeviceId], [CommandType], [RequestedValue], [CommandStatus], [JobStatus], [VerificationStatus],
                 [RequestJson], [RequestedByUserId], [RequestedBy], [RequestedAtUtc], [NextPollAtUtc], [MaxPollCount])
            OUTPUT INSERTED.[ID]
            VALUES
                (@deviceId, @terminalId, @kvhDeviceId, @commandType, @requestedValue, @commandStatus, @jobStatus, @verificationStatus,
                 @requestJson, @userId, @requestedBy, @requestedAtUtc, @nextPollAtUtc, @maxPollCount)
            """;
        var now = DateTime.UtcNow;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = context.DeviceId;
        command.Parameters.Add("@terminalId", SqlDbType.NVarChar, 200).Value = context.TerminalId;
        command.Parameters.Add("@kvhDeviceId", SqlDbType.NVarChar, 200).Value = context.KvhDeviceId;
        command.Parameters.Add("@commandType", SqlDbType.NVarChar, 50).Value = commandType;
        command.Parameters.Add("@requestedValue", SqlDbType.NVarChar, -1).Value = requestedValue;
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

    private async Task MarkSubmittedAsync(long commandId, string jobId, int? httpStatusCode, string rawResponse, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [dbo].[TblKvhCommand]
            SET [CommandStatus] = @commandStatus,
                [JobStatus] = @jobStatus,
                [JobId] = @jobId,
                [HttpStatusCode] = @httpStatusCode,
                [SubmitResponseJson] = @rawResponse
            WHERE [ID] = @id
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@commandStatus", SqlDbType.NVarChar, 30).Value = KvhCommandStatuses.Submitted;
        command.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = KvhJobStatuses.Submitted;
        command.Parameters.Add("@jobId", SqlDbType.NVarChar, 200).Value = jobId;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)httpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@rawResponse", SqlDbType.NVarChar, -1).Value = rawResponse;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkSubmitFailedAsync(long commandId, int? httpStatusCode, string rawResponse, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [dbo].[TblKvhCommand]
            SET [CommandStatus] = @commandStatus,
                [JobStatus] = @jobStatus,
                [HttpStatusCode] = @httpStatusCode,
                [SubmitResponseJson] = @rawResponse,
                [CompletedAtUtc] = SYSUTCDATETIME(),
                [ErrorCode] = @errorCode,
                [ErrorMessage] = @errorMessage
            WHERE [ID] = @id
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@commandStatus", SqlDbType.NVarChar, 30).Value = KvhCommandStatuses.Failed;
        command.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 30).Value = KvhJobStatuses.Unknown;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)httpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@rawResponse", SqlDbType.NVarChar, -1).Value = rawResponse;
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode;
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = errorMessage;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<KvhHttpResult> SendDataOptInAsync(string terminalId, string accessToken, bool enabled, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Put, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/optin", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { enabled }), Encoding.UTF8, "application/json");
        return await SendKvhRequestAsync(request, "data_optin_api_error", cancellationToken);
    }

    private async Task<KvhHttpResult> SendWifiUpdateAsync(string terminalId, string deviceId, string accessToken, string ssid, string password, bool enabled, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Post, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/devices/{Uri.EscapeDataString(deviceId)}/wifi", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { ssid, password, enabled }), Encoding.UTF8, "application/json");
        return await SendKvhRequestAsync(request, "wifi_update_api_error", cancellationToken);
    }

    private async Task<KvhHttpResult> SendRebootAsync(string terminalId, string deviceId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Post, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/devices/{Uri.EscapeDataString(deviceId)}/reboot", accessToken);
        return await SendKvhRequestAsync(request, "router_reboot_api_error", cancellationToken);
    }

    private async Task<KvhHttpResult> SendKvhRequestAsync(HttpRequestMessage request, string errorCode, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken);
            var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            return new KvhHttpResult
            {
                Success = response.IsSuccessStatusCode,
                HttpStatusCode = (int)response.StatusCode,
                RawResponse = rawResponse,
                ErrorCode = response.IsSuccessStatusCode ? string.Empty : errorCode,
                ErrorMessage = response.IsSuccessStatusCode ? string.Empty : $"KVH API returned HTTP {(int)response.StatusCode}."
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KvhHttpResult { Success = false, ErrorCode = KvhErrorCodes.CommandSubmitFailed, ErrorMessage = "KVH command request timed out." };
        }
    }

    private async Task<KvhHttpResult> ResolveRouterDeviceIdAsync(string terminalId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/devices", accessToken);
        var result = await SendKvhRequestAsync(request, "terminal_devices_error", cancellationToken);
        if (!result.Success)
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(result.RawResponse);
            if (!TryFindDeviceArray(document.RootElement, out var devices))
            {
                result.Success = false;
                result.ErrorCode = "router_device_not_found";
                result.ErrorMessage = "No devices array was returned for this terminal.";
                return result;
            }

            foreach (var device in devices.EnumerateArray())
            {
                var type = KvhJsonHelpers.FindStringValue(device, "type", "device_type", "deviceType");
                if (type.Contains("router", StringComparison.OrdinalIgnoreCase))
                {
                    result.DeviceId = KvhJsonHelpers.FindStringValue(device, "id", "device_id", "deviceId");
                    if (!string.IsNullOrWhiteSpace(result.DeviceId))
                    {
                        return result;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        result.Success = false;
        result.ErrorCode = "router_device_not_found";
        result.ErrorMessage = "No Starlink WiFi router was found in this terminal's device list.";
        return result;
    }

    private static HttpRequestMessage CreateKvhRequest(HttpMethod method, string uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<(bool Success, string RawResponse, bool? DataOptInEnabled)> RequestTerminalUsageAsync(string terminalId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/usage", accessToken);
        var result = await SendKvhRequestAsync(request, "usage_api_error", cancellationToken);
        if (!result.Success)
        {
            return (false, result.RawResponse, null);
        }

        try
        {
            using var document = JsonDocument.Parse(result.RawResponse);
            return (true, result.RawResponse, KvhJsonHelpers.FindBooleanValue(document.RootElement, "optin", "optIn", "dataOptIn"));
        }
        catch (JsonException)
        {
            return (false, result.RawResponse, null);
        }
    }

    private async Task<(bool Success, string RawResponse, long? UptimeSeconds)> RequestTerminalStatusAsync(string terminalId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateKvhRequest(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/status", accessToken);
        var result = await SendKvhRequestAsync(request, "status_api_error", cancellationToken);
        if (!result.Success)
        {
            return (false, result.RawResponse, null);
        }

        try
        {
            using var document = JsonDocument.Parse(result.RawResponse);
            return (true, result.RawResponse, KvhJsonHelpers.FindLongValue(document.RootElement, "uptime", "uptimeSeconds", "up_time"));
        }
        catch (JsonException)
        {
            return (false, result.RawResponse, null);
        }
    }

    private async Task<DeviceRow?> GetDeviceAsync(SqlConnection connection, int id, int? tenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 [ID], [DeviceCode], [KITID], [TokenString], [TokenExpiredTime]
            FROM [dbo].[TblDevices]
            WHERE [ID] = @id
              AND (@tenantId IS NULL OR [TenantID] = @tenantId)
              AND (@allowedDeviceId IS NULL OR [ID] = @allowedDeviceId)
            """;
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceRow
        {
            Id = Convert.ToInt32(reader["ID"]),
            DeviceCode = reader["DeviceCode"]?.ToString() ?? string.Empty,
            KitId = reader["KITID"]?.ToString() ?? string.Empty,
            TokenString = reader["TokenString"]?.ToString() ?? string.Empty,
            TokenExpiredTime = reader["TokenExpiredTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["TokenExpiredTime"])
        };
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

    private async Task<(bool Success, string ErrorCode, string Message, string MessageEn, string RawResponse, string? AccessToken, DateTime? ExpiredTime)> RequestDeviceTokenAsync(string clientId, string clientSecret, string terminalId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            client_id = clientId,
            client_secret = clientSecret,
            audience = "https://api.mykvh.com",
            grant_type = "jwt_bearer",
            scope = $"asset#{terminalId}"
        }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("https://mapi.mykvh.com/oauth/token", content, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (false, KvhErrorCodes.TokenRefreshFailed, "Khong refresh duoc KVH token.", "Could not refresh KVH token.", rawResponse, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var accessToken = KvhJsonHelpers.FindStringValue(document.RootElement, "access_token");
            var expiresIn = KvhJsonHelpers.FindLongValue(document.RootElement, "expires_in") ?? 3600;
            return (true, string.Empty, string.Empty, string.Empty, rawResponse, accessToken, DateTime.UtcNow.AddSeconds(expiresIn));
        }
        catch (JsonException)
        {
            return (false, KvhErrorCodes.TokenRefreshFailed, "KVH token response khong hop le.", "KVH token response is invalid.", rawResponse, null, null);
        }
    }

    private async Task UpdateDeviceTokenAsync(SqlConnection connection, int id, string? accessToken, DateTime? tokenExpiredTime, CancellationToken cancellationToken)
    {
        const string query = "UPDATE [dbo].[TblDevices] SET [TokenString] = @tokenString, [TokenExpiredTime] = @tokenExpiredTime WHERE [ID] = @id";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@tokenString", SqlDbType.NVarChar, -1).Value = (object?)accessToken ?? DBNull.Value;
        command.Parameters.Add("@tokenExpiredTime", SqlDbType.DateTime2).Value = (object?)tokenExpiredTime ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool TryFindDeviceArray(JsonElement root, out JsonElement devices)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            devices = root;
            return true;
        }

        foreach (var name in new[] { "devices", "data", "items", "results" })
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out devices) &&
                devices.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }

        devices = default;
        return false;
    }

    private static bool IsTokenExpired(DateTime? tokenExpiredTime) =>
        !tokenExpiredTime.HasValue || DateTime.SpecifyKind(tokenExpiredTime.Value, DateTimeKind.Utc) <= DateTime.UtcNow;

    private static KvhCommandStatusDto MapStatusDto(SqlDataReader reader) => new()
    {
        Id = Convert.ToInt64(reader["ID"]),
        DeviceId = Convert.ToInt32(reader["DeviceId"]),
        TerminalId = reader["TerminalId"]?.ToString() ?? string.Empty,
        KvhDeviceId = reader["KvhDeviceId"]?.ToString() ?? string.Empty,
        CommandType = reader["CommandType"]?.ToString() ?? string.Empty,
        CommandStatus = reader["CommandStatus"]?.ToString() ?? string.Empty,
        JobStatus = reader["JobStatus"]?.ToString() ?? string.Empty,
        VerificationStatus = reader["VerificationStatus"]?.ToString() ?? string.Empty,
        JobId = reader["JobId"]?.ToString() ?? string.Empty,
        RequestedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["RequestedAtUtc"]), DateTimeKind.Utc),
        LastPolledAtUtc = reader["LastPolledAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["LastPolledAtUtc"]), DateTimeKind.Utc),
        NextPollAtUtc = reader["NextPollAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["NextPollAtUtc"]), DateTimeKind.Utc),
        CompletedAtUtc = reader["CompletedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["CompletedAtUtc"]), DateTimeKind.Utc),
        VerifiedAtUtc = reader["VerifiedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["VerifiedAtUtc"]), DateTimeKind.Utc),
        ErrorCode = reader["ErrorCode"]?.ToString() ?? string.Empty,
        ErrorMessage = reader["ErrorMessage"]?.ToString() ?? string.Empty
    };

    private sealed class DeviceRow
    {
        public int Id { get; set; }
        public string DeviceCode { get; set; } = string.Empty;
        public string KitId { get; set; } = string.Empty;
        public string TokenString { get; set; } = string.Empty;
        public DateTime? TokenExpiredTime { get; set; }
    }

    private sealed class CommandContext
    {
        public bool Success { get; set; }
        public int DeviceId { get; set; }
        public string TerminalId { get; set; } = string.Empty;
        public string KvhDeviceId { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string MessageEn { get; set; } = string.Empty;

        public static CommandContext Fail(string errorCode, string message, string messageEn, string terminalId = "", string kvhDeviceId = "") => new()
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            MessageEn = messageEn,
            TerminalId = terminalId,
            KvhDeviceId = kvhDeviceId
        };

        public KvhCommandSubmitResult ToSubmitResult(int deviceId) => new()
        {
            Success = false,
            ErrorCode = ErrorCode,
            Message = Message,
            MessageEn = MessageEn,
            DeviceId = deviceId,
            TerminalId = TerminalId,
            KvhDeviceId = KvhDeviceId
        };
    }

    private sealed class KvhHttpResult
    {
        public bool Success { get; set; }
        public int? HttpStatusCode { get; set; }
        public string RawResponse { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
    }
}
