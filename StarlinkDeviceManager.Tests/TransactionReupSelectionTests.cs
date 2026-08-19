using System.Reflection;
using System.Text.Json;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Tests;

public sealed class TransactionReupSelectionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void TransactionSelectionBatchDisplaysSourceWithoutExcelFile()
    {
        var batch = new TransactionReupBatchViewModel
        {
            SourceType = TransactionReupSourceTypes.TransactionSelection,
            OriginalFileName = string.Empty,
            TotalRows = 12
        };

        Assert.True(batch.IsTransactionSelection);
        Assert.Equal("Transaction History", batch.SourceDisplay);
        Assert.Equal(string.Empty, batch.OriginalFileName);
    }

    [Fact]
    public void EnsureReupFlagMarksPayloadAndKeepsInvoiceUrl()
    {
        var method = typeof(TransactionReupService).GetMethod("EnsureReupFlag", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var updated = Assert.IsType<string>(method.Invoke(null, ["{\"transactionCode\":\"T1\",\"invoiceCode\":\"SPN-INV-26-00001\",\"InvoiceURL\":\"https://example.test/invoice.pdf\"}"]));

        using var document = JsonDocument.Parse(updated);
        Assert.Equal(1, document.RootElement.GetProperty("reup").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("InvoiceURL", out var invoiceUrl));
        Assert.Equal("https://example.test/invoice.pdf", invoiceUrl.GetString());
        Assert.Equal("SPN-INV-26-00001", document.RootElement.GetProperty("invoiceCode").GetString());
    }

    [Fact]
    public void PrepareReupItemPayloadUsesDedicatedItemInvoiceUrl()
    {
        var method = typeof(TransactionReupService).GetMethod("PrepareReupItemPayload", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var updated = Assert.IsType<string>(method.Invoke(null, [
            "{\"transactionCode\":\"PAY001\",\"invoiceCode\":\"INV001\",\"InvoiceURL\":\"https://example.test/api/invoices/INV001/pdf\"}",
            "https://example.test/api/transaction-reup/items/815/pdf",
            "https://example.test/api/transaction-reup/items/815/result",
            815
        ]));

        using var document = JsonDocument.Parse(updated);
        Assert.Equal(1, document.RootElement.GetProperty("reup").GetInt32());
        Assert.Equal(815, document.RootElement.GetProperty("reupItemId").GetInt32());
        Assert.Equal("https://example.test/api/transaction-reup/items/815/pdf", document.RootElement.GetProperty("InvoiceURL").GetString());
        Assert.Equal("https://example.test/api/transaction-reup/items/815/result", document.RootElement.GetProperty("ReupResultURL").GetString());
        Assert.Equal("INV001", document.RootElement.GetProperty("invoiceCode").GetString());
    }

    [Fact]
    public void ReupPayloadContainsDedicatedResultUrl()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var createBody = ExtractMethodBody(service, "public async Task<TransactionReupSelectionResult> CreateFromTransactionSelectionAsync");
        var prepareBody = ExtractMethodBody(service, "private static string PrepareReupItemPayload");

        Assert.Contains("BuildReupItemResultUrl(itemId)", createBody);
        Assert.Contains("node[\"ReupResultURL\"] = reupResultUrl", prepareBody);
        Assert.Contains("api/transaction-reup/items/{itemId.ToString(CultureInfo.InvariantCulture)}/{action}", service);
    }

    [Fact]
    public void TransactionReupApiExposesItemResultCallback()
    {
        var controller = File.ReadAllText(Path.Combine(RepoRoot, "Controllers", "TransactionReupApiController.cs"));

        Assert.Contains("[HttpPost(\"items/{itemId:int}/result\")]", controller);
        Assert.Contains("RecordItemResultAsync(itemId", controller);
        Assert.Contains("ValidateApiKey()", ExtractMethodBody(controller, "public async Task<IActionResult> RecordItemResult"));
    }

    [Fact]
    public void FailureResultCallbackUpdatesOnlyExactItemToError()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var body = ExtractMethodBody(service, "public async Task<bool> RecordItemResultAsync");

        Assert.Contains("GetItemAsync(itemId", body);
        Assert.Contains("WHERE [ID] = @id", body);
        Assert.Contains("WHEN @isFailed = 1 AND [PublishStatus] IN (@waitingPdf, @processing) THEN @error", body);
        Assert.Contains("[CompletedAtUtc] = CASE", body);
        Assert.Contains("RecalculateBatchAsync", body);
        Assert.DoesNotContain("SourceTransactionCode] = @transactionCode", body);
        Assert.DoesNotContain("InvoiceCode] = @invoiceCode", body);
    }

    [Fact]
    public void SuccessResultCallbackDoesNotMarkDoneWithoutPdf()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var resultBody = ExtractMethodBody(service, "public async Task<bool> RecordItemResultAsync");
        var pdfBody = ExtractMethodBody(service, "public async Task<TransactionReupPdfCallbackResult> SaveItemPdfAsync");

        Assert.Contains("WHEN @isFailed = 1 AND [PublishStatus] IN (@waitingPdf, @processing) THEN @error", resultBody);
        Assert.DoesNotContain("WHEN @isFailed = 0", resultBody);
        Assert.Contains("waiting for PDF callback", resultBody);
        Assert.Contains("command.Parameters.Add(\"@status\", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Done", pdfBody);
    }

    [Fact]
    public void PublishResultCannotRegressDoneOrReceivedPdf()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var body = ExtractMethodBody(service, "private async Task UpdatePublishResultAsync");

        Assert.Contains("WHEN [PublishStatus] = @done OR [PdfReceivedAtUtc] IS NOT NULL THEN @done", body);
        Assert.Contains("WHEN [PublishStatus] = @error THEN @error", body);
        Assert.Contains("WHEN @success = 1 THEN @waitingPdf", body);
        Assert.Contains("ELSE @publishFailed", body);
        Assert.Contains("WHEN [PublishStatus] = @done OR [PdfReceivedAtUtc] IS NOT NULL OR [PublishStatus] = @error THEN [PublishMessage]", body);
        Assert.Contains("WHEN [PublishStatus] = @done OR [PdfReceivedAtUtc] IS NOT NULL OR [PublishStatus] = @error THEN [PublishLogs]", body);
        Assert.Contains("WHEN [PublishStatus] = @done OR [PdfReceivedAtUtc] IS NOT NULL OR [PublishStatus] = @error THEN [ErrorCode]", body);
        Assert.Contains("WHEN [PublishStatus] = @done OR [PdfReceivedAtUtc] IS NOT NULL OR [PublishStatus] = @error THEN [ErrorMessage]", body);
        Assert.Contains("command.Parameters.Add(\"@error\", SqlDbType.NVarChar, 30).Value = TransactionReupStatuses.Error", body);
    }

    [Fact]
    public void StaleExternalReferenceCallbacksAreRejected()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var validatorBody = ExtractMethodBody(service, "private static void ValidateCurrentAttempt");
        var resultBody = ExtractMethodBody(service, "public async Task<bool> RecordItemResultAsync");
        var pdfBody = ExtractMethodBody(service, "public async Task<TransactionReupPdfCallbackResult> SaveItemPdfAsync");

        Assert.Contains("REUP-CALLBACK-STALE-ATTEMPT", validatorBody);
        Assert.Contains("item.RabbitMessageId", validatorBody);
        Assert.Contains("externalReference.Trim()", validatorBody);
        Assert.Contains("ValidateCurrentAttempt(item, request.ExternalReference)", resultBody);
        Assert.Contains("ValidateCurrentAttempt(item, externalReference)", pdfBody);
    }

    [Fact]
    public void PdfCallbackIsIdempotentForSameDonePdfAndConflictsOnDifferentPdf()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var body = ExtractMethodBody(service, "public async Task<TransactionReupPdfCallbackResult> SaveItemPdfAsync");

        Assert.Contains("ComputeSha256Async(file", body);
        Assert.Contains("string.Equals(item.PdfSha256, incomingSha256", body);
        Assert.Contains("REUP-PDF-CALLBACK-CONFLICT", body);
        Assert.True(body.IndexOf("ComputeSha256Async(file", StringComparison.Ordinal) <
            body.IndexOf("fileStorage.SavePdfAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void TransactionSelectionBeginDoesNotSynchronouslyPublishBatch()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var body = ExtractMethodBody(service, "public async Task<TransactionReupSelectionResult> CreateFromTransactionSelectionAsync");

        Assert.DoesNotContain("PublishPendingItemsAsync", body);
        Assert.Contains("InsertTransactionSelectionBatchAsync", body);
        Assert.Contains("InsertTransactionSelectionItemAsync", body);
        Assert.Contains("CommitAsync", body);
    }

    [Fact]
    public void PendingWorkerIsRegisteredAndUsesClaimedProcessing()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "Program.cs"));
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var claimBody = ExtractMethodBody(service, "private async Task<bool> ClaimPendingItemAsync");

        Assert.Contains("AddHostedService<TransactionReupPendingWorker>", program);
        Assert.Contains("[PublishStatus] = @processing", claimBody);
        Assert.Contains("WHERE [ID] = @id AND [PublishStatus] = @pending", claimBody);
    }

    [Fact]
    public void CallbackValidationDoesNotTerminallyFailWaitingItemForMalformedRequest()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var saveBody = ExtractMethodBody(service, "public async Task<TransactionReupPdfCallbackResult> SaveItemPdfAsync");

        Assert.DoesNotContain("MarkItemErrorAsync(itemId, \"REUP-PDF-MISSING\"", saveBody);
        Assert.DoesNotContain("MarkItemErrorAsync(itemId, \"REUP-PDF-CALLBACK-MISMATCH\"", saveBody);
        Assert.Contains("MarkItemErrorAsync(itemId, \"REUP-PDF-INVALID\"", saveBody);
    }

    [Fact]
    public void ReupSelectionCacheClearsOnlyOnSuccessfulBeginMarker()
    {
        var details = File.ReadAllText(Path.Combine(RepoRoot, "Views", "TransactionReup", "Details.cshtml"));
        var transactions = File.ReadAllText(Path.Combine(RepoRoot, "Controllers", "TransactionsController.cs"));

        Assert.Contains("clearReupSelection = true", transactions);
        Assert.Contains("Context.Request.Query[\"clearReupSelection\"] == \"true\"", details);
        Assert.Contains("sessionStorage.removeItem(\"shipnet.transactionHistory.reupPdf.selection.v1\")", details);
    }

    [Fact]
    public void ProcessPendingRecoversStaleProcessingBeforePendingPublish()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var processBody = ExtractMethodBody(service, "public async Task<int> ProcessPendingAsync");

        Assert.True(processBody.IndexOf("RecoverStaleProcessingItemsAsync", StringComparison.Ordinal) <
            processBody.IndexOf("PublishPendingItemsAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void StaleProcessingRecoveryUsesConfiguredUtcTimeout()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var recoveryBody = ExtractMethodBody(service, "private async Task<int> RecoverStaleProcessingItemsAsync");

        Assert.Contains("TransactionReup:ProcessingStaleMinutes", recoveryBody);
        Assert.Contains("GetValue(\"TransactionReup:ProcessingStaleMinutes\", 10)", recoveryBody);
        Assert.Contains("Math.Clamp", recoveryBody);
        Assert.Contains("1, 120", recoveryBody);
        Assert.Contains("SYSUTCDATETIME()", recoveryBody);
        Assert.Contains("DATEADD(minute, -@staleMinutes, SYSUTCDATETIME())", recoveryBody);
    }

    [Fact]
    public void StaleProcessingRecoveryOnlyMovesStaleProcessingToPending()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var recoveryBody = ExtractMethodBody(service, "private async Task<int> RecoverStaleProcessingItemsAsync");

        Assert.Contains("WHERE i.[PublishStatus] = @processing", recoveryBody);
        Assert.Contains("i.[ProcessingStartedAtUtc] IS NOT NULL", recoveryBody);
        Assert.Contains("i.[ProcessingStartedAtUtc] < DATEADD", recoveryBody);
        Assert.Contains("[PublishStatus] = @pending", recoveryBody);
        Assert.DoesNotContain("WaitingPdf", recoveryBody);
        Assert.DoesNotContain("Done", recoveryBody);
        Assert.DoesNotContain("PublishFailed", recoveryBody);
        Assert.DoesNotContain("INSERT INTO [dbo].[TblTransactionReupImportItem]", recoveryBody);
        Assert.DoesNotContain("INSERT INTO [dbo].[TblTransactionReupImportBatch]", recoveryBody);
    }

    [Fact]
    public void RecoveredItemsStillUseAtomicPendingClaim()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot, "Services", "TransactionReupService.cs"));
        var publishBody = ExtractMethodBody(service, "private async Task<int> PublishPendingItemsAsync");
        var claimBody = ExtractMethodBody(service, "private async Task<bool> ClaimPendingItemAsync");

        Assert.Contains("ClaimPendingItemAsync(item.Id", publishBody);
        Assert.Contains("WHERE [ID] = @id AND [PublishStatus] = @pending", claimBody);
        Assert.Contains("RecalculateBatchAsync(batchId", ExtractMethodBody(service, "private async Task<int> RecoverStaleProcessingItemsAsync"));
    }

    private static string ExtractMethodBody(string source, string signatureStart)
    {
        var signatureIndex = source.IndexOf(signatureStart, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Missing method: {signatureStart}");
        var braceIndex = source.IndexOf('{', signatureIndex);
        Assert.True(braceIndex >= 0, $"Missing method body: {signatureStart}");

        var depth = 0;
        for (var index = braceIndex; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[braceIndex..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Could not parse method body: {signatureStart}");
    }
}
