using System.Reflection;
using System.Text.Json;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Tests;

public sealed class TransactionReupSelectionTests
{
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
    public void EnsureReupFlagMarksPayloadAndRemovesInvoiceUrl()
    {
        var method = typeof(TransactionReupService).GetMethod("EnsureReupFlag", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var updated = Assert.IsType<string>(method.Invoke(null, ["{\"transactionCode\":\"T1\",\"invoiceCode\":\"SPN-INV-26-00001\",\"InvoiceURL\":\"https://example.test/invoice.pdf\"}"]));

        using var document = JsonDocument.Parse(updated);
        Assert.Equal(1, document.RootElement.GetProperty("reup").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("InvoiceURL", out _));
        Assert.Equal("SPN-INV-26-00001", document.RootElement.GetProperty("invoiceCode").GetString());
    }
}
