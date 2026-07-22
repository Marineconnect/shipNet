using System.Data;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class InvoiceIntegrationLogService(IConfiguration configuration) : IInvoiceIntegrationLogService
{
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");

    public async Task<long> WriteAsync(InvoiceIntegrationLogEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        const string query = """
            INSERT INTO [dbo].[TblInvoiceIntegrationLog]
                ([InvoiceId], [InvoiceCode], [TransactionCode], [EventType], [Direction], [Status], [SourceSystem], [TargetSystem],
                 [RabbitExchange], [RabbitRoutingKey], [RabbitQueue], [MessageId], [CorrelationId], [PayloadJson],
                 [FileOriginalName], [FileStoredName], [FileSize], [FileVersion], [HttpStatusCode],
                 [ErrorCode], [ErrorMessage], [StartedAtUtc], [CompletedAtUtc], [DurationMs], [CreatedAtUtc], [CreatedBy])
            OUTPUT INSERTED.[ID]
            VALUES
                (@invoiceId, @invoiceCode, @transactionCode, @eventType, @direction, @status, @sourceSystem, @targetSystem,
                 @rabbitExchange, @rabbitRoutingKey, @rabbitQueue, @messageId, @correlationId, @payloadJson,
                 @fileOriginalName, @fileStoredName, @fileSize, @fileVersion, @httpStatusCode,
                 @errorCode, @errorMessage, @startedAtUtc, @completedAtUtc, @durationMs, SYSUTCDATETIME(), @createdBy);
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        AddParameters(command, entry);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<IReadOnlyList<InvoiceIntegrationLogListItem>> GetLogsAsync(string invoiceCode, int page, int pageSize, string eventType = "", int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        var invoice = await ResolveInvoiceIdentityAsync(connection, transaction, invoiceCode, tenantId, deviceId, cancellationToken);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);
        var offset = (page - 1) * pageSize;

        const string query = """
            SELECT [ID], [InvoiceCode], [TransactionCode], [EventType], [Direction], [Status], [SourceSystem], [TargetSystem],
                   [MessageId], [CorrelationId], [FileOriginalName], [FileStoredName], [FileSize], [FileVersion],
                   [ErrorCode], [ErrorMessage], [CreatedAtUtc],
                   CASE WHEN NULLIF([PayloadJson], N'') IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS [HasPayload]
            FROM [dbo].[TblInvoiceIntegrationLog]
            WHERE ([InvoiceId] = @invoiceId OR [InvoiceCode] IN (@invoiceCode, @invoiceNumber, @generatedInvoiceCode))
              AND (@eventType = N'' OR [EventType] = @eventType)
            ORDER BY [CreatedAtUtc] DESC, [ID] DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoice.InvoiceId;
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = invoice.RequestedCode;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoice.InvoiceNumber;
        command.Parameters.Add("@generatedInvoiceCode", SqlDbType.NVarChar, 100).Value = invoice.GeneratedInvoiceCode;
        command.Parameters.Add("@eventType", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(eventType) ? string.Empty : eventType.Trim();
        command.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
        command.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;

        var items = new List<InvoiceIntegrationLogListItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var fileSize = ReadNullableLong(reader, "FileSize");
                items.Add(new InvoiceIntegrationLogListItem
                {
                    Id = ReadLong(reader, "ID"),
                    InvoiceCode = ReadText(reader, "InvoiceCode"),
                    TransactionCode = ReadText(reader, "TransactionCode"),
                    EventType = ReadText(reader, "EventType"),
                    Direction = ReadText(reader, "Direction"),
                    Status = ReadText(reader, "Status"),
                    SourceSystem = ReadText(reader, "SourceSystem"),
                    TargetSystem = ReadText(reader, "TargetSystem"),
                    MessageId = ReadText(reader, "MessageId"),
                    CorrelationId = ReadText(reader, "CorrelationId"),
                    FileName = FirstNotEmpty(ReadText(reader, "FileStoredName"), ReadText(reader, "FileOriginalName")),
                    FileSizeDisplay = fileSize.HasValue ? FormatBytes(fileSize.Value) : string.Empty,
                    FileVersion = ReadNullableInt(reader, "FileVersion"),
                    ErrorCode = ReadText(reader, "ErrorCode"),
                    ErrorMessage = ReadText(reader, "ErrorMessage"),
                    CreatedAtUtc = ReadDate(reader, "CreatedAtUtc") ?? DateTime.MinValue,
                    HasPayload = reader["HasPayload"] != DBNull.Value && Convert.ToBoolean(reader["HasPayload"], CultureInfo.InvariantCulture)
                });
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    public async Task<InvoiceIntegrationLogEntry?> GetLogDetailAsync(string invoiceCode, long logId, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        var invoice = await ResolveInvoiceIdentityAsync(connection, transaction, invoiceCode, tenantId, deviceId, cancellationToken);

        const string query = """
            SELECT TOP 1 *
            FROM [dbo].[TblInvoiceIntegrationLog]
            WHERE [ID] = @logId
              AND ([InvoiceId] = @invoiceId OR [InvoiceCode] IN (@invoiceCode, @invoiceNumber, @generatedInvoiceCode));
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@logId", SqlDbType.BigInt).Value = logId;
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoice.InvoiceId;
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = invoice.RequestedCode;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoice.InvoiceNumber;
        command.Parameters.Add("@generatedInvoiceCode", SqlDbType.NVarChar, 100).Value = invoice.GeneratedInvoiceCode;
        InvoiceIntegrationLogEntry? entry = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            entry = await reader.ReadAsync(cancellationToken) ? MapEntry(reader) : null;
        }
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<InvoiceLogIdentity> ResolveInvoiceIdentityAsync(SqlConnection connection, SqlTransaction transaction, string invoiceCode, int? tenantId, int? deviceId, CancellationToken cancellationToken)
    {
        const string query = """
            WITH invoice_codes AS (
                SELECT
                    i.[ID] AS [InvoiceId],
                    i.[InvoiceNumber],
                    s.[TenantId],
                    s.[DeviceId],
                    i.[InvoiceNumber] AS [GeneratedInvoiceCode]
                FROM [dbo].[TblSubscriptionInvoice] i
                INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
            )
            SELECT [InvoiceId], [InvoiceNumber], [GeneratedInvoiceCode]
            FROM invoice_codes
            WHERE ([InvoiceNumber] = @invoiceCode OR [GeneratedInvoiceCode] = @invoiceCode)
              AND (@tenantId IS NULL OR [TenantId] = @tenantId)
              AND (@deviceId IS NULL OR [DeviceId] = @deviceId);
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = invoiceCode.Trim();
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        InvoiceLogIdentity? invoice = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                invoice = new InvoiceLogIdentity(
                    ReadInt(reader, "InvoiceId"),
                    ReadText(reader, "InvoiceNumber"),
                    ReadText(reader, "GeneratedInvoiceCode"),
                    invoiceCode.Trim());
            }
        }

        if (invoice is null)
        {
            throw new InvoicePdfError("invoice_not_found", "Invoice khong ton tai.", "Invoice was not found.", StatusCodes.Status404NotFound);
        }

        return invoice;
    }

    private static void AddParameters(SqlCommand command, InvoiceIntegrationLogEntry entry)
    {
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = (object?)entry.InvoiceId ?? DBNull.Value;
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = entry.InvoiceCode.Trim();
        command.Parameters.Add("@transactionCode", SqlDbType.NVarChar, 200).Value = EmptyToDbNull(entry.TransactionCode);
        command.Parameters.Add("@eventType", SqlDbType.NVarChar, 50).Value = entry.EventType;
        command.Parameters.Add("@direction", SqlDbType.NVarChar, 20).Value = entry.Direction;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = entry.Status;
        command.Parameters.Add("@sourceSystem", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(entry.SourceSystem);
        command.Parameters.Add("@targetSystem", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(entry.TargetSystem);
        command.Parameters.Add("@rabbitExchange", SqlDbType.NVarChar, 200).Value = EmptyToDbNull(entry.RabbitExchange);
        command.Parameters.Add("@rabbitRoutingKey", SqlDbType.NVarChar, 200).Value = EmptyToDbNull(entry.RabbitRoutingKey);
        command.Parameters.Add("@rabbitQueue", SqlDbType.NVarChar, 200).Value = EmptyToDbNull(entry.RabbitQueue);
        command.Parameters.Add("@messageId", SqlDbType.NVarChar, 200).Value = EmptyToDbNull(entry.MessageId);
        command.Parameters.Add("@correlationId", SqlDbType.NVarChar, 200).Value = EmptyToDbNull(entry.CorrelationId);
        command.Parameters.Add("@payloadJson", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(entry.PayloadJson);
        command.Parameters.Add("@fileOriginalName", SqlDbType.NVarChar, 255).Value = EmptyToDbNull(entry.FileOriginalName);
        command.Parameters.Add("@fileStoredName", SqlDbType.NVarChar, 255).Value = EmptyToDbNull(entry.FileStoredName);
        command.Parameters.Add("@fileSize", SqlDbType.BigInt).Value = (object?)entry.FileSize ?? DBNull.Value;
        command.Parameters.Add("@fileVersion", SqlDbType.Int).Value = (object?)entry.FileVersion ?? DBNull.Value;
        command.Parameters.Add("@httpStatusCode", SqlDbType.Int).Value = (object?)entry.HttpStatusCode ?? DBNull.Value;
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(entry.ErrorCode);
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(CleanError(entry.ErrorMessage));
        command.Parameters.Add("@startedAtUtc", SqlDbType.DateTime2).Value = entry.StartedAtUtc == DateTime.MinValue ? DateTime.UtcNow : entry.StartedAtUtc;
        command.Parameters.Add("@completedAtUtc", SqlDbType.DateTime2).Value = (object?)entry.CompletedAtUtc ?? DBNull.Value;
        command.Parameters.Add("@durationMs", SqlDbType.BigInt).Value = (object?)entry.DurationMs ?? DBNull.Value;
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(entry.CreatedBy);
    }

    private static async Task EnsureSchemaAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblInvoiceIntegrationLog](
                    [ID] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblInvoiceIntegrationLog] PRIMARY KEY,
                    [InvoiceId] int NULL,
                    [InvoiceCode] nvarchar(100) NOT NULL,
                    [TransactionCode] nvarchar(200) NULL,
                    [EventType] nvarchar(50) NOT NULL,
                    [Direction] nvarchar(20) NOT NULL,
                    [Status] nvarchar(30) NOT NULL,
                    [SourceSystem] nvarchar(100) NULL,
                    [TargetSystem] nvarchar(100) NULL,
                    [RabbitExchange] nvarchar(200) NULL,
                    [RabbitRoutingKey] nvarchar(200) NULL,
                    [RabbitQueue] nvarchar(200) NULL,
                    [MessageId] nvarchar(200) NULL,
                    [CorrelationId] nvarchar(200) NULL,
                    [PayloadJson] nvarchar(max) NULL,
                    [FileOriginalName] nvarchar(255) NULL,
                    [FileStoredName] nvarchar(255) NULL,
                    [FileSize] bigint NULL,
                    [FileVersion] int NULL,
                    [HttpStatusCode] int NULL,
                    [ErrorCode] nvarchar(100) NULL,
                    [ErrorMessage] nvarchar(max) NULL,
                    [StartedAtUtc] datetime2(0) NOT NULL,
                    [CompletedAtUtc] datetime2(0) NULL,
                    [DurationMs] bigint NULL,
                    [CreatedAtUtc] datetime2(0) NOT NULL CONSTRAINT [DF_TblInvoiceIntegrationLog_CreatedAtUtc] DEFAULT(SYSUTCDATETIME()),
                    [CreatedBy] nvarchar(100) NULL
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_InvoiceId_CreatedAt' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
                CREATE INDEX [IX_TblInvoiceIntegrationLog_InvoiceId_CreatedAt] ON [dbo].[TblInvoiceIntegrationLog]([InvoiceId], [CreatedAtUtc] DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_InvoiceCode_CreatedAt' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
                CREATE INDEX [IX_TblInvoiceIntegrationLog_InvoiceCode_CreatedAt] ON [dbo].[TblInvoiceIntegrationLog]([InvoiceCode], [CreatedAtUtc] DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_EventType_CreatedAt' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
                CREATE INDEX [IX_TblInvoiceIntegrationLog_EventType_CreatedAt] ON [dbo].[TblInvoiceIntegrationLog]([EventType], [CreatedAtUtc] DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_MessageId' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
                CREATE INDEX [IX_TblInvoiceIntegrationLog_MessageId] ON [dbo].[TblInvoiceIntegrationLog]([MessageId]) WHERE [MessageId] IS NOT NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TblInvoiceIntegrationLog_CorrelationId' AND [object_id] = OBJECT_ID(N'[dbo].[TblInvoiceIntegrationLog]'))
                CREATE INDEX [IX_TblInvoiceIntegrationLog_CorrelationId] ON [dbo].[TblInvoiceIntegrationLog]([CorrelationId]) WHERE [CorrelationId] IS NOT NULL;
            """;
        await using var command = new SqlCommand(query, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static InvoiceIntegrationLogEntry MapEntry(SqlDataReader reader)
    {
        return new InvoiceIntegrationLogEntry
        {
            Id = ReadLong(reader, "ID"),
            InvoiceId = ReadNullableInt(reader, "InvoiceId"),
            InvoiceCode = ReadText(reader, "InvoiceCode"),
            TransactionCode = ReadText(reader, "TransactionCode"),
            EventType = ReadText(reader, "EventType"),
            Direction = ReadText(reader, "Direction"),
            Status = ReadText(reader, "Status"),
            SourceSystem = ReadText(reader, "SourceSystem"),
            TargetSystem = ReadText(reader, "TargetSystem"),
            RabbitExchange = ReadText(reader, "RabbitExchange"),
            RabbitRoutingKey = ReadText(reader, "RabbitRoutingKey"),
            RabbitQueue = ReadText(reader, "RabbitQueue"),
            MessageId = ReadText(reader, "MessageId"),
            CorrelationId = ReadText(reader, "CorrelationId"),
            PayloadJson = ReadText(reader, "PayloadJson"),
            FileOriginalName = ReadText(reader, "FileOriginalName"),
            FileStoredName = ReadText(reader, "FileStoredName"),
            FileSize = ReadNullableLong(reader, "FileSize"),
            FileVersion = ReadNullableInt(reader, "FileVersion"),
            HttpStatusCode = ReadNullableInt(reader, "HttpStatusCode"),
            ErrorCode = ReadText(reader, "ErrorCode"),
            ErrorMessage = ReadText(reader, "ErrorMessage"),
            StartedAtUtc = ReadDate(reader, "StartedAtUtc") ?? DateTime.MinValue,
            CompletedAtUtc = ReadDate(reader, "CompletedAtUtc"),
            DurationMs = ReadNullableLong(reader, "DurationMs"),
            CreatedAtUtc = ReadDate(reader, "CreatedAtUtc") ?? DateTime.MinValue,
            CreatedBy = ReadText(reader, "CreatedBy")
        };
    }

    private static object EmptyToDbNull(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string CleanError(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace("\r", " ").Replace("\n", " ").Trim();
    private static string FirstNotEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static int ReadInt(SqlDataReader reader, string name) => reader[name] == DBNull.Value ? 0 : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);
    private static long ReadLong(SqlDataReader reader, string name) => reader[name] == DBNull.Value ? 0 : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
    private static int? ReadNullableInt(SqlDataReader reader, string name) => reader[name] == DBNull.Value ? null : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);
    private static long? ReadNullableLong(SqlDataReader reader, string name) => reader[name] == DBNull.Value ? null : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
    private static string ReadText(SqlDataReader reader, string name) => reader[name]?.ToString() ?? string.Empty;
    private static DateTime? ReadDate(SqlDataReader reader, string name) => reader[name] == DBNull.Value ? null : Convert.ToDateTime(reader[name], CultureInfo.InvariantCulture);
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:#,##0.##} {units[unit]}";
    }

    private sealed record InvoiceLogIdentity(int InvoiceId, string InvoiceNumber, string GeneratedInvoiceCode, string RequestedCode);
}
