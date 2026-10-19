namespace StarlinkDeviceManager.Models;
public sealed record TransferInvoiceCandidate(int Id, int SubscriptionId, string InvoiceNumber, decimal Amount);
public sealed class TransferTransactionRequest
{
    public int SourceInvoiceId { get; set; }
    public List<string> InvoiceNumbers { get; set; } = [];
    public string Reason { get; set; } = "";
}
