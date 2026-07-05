# Invoice PDF RabbitMQ

When a monthly subscription invoice is created successfully, ShipNet publishes an `invoice.generate` JSON message to RabbitMQ so a PDF worker can generate the invoice file.

The publish happens only after the SQL transaction commits. If RabbitMQ is unavailable, invoice creation still succeeds and the error is logged.

## Configuration

Config file:

```text
appsettings.InvoiceRabbitMq.json
```

Environment variables:

```bash
RABBITMQ_HOST=124.197.28.50
RABBITMQ_PORT=5672
RABBITMQ_USERNAME=minhduc
RABBITMQ_PASSWORD=<set by production secret manager>
RABBITMQ_VHOST=dev
RABBITMQ_QUEUE=nvoice.generate.9pay
```

Optional connection URL:

```bash
RABBITMQ_URL=amqp://minhduc:<password>@124.197.28.50:5672/dev
```

Do not store the real RabbitMQ password in source code or committed appsettings files. In production, set `RABBITMQ_PASSWORD` through IIS environment variables, Windows service environment variables, Docker/Kubernetes secrets, or the hosting platform secret store. Restart the app after changing the secret.

RabbitMQ port `5672` must be open and whitelisted from the server running ShipNet to `124.197.28.50`.

## Test Page

```text
/InvoiceRabbitMq
```

This page validates custom invoice JSON and publishes it to the configured queue with detailed connection/publish logs.

## Runtime Message

```json
{
  "eventId": "d2f637f2-2b2f-4cf2-9f55-5a8f4d75f61f",
  "eventType": "invoice.generate",
  "requestedAt": "2026-07-05T17:05:00+07:00",
  "invoiceId": 123,
  "invoiceCode": "SPN-INV-26-00123",
  "templateCode": "OVERCHARGE",
  "outputFileName": "SPN-INV-26-00123.pdf",
  "invoice": {
    "seller": null,
    "buyer": {
      "tenantName": "Demo Tenant",
      "vesselName": "Demo Vessel",
      "kitId": "KIT-DEMO-001",
      "planName": "50GB"
    },
    "items": [
      {
        "description": "OVERCHARGE",
        "quantity": 10,
        "unitPrice": 100000,
        "amount": 1000000,
        "dataGb": 10,
        "invoiceType": "OVERCHARGE"
      }
    ],
    "subtotal": 1000000,
    "vatAmount": 0,
    "totalAmount": 1000000,
    "currency": "VND",
    "issueDate": "2026-07-05"
  }
}
```

## Publish Behavior

- Queue: `nvoice.generate.9pay`
- Exchange: default exchange
- Routing key: queue name
- Queue declare: durable
- Message delivery: persistent
- Content type: `application/json`
- Encoding: UTF-8
- `messageId`: `eventId`
- `correlationId`: `invoiceId`
- Retry: 3 attempts with exponential backoff
