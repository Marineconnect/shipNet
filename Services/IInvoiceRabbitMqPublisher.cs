using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IInvoiceRabbitMqPublisher
{
    Task<InvoiceRabbitMqPublishResult> PublishInvoiceAsync(InvoiceRabbitMqPublishRequest request, CancellationToken cancellationToken = default);
    Task<InvoiceRabbitMqPublishResult> PublishInvoiceGenerateEventAsync(InvoiceGenerateEvent invoiceEvent, CancellationToken cancellationToken = default);
    string GetConfigurationSummary();
}
