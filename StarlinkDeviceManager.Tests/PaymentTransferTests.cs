using Microsoft.Extensions.Configuration;
using StarlinkDeviceManager.Services;

public sealed class PaymentTransferTests
{
    private static PaymentTransactionService CreateService() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=invalid;Database=unused"
        }).Build(), null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsMissingReasonBeforeOpeningDatabase(string reason)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().TransferTransactionAsync(1, ["INV-2"], reason, "admin", null, null));
    }

    [Fact]
    public async Task RejectsDuplicateInvoiceCodesIgnoringCaseAndWhitespace()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().TransferTransactionAsync(1, [" INV-2 ", "inv-2"], "Correction", "admin", null, null));
    }

    [Fact]
    public async Task RejectsEmptyTargetList()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().TransferTransactionAsync(1, [], "Correction", "admin", null, null));
    }

    [Fact]
    public async Task RejectsOversizedReason()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().TransferTransactionAsync(1, ["INV-2"], new string('x', 1001), "admin", null, null));
    }

    [Fact]
    public async Task RejectsBlankInvoiceCode()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().ValidateTransferInvoiceAsync(" ", null, null));
    }
}
