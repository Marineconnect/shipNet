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
            815
        ]));

        using var document = JsonDocument.Parse(updated);
        Assert.Equal(1, document.RootElement.GetProperty("reup").GetInt32());
        Assert.Equal(815, document.RootElement.GetProperty("reupItemId").GetInt32());
        Assert.Equal("https://example.test/api/transaction-reup/items/815/pdf", document.RootElement.GetProperty("InvoiceURL").GetString());
        Assert.Equal("INV001", document.RootElement.GetProperty("invoiceCode").GetString());
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
