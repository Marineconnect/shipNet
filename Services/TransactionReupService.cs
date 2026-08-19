using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public sealed class TransactionReupService(
    IConfiguration configuration,
    ITransactionReupFileStorage fileStorage,
    IInvoiceRabbitMqPublisher publisher,
    IInvoicePdfService invoicePdfService,
    IPaymentTransactionService paymentTransactionService,
    ILogger<TransactionReupService> logger) : ITransactionReupService
{
    private const string MissingSchemaMessage = "Transaction Reup database schema is missing. Run ShipNet-Transaction-Reup-Database.sql before using this feature.";
    private const string VietnamTimeZoneId = "SE Asia Standard Time";
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    public async Task<IReadOnlyList<TransactionReupBatchViewModel>> GetBatchesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        const string sql = """
            SELECT [ID], [BatchCode], [SourceType], [OriginalFileName], [ImportedByUsername], [ImportedAtUtc],
                   [InvoiceStartNumber], [InvoiceEndNumber], [NextInvoiceNumber], [TotalRows],
                   [ValidRows], [PublishedRows], [FailedRows], [SkippedRows], [DuplicateRows], [Status]
            FROM [dbo].[TblTransactionReupImportBatch]
            ORDER BY [ImportedAtUtc] DESC, [ID] DESC;
            """;
        var result = new List<TransactionReupBatchViewModel>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(MapBatch(reader));
        }
        return result;
    }

    public async Task<TransactionReupDetailsViewModel?> GetDetailsAsync(int batchId, CancellationToken cancellationToken)
    {
        if (batchId <= 0) return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        var batch = await GetBatchAsync(connection, batchId, cancellationToken);
        if (batch is null) return null;
        var items = new List<TransactionReupItemViewModel>();
        const string sql = """
            SELECT [ID], [SourceInvoiceId], [RowNumber], [SourceTransactionCode], [SourceRequestCode], [InvoiceCode],
                   [GrossAmountVnd], [ValidationStatus], [PublishStatus], [PublishAttemptCount],
                   [RabbitMessageId], [RabbitCorrelationId], [PublishMessage], [PublishLogs], [PayloadJson],
                   [PdfFileName], [PdfStorageKey], [PdfSize], [PdfSha256], [PdfContentType], [PdfReceivedAtUtc],
                   [ErrorCode], [ErrorMessage], [ProcessingStartedAtUtc], [WaitingPdfAtUtc], [CompletedAtUtc],
                   [TransactionType], [PaymentMethod], [BankName], [ProcessingFeeVnd], [NetAmountVnd],
                   [SourceStatus], [PublishedAtUtc]
            FROM [dbo].[TblTransactionReupImportItem]
            WHERE [BatchId] = @batchId
            ORDER BY [RowNumber], [ID];
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@batchId", SqlDbType.Int).Value = batchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapItem(reader));
        }
        return new TransactionReupDetailsViewModel { Batch = batch, Items = items };
    }

    public async Task<TransactionReupItemViewModel?> GetItemAsync(int itemId, CancellationToken cancellationToken)
    {
        if (itemId <= 0) return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        const string sql = """
            SELECT [ID], [SourceInvoiceId], [RowNumber], [SourceTransactionCode], [SourceRequestCode], [InvoiceCode],
                   [GrossAmountVnd], [ValidationStatus], [PublishStatus], [PublishAttemptCount],
                   [RabbitMessageId], [RabbitCorrelationId], [PublishMessage], [PublishLogs], [PayloadJson],
                   [PdfFileName], [PdfStorageKey], [PdfSize], [PdfSha256], [PdfContentType], [PdfReceivedAtUtc],
                   [ErrorCode], [ErrorMessage], [ProcessingStartedAtUtc], [WaitingPdfAtUtc], [CompletedAtUtc],
                   [TransactionType], [PaymentMethod], [BankName], [ProcessingFeeVnd], [NetAmountVnd],
                   [SourceStatus], [PublishedAtUtc]
            FROM [dbo].[TblTransactionReupImportItem]
            WHERE [ID] = @id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapItem(reader) : null;
    }

    public async Task<TransactionReupImportResult> ImportAsync(TransactionReupImportViewModel model, AuthUserRecord user, CancellationToken cancellationToken)
    {
        if (model.File is null) throw new InvalidOperationException("Choose a CSV or XLSX file.");
        if (model.StartInvoiceNumber <= 0) throw new InvalidOperationException("Start invoice number must be greater than 0.");

        var rows = await ParseRowsAsync(model.File, cancellationToken);
        if (rows.Count == 0) throw new InvalidOperationException("The input file has no data rows.");

        var batchCode = $"TRX-REUP-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..33];
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        var storedFile = await fileStorage.SaveAsync(model.File, batchCode, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var nextSequence = model.StartInvoiceNumber;
        var validCount = 0;

        var batchId = await InsertExcelBatchAsync(connection, transaction, batchCode, storedFile, user, rows.Count, model.StartInvoiceNumber, cancellationToken);
        foreach (var row in rows)
        {
            var validation = ValidateRow(row, out var createdAt, out var updatedAt);
            var sequence = 0;
            var invoiceCode = string.Empty;
            var payload = string.Empty;
            if (validation == "Valid")
            {
                sequence = nextSequence++;
                validCount++;
                var year = (updatedAt ?? DateTime.UtcNow).Year;
                invoiceCode = BuildInvoiceCode(year, sequence);
                payload = BuildPayload(row, createdAt!.Value, updatedAt!.Value, invoiceCode, user.Username);
            }

            await InsertItemAsync(connection, transaction, batchId, row, validation, sequence, invoiceCode, payload, cancellationToken);
        }

        var endNumber = nextSequence - 1;
        await UpdateBatchCountsAsync(connection, transaction, batchId, endNumber, nextSequence, validCount, 0, 0, 0, 0, rows.Count == validCount ? "Completed" : "CompletedWithErrors", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await PublishPendingItemsAsync(batchId, user, cancellationToken);
        return new TransactionReupImportResult(batchId, $"Imported {rows.Count} rows.", model.StartInvoiceNumber, endNumber, nextSequence);
    }

    public async Task<TransactionReupSelectionResult> CreateFromTransactionSelectionAsync(
        TransactionReupSelectionRequest request,
        AuthUserRecord user,
        int? allowedTenantId,
        int? allowedDeviceId,
        CancellationToken cancellationToken)
    {
        var requestedInvoiceIds = request.InvoiceIds.Where(id => id > 0).Distinct().ToList();
        if (requestedInvoiceIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one invoice to Reup PDF.");
        }

        var candidates = (await paymentTransactionService.GetTransactionReupCandidatesAsync(requestedInvoiceIds, allowedTenantId, allowedDeviceId, cancellationToken)).ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No authorized invoices were found for Reup PDF.");
        }

        var batchCode = $"TRX-REUP-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..33];
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var batchId = await InsertTransactionSelectionBatchAsync(connection, transaction, batchCode, user, candidates.Count, cancellationToken);

        var rowNumber = 1;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await paymentTransactionService.BuildInvoicePdfPayloadAsync(
                candidate.InvoiceId,
                candidate.SourceTransactionCode,
                null,
                user.Username,
                cancellationToken);

            var itemId = await InsertTransactionSelectionItemAsync(
                connection,
                transaction,
                batchId,
                rowNumber++,
                candidate,
                payload,
                cancellationToken);
            var itemPayload = PrepareReupItemPayload(payload.PayloadJson, BuildReupItemUploadUrl(itemId), itemId);
            await UpdateItemPayloadAsync(connection, transaction, itemId, itemPayload, cancellationToken);
        }

        await UpdateBatchCountsAsync(connection, transaction, batchId, null, null, candidates.Count, 0, 0, 0, 0, "Processing", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new TransactionReupSelectionResult(
            batchId,
            batchCode,
            requestedInvoiceIds.Count,
            candidates.Count,
            candidates.Count,
            $"Created Transaction History Reup PDF batch for {candidates.Count} invoice(s).");
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        return await PublishPendingItemsAsync(null, null, cancellationToken);
    }

    public async Task RetryFailedAsync(int batchId, AuthUserRecord user, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        const string sql = "SELECT [ID] FROM [dbo].[TblTransactionReupImportItem] WHERE [BatchId] = @batchId AND [PublishStatus] IN (@status, @errorStatus) ORDER BY [RowNumber], [ID];";
        var ids = new List<int>();
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@batchId", SqlDbType.Int).Value = batchId;
            command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.PublishFailed;
            command.Parameters.Add("@errorStatus", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Error;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        }
        foreach (var id in ids) await RetryItemAsync(id, user, cancellationToken);
        await RecalculateBatchAsync(batchId, cancellationToken);
    }

    public async Task RetryItemAsync(int itemId, AuthUserRecord user, CancellationToken cancellationToken)
    {
        var item = await LoadRetryItemAsync(itemId, cancellationToken);
        if (item is null ||
            (!string.Equals(item.PublishStatus, TransactionReupStatuses.PublishFailed, StringComparison.OrdinalIgnoreCase)
             && !string.Equals(item.PublishStatus, TransactionReupStatuses.Error, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (!await ClaimRetryItemAsync(itemId, cancellationToken))
        {
            return;
        }

        var messageId = Guid.NewGuid().ToString();
        var result = await PublishAsync(item.PayloadJson, item.SourceTransactionCode, messageId, user, cancellationToken);
        await UpdatePublishResultAsync(itemId, result, messageId, item.PublishAttemptCount + 1, cancellationToken);
        await RecalculateItemBatchAsync(itemId, cancellationToken);
    }

    public async Task<string?> GetOriginalFilePathAsync(int batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        const string sql = "SELECT [StoredFilePath] FROM [dbo].[TblTransactionReupImportBatch] WHERE [ID] = @id;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = batchId;
        return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
    }

    public async Task<TransactionReupPdfCallbackResult> SaveItemPdfAsync(
        int itemId,
        IFormFile? file,
        string transactionCode,
        string sourceSystem,
        DateTime? generatedAt,
        string externalReference,
        CancellationToken cancellationToken)
    {
        if (itemId <= 0)
        {
            throw new InvalidOperationException("REUP-PDF-CALLBACK-INVALID: Reup item id is invalid.");
        }

        if (file is null)
        {
            throw new InvalidOperationException("REUP-PDF-MISSING: PDF file is required.");
        }

        var item = await GetItemAsync(itemId, cancellationToken)
            ?? throw new InvalidOperationException("REUP-INVOICE-NOT-FOUND: Reup item was not found.");

        if (!string.IsNullOrWhiteSpace(transactionCode) &&
            !string.Equals(item.SourceTransactionCode, transactionCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("REUP-PDF-CALLBACK-MISMATCH: Callback transactionCode does not match the Reup item.");
        }

        var batch = await GetItemBatchAsync(itemId, cancellationToken)
            ?? throw new InvalidOperationException("REUP-INVOICE-NOT-FOUND: Reup batch was not found.");

        TransactionReupStoredFile storedFile;
        try
        {
            storedFile = await fileStorage.SavePdfAsync(file, batch.BatchCode, itemId, cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkItemErrorAsync(itemId, "REUP-PDF-INVALID", exception.GetBaseException().Message, cancellationToken);
            throw new InvalidOperationException($"REUP-PDF-INVALID: {exception.GetBaseException().Message}", exception);
        }

        const string sql = """
            UPDATE [dbo].[TblTransactionReupImportItem]
            SET [PublishStatus] = @status,
                [PdfFileName] = @fileName,
                [PdfStorageKey] = @storageKey,
                [PdfSize] = @size,
                [PdfSha256] = @sha256,
                [PdfContentType] = @contentType,
                [PdfReceivedAtUtc] = SYSUTCDATETIME(),
                [CompletedAtUtc] = SYSUTCDATETIME(),
                [PublishedAtUtc] = COALESCE([PublishedAtUtc], SYSUTCDATETIME()),
                [ErrorCode] = N'',
                [ErrorMessage] = N'',
                [PublishMessage] = @message,
                [PublishLogs] = CASE
                    WHEN NULLIF([PublishLogs], N'') IS NULL THEN @logs
                    ELSE CONCAT([PublishLogs], CHAR(13), CHAR(10), @logs)
                END,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @id;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Done;
        command.Parameters.Add("@fileName", SqlDbType.NVarChar, 260).Value = storedFile.StoredFileName;
        command.Parameters.Add("@storageKey", SqlDbType.NVarChar, 500).Value = storedFile.RelativePath;
        command.Parameters.Add("@size", SqlDbType.BigInt).Value = storedFile.Size;
        command.Parameters.Add("@sha256", SqlDbType.VarChar, 64).Value = storedFile.Sha256;
        command.Parameters.Add("@contentType", SqlDbType.NVarChar, 100).Value = storedFile.ContentType;
        command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = "PDF callback received and stored.";
        command.Parameters.Add("@logs", SqlDbType.NVarChar, -1).Value = string.Join(Environment.NewLine, [
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | Reup PDF callback received.",
            $"SourceSystem={sourceSystem}",
            $"GeneratedAt={generatedAt:O}",
            $"ExternalReference={externalReference}",
            $"FileName={storedFile.StoredFileName}",
            $"Sha256={storedFile.Sha256}"
        ]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await RecalculateBatchAsync(batch.Id, cancellationToken);

        return new TransactionReupPdfCallbackResult(itemId, item.InvoiceCode, item.SourceTransactionCode, storedFile.StoredFileName, storedFile.Size, storedFile.Sha256, DateTime.UtcNow);
    }

    public async Task<TransactionReupItemPdfOpenResult?> OpenItemPdfAsync(int itemId, CancellationToken cancellationToken)
    {
        var item = await GetItemAsync(itemId, cancellationToken);
        if (item is null || string.IsNullOrWhiteSpace(item.PdfStorageKey))
        {
            return null;
        }

        var stream = await fileStorage.OpenReadAsync(item.PdfStorageKey, cancellationToken);
        return stream is null ? null : new TransactionReupItemPdfOpenResult { Item = item, Stream = stream };
    }

    private async Task<int> PublishPendingItemsAsync(int? batchId, AuthUserRecord? user, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        const string sql = """
            SELECT TOP (100)
                   i.[ID], i.[BatchId], i.[SourceTransactionCode], i.[PayloadJson], i.[PublishAttemptCount],
                   b.[ImportedByUserId], b.[ImportedByUsername]
            FROM [dbo].[TblTransactionReupImportItem] i
            INNER JOIN [dbo].[TblTransactionReupImportBatch] b ON b.[ID] = i.[BatchId]
            WHERE (@batchId IS NULL OR i.[BatchId] = @batchId)
              AND i.[PublishStatus] = @status
            ORDER BY i.[BatchId], i.[RowNumber], i.[ID];
            """;
        var items = new List<(int Id, int BatchId, string Code, string Payload, int Attempts, int? UserId, string Username)>();
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@batchId", SqlDbType.Int).Value = (object?)batchId ?? DBNull.Value;
            command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Pending;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add((
                    ReadInt(reader, "ID"),
                    ReadInt(reader, "BatchId"),
                    ReadText(reader, "SourceTransactionCode"),
                    ReadText(reader, "PayloadJson"),
                    ReadInt(reader, "PublishAttemptCount"),
                    reader["ImportedByUserId"] is DBNull ? null : ReadInt(reader, "ImportedByUserId"),
                    ReadText(reader, "ImportedByUsername")));
            }
        }

        var processed = 0;
        var touchedBatches = new HashSet<int>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ClaimPendingItemAsync(item.Id, cancellationToken))
            {
                continue;
            }

            var messageId = Guid.NewGuid().ToString();
            var publishUser = user ?? new AuthUserRecord
            {
                Id = item.UserId ?? 0,
                Username = string.IsNullOrWhiteSpace(item.Username) ? "system" : item.Username
            };
            var result = await PublishAsync(item.Payload, item.Code, messageId, publishUser, cancellationToken);
            await UpdatePublishResultAsync(item.Id, result, messageId, item.Attempts + 1, cancellationToken);
            processed++;
            touchedBatches.Add(item.BatchId);
        }

        foreach (var id in touchedBatches)
        {
            await RecalculateBatchAsync(id, cancellationToken);
        }

        return processed;
    }

    private async Task<bool> ClaimPendingItemAsync(int itemId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [dbo].[TblTransactionReupImportItem]
            SET [PublishStatus] = @processing,
                [ProcessingStartedAtUtc] = SYSUTCDATETIME(),
                [ErrorCode] = N'',
                [ErrorMessage] = N'',
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @id AND [PublishStatus] = @pending;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        command.Parameters.Add("@pending", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Pending;
        command.Parameters.Add("@processing", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Processing;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> ClaimRetryItemAsync(int itemId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [dbo].[TblTransactionReupImportItem]
            SET [PublishStatus] = @processing,
                [ProcessingStartedAtUtc] = SYSUTCDATETIME(),
                [CompletedAtUtc] = NULL,
                [ErrorCode] = N'',
                [ErrorMessage] = N'',
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @id AND [PublishStatus] IN (@publishFailed, @error);
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        command.Parameters.Add("@processing", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Processing;
        command.Parameters.Add("@publishFailed", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.PublishFailed;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Error;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<InvoiceRabbitMqPublishResult> PublishAsync(string payload, string transactionCode, string messageId, AuthUserRecord user, CancellationToken cancellationToken)
    {
        try
        {
            payload = EnsureReupFlag(payload);
            return await publisher.PublishInvoiceAsync(new InvoiceRabbitMqPublishRequest
            {
                InvoiceJson = payload,
                MessageId = messageId,
                CorrelationId = transactionCode,
                UserId = user.Id,
                Username = user.Username
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Transaction Reup RabbitMQ publish failed for transaction {TransactionCode}.", transactionCode);
            return new InvoiceRabbitMqPublishResult { Success = false, Message = exception.GetBaseException().Message, CorrelationId = transactionCode };
        }
    }

    private async Task UpdatePublishResultAsync(int itemId, InvoiceRabbitMqPublishResult result, string messageId, int attemptCount, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [dbo].[TblTransactionReupImportItem]
            SET [PublishStatus] = @status, [RabbitMessageId] = @messageId, [RabbitCorrelationId] = @correlationId,
                [RabbitExchange] = @rabbitExchange, [RabbitRoutingKey] = @rabbitRoutingKey, [RabbitQueue] = @rabbitQueue,
                [PublishMessage] = @message, [PublishLogs] = @logs, [PublishAttemptCount] = @attemptCount,
                [PublishedAtUtc] = CASE WHEN @success = 0 THEN [PublishedAtUtc] ELSE SYSUTCDATETIME() END,
                [ProcessingStartedAtUtc] = CASE WHEN @success = 0 THEN [ProcessingStartedAtUtc] ELSE COALESCE([ProcessingStartedAtUtc], SYSUTCDATETIME()) END,
                [WaitingPdfAtUtc] = CASE WHEN @success = 0 THEN [WaitingPdfAtUtc] ELSE SYSUTCDATETIME() END,
                [ErrorCode] = CASE WHEN @success = 0 THEN N'REUP-RABBIT-PUBLISH-FAILED' ELSE N'' END,
                [ErrorMessage] = CASE WHEN @success = 0 THEN @message ELSE N'' END,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @id;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = result.Success ? TransactionReupStatuses.WaitingPdf : TransactionReupStatuses.PublishFailed;
        command.Parameters.Add("@messageId", SqlDbType.NVarChar, 100).Value = messageId;
        command.Parameters.Add("@correlationId", SqlDbType.NVarChar, 250).Value = result.CorrelationId;
        command.Parameters.Add("@rabbitExchange", SqlDbType.NVarChar, 250).Value = result.ExchangeName;
        command.Parameters.Add("@rabbitRoutingKey", SqlDbType.NVarChar, 250).Value = result.RoutingKey;
        command.Parameters.Add("@rabbitQueue", SqlDbType.NVarChar, 250).Value = result.QueueName;
        command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = result.Message;
        command.Parameters.Add("@logs", SqlDbType.NVarChar, -1).Value = string.Join(Environment.NewLine, result.Logs);
        command.Parameters.Add("@attemptCount", SqlDbType.Int).Value = attemptCount;
        command.Parameters.Add("@success", SqlDbType.Bit).Value = result.Success;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RecordWorkerResultAsync(TransactionReupWorkerResultRequest request, CancellationToken cancellationToken)
    {
        var invoiceCode = request.InvoiceCode.Trim();
        var transactionCode = request.TransactionCode.Trim();
        if (string.IsNullOrWhiteSpace(invoiceCode) && string.IsNullOrWhiteSpace(transactionCode))
        {
            return false;
        }

        var workerStatus = request.Status?.Trim() ?? string.Empty;
        var workerErrorCode = request.ErrorCode?.Trim() ?? string.Empty;
        var isFailed = string.Equals(workerStatus, "FAILED", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(workerErrorCode) && !string.Equals(workerErrorCode, "NONE", StringComparison.OrdinalIgnoreCase) && !string.Equals(workerStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase));
        var status = isFailed ? TransactionReupStatuses.PublishFailed : TransactionReupStatuses.Published;
        var message = BuildWorkerResultMessage(request, isFailed);
        var logs = BuildWorkerResultLogs(request);

        const string selectBatchSql = """
            SELECT DISTINCT i.[BatchId]
            FROM [dbo].[TblTransactionReupImportItem] i
            INNER JOIN [dbo].[TblTransactionReupImportBatch] b ON b.[ID] = i.[BatchId]
            WHERE ISNULL(b.[SourceType], N'EXCEL_IMPORT') <> N'TRANSACTION_SELECTION'
              AND (@invoiceCode = N'' OR i.[InvoiceCode] = @invoiceCode)
              AND (@transactionCode = N'' OR i.[SourceTransactionCode] = @transactionCode);
            """;

        const string updateSql = """
            UPDATE i
            SET i.[PublishStatus] = @status,
                i.[PublishMessage] = @message,
                i.[PublishLogs] = CASE
                    WHEN NULLIF(i.[PublishLogs], N'') IS NULL THEN @logs
                    ELSE CONCAT(i.[PublishLogs], CHAR(13), CHAR(10), @logs)
                END,
                i.[PublishedAtUtc] = CASE WHEN @isPublished = 1 THEN SYSUTCDATETIME() ELSE i.[PublishedAtUtc] END,
                i.[UpdatedAtUtc] = SYSUTCDATETIME()
            FROM [dbo].[TblTransactionReupImportItem] i
            INNER JOIN [dbo].[TblTransactionReupImportBatch] b ON b.[ID] = i.[BatchId]
            WHERE ISNULL(b.[SourceType], N'EXCEL_IMPORT') <> N'TRANSACTION_SELECTION'
              AND (@invoiceCode = N'' OR i.[InvoiceCode] = @invoiceCode)
              AND (@transactionCode = N'' OR i.[SourceTransactionCode] = @transactionCode);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);

        var batchIds = new List<int>();
        await using (var selectCommand = new SqlCommand(selectBatchSql, connection))
        {
            selectCommand.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = invoiceCode;
            selectCommand.Parameters.Add("@transactionCode", SqlDbType.NVarChar, 100).Value = transactionCode;

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                batchIds.Add(reader.GetInt32(0));
            }
        }

        if (batchIds.Count == 0)
        {
            return false;
        }

        await using var command = new SqlCommand(updateSql, connection);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = status;
        command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = message;
        command.Parameters.Add("@logs", SqlDbType.NVarChar, -1).Value = logs;
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = invoiceCode;
        command.Parameters.Add("@transactionCode", SqlDbType.NVarChar, 100).Value = transactionCode;
        command.Parameters.Add("@isPublished", SqlDbType.Bit).Value = !isFailed;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (var batchId in batchIds)
        {
            await RecalculateBatchAsync(batchId, cancellationToken);
        }

        return affected > 0;
    }

    private static string BuildWorkerResultMessage(TransactionReupWorkerResultRequest request, bool isFailed)
    {
        var status = string.IsNullOrWhiteSpace(request.Status) ? (isFailed ? "FAILED" : "SUCCEEDED") : request.Status.Trim();
        var error = FirstNotEmpty(request.ErrorCode, request.ErrorMessage);
        return string.IsNullOrWhiteSpace(error)
            ? $"Invoice worker result: {status}."
            : $"Invoice worker result: {status}. {error}";
    }

    private static string BuildWorkerResultLogs(TransactionReupWorkerResultRequest request)
    {
        var lines = new List<string>
        {
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | Invoice worker callback received.",
            $"Status={request.Status}",
            $"TransactionCode={request.TransactionCode}",
            $"InvoiceCode={request.InvoiceCode}",
            $"ErrorCode={request.ErrorCode}",
            $"ErrorMessage={request.ErrorMessage}",
            $"LogFile={request.LogFile}",
            $"OutputFile={request.OutputFile}",
            $"SourceSystem={request.SourceSystem}"
        };

        if (!string.IsNullOrWhiteSpace(request.LogContent))
        {
            lines.Add("DiagnosticLogContent:");
            lines.Add(request.LogContent);
        }

        if (!string.IsNullOrWhiteSpace(request.RawMessage))
        {
            lines.Add($"RawMessage={request.RawMessage}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<List<TransactionReupSourceRow>> ParseRowsAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        return extension == ".csv"
            ? await ParseCsvAsync(file, cancellationToken)
            : await ParseXlsxAsync(file, cancellationToken);
    }

    private static async Task<List<TransactionReupSourceRow>> ParseCsvAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false), true);
        var lines = new List<string>();
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(await reader.ReadLineAsync(cancellationToken) ?? string.Empty);
        }
        if (lines.Count == 0) return [];
        return MapRows(lines.Select(ParseCsvLine).ToList());
    }

    private static async Task<List<TransactionReupSourceRow>> ParseXlsxAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var workbook = new XSSFWorkbook(stream);
        var sheet = workbook.GetSheetAt(0);
        var rows = new List<List<string>>();
        for (var index = sheet.FirstRowNum; index <= sheet.LastRowNum; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = sheet.GetRow(index);
            if (row is null) continue;
            rows.Add(Enumerable.Range(0, Math.Max(0, (int)row.LastCellNum)).Select(cell => ReadCell(row.GetCell(cell))).ToList());
        }
        return MapRows(rows);
    }

    private static List<TransactionReupSourceRow> MapRows(List<List<string>> rows)
    {
        if (rows.Count < 2) return [];
        var headers = rows[0].Select(NormalizeHeader).ToList();
        var result = new List<TransactionReupSourceRow>();
        for (var index = 1; index < rows.Count; index++)
        {
            var values = headers.Select((header, column) => new { header, value = column < rows[index].Count ? rows[index][column].Trim().TrimStart('\'') : string.Empty })
                .Where(item => !string.IsNullOrWhiteSpace(item.header))
                .ToDictionary(item => item.header, item => item.value, StringComparer.OrdinalIgnoreCase);
            if (values.Values.All(string.IsNullOrWhiteSpace)) continue;
            result.Add(new TransactionReupSourceRow(
                index + 1,
                Get(values, "thoi gian khoi tao", "CreatedAt"),
                Get(values, "thoi gian cap nhat", "UpdatedAt"),
                Get(values, "ma giao dich", "TransactionCode"),
                Get(values, "ma yeu cau ma hoa don", "RequestInvoiceCode"),
                Get(values, "ma yeu cau goc", "SourceOriginalRequestCode"),
                Get(values, "nguoi tao hoa don", "SourceCreatedBy"),
                Get(values, "loai giao dich", "TransactionType"),
                Get(values, "phuong thuc thanh toan", "PaymentMethod"),
                Get(values, "ngan hang thuong hieu the", "BankName"),
                ParseMoney(Get(values, "tong gia tri vnd", "TotalAmountVnd")),
                ParseMoney(Get(values, "phi xu ly", "ProcessingFee")),
                Get(values, "noi dung chuyen khoan", "TransferContent"),
                Get(values, "doi tuong chiu phi", "FeeBearer"),
                ParseMoney(Get(values, "so tien thuc nhan", "NetAmountVnd")),
                Get(values, "trang thai", "SourceStatus"),
                values));
        }
        return result;
    }

    private static string ValidateRow(TransactionReupSourceRow row, out DateTime? createdAt, out DateTime? updatedAt)
    {
        createdAt = ParseVietnamDate(row.CreatedAtText);
        updatedAt = ParseVietnamDate(row.UpdatedAtText);
        if (!string.Equals(row.SourceStatus.Trim(), "Thành công", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(row.TransactionCode)
            || string.IsNullOrWhiteSpace(row.RequestInvoiceCode)
            || row.TotalAmountVnd <= 0
            || !createdAt.HasValue
            || !updatedAt.HasValue)
        {
            return "Invalid";
        }
        return "Valid";
    }

    private string TryBuildInvoiceUrl(string invoiceCode)
    {
        if (string.IsNullOrWhiteSpace(configuration["InvoicePdfIntegration:PublicBaseUrl"]))
        {
            return string.Empty;
        }

        try
        {
            return invoicePdfService.BuildUploadUrl(invoiceCode);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Skipped InvoiceURL for transaction reup invoice {InvoiceCode} because InvoicePdfIntegration:PublicBaseUrl is not usable.", invoiceCode);
            return string.Empty;
        }
    }

    private static string EnsureReupFlag(string payload)
    {
        try
        {
            var node = JsonSerializer.Deserialize<JsonObject>(payload);
            if (node is null)
            {
                return payload;
            }

            node["reup"] = 1;
            return node.ToJsonString();
        }
        catch
        {
            return payload;
        }
    }

    private static string PrepareReupItemPayload(string payload, string invoiceUrl, int itemId)
    {
        try
        {
            var node = JsonSerializer.Deserialize<JsonObject>(payload) ?? [];
            node["reup"] = 1;
            node["InvoiceURL"] = invoiceUrl;
            node["reupItemId"] = itemId;
            return node.ToJsonString();
        }
        catch
        {
            return payload;
        }
    }

    private string BuildReupItemUploadUrl(int itemId)
    {
        var baseUrl = configuration["InvoicePdfIntegration:PublicBaseUrl"]?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("InvoicePdfIntegration:PublicBaseUrl is required to build Transaction Reup InvoiceURL.");
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("InvoicePdfIntegration:PublicBaseUrl must be an absolute URL.");
        }

        return new Uri(baseUri, $"api/transaction-reup/items/{itemId.ToString(CultureInfo.InvariantCulture)}/pdf").AbsoluteUri;
    }

    private static string BuildPayload(TransactionReupSourceRow row, DateTime createdAt, DateTime updatedAt, string invoiceCode, string username)
    {
        var paymentUtc = ToUtc(updatedAt);
        var invoiceParams = new
        {
            LogoUrl = "",
            CompanyName = "MLTECH MARINE CONNECT PTE LTD",
            CompanyAddressLine1 = "Address: 18 Sin Ming Lane, #07-13, Midview City,",
            CompanyAddressLine2 = "Singapore- 573960, Singapore",
            CompanyEmail = "Email: admin@marineconnect.sg",
            ContactNote = "If you have any questions regarding this invoice, please contact us.",
            PaymentTitle = "PAYMENT INSTRUCTIONS:",
            BankAccountNumber = "9633369179",
            BeneficiaryName = "MLTECH MARINE CONNECT PTE LTD",
            BankName = "BIDV - Joint Stock Commercial Bank for Investment and Development of Vietnam",
            SwiftCode = "BIDVVNVX",
            BankAddressLine1 = "Ho Chi Minh city, Vietnam",
            BankAddressLine2 = "Bank Charges: To be borne by the Remitter"
        };
        var vessel = new
        {
            vesselId = row.RequestInvoiceCode,
            vesselName = "shipNet",
            kit_id = row.RequestInvoiceCode,
            PONumber = "",
            PO_Number = "",
            subscriptions = new[]
            {
                new
                {
                    type = "subscription",
                    title = row.TransactionCode,
                    subTitles = new[] { row.TransferContent },
                    price = row.TotalAmountVnd,
                    start_time = createdAt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    end_time = updatedAt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    kit_id = row.RequestInvoiceCode
                }
            }
        };
        var payload = new Dictionary<string, object?>
        {
            ["transactionCode"] = row.TransactionCode,
            ["invoiceCode"] = invoiceCode,
            ["reup"] = 1,
            ["source"] = "SHIPNET",
            ["paymentTime"] = paymentUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["operatorName"] = string.IsNullOrWhiteSpace(row.SourceCreatedBy) ? username : row.SourceCreatedBy,
            ["email"] = "",
            ["PONumber"] = "",
            ["PO_Number"] = "",
            ["invoiceParams"] = invoiceParams,
            ["vessels"] = new[] { vessel }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildInvoiceCode(int year, int sequence) => $"SPN-INV-{year % 100:00}-{sequence:00000}";

    private static DateTime? ParseVietnamDate(string value)
    {
        value = value.Trim().TrimStart('\'');
        var formats = new[] { "HH:mm:ss dd/MM/yyyy", "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy H:mm:ss" };
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact)
            ? DateTime.SpecifyKind(exact, DateTimeKind.Unspecified)
            : DateTime.TryParse(value, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.AllowWhiteSpaces, out var parsed)
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
                : null;
    }

    private static DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), GetVietnamTimeZone());

    private static TimeZoneInfo GetVietnamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(VietnamTimeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
    }

    private static decimal ParseMoney(string value)
    {
        var normalized = value.Trim().Replace("VND", "", StringComparison.OrdinalIgnoreCase).Replace("₫", "").Replace(" ", "");
        if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariant)) return invariant;
        normalized = normalized.Replace(".", "").Replace(",", ".");
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (character == ',' && !quoted) { result.Add(value.ToString()); value.Clear(); continue; }
            value.Append(character);
        }
        result.Add(value.ToString());
        return result;
    }

    private static string NormalizeHeader(string value) => value.Trim().TrimStart('\'').Replace("\u00a0", " ", StringComparison.Ordinal).Trim();
    private static string Get(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var exactValue) && !string.IsNullOrWhiteSpace(exactValue))
            {
                return exactValue;
            }

            var normalizedKey = NormalizeColumnKey(key);
            foreach (var item in values)
            {
                if (NormalizeColumnKey(item.Key) == normalizedKey && !string.IsNullOrWhiteSpace(item.Value))
                {
                    return item.Value;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeColumnKey(string value)
    {
        var normalized = value.Trim().TrimStart('\'').Replace("\u00a0", " ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                var lower = char.ToLowerInvariant(character);
                builder.Append(lower == '\u0111' ? 'd' : lower);
            }
        }

        return builder.ToString()
            .Normalize(NormalizationForm.FormC)
            .Replace("/", " ", StringComparison.Ordinal)
            .Replace("(", " ", StringComparison.Ordinal)
            .Replace(")", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(string.Empty, (current, part) => current + part);
    }
    private static string ReadCell(ICell? cell)
    {
        if (cell is null) return string.Empty;
        if (cell.CellType == CellType.Numeric && DateUtil.IsCellDateFormatted(cell))
        {
            return cell.DateCellValue is { } dateValue
                ? dateValue.ToString("HH:mm:ss dd/MM/yyyy", CultureInfo.InvariantCulture)
                : string.Empty;
        }
        return cell.ToString() ?? string.Empty;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureSchemaExistsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = "SELECT CASE WHEN OBJECT_ID(N'dbo.TblTransactionReupImportBatch', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.TblTransactionReupImportItem', N'U') IS NOT NULL THEN 1 ELSE 0 END;";
        await using var command = new SqlCommand(sql, connection);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException(MissingSchemaMessage);
    }

    private static async Task<int> InsertExcelBatchAsync(SqlConnection connection, SqlTransaction transaction, string batchCode, TransactionReupStoredFile file, AuthUserRecord user, int totalRows, int startNumber, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[TblTransactionReupImportBatch]
                ([BatchCode], [SourceType], [OriginalFileName], [StoredFileName], [StoredFilePath], [FileSize], [ContentType], [FileExtension], [FileSha256],
                 [ImportedByUserId], [ImportedByUsername], [ImportedAtUtc], [InvoiceStartNumber], [InvoiceEndNumber], [NextInvoiceNumber],
                 [TotalRows], [ValidRows], [PublishedRows], [FailedRows], [SkippedRows], [DuplicateRows], [Status], [CreatedAtUtc], [UpdatedAtUtc])
            OUTPUT INSERTED.[ID]
            VALUES
                (@batchCode, @sourceType, @originalFileName, @storedFileName, @storedFilePath, @fileSize, @contentType, @extension, @sha256,
                 @userId, @username, SYSUTCDATETIME(), @startNumber, @startNumber - 1, @startNumber, @totalRows, 0, 0, 0, 0, 0, N'Processing', SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchCode", SqlDbType.NVarChar, 100).Value = batchCode;
        command.Parameters.Add("@sourceType", SqlDbType.NVarChar, 40).Value = TransactionReupSourceTypes.ExcelImport;
        command.Parameters.Add("@originalFileName", SqlDbType.NVarChar, 260).Value = file.OriginalFileName;
        command.Parameters.Add("@storedFileName", SqlDbType.NVarChar, 260).Value = file.StoredFileName;
        command.Parameters.Add("@storedFilePath", SqlDbType.NVarChar, 500).Value = file.RelativePath;
        command.Parameters.Add("@fileSize", SqlDbType.BigInt).Value = file.Size;
        command.Parameters.Add("@contentType", SqlDbType.NVarChar, 200).Value = file.ContentType;
        command.Parameters.Add("@extension", SqlDbType.NVarChar, 20).Value = file.Extension;
        command.Parameters.Add("@sha256", SqlDbType.VarChar, 64).Value = file.Sha256;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = user.Id;
        command.Parameters.Add("@username", SqlDbType.NVarChar, 100).Value = user.Username;
        command.Parameters.Add("@startNumber", SqlDbType.Int).Value = startNumber;
        command.Parameters.Add("@totalRows", SqlDbType.Int).Value = totalRows;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertTransactionSelectionBatchAsync(SqlConnection connection, SqlTransaction transaction, string batchCode, AuthUserRecord user, int totalRows, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[TblTransactionReupImportBatch]
                ([BatchCode], [SourceType], [OriginalFileName], [StoredFileName], [StoredFilePath], [FileSize], [ContentType], [FileExtension], [FileSha256],
                 [ImportedByUserId], [ImportedByUsername], [ImportedAtUtc], [InvoiceStartNumber], [InvoiceEndNumber], [NextInvoiceNumber],
                 [TotalRows], [ValidRows], [PublishedRows], [FailedRows], [SkippedRows], [DuplicateRows], [Status], [CreatedAtUtc], [UpdatedAtUtc])
            OUTPUT INSERTED.[ID]
            VALUES
                (@batchCode, @sourceType, NULL, NULL, NULL, 0, NULL, NULL, NULL,
                 @userId, @username, SYSUTCDATETIME(), NULL, NULL, NULL, @totalRows, @totalRows, 0, 0, 0, 0, N'Processing', SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchCode", SqlDbType.NVarChar, 100).Value = batchCode;
        command.Parameters.Add("@sourceType", SqlDbType.NVarChar, 40).Value = TransactionReupSourceTypes.TransactionSelection;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = user.Id;
        command.Parameters.Add("@username", SqlDbType.NVarChar, 100).Value = user.Username;
        command.Parameters.Add("@totalRows", SqlDbType.Int).Value = totalRows;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task InsertItemAsync(SqlConnection connection, SqlTransaction transaction, int batchId, TransactionReupSourceRow row, string validation, int sequence, string invoiceCode, string payload, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[TblTransactionReupImportItem]
                ([BatchId], [RowNumber], [SourceTransactionCode], [SourceRequestCode], [SourceOriginalRequestCode], [SourceCreatedBy],
                 [TransactionType], [PaymentMethod], [BankName], [GrossAmountVnd], [ProcessingFeeVnd], [TransferContent], [FeeBearer],
                 [NetAmountVnd], [SourceStatus], [SourceCreatedAt], [SourceUpdatedAt], [ValidationStatus],
                 [PublishStatus], [InvoiceYear], [InvoiceSequence], [InvoiceCode], [ExpectedPdfFileName], [PayloadJson],
                 [PublishAttemptCount], [CreatedAtUtc], [UpdatedAtUtc])
            VALUES
                (@batchId, @rowNumber, @sourceTransactionCode, @sourceRequestCode, @sourceOriginalRequestCode, @sourceCreatedBy, @transactionType, @paymentMethod,
                 @bankName, @grossAmountVnd, @processingFeeVnd, @transferContent, @feeBearer, @netAmountVnd, @sourceStatus,
                 @sourceCreatedAt, @sourceUpdatedAt, @validationStatus, @publishStatus, @invoiceYear, @sequence,
                 @invoiceCode, @expectedFileName, @payload, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchId", SqlDbType.Int).Value = batchId;
        command.Parameters.Add("@rowNumber", SqlDbType.Int).Value = row.RowNumber;
        command.Parameters.Add("@sourceTransactionCode", SqlDbType.NVarChar, 250).Value = row.TransactionCode;
        command.Parameters.Add("@sourceRequestCode", SqlDbType.NVarChar, 250).Value = row.RequestInvoiceCode;
        command.Parameters.Add("@sourceOriginalRequestCode", SqlDbType.NVarChar, 250).Value = row.SourceOriginalRequestCode;
        command.Parameters.Add("@sourceCreatedBy", SqlDbType.NVarChar, 250).Value = row.SourceCreatedBy;
        command.Parameters.Add("@transactionType", SqlDbType.NVarChar, 100).Value = row.TransactionType;
        command.Parameters.Add("@paymentMethod", SqlDbType.NVarChar, 100).Value = row.PaymentMethod;
        command.Parameters.Add("@bankName", SqlDbType.NVarChar, 250).Value = row.BankName;
        AddDecimal(command, "@grossAmountVnd", row.TotalAmountVnd);
        AddDecimal(command, "@processingFeeVnd", row.ProcessingFee);
        command.Parameters.Add("@transferContent", SqlDbType.NVarChar, 1000).Value = row.TransferContent;
        command.Parameters.Add("@feeBearer", SqlDbType.NVarChar, 250).Value = row.FeeBearer;
        AddDecimal(command, "@netAmountVnd", row.NetAmountVnd);
        command.Parameters.Add("@sourceStatus", SqlDbType.NVarChar, 100).Value = row.SourceStatus;
        command.Parameters.Add("@sourceCreatedAt", SqlDbType.DateTime2).Value = ParseVietnamDate(row.CreatedAtText) is { } created ? created : DBNull.Value;
        command.Parameters.Add("@sourceUpdatedAt", SqlDbType.DateTime2).Value = ParseVietnamDate(row.UpdatedAtText) is { } updated ? updated : DBNull.Value;
        command.Parameters.Add("@validationStatus", SqlDbType.NVarChar, 30).Value = validation;
        command.Parameters.Add("@publishStatus", SqlDbType.NVarChar, 30).Value = validation == "Valid" ? TransactionReupStatuses.Pending : validation;
        command.Parameters.Add("@invoiceYear", SqlDbType.Int).Value = sequence > 0 && ParseVietnamDate(row.UpdatedAtText) is { } yearDate ? yearDate.Year : DBNull.Value;
        command.Parameters.Add("@sequence", SqlDbType.Int).Value = sequence > 0 ? sequence : DBNull.Value;
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = invoiceCode;
        command.Parameters.Add("@expectedFileName", SqlDbType.NVarChar, 150).Value = string.IsNullOrWhiteSpace(invoiceCode) ? string.Empty : $"{invoiceCode}.pdf";
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = payload;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> InsertTransactionSelectionItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int batchId,
        int rowNumber,
        PaymentTransactionReupCandidate candidate,
        InvoicePdfPayloadBuildResult payload,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [dbo].[TblTransactionReupImportItem]
                ([BatchId], [SourceInvoiceId], [RowNumber], [SourceTransactionCode], [SourceRequestCode], [SourceOriginalRequestCode], [SourceCreatedBy],
                 [TransactionType], [PaymentMethod], [BankName], [GrossAmountVnd], [ProcessingFeeVnd], [TransferContent], [FeeBearer],
                 [NetAmountVnd], [SourceStatus], [SourceCreatedAt], [SourceUpdatedAt], [ValidationStatus],
                 [PublishStatus], [InvoiceYear], [InvoiceSequence], [InvoiceCode], [ExpectedPdfFileName], [PayloadJson],
                 [PublishAttemptCount], [CreatedAtUtc], [UpdatedAtUtc])
            OUTPUT INSERTED.[ID]
            VALUES
                (@batchId, @sourceInvoiceId, @rowNumber, @sourceTransactionCode, @sourceRequestCode, N'', @sourceCreatedBy,
                 @transactionType, @paymentMethod, @bankName, @grossAmountVnd, 0, N'', N'',
                 @netAmountVnd, @sourceStatus, NULL, NULL, N'Valid',
                 @publishStatus, NULL, NULL, @invoiceCode, @expectedFileName, @payload,
                 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@batchId", SqlDbType.Int).Value = batchId;
        command.Parameters.Add("@sourceInvoiceId", SqlDbType.Int).Value = candidate.InvoiceId;
        command.Parameters.Add("@rowNumber", SqlDbType.Int).Value = rowNumber;
        command.Parameters.Add("@sourceTransactionCode", SqlDbType.NVarChar, 250).Value = payload.TransactionCode;
        command.Parameters.Add("@sourceRequestCode", SqlDbType.NVarChar, 250).Value = FirstNotEmpty(candidate.SourceRequestCode, candidate.InvoiceNumber);
        command.Parameters.Add("@sourceCreatedBy", SqlDbType.NVarChar, 250).Value = payload.OperatorName;
        command.Parameters.Add("@transactionType", SqlDbType.NVarChar, 100).Value = candidate.TransactionType;
        command.Parameters.Add("@paymentMethod", SqlDbType.NVarChar, 100).Value = candidate.PaymentMethod;
        command.Parameters.Add("@bankName", SqlDbType.NVarChar, 250).Value = candidate.BankName;
        AddDecimal(command, "@grossAmountVnd", payload.AmountVnd > 0 ? payload.AmountVnd : candidate.GrossAmountVnd);
        AddDecimal(command, "@netAmountVnd", payload.AmountVnd > 0 ? payload.AmountVnd : candidate.NetAmountVnd);
        command.Parameters.Add("@sourceStatus", SqlDbType.NVarChar, 100).Value = candidate.SourceStatus;
        command.Parameters.Add("@publishStatus", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Pending;
        command.Parameters.Add("@invoiceCode", SqlDbType.NVarChar, 100).Value = payload.InvoiceCode;
        command.Parameters.Add("@expectedFileName", SqlDbType.NVarChar, 150).Value = $"{payload.InvoiceCode}.pdf";
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = payload.PayloadJson;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task UpdateItemPayloadAsync(SqlConnection connection, SqlTransaction transaction, int itemId, string payload, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE [dbo].[TblTransactionReupImportItem] SET [PayloadJson] = @payload, [UpdatedAtUtc] = SYSUTCDATETIME() WHERE [ID] = @id;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = payload;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateBatchCountsAsync(SqlConnection connection, SqlTransaction transaction, int batchId, int? endNumber, int? nextNumber, int valid, int published, int failed, int skipped, int duplicate, string status, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [dbo].[TblTransactionReupImportBatch]
            SET [InvoiceEndNumber] = @endNumber, [NextInvoiceNumber] = @nextNumber, [ValidRows] = @valid,
                [PublishedRows] = @published, [FailedRows] = @failed, [SkippedRows] = @skipped,
                [DuplicateRows] = @duplicate, [Status] = @status, [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = batchId;
        command.Parameters.Add("@endNumber", SqlDbType.Int).Value = (object?)endNumber ?? DBNull.Value;
        command.Parameters.Add("@nextNumber", SqlDbType.Int).Value = (object?)nextNumber ?? DBNull.Value;
        command.Parameters.Add("@valid", SqlDbType.Int).Value = valid;
        command.Parameters.Add("@published", SqlDbType.Int).Value = published;
        command.Parameters.Add("@failed", SqlDbType.Int).Value = failed;
        command.Parameters.Add("@skipped", SqlDbType.Int).Value = skipped;
        command.Parameters.Add("@duplicate", SqlDbType.Int).Value = duplicate;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 40).Value = status;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecalculateBatchAsync(int batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        const string sql = """
            UPDATE b SET
                [PublishedRows] = x.PublishedRows, [FailedRows] = x.FailedRows, [SkippedRows] = x.SkippedRows,
                [DuplicateRows] = x.DuplicateRows,
                [Status] = CASE
                    WHEN x.PendingRows > 0 OR x.ProcessingRows > 0 OR x.WaitingPdfRows > 0 THEN N'Processing'
                    WHEN x.FailedRows > 0 OR x.DuplicateRows > 0 OR x.SkippedRows > 0 THEN N'CompletedWithErrors'
                    ELSE N'Completed'
                END,
                [UpdatedAtUtc] = SYSUTCDATETIME()
            FROM [dbo].[TblTransactionReupImportBatch] b
            CROSS APPLY (
                SELECT
                    SUM(CASE WHEN [PublishStatus] = N'Pending' THEN 1 ELSE 0 END) PendingRows,
                    SUM(CASE WHEN [PublishStatus] = N'Processing' THEN 1 ELSE 0 END) ProcessingRows,
                    SUM(CASE WHEN [PublishStatus] IN (N'Published', N'Done') THEN 1 ELSE 0 END) PublishedRows,
                    SUM(CASE WHEN [PublishStatus] IN (N'PublishFailed', N'Error') THEN 1 ELSE 0 END) FailedRows,
                    SUM(CASE WHEN [PublishStatus] = N'WaitingPdf' THEN 1 ELSE 0 END) WaitingPdfRows,
                    SUM(CASE WHEN [PublishStatus] = N'Skipped' THEN 1 ELSE 0 END) SkippedRows,
                    SUM(CASE WHEN [PublishStatus] = N'Duplicate' THEN 1 ELSE 0 END) DuplicateRows
                FROM [dbo].[TblTransactionReupImportItem] WHERE [BatchId] = b.[ID]
            ) x WHERE b.[ID] = @id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = batchId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecalculateItemBatchAsync(int itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        const string sql = "SELECT [BatchId] FROM [dbo].[TblTransactionReupImportItem] WHERE [ID] = @id;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        var batchId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await RecalculateBatchAsync(batchId, cancellationToken);
    }

    private async Task MarkItemErrorAsync(int itemId, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [dbo].[TblTransactionReupImportItem]
            SET [PublishStatus] = @status,
                [ErrorCode] = @errorCode,
                [ErrorMessage] = @errorMessage,
                [PublishMessage] = @errorMessage,
                [PublishLogs] = CASE
                    WHEN NULLIF([PublishLogs], N'') IS NULL THEN @log
                    ELSE CONCAT([PublishLogs], CHAR(13), CHAR(10), @log)
                END,
                [CompletedAtUtc] = SYSUTCDATETIME(),
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [ID] = @id;
            SELECT [BatchId] FROM [dbo].[TblTransactionReupImportItem] WHERE [ID] = @id;
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Error;
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode;
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value = errorMessage;
        command.Parameters.Add("@log", SqlDbType.NVarChar, -1).Value = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {errorCode}: {errorMessage}";
        var batchId = await command.ExecuteScalarAsync(cancellationToken);
        if (batchId is not null && batchId is not DBNull)
        {
            await RecalculateBatchAsync(Convert.ToInt32(batchId, CultureInfo.InvariantCulture), cancellationToken);
        }
    }

    private async Task<TransactionReupBatchViewModel?> GetItemBatchAsync(int itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureSchemaExistsAsync(connection, cancellationToken);
        const string sql = "SELECT [BatchId] FROM [dbo].[TblTransactionReupImportItem] WHERE [ID] = @id;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = itemId;
        var batchId = await command.ExecuteScalarAsync(cancellationToken);
        return batchId is null || batchId is DBNull
            ? null
            : await GetBatchAsync(connection, Convert.ToInt32(batchId, CultureInfo.InvariantCulture), cancellationToken);
    }

    private async Task<TransactionReupItemViewModel?> LoadRetryItemAsync(int itemId, CancellationToken cancellationToken)
    {
        return await GetItemAsync(itemId, cancellationToken);
    }

    private static async Task<TransactionReupBatchViewModel?> GetBatchAsync(SqlConnection connection, int batchId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [ID], [BatchCode], [SourceType], [OriginalFileName], [ImportedByUsername], [ImportedAtUtc],
                   [InvoiceStartNumber], [InvoiceEndNumber], [NextInvoiceNumber], [TotalRows],
                   [ValidRows], [PublishedRows], [FailedRows], [SkippedRows], [DuplicateRows], [Status]
            FROM [dbo].[TblTransactionReupImportBatch] WHERE [ID] = @id;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = batchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapBatch(reader) : null;
    }

    private static TransactionReupBatchViewModel MapBatch(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(reader.GetOrdinal("ID")),
        BatchCode = ReadText(reader, "BatchCode"),
        SourceType = FirstNotEmpty(ReadText(reader, "SourceType"), TransactionReupSourceTypes.ExcelImport),
        OriginalFileName = ReadText(reader, "OriginalFileName"),
        ImportedByUsername = ReadText(reader, "ImportedByUsername"),
        ImportedAtUtc = ReadDate(reader, "ImportedAtUtc") ?? DateTime.UtcNow,
        InvoiceStartNumber = ReadInt(reader, "InvoiceStartNumber"),
        InvoiceEndNumber = ReadInt(reader, "InvoiceEndNumber"),
        NextInvoiceNumber = ReadInt(reader, "NextInvoiceNumber"),
        TotalRows = ReadInt(reader, "TotalRows"),
        ValidRows = ReadInt(reader, "ValidRows"),
        PublishedRows = ReadInt(reader, "PublishedRows"),
        FailedRows = ReadInt(reader, "FailedRows"),
        SkippedRows = ReadInt(reader, "SkippedRows"),
        DuplicateRows = ReadInt(reader, "DuplicateRows"),
        Status = ReadText(reader, "Status")
    };

    private static TransactionReupItemViewModel MapItem(SqlDataReader reader) => new()
    {
        Id = ReadInt(reader, "ID"),
        SourceInvoiceId = ReadInt(reader, "SourceInvoiceId"),
        RowNumber = ReadInt(reader, "RowNumber"),
        SourceTransactionCode = ReadText(reader, "SourceTransactionCode"),
        SourceRequestCode = ReadText(reader, "SourceRequestCode"),
        InvoiceCode = ReadText(reader, "InvoiceCode"),
        GrossAmountVnd = ReadDecimal(reader, "GrossAmountVnd"),
        ValidationStatus = ReadText(reader, "ValidationStatus"),
        PublishStatus = ReadText(reader, "PublishStatus"),
        PublishAttemptCount = ReadInt(reader, "PublishAttemptCount"),
        RabbitMessageId = ReadText(reader, "RabbitMessageId"),
        RabbitCorrelationId = ReadText(reader, "RabbitCorrelationId"),
        PublishMessage = ReadText(reader, "PublishMessage"),
        PublishLogs = ReadText(reader, "PublishLogs"),
        PayloadJson = ReadText(reader, "PayloadJson"),
        PdfFileName = ReadText(reader, "PdfFileName"),
        PdfStorageKey = ReadText(reader, "PdfStorageKey"),
        PdfSize = ReadLong(reader, "PdfSize"),
        PdfSha256 = ReadText(reader, "PdfSha256"),
        PdfContentType = ReadText(reader, "PdfContentType"),
        PdfReceivedAtUtc = ReadDate(reader, "PdfReceivedAtUtc"),
        ErrorCode = ReadText(reader, "ErrorCode"),
        ErrorMessage = ReadText(reader, "ErrorMessage"),
        ProcessingStartedAtUtc = ReadDate(reader, "ProcessingStartedAtUtc"),
        WaitingPdfAtUtc = ReadDate(reader, "WaitingPdfAtUtc"),
        CompletedAtUtc = ReadDate(reader, "CompletedAtUtc"),
        TransactionType = ReadText(reader, "TransactionType"),
        PaymentMethod = ReadText(reader, "PaymentMethod"),
        BankName = ReadText(reader, "BankName"),
        ProcessingFeeVnd = ReadDecimal(reader, "ProcessingFeeVnd"),
        NetAmountVnd = ReadDecimal(reader, "NetAmountVnd"),
        SourceStatus = ReadText(reader, "SourceStatus"),
        PublishedAtUtc = ReadDate(reader, "PublishedAtUtc")
    };

    private static int ReadInt(SqlDataReader reader, string name) => reader[name] is DBNull ? 0 : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);
    private static long ReadLong(SqlDataReader reader, string name) => reader[name] is DBNull ? 0 : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
    private static decimal ReadDecimal(SqlDataReader reader, string name) => reader[name] is DBNull ? 0 : Convert.ToDecimal(reader[name], CultureInfo.InvariantCulture);
    private static DateTime? ReadDate(SqlDataReader reader, string name) => reader[name] is DBNull ? null : Convert.ToDateTime(reader[name], CultureInfo.InvariantCulture);
    private static string ReadText(SqlDataReader reader, string name) => reader[name] is DBNull ? string.Empty : reader[name]?.ToString() ?? string.Empty;
    private static string FirstNotEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 2;
        parameter.Value = value;
    }
}
