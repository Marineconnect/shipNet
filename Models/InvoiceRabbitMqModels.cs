using System.ComponentModel.DataAnnotations;

namespace StarlinkDeviceManager.Models;

public class InvoiceRabbitMqOptions
{
    public const string SectionName = "InvoiceRabbitMq";

    public bool Enabled { get; set; }
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseSsl { get; set; }
    public string ExchangeName { get; set; } = string.Empty;
    public string ExchangeType { get; set; } = "direct";
    public string QueueName { get; set; } = "nvoice.generate.9pay";
    public string RoutingKey { get; set; } = "nvoice.generate.9pay";
    public bool DeclareExchange { get; set; }
    public bool DeclareQueue { get; set; } = true;
    public bool BindQueue { get; set; }
    public bool Durable { get; set; } = true;
    public bool PersistentMessages { get; set; } = true;
    public bool PublisherConfirms { get; set; } = true;
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 3;
    public int RetryBaseDelayMilliseconds { get; set; } = 500;
}

public class InvoiceRabbitMqTestViewModel
{
    [Required(ErrorMessage = "Please enter invoice JSON.")]
    public string InvoiceJson { get; set; } = DefaultInvoiceJson;

    public string RoutingKeyOverride { get; set; } = string.Empty;
    public bool Published { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ConfigSummary { get; set; } = string.Empty;
    public List<string> Logs { get; set; } = [];

    public static string DefaultInvoiceJson => """
{
  "invoiceNumber": "SPN-INV-TEST-001",
  "tenantName": "Demo Tenant",
  "deviceKitNumber": "KIT-DEMO-001",
  "billingPeriod": "2026-07",
  "currency": "VND",
  "totalAmount": 5704400,
  "items": [
    {
      "description": "Monthly subscription",
      "quantity": 1,
      "unitPrice": 5704400,
      "amount": 5704400
    }
  ]
}
""";
}

public class InvoiceRabbitMqPublishRequest
{
    public string InvoiceJson { get; set; } = string.Empty;
    public string RoutingKeyOverride { get; set; } = string.Empty;
    public int? UserId { get; set; }
    public string Username { get; set; } = "system";
}

public class InvoiceRabbitMqPublishResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public List<string> Logs { get; set; } = [];
}

public class InvoiceGenerateEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = "invoice.generate";
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.Now;
    public int InvoiceId { get; set; }
    public string InvoiceCode { get; set; } = string.Empty;
    public string? TemplateCode { get; set; }
    public string OutputFileName { get; set; } = string.Empty;
    public InvoiceGeneratePayload Invoice { get; set; } = new();
}

public class InvoiceGeneratePayload
{
    public object? Seller { get; set; }
    public object? Buyer { get; set; }
    public List<InvoiceGenerateItem> Items { get; set; } = [];
    public decimal Subtotal { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "VND";
    public string IssueDate { get; set; } = DateTimeOffset.Now.ToString("yyyy-MM-dd");
}

public class InvoiceGenerateItem
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public decimal? DataGb { get; set; }
    public string? InvoiceType { get; set; }
}
