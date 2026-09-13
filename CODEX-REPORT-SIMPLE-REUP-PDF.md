# Codex Simple Reup PDF Report

## 1. Summary
Simplified Transaction History Reup PDF so ShipNet only republishes the normal invoice JSON to RabbitMQ. The PDF worker remains unchanged and continues to own PDF generation/output after RabbitMQ.

## 2. Previous flow
Transaction History Reup PDF previously treated Reup as a separate callback protocol:

- Build canonical invoice payload.
- Insert Transaction Selection Reup item.
- Mutate payload with `InvoiceURL`, `ReupResultURL`, `reupItemId`, and `reup`.
- Publish to RabbitMQ.
- Mark item `WaitingPdf`.
- Wait for worker/PDF callbacks to reach `Done`.

This made ShipNet wait for PDF callback state that is not required for normal PDF generation.

## 3. New flow
Transaction History Reup PDF now uses replay semantics:

- Existing invoice is selected in Transaction History.
- ShipNet uses `SourceInvoiceId` / existing invoice id.
- ShipNet rebuilds the normal invoice PDF payload with `PaymentTransactionService.BuildInvoicePdfPayloadAsync(...)`.
- ShipNet publishes that normal payload to RabbitMQ.
- Successful publish sets item status to `Published`.
- Batch completes once every item is `Published`.
- ShipNet does not wait for PDF callback.

## 4. Files changed
- `Services/TransactionReupService.cs`
  - Removed Transaction Selection use of `PrepareReupItemPayload(...)`.
  - Stopped adding callback/reup fields for Transaction Selection publish.
  - Added SourceType-aware publishing.
  - Rebuilds canonical payload from `SourceInvoiceId` before publishing Transaction Selection pending/retry items, including legacy rows.
  - Marks Transaction Selection successful Rabbit publishes as `Published`.
  - Keeps Excel import behavior distinct and still allows its existing `WaitingPdf` flow.
- `Views/TransactionReup/Details.cshtml`
  - Transaction History Reup summary now shows Pending / Processing / Published / Error.
  - Hides PDF callback column for Transaction Selection batches.
  - Shows Rabbit message id and Published At for publish-only Reup items.
- `StarlinkDeviceManager.Tests/TransactionReupSelectionTests.cs`
  - Updated regression coverage for canonical payload replay and publish-only lifecycle.

## 5. Canonical payload builder used
Transaction Selection Reup uses:

`PaymentTransactionService.BuildInvoicePdfPayloadAsync(invoiceId, sourceTransactionCode, null, username, cancellationToken)`

This preserves existing invoice data including transaction code, invoice code, source, payment time, operator, PO fields, invoice params, vessel/subscription details, and other fields already handled by the canonical invoice payload builder.

## 6. Callback fields removed
For Transaction History Reup PDF, ShipNet no longer adds or publishes:

- `InvoiceURL`
- `ReupResultURL`
- `reupItemId`
- `reup`

Legacy helper methods/endpoints remain in code for backward compatibility, but the Transaction Selection flow no longer calls them.

## 7. Status transition changes
For Transaction Selection batches:

- `Pending`
- `Processing`
- `Published`

Successful RabbitMQ publish sets:

- `PublishStatus = Published`
- `PublishMessage = Reup PDF request published successfully.`
- `PublishedAtUtc = SYSUTCDATETIME()`
- `CompletedAtUtc = SYSUTCDATETIME()`
- `WaitingPdfAtUtc = NULL`

Rabbit failure still sets:

- `PublishStatus = PublishFailed`
- error code/message/logs
- attempt count

Excel import Reup remains separate and may still use `WaitingPdf` where that flow requires it.

## 8. Batch status behavior
`RecalculateBatchAsync(...)` already counts `Published` and `Done` as successful rows.

For new Transaction Selection batches:

- All items `Published` => batch `Completed`.
- Any `Pending` or `Processing` => batch `Processing`.
- Any `PublishFailed` or `Error` => batch `CompletedWithErrors`.

Historical `WaitingPdf` rows can still be displayed safely and are not destructively migrated.

## 9. Retry behavior
Retry still supports:

- `PublishFailed`
- `Error`

For Transaction Selection retry, ShipNet rebuilds the canonical invoice payload from `SourceInvoiceId` before publishing. This prevents legacy stored payloads containing `InvoiceURL`, `ReupResultURL`, `reupItemId`, or `reup` from being republished.

For Excel import retry, ShipNet preserves the existing Reup flag behavior.

## 10. Tests
Commands run:

- `dotnet restore StarlinkDeviceManager.sln`: PASS.
- `dotnet build StarlinkDeviceManager.sln --no-restore`: PASS with existing nullable warnings in `Services/DeviceService.cs`.
- `dotnet test StarlinkDeviceManager.sln --no-build`: PASS, 93 passed, 0 failed, 0 skipped.

Regression coverage now verifies:

- Transaction History Reup uses `SourceInvoiceId`.
- Existing transaction code is passed to the canonical payload builder.
- Existing invoice code is preserved through the canonical payload result.
- Transaction Selection create flow does not call callback payload mutation helpers.
- Pending worker and retry rebuild canonical payload from `SourceInvoiceId`.
- Transaction Selection publish does not add the Reup flag.
- Successful Rabbit publish can become `Published`.
- Successful Transaction Selection publish does not become `WaitingPdf`.
- UI hides Waiting PDF as an active Transaction Selection lifecycle state.
- Rabbit failure continues to use `PublishFailed`.
- Excel Reup behavior is not globally converted away from `WaitingPdf`.

## 11. Build result
PASS.

Warnings seen during build are existing nullable warnings in `Services/DeviceService.cs` and are unrelated to this change.

## 12. Git
- Branch: `main`.
- Commit SHA: `f78588e4fda5e91d627664be34ab26b58a6283e0`.
- Push status: pushed to `origin/main`.

## 13. Deployment requirements
- Deploy ShipNet only.
- Do not deploy or modify `Marineconnect/marineconnect-9pay-audit`.
- No database migration is required.
- No RabbitMQ route change is required; existing `invoice.generate.9pay` publishing remains in use.
