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
    private const string SaveDevicePlanAuditAction = "saved_device_pricing";
    private const string DeviceDataOptInHistorySchemaSql = "IF OBJECT_ID(N'[dbo].[TblDeviceDataOptInHistory]', N'U') IS NULL CREATE TABLE [dbo].[TblDeviceDataOptInHistory](" +
        "[ID] int IDENTITY(1,1) NOT NULL PRIMARY KEY,[DeviceId] int NOT NULL,[UserId] int NULL,[PerformedBy] nvarchar(250) NOT NULL," +
        "[PerformedAtUtc] datetime2 NOT NULL,[OldStatus] bit NULL,[NewStatus] bit NOT NULL,[ApiSuccess] bit NOT NULL," +
        "[HttpStatusCode] int NULL,[ApiResponse] nvarchar(max) NULL,[JobId] nvarchar(200) NULL);";

    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");
    private bool? _hasPlanNameColumn;

    public async Task<DevicePageResult> GetDevicesAsync(int page, int pageSize, string? searchTerm = null, int? tenantId = null, int? deviceId = null, bool stockOnly = false, CancellationToken cancellationToken = default)
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
        var planDataLimitSelect = hasPlanNameColumn
            ? "COALESCE(planLimit.[BaseData], d.[PriorityData]) AS [PlanDataLimit],"
            : "d.[PriorityData] AS [PlanDataLimit],";
        var planDataLimitApply = hasPlanNameColumn
            ? """
            OUTER APPLY (
                SELECT TOP 1 pp.[BaseData]
                FROM [dbo].[TblPricingPlan] pp
                WHERE pp.[PlanName] = d.[PlanName]
                   OR pp.[PlanCode] = d.[PlanName]
                ORDER BY CASE WHEN pp.[PlanName] = d.[PlanName] THEN 0 ELSE 1 END, pp.[ID]
            ) planLimit
            """
            : string.Empty;
        var countQuery = $"""
            SELECT COUNT(1)
            FROM [TblDevices] d
            LEFT JOIN [TblTenant] t ON t.[ID] = d.[TenantID]
            WHERE (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
              AND (
                    (@stockOnly = 1 AND NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NULL)
                    OR (@stockOnly = 0 AND NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NOT NULL)
                  )
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
                {planDataLimitSelect}
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
            {planDataLimitApply}
            WHERE (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
              AND (
                    (@stockOnly = 1 AND NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NULL)
                    OR (@stockOnly = 0 AND NULLIF(LTRIM(RTRIM(ISNULL(d.[DeviceCode], ''))), '') IS NOT NULL)
                  )
            {searchClause}
            ORDER BY d.[LastUpdateTime] DESC, d.[DeviceName] ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        await using var countCommand = new SqlCommand(countQuery, connection);
        countCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        countCommand.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        countCommand.Parameters.Add("@stockOnly", SqlDbType.Bit).Value = stockOnly;
        AddDeviceSearchParameters(countCommand, normalizedSearchTerm, searchPattern);
        var totalResult = await countCommand.ExecuteScalarAsync(cancellationToken);
        var totalDevices = totalResult is int total ? total : Convert.ToInt32(totalResult ?? 0);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        command.Parameters.Add("@stockOnly", SqlDbType.Bit).Value = stockOnly;
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
                PriorityData = reader["PlanDataLimit"] as decimal?,
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

    public async Task<DeviceDetailViewModel?> GetDeviceByIdAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var hasPlanNameColumn = await HasPlanNameColumnAsync(connection, cancellationToken);
        var planNameSelect = hasPlanNameColumn
            ? "d.[PlanName],"
            : "CAST(NULL AS nvarchar(255)) AS [PlanName],";
        var planDataLimitSelect = hasPlanNameColumn
            ? "COALESCE(planLimit.[BaseData], d.[PriorityData]) AS [PlanDataLimit],"
            : "d.[PriorityData] AS [PlanDataLimit],";
        var planDataLimitApply = hasPlanNameColumn
            ? """
            OUTER APPLY (
                SELECT TOP 1 pp.[BaseData]
                FROM [dbo].[TblPricingPlan] pp
                WHERE pp.[PlanName] = d.[PlanName]
                   OR pp.[PlanCode] = d.[PlanName]
                ORDER BY CASE WHEN pp.[PlanName] = d.[PlanName] THEN 0 ELSE 1 END, pp.[ID]
            ) planLimit
            """
            : string.Empty;
        var deviceQuery = $"""
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
                {planDataLimitSelect}
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
            {planDataLimitApply}
            WHERE d.[ID] = @id
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
            """;
        await using var command = new SqlCommand(deviceQuery, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;

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
            SubscriptionLimitGb = reader["PlanDataLimit"] as decimal?,
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

    public async Task<DeviceDetailViewModel?> GetDeviceDetailAsync(int id, int? userId = null, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken, allowedTenantId: tenantId, allowedDeviceId: deviceId);
        if (device is null)
        {
            return null;
        }

        try
        {
            var refreshResult = await RefreshDeviceInternalAsync(id, onlyIfTokenExpired: false, cancellationToken: cancellationToken, userId: userId, auditRefreshWhenTokenRenewed: true);
            if (refreshResult.Success)
            {
                device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken, allowedTenantId: tenantId, allowedDeviceId: deviceId) ?? device;
            }
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Keep returning the last synchronized database snapshot when the upstream API is unavailable.
        }

        if (!string.IsNullOrWhiteSpace(device.DeviceCode) && !string.IsNullOrWhiteSpace(device.TokenString))
        {
            try
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
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Usage is supplemental for the detail popup; stale database values are preferable to a failed detail load.
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

    public async Task<DeviceWifiResult> GetDeviceWifiAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken, allowedTenantId: tenantId, allowedDeviceId: deviceId);
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
        var routerKitId = device.KitId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(routerKitId))
        {
            return new DeviceWifiResult
            {
                Success = false,
                ErrorCode = "missing_wifi_identifiers",
                Message = "Thieu KITID hoac KITNumber de lay thong tin WiFi.",
                MessageEn = "KITID or KITNumber is missing for the WiFi request.",
                TerminalId = terminalId,
                DeviceId = routerKitId
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
                    DeviceId = routerKitId
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
                    DeviceId = routerKitId
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
                DeviceId = routerKitId
            };
        }

        var routerDevice = await ResolveRouterDeviceIdAsync(terminalId, accessToken, cancellationToken);
        if (!routerDevice.Success)
        {
            return new DeviceWifiResult
            {
                Success = false,
                ErrorCode = routerDevice.ErrorCode,
                Message = routerDevice.Message,
                MessageEn = routerDevice.MessageEn,
                RawResponse = routerDevice.RawResponse,
                TerminalId = terminalId,
                DeviceId = routerKitId
            };
        }

        var wifiResult = await RequestDeviceWifiAsync(terminalId, routerDevice.DeviceId, accessToken, cancellationToken);
        if (!wifiResult.Success &&
            string.Equals(wifiResult.ErrorCode, "wifi_endpoint_not_found", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(routerKitId) &&
            !string.Equals(routerKitId, routerDevice.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            var fallbackResult = await RequestDeviceWifiAsync(terminalId, routerKitId, accessToken, cancellationToken);
            if (fallbackResult.Success)
            {
                return fallbackResult;
            }

            fallbackResult.RawResponse = BuildSyncApiResult(wifiResult.RawResponse, fallbackResult.RawResponse);
            return fallbackResult;
        }

        return wifiResult;
    }

    public async Task<DeviceCommandResult> UpdateDeviceWifiAsync(UpdateDeviceWifiRequest request, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        request.Ssid = request.Ssid.Trim();
        request.Password = request.Password.Trim();
        if (request.Id <= 0 || string.IsNullOrWhiteSpace(request.Ssid) || string.IsNullOrWhiteSpace(request.Password))
        {
            return new DeviceCommandResult
            {
                ErrorCode = "wifi_validation_required",
                Message = "SSID va mat khau WiFi la bat buoc.",
                MessageEn = "SSID and WiFi password are required."
            };
        }

        var context = await GetDeviceCommandContextAsync(request.Id, tenantId, deviceId, useRouterDevice: true, cancellationToken);
        if (!context.Success)
        {
            return context;
        }

        return await RequestUpdateDeviceWifiAsync(context.TerminalId, context.DeviceId, context.AccessToken, request.Ssid, request.Password, request.Enabled, cancellationToken);
    }

    public async Task<DevicePlanManagementResult> GetDevicePlanManagementAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureDevicePricingSchemaAsync(connection, null, cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken, allowedTenantId: tenantId, allowedDeviceId: deviceId);
        if (device is null)
        {
            return new DevicePlanManagementResult
            {
                Success = false,
                ErrorCode = "device_not_found",
                Message = "Không tìm thấy thiết bị hoặc bạn không có quyền truy cập.",
                MessageEn = "The device was not found or you do not have access."
            };
        }

        if (!device.TenantId.HasValue)
        {
            return new DevicePlanManagementResult
            {
                Success = false,
                ErrorCode = "missing_tenant",
                Message = "Thiết bị chưa được gán tenant.",
                MessageEn = "The device has no tenant assigned.",
                DeviceId = device.Id,
                TenantName = device.TenantName
            };
        }

        return new DevicePlanManagementResult
        {
            Success = true,
            DeviceId = device.Id,
            TenantId = device.TenantId,
            TenantName = device.TenantName,
            PlanOptions = await GetDevicePlanOptionsAsync(connection, device.TenantId.Value, cancellationToken),
            DevicePrices = await GetDevicePlanPricesAsync(connection, device.Id, cancellationToken)
        };
    }

    public async Task<SaveDevicePlanResult> SaveDevicePlanAsync(SaveDevicePlanRequest request, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        if (request.DeviceId <= 0 || request.PricingPlanId <= 0)
        {
            return new SaveDevicePlanResult
            {
                Success = false,
                ErrorCode = "validation_required",
                Message = "Vui lòng chọn thiết bị và gói giá.",
                MessageEn = "Please choose a device and pricing plan."
            };
        }

        if (request.FinalPrice < 0 || request.FinalOverChargePrice < 0)
        {
            return new SaveDevicePlanResult
            {
                Success = false,
                ErrorCode = "invalid_price",
                Message = "Đơn giá phải lớn hơn hoặc bằng 0.",
                MessageEn = "Prices must be greater than or equal to 0."
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureDevicePricingSchemaAsync(connection, transaction, cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, request.DeviceId, cancellationToken, transaction, tenantId, deviceId);
        if (device is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new SaveDevicePlanResult
            {
                Success = false,
                ErrorCode = "device_not_found",
                Message = "Không tìm thấy thiết bị hoặc bạn không có quyền truy cập.",
                MessageEn = "The device was not found or you do not have access."
            };
        }

        if (!device.TenantId.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new SaveDevicePlanResult
            {
                Success = false,
                ErrorCode = "missing_tenant",
                Message = "Thiết bị chưa được gán tenant.",
                MessageEn = "The device has no tenant assigned."
            };
        }

        var planOption = await GetDevicePlanOptionAsync(connection, transaction, device.TenantId.Value, request.PricingPlanId, cancellationToken);
        if (planOption is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new SaveDevicePlanResult
            {
                Success = false,
                ErrorCode = "plan_not_found",
                Message = "Gói giá không thuộc tenant quản lý thiết bị.",
                MessageEn = "The pricing plan does not belong to the device tenant."
            };
        }

        request.ResellerPrice = planOption.ResellerPrice;
        request.ResellerOverChargePrice = planOption.ResellerOverChargePrice;
        request.Status = string.IsNullOrWhiteSpace(planOption.Status) ? "active" : planOption.Status;

        var existingId = await GetExistingDevicePlanPriceIdAsync(connection, transaction, request.DeviceId, request.PricingPlanId, cancellationToken);
        var created = !existingId.HasValue;
        if (created)
        {
            const string insertQuery = """
                INSERT INTO [TblDevicePricing]
                    ([DeviceId], [TenantId], [PricingPlanId], [ResellerPrice], [FinalPrice], [ResellerOverChargePrice], [FinalOverChargePrice], [Status], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
                VALUES
                    (@deviceId, @tenantId, @pricingPlanId, @resellerPrice, @finalPrice, @resellerOverChargePrice, @finalOverChargePrice, @status, GETDATE(), @createdBy, GETDATE(), @updatedBy)
                """;
            await using var insertCommand = new SqlCommand(insertQuery, connection, transaction);
            AddDevicePlanPriceParameters(insertCommand, request, device.TenantId.Value);
            insertCommand.Parameters.Add("@createdBy", SqlDbType.NVarChar, 50).Value = username;
            insertCommand.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            const string updateQuery = """
                UPDATE [TblDevicePricing]
                SET
                    [TenantId] = @tenantId,
                    [ResellerPrice] = @resellerPrice,
                    [FinalPrice] = @finalPrice,
                    [ResellerOverChargePrice] = @resellerOverChargePrice,
                    [FinalOverChargePrice] = @finalOverChargePrice,
                    [Status] = @status,
                    [Updated_Date] = GETDATE(),
                    [Updated_By] = @updatedBy
                WHERE [ID] = @id
                """;
            await using var updateCommand = new SqlCommand(updateQuery, connection, transaction);
            updateCommand.Parameters.Add("@id", SqlDbType.Int).Value = existingId.Value;
            AddDevicePlanPriceParameters(updateCommand, request, device.TenantId.Value);
            updateCommand.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 50).Value = username;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, userId, request.DeviceId, $"Saved device pricing for plan '{planOption.PlanCode}' by '{username}'.", SaveDevicePlanAuditAction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var savedPrices = await GetDevicePlanPricesAsync(connection, request.DeviceId, cancellationToken);
        var savedPrice = savedPrices.FirstOrDefault(price => price.PricingPlanId == request.PricingPlanId);
        return new SaveDevicePlanResult
        {
            Success = true,
            Created = created,
            Message = created ? "Thêm gói thiết bị thành công." : "Cập nhật gói thiết bị thành công.",
            MessageEn = created ? "Device plan created successfully." : "Device plan updated successfully.",
            Price = savedPrice
        };
    }

    public async Task<DeleteDevicePlanResult> DeleteDevicePlanAsync(DeleteDevicePlanRequest request, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        if (request.DeviceId <= 0 || request.PricingPlanId <= 0)
        {
            return new DeleteDevicePlanResult
            {
                Success = false,
                ErrorCode = "validation_required",
                Message = "Vui lòng chọn thiết bị và gói giá.",
                MessageEn = "Please choose a device and pricing plan."
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureDevicePricingSchemaAsync(connection, transaction, cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, request.DeviceId, cancellationToken, transaction, tenantId, deviceId);
        if (device is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeleteDevicePlanResult
            {
                Success = false,
                ErrorCode = "device_not_found",
                Message = "Không tìm thấy thiết bị hoặc bạn không có quyền truy cập.",
                MessageEn = "The device was not found or you do not have access."
            };
        }

        if (await DevicePlanHasInvoicesAsync(connection, transaction, request.DeviceId, request.PricingPlanId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeleteDevicePlanResult
            {
                Success = false,
                ErrorCode = "device_plan_has_invoice",
                Message = "Khong the xoa goi cuoc thiet bi da co invoice duoc tao.",
                MessageEn = "Cannot delete a device plan that already has invoices."
            };
        }

        const string query = """
            DELETE FROM [TblDevicePricing]
            WHERE [DeviceId] = @deviceId
              AND [PricingPlanId] = @pricingPlanId
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = request.DeviceId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = request.PricingPlanId;
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);

        if (affectedRows <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeleteDevicePlanResult
            {
                Success = false,
                ErrorCode = "plan_not_found",
                Message = "Gói thiết bị không tồn tại.",
                MessageEn = "The device plan was not found."
            };
        }

        await InsertAuditAsync(connection, transaction, userId, request.DeviceId, $"Deleted device pricing plan ID {request.PricingPlanId} by '{username}'.", SaveDevicePlanAuditAction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DeleteDevicePlanResult
        {
            Success = true,
            Message = "Xóa gói thiết bị thành công.",
            MessageEn = "Device plan deleted successfully."
        };
    }

    public async Task<DeviceCommandResult> RebootDeviceRouterAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        var context = await GetDeviceCommandContextAsync(id, tenantId, deviceId, useRouterDevice: false, cancellationToken);
        if (!context.Success)
        {
            return context;
        }

        return await RequestRebootDeviceAsync(context.TerminalId, context.DeviceId, context.AccessToken, cancellationToken);
    }

    public async Task<DeviceDataOptInManagementResult> GetDeviceDataOptInAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        var context = await GetDeviceCommandContextAsync(id, tenantId, deviceId, useRouterDevice: false, cancellationToken);
        if (!context.Success)
        {
            return new DeviceDataOptInManagementResult
            {
                Success = false,
                ErrorCode = context.ErrorCode,
                Message = context.Message,
                MessageEn = context.MessageEn,
                DeviceId = id,
                TerminalId = context.TerminalId
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureDeviceDataOptInHistorySchemaAsync(connection, null, cancellationToken);
        var history = await GetDeviceDataOptInHistoryAsync(connection, id, cancellationToken);
        var usage = await RequestTerminalUsageAsync(context.TerminalId, context.AccessToken, cancellationToken);
        var currentEnabled = usage.Success ? usage.DataOptInEnabled : null;
        currentEnabled ??= history.FirstOrDefault(item => item.ApiSuccess)?.NewStatus;

        return new DeviceDataOptInManagementResult
        {
            Success = true,
            DeviceId = id,
            TerminalId = context.TerminalId,
            CurrentEnabled = currentEnabled,
            ApiWarning = usage.Success ? string.Empty : usage.RawResponse,
            History = history
        };
    }

    public async Task<DeviceDataOptInChangeResult> UpdateDeviceDataOptInAsync(UpdateDeviceDataOptInRequest request, int? userId, string performedBy, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        var context = await GetDeviceCommandContextAsync(request.Id, tenantId, deviceId, false, cancellationToken);
        return !context.Success
            ? MapDataOptInContextError(request, context)
            : await UpdateDeviceDataOptInCoreAsync(request, userId, performedBy, context, cancellationToken);
    }

    public Task<RefreshDeviceResult> RefreshExpiredDeviceAsync(int id, CancellationToken cancellationToken = default)
    {
        return RefreshDeviceInternalAsync(id, onlyIfTokenExpired: false, cancellationToken: cancellationToken);
    }

    public async Task<StockDeviceSyncResult> SyncStockDevicesAsync(int? tenantId = null, CancellationToken cancellationToken = default)
    {
        var result = new StockDeviceSyncResult { Success = true };
        var stockDevices = new List<(int Id, string DeviceCode)>();

        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            const string query = """
                SELECT [ID], ISNULL([DeviceCode], '') AS [DeviceCode]
                FROM [TblDevices]
                WHERE (@tenantId IS NULL OR [TenantID] = @tenantId)
                  AND [LastSysnTime] IS NULL
                  AND (
                        NULLIF(LTRIM(RTRIM(ISNULL([DeviceCode], ''))), '') IS NULL
                        OR LOWER(ISNULL([Availability], '')) = 'offline'
                      )
                ORDER BY [ID] ASC
                """;

            await using var command = new SqlCommand(query, connection);
            command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stockDevices.Add((reader["ID"] is int id ? id : Convert.ToInt32(reader["ID"]), reader["DeviceCode"]?.ToString() ?? string.Empty));
            }
        }

        result.TotalStockDevices = stockDevices.Count;
        foreach (var stockDevice in stockDevices)
        {
            if (string.IsNullOrWhiteSpace(stockDevice.DeviceCode))
            {
                result.SkippedNoTerminalCount++;
                continue;
            }

            var refresh = await RefreshDeviceInternalAsync(stockDevice.Id, onlyIfTokenExpired: false, cancellationToken: cancellationToken);
            if (refresh.Success)
            {
                result.SyncedCount++;
            }
            else
            {
                result.FailedCount++;
                result.Errors.Add($"Device #{stockDevice.Id}: {refresh.MessageEn}");
            }
        }

        result.Message = $"Sync stock hoan tat. Dong bo: {result.SyncedCount}, bo qua chua co SLK: {result.SkippedNoTerminalCount}, loi: {result.FailedCount}.";
        result.MessageEn = $"Stock sync completed. Synced: {result.SyncedCount}, skipped without SLK: {result.SkippedNoTerminalCount}, failed: {result.FailedCount}.";
        return result;
    }

    public async Task<CreateDeviceResult> CreateDeviceAsync(CreateDeviceRequest request, int? userId, CancellationToken cancellationToken = default)
    {
        request.DeviceName = (request.DeviceName ?? string.Empty).Trim();
        request.DeviceCode = (request.DeviceCode ?? string.Empty).Trim();
        request.KitNumber = (request.KitNumber ?? string.Empty).Trim();
        request.VesselName = (request.VesselName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(request.DeviceName) ||
            (string.IsNullOrWhiteSpace(request.DeviceCode) && string.IsNullOrWhiteSpace(request.KitNumber)) ||
            string.IsNullOrWhiteSpace(request.VesselName) ||
            !request.TenantId.HasValue ||
            request.TenantId.Value <= 0)
        {
            return new CreateDeviceResult
            {
                ErrorCode = "validation_required",
                Message = "TÃƒÂªn thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹, mÃƒÂ£ thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹, tÃƒÂªn tÃƒÂ u vÃƒÂ  tenant lÃƒÂ  bÃ¡ÂºÂ¯t buÃ¡Â»â„¢c",
                MessageEn = "Device name, terminal id or KIT Number, vessel name and tenant are required"
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.DeviceCode) && await DeviceCodeExistsAsync(connection, transaction, request.DeviceCode, cancellationToken))
        {
            return new CreateDeviceResult
            {
                IsDuplicate = true,
                ErrorCode = "duplicate_kit_code",
                Message = "MÃƒÂ£ KIT Ã„â€˜ÃƒÂ£ trÃƒÂ¹ng",
                MessageEn = "Terminal id already exists"
            };
        }

        if (string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            var stockDeviceId = await InsertDeviceAsync(
                connection,
                transaction,
                request,
                accessToken: null,
                tokenExpiredTime: null,
                kitId: null,
                availability: "offline",
                cancellationToken);

            await InsertAuditAsync(connection, transaction, userId, stockDeviceId, $"Created stock device KIT '{request.KitNumber}' without terminal id.", cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new CreateDeviceResult
            {
                Success = true,
                Message = "Them thiet bi stock thanh cong",
                MessageEn = "Stock device created successfully",
                DeviceId = stockDeviceId
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

    public async Task<KitTerminalLookupResult> LookupKitTerminalInfoAsync(string terminalId, CancellationToken cancellationToken = default)
    {
        terminalId = (terminalId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return new KitTerminalLookupResult
            {
                Success = false,
                ErrorCode = "validation_required",
                Message = "Terminal ID is required.",
                MessageEn = "Terminal ID is required."
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var settings = await GetApiCredentialsAsync(connection, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return new KitTerminalLookupResult
            {
                Success = false,
                ErrorCode = "missing_api_credentials",
                Message = "Missing client_id or client_secret in TblSettings.",
                MessageEn = "Missing client_id or client_secret in TblSettings.",
                TerminalId = terminalId
            };
        }

        var tokenCall = await RequestDeviceTokenAsync(settings.ClientId, settings.ClientSecret, terminalId, cancellationToken);
        if (!tokenCall.Success)
        {
            return new KitTerminalLookupResult
            {
                Success = false,
                IsRateLimited = IsRateLimitResponse(tokenCall.RawResponse, tokenCall.MessageEn),
                ErrorCode = tokenCall.ErrorCode,
                Message = tokenCall.Message,
                MessageEn = tokenCall.MessageEn,
                TerminalId = terminalId,
                ApiResult = tokenCall.RawResponse
            };
        }

        var terminalCall = await RequestTerminalDevicesAsync(terminalId, tokenCall.AccessToken, cancellationToken);
        if (!terminalCall.Success)
        {
            return new KitTerminalLookupResult
            {
                Success = false,
                IsRateLimited = IsRateLimitResponse(terminalCall.RawResponse, terminalCall.MessageEn),
                ErrorCode = terminalCall.ErrorCode,
                Message = terminalCall.Message,
                MessageEn = terminalCall.MessageEn,
                TerminalId = terminalId,
                ApiResult = BuildCombinedApiResult(tokenCall.RawResponse, terminalCall.RawResponse)
            };
        }

        var statusCall = await RequestTerminalStatusAsync(terminalId, tokenCall.AccessToken ?? string.Empty, cancellationToken);
        if (!statusCall.Success)
        {
            return new KitTerminalLookupResult
            {
                Success = false,
                IsRateLimited = IsRateLimitResponse(statusCall.RawResponse, string.Empty),
                ErrorCode = IsRateLimitResponse(statusCall.RawResponse, string.Empty) ? "rate_limited" : "terminal_status_error",
                Message = IsRateLimitResponse(statusCall.RawResponse, string.Empty) ? "Rate limit. Pending retry." : "Cannot load terminal status.",
                MessageEn = IsRateLimitResponse(statusCall.RawResponse, string.Empty) ? "Rate limit. Pending retry." : "Cannot load terminal status.",
                TerminalId = terminalId,
                KitId = terminalCall.KitId ?? string.Empty,
                ApiResult = BuildCombinedApiResult(tokenCall.RawResponse, terminalCall.RawResponse, statusCall.RawResponse)
            };
        }

        return new KitTerminalLookupResult
        {
            Success = true,
            TerminalId = terminalId,
            KitId = string.IsNullOrWhiteSpace(statusCall.KitId) ? terminalCall.KitId ?? string.Empty : statusCall.KitId,
            KitNumber = statusCall.KitNumber,
            ServiceLine = statusCall.ServiceLine,
            Message = "OK",
            MessageEn = "OK",
            ApiResult = BuildCombinedApiResult(tokenCall.RawResponse, terminalCall.RawResponse, statusCall.RawResponse)
        };
    }

    public async Task<UpdateDeviceResult> UpdateDeviceAsync(UpdateDeviceRequest request, int? userId, CancellationToken cancellationToken = default)
    {
        request.DeviceName = (request.DeviceName ?? string.Empty).Trim();
        request.DeviceCode = (request.DeviceCode ?? string.Empty).Trim();
        request.KitNumber = (request.KitNumber ?? string.Empty).Trim();
        request.VesselName = (request.VesselName ?? string.Empty).Trim();

        if (request.Id <= 0 ||
            string.IsNullOrWhiteSpace(request.DeviceName) ||
            (string.IsNullOrWhiteSpace(request.DeviceCode) && string.IsNullOrWhiteSpace(request.KitNumber)) ||
            string.IsNullOrWhiteSpace(request.VesselName) ||
            !request.TenantId.HasValue ||
            request.TenantId.Value <= 0)
        {
            return new UpdateDeviceResult
            {
                ErrorCode = "validation_required",
                Message = "TÃƒÂªn thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹, mÃƒÂ£ thiÃ¡ÂºÂ¿t bÃ¡Â»â€¹, tÃƒÂªn tÃƒÂ u vÃƒÂ  tenant lÃƒÂ  bÃ¡ÂºÂ¯t buÃ¡Â»â„¢c",
                MessageEn = "Device name, terminal id or KIT Number, vessel name and tenant are required"
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
        if (!string.IsNullOrWhiteSpace(request.DeviceCode) && await DeviceCodeExistsAsync(connection, transaction, request.DeviceCode, cancellationToken, request.Id))
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

        if (deviceCodeChanged && string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            accessToken = null;
            tokenExpiredTime = null;
            kitId = null;
            availability = "offline";
        }
        else if (deviceCodeChanged)
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

        if (await DeviceHasPaidInvoicesAsync(connection, transaction, id, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeleteDeviceResult
            {
                Success = false,
                ErrorCode = "device_has_paid_invoice",
                Message = "Khong the xoa thiet bi da co invoice duoc thanh toan.",
                MessageEn = "Cannot delete a device that has paid invoices."
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

    private static async Task<bool> DeviceHasPaidInvoicesAsync(SqlConnection connection, SqlTransaction transaction, int deviceId, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NULL
               OR OBJECT_ID(N'[dbo].[TblSubscriptionInvoice]', N'U') IS NULL
                SELECT CAST(0 AS bit);
            ELSE
                SELECT CASE WHEN EXISTS (
                    SELECT 1
                    FROM [dbo].[TblMonthlySubscription] s
                    INNER JOIN [dbo].[TblSubscriptionInvoice] i ON i.[SubscriptionId] = s.[ID]
                    WHERE s.[DeviceId] = @deviceId
                      AND (
                            LOWER(ISNULL(i.[Status], N'')) = N'paid'
                            OR ISNULL(i.[PaidAmount], 0) > 0
                          )
                ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is bool value && value;
    }

    private static async Task<bool> DevicePlanHasInvoicesAsync(SqlConnection connection, SqlTransaction transaction, int deviceId, int pricingPlanId, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NULL
               OR OBJECT_ID(N'[dbo].[TblSubscriptionInvoice]', N'U') IS NULL
                SELECT CAST(0 AS bit);
            ELSE
                SELECT CASE WHEN EXISTS (
                    SELECT 1
                    FROM [dbo].[TblMonthlySubscription] s
                    INNER JOIN [dbo].[TblSubscriptionInvoice] i ON i.[SubscriptionId] = s.[ID]
                    WHERE s.[DeviceId] = @deviceId
                      AND s.[PricingPlanId] = @pricingPlanId
                ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = pricingPlanId;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is bool value && value;
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

    private async Task<DeviceDataOptInChangeResult> UpdateDeviceDataOptInCoreAsync(UpdateDeviceDataOptInRequest request, int? userId, string performedBy, DeviceCommandContext context, CancellationToken cancellationToken)
    {
        var usage = await RequestTerminalUsageAsync(context.TerminalId, context.AccessToken, cancellationToken);
        var oldStatus = usage.Success ? usage.DataOptInEnabled : null;
        if (oldStatus == request.Enabled)
        {
            return UnchangedDataOptInResult(request, context.TerminalId, oldStatus);
        }
        var apiResult = await RequestTerminalDataOptInAsync(context.TerminalId, context.AccessToken, request.Enabled, cancellationToken);
        return await SaveDeviceDataOptInResultAsync(request, userId, performedBy, context.TerminalId, oldStatus, apiResult, cancellationToken);
    }

    private static DeviceDataOptInChangeResult MapDataOptInContextError(UpdateDeviceDataOptInRequest request, DeviceCommandContext context)
    {
        return new DeviceDataOptInChangeResult { ErrorCode = request.Id <= 0 ? "validation_required" : context.ErrorCode, Message = context.Message, MessageEn = context.MessageEn, DeviceId = request.Id, TerminalId = context.TerminalId, NewStatus = request.Enabled, ApiResponse = context.RawResponse };
    }

    private async Task<DeviceCommandContext> GetDeviceCommandContextAsync(
        int id,
        int? tenantId,
        int? deviceId,
        bool useRouterDevice,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var device = await GetDeviceByIdInternalAsync(connection, id, cancellationToken, allowedTenantId: tenantId, allowedDeviceId: deviceId);
        if (device is null)
        {
            return new DeviceCommandContext
            {
                Success = false,
                ErrorCode = "device_not_found",
                Message = "Khong tim thay thiet bi hoac ban khong co quyen truy cap.",
                MessageEn = "The device was not found or you do not have access."
            };
        }

        var terminalId = device.DeviceCode?.Trim() ?? string.Empty;
        var routerKitId = device.KitId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(routerKitId))
        {
            return new DeviceCommandContext
            {
                Success = false,
                ErrorCode = "missing_wifi_identifiers",
                Message = "Thieu KITID hoac KITNumber de thuc hien lenh router.",
                MessageEn = "KITID or KITNumber is missing for the router command.",
                TerminalId = terminalId,
                DeviceId = routerKitId
            };
        }

        var accessToken = device.TokenString;
        if (string.IsNullOrWhiteSpace(accessToken) || IsTokenExpired(device.TokenExpiredTime))
        {
            var settings = await GetApiCredentialsAsync(connection, null, cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
            {
                return new DeviceCommandContext
                {
                    Success = false,
                    ErrorCode = "missing_api_credentials",
                    Message = "Thieu client_id hoac client_secret trong TblSettings",
                    MessageEn = "Missing client_id or client_secret in TblSettings",
                    TerminalId = terminalId,
                    DeviceId = routerKitId
                };
            }

            var tokenCall = await RequestDeviceTokenAsync(settings.ClientId, settings.ClientSecret, terminalId, cancellationToken);
            if (!tokenCall.Success)
            {
                return new DeviceCommandContext
                {
                    Success = false,
                    ErrorCode = tokenCall.ErrorCode,
                    Message = tokenCall.Message,
                    MessageEn = tokenCall.MessageEn,
                    RawResponse = tokenCall.RawResponse,
                    TerminalId = terminalId,
                    DeviceId = routerKitId
                };
            }

            accessToken = tokenCall.AccessToken ?? string.Empty;
            await UpdateDeviceTokenAsync(connection, id, accessToken, tokenCall.ExpiredTime, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new DeviceCommandContext
            {
                Success = false,
                ErrorCode = "missing_access_token",
                Message = "Khong co access token hop le de thuc hien lenh router.",
                MessageEn = "No valid access token was available for the router command.",
                TerminalId = terminalId,
                DeviceId = routerKitId
            };
        }

        if (!useRouterDevice)
        {
            return new DeviceCommandContext
            {
                Success = true,
                TerminalId = terminalId,
                DeviceId = routerKitId,
                AccessToken = accessToken
            };
        }

        var routerDevice = await ResolveRouterDeviceIdAsync(terminalId, accessToken, cancellationToken);
        if (!routerDevice.Success)
        {
            return new DeviceCommandContext
            {
                Success = false,
                ErrorCode = routerDevice.ErrorCode,
                Message = routerDevice.Message,
                MessageEn = routerDevice.MessageEn,
                RawResponse = routerDevice.RawResponse,
                TerminalId = terminalId,
                DeviceId = routerKitId
            };
        }

        return new DeviceCommandContext
        {
            Success = true,
            TerminalId = terminalId,
            DeviceId = routerDevice.DeviceId,
            AccessToken = accessToken
        };
    }

    private static DeviceDataOptInChangeResult UnchangedDataOptInResult(UpdateDeviceDataOptInRequest r, string terminalId, bool? oldStatus)
    {
        var result = new DeviceDataOptInChangeResult();
        result.ErrorCode = "status_unchanged";
        result.Message = "Thiết bị đã ở trạng thái được chọn.";
        result.MessageEn = "The terminal is already in the selected state.";
        result.DeviceId = r.Id;
        result.TerminalId = terminalId;
        result.OldStatus = oldStatus;
        result.NewStatus = r.Enabled;
        return result;
    }

    private sealed class DeviceCommandContext : DeviceCommandResult
    {
        public string AccessToken { get; set; } = string.Empty;
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
                ([DeviceName], [DeviceCode], [VesselName], [TenantID], [TokenString], [TokenExpiredTime], [Availability], [LastUpdateTime], [KITID], [KITNumber], [LastSysnTime])
            VALUES
                (@deviceName, @deviceCode, @vesselName, @tenantId, @tokenString, @tokenExpiredTime, @availability, GETUTCDATE(), @kitId, @kitNumber, NULL);
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
        command.Parameters.AddWithValue("@kitNumber", (object?)request.KitNumber ?? DBNull.Value);
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
                [KITNumber] = @kitNumber,
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
                [UsageData] = CASE WHEN NULLIF(@deviceCode, '') IS NULL THEN NULL ELSE [UsageData] END,
                [PriorityData] = CASE WHEN NULLIF(@deviceCode, '') IS NULL THEN NULL ELSE [PriorityData] END,
                [OverageData] = CASE WHEN NULLIF(@deviceCode, '') IS NULL THEN NULL ELSE [OverageData] END,
                [ServiceLine] = CASE WHEN NULLIF(@deviceCode, '') IS NULL THEN NULL ELSE [ServiceLine] END,
                [LastSysnTime] = CASE WHEN NULLIF(@deviceCode, '') IS NULL THEN NULL ELSE [LastSysnTime] END,
                [LastUpdateTime] = GETUTCDATE()
                """;
        }

        query += "\nWHERE [ID] = @id";

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.AddWithValue("@id", request.Id);
        command.Parameters.AddWithValue("@deviceName", request.DeviceName);
        command.Parameters.AddWithValue("@deviceCode", request.DeviceCode);
        command.Parameters.AddWithValue("@kitNumber", (object?)request.KitNumber ?? DBNull.Value);
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
        int? allowedTenantId = null,
        int? allowedDeviceId = null)
    {
        var hasPlanNameColumn = await HasPlanNameColumnAsync(connection, cancellationToken, transaction);
        var planNameSelect = hasPlanNameColumn
            ? "d.[PlanName],"
            : "CAST(NULL AS nvarchar(255)) AS [PlanName],";
        var planDataLimitSelect = hasPlanNameColumn
            ? "COALESCE(planLimit.[BaseData], d.[PriorityData]) AS [PlanDataLimit],"
            : "d.[PriorityData] AS [PlanDataLimit],";
        var planDataLimitApply = hasPlanNameColumn
            ? """
            OUTER APPLY (
                SELECT TOP 1 pp.[BaseData]
                FROM [dbo].[TblPricingPlan] pp
                WHERE pp.[PlanName] = d.[PlanName]
                   OR pp.[PlanCode] = d.[PlanName]
                ORDER BY CASE WHEN pp.[PlanName] = d.[PlanName] THEN 0 ELSE 1 END, pp.[ID]
            ) planLimit
            """
            : string.Empty;
        var deviceQuery = $"""
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
                {planDataLimitSelect}
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
            {planDataLimitApply}
            WHERE d.[ID] = @id
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
            """;
        await using var command = new SqlCommand(deviceQuery, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
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
            SubscriptionLimitGb = reader["PlanDataLimit"] as decimal?,
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
        if (string.IsNullOrWhiteSpace(device.DeviceCode))
        {
            device.Availability = string.IsNullOrWhiteSpace(device.Availability) ? "offline" : device.Availability;
            var stockResult = MapRefreshResult(device, refreshed: false);
            stockResult.Success = true;
            stockResult.Message = "Thiet bi stock chua co ma SLK.";
            stockResult.MessageEn = "Stock device does not have an SLK terminal id.";
            return stockResult;
        }

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

    private static async Task EnsureDeviceDataOptInHistorySchemaAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var command = new SqlCommand(DeviceDataOptInHistorySchemaSql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await command.DisposeAsync();
    }

    private static async Task<List<DeviceDataOptInHistoryItem>> GetDeviceDataOptInHistoryAsync(SqlConnection connection, int deviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 100 [ID], [DeviceId], [UserId], [PerformedBy], [PerformedAtUtc], [OldStatus], [NewStatus], [ApiSuccess], [HttpStatusCode], [ApiResponse], [JobId]
            FROM [dbo].[TblDeviceDataOptInHistory]
            WHERE [DeviceId] = @deviceId
            ORDER BY [PerformedAtUtc] DESC, [ID] DESC
            """;
        var items = new List<DeviceDataOptInHistoryItem>();
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapDeviceDataOptInHistoryItem(reader));
        }
        return items;
    }

    private static DeviceDataOptInHistoryItem MapDeviceDataOptInHistoryItem(SqlDataReader reader)
    {
        return new DeviceDataOptInHistoryItem
        {
            Id = Convert.ToInt32(reader["ID"]),
            DeviceId = Convert.ToInt32(reader["DeviceId"]),
            UserId = reader["UserId"] == DBNull.Value ? null : Convert.ToInt32(reader["UserId"]),
            PerformedBy = reader["PerformedBy"]?.ToString() ?? string.Empty,
            PerformedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["PerformedAtUtc"]), DateTimeKind.Utc),
            OldStatus = reader["OldStatus"] == DBNull.Value ? null : Convert.ToBoolean(reader["OldStatus"]),
            NewStatus = Convert.ToBoolean(reader["NewStatus"]),
            ApiSuccess = Convert.ToBoolean(reader["ApiSuccess"]),
            HttpStatusCode = reader["HttpStatusCode"] == DBNull.Value ? null : Convert.ToInt32(reader["HttpStatusCode"]),
            ApiResponse = reader["ApiResponse"]?.ToString() ?? string.Empty,
            JobId = reader["JobId"]?.ToString() ?? string.Empty
        };
    }

    private async Task<DeviceDataOptInChangeResult> SaveDeviceDataOptInResultAsync(UpdateDeviceDataOptInRequest request, int? userId, string performedBy, string terminalId, bool? oldStatus, DeviceCommandResult apiResult, CancellationToken cancellationToken)
    {
        const string insertQuery = """
            INSERT INTO [dbo].[TblDeviceDataOptInHistory]
                ([DeviceId], [UserId], [PerformedBy], [PerformedAtUtc], [OldStatus], [NewStatus], [ApiSuccess], [HttpStatusCode], [ApiResponse], [JobId])
            OUTPUT INSERTED.[ID]
            VALUES
                (@deviceId, @userId, @performedBy, @performedAtUtc, @oldStatus, @newStatus, @apiSuccess, @httpStatusCode, @apiResponse, @jobId)
            """;
        var performedAtUtc = DateTime.UtcNow;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureDeviceDataOptInHistorySchemaAsync(connection, null, cancellationToken);
        await using var command = new SqlCommand(insertQuery, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = request.Id;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@performedBy", SqlDbType.NVarChar, 250).Value = string.IsNullOrWhiteSpace(performedBy) ? "system" : performedBy.Trim();
        command.Parameters.Add("@performedAtUtc", SqlDbType.DateTime2).Value = performedAtUtc;
        command.Parameters.Add("@oldStatus", SqlDbType.Bit).Value = (object?)oldStatus ?? DBNull.Value;
        command.Parameters.Add("@newStatus", SqlDbType.Bit).Value = request.Enabled;
        command.Parameters.Add("@apiSuccess", SqlDbType.Bit).Value = apiResult.Success;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)apiResult.HttpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@apiResponse", SqlDbType.NVarChar, -1).Value = apiResult.RawResponse ?? string.Empty;
        command.Parameters.Add("@jobId", SqlDbType.NVarChar, 200).Value = apiResult.JobId ?? string.Empty;
        var historyId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var historyItem = new DeviceDataOptInHistoryItem { Id = historyId, DeviceId = request.Id, UserId = userId, PerformedBy = performedBy, PerformedAtUtc = performedAtUtc, OldStatus = oldStatus, NewStatus = request.Enabled, ApiSuccess = apiResult.Success, HttpStatusCode = apiResult.HttpStatusCode, ApiResponse = apiResult.RawResponse, JobId = apiResult.JobId };
        return new DeviceDataOptInChangeResult { Success = apiResult.Success, ErrorCode = apiResult.ErrorCode, Message = apiResult.Message, MessageEn = apiResult.MessageEn, DeviceId = request.Id, TerminalId = terminalId, OldStatus = oldStatus, NewStatus = request.Enabled, HttpStatusCode = apiResult.HttpStatusCode, ApiResponse = apiResult.RawResponse, JobId = apiResult.JobId, HistoryItem = historyItem };
    }

    private static async Task EnsureDevicePricingSchemaAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblDevicePricing]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblDevicePricing](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblDevicePricing] PRIMARY KEY,
                    [DeviceId] int NOT NULL,
                    [TenantId] int NOT NULL,
                    [PricingPlanId] int NOT NULL,
                    [ResellerPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblDevicePricing_ResellerPrice] DEFAULT(0),
                    [FinalPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblDevicePricing_FinalPrice] DEFAULT(0),
                    [ResellerOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblDevicePricing_ResellerOverChargePrice] DEFAULT(0),
                    [FinalOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblDevicePricing_FinalOverChargePrice] DEFAULT(0),
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblDevicePricing_Status] DEFAULT('active'),
                    [Created_Date] datetime NULL,
                    [Created_By] nvarchar(50) NULL,
                    [Updated_Date] datetime NULL,
                    [Updated_By] nvarchar(50) NULL
                );
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE name = N'UX_TblDevicePricing_Device_Plan'
                  AND object_id = OBJECT_ID(N'[dbo].[TblDevicePricing]')
            )
            BEGIN
                CREATE UNIQUE INDEX [UX_TblDevicePricing_Device_Plan]
                    ON [dbo].[TblDevicePricing]([DeviceId], [PricingPlanId]);
            END;

            IF COL_LENGTH(N'[dbo].[TblDevicePricing]', N'Status') IS NULL
            BEGIN
                ALTER TABLE [dbo].[TblDevicePricing]
                ADD [Status] nvarchar(50) NOT NULL
                    CONSTRAINT [DF_TblDevicePricing_Status] DEFAULT('active') WITH VALUES;
            END;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<DevicePlanOptionViewModel>> GetDevicePlanOptionsAsync(SqlConnection connection, int tenantId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT
                tp.[ID] AS [TenantPricingId],
                tp.[PricingPlanId],
                pp.[PlanName],
                pp.[PlanCode],
                pp.[Status],
                pp.[BaseData],
                tp.[ResellerPrice],
                tp.[FinalPrice],
                tp.[ResellerOverChargePrice],
                tp.[FinalOverChargePrice]
            FROM [TblTenantPricing] tp
            INNER JOIN [TblPricingPlan] pp ON pp.[ID] = tp.[PricingPlanId]
            WHERE tp.[TenantId] = @tenantId
              AND LOWER(ISNULL(pp.[Status], N'')) = N'active'
            ORDER BY pp.[PlanName] ASC, pp.[PlanCode] ASC
            """;

        var options = new List<DevicePlanOptionViewModel>();
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = tenantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            options.Add(MapDevicePlanOption(reader));
        }

        return options;
    }

    private static async Task<DevicePlanOptionViewModel?> GetDevicePlanOptionAsync(SqlConnection connection, SqlTransaction transaction, int tenantId, int pricingPlanId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1
                tp.[ID] AS [TenantPricingId],
                tp.[PricingPlanId],
                pp.[PlanName],
                pp.[PlanCode],
                pp.[Status],
                pp.[BaseData],
                tp.[ResellerPrice],
                tp.[FinalPrice],
                tp.[ResellerOverChargePrice],
                tp.[FinalOverChargePrice]
            FROM [TblTenantPricing] tp
            INNER JOIN [TblPricingPlan] pp ON pp.[ID] = tp.[PricingPlanId]
            WHERE tp.[TenantId] = @tenantId
              AND tp.[PricingPlanId] = @pricingPlanId
              AND LOWER(ISNULL(pp.[Status], N'')) = N'active'
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = pricingPlanId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapDevicePlanOption(reader) : null;
    }

    private static async Task<List<DevicePlanPriceViewModel>> GetDevicePlanPricesAsync(SqlConnection connection, int deviceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT
                dp.[ID],
                dp.[DeviceId],
                dp.[PricingPlanId],
                pp.[PlanName],
                pp.[PlanCode],
                COALESCE(dp.[Status], pp.[Status], 'active') AS [Status],
                pp.[BaseData],
                dp.[ResellerPrice],
                dp.[FinalPrice],
                dp.[ResellerOverChargePrice],
                dp.[FinalOverChargePrice],
                dp.[Updated_Date],
                dp.[Updated_By]
            FROM [TblDevicePricing] dp
            INNER JOIN [TblPricingPlan] pp ON pp.[ID] = dp.[PricingPlanId]
            WHERE dp.[DeviceId] = @deviceId
            ORDER BY pp.[PlanName] ASC, pp.[PlanCode] ASC
            """;

        var prices = new List<DevicePlanPriceViewModel>();
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            prices.Add(MapDevicePlanPrice(reader));
        }

        return prices;
    }

    private static async Task<int?> GetExistingDevicePlanPriceIdAsync(SqlConnection connection, SqlTransaction transaction, int deviceId, int pricingPlanId, CancellationToken cancellationToken)
    {
        const string query = "SELECT TOP 1 [ID] FROM [TblDevicePricing] WHERE [DeviceId] = @deviceId AND [PricingPlanId] = @pricingPlanId";
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = deviceId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = pricingPlanId;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null || scalar == DBNull.Value ? null : Convert.ToInt32(scalar);
    }

    private static DevicePlanOptionViewModel MapDevicePlanOption(SqlDataReader reader)
    {
        return new DevicePlanOptionViewModel
        {
            TenantPricingId = reader["TenantPricingId"] is int tenantPricingId ? tenantPricingId : 0,
            PricingPlanId = reader["PricingPlanId"] is int pricingPlanId ? pricingPlanId : 0,
            PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
            PlanCode = reader["PlanCode"]?.ToString() ?? string.Empty,
            Status = reader["Status"]?.ToString() ?? "active",
            BaseData = ReadDecimal(reader, "BaseData"),
            ResellerPrice = ReadDecimal(reader, "ResellerPrice"),
            FinalPrice = ReadDecimal(reader, "FinalPrice"),
            ResellerOverChargePrice = ReadDecimal(reader, "ResellerOverChargePrice"),
            FinalOverChargePrice = ReadDecimal(reader, "FinalOverChargePrice")
        };
    }

    private static DevicePlanPriceViewModel MapDevicePlanPrice(SqlDataReader reader)
    {
        return new DevicePlanPriceViewModel
        {
            Id = reader["ID"] is int id ? id : 0,
            DeviceId = reader["DeviceId"] is int deviceId ? deviceId : 0,
            PricingPlanId = reader["PricingPlanId"] is int pricingPlanId ? pricingPlanId : 0,
            PlanName = reader["PlanName"]?.ToString() ?? string.Empty,
            PlanCode = reader["PlanCode"]?.ToString() ?? string.Empty,
            Status = reader["Status"]?.ToString() ?? "active",
            BaseData = ReadDecimal(reader, "BaseData"),
            ResellerPrice = ReadDecimal(reader, "ResellerPrice"),
            FinalPrice = ReadDecimal(reader, "FinalPrice"),
            ResellerOverChargePrice = ReadDecimal(reader, "ResellerOverChargePrice"),
            FinalOverChargePrice = ReadDecimal(reader, "FinalOverChargePrice"),
            UpdatedDate = reader["Updated_Date"] as DateTime?,
            UpdatedBy = reader["Updated_By"]?.ToString()
        };
    }

    private static decimal ReadDecimal(SqlDataReader reader, string columnName)
    {
        return reader[columnName] is decimal value ? value : 0m;
    }

    private static void AddDevicePlanPriceParameters(SqlCommand command, SaveDevicePlanRequest request, int tenantId)
    {
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = request.DeviceId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@pricingPlanId", SqlDbType.Int).Value = request.PricingPlanId;
        AddDecimalParameter(command, "@resellerPrice", request.ResellerPrice);
        AddDecimalParameter(command, "@finalPrice", request.FinalPrice);
        AddDecimalParameter(command, "@resellerOverChargePrice", request.ResellerOverChargePrice);
        AddDecimalParameter(command, "@finalOverChargePrice", request.FinalOverChargePrice);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(request.Status) ? "active" : request.Status;
    }

    private static void AddDecimalParameter(SqlCommand command, string name, decimal value)
    {
        command.Parameters.Add(name, SqlDbType.Decimal).Value = value;
        command.Parameters[name].Precision = 18;
        command.Parameters[name].Scale = 2;
    }

    private static string BuildDeviceSearchClause(bool hasPlanNameColumn)
    {
        var searchableColumns = new List<string>
        {
            "ISNULL(d.[DeviceName], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(d.[VesselName], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(t.[TenantName], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(d.[DeviceCode], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(d.[KITNumber], '') LIKE @searchPattern ESCAPE '\\'",
            "ISNULL(d.[ServiceLine], '') LIKE @searchPattern ESCAPE '\\'",
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
            if (!TryFindDeviceArray(document.RootElement, out var devicesElement) || devicesElement.GetArrayLength() == 0)
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

            var selectedDevice = FindPreferredDevice(devicesElement, preferRouter: true) ?? devicesElement[0];
            var kitId = selectedDevice.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var availability = selectedDevice.TryGetProperty("availability", out var availabilityElement)
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

    private async Task<(bool Success, string RawResponse, decimal? SubscriptionUsageGb, decimal? SubscriptionLimitGb, decimal? PriorityOverageGb, decimal? PriorityOverageLimitGb, string PlanName, bool? DataOptInEnabled)> RequestTerminalUsageAsync(
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
            return (false, rawResponse, null, null, null, null, string.Empty, null);
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
            bool? dataOptInEnabled = null;

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
                            if (usage.TryGetProperty("optin", out var optInElement) &&
                                (optInElement.ValueKind == JsonValueKind.True || optInElement.ValueKind == JsonValueKind.False))
                            {
                                dataOptInEnabled = optInElement.GetBoolean();
                            }
                            continue;
                        }

                        if (string.Equals(serviceCode, "SLP", StringComparison.OrdinalIgnoreCase) &&
                            usage.TryGetProperty("optin", out var localPriorityOptInElement) &&
                            (localPriorityOptInElement.ValueKind == JsonValueKind.True || localPriorityOptInElement.ValueKind == JsonValueKind.False))
                        {
                            dataOptInEnabled = localPriorityOptInElement.GetBoolean();
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

            return (true, rawResponse, totalUsageGb, totalLimitGb, priorityOverageGb, priorityOverageLimitGb, planName, dataOptInEnabled);
        }
        catch (JsonException)
        {
            return (false, rawResponse, null, null, null, null, string.Empty, null);
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
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new DeviceWifiResult
                {
                    Success = false,
                    ErrorCode = "wifi_endpoint_not_found",
                    Message = "API WiFi khong tim thay cau hinh router cho device nay.",
                    MessageEn = "The WiFi API did not find router configuration for this device.",
                    RawResponse = rawResponse,
                    TerminalId = terminalId,
                    DeviceId = deviceId
                };
            }

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
                Password = FindJsonStringValue(root, "password", "passphrase", "wifiPassword", "wiFiPassword", "psk"),
                Enabled = FindJsonBooleanValue(root, "enabled", "wifiEnabled", "wiFiEnabled", "isEnabled")
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

    private async Task<DeviceCommandResult> RequestTerminalDataOptInAsync(string terminalId, string accessToken, bool enabled, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/optin");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { enabled }), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        return BuildDataOptInApiResult(response, rawResponse, terminalId);
    }

    private async Task<DeviceCommandResult> RequestUpdateDeviceWifiAsync(
        string terminalId,
        string deviceId,
        string accessToken,
        string ssid,
        string password,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/devices/{Uri.EscapeDataString(deviceId)}/wifi");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                ssid,
                password,
                enabled
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new DeviceCommandResult
            {
                Success = false,
                ErrorCode = "wifi_update_api_error",
                Message = $"API cap nhat WiFi tra ve loi {(int)response.StatusCode}",
                MessageEn = $"WiFi update API returned error {(int)response.StatusCode}",
                RawResponse = rawResponse,
                TerminalId = terminalId,
                DeviceId = deviceId
            };
        }

        return new DeviceCommandResult
        {
            Success = true,
            Message = "Da gui lenh cap nhat WiFi.",
            MessageEn = "WiFi update command was submitted.",
            RawResponse = rawResponse,
            TerminalId = terminalId,
            DeviceId = deviceId,
            JobId = ExtractJobId(rawResponse)
        };
    }

    private async Task<DeviceCommandResult> RequestRebootDeviceAsync(
        string terminalId,
        string deviceId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/devices/{Uri.EscapeDataString(deviceId)}/reboot");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new DeviceCommandResult
            {
                Success = false,
                ErrorCode = "router_reboot_api_error",
                Message = $"API reboot router tra ve loi {(int)response.StatusCode}",
                MessageEn = $"Router reboot API returned error {(int)response.StatusCode}",
                RawResponse = rawResponse,
                TerminalId = terminalId,
                DeviceId = deviceId
            };
        }

        return new DeviceCommandResult
        {
            Success = true,
            Message = "Da gui lenh reboot router.",
            MessageEn = "Router reboot command was submitted.",
            RawResponse = rawResponse,
            TerminalId = terminalId,
            DeviceId = deviceId,
            JobId = ExtractJobId(rawResponse)
        };
    }

    private async Task<DeviceCommandResult> ResolveRouterDeviceIdAsync(string terminalId, string accessToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mykvh.com/v3/terminals/{Uri.EscapeDataString(terminalId)}/devices");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new DeviceCommandResult
            {
                Success = false,
                ErrorCode = "terminal_devices_error",
                Message = $"API danh sach thiet bi tra ve loi {(int)response.StatusCode}",
                MessageEn = $"Terminal devices API returned error {(int)response.StatusCode}",
                RawResponse = rawResponse,
                TerminalId = terminalId
            };
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var routerDeviceId = FindRouterDeviceId(document.RootElement);
            if (string.IsNullOrWhiteSpace(routerDeviceId))
            {
                return new DeviceCommandResult
                {
                    Success = false,
                    ErrorCode = "router_device_not_found",
                    Message = "Khong tim thay Starlink WiFi router trong danh sach device cua terminal nay.",
                    MessageEn = "No Starlink WiFi router was found in this terminal's device list.",
                    RawResponse = rawResponse,
                    TerminalId = terminalId
                };
            }

            return new DeviceCommandResult
            {
                Success = true,
                RawResponse = rawResponse,
                TerminalId = terminalId,
                DeviceId = routerDeviceId
            };
        }
        catch (JsonException)
        {
            return new DeviceCommandResult
            {
                Success = false,
                ErrorCode = "terminal_devices_invalid_json",
                Message = "API danh sach thiet bi tra ve JSON khong hop le.",
                MessageEn = "The terminal devices API returned invalid JSON.",
                RawResponse = rawResponse,
                TerminalId = terminalId
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

    private static bool? FindJsonBooleanValue(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var propertyValue))
                {
                    continue;
                }

                if (propertyValue.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (propertyValue.ValueKind == JsonValueKind.False)
                {
                    return false;
                }

                if (propertyValue.ValueKind == JsonValueKind.String &&
                    bool.TryParse(propertyValue.GetString(), out var parsedValue))
                {
                    return parsedValue;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var value = FindJsonBooleanValue(property.Value, propertyNames);
                if (value.HasValue)
                {
                    return value;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = FindJsonBooleanValue(item, propertyNames);
                if (value.HasValue)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static DeviceCommandResult BuildDataOptInApiResult(HttpResponseMessage response, string rawResponse, string terminalId)
    {
        var result = new DeviceCommandResult();
        result.Success = response.IsSuccessStatusCode;
        result.ErrorCode = result.Success ? string.Empty : "data_optin_api_error";
        result.Message = result.Success ? "Data opt-in/out request accepted." : "Data opt-in/out API request failed.";
        result.MessageEn = result.Message;
        result.RawResponse = rawResponse;
        result.TerminalId = terminalId;
        result.JobId = ExtractJobId(rawResponse);
        result.HttpStatusCode = (int)response.StatusCode;
        return result;
    }

    private static string ExtractJobId(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            return FindJsonStringValue(document.RootElement, "job_id", "jobId", "jobID", "id");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string? FindPreferredDeviceId(JsonElement devicesElement, bool preferRouter)
    {
        var device = FindPreferredDevice(devicesElement, preferRouter);
        return device?.TryGetProperty("id", out var idElement) == true
            ? idElement.GetString()
            : null;
    }

    private static string? FindRouterDeviceId(JsonElement devicesElement)
    {
        if (!TryFindDeviceArray(devicesElement, out var deviceArray))
        {
            return null;
        }

        foreach (var device in deviceArray.EnumerateArray())
        {
            if (device.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = FindJsonStringValue(device, "type", "device_type", "deviceType");
            if (!type.Contains("router", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return FindJsonStringValue(device, "id", "device_id", "deviceId");
        }

        return null;
    }

    private static bool TryFindDeviceArray(JsonElement element, out JsonElement devicesElement)
    {
        if (IsDeviceArray(element))
        {
            devicesElement = element;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "devices", "data", "items", "results" })
            {
                if (element.TryGetProperty(propertyName, out var child) && IsDeviceArray(child))
                {
                    devicesElement = child;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindDeviceArray(property.Value, out devicesElement))
                {
                    return true;
                }
            }
        }

        devicesElement = default;
        return false;
    }

    private static bool IsDeviceArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return element.GetArrayLength() == 0 ||
            element.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.Object &&
                (item.TryGetProperty("id", out _) ||
                 item.TryGetProperty("device_id", out _) ||
                 item.TryGetProperty("deviceId", out _)));
    }

    private static JsonElement? FindPreferredDevice(JsonElement devicesElement, bool preferRouter)
    {
        if (devicesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? fallback = null;
        foreach (var device in devicesElement.EnumerateArray())
        {
            if (device.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            fallback ??= device;
            if (!preferRouter)
            {
                continue;
            }

            var type = FindJsonStringValue(device, "type", "device_type", "deviceType");
            if (type.Contains("router", StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return fallback;
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

    private static string BuildCombinedApiResult(string tokenRawResponse, string devicesRawResponse, string statusRawResponse = "")
    {
        try
        {
            using var tokenDocument = JsonDocument.Parse(tokenRawResponse);
            using var devicesDocument = JsonDocument.Parse(devicesRawResponse);
            using var statusDocument = string.IsNullOrWhiteSpace(statusRawResponse) ? null : JsonDocument.Parse(statusRawResponse);

            return JsonSerializer.Serialize(new
            {
                token = tokenDocument.RootElement.Clone(),
                devices = devicesDocument.RootElement.Clone(),
                status = statusDocument?.RootElement.Clone()
            }, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (JsonException)
        {
            var result = $"TOKEN RESPONSE:{Environment.NewLine}{tokenRawResponse}{Environment.NewLine}{Environment.NewLine}DEVICES RESPONSE:{Environment.NewLine}{devicesRawResponse}";
            return string.IsNullOrWhiteSpace(statusRawResponse)
                ? result
                : $"{result}{Environment.NewLine}{Environment.NewLine}STATUS RESPONSE:{Environment.NewLine}{statusRawResponse}";
        }
    }

    private static bool IsRateLimitResponse(string rawResponse, string message)
    {
        return (message ?? string.Empty).Contains("429", StringComparison.OrdinalIgnoreCase)
            || (rawResponse ?? string.Empty).Contains("429", StringComparison.OrdinalIgnoreCase)
            || (rawResponse ?? string.Empty).Contains("too many", StringComparison.OrdinalIgnoreCase)
            || (rawResponse ?? string.Empty).Contains("rate", StringComparison.OrdinalIgnoreCase);
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
