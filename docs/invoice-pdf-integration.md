# Invoice PDF Integration

## Overview

Flow:

1. Customer pays an invoice in ShipNet.
2. ShipNet marks `TblSubscriptionInvoice` as paid.
3. ShipNet publishes the existing invoice payload to RabbitMQ.
4. The payload includes `InvoiceURL`.
5. The external invoice generator uploads the PDF to `POST /api/invoices/{invoiceCode}/pdf`.
6. ShipNet validates, stores, versions, and links the PDF to the invoice.
7. Authenticated ShipNet users can view, download, upload, replace, and delete the current PDF from subscription invoice detail.

Invoice metadata is stored in `dbo.TblInvoicePdf`. Physical PDFs are stored outside the application and are never exposed through static files.

## Appsettings

```json
{
  "InvoicePdfIntegration": {
    "PublicBaseUrl": "https://YOUR-SHIPNET-DOMAIN",
    "ApiKey": "",
    "HeaderName": "X-ShipNet-Api-Key"
  },
  "InvoicePdfStorage": {
    "RootPath": "D:\\ShipNetData\\InvoicePdfs",
    "MaxFileSizeMb": 20
  },
  "InvoiceIntegrationLog": {
    "RetentionDays": 180,
    "MaxPayloadDisplayLength": 200000
  }
}
```

Use IIS environment variables for production secrets:

```text
InvoicePdfIntegration__PublicBaseUrl
InvoicePdfIntegration__ApiKey
InvoicePdfIntegration__HeaderName
InvoicePdfStorage__RootPath
InvoicePdfStorage__MaxFileSizeMb
InvoiceIntegrationLog__RetentionDays
InvoiceIntegrationLog__MaxPayloadDisplayLength
```

Do not put the API key in a URL or in frontend code.

Generate a shared API key outside source control:

```powershell
$keyBytes = New-Object byte[] 48
[System.Security.Cryptography.RandomNumberGenerator]::Fill($keyBytes)
[Convert]::ToBase64String($keyBytes)
```

Or with .NET:

```csharp
using System.Security.Cryptography;
var apiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
Console.WriteLine(apiKey);
```

Store the same value in ShipNet `InvoicePdfIntegration:ApiKey` and the PDF generator configuration. Send it only through `X-ShipNet-Api-Key`.

## RabbitMQ Payload

ShipNet keeps the existing payload and adds:

```json
{
  "InvoiceURL": "https://YOUR-SHIPNET-DOMAIN/api/invoices/SHIPNET-INV-2026-00001/pdf"
}
```

`InvoiceURL` is built only from `InvoicePdfIntegration:PublicBaseUrl`. ShipNet does not use `Request.Host` or forwarded headers as a fallback.

RabbitMQ publish audit stores the exact JSON string sent to RabbitMQ in `TblInvoiceIntegrationLog.PayloadJson`. The publisher sets RabbitMQ `BasicProperties.MessageId` and `BasicProperties.CorrelationId`; messages remain persistent when `InvoiceRabbitMq:PersistentMessages` is enabled, and `Succeeded` means broker ACK when publisher confirms are enabled.

## PDF Generator Contract

The PDF generator should:

- Consume the RabbitMQ message and read `invoiceCode`, `transactionCode`, `InvoiceURL`, and the invoice data.
- Prefer `message.InvoiceURL`; do not rebuild the endpoint when it is present.
- Validate that `InvoiceURL` is absolute, HTTPS in production, and belongs to an allowed ShipNet domain.
- Generate the PDF, then `POST multipart/form-data` directly to `InvoiceURL`.
- Send header `X-ShipNet-Api-Key: <shared-secret>`.
- Send form fields: `file`, `transactionCode`, `sourceSystem`, `generatedAt`, `externalReference`.
- Use a 60 to 120 second timeout.
- Retry temporary failures: `408`, `429`, `500`, `502`, `503`, `504`.
- Do not retry indefinitely for `400`, `401`, `403`, `404`, `409`, `413`, `415`.
- Log invoice code, HTTP status, response body, and duration, but never log the API key.
- Send RabbitMQ `MessageId` or task id back as `externalReference` when available.

## External Upload API

```powershell
curl.exe -X POST `
  "https://localhost:5001/api/invoices/SHIPNET-INV-2026-00001/pdf" `
  -H "X-ShipNet-Api-Key: YOUR_API_KEY" `
  -F "file=@C:\Temp\SHIPNET-INV-2026-00001.pdf;type=application/pdf" `
  -F "sourceSystem=InvoiceGenerator" `
  -F "transactionCode=test PO" `
  -F "generatedAt=2026-07-22T10:30:00Z" `
  -F "externalReference=rabbit-message-id"
```

Equivalent C#:

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, invoiceUrl);
request.Headers.TryAddWithoutValidation("X-ShipNet-Api-Key", shipNetApiKey);

using var multipart = new MultipartFormDataContent();
await using var fileStream = File.OpenRead(pdfPath);
using var fileContent = new StreamContent(fileStream);
fileContent.Headers.ContentType =
    new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

multipart.Add(fileContent, "file", Path.GetFileName(pdfPath));
multipart.Add(new StringContent(transactionCode ?? ""), "transactionCode");
multipart.Add(new StringContent("InvoiceGenerator"), "sourceSystem");
multipart.Add(new StringContent(DateTime.UtcNow.ToString("O")), "generatedAt");
multipart.Add(new StringContent(messageId ?? ""), "externalReference");

request.Content = multipart;

using var response = await httpClient.SendAsync(request, cancellationToken);
var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
```

Optional form fields:

```text
transactionCode
sourceSystem
generatedAt
externalReference
```

Validation:

- API key header.
- Invoice existence and unique mapping.
- `.pdf` extension.
- PDF content type.
- `%PDF-` magic bytes.
- Max file size.
- SHA-256 idempotency.

## View And Download

Authenticated users can view/download through scoped API endpoints:

```powershell
curl.exe -L `
  "https://localhost:5001/api/invoices/SHIPNET-INV-2026-00001/pdf/file" `
  -o "C:\Temp\invoice-view.pdf"

curl.exe -L `
  "https://localhost:5001/api/invoices/SHIPNET-INV-2026-00001/pdf/file?download=true" `
  -o "C:\Temp\invoice-download.pdf"
```

Tenant-scoped and device-scoped users are restricted by the same subscription invoice scope used in ShipNet.

## Error Contract

```json
{
  "success": false,
  "errorCode": "invalid_pdf",
  "message": "File không phải định dạng PDF hợp lệ.",
  "messageEn": "The uploaded file is not a valid PDF."
}
```

Status codes include `400`, `401`, `403`, `404`, `409`, `413`, `415`, `500`, and `503`.

## SQL Migration

Run manually:

```text
Database/Scripts/20260722_AddInvoicePdfStorage.sql
Database/Scripts/20260722_AddInvoiceIntegrationLog.sql
```

Created objects:

- `dbo.TblInvoicePdf`
- `IX_TblInvoicePdf_InvoiceId`
- `IX_TblInvoicePdf_InvoiceCode`
- `IX_TblInvoicePdf_Sha256`
- `UX_TblInvoicePdf_Current`
- `UX_TblInvoicePdf_Version`
- `FK_TblInvoicePdf_TblSubscriptionInvoice`
- `dbo.TblInvoiceIntegrationLog`
- `IX_TblInvoiceIntegrationLog_InvoiceId_CreatedAt`
- `IX_TblInvoiceIntegrationLog_InvoiceCode_CreatedAt`
- `IX_TblInvoiceIntegrationLog_EventType_CreatedAt`
- `IX_TblInvoiceIntegrationLog_MessageId`
- `IX_TblInvoiceIntegrationLog_CorrelationId`

The script is idempotent and non-destructive.

## IIS

1. Create:

```powershell
New-Item -ItemType Directory -Force "D:\ShipNetData\InvoicePdfs"
```

2. Grant Modify permission to the app pool identity:

```powershell
icacls "D:\ShipNetData\InvoicePdfs" /grant "IIS AppPool\<AppPoolName>:(OI)(CI)M"
```

3. Do not enable Directory Browsing.
4. Do not create a public virtual directory to the PDF folder.
5. Set production environment variables.
6. Restart the app pool.

## Retry And Versioning

- Same SHA-256: returns `unchanged = true`, version does not increase.
- New file: increments version and marks the new row as current.
- Only one current PDF is allowed per invoice by filtered unique index.

## Integration Logs

Persistent audit events are stored in `dbo.TblInvoiceIntegrationLog` and visible on subscription invoice detail under `Lịch sử tích hợp / Integration Logs`.

Events include:

- `RabbitMqPublishStarted`
- `RabbitMqPublishSucceeded`
- `RabbitMqPublishFailed`
- `InvoicePdfReceiveStarted`
- `InvoicePdfReceiveSucceeded`
- `InvoicePdfReceiveFailed`
- `InvoicePdfReplaced`
- `InvoicePdfDeleted`

UI/API endpoints:

```text
GET /api/invoices/{invoiceCode}/integration-logs?page=1&pageSize=20
GET /api/invoices/{invoiceCode}/integration-logs/{logId}
```

The list endpoint omits `PayloadJson`. The detail endpoint returns `PayloadJson` only when a user clicks `Xem JSON`. Payload display is capped by `InvoiceIntegrationLog:MaxPayloadDisplayLength`.

Do not store or log API keys, RabbitMQ passwords, cookies, authorization headers, connection strings, PDF binary data, full request headers, or physical file paths.

## Rollback

1. Stop new uploads.
2. Restore the previous application build.
3. If needed, drop `TblInvoicePdf` indexes/FK/table manually.
4. If needed, drop `TblInvoiceIntegrationLog` manually after exporting required audit records.
5. Keep or archive physical files under `InvoicePdfStorage:RootPath`.

## Troubleshooting

- `api_key_not_configured`: set `InvoicePdfIntegration__ApiKey`.
- `storage_error`: check `InvoicePdfStorage__RootPath` and IIS App Pool Modify permission.
- `invoice_not_found`: confirm the external system uses the exact `invoiceCode` from RabbitMQ `InvoiceURL`.
- `invalid_pdf`: confirm the file starts with `%PDF-` and is uploaded as `application/pdf`.
