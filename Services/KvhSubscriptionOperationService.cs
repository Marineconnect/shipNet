using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class KvhSubscriptionOperationService(
    IConfiguration configuration,
    IKvhSubscriptionService kvhSubscriptionService,
    IOptions<KvhSubscriptionOperationOptions> options,
    ILogger<KvhSubscriptionOperationService> logger) : IKvhSubscriptionOperationService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task<KvhSubscriptionOperationIndexViewModel> GetBatchesAsync(KvhSubscriptionOperationFilter filter, int page, int pageSize, int? allowedTenantId = null, int? allowedDeviceId = null, bool canManage = false, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is 20 or 50 or 100 ? pageSize : 20;
        NormalizeFilter(filter);
        if (allowedTenantId.HasValue)
        {
            filter.TenantId = allowedTenantId;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var where = BuildBatchWhere(filter, allowedTenantId, allowedDeviceId);
        var total = await CountBatchesAsync(connection, where, filter, allowedTenantId, allowedDeviceId, cancellationToken);
        var totalPages = pageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        if (totalPages > 0 && page > totalPages) page = totalPages;

        var items = new List<KvhSubscriptionOperationBatchListItem>();
        var sql = $"""
            SELECT b.*, ISNULL(t.[TenantName], '') AS [TenantName]
            FROM [dbo].[TblKvhSubscriptionOperationBatch] b
            LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = b.[TenantId]
            WHERE {where}
            ORDER BY b.[CreatedAtUtc] DESC, b.[ID] DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;
        await using (var command = new SqlCommand(sql, connection))
        {
            AddBatchFilterParameters(command, filter, allowedTenantId, allowedDeviceId);
            command.Parameters.Add("@offset", SqlDbType.Int).Value = (page - 1) * pageSize;
            command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(MapBatchListItem(reader));
            }
        }

        return new KvhSubscriptionOperationIndexViewModel
        {
            Items = items,
            Tenants = (await GetTenantOptionsAsync(connection, allowedTenantId, cancellationToken)).ToList(),
            Filter = filter,
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = total,
            CanManage = canManage,
            IsTenantScoped = allowedTenantId.HasValue
        };
    }

    public async Task<KvhSubscriptionOperationDetailViewModel?> GetBatchAsync(long id, int? allowedTenantId = null, int? allowedDeviceId = null, bool canManage = false, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await RefreshCountersAsync(connection, id, cancellationToken);

        const string batchSql = """
            SELECT b.*, ISNULL(t.[TenantName], '') AS [TenantName]
            FROM [dbo].[TblKvhSubscriptionOperationBatch] b
            LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = b.[TenantId]
            WHERE b.[ID] = @id
              AND (@tenantId IS NULL OR b.[TenantId] = @tenantId)
              AND (@deviceId IS NULL OR EXISTS (SELECT 1 FROM [dbo].[TblKvhSubscriptionOperationItem] i WHERE i.[BatchId] = b.[ID] AND i.[DeviceId] = @deviceId))
            """;
        KvhSubscriptionOperationDetailViewModel? model = null;
        await using (var command = new SqlCommand(batchSql, connection))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
            command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                model = MapBatchDetail(reader);
                model.CanManage = canManage;
            }
        }

        if (model is null) return null;
        model.Items = (await GetItemsAsync(connection, id, cancellationToken)).ToList();
        model.DeviceOptions = canManage && model.CanEdit
            ? (await GetDeviceOptionsAsync(connection, allowedTenantId ?? model.TenantId, allowedDeviceId, id, cancellationToken)).ToList()
            : [];
        return model;
    }

    public async Task<long> CreateBatchAsync(KvhSubscriptionOperationCreateRequest request, int? userId, string requestedBy, int? allowedTenantId = null, CancellationToken cancellationToken = default)
    {
        var operationType = KvhSubscriptionOperationTypes.Normalize(request.OperationType);
        if (string.IsNullOrWhiteSpace(request.BatchName)) throw new InvalidOperationException("Tên đợt là bắt buộc.");
        if (string.IsNullOrWhiteSpace(operationType)) throw new InvalidOperationException("Loại thao tác không hợp lệ.");
        if (allowedTenantId.HasValue) request.TenantId = allowedTenantId;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var tempCode = "KSO-PENDING-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        const string insertSql = """
            INSERT INTO [dbo].[TblKvhSubscriptionOperationBatch]
                ([BatchCode], [BatchName], [OperationType], [Status], [TenantId], [Description], [ScheduledStartAtUtc], [RequestedByUserId], [RequestedBy], [CreatedAtUtc], [UpdatedAtUtc])
            OUTPUT INSERTED.[ID]
            VALUES (@code, @name, @operationType, 'DRAFT', @tenantId, @description, @scheduledStart, @userId, @requestedBy, SYSUTCDATETIME(), SYSUTCDATETIME())
            """;
        long id;
        await using (var command = new SqlCommand(insertSql, connection, transaction))
        {
            command.Parameters.Add("@code", SqlDbType.NVarChar, 50).Value = tempCode;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 250).Value = request.BatchName.Trim();
            command.Parameters.Add("@operationType", SqlDbType.NVarChar, 30).Value = operationType;
            command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)request.TenantId ?? DBNull.Value;
            command.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value = Db(request.Description);
            command.Parameters.Add("@scheduledStart", SqlDbType.DateTime2).Value = (object?)request.ScheduledStartAtUtc ?? DBNull.Value;
            command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
            command.Parameters.Add("@requestedBy", SqlDbType.NVarChar, 250).Value = NormalizeUser(requestedBy);
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        var batchCode = $"KSO-{DateTime.UtcNow:yyyyMMdd}-{id:000000}";
        await using (var update = new SqlCommand("UPDATE [dbo].[TblKvhSubscriptionOperationBatch] SET [BatchCode] = @code WHERE [ID] = @id", connection, transaction))
        {
            update.Parameters.Add("@code", SqlDbType.NVarChar, 50).Value = batchCode;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, id, null, "CREATE_BATCH", userId, requestedBy, $"Tạo đợt {batchCode}.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<int> AddDevicesAsync(long batchId, IReadOnlyList<int> deviceIds, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        if (deviceIds.Count == 0) return 0;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var batch = await GetBatchHeaderAsync(connection, transaction, batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        EnsureDraft(batch);

        var devices = await FindDevicesByIdsAsync(connection, transaction, deviceIds, allowedTenantId ?? batch.TenantId, allowedDeviceId, cancellationToken);
        var added = 0;
        foreach (var device in devices)
        {
            added += await InsertItemIfNotExistsAsync(connection, transaction, batchId, device, batch.OperationType, null, "UI", null, cancellationToken) ? 1 : 0;
        }

        await InsertAuditAsync(connection, transaction, batchId, null, "ADD_DEVICES", userId, requestedBy, $"Thêm {added} thiết bị.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ValidateBatchAsync(batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        return added;
    }

    public async Task<KvhSubscriptionOperationImportPreview> PreviewImportAsync(long batchId, IFormFile file, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        if (file.Length <= 0) throw new InvalidOperationException("File import trống.");
        if (file.Length > options.Value.MaxImportFileSizeMb * 1024L * 1024L) throw new InvalidOperationException("File import vượt dung lượng cho phép.");
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Chỉ hỗ trợ file .xlsx.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var batch = await GetBatchHeaderAsync(connection, null, batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        EnsureDraft(batch);
        var existingKits = await GetExistingKitSetAsync(connection, batchId, cancellationToken);

        await using var stream = file.OpenReadStream();
        IWorkbook workbook = new XSSFWorkbook(stream);
        var sheet = workbook.GetSheet("Danh_sach_KIT") ?? workbook.GetSheetAt(0);
        var rows = new List<KvhSubscriptionOperationImportRow>();
        var fileKitSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxRows = Math.Max(1, options.Value.MaxImportRows);
        for (var rowIndex = 1; rowIndex <= sheet.LastRowNum && rows.Count < maxRows; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row is null) continue;
            var kit = ReadCell(row, 1);
            var terminal = ReadCell(row, 2);
            var traffic = ReadCell(row, 3);
            var region = ReadCell(row, 4);
            var operation = KvhSubscriptionOperationTypes.Normalize(ReadCell(row, 5));
            var vessel = ReadCell(row, 6);
            var tenant = ReadCell(row, 7);
            var note = ReadCell(row, 8);
            if (string.IsNullOrWhiteSpace(kit) && string.IsNullOrWhiteSpace(terminal) && string.IsNullOrWhiteSpace(traffic) && string.IsNullOrWhiteSpace(region) && string.IsNullOrWhiteSpace(operation) && string.IsNullOrWhiteSpace(vessel) && string.IsNullOrWhiteSpace(tenant)) continue;

            operation = string.IsNullOrWhiteSpace(operation) ? batch.OperationType : operation;
            var importRow = new KvhSubscriptionOperationImportRow
            {
                RowNumber = rowIndex + 1,
                KitNumber = kit.Trim(),
                TerminalId = terminal.Trim(),
                TrafficId = traffic.Trim(),
                Region = region.Trim(),
                OperationType = operation,
                VesselName = vessel.Trim(),
                TenantName = tenant.Trim(),
                Note = note.Trim()
            };
            ValidateImportRowShape(importRow, existingKits, fileKitSet);
            rows.Add(importRow);
        }

        var validRows = rows.Where(r => !string.IsNullOrWhiteSpace(r.KitNumber)).ToList();
        var devices = await FindDevicesByKitsAsync(connection, validRows.Select(r => r.KitNumber).ToList(), allowedTenantId ?? batch.TenantId, allowedDeviceId, cancellationToken);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.KitNumber) && devices.TryGetValue(NormalizeKit(row.KitNumber), out var device))
            {
                row.DeviceId = device.DeviceId;
                row.KvhSubscriptionId = device.KvhSubscriptionId;
                row.VesselName = string.IsNullOrWhiteSpace(row.VesselName) ? device.VesselName : row.VesselName;
                row.TenantName = string.IsNullOrWhiteSpace(row.TenantName) ? device.TenantName : row.TenantName;
                row.TerminalId = string.IsNullOrWhiteSpace(row.TerminalId) ? device.TerminalId : row.TerminalId;
                row.TrafficId = string.IsNullOrWhiteSpace(row.TrafficId) ? device.TrafficId : row.TrafficId;
                row.Region = string.IsNullOrWhiteSpace(row.Region) ? device.Region : row.Region;
                ValidateOperationState(row, device.SubscriptionStatus, device.ScheduledAction, device.TrafficId, device.Region, device.KvhSubscriptionId);
            }
            else if (!string.IsNullOrWhiteSpace(row.KitNumber))
            {
                row.IsValid = false;
                row.Message = AppendMessage(row.Message, "Không tìm thấy KIT hoặc không có quyền.");
            }
        }

        return new KvhSubscriptionOperationImportPreview
        {
            PreviewToken = Guid.NewGuid().ToString("N"),
            TotalRows = rows.Count,
            ValidRows = rows.Count(row => row.IsValid),
            WarningRows = rows.Count(row => row.HasWarning),
            ErrorRows = rows.Count(row => !row.IsValid),
            DuplicateRows = rows.Count(row => row.IsDuplicate),
            UnknownKitRows = rows.Count(row => !row.DeviceId.HasValue),
            Rows = rows
        };
    }

    public async Task<int> ConfirmImportAsync(long batchId, KvhSubscriptionOperationImportPreview preview, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var batch = await GetBatchHeaderAsync(connection, transaction, batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        EnsureDraft(batch);
        var added = 0;
        foreach (var row in preview.Rows.Where(row => row.IsValid && row.DeviceId.HasValue))
        {
            var deviceId = row.DeviceId.GetValueOrDefault();
            var snapshot = new DeviceSnapshot
            {
                DeviceId = deviceId,
                KitNumber = row.KitNumber,
                TerminalId = row.TerminalId,
                TrafficId = row.TrafficId,
                Region = row.Region,
                KvhSubscriptionId = row.KvhSubscriptionId
            };
            added += await InsertItemIfNotExistsAsync(connection, transaction, batchId, snapshot, row.OperationType, row.RowNumber, "EXCEL", row.Note, cancellationToken) ? 1 : 0;
        }

        await InsertAuditAsync(connection, transaction, batchId, null, "CONFIRM_IMPORT", userId, requestedBy, $"Import {added} dòng hợp lệ.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ValidateBatchAsync(batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        return added;
    }

    public async Task<int> ValidateBatchAsync(long batchId, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var batch = await GetBatchHeaderAsync(connection, transaction, batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        if (batch.Status is not (KvhSubscriptionOperationBatchStatuses.Draft or KvhSubscriptionOperationBatchStatuses.Ready))
        {
            throw new InvalidOperationException("Chỉ có thể kiểm tra đợt ở trạng thái nháp/sẵn sàng.");
        }

        await MarkBatchStatusAsync(connection, transaction, batchId, KvhSubscriptionOperationBatchStatuses.Validating, cancellationToken);
        var items = await GetValidationItemsAsync(connection, transaction, batchId, cancellationToken);
        var ready = 0;
        foreach (var item in items)
        {
            var error = ValidateItem(batch.OperationType, item);
            var status = string.IsNullOrWhiteSpace(error) ? KvhSubscriptionOperationItemStatuses.Ready : KvhSubscriptionOperationItemStatuses.ValidationFailed;
            if (status == KvhSubscriptionOperationItemStatuses.Ready) ready++;
            await using var command = new SqlCommand("""
                UPDATE [dbo].[TblKvhSubscriptionOperationItem]
                SET [DeviceId] = @deviceId, [TerminalId] = @terminalId, [TrafficId] = @trafficId, [Region] = @region, [KvhSubscriptionId] = @subscriptionId,
                    [Status] = @status, [ErrorCode] = @errorCode, [ErrorMessage] = @errorMessage, [UpdatedAtUtc] = SYSUTCDATETIME()
                WHERE [ID] = @id
                """, connection, transaction);
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = item.Id;
            command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)item.DeviceId ?? DBNull.Value;
            command.Parameters.Add("@terminalId", SqlDbType.NVarChar, 200).Value = Db(item.TerminalId);
            command.Parameters.Add("@trafficId", SqlDbType.NVarChar, 200).Value = Db(item.TrafficId);
            command.Parameters.Add("@region", SqlDbType.NVarChar, 100).Value = Db(item.Region);
            command.Parameters.Add("@subscriptionId", SqlDbType.BigInt).Value = (object?)item.KvhSubscriptionId ?? DBNull.Value;
            command.Parameters.Add("@status", SqlDbType.NVarChar, 40).Value = status;
            command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = string.IsNullOrWhiteSpace(error) ? DBNull.Value : "validation_failed";
            command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = Db(error);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await MarkBatchStatusAsync(connection, transaction, batchId, ready == items.Count && ready > 0 ? KvhSubscriptionOperationBatchStatuses.Ready : KvhSubscriptionOperationBatchStatuses.Draft, cancellationToken);
        await RefreshCountersAsync(connection, batchId, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ready;
    }

    public async Task<bool> StartBatchAsync(long batchId, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        var ready = await ValidateBatchAsync(batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        if (ready <= 0) throw new InvalidOperationException("Đợt chưa có item hợp lệ để bắt đầu.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var batch = await GetBatchHeaderAsync(connection, transaction, batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        if (batch.Status != KvhSubscriptionOperationBatchStatuses.Ready) throw new InvalidOperationException("Đợt chưa sẵn sàng để bắt đầu.");

        const string queueSql = """
            UPDATE [dbo].[TblKvhSubscriptionOperationItem]
            SET [Status] = 'QUEUED', [NextSubmitAtUtc] = COALESCE(@scheduledStart, SYSUTCDATETIME()), [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [BatchId] = @batchId AND [Status] = 'READY';

            UPDATE [dbo].[TblKvhSubscriptionOperationBatch]
            SET [Status] = 'QUEUED', [StartedAtUtc] = COALESCE([StartedAtUtc], SYSUTCDATETIME()), [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @batchId;
            """;
        await using (var command = new SqlCommand(queueSql, connection, transaction))
        {
            command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
            command.Parameters.Add("@scheduledStart", SqlDbType.DateTime2).Value = (object?)batch.ScheduledStartAtUtc ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, batchId, null, "START_BATCH", userId, requestedBy, "Bắt đầu đợt thao tác.", cancellationToken);
        await RefreshCountersAsync(connection, batchId, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task CancelBatchAsync(long batchId, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await GetBatchHeaderAsync(connection, transaction, batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        const string sql = """
            UPDATE [dbo].[TblKvhSubscriptionOperationBatch]
            SET [Status] = 'CANCEL_REQUESTED', [CancelRequestedAtUtc] = SYSUTCDATETIME(), [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @batchId AND [Status] IN ('QUEUED', 'RUNNING', 'VERIFYING');

            UPDATE [dbo].[TblKvhSubscriptionOperationItem]
            SET [Status] = 'CANCELLED', [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [BatchId] = @batchId AND [Status] IN ('DRAFT', 'READY', 'QUEUED', 'WAITING_COOLDOWN', 'RETRY_WAIT');
            """;
        await using (var command = new SqlCommand(sql, connection, transaction))
        {
            command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, batchId, null, "CANCEL_BATCH", userId, requestedBy, "Yêu cầu hủy batch. Lệnh đã gửi KVH vẫn tiếp tục được theo dõi.", cancellationToken);
        await RefreshCountersAsync(connection, batchId, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RetryFailedAsync(long batchId, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await GetBatchHeaderAsync(connection, transaction, batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        const string sql = """
            UPDATE [dbo].[TblKvhSubscriptionOperationItem]
            SET [Status] = 'RETRY_WAIT', [KvhCommandId] = NULL, [JobId] = NULL, [JobStatus] = NULL, [VerificationStatus] = NULL,
                [NextSubmitAtUtc] = SYSUTCDATETIME(), [ErrorCode] = NULL, [ErrorMessage] = NULL,
                [HttpStatusCode] = NULL, [SubmitResponseJson] = NULL, [OperationLogJson] = NULL,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [BatchId] = @batchId
              AND [Status] IN ('JOB_FAILED', 'TIMEOUT')
              AND [AttemptCount] < [MaxAttemptCount]
              AND ISNULL([ErrorCode], '') NOT IN ('validation_failed', 'missing_traffic_id', 'missing_region', 'missing_subscription', 'invalid_subscription_state', 'permission_denied');

            UPDATE [dbo].[TblKvhSubscriptionOperationBatch]
            SET [Status] = 'QUEUED', [CompletedAtUtc] = NULL, [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @batchId;
            """;
        await using (var command = new SqlCommand(sql, connection, transaction))
        {
            command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, batchId, null, "RETRY_FAILED", userId, requestedBy, "Chạy lại các item lỗi retryable trong cùng batch.", cancellationToken);
        await RefreshCountersAsync(connection, batchId, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveItemAsync(long batchId, long itemId, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var batch = await GetBatchHeaderAsync(connection, transaction, batchId, allowedTenantId, allowedDeviceId, cancellationToken);
        EnsureDraft(batch);
        await using (var command = new SqlCommand("DELETE FROM [dbo].[TblKvhSubscriptionOperationItem] WHERE [ID] = @itemId AND [BatchId] = @batchId", connection, transaction))
        {
            command.Parameters.Add("@itemId", SqlDbType.BigInt).Value = itemId;
            command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, batchId, itemId, "REMOVE_ITEM", userId, requestedBy, "Xóa item khỏi đợt nháp.", cancellationToken);
        await RefreshCountersAsync(connection, batchId, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<byte[]> ExportAsync(long batchId, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default)
    {
        var batch = await GetBatchAsync(batchId, allowedTenantId, allowedDeviceId, true, cancellationToken) ?? throw new InvalidOperationException("Không tìm thấy batch.");
        IWorkbook workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Ket_qua");
        var header = sheet.CreateRow(0);
        var headers = new[] { "KIT", "Terminal", "Tau", "Tenant", "Traffic ID", "Region", "Operation", "Status", "Command ID", "Job ID", "Job Status", "Verification", "Attempt", "Poll", "Loi" };
        for (var i = 0; i < headers.Length; i++) header.CreateCell(i).SetCellValue(headers[i]);
        for (var i = 0; i < batch.Items.Count; i++)
        {
            var item = batch.Items[i];
            var row = sheet.CreateRow(i + 1);
            row.CreateCell(0).SetCellValue(item.KitNumber);
            row.CreateCell(1).SetCellValue(item.TerminalId);
            row.CreateCell(2).SetCellValue(item.VesselName);
            row.CreateCell(3).SetCellValue(item.TenantName);
            row.CreateCell(4).SetCellValue(item.TrafficId);
            row.CreateCell(5).SetCellValue(item.Region);
            row.CreateCell(6).SetCellValue(item.OperationType);
            row.CreateCell(7).SetCellValue(item.Status);
            row.CreateCell(8).SetCellValue(item.KvhCommandId?.ToString() ?? string.Empty);
            row.CreateCell(9).SetCellValue(item.JobId);
            row.CreateCell(10).SetCellValue(item.JobStatus);
            row.CreateCell(11).SetCellValue(item.VerificationStatus);
            row.CreateCell(12).SetCellValue(item.AttemptCount);
            row.CreateCell(13).SetCellValue(item.PollCount);
            row.CreateCell(14).SetCellValue(item.ErrorMessage);
        }

        using var output = new MemoryStream();
        workbook.Write(output, true);
        return output.ToArray();
    }

    public byte[] BuildTemplate()
    {
        IWorkbook workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Danh_sach_KIT");
        var header = sheet.CreateRow(0);
        var headers = new[] { "STT", "KIT Number (*)", "Terminal ID", "Traffic ID", "Region", "Loại thao tác", "Tên tàu", "Tenant", "Ghi chú", "Kết quả kiểm tra" };
        for (var i = 0; i < headers.Length; i++) header.CreateCell(i).SetCellValue(headers[i]);
        var guide = workbook.CreateSheet("Huong_dan");
        guide.CreateRow(0).CreateCell(0).SetCellValue("Nhập KIT Number. Loại thao tác nhận PAUSE hoặc RESUME; nếu trống sẽ dùng loại mặc định của batch.");
        guide.CreateRow(1).CreateCell(0).SetCellValue("Không cần nhập Terminal ID/Traffic ID/Region nếu hệ thống đã có dữ liệu.");
        using var output = new MemoryStream();
        workbook.Write(output, true);
        return output.ToArray();
    }

    public async Task<IReadOnlyList<long>> ClaimQueuedItemsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var ids = new List<long>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            ;WITH claim AS
            (
                SELECT TOP (@batchSize) i.*
                FROM [dbo].[TblKvhSubscriptionOperationItem] i WITH (UPDLOCK, READPAST, ROWLOCK)
                INNER JOIN [dbo].[TblKvhSubscriptionOperationBatch] b WITH (UPDLOCK, ROWLOCK) ON b.[ID] = i.[BatchId]
                WHERE i.[Status] IN ('QUEUED', 'RETRY_WAIT', 'WAITING_COOLDOWN')
                  AND (i.[NextSubmitAtUtc] IS NULL OR i.[NextSubmitAtUtc] <= SYSUTCDATETIME())
                  AND b.[Status] IN ('QUEUED', 'RUNNING')
                ORDER BY i.[NextSubmitAtUtc], i.[ID]
            )
            UPDATE claim
            SET [Status] = 'SUBMITTING',
                [AttemptCount] = [AttemptCount] + 1,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[ID];

            UPDATE b
            SET [Status] = 'RUNNING', [StartedAtUtc] = COALESCE([StartedAtUtc], SYSUTCDATETIME()), [UpdatedAtUtc] = SYSUTCDATETIME()
            FROM [dbo].[TblKvhSubscriptionOperationBatch] b
            WHERE EXISTS (SELECT 1 FROM [dbo].[TblKvhSubscriptionOperationItem] i WHERE i.[BatchId] = b.[ID] AND i.[Status] = 'SUBMITTING');
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchSize", SqlDbType.Int).Value = Math.Clamp(batchSize, 1, 100);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(Convert.ToInt64(reader[0]));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    public async Task SubmitItemAsync(long itemId, int? userId, string requestedBy, CancellationToken cancellationToken = default)
    {
        OperationItemSubmitContext item;
        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            item = await GetSubmitContextAsync(connection, itemId, cancellationToken);
        }

        if (item.BatchStatus == KvhSubscriptionOperationBatchStatuses.CancelRequested)
        {
            await MarkItemAsync(itemId, KvhSubscriptionOperationItemStatuses.Cancelled, null, null, cancellationToken: cancellationToken);
            return;
        }

        var request = new KvhSolutionCommandRequest { DeviceId = item.DeviceId, KvhSubscriptionId = item.KvhSubscriptionId };
        KvhCommandSubmitResult result;
        try
        {
            result = item.OperationType == KvhSubscriptionOperationTypes.Resume
                ? await kvhSubscriptionService.ResumeAsync(request, userId, requestedBy, item.AllowedTenantId, item.AllowedDeviceId, cancellationToken)
                : await kvhSubscriptionService.PauseAsync(request, userId, requestedBy, item.AllowedTenantId, item.AllowedDeviceId, cancellationToken);
        }
        catch (Exception ex)
        {
            await MarkItemAsync(
                itemId,
                KvhSubscriptionOperationItemStatuses.JobFailed,
                "operation_submit_exception",
                ex.GetBaseException().Message,
                operationLogJson: BuildOperationLog(item, null, requestedBy, "Exception while submitting KVH operation.", ex),
                cancellationToken: cancellationToken);
            logger.LogError(ex, "KVH operation submit exception for item {ItemId}.", itemId);
            return;
        }

        if (result.Success)
        {
            await MarkItemSubmittedAsync(itemId, result, cancellationToken);
            logger.LogInformation("Submitted KVH operation item {ItemId}, command {CommandId}, job {JobId}.", itemId, result.CommandId, result.JobId);
            return;
        }

        if (result.ErrorCode is "kvh_command_cooldown" or "kvh_terminal_command_cooldown" && result.NextAllowedAtUtc.HasValue)
        {
            await MarkItemAsync(
                itemId,
                KvhSubscriptionOperationItemStatuses.WaitingCooldown,
                result.ErrorCode,
                result.MessageEn,
                result.NextAllowedAtUtc,
                result.CommandId,
                result.HttpStatusCode,
                result.RawResponse,
                BuildOperationLog(item, result, requestedBy, "KVH command cooldown."),
                cancellationToken);
            return;
        }

        var retryable = IsRetryable(result.ErrorCode, result.HttpStatusCode) && item.AttemptCount < item.MaxAttemptCount;
        await MarkItemAsync(
            itemId,
            retryable ? KvhSubscriptionOperationItemStatuses.RetryWait : KvhSubscriptionOperationItemStatuses.JobFailed,
            result.ErrorCode,
            result.MessageEn,
            retryable ? DateTime.UtcNow.AddMinutes(2) : null,
            result.CommandId,
            result.HttpStatusCode,
            result.RawResponse,
            BuildOperationLog(item, result, requestedBy, retryable ? "Submit failed, queued for retry." : "Submit failed."),
            cancellationToken);
    }

    public async Task SyncCommandStatusesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string updateSql = """
            UPDATE i
            SET [JobId] = c.[JobId],
                [JobStatus] = c.[JobStatus],
                [VerificationStatus] = c.[VerificationStatus],
                [PollCount] = c.[PollCount],
                [VerificationAttemptCount] = c.[VerificationAttemptCount],
                [ErrorCode] = c.[ErrorCode],
                [ErrorMessage] = c.[ErrorMessage],
                [HttpStatusCode] = c.[HttpStatusCode],
                [SubmitResponseJson] = COALESCE(NULLIF(i.[SubmitResponseJson], ''), c.[SubmitResponseJson]),
                [OperationLogJson] = COALESCE(NULLIF(i.[OperationLogJson], ''), c.[SubmitResponseJson], c.[JobResponseJson], c.[VerificationResponseJson]),
                [JobCompletedAtUtc] = CASE WHEN c.[JobStatus] = 'Failed' THEN COALESCE(i.[JobCompletedAtUtc], c.[CompletedAtUtc]) ELSE i.[JobCompletedAtUtc] END,
                [VerifiedAtUtc] = CASE WHEN c.[CommandStatus] = 'VERIFIED' THEN COALESCE(i.[VerifiedAtUtc], c.[VerifiedAtUtc], SYSUTCDATETIME()) ELSE i.[VerifiedAtUtc] END,
                [Status] = CASE
                    WHEN c.[CommandStatus] = 'VERIFIED' THEN 'VERIFIED'
                    WHEN c.[CommandStatus] = 'VERIFICATION_MISMATCH' THEN 'VERIFICATION_MISMATCH'
                    WHEN c.[CommandStatus] = 'VERIFICATION_TIMEOUT' THEN 'TIMEOUT'
                    WHEN c.[CommandStatus] = 'VERIFYING' THEN 'VERIFYING'
                    WHEN c.[JobStatus] = 'Success' THEN 'JOB_SUCCESS'
                    WHEN c.[JobStatus] = 'Failed' OR c.[CommandStatus] = 'FAILED' THEN 'JOB_FAILED'
                    WHEN c.[CommandStatus] = 'PENDING' OR c.[JobStatus] = 'Pending' THEN 'JOB_PENDING'
                    WHEN c.[CommandStatus] = 'SUBMITTED' THEN 'SUBMITTED'
                    ELSE i.[Status]
                END,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            FROM [dbo].[TblKvhSubscriptionOperationItem] i
            INNER JOIN [dbo].[TblKvhCommand] c ON c.[ID] = i.[KvhCommandId]
            WHERE i.[Status] NOT IN ('VERIFIED', 'JOB_FAILED', 'VERIFICATION_MISMATCH', 'VALIDATION_FAILED', 'SKIPPED', 'CANCELLED', 'TIMEOUT')
            """;
        await using (var command = new SqlCommand(updateSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var batchIds = new List<long>();
        await using (var command = new SqlCommand("SELECT DISTINCT [BatchId] FROM [dbo].[TblKvhSubscriptionOperationItem] WHERE [UpdatedAtUtc] >= DATEADD(minute, -10, SYSUTCDATETIME())", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) batchIds.Add(Convert.ToInt64(reader[0]));
        }

        foreach (var batchId in batchIds)
        {
            await RefreshCountersAsync(connection, batchId, cancellationToken);
        }
    }

    // Helpers
    private static void NormalizeFilter(KvhSubscriptionOperationFilter filter)
    {
        filter.Search = NormalizeNullable(filter.Search);
        filter.OperationType = NormalizeNullable(KvhSubscriptionOperationTypes.Normalize(filter.OperationType));
        filter.Status = NormalizeNullable(filter.Status)?.ToUpperInvariant();
        filter.CreatedBy = NormalizeNullable(filter.CreatedBy);
    }

    private static string BuildBatchWhere(KvhSubscriptionOperationFilter filter, int? allowedTenantId, int? allowedDeviceId)
    {
        var clauses = new List<string>
        {
            "(@allowedTenantId IS NULL OR b.[TenantId] = @allowedTenantId)",
            "(@allowedDeviceId IS NULL OR EXISTS (SELECT 1 FROM [dbo].[TblKvhSubscriptionOperationItem] i WHERE i.[BatchId] = b.[ID] AND i.[DeviceId] = @allowedDeviceId))",
            "(@tenantId IS NULL OR b.[TenantId] = @tenantId)",
            "(@operationType IS NULL OR b.[OperationType] = @operationType)",
            "(@status IS NULL OR b.[Status] = @status)",
            "(@createdBy IS NULL OR b.[RequestedBy] LIKE @createdBy)",
            "(@dateFrom IS NULL OR b.[CreatedAtUtc] >= @dateFrom)",
            "(@dateTo IS NULL OR b.[CreatedAtUtc] < DATEADD(day, 1, @dateTo))"
        };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add("(b.[BatchCode] LIKE @search OR b.[BatchName] LIKE @search OR EXISTS (SELECT 1 FROM [dbo].[TblKvhSubscriptionOperationItem] i WHERE i.[BatchId] = b.[ID] AND i.[KitNumber] LIKE @search))");
        }

        return string.Join(" AND ", clauses);
    }

    private static void AddBatchFilterParameters(SqlCommand command, KvhSubscriptionOperationFilter filter, int? allowedTenantId, int? allowedDeviceId)
    {
        command.Parameters.Add("@allowedTenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)filter.TenantId ?? DBNull.Value;
        command.Parameters.Add("@operationType", SqlDbType.NVarChar, 30).Value = (object?)filter.OperationType ?? DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 40).Value = (object?)filter.Status ?? DBNull.Value;
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 260).Value = string.IsNullOrWhiteSpace(filter.CreatedBy) ? DBNull.Value : $"%{filter.CreatedBy}%";
        command.Parameters.Add("@dateFrom", SqlDbType.DateTime2).Value = (object?)filter.DateFrom ?? DBNull.Value;
        command.Parameters.Add("@dateTo", SqlDbType.DateTime2).Value = (object?)filter.DateTo ?? DBNull.Value;
        if (!string.IsNullOrWhiteSpace(filter.Search)) command.Parameters.Add("@search", SqlDbType.NVarChar, 260).Value = $"%{filter.Search}%";
    }

    private static async Task<int> CountBatchesAsync(SqlConnection connection, string where, KvhSubscriptionOperationFilter filter, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT COUNT(1) FROM [dbo].[TblKvhSubscriptionOperationBatch] b WHERE {where}", connection);
        AddBatchFilterParameters(command, filter, allowedTenantId, allowedDeviceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
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

    private static KvhSubscriptionOperationBatchListItem MapBatchListItem(SqlDataReader reader) => new()
    {
        Id = Convert.ToInt64(reader["ID"]),
        BatchCode = reader["BatchCode"]?.ToString() ?? string.Empty,
        BatchName = reader["BatchName"]?.ToString() ?? string.Empty,
        OperationType = reader["OperationType"]?.ToString() ?? string.Empty,
        Status = reader["Status"]?.ToString() ?? string.Empty,
        TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
        TotalItems = Convert.ToInt32(reader["TotalItems"]),
        DraftItems = Convert.ToInt32(reader["DraftItems"]),
        QueuedItems = Convert.ToInt32(reader["QueuedItems"]),
        SubmittingItems = Convert.ToInt32(reader["SubmittingItems"]),
        PendingItems = Convert.ToInt32(reader["PendingItems"]),
        JobSuccessItems = Convert.ToInt32(reader["JobSuccessItems"]),
        JobFailedItems = Convert.ToInt32(reader["JobFailedItems"]),
        VerifiedItems = Convert.ToInt32(reader["VerifiedItems"]),
        VerificationMismatchItems = Convert.ToInt32(reader["VerificationMismatchItems"]),
        SkippedItems = Convert.ToInt32(reader["SkippedItems"]),
        CancelledItems = Convert.ToInt32(reader["CancelledItems"]),
        RequestedBy = reader["RequestedBy"]?.ToString() ?? string.Empty,
        CreatedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["CreatedAtUtc"]), DateTimeKind.Utc),
        StartedAtUtc = reader["StartedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["StartedAtUtc"]), DateTimeKind.Utc),
        CompletedAtUtc = reader["CompletedAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["CompletedAtUtc"]), DateTimeKind.Utc)
    };

    private static KvhSubscriptionOperationDetailViewModel MapBatchDetail(SqlDataReader reader) => new()
    {
        Id = Convert.ToInt64(reader["ID"]),
        TenantId = reader["TenantId"] == DBNull.Value ? null : Convert.ToInt32(reader["TenantId"]),
        BatchCode = reader["BatchCode"]?.ToString() ?? string.Empty,
        BatchName = reader["BatchName"]?.ToString() ?? string.Empty,
        OperationType = reader["OperationType"]?.ToString() ?? string.Empty,
        Status = reader["Status"]?.ToString() ?? string.Empty,
        TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
        Description = reader["Description"]?.ToString() ?? string.Empty,
        RequestedBy = reader["RequestedBy"]?.ToString() ?? string.Empty,
        CreatedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["CreatedAtUtc"]), DateTimeKind.Utc),
        ScheduledStartAtUtc = reader["ScheduledStartAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["ScheduledStartAtUtc"]), DateTimeKind.Utc),
        TotalItems = Convert.ToInt32(reader["TotalItems"]),
        ReadyItems = Convert.ToInt32(reader["ReadyItems"]),
        QueuedItems = Convert.ToInt32(reader["QueuedItems"]),
        PendingItems = Convert.ToInt32(reader["PendingItems"]),
        JobSuccessItems = Convert.ToInt32(reader["JobSuccessItems"]),
        JobFailedItems = Convert.ToInt32(reader["JobFailedItems"]),
        VerifyingItems = Convert.ToInt32(reader["VerifyingItems"]),
        VerifiedItems = Convert.ToInt32(reader["VerifiedItems"]),
        VerificationMismatchItems = Convert.ToInt32(reader["VerificationMismatchItems"]),
        SkippedItems = Convert.ToInt32(reader["SkippedItems"]),
        CancelledItems = Convert.ToInt32(reader["CancelledItems"])
    };

    private static async Task<IReadOnlyList<KvhSubscriptionOperationItemViewModel>> GetItemsAsync(SqlConnection connection, long batchId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT i.*, ISNULL(d.[DeviceName], '') AS [DeviceName], ISNULL(d.[VesselName], '') AS [VesselName],
                   ISNULL(t.[TenantName], '') AS [TenantName], ISNULL(s.[Status], '') AS [SubscriptionStatus], ISNULL(s.[ScheduledAction], '') AS [ScheduledAction],
                   c.[RequestJson], c.[SubmitResponseJson] AS [CommandSubmitResponseJson], c.[JobResponseJson], c.[VerificationResponseJson]
            FROM [dbo].[TblKvhSubscriptionOperationItem] i
            LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = i.[DeviceId]
            LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = d.[TenantID]
            LEFT JOIN [dbo].[TblKvhSubscription] s ON s.[ID] = i.[KvhSubscriptionId]
            LEFT JOIN [dbo].[TblKvhCommand] c ON c.[ID] = i.[KvhCommandId]
            WHERE i.[BatchId] = @batchId
            ORDER BY i.[ID]
            """;
        var items = new List<KvhSubscriptionOperationItemViewModel>();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new KvhSubscriptionOperationItemViewModel
            {
                Id = Convert.ToInt64(reader["ID"]),
                DeviceId = reader["DeviceId"] == DBNull.Value ? null : Convert.ToInt32(reader["DeviceId"]),
                DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
                VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
                TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
                KitNumber = reader["KitNumber"]?.ToString() ?? string.Empty,
                TerminalId = reader["TerminalId"]?.ToString() ?? string.Empty,
                TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
                Region = reader["Region"]?.ToString() ?? string.Empty,
                SubscriptionStatus = reader["SubscriptionStatus"]?.ToString() ?? string.Empty,
                ScheduledAction = reader["ScheduledAction"]?.ToString() ?? string.Empty,
                OperationType = reader["OperationType"]?.ToString() ?? string.Empty,
                Status = reader["Status"]?.ToString() ?? string.Empty,
                KvhCommandId = reader["KvhCommandId"] == DBNull.Value ? null : Convert.ToInt64(reader["KvhCommandId"]),
                JobId = reader["JobId"]?.ToString() ?? string.Empty,
                JobStatus = reader["JobStatus"]?.ToString() ?? string.Empty,
                VerificationStatus = reader["VerificationStatus"]?.ToString() ?? string.Empty,
                AttemptCount = Convert.ToInt32(reader["AttemptCount"]),
                PollCount = Convert.ToInt32(reader["PollCount"]),
                UpdatedAtUtc = DateTime.SpecifyKind(Convert.ToDateTime(reader["UpdatedAtUtc"]), DateTimeKind.Utc),
                ErrorCode = reader["ErrorCode"]?.ToString() ?? string.Empty,
                ErrorMessage = reader["ErrorMessage"]?.ToString() ?? string.Empty,
                HttpStatusCode = reader["HttpStatusCode"] == DBNull.Value ? null : Convert.ToInt32(reader["HttpStatusCode"]),
                SubmitResponseJson = reader["SubmitResponseJson"]?.ToString() ?? string.Empty,
                RequestJson = reader["RequestJson"]?.ToString() ?? string.Empty,
                CommandSubmitResponseJson = reader["CommandSubmitResponseJson"]?.ToString() ?? string.Empty,
                JobResponseJson = reader["JobResponseJson"]?.ToString() ?? string.Empty,
                VerificationResponseJson = reader["VerificationResponseJson"]?.ToString() ?? string.Empty,
                OperationLogJson = reader["OperationLogJson"]?.ToString() ?? string.Empty
            });
        }
        return items;
    }

    private static async Task<IReadOnlyList<KvhSubscriptionOperationDeviceOption>> GetDeviceOptionsAsync(SqlConnection connection, int? tenantId, int? deviceId, long batchId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 200 d.[ID], d.[DeviceName], d.[VesselName], d.[DeviceCode], d.[KITNumber], d.[Availability], ISNULL(t.[TenantName], '') AS [TenantName],
                   s.[TrafficId], s.[Region], s.[Status], s.[ScheduledAction]
            FROM [dbo].[TblDevices] d
            LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = d.[TenantID]
            LEFT JOIN [dbo].[TblKvhSubscription] s ON s.[DeviceId] = d.[ID] AND s.[IsCurrent] = 1
            WHERE NULLIF(LTRIM(RTRIM(ISNULL(d.[KITNumber], ''))), '') IS NOT NULL
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
              AND NOT EXISTS (SELECT 1 FROM [dbo].[TblKvhSubscriptionOperationItem] i WHERE i.[BatchId] = @batchId AND i.[DeviceId] = d.[ID])
            ORDER BY d.[VesselName], d.[KITNumber]
            """;
        var items = new List<KvhSubscriptionOperationDeviceOption>();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new KvhSubscriptionOperationDeviceOption
            {
                DeviceId = Convert.ToInt32(reader["ID"]),
                DeviceName = reader["DeviceName"]?.ToString() ?? string.Empty,
                VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
                TenantName = reader["TenantName"]?.ToString() ?? string.Empty,
                KitNumber = reader["KITNumber"]?.ToString() ?? string.Empty,
                TerminalId = reader["DeviceCode"]?.ToString() ?? string.Empty,
                TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
                Region = reader["Region"]?.ToString() ?? string.Empty,
                SubscriptionStatus = reader["Status"]?.ToString() ?? string.Empty,
                ScheduledAction = reader["ScheduledAction"]?.ToString() ?? string.Empty,
                Availability = reader["Availability"]?.ToString() ?? string.Empty
            });
        }
        return items;
    }

    private async Task<BatchHeader> GetBatchHeaderAsync(SqlConnection connection, SqlTransaction? transaction, long batchId, int? allowedTenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 *
            FROM [dbo].[TblKvhSubscriptionOperationBatch] b
            WHERE b.[ID] = @batchId
              AND (@tenantId IS NULL OR b.[TenantId] = @tenantId)
              AND (@deviceId IS NULL OR EXISTS (SELECT 1 FROM [dbo].[TblKvhSubscriptionOperationItem] i WHERE i.[BatchId] = b.[ID] AND i.[DeviceId] = @deviceId))
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)allowedTenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Không tìm thấy đợt hoặc bạn không có quyền.");
        return new BatchHeader
        {
            Id = batchId,
            OperationType = reader["OperationType"]?.ToString() ?? string.Empty,
            Status = reader["Status"]?.ToString() ?? string.Empty,
            TenantId = reader["TenantId"] == DBNull.Value ? null : Convert.ToInt32(reader["TenantId"]),
            ScheduledStartAtUtc = reader["ScheduledStartAtUtc"] == DBNull.Value ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader["ScheduledStartAtUtc"]), DateTimeKind.Utc)
        };
    }

    private static void EnsureDraft(BatchHeader batch)
    {
        if (batch.Status != KvhSubscriptionOperationBatchStatuses.Draft)
        {
            throw new InvalidOperationException("Chỉ có thể chỉnh sửa đợt ở trạng thái nháp.");
        }
    }

    private static async Task<IReadOnlyList<DeviceSnapshot>> FindDevicesByIdsAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<int> deviceIds, int? tenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        var names = deviceIds.Distinct().Select((_, index) => $"@id{index}").ToArray();
        if (names.Length == 0) return [];
        var sql = $"""
            SELECT d.[ID], d.[KITNumber], d.[DeviceCode], s.[TrafficId], s.[Region], s.[ID] AS [KvhSubscriptionId], s.[Status], s.[ScheduledAction]
            FROM [dbo].[TblDevices] d
            LEFT JOIN [dbo].[TblKvhSubscription] s ON s.[DeviceId] = d.[ID] AND s.[IsCurrent] = 1
            WHERE d.[ID] IN ({string.Join(",", names)})
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
            """;
        var items = new List<DeviceSnapshot>();
        await using var command = new SqlCommand(sql, connection, transaction);
        for (var i = 0; i < names.Length; i++) command.Parameters.Add(names[i], SqlDbType.Int).Value = deviceIds.Distinct().ElementAt(i);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(MapSnapshot(reader));
        return items;
    }

    private static async Task<Dictionary<string, DeviceSnapshot>> FindDevicesByKitsAsync(SqlConnection connection, IReadOnlyList<string> kits, int? tenantId, int? allowedDeviceId, CancellationToken cancellationToken)
    {
        var normalized = kits.Select(NormalizeKit).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length == 0) return new(StringComparer.OrdinalIgnoreCase);
        var names = normalized.Select((_, index) => $"@kit{index}").ToArray();
        var sql = $"""
            SELECT d.[ID], d.[KITNumber], d.[DeviceCode], d.[VesselName], ISNULL(t.[TenantName], '') AS [TenantName],
                   s.[TrafficId], s.[Region], s.[ID] AS [KvhSubscriptionId], s.[Status], s.[ScheduledAction]
            FROM [dbo].[TblDevices] d
            LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = d.[TenantID]
            LEFT JOIN [dbo].[TblKvhSubscription] s ON s.[DeviceId] = d.[ID] AND s.[IsCurrent] = 1
            WHERE UPPER(LTRIM(RTRIM(d.[KITNumber]))) IN ({string.Join(",", names)})
              AND (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)
            """;
        var result = new Dictionary<string, DeviceSnapshot>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection);
        for (var i = 0; i < names.Length; i++) command.Parameters.Add(names[i], SqlDbType.NVarChar, 200).Value = normalized[i];
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@allowedDeviceId", SqlDbType.Int).Value = (object?)allowedDeviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = MapSnapshot(reader);
            result[NormalizeKit(snapshot.KitNumber)] = snapshot;
        }
        return result;
    }

    private static DeviceSnapshot MapSnapshot(SqlDataReader reader) => new()
    {
        DeviceId = Convert.ToInt32(reader["ID"]),
        KitNumber = reader["KITNumber"]?.ToString() ?? string.Empty,
        TerminalId = reader["DeviceCode"]?.ToString() ?? string.Empty,
        TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
        Region = reader["Region"]?.ToString() ?? string.Empty,
        KvhSubscriptionId = reader["KvhSubscriptionId"] == DBNull.Value ? null : Convert.ToInt64(reader["KvhSubscriptionId"]),
        SubscriptionStatus = reader["Status"]?.ToString() ?? string.Empty,
        ScheduledAction = reader["ScheduledAction"]?.ToString() ?? string.Empty,
        VesselName = HasColumn(reader, "VesselName") ? reader["VesselName"]?.ToString() ?? string.Empty : string.Empty,
        TenantName = HasColumn(reader, "TenantName") ? reader["TenantName"]?.ToString() ?? string.Empty : string.Empty
    };

    private static async Task<bool> InsertItemIfNotExistsAsync(SqlConnection connection, SqlTransaction transaction, long batchId, DeviceSnapshot device, string operationType, int? rowNumber, string source, string? note, CancellationToken cancellationToken)
    {
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[TblKvhSubscriptionOperationItem] WHERE [BatchId] = @batchId AND [KitNumberNormalized] = UPPER(LTRIM(RTRIM(@kit))))
            BEGIN
                INSERT INTO [dbo].[TblKvhSubscriptionOperationItem]
                    ([BatchId], [DeviceId], [KitNumber], [TerminalId], [TrafficId], [Region], [KvhSubscriptionId], [OperationType], [Status], [ImportedRowNumber], [ImportSource], [Note], [CreatedAtUtc], [UpdatedAtUtc])
                VALUES (@batchId, @deviceId, @kit, @terminalId, @trafficId, @region, @subscriptionId, @operationType, 'DRAFT', @rowNumber, @source, @note, SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT 1;
            END
            ELSE SELECT 0;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = device.DeviceId;
        command.Parameters.Add("@kit", SqlDbType.NVarChar, 200).Value = device.KitNumber;
        command.Parameters.Add("@terminalId", SqlDbType.NVarChar, 200).Value = Db(device.TerminalId);
        command.Parameters.Add("@trafficId", SqlDbType.NVarChar, 200).Value = Db(device.TrafficId);
        command.Parameters.Add("@region", SqlDbType.NVarChar, 100).Value = Db(device.Region);
        command.Parameters.Add("@subscriptionId", SqlDbType.BigInt).Value = (object?)device.KvhSubscriptionId ?? DBNull.Value;
        command.Parameters.Add("@operationType", SqlDbType.NVarChar, 30).Value = operationType;
        command.Parameters.Add("@rowNumber", SqlDbType.Int).Value = (object?)rowNumber ?? DBNull.Value;
        command.Parameters.Add("@source", SqlDbType.NVarChar, 50).Value = source;
        command.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = Db(note);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
    }

    private static async Task<HashSet<string>> GetExistingKitSetAsync(SqlConnection connection, long batchId, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand("SELECT [KitNumberNormalized] FROM [dbo].[TblKvhSubscriptionOperationItem] WHERE [BatchId] = @batchId", connection);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) set.Add(reader[0]?.ToString() ?? string.Empty);
        return set;
    }

    private static void ValidateImportRowShape(KvhSubscriptionOperationImportRow row, HashSet<string> existingKits, HashSet<string> fileKitSet)
    {
        if (string.IsNullOrWhiteSpace(row.KitNumber))
        {
            row.IsValid = false;
            row.Message = "Thiếu KIT Number.";
            return;
        }

        if (string.IsNullOrWhiteSpace(row.OperationType))
        {
            row.IsValid = false;
            row.Message = "Loại thao tác không hợp lệ.";
            return;
        }

        var normalized = NormalizeKit(row.KitNumber);
        if (existingKits.Contains(normalized) || !fileKitSet.Add(normalized))
        {
            row.IsDuplicate = true;
            row.IsValid = false;
            row.Message = "Trùng KIT trong file hoặc batch.";
            return;
        }

        row.IsValid = true;
    }

    private static void ValidateOperationState(KvhSubscriptionOperationImportRow row, string status, string scheduledAction, string trafficId, string region, long? subscriptionId)
    {
        var error = ValidateItem(row.OperationType, new ValidationItem
        {
            DeviceId = row.DeviceId,
            TrafficId = trafficId,
            Region = region,
            KvhSubscriptionId = subscriptionId,
            SubscriptionStatus = status,
            ScheduledAction = scheduledAction
        });
        if (!string.IsNullOrWhiteSpace(error))
        {
            row.IsValid = false;
            row.Message = AppendMessage(row.Message, error);
        }
    }

    private static async Task<List<ValidationItem>> GetValidationItemsAsync(SqlConnection connection, SqlTransaction transaction, long batchId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT i.[ID], d.[ID] AS [ResolvedDeviceId], d.[DeviceCode], d.[KITNumber],
                   s.[ID] AS [ResolvedSubscriptionId], s.[TrafficId], s.[Region], s.[Status], s.[ScheduledAction],
                   i.[OperationType]
            FROM [dbo].[TblKvhSubscriptionOperationItem] i
            LEFT JOIN [dbo].[TblDevices] d ON UPPER(LTRIM(RTRIM(d.[KITNumber]))) = i.[KitNumberNormalized]
            LEFT JOIN [dbo].[TblKvhSubscription] s ON s.[DeviceId] = d.[ID] AND s.[IsCurrent] = 1
            WHERE i.[BatchId] = @batchId
            ORDER BY i.[ID]
            """;
        var items = new List<ValidationItem>();
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ValidationItem
            {
                Id = Convert.ToInt64(reader["ID"]),
                DeviceId = reader["ResolvedDeviceId"] == DBNull.Value ? null : Convert.ToInt32(reader["ResolvedDeviceId"]),
                TerminalId = reader["DeviceCode"]?.ToString() ?? string.Empty,
                TrafficId = reader["TrafficId"]?.ToString() ?? string.Empty,
                Region = reader["Region"]?.ToString() ?? string.Empty,
                KvhSubscriptionId = reader["ResolvedSubscriptionId"] == DBNull.Value ? null : Convert.ToInt64(reader["ResolvedSubscriptionId"]),
                OperationType = reader["OperationType"]?.ToString() ?? string.Empty,
                SubscriptionStatus = reader["Status"]?.ToString() ?? string.Empty,
                ScheduledAction = reader["ScheduledAction"]?.ToString() ?? string.Empty
            });
        }
        return items;
    }

    private static string ValidateItem(string batchOperationType, ValidationItem item)
    {
        var operation = KvhSubscriptionOperationTypes.Normalize(string.IsNullOrWhiteSpace(item.OperationType) ? batchOperationType : item.OperationType);
        if (!item.DeviceId.HasValue) return "Không tìm thấy thiết bị.";
        if (string.IsNullOrWhiteSpace(item.TerminalId)) return "Thiếu Terminal ID.";
        if (string.IsNullOrWhiteSpace(item.TrafficId)) return "Thiếu Traffic ID.";
        if (string.IsNullOrWhiteSpace(item.Region)) return "Thiếu Region.";
        if (!item.KvhSubscriptionId.HasValue) return "Không tìm thấy current subscription.";
        if (operation == KvhSubscriptionOperationTypes.Pause)
        {
            if (!item.SubscriptionStatus.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)) return "PAUSE chỉ hợp lệ khi subscription ACTIVE.";
            if (item.ScheduledAction.Contains("pause", StringComparison.OrdinalIgnoreCase) || item.ScheduledAction.Contains("suspend", StringComparison.OrdinalIgnoreCase)) return "Đã có lịch Pause/Suspend.";
        }
        else if (operation == KvhSubscriptionOperationTypes.Resume)
        {
            if (item.SubscriptionStatus.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)) return "Subscription đang ACTIVE, không gửi Resume.";
            if (!item.SubscriptionStatus.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) && !item.SubscriptionStatus.Contains("PAUSE", StringComparison.OrdinalIgnoreCase)) return "RESUME chỉ hợp lệ khi subscription đang tạm dừng.";
        }
        else
        {
            return "Loại thao tác không hợp lệ.";
        }

        return string.Empty;
    }

    private static async Task MarkBatchStatusAsync(SqlConnection connection, SqlTransaction transaction, long batchId, string status, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("UPDATE [dbo].[TblKvhSubscriptionOperationBatch] SET [Status] = @status, [UpdatedAtUtc] = SYSUTCDATETIME() WHERE [ID] = @id", connection, transaction);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = batchId;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 40).Value = status;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshCountersAsync(SqlConnection connection, long batchId, CancellationToken cancellationToken) =>
        await RefreshCountersAsync(connection, batchId, null, cancellationToken);

    private static async Task RefreshCountersAsync(SqlConnection connection, long batchId, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH c AS
            (
                SELECT [BatchId],
                    COUNT(1) AS TotalItems,
                    SUM(CASE WHEN [Status] = 'DRAFT' THEN 1 ELSE 0 END) AS DraftItems,
                    SUM(CASE WHEN [Status] = 'READY' THEN 1 ELSE 0 END) AS ReadyItems,
                    SUM(CASE WHEN [Status] = 'QUEUED' THEN 1 ELSE 0 END) AS QueuedItems,
                    SUM(CASE WHEN [Status] = 'SUBMITTING' THEN 1 ELSE 0 END) AS SubmittingItems,
                    SUM(CASE WHEN [Status] IN ('SUBMITTED','JOB_PENDING','WAITING_COOLDOWN','RETRY_WAIT') THEN 1 ELSE 0 END) AS PendingItems,
                    SUM(CASE WHEN [Status] = 'JOB_SUCCESS' THEN 1 ELSE 0 END) AS JobSuccessItems,
                    SUM(CASE WHEN [Status] = 'JOB_FAILED' THEN 1 ELSE 0 END) AS JobFailedItems,
                    SUM(CASE WHEN [Status] = 'VERIFYING' THEN 1 ELSE 0 END) AS VerifyingItems,
                    SUM(CASE WHEN [Status] = 'VERIFIED' THEN 1 ELSE 0 END) AS VerifiedItems,
                    SUM(CASE WHEN [Status] = 'VERIFICATION_MISMATCH' THEN 1 ELSE 0 END) AS VerificationMismatchItems,
                    SUM(CASE WHEN [Status] = 'SKIPPED' THEN 1 ELSE 0 END) AS SkippedItems,
                    SUM(CASE WHEN [Status] = 'CANCELLED' THEN 1 ELSE 0 END) AS CancelledItems,
                    SUM(CASE WHEN [Status] IN ('VERIFIED','JOB_FAILED','VERIFICATION_MISMATCH','VALIDATION_FAILED','SKIPPED','CANCELLED','TIMEOUT') THEN 1 ELSE 0 END) AS TerminalItems,
                    SUM(CASE WHEN [Status] IN ('JOB_FAILED','VERIFICATION_MISMATCH','VALIDATION_FAILED','TIMEOUT') THEN 1 ELSE 0 END) AS ErrorItems
                FROM [dbo].[TblKvhSubscriptionOperationItem]
                WHERE [BatchId] = @batchId
                GROUP BY [BatchId]
            )
            UPDATE b
            SET [TotalItems] = ISNULL(c.[TotalItems], 0),
                [DraftItems] = ISNULL(c.[DraftItems], 0),
                [ReadyItems] = ISNULL(c.[ReadyItems], 0),
                [QueuedItems] = ISNULL(c.[QueuedItems], 0),
                [SubmittingItems] = ISNULL(c.[SubmittingItems], 0),
                [PendingItems] = ISNULL(c.[PendingItems], 0),
                [JobSuccessItems] = ISNULL(c.[JobSuccessItems], 0),
                [JobFailedItems] = ISNULL(c.[JobFailedItems], 0),
                [VerifyingItems] = ISNULL(c.[VerifyingItems], 0),
                [VerifiedItems] = ISNULL(c.[VerifiedItems], 0),
                [VerificationMismatchItems] = ISNULL(c.[VerificationMismatchItems], 0),
                [SkippedItems] = ISNULL(c.[SkippedItems], 0),
                [CancelledItems] = ISNULL(c.[CancelledItems], 0),
                [Status] = CASE
                    WHEN b.[Status] = 'CANCEL_REQUESTED' AND ISNULL(c.[TerminalItems], 0) = ISNULL(c.[TotalItems], 0) THEN 'CANCELLED'
                    WHEN b.[Status] IN ('QUEUED','RUNNING','VERIFYING','CANCEL_REQUESTED') AND ISNULL(c.[TotalItems], 0) > 0 AND ISNULL(c.[TerminalItems], 0) = ISNULL(c.[TotalItems], 0)
                        THEN CASE WHEN ISNULL(c.[ErrorItems], 0) > 0 THEN 'COMPLETED_WITH_ERRORS' ELSE 'COMPLETED' END
                    WHEN b.[Status] IN ('QUEUED','RUNNING') AND ISNULL(c.[VerifyingItems], 0) + ISNULL(c.[JobSuccessItems], 0) > 0 THEN 'VERIFYING'
                    ELSE b.[Status]
                END,
                [CompletedAtUtc] = CASE
                    WHEN b.[Status] IN ('QUEUED','RUNNING','VERIFYING','CANCEL_REQUESTED') AND ISNULL(c.[TotalItems], 0) > 0 AND ISNULL(c.[TerminalItems], 0) = ISNULL(c.[TotalItems], 0)
                    THEN COALESCE(b.[CompletedAtUtc], SYSUTCDATETIME()) ELSE b.[CompletedAtUtc] END,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            FROM [dbo].[TblKvhSubscriptionOperationBatch] b
            LEFT JOIN c ON c.[BatchId] = b.[ID]
            WHERE b.[ID] = @batchId
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = batchId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<OperationItemSubmitContext> GetSubmitContextAsync(SqlConnection connection, long itemId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT i.*, b.[Status] AS [BatchStatus], b.[TenantId]
            FROM [dbo].[TblKvhSubscriptionOperationItem] i
            INNER JOIN [dbo].[TblKvhSubscriptionOperationBatch] b ON b.[ID] = i.[BatchId]
            WHERE i.[ID] = @itemId
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@itemId", SqlDbType.BigInt).Value = itemId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Không tìm thấy item.");
        return new OperationItemSubmitContext
        {
            Id = itemId,
            BatchId = Convert.ToInt64(reader["BatchId"]),
            BatchStatus = reader["BatchStatus"]?.ToString() ?? string.Empty,
            DeviceId = Convert.ToInt32(reader["DeviceId"]),
            KvhSubscriptionId = Convert.ToInt64(reader["KvhSubscriptionId"]),
            OperationType = reader["OperationType"]?.ToString() ?? string.Empty,
            AttemptCount = Convert.ToInt32(reader["AttemptCount"]),
            MaxAttemptCount = Convert.ToInt32(reader["MaxAttemptCount"]),
            AllowedTenantId = reader["TenantId"] == DBNull.Value ? null : Convert.ToInt32(reader["TenantId"]),
            AllowedDeviceId = null
        };
    }

    private async Task MarkItemSubmittedAsync(long itemId, KvhCommandSubmitResult result, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE [dbo].[TblKvhSubscriptionOperationItem]
            SET [Status] = 'JOB_PENDING', [KvhCommandId] = @commandId, [JobId] = @jobId, [JobStatus] = @jobStatus,
                [SubmittedAtUtc] = SYSUTCDATETIME(), [NextPollAtUtc] = DATEADD(second, @pollSeconds, SYSUTCDATETIME()), [UpdatedAtUtc] = SYSUTCDATETIME(),
                [HttpStatusCode] = @httpStatusCode, [SubmitResponseJson] = NULLIF(@submitResponse, ''),
                [ErrorCode] = NULL, [ErrorMessage] = NULL
            WHERE [ID] = @id
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
        command.Parameters.Add("@commandId", SqlDbType.BigInt).Value = (object?)result.CommandId ?? DBNull.Value;
        command.Parameters.Add("@jobId", SqlDbType.NVarChar, 200).Value = Db(result.JobId);
        command.Parameters.Add("@jobStatus", SqlDbType.NVarChar, 40).Value = KvhJobStatuses.Submitted;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)result.HttpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@submitResponse", SqlDbType.NVarChar, -1).Value = result.RawResponse ?? string.Empty;
        command.Parameters.Add("@pollSeconds", SqlDbType.Int).Value = Math.Max(120, options.Value.JobPollIntervalSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkItemAsync(
        long itemId,
        string status,
        string? errorCode,
        string? errorMessage,
        DateTime? nextSubmitAtUtc = null,
        long? commandId = null,
        int? httpStatusCode = null,
        string? submitResponseJson = null,
        string? operationLogJson = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE [dbo].[TblKvhSubscriptionOperationItem]
            SET [Status] = @status, [ErrorCode] = NULLIF(@errorCode, ''), [ErrorMessage] = NULLIF(@errorMessage, ''),
                [NextSubmitAtUtc] = @nextSubmit,
                [KvhCommandId] = COALESCE(@commandId, [KvhCommandId]),
                [HttpStatusCode] = COALESCE(@httpStatusCode, [HttpStatusCode]),
                [SubmitResponseJson] = COALESCE(NULLIF(@submitResponse, ''), [SubmitResponseJson]),
                [OperationLogJson] = COALESCE(NULLIF(@operationLog, ''), [OperationLogJson]),
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @id
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 40).Value = status;
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode ?? string.Empty;
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = errorMessage ?? string.Empty;
        command.Parameters.Add("@nextSubmit", SqlDbType.DateTime2).Value = (object?)nextSubmitAtUtc ?? DBNull.Value;
        command.Parameters.Add("@commandId", SqlDbType.BigInt).Value = (object?)commandId ?? DBNull.Value;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)httpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@submitResponse", SqlDbType.NVarChar, -1).Value = submitResponseJson ?? string.Empty;
        command.Parameters.Add("@operationLog", SqlDbType.NVarChar, -1).Value = operationLogJson ?? string.Empty;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildOperationLog(OperationItemSubmitContext item, KvhCommandSubmitResult? result, string requestedBy, string message, Exception? exception = null)
    {
        return JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTime.UtcNow,
            message,
            requestedBy,
            item = new
            {
                item.Id,
                item.BatchId,
                item.DeviceId,
                item.KvhSubscriptionId,
                item.OperationType,
                item.AttemptCount,
                item.MaxAttemptCount,
                item.AllowedTenantId,
                item.AllowedDeviceId
            },
            result = result is null
                ? null
                : new
                {
                    result.Success,
                    result.ErrorCode,
                    result.Message,
                    result.MessageEn,
                    result.CommandId,
                    result.JobId,
                    result.HttpStatusCode,
                    result.RawResponse,
                    result.NextAllowedAtUtc
                },
            exception = exception is null
                ? null
                : new
                {
                    type = exception.GetType().FullName,
                    message = exception.GetBaseException().Message,
                    stackTrace = exception.ToString()
                }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool IsRetryable(string errorCode, int? httpStatusCode) =>
        httpStatusCode is 429 or >= 500 ||
        errorCode.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        errorCode.Contains("network", StringComparison.OrdinalIgnoreCase) ||
        errorCode.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static async Task InsertAuditAsync(SqlConnection connection, SqlTransaction transaction, long? batchId, long? itemId, string action, int? userId, string requestedBy, string message, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[TblKvhSubscriptionOperationAudit] ([BatchId], [ItemId], [Action], [PerformedByUserId], [PerformedBy], [Message], [CreatedAtUtc])
            VALUES (@batchId, @itemId, @action, @userId, @performedBy, @message, SYSUTCDATETIME())
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchId", SqlDbType.BigInt).Value = (object?)batchId ?? DBNull.Value;
        command.Parameters.Add("@itemId", SqlDbType.BigInt).Value = (object?)itemId ?? DBNull.Value;
        command.Parameters.Add("@action", SqlDbType.NVarChar, 80).Value = action;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@performedBy", SqlDbType.NVarChar, 250).Value = NormalizeUser(requestedBy);
        command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = message;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadCell(IRow row, int index) => row.GetCell(index)?.ToString()?.Trim() ?? string.Empty;
    private static string NormalizeKit(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    private static string NormalizeUser(string value) => string.IsNullOrWhiteSpace(value) ? "system" : value.Trim();
    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string AppendMessage(string current, string message) => string.IsNullOrWhiteSpace(current) ? message : current + " " + message;
    private static object Db(object? value) => value switch { null => DBNull.Value, string text when string.IsNullOrWhiteSpace(text) => DBNull.Value, _ => value };

    private static bool HasColumn(IDataRecord reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private sealed class BatchHeader
    {
        public long Id { get; set; }
        public string OperationType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int? TenantId { get; set; }
        public DateTime? ScheduledStartAtUtc { get; set; }
    }

    private sealed class DeviceSnapshot
    {
        public int DeviceId { get; set; }
        public string KitNumber { get; set; } = string.Empty;
        public string TerminalId { get; set; } = string.Empty;
        public string TrafficId { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public long? KvhSubscriptionId { get; set; }
        public string SubscriptionStatus { get; set; } = string.Empty;
        public string ScheduledAction { get; set; } = string.Empty;
        public string VesselName { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
    }

    private sealed class ValidationItem
    {
        public long Id { get; set; }
        public int? DeviceId { get; set; }
        public string TerminalId { get; set; } = string.Empty;
        public string TrafficId { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public long? KvhSubscriptionId { get; set; }
        public string OperationType { get; set; } = string.Empty;
        public string SubscriptionStatus { get; set; } = string.Empty;
        public string ScheduledAction { get; set; } = string.Empty;
    }

    private sealed class OperationItemSubmitContext
    {
        public long Id { get; set; }
        public long BatchId { get; set; }
        public string BatchStatus { get; set; } = string.Empty;
        public int DeviceId { get; set; }
        public long KvhSubscriptionId { get; set; }
        public string OperationType { get; set; } = string.Empty;
        public int AttemptCount { get; set; }
        public int MaxAttemptCount { get; set; }
        public int? AllowedTenantId { get; set; }
        public int? AllowedDeviceId { get; set; }
    }
}
