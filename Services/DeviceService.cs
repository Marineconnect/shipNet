using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class DeviceService(IConfiguration configuration, IHttpClientFactory httpClientFactory) : IDeviceService
{
    private const string CreateDeviceAuditAction = "created_Device";
    private const string UpdateDeviceAuditAction = "updated_Device";

    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");
    private bool? _hasPlanNameColumn;

    public async Task<DevicePageResult> GetDevicesAsync(int page, int pageSize, string? searchTerm = null, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize <= 0 ? 10 : pageSize;
        var offset = (page - 1) * pageSize;

        var devices = new List<DeviceListItemViewModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var hasPlanNameColumn = await HasPlanNameColumnAsync(connection, cancellationToken);
        var normalizedSearchTerm = NormalizeSearchTerm(searchTerm);
        var searchPattern = BuildSearchPattern(normalizedSearchTerm);
        var searchClause = BuildDeviceSearchClause(hasPlanNameColumn);
        var planNameSelect = hasPlanNameColumn
            ? "d.[PlanName],"
            : "CAST(NULL AS nvarchar(255)) AS [PlanName],";
        var countQuery = $"""
            SELECT COUNT(1)
            FROM [TblDevices] d
            LEFT JOIN [TblTenant] t ON t.[ID] = d.[TenantID]
            WHERE (@tenantId IS NULL OR d.[TenantID] = @tenantId)
            {searchClause}
            """;
        var query = $"""
            SELECT
                d.[ID],
                d.[DeviceName],
                d.[DeviceCode],
                d.[VesselName],
                d.[TenantID],
                t.[TenantName],
                d.[KITID],
                d.[Availability],
                d.[UsageData],
                d.[PriorityData],
                d.[OverageData],
                d.[Latitude],
                d.[Longitude],
                d.[SystemType],
                d.[KITNumber],
                d.[ServiceLine],
                {planNameSelect}
                d.[LastUpdateTime],
                d.[TokenExpiredTime],
                d.[LastSysnTime]
            FROM [TblDevices] d
            LEFT JOIN [TblTenant] t ON t.[ID] = d.[TenantID]
            WHERE (@tenantId IS NULL OR d.[TenantID] = @tenantId)
            {searchClause}
            ORDER BY d.[LastUpdateTime] DESC, d.[DeviceName] ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        await using var countCommand = new SqlCommand(countQuery, connection);
        countCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        AddDeviceSearchParameters(countCommand, normalizedSearchTerm, searchPattern);
        var totalResult = await countCommand.ExecuteScalarAsync(cancellationToken);
        var totalDevices = totalResult is int total ? total : Convert.ToInt32(totalResult ?? 0);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        AddDeviceSearchParameters(command, normalizedSearchTerm, searchPattern);
        command.Parameters.AddWithValue("@offset", offset);
        command.Parameters.AddWithValue("@pageSize", pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new DeviceListItemViewModel
            {
                Id = reader["ID"] as int? ?? 0,
                DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
                DeviceCode = reader["DeviceCode"]?.ToString() ?? string.Empty,
                VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
                TenantId = reader["TenantID"] as int?,
                TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
                KitId = reader["KITID"]?.ToString() ?? string.Empty,
                Availability = reader["Availability"]?.ToString() ?? string.Empty,
                UsageData = reader["UsageData"] as decimal?,
                PriorityData = reader["PriorityData"] as decimal?,
                OverageData = reader["OverageData"] as decimal?,
                Latitude = reader["Latitude"]?.ToString() ?? string.Empty,
                Longitude = reader["Longitude"]?.ToString() ?? string.Empty,
                SystemType = reader["SystemType"]?.ToString() ?? string.Empty,
                KitNumber = reader["KITNumber"]?.ToString() ?? string.Empty,
                ServiceLine = reader["ServiceLine"]?.ToString() ?? string.Empty,
                PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
                LastUpdateTime = reader["LastUpdateTime"] as DateTime?,
                TokenExpiredTime = reader["TokenExpiredTime"] as DateTime?,
                LastSysnTime = reader["LastSysnTime"] as DateTime?
            });
        }

        return new DevicePageResult
        {
            Devices = devices,
            TotalDevices = totalDevices
        };
    }

    public async Task<DeviceDetailViewModel?> GetDeviceByIdAsync(int id, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1
                d.[ID],
                d.[DeviceName],
                d.[DeviceCode],
                d.[VesselName],
                d.[TenantID],
                t.[TenantName],
                d.[KITID],
                d.[Availability],
                d.[UsageData],
                d.[PriorityData],
                d.[OverageData],
                d.[Latitude],
                d.[Longitude],
                d.[SystemType],
                d.[KITNumber],
                d.[ServiceLine],
                d.[LastUpdateTime],
                d.[TokenExpiredTime],
                d.[LastSysnTime]
            FROM [TblDevices] d
            LEFT JOIN [TblTenant] t ON t.[ID] = d.[TenantID]
            WHERE d.[ID] = @id
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var hasPlanNameColumn = await HasPlanNameColumnAsync(connection, cancellationToken);

        var deviceQuery = hasPlanNameColumn
            ? query.Replace("d.[ServiceLine],", "d.[ServiceLine],\n                d.[PlanName],")
            : query;

        await using var command = new SqlCommand(deviceQuery, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceDetailViewModel
        {
            Id = reader["ID"] as int? ?? 0,
            DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
            DeviceCode = reader["DeviceCode"]?.ToString() ?? string.Empty,
            VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
            TenantId = reader["TenantID"] as int?,
            TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
            KitId = reader["KITID"]?.ToString() ?? string.Empty,
            Availability = reader["Availability"]?.ToString() ?? string.Empty,
            SubscriptionUsageGb = reader["UsageData"] as decimal?,
            SubscriptionLimitGb = reader["PriorityData"] as decimal?,
            PriorityOverageGb = reader["OverageData"] as decimal?,
            Latitude = reader["Latitude"]?.ToString() ?? string.Empty,
            Longitude = reader["Longitude"]?.ToString() ?? string.Empty,
            SystemType = reader["SystemType"]?.ToString() ?? string.Empty,
            KitNumber = reader["KITNumber"]?.ToString() ?? string.Empty,
            ServiceLine = reader["ServiceLine"]?.ToString() ?? string.Empty,
            PlanName = hasPlanNameColumn ? reader["PlanName"]?.ToString() ?? string.Empty : string.Empty,
            LastUpdateTime = reader["LastUpdateTime"] as DateTime?,
            TokenExpiredTime = reader["TokenExpiredTime"] as DateTime?,
            LastSysnTime = reader["LastSysnTime"] as DateTime?
        };
    }

    public async Task<DeviceDetailViewModel?> GetDeviceDetailAsync(int id, int? userId = null, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        await RefreshDeviceInternalAsync(id, onlyIfTokenExpired: false, cancellationToken: cancellationToken, userId: userId, auditRefreshWhenTokenRenewed: true);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken, allowedTenantId: tenantId);
        if (device is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(device.DeviceCode) && !string.IsNullOrWhiteSpace(device.TokenString))
        {
            var usage = await RequestTerminalUsageAsync(device.DeviceCode, device.TokenString, cancellationToken);
            if (usage.Success)
            {
                device.SubscriptionUsageGb = usage.SubscriptionUsageGb;
                device.SubscriptionLimitGb = usage.SubscriptionLimitGb;
                device.PriorityOverageGb = usage.PriorityOverageGb;
                device.PriorityOverageLimitGb = usage.PriorityOverageLimitGb;
                device.PlanName = usage.PlanName;

                await UpdateDeviceUsageSummaryAsync(
                    connection,
                    id,
                    usage.SubscriptionUsageGb,
                    usage.SubscriptionLimitGb,
                    usage.PriorityOverageGb,
                    usage.PlanName,
                    cancellationToken);
            }
        }

        return device;
    }

    public async Task<TelemetryTimelineResult> GetTelemetryTimelineAsync(int id, long start, long end, string metric, CancellationToken cancellationToken = default)
    {
        metric = string.IsNullOrWhiteSpace(metric) ? "uplink_throughput" : metric.Trim();
        if (start <= 0 || end <= 0 || start >= end)
        {
            return new TelemetryTimelineResult
            {
                Success = false,
                Message = "KhoÃ¡ÂºÂ£ng thÃ¡Â»Âi gian khÃƒÂ´ng hÃ¡Â»Â£p lÃ¡Â»â€¡",
                MessageEn = "Invalid time range",
                Metric = metric,
                Start = start,
                End = end
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken);
        if (device is null)
        {
            return new TelemetryTimelineResult
            {
                Success = false,
                Message = "KhÃƒÂ´ng tÃƒÂ¬m thÃ¡ÂºÂ¥y thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹",
                MessageEn = "Device not found",
                Metric = metric,
                Start = start,
                End = end
            };
        }

        var accessToken = device.TokenString;
        if (string.IsNullOrWhiteSpace(accessToken) || IsTokenExpired(device.TokenExpiredTime))
        {
            var settings = await GetApiCredentialsAsync(connection, null, cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
            {
                return new TelemetryTimelineResult
                {
                    Success = false,
                    Message = "ThiÃ¡ÂºÂ¿u client_id hoÃ¡ÂºÂ·c client_secret trong TblSettings",
                    MessageEn = "Missing client_id or client_secret in TblSettings",
                    TerminalId = device.DeviceCode,
                    Metric = metric,
                    Start = start,
                    End = end
                };
            }

            var tokenCall = await RequestDeviceTokenAsync(settings.ClientId, settings.ClientSecret, device.DeviceCode, cancellationToken);
            if (!tokenCall.Success)
            {
                return new TelemetryTimelineResult
                {
                    Success = false,
                    Message = tokenCall.Message,
                    MessageEn = tokenCall.MessageEn,
                    RawResponse = tokenCall.RawResponse,
                    TerminalId = device.DeviceCode,
                    Metric = metric,
                    Start = start,
                    End = end
                };
            }

            accessToken = tokenCall.AccessToken ?? string.Empty;
            await UpdateDeviceTokenAsync(connection, id, accessToken, tokenCall.ExpiredTime, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new TelemetryTimelineResult
            {
                Success = false,
                Message = "KhÃƒÂ´ng nhÃ¡ÂºÂ­n Ã„â€˜Ã†Â°Ã¡Â»Â£c access token hÃ¡Â»Â£p lÃ¡Â»â€¡",
                MessageEn = "No valid access token was available",
                TerminalId = device.DeviceCode,
                Metric = metric,
                Start = start,
                End = end
            };
        }

        return await RequestTelemetryTimelineAsync(device.DeviceCode, accessToken, start, end, metric, cancellationToken);
    }

    public async Task<DeviceWifiResult> GetDeviceWifiAsync(int id, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken, allowedTenantId: tenantId);
        if (device is null)
        {
            return new DeviceWifiResult
            {
                Success = false,
                ErrorCode = "device_not_found",
                Message = "Khong tim thay thiet bi hoac ban khong co quyen truy cap.",
                MessageEn = "The device was not found or you do not have access."
            };
        }

        var terminalId = device.DeviceCode?.Trim() ?? string.Empty;
        var deviceId = device.KitId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return new DeviceWifiResult
            {
                Success = false,
                ErrorCode = "missing_wifi_identifiers",
                Message = "Thieu KITID hoac KITNumber de lay thong tin WiFi.",
                MessageEn = "KITID or KITNumber is missing for the WiFi request.",
                TerminalId = terminalId,
                DeviceId = deviceId
            };
        }

        var accessToken = device.TokenString;
        if (string.IsNullOrWhiteSpace(accessToken) || IsTokenExpired(device.TokenExpiredTime))
        {
            var settings = await GetApiCredentialsAsync(connection, null, cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
            {
                return new DeviceWifiResult
                {
                    Success = false,
                    ErrorCode = "missing_api_credentials",
                    Message = "Thieu client_id hoac client_secret trong TblSettings",
                    MessageEn = "Missing client_id or client_secret in TblSettings",
                    TerminalId = terminalId,
                    DeviceId = deviceId
                };
            }

            var tokenCall = await RequestDeviceTokenAsync(settings.ClientId, settings.ClientSecret, terminalId, cancellationToken);
            if (!tokenCall.Success)
            {
                return new DeviceWifiResult
                {
                    Success = false,
                    ErrorCode = tokenCall.ErrorCode,
                    Message = tokenCall.Message,
                    MessageEn = tokenCall.MessageEn,
                    RawResponse = tokenCall.RawResponse,
                    TerminalId = terminalId,
                    DeviceId = deviceId
                };
            }

            accessToken = tokenCall.AccessToken ?? string.Empty;
            await UpdateDeviceTokenAsync(connection, id, accessToken, tokenCall.ExpiredTime, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new DeviceWifiResult
            {
                Success = false,
                ErrorCode = "missing_access_token",
                Message = "Khong co access token hop le de lay thong tin WiFi.",
                MessageEn = "No valid access token was available for the WiFi request.",
                TerminalId = terminalId,
                DeviceId = deviceId
            };
        }

        return await RequestDeviceWifiAsync(terminalId, deviceId, accessToken, cancellationToken);
    }

    public Task<RefreshDeviceResult> RefreshExpiredDeviceAsync(int id, CancellationToken cancellationToken = default)
    {
        return RefreshDeviceInternalAsync(id, onlyIfTokenExpired: false, cancellationToken: cancellationToken);
    }

    public async Task<CreateDeviceResult> CreateDeviceAsync(CreateDeviceRequest request, int? userId, CancellationToken cancellationToken = default)
    {
        request.DeviceName = request.DeviceName.Trim();
        request.DeviceCode = request.DeviceCode.Trim();
        request.VesselName = request.VesselName.Trim();

        if (string.IsNullOrWhiteSpace(request.DeviceName) ||
            string.IsNullOrWhiteSpace(request.DeviceCode) ||
            string.IsNullOrWhiteSpace(request.VesselName) ||
            !request.TenantId.HasValue ||
            request.TenantId.Value <= 0)
        {
            return new CreateDeviceResult
            {
                ErrorCode = "validation_required",
                Message = "TÃƒÂªn thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹, mÃƒÂ£ thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹, tÃƒÂªn tÃƒÂ u vÃƒÂ  tenant lÃƒÂ  bÃ¡ÂºÂ¯t buÃ¡Â»â„¢c",
                MessageEn = "Device name, terminal id, vessel name and tenant are required"
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (await DeviceCodeExistsAsync(connection, transaction, request.DeviceCode, cancellationToken))
        {
            return new CreateDeviceResult
            {
                IsDuplicate = true,
                ErrorCode = "duplicate_kit_code",
                Message = "MÃƒÂ£ KIT Ã„â€˜ÃƒÂ£ trÃƒÂ¹ng",
                MessageEn = "Terminal id already exists"
            };
        }

        var settings = await GetApiCredentialsAsync(connection, transaction, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return new CreateDeviceResult
            {
                ErrorCode = "missing_api_credentials",
                Message = "ThiÃ¡ÂºÂ¿u client_id hoÃ¡ÂºÂ·c client_secret trong TblSettings",
                MessageEn = "Missing client_id or client_secret in TblSettings"
            };
        }

        var tokenCall = await RequestDeviceTokenAsync(settings.ClientId, settings.ClientSecret, request.DeviceCode, cancellationToken);
        if (!tokenCall.Success)
        {
            return new CreateDeviceResult
            {
                ErrorCode = tokenCall.ErrorCode,
                Message = tokenCall.Message,
                MessageEn = tokenCall.MessageEn,
                ApiResult = tokenCall.RawResponse
            };
        }

        var terminalCall = await RequestTerminalDevicesAsync(request.DeviceCode, tokenCall.AccessToken, cancellationToken);
        if (!terminalCall.Success)
        {
            return new CreateDeviceResult
            {
                ErrorCode = terminalCall.ErrorCode,
                Message = terminalCall.Message,
                MessageEn = terminalCall.MessageEn,
                ApiResult = terminalCall.RawResponse
            };
        }

        var combinedApiResult = BuildCombinedApiResult(tokenCall.RawResponse, terminalCall.RawResponse);

        var deviceId = await InsertDeviceAsync(
            connection,
            transaction,
            request,
            tokenCall.AccessToken,
            tokenCall.ExpiredTime,
            terminalCall.KitId,
            terminalCall.Availability,
            cancellationToken);

        await InsertAuditAsync(connection, transaction, userId, deviceId, combinedApiResult, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreateDeviceResult
        {
            Success = true,
            Message = "Th\u00eam thi\u1ebft b\u1ecb th\u00e0nh c\u00f4ng",
            MessageEn = "Device created successfully",
            ApiResult = combinedApiResult,
            DeviceId = deviceId
        };
    }

    public async Task<UpdateDeviceResult> UpdateDeviceAsync(UpdateDeviceRequest request, int? userId, CancellationToken cancellationToken = default)
    {
        request.DeviceName = request.DeviceName.Trim();
        request.DeviceCode = request.DeviceCode.Trim();
        request.VesselName = request.VesselName.Trim();

        if (request.Id <= 0 ||
            string.IsNullOrWhiteSpace(request.DeviceName) ||
            string.IsNullOrWhiteSpace(request.DeviceCode) ||
            string.IsNullOrWhiteSpace(request.VesselName) ||
            !request.TenantId.HasValue ||
            request.TenantId.Value <= 0)
        {
            return new UpdateDeviceResult
            {
                ErrorCode = "validation_required",
                Message = "TÃƒÂªn thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹, mÃƒÂ£ thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹, tÃƒÂªn tÃƒÂ u vÃƒÂ  tenant lÃƒÂ  bÃ¡ÂºÂ¯t buÃ¡Â»â„¢c",
                MessageEn = "Device name, terminal id, vessel name and tenant are required"
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var existingDevice = await GetDeviceByIdInternalAsync(connection, request.Id, cancellationToken, transaction);
        if (existingDevice is null)
        {
            return new UpdateDeviceResult
            {
                ErrorCode = "device_not_found",
                Message = "KhÃƒÂ´ng tÃƒÂ¬m thÃ¡ÂºÂ¥y thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹",
                MessageEn = "Device not found"
            };
        }

        var deviceCodeChanged = !string.Equals(existingDevice.DeviceCode, request.DeviceCode, StringComparison.OrdinalIgnoreCase);
        if (await DeviceCodeExistsAsync(connection, transaction, request.DeviceCode, cancellationToken, request.Id))
        {
            return new UpdateDeviceResult
            {
                IsDuplicate = true,
                ErrorCode = "duplicate_kit_code",
                Message = "MÃƒÂ£ KIT Ã„â€˜ÃƒÂ£ trÃƒÂ¹ng",
                MessageEn = "Terminal id already exists"
            };
        }

        string combinedApiResult = string.Empty;
        string? accessToken = null;
        DateTime? tokenExpiredTime = null;
        string? kitId = null;
        string availability = existingDevice.Availability;

        if (deviceCodeChanged)
        {
            var settings = await GetApiCredentialsAsync(connection, transaction, cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
            {
                return new UpdateDeviceResult
                {
                    ErrorCode = "missing_api_credentials",
                    Message = "ThiÃ¡ÂºÂ¿u client_id hoÃ¡ÂºÂ·c client_secret trong TblSettings",
                    MessageEn = "Missing client_id or client_secret in TblSettings"
                };
            }

            var tokenCall = await RequestDeviceTokenAsync(settings.ClientId, settings.ClientSecret, request.DeviceCode, cancellationToken);
            if (!tokenCall.Success)
            {
                return new UpdateDeviceResult
                {
                    ErrorCode = tokenCall.ErrorCode,
                    Message = tokenCall.Message,
                    MessageEn = tokenCall.MessageEn,
                    ApiResult = tokenCall.RawResponse
                };
            }

            var terminalCall = await RequestTerminalDevicesAsync(request.DeviceCode, tokenCall.AccessToken, cancellationToken);
            if (!terminalCall.Success)
            {
                return new UpdateDeviceResult
                {
                    ErrorCode = terminalCall.ErrorCode,
                    Message = terminalCall.Message,
                    MessageEn = terminalCall.MessageEn,
                    ApiResult = terminalCall.RawResponse
                };
            }

            accessToken = tokenCall.AccessToken;
            tokenExpiredTime = tokenCall.ExpiredTime;
            kitId = terminalCall.KitId;
            availability = terminalCall.Availability;
            combinedApiResult = BuildCombinedApiResult(tokenCall.RawResponse, terminalCall.RawResponse);
        }

        await UpdateDeviceRecordAsync(
            connection,
            transaction,
            request,
            accessToken,
            tokenExpiredTime,
            availability,
            kitId,
            deviceCodeChanged,
            cancellationToken);

        var auditDetail = BuildUpdateAuditDetail(existingDevice, request, deviceCodeChanged, combinedApiResult);
        await InsertAuditAsync(connection, transaction, userId, request.Id, auditDetail, UpdateDeviceAuditAction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new UpdateDeviceResult
        {
            Success = true,
            Message = "C\u1eadp nh\u1eadt thi\u1ebft b\u1ecb th\u00e0nh c\u00f4ng",
            MessageEn = "Device updated successfully",
            ApiResult = combinedApiResult,
            DeviceId = request.Id
        };
    }

    public async Task<DeleteDeviceResult> DeleteDeviceAsync(int id, int? userId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var device = await GetDeviceRecordForDeleteAsync(connection, transaction, id, cancellationToken);
        if (device is null)
        {
            return new DeleteDeviceResult
            {
                ErrorCode = "device_not_found",
                Message = "KhÃƒÂ´ng tÃƒÂ¬m thÃ¡ÂºÂ¥y thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹",
                MessageEn = "Device not found"
            };
        }

        const string deleteQuery = "DELETE FROM [TblDevices] WHERE [ID] = @id";
        await using (var deleteCommand = new SqlCommand(deleteQuery, connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("@id", id);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            userId,
            id,
            $"xÃƒÂ³a mÃƒÂ£ thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹ Ã„â€˜Ã¡ÂºÂ§u cuÃ¡Â»â€˜i {device.DeviceCode}",
            "delete_KIT",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new DeleteDeviceResult
        {
            Success = true,
            Message = "X\u00f3a KIT th\u00e0nh c\u00f4ng",
            MessageEn = "Terminal id deleted successfully"
        };
    }

    private async Task<bool> DeviceCodeExistsAsync(SqlConnection connection, SqlTransaction transaction, string deviceCode, CancellationToken cancellationToken, int? excludeId = null)
    {
        var query = "SELECT COUNT(1) FROM [TblDevices] WHERE [DeviceCode] = @deviceCode";
        if (excludeId.HasValue)
        {
            query += " AND [ID] <> @excludeId";
        }

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.AddWithValue("@deviceCode", deviceCode);
        if (excludeId.HasValue)
        {
            command.Parameters.AddWithValue("@excludeId", excludeId.Value);
        }
        var count = (int)await command.ExecuteScalarAsync(cancellationToken);
        return count > 0;
    }

    private async Task<(string ClientId, string ClientSecret)> GetApiCredentialsAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT [SettingCode], [SettingValue]
            FROM [TblSettings]
            WHERE [SettingCode] IN ('client_id', 'client_secret')
            """;

        string clientId = string.Empty;
        string clientSecret = string.Empty;

        await using var command = new SqlCommand(query, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader["SettingCode"]?.ToString();
            var value = reader["SettingValue"]?.ToString() ?? string.Empty;

            if (string.Equals(code, "client_id", StringComparison.OrdinalIgnoreCase))
            {
                clientId = value;
            }

            if (string.Equals(code, "client_secret", StringComparison.OrdinalIgnoreCase))
            {
                clientSecret = value;
            }
        }

        return (clientId, clientSecret);
    }

    private async Task<int> InsertDeviceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CreateDeviceRequest request,
        string? accessToken,
        DateTime? tokenExpiredTime,
        string? kitId,
        string availability,
        CancellationToken cancellationToken)
    {
        const string query = """
            INSERT INTO [TblDevices]
                ([DeviceName], [DeviceCode], [VesselName], [TenantID], [TokenString], [TokenExpiredTime], [Availability], [LastUpdateTime], [KITID], [LastSysnTime])
            VALUES
                (@deviceName, @deviceCode, @vesselName, @tenantId, @tokenString, @tokenExpiredTime, @availability, GETUTCDATE(), @kitId, NULL);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.AddWithValue("@deviceName", request.DeviceName);
        command.Parameters.AddWithValue("@deviceCode", request.DeviceCode);
        command.Parameters.AddWithValue("@vesselName", request.VesselName);
        command.Parameters.AddWithValue("@tenantId", (object?)request.TenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("@tokenString", (object?)accessToken ?? DBNull.Value);
        command.Parameters.AddWithValue("@tokenExpiredTime", (object?)tokenExpiredTime ?? DBNull.Value);
        command.Parameters.AddWithValue("@availability", string.IsNullOrWhiteSpace(availability) ? "unknown" : availability);
        command.Parameters.AddWithValue("@kitId", (object?)kitId ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int deviceId ? deviceId : 0;
    }

    private async Task UpdateDeviceRecordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        UpdateDeviceRequest request,
        string? accessToken,
        DateTime? tokenExpiredTime,
        string availability,
        string? kitId,
        bool deviceCodeChanged,
        CancellationToken cancellationToken)
    {
        var query = """
            UPDATE [TblDevices]
            SET
                [DeviceName] = @deviceName,
                [DeviceCode] = @deviceCode,
                [VesselName] = @vesselName,
                [TenantID] = @tenantId
            """;

        if (deviceCodeChanged)
        {
            query += """

                ,
                [TokenString] = @tokenString,
                [TokenExpiredTime] = @tokenExpiredTime,
                [Availability] = @availability,
                [KITID] = @kitId,
                [LastUpdateTime] = GETUTCDATE()
                """;
        }

        query += "\nWHERE [ID] = @id";

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.AddWithValue("@id", request.Id);
        command.Parameters.AddWithValue("@deviceName", request.DeviceName);
        command.Parameters.AddWithValue("@deviceCode", request.DeviceCode);
        command.Parameters.AddWithValue("@vesselName", request.VesselName);
        command.Parameters.AddWithValue("@tenantId", (object?)request.TenantId ?? DBNull.Value);

        if (deviceCodeChanged)
        {
            command.Parameters.AddWithValue("@tokenString", (object?)accessToken ?? DBNull.Value);
            command.Parameters.AddWithValue("@tokenExpiredTime", (object?)tokenExpiredTime ?? DBNull.Value);
            command.Parameters.AddWithValue("@availability", string.IsNullOrWhiteSpace(availability) ? "unknown" : availability);
            command.Parameters.AddWithValue("@kitId", (object?)kitId ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildUpdateAuditDetail(DeviceDetailViewModel existingDevice, UpdateDeviceRequest request, bool deviceCodeChanged, string apiResult)
    {
        var changes = new List<string>();

        if (!string.Equals(existingDevice.DeviceName, request.DeviceName, StringComparison.Ordinal))
        {
            changes.Add("DeviceName");
        }

        if (!string.Equals(existingDevice.DeviceCode, request.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("DeviceCode");
        }

        if (!string.Equals(existingDevice.VesselName, request.VesselName, StringComparison.Ordinal))
        {
            changes.Add("VesselName");
        }

        if (existingDevice.TenantId != request.TenantId)
        {
            changes.Add("TenantID");
        }

        if (deviceCodeChanged)
        {
            changes.Add("Token/KIT sync");
        }

        var detail = changes.Count == 0
            ? $"updated device {request.DeviceCode} with no field changes"
            : $"updated device {request.DeviceCode}; changed: {string.Join(", ", changes)}";

        return string.IsNullOrWhiteSpace(apiResult)
            ? detail
            : $"{detail}{Environment.NewLine}{Environment.NewLine}{apiResult}";
    }

    private async Task<DeviceDetailViewModel?> GetDeviceByIdInternalAsync(
        SqlConnection connection,
        int id,
        CancellationToken cancellationToken,
        SqlTransaction? transaction = null,
        int? allowedTenantId = null)
    {
        const string query = """
            SELECT TOP 1
                d.[ID],
                d.[DeviceName],
                d.[DeviceCode],
                d.[VesselName],
                d.[TenantID],
                t.[TenantName],
                d.[TokenString],
                d.[KITID],
                d.[Availability],
                d.[UsageData],
                d.[PriorityData],
                d.[OverageData],
                d.[Latitude],
                d.[Longitude],
                d.[SystemType],
                d.[KITNumber],
                d.[ServiceLine],
                d.[LastUpdateTime],
                d.[TokenExpiredTime],
                d.[LastSysnTime]
            FROM [TblDevices] d
            LEFT JOIN [TblTenant] t ON t.[ID] = d.[TenantID]
            WHERE d.[ID] = @id
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
            """;

        var hasPlanNameColumn = await HasPlanNameColumnAsync(connection, cancellationToken, transaction);
        var deviceQuery = hasPlanNameColumn
            ? query.Replace("d.[ServiceLine],", "d.[ServiceLine],\n                d.[PlanName],")
            : query;

        await using var command = new SqlCommand(deviceQuery, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceDetailViewModel
        {
            Id = reader["ID"] as int? ?? 0,
            DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
            DeviceCode = reader["DeviceCode"]?.ToString() ?? string.Empty,
            VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
            TenantId = reader["TenantID"] as int?,
            TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
            TokenString = reader["TokenString"]?.ToString() ?? string.Empty,
            KitId = reader["KITID"]?.ToString() ?? string.Empty,
            Availability = reader["Availability"]?.ToString() ?? string.Empty,
            SubscriptionUsageGb = reader["UsageData"] as decimal?,
            SubscriptionLimitGb = reader["PriorityData"] as decimal?,
            PriorityOverageGb = reader["OverageData"] as decimal?,
            Latitude = reader["Latitude"]?.ToString() ?? string.Empty,
            Longitude = reader["Longitude"]?.ToString() ?? string.Empty,
            SystemType = reader["SystemType"]?.ToString() ?? string.Empty,
            KitNumber = reader["KITNumber"]?.ToString() ?? string.Empty,
            ServiceLine = reader["ServiceLine"]?.ToString() ?? string.Empty,
            PlanName = hasPlanNameColumn ? reader["PlanName"]?.ToString() ?? string.Empty : string.Empty,
            LastUpdateTime = reader["LastUpdateTime"] as DateTime?,
            TokenExpiredTime = reader["TokenExpiredTime"] as DateTime?,
            LastSysnTime = reader["LastSysnTime"] as DateTime?
        };
    }

    private async Task UpdateDeviceStatusAsync(SqlConnection connection, int id, (bool Success, string RawResponse, string Latitude, string Longitude, string Availability, string SystemType, string KitNumber, string ServiceLine, string KitId) status, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [TblDevices]
            SET
                [Latitude] = @latitude,
                [Longitude] = @longitude,
                [Availability] = @availability,
                [SystemType] = @systemType,
                [KITNumber] = @kitNumber,
                [ServiceLine] = @serviceLine,
                [KITID] = CASE WHEN NULLIF(@kitId, '') IS NULL THEN [KITID] ELSE @kitId END,
                [LastUpdateTime] = GETUTCDATE()
            WHERE [ID] = @id
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@latitude", (object?)status.Latitude ?? DBNull.Value);
        command.Parameters.AddWithValue("@longitude", (object?)status.Longitude ?? DBNull.Value);
        command.Parameters.AddWithValue("@availability", status.Availability);
        command.Parameters.AddWithValue("@systemType", (object?)status.SystemType ?? DBNull.Value);
        command.Parameters.AddWithValue("@kitNumber", (object?)status.KitNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("@serviceLine", (object?)status.ServiceLine ?? DBNull.Value);
        command.Parameters.AddWithValue("@kitId", (object?)status.KitId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DeviceListItemViewModel?> GetDeviceRecordForDeleteAsync(SqlConnection connection, SqlTransaction transaction, int id, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 [ID], [DeviceName], [DeviceCode], [KITID], [Availability], [LastUpdateTime], [TokenExpiredTime]
            FROM [TblDevices]
            WHERE [ID] = @id
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceListItemViewModel
        {
            Id = reader["ID"] as int? ?? 0,
            DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
            DeviceCode = reader["DeviceCode"]?.ToString() ?? string.Empty,
            KitId = reader["KITID"]?.ToString() ?? string.Empty,
            Availability = reader["Availability"]?.ToString() ?? string.Empty,
            LastUpdateTime = reader["LastUpdateTime"] as DateTime?,
            TokenExpiredTime = reader["TokenExpiredTime"] as DateTime?
        };
    }

    private async Task<RefreshDeviceResult> RefreshDeviceInternalAsync(int id, bool onlyIfTokenExpired, CancellationToken cancellationToken, int? userId = null, bool auditRefreshWhenTokenRenewed = false)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken);
        if (device is null)
        {
            return new RefreshDeviceResult
            {
                ErrorCode = "device_not_found",
                Message = "KhÃƒÂ´ng tÃƒÂ¬m thÃ¡ÂºÂ¥y thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹",
                MessageEn = "Device not found"
            };
        }

        var tokenExpired = IsTokenExpired(device.TokenExpiredTime);
        var canUseCachedData = IsSyncStillFresh(device.LastSysnTime)
            && !string.IsNullOrWhiteSpace(device.Availability)
            && device.SubscriptionUsageGb.HasValue
            && device.SubscriptionLimitGb.HasValue;

        if (canUseCachedData)
        {
            return MapRefreshResult(device, refreshed: false);
        }

        if (onlyIfTokenExpired && !tokenExpired)
        {
            return MapRefreshResult(device, refreshed: false);
        }

        var accessToken = device.TokenString;
        var tokenExpiredTime = device.TokenExpiredTime;
        string tokenRawResponse = string.Empty;

        if (string.IsNullOrWhiteSpace(accessToken) || tokenExpired)
        {
            var settings = await GetApiCredentialsAsync(connection, null, cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
            {
                return new RefreshDeviceResult
                {
                    ErrorCode = "missing_api_credentials",
                    Message = "ThiÃ¡ÂºÂ¿u client_id hoÃ¡ÂºÂ·c client_secret trong TblSettings",
                    MessageEn = "Missing client_id or client_secret in TblSettings"
                };
            }

            var tokenCall = await RequestDeviceTokenAsync(settings.ClientId, settings.ClientSecret, device.DeviceCode, cancellationToken);
            if (!tokenCall.Success)
            {
                return new RefreshDeviceResult
                {
                    ErrorCode = tokenCall.ErrorCode,
                    Message = tokenCall.Message,
                    MessageEn = tokenCall.MessageEn,
                    ApiResult = tokenCall.RawResponse
                };
            }

            accessToken = tokenCall.AccessToken;
            tokenExpiredTime = tokenCall.ExpiredTime;
            tokenRawResponse = tokenCall.RawResponse;
            await UpdateDeviceTokenAsync(connection, id, accessToken, tokenExpiredTime, cancellationToken);

            if (auditRefreshWhenTokenRenewed)
            {
                await InsertAuditAsync(
                    connection,
                    transaction: null,
                    userId,
                    id,
                    $"refresh token khi mÃ¡Â»Å¸ chi tiÃ¡ÂºÂ¿t KIT {device.DeviceCode}",
                    "refresh_token_when_open_detail",
                    cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new RefreshDeviceResult
            {
                ErrorCode = "missing_access_token",
                Message = "KhÃƒÂ´ng nhÃ¡ÂºÂ­n Ã„â€˜Ã†Â°Ã¡Â»Â£c access token hÃ¡Â»Â£p lÃ¡Â»â€¡",
                MessageEn = "No valid access token was available"
            };
        }

        var statusCall = await RequestTerminalStatusAsync(device.DeviceCode, accessToken, cancellationToken);

        if (!statusCall.Success)
        {
            return new RefreshDeviceResult
            {
                ErrorCode = "status_sync_failed",
                Message = "KhÃƒÂ´ng thÃ¡Â»Æ’ Ã„â€˜Ã¡Â»â€œng bÃ¡Â»â„¢ trÃ¡ÂºÂ¡ng thÃƒÂ¡i thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹",
                MessageEn = "The device status could not be synchronized",
                ApiResult = BuildSyncApiResult(tokenRawResponse, statusCall.RawResponse)
            };
        }

        await UpdateDeviceStatusAsync(connection, id, statusCall, cancellationToken);

        var usageCall = await RequestTerminalUsageAsync(device.DeviceCode, accessToken, cancellationToken);
        if (usageCall.Success)
        {
            await UpdateDeviceUsageSummaryAsync(
                connection,
                id,
                usageCall.SubscriptionUsageGb,
                usageCall.SubscriptionLimitGb,
                usageCall.PriorityOverageGb,
                usageCall.PlanName,
                cancellationToken);
        }

        var syncSucceeded = statusCall.Success && usageCall.Success;
        if (syncSucceeded)
        {
            await TouchDeviceSyncTimeAsync(connection, id, cancellationToken);
        }

        device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken) ?? device;

        if (usageCall.Success)
        {
            device.SubscriptionUsageGb = usageCall.SubscriptionUsageGb;
            device.SubscriptionLimitGb = usageCall.SubscriptionLimitGb;
            device.PriorityOverageGb = usageCall.PriorityOverageGb;
            device.PriorityOverageLimitGb = usageCall.PriorityOverageLimitGb;
            device.PlanName = usageCall.PlanName;
        }

        var result = MapRefreshResult(device, refreshed: syncSucceeded);
        result.ApiResult = BuildSyncApiResult(
            tokenRawResponse,
            statusCall.RawResponse,
            usageCall.Success ? usageCall.RawResponse : string.Empty);
        return result;
    }

    private static bool IsTokenExpired(DateTime? tokenExpiredTime)
    {
        if (!tokenExpiredTime.HasValue)
        {
            return true;
        }

        var utcValue = DateTime.SpecifyKind(tokenExpiredTime.Value, DateTimeKind.Utc);
        return utcValue <= DateTime.UtcNow;
    }

    private static bool IsSyncStillFresh(DateTime? lastSyncTime)
    {
        if (!lastSyncTime.HasValue)
        {
            return false;
        }

        var utcValue = DateTime.SpecifyKind(lastSyncTime.Value, DateTimeKind.Utc);
        return utcValue >= DateTime.UtcNow.AddHours(-1);
    }

    private async Task<bool> HasPlanNameColumnAsync(SqlConnection connection, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        if (_hasPlanNameColumn.HasValue)
        {
            return _hasPlanNameColumn.Value;
        }

        const string query = """
            SELECT COUNT(1)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = 'TblDevices' AND COLUMN_NAME = 'PlanName'
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        _hasPlanNameColumn = Convert.ToInt32(result ?? 0) > 0;
        return _hasPlanNameColumn.Value;
    }

    private static string BuildDeviceSearchClause(bool hasPlanNameColumn)
    {
        var searchableColumns = new List<string>
        {
            "ISNULL(d.[DeviceName], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(d.[VesselName], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(t.[TenantName], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(d.[DeviceCode], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(d.[Availability], '') LIKE @searchPattern ESCAPE '\\'"
        };

        if (hasPlanNameColumn)
        {
            searchableColumns.Add("ISNULL(d.[PlanName], '') LIKE @searchPattern ESCAPE '\\'");
        }

        return $"""
            AND (
                @searchTerm IS NULL
                OR {string.Join("\n                OR ", searchableColumns)}
            )
            """;
    }

    private static string? NormalizeSearchTerm(string? searchTerm)
    {
        return string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim();
    }

    private static string? BuildSearchPattern(string? searchTerm)
    {
        return searchTerm is null ? null : $"%{EscapeLikeValue(searchTerm)}%";
    }

    private static string EscapeLikeValue(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .Replace("[", "\\[");
    }

    private static void AddDeviceSearchParameters(SqlCommand command, string? searchTerm, string? searchPattern)
    {
        command.Parameters.Add("@searchTerm", SqlDbType.NVarChar, 4000).Value = (object?)searchTerm ?? DBNull.Value;
        command.Parameters.Add("@searchPattern", SqlDbType.NVarChar, 4000).Value = (object?)searchPattern ?? DBNull.Value;
    }

    private async Task UpdateDeviceTokenAsync(SqlConnection connection, int id, string? accessToken, DateTime? tokenExpiredTime, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [TblDevices]
            SET
                [TokenString] = @tokenString,
                [TokenExpiredTime] = @tokenExpiredTime
            WHERE [ID] = @id
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@tokenString", (object?)accessToken ?? DBNull.Value);
        command.Parameters.AddWithValue("@tokenExpiredTime", (object?)tokenExpiredTime ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TouchDeviceSyncTimeAsync(SqlConnection connection, int id, CancellationToken cancellationToken)
    {
        const string query = "UPDATE [TblDevices] SET [LastSysnTime] = GETUTCDATE() WHERE [ID] = @id";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateDeviceUsageSummaryAsync(
        SqlConnection connection,
        int id,
        decimal? usageData,
        decimal? priorityData,
        decimal? overageData,
        string? planName,
        CancellationToken cancellationToken)
    {
        var hasPlanNameColumn = await HasPlanNameColumnAsync(connection, cancellationToken);
        var query = """
            UPDATE [TblDevices]
            SET
                [UsageData] = @usageData,
                [PriorityData] = @priorityData,
                [OverageData] = @overageData
            """;

        if (hasPlanNameColumn)
        {
            query += ",\n                [PlanName] = @planName";
        }

        query += "\nWHERE [ID] = @id";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@usageData", (object?)usageData ?? DBNull.Value);
        command.Parameters.AddWithValue("@priorityData", (object?)priorityData ?? DBNull.Value);
        command.Parameters.AddWithValue("@overageData", (object?)overageData ?? DBNull.Value);
        if (hasPlanNameColumn)
        {
            command.Parameters.AddWithValue("@planName", (object?)planName ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static RefreshDeviceResult MapRefreshResult(DeviceDetailViewModel device, bool refreshed)
    {
        return new RefreshDeviceResult
        {
            Success = true,
            Refreshed = refreshed,
            DeviceId = device.Id,
            Availability = device.Availability,
            LastUpdateTimeVietnam = device.LastUpdateTimeVietnam,
            TokenExpiredTimeVietnam = device.TokenExpiredTimeVietnam,
            TokenExpiredTimeUtc = device.TokenExpiredTimeUtcIso,
            LastSysnTimeVietnam = device.LastSysnTimeVietnam,
            UsageDataDisplay = device.SubscriptionUsageDisplay,
            PriorityDataDisplay = device.SubscriptionLimitDisplay,
            PlanName = device.PlanName
        };
    }

    private async Task InsertAuditAsync(SqlConnection connection, SqlTransaction? transaction, int? userId, int deviceId, string logDetail, CancellationToken cancellationToken)
    {
        await InsertAuditAsync(connection, transaction, userId, deviceId, logDetail, CreateDeviceAuditAction, cancellationToken);
    }

    private async Task InsertAuditAsync(SqlConnection connection, SqlTransaction? transaction, int? userId, int deviceId, string logDetail, string logAction, CancellationToken cancellationToken)
    {
        const string query = """
            INSERT INTO [TblAudit]
                ([IDUser], [LogDate], [LogAction], [LogDetail], [IDDevice])
            VALUES
                (@userId, GETDATE(), @logAction, @logDetail, @deviceId)
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.AddWithValue("@userId", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("@logAction", logAction);
        command.Parameters.Add("@logDetail", SqlDbType.NVarChar, -1).Value = logDetail;
        command.Parameters.AddWithValue("@deviceId", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(bool Success, string ErrorCode, string Message, string MessageEn, string RawResponse, string? AccessToken, DateTime? ExpiredTime)> RequestDeviceTokenAsync(
        string clientId,
        string clientSecret,
        string kitCode,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                client_id = clientId,
                client_secret = clientSecret,
                audience = "https://api.mykvh.com",
                grant_type = "jwt_bearer",
                scope = $"asset#{kitCode}"
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("https://mapi.mykvh.com/oauth/token", content, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);

        var isAccessDenied = false;
        try
        {
            using var errorDocument = JsonDocument.Parse(rawResponse);
            if (errorDocument.RootElement.TryGetProperty("error", out var errorElement))
            {
                isAccessDenied = string.Equals(errorElement.GetString(), "access denied", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (
                false,
                isAccessDenied ? "kit_unavailable" : "api_error",
                isAccessDenied ? "KIT khÃƒÂ´ng khÃ¡ÂºÂ£ dÃ¡Â»Â¥ng" : $"API trÃ¡ÂºÂ£ vÃ¡Â»Â lÃ¡Â»â€”i {(int)response.StatusCode}",
                isAccessDenied ? "KIT is unavailable" : $"API returned error {(int)response.StatusCode}",
                rawResponse,
                null,
                null);
        }

        string? accessToken = null;
        DateTime? expiredTime = null;

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;

            if (root.TryGetProperty("access_token", out var accessTokenElement))
            {
                accessToken = accessTokenElement.GetString();
            }

            if (root.TryGetProperty("expires_in", out var expiresInElement) && expiresInElement.TryGetInt32(out var expiresIn))
            {
                expiredTime = DateTime.UtcNow.AddSeconds(expiresIn);
            }
        }
        catch (JsonException)
        {
        }

        return (
            true,
            string.Empty,
            "API thÃƒÂ nh cÃƒÂ´ng",
            "API succeeded",
            rawResponse,
            accessToken,
            expiredTime);
    }

    private async Task<(bool Success, string ErrorCode, string Message, string MessageEn, string RawResponse, string? KitId, string Availability)> RequestTerminalDevicesAsync(
        string kitCode,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return (
                false,
                "missing_access_token",
                "KhÃƒÂ´ng nhÃ¡ÂºÂ­n Ã„â€˜Ã†Â°Ã¡Â»Â£c access token tÃ¡Â»Â« API",
                "No access token was returned from the API",
                string.Empty,
                null,
                string.Empty);
        }

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(kitCode)}/devices");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (
                false,
                "terminal_devices_error",
                $"API danh sÃƒÂ¡ch thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹ trÃ¡ÂºÂ£ vÃ¡Â»Â lÃ¡Â»â€”i {(int)response.StatusCode}",
                $"Terminal devices API returned error {(int)response.StatusCode}",
                rawResponse,
                null,
                string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return (
                    false,
                    "terminal_devices_empty",
                    "API khÃƒÂ´ng trÃ¡ÂºÂ£ vÃ¡Â»Â danh sÃƒÂ¡ch thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹ hÃ¡Â»Â£p lÃ¡Â»â€¡",
                    "The terminal devices API did not return a valid device list",
                    rawResponse,
                    null,
                    string.Empty);
            }

            var firstDevice = document.RootElement[0];
            var kitId = firstDevice.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var availability = firstDevice.TryGetProperty("availability", out var availabilityElement)
                ? availabilityElement.GetString() ?? "unknown"
                : "unknown";

            if (string.IsNullOrWhiteSpace(kitId))
            {
                return (
                    false,
                    "terminal_device_missing_id",
                    "API khÃƒÂ´ng trÃ¡ÂºÂ£ vÃ¡Â»Â KITID hÃ¡Â»Â£p lÃ¡Â»â€¡",
                    "The terminal devices API did not return a valid KITID",
                    rawResponse,
                    null,
                    string.Empty);
            }

            return (
                true,
                string.Empty,
                "API thÃƒÂ nh cÃƒÂ´ng",
                "API succeeded",
                rawResponse,
                kitId,
                availability);
        }
        catch (JsonException)
        {
            return (
                false,
                "terminal_devices_invalid_json",
                "API danh sÃƒÂ¡ch thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹ trÃ¡ÂºÂ£ vÃ¡Â»Â JSON khÃƒÂ´ng hÃ¡Â»Â£p lÃ¡Â»â€¡",
                "The terminal devices API returned invalid JSON",
                rawResponse,
                null,
                string.Empty);
        }
    }

    private async Task<(bool Success, string RawResponse, string Latitude, string Longitude, string Availability, string SystemType, string KitNumber, string ServiceLine, string KitId)> RequestTerminalStatusAsync(
        string kitCode,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(kitCode)}/status");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (false, rawResponse, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;

            var latitude = root.TryGetProperty("latitude", out var latElement) ? latElement.ToString() : string.Empty;
            var longitude = root.TryGetProperty("longitude", out var lngElement) ? lngElement.ToString() : string.Empty;
            var availability = root.TryGetProperty("availability", out var availabilityElement) ? availabilityElement.GetString() ?? "unknown" : "unknown";

            string systemType = string.Empty;
            string kitNumber = string.Empty;
            string serviceLine = string.Empty;
            string kitId = string.Empty;

            if (root.TryGetProperty("system", out var systemElement))
            {
                if (systemElement.TryGetProperty("type", out var typeElement))
                {
                    systemType = typeElement.GetString() ?? string.Empty;
                }

                if (systemElement.TryGetProperty("starlink", out var starlinkElement))
                {
                    if (starlinkElement.TryGetProperty("kit_number", out var kitNumberElement))
                    {
                        kitNumber = kitNumberElement.GetString() ?? string.Empty;
                    }

                    if (starlinkElement.TryGetProperty("service_line_id", out var serviceLineElement))
                    {
                        serviceLine = serviceLineElement.GetString() ?? string.Empty;
                    }

                    if (starlinkElement.TryGetProperty("device_id", out var deviceIdElement))
                    {
                        kitId = deviceIdElement.GetString() ?? string.Empty;
                    }
                }
            }

            return (true, rawResponse, latitude, longitude, availability, systemType, kitNumber, serviceLine, kitId);
        }
        catch (JsonException)
        {
            return (false, rawResponse, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    private async Task<(bool Success, string RawResponse, decimal? SubscriptionUsageGb, decimal? SubscriptionLimitGb, decimal? PriorityOverageGb, decimal? PriorityOverageLimitGb, string PlanName)> RequestTerminalUsageAsync(
        string terminalId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/usage");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (false, rawResponse, null, null, null, null, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;

            decimal totalUsageBytes = 0m;
            decimal slkAllowanceBytes = 0m;
            decimal sloUsageBytes = 0m;
            decimal sloLimitBytes = 0m;
            string planName = string.Empty;

            if (root.TryGetProperty("subscriptions", out var subscriptionsElement) &&
                subscriptionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var subscription in subscriptionsElement.EnumerateArray())
                {
                    if (string.IsNullOrWhiteSpace(planName) &&
                        subscription.TryGetProperty("plan", out var planElement) &&
                        planElement.ValueKind == JsonValueKind.Object &&
                        planElement.TryGetProperty("name", out var planNameElement))
                    {
                        planName = planNameElement.GetString() ?? string.Empty;
                    }

                    if (!subscription.TryGetProperty("usages", out var usagesElement) || usagesElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var usage in usagesElement.EnumerateArray())
                    {
                        var serviceCode = usage.TryGetProperty("service_code", out var serviceCodeElement)
                            ? serviceCodeElement.GetString() ?? string.Empty
                            : string.Empty;

                        var allowanceBytes = usage.TryGetProperty("allowance", out var allowanceElement) &&
                                             TryGetDecimal(allowanceElement, out var parsedAllowance)
                            ? parsedAllowance
                            : 0m;

                        var usageBytes = usage.TryGetProperty("usage", out var usageElement) &&
                                         TryGetDecimal(usageElement, out var parsedUsage)
                            ? parsedUsage
                            : 0m;

                        var overageBytes = usage.TryGetProperty("overage", out var overageElement) &&
                                           TryGetDecimal(overageElement, out var parsedOverage)
                            ? parsedOverage
                            : 0m;

                        totalUsageBytes += usageBytes;

                        if (string.Equals(serviceCode, "SLK", StringComparison.OrdinalIgnoreCase))
                        {
                            slkAllowanceBytes += allowanceBytes;
                            continue;
                        }

                        if (string.Equals(serviceCode, "SLO", StringComparison.OrdinalIgnoreCase))
                        {
                            sloUsageBytes += usageBytes;
                            sloLimitBytes += overageBytes > 0m ? overageBytes : allowanceBytes;
                        }
                    }
                }
            }

            var totalUsageGb = BytesToDecimalGigabytes(totalUsageBytes);
            var totalLimitGb = BytesToDecimalGigabytes(slkAllowanceBytes + sloUsageBytes);
            var priorityOverageGb = BytesToDecimalGigabytes(sloUsageBytes);
            var priorityOverageLimitGb = BytesToDecimalGigabytes(sloLimitBytes);

            return (true, rawResponse, totalUsageGb, totalLimitGb, priorityOverageGb, priorityOverageLimitGb, planName);
        }
        catch (JsonException)
        {
            return (false, rawResponse, null, null, null, null, string.Empty);
        }
    }

    private async Task<DeviceWifiResult> RequestDeviceWifiAsync(
        string terminalId,
        string deviceId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/devices/{Uri.EscapeDataString(deviceId)}/wifi");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new DeviceWifiResult
            {
                Success = false,
                ErrorCode = "wifi_api_error",
                Message = $"API WiFi tra ve loi {(int)response.StatusCode}",
                MessageEn = $"WiFi API returned error {(int)response.StatusCode}",
                RawResponse = rawResponse,
                TerminalId = terminalId,
                DeviceId = deviceId
            };
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;

            return new DeviceWifiResult
            {
                Success = true,
                RawResponse = rawResponse,
                TerminalId = terminalId,
                DeviceId = deviceId,
                Ssid = FindJsonStringValue(root, "ssid", "wifiSsid", "wiFiSsid", "networkName", "name"),
                Password = FindJsonStringValue(root, "password", "passphrase", "wifiPassword", "wiFiPassword", "psk")
            };
        }
        catch (JsonException)
        {
            return new DeviceWifiResult
            {
                Success = false,
                ErrorCode = "wifi_invalid_json",
                Message = "API WiFi tra ve JSON khong hop le.",
                MessageEn = "WiFi API returned invalid JSON.",
                RawResponse = rawResponse,
                TerminalId = terminalId,
                DeviceId = deviceId
            };
        }
    }

    private async Task<TelemetryTimelineResult> RequestTelemetryTimelineAsync(
        string terminalId,
        string accessToken,
        long start,
        long end,
        string metric,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        var requestUri = $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/telemetry/timeline?start={start}&end={end}&metric={Uri.EscapeDataString(metric)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new TelemetryTimelineResult
            {
                Success = false,
                Message = $"Telemetry API trÃ¡ÂºÂ£ vÃ¡Â»Â lÃ¡Â»â€”i {(int)response.StatusCode}",
                MessageEn = $"Telemetry API returned error {(int)response.StatusCode}",
                RawResponse = rawResponse,
                TerminalId = terminalId,
                Metric = metric,
                Unit = string.Empty,
                Start = start,
                End = end
            };
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;
            var points = ExtractTelemetryPoints(root);

            return new TelemetryTimelineResult
            {
                Success = true,
                RawResponse = rawResponse,
                TerminalId = terminalId,
                Metric = metric,
                Unit = root.TryGetProperty("unit", out var unitElement) ? unitElement.GetString() ?? string.Empty : string.Empty,
                Start = start,
                End = end,
                Points = points
            };
        }
        catch (JsonException)
        {
            return new TelemetryTimelineResult
            {
                Success = false,
                Message = "DÃ¡Â»Â¯ liÃ¡Â»â€¡u telemetry khÃƒÂ´ng hÃ¡Â»Â£p lÃ¡Â»â€¡",
                MessageEn = "Telemetry response JSON is invalid",
                RawResponse = rawResponse,
                TerminalId = terminalId,
                Metric = metric,
                Unit = string.Empty,
                Start = start,
                End = end
            };
        }
    }

    private static List<TelemetryTimelinePoint> ExtractTelemetryPoints(JsonElement root)
    {
        var points = new List<TelemetryTimelinePoint>();

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("fixed_parts", out var fixedPartsElement) &&
            fixedPartsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in fixedPartsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                using var enumerator = item.EnumerateArray();
                if (!enumerator.MoveNext())
                {
                    continue;
                }

                var timestampElement = enumerator.Current;
                if (!enumerator.MoveNext())
                {
                    continue;
                }

                var valueElement = enumerator.Current;
                if (!TryGetUnixSeconds(timestampElement, out var timestamp) || !TryGetDecimal(valueElement, out var value))
                {
                    continue;
                }

                points.Add(new TelemetryTimelinePoint
                {
                    Timestamp = timestamp,
                    Value = value
                });
            }
        }

        CollectTelemetryPoints(root, points);
        return points
            .Where(point => point.Timestamp > 0)
            .OrderBy(point => point.Timestamp)
            .GroupBy(point => point.Timestamp)
            .Select(group => group.Last())
            .ToList();
    }

    private static void CollectTelemetryPoints(JsonElement element, List<TelemetryTimelinePoint> points)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectTelemetryPoints(child, points);
                }
                break;

            case JsonValueKind.Object:
                if (TryParseTelemetryPoint(element, out var point))
                {
                    points.Add(point);
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectTelemetryPoints(property.Value, points);
                }
                break;
        }
    }

    private static bool TryParseTelemetryPoint(JsonElement element, out TelemetryTimelinePoint point)
    {
        point = new TelemetryTimelinePoint();

        if (!TryGetTimestamp(element, out var timestamp))
        {
            return false;
        }

        if (!TryGetMetricValue(element, out var value))
        {
            return false;
        }

        point.Timestamp = timestamp;
        point.Value = value;
        return true;
    }

    private static bool TryGetTimestamp(JsonElement element, out long timestamp)
    {
        timestamp = 0;
        string[] candidateNames = ["timestamp", "time", "ts", "t", "start", "end"];
        foreach (var name in candidateNames)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (TryGetUnixSeconds(property, out timestamp))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetMetricValue(JsonElement element, out decimal value)
    {
        value = 0m;
        string[] candidateNames = ["value", "avg", "average", "mean", "sum", "max", "min", "uplink_throughput", "throughput"];
        foreach (var name in candidateNames)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (TryGetDecimal(property, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetUnixSeconds(JsonElement element, out long unixSeconds)
    {
        unixSeconds = 0;

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var numericValue))
                {
                    return false;
                }

                unixSeconds = numericValue > 10_000_000_000L ? numericValue / 1000L : numericValue;
                return true;

            case JsonValueKind.String:
                var raw = element.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                if (long.TryParse(raw, out var parsedLong))
                {
                    unixSeconds = parsedLong > 10_000_000_000L ? parsedLong / 1000L : parsedLong;
                    return true;
                }

                if (DateTimeOffset.TryParse(raw, out var parsedDateTime))
                {
                    unixSeconds = parsedDateTime.ToUnixTimeSeconds();
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static string FindJsonStringValue(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (element.TryGetProperty(propertyName, out var propertyValue))
                {
                    var value = GetJsonScalarString(propertyValue);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var value = FindJsonStringValue(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = FindJsonStringValue(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    private static string GetJsonScalarString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => string.Empty
        };
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        value = 0m;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static decimal? BytesToDecimalGigabytes(decimal bytes)
    {
        return bytes > 0m ? Math.Round(bytes / 1_000_000_000m, 2) : 0m;
    }

    private static string BuildCombinedApiResult(string tokenRawResponse, string devicesRawResponse)
    {
        try
        {
            using var tokenDocument = JsonDocument.Parse(tokenRawResponse);
            using var devicesDocument = JsonDocument.Parse(devicesRawResponse);

            return JsonSerializer.Serialize(new
            {
                token = tokenDocument.RootElement.Clone(),
                devices = devicesDocument.RootElement.Clone()
            }, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (JsonException)
        {
            return $"TOKEN RESPONSE:{Environment.NewLine}{tokenRawResponse}{Environment.NewLine}{Environment.NewLine}DEVICES RESPONSE:{Environment.NewLine}{devicesRawResponse}";
        }
    }

    private static string BuildSyncApiResult(string tokenRawResponse, string statusRawResponse, string usageRawResponse = "")
    {
        if (string.IsNullOrWhiteSpace(tokenRawResponse) && string.IsNullOrWhiteSpace(usageRawResponse))
        {
            return statusRawResponse;
        }

        try
        {
            using var statusDocument = JsonDocument.Parse(statusRawResponse);
            using var tokenDocument = string.IsNullOrWhiteSpace(tokenRawResponse) ? null : JsonDocument.Parse(tokenRawResponse);
            using var usageDocument = string.IsNullOrWhiteSpace(usageRawResponse) ? null : JsonDocument.Parse(usageRawResponse);

            return JsonSerializer.Serialize(new
            {
                token = tokenDocument?.RootElement.Clone(),
                status = statusDocument.RootElement.Clone(),
                usage = usageDocument?.RootElement.Clone()
            }, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (JsonException)
        {
            return $"TOKEN RESPONSE:{Environment.NewLine}{tokenRawResponse}{Environment.NewLine}{Environment.NewLine}STATUS RESPONSE:{Environment.NewLine}{statusRawResponse}{Environment.NewLine}{Environment.NewLine}USAGE RESPONSE:{Environment.NewLine}{usageRawResponse}";
        }
    }
}
