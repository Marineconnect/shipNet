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
```

Do not put the API key in a URL or in frontend code.

## RabbitMQ Payload

ShipNet keeps the existing payload and adds:

```json
{
  "InvoiceURL": "https://YOUR-SHIPNET-DOMAIN/api/invoices/SHIPNET-INV-2026-00001/pdf"
}
```

`InvoiceURL` is built only from `InvoicePdfIntegration:PublicBaseUrl`. ShipNet does not use `Request.Host` or forwarded headers as a fallback.

## External Upload API

```powershell
curl.exe -X POST `
  "https://localhost:5001/api/invoices/SHIPNET-INV-2026-00001/pdf" `
  -H "X-ShipNet-Api-Key: YOUR_API_KEY" `
  -F "file=@C:\Temp\SHIPNET-INV-2026-00001.pdf;type=application/pdf" `
  -F "sourceSystem=InvoiceGenerator" `
  -F "transactionCode=test PO"
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
```

Created objects:

- `dbo.TblInvoicePdf`
- `IX_TblInvoicePdf_InvoiceId`
- `IX_TblInvoicePdf_InvoiceCode`
- `IX_TblInvoicePdf_Sha256`
- `UX_TblInvoicePdf_Current`
- `UX_TblInvoicePdf_Version`
- `FK_TblInvoicePdf_TblSubscriptionInvoice`

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

## Rollback

1. Stop new uploads.
2. Restore the previous application build.
3. If needed, drop `TblInvoicePdf` indexes/FK/table manually.
4. Keep or archive physical files under `InvoicePdfStorage:RootPath`.

## Troubleshooting

- `api_key_not_configured`: set `InvoicePdfIntegration__ApiKey`.
- `storage_error`: check `InvoicePdfStorage__RootPath` and IIS App Pool Modify permission.
- `invoice_not_found`: confirm the external system uses the exact `invoiceCode` from RabbitMQ `InvoiceURL`.
- `invalid_pdf`: confirm the file starts with `%PDF-` and is uploaded as `application/pdf`.
