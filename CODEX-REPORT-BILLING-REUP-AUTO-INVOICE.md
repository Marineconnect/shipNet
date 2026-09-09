# Codex Implementation Report

## 1. Summary
Implemented the Billing & Invoice transaction-column update and Transaction Reup import improvements.

- Billing & Invoice now shows `Transaction` and displays only `TransactionCode`.
- Billing CSV export now exports `Transaction`/`TransactionCode` instead of `Payment Method`/`PaymentMethod`.
- Transaction Reup now has an admin-only Excel template download generated with NPOI.
- Parser and template share a single `TransactionReupImportColumn` schema.
- `StartInvoiceNumber = 0` is valid and resolves to the latest existing invoice sequence + 1 in the backend.
- Manual and automatic imports check generated invoice codes against existing invoice codes before inserting.
- Batch history and import result store/return the resolved start number, not the original `0`.

## 2. Root cause / Current behavior
The previous Billing & Invoice table had a `Phương thức` column that rendered `PaymentMethod` and then showed `TransactionCode` as muted secondary text. The CSV export also emitted `Payment Method` from `PaymentMethod`.

Transaction Reup previously rejected `StartInvoiceNumber <= 0` in both model/UI/backend validation. `ImportAsync` used `model.StartInvoiceNumber` directly as the first sequence, inserted the batch with that same value, and then assigned invoice sequences only while processing valid rows.

There was no Transaction Reup template download action or generated XLSX template.

## 3. Files changed
- `Views/BillingInvoice/Index.cshtml`: replaced `Phương thức` header with `Transaction`; cell now renders only `item.TransactionCode` or `-`.
- `Services/BillingInvoiceReportService.cs`: changed CSV header/value from `Payment Method`/`PaymentMethod` to `Transaction`/`TransactionCode`.
- `Models/TransactionReupModels.cs`: changed `StartInvoiceNumber` validation range to `0..int.MaxValue`.
- `Views/TransactionReup/Index.cshtml`: added template download button, `min="0"`, helper text, zero-mode confirmation, and double-submit guard.
- `wwwroot/css/site.css`: added small layout helpers for the file input/template button row and helper text.
- `Controllers/TransactionReupController.cs`: added admin-gated `DownloadTemplate` action.
- `Services/ITransactionReupService.cs`: added `GenerateImportTemplate()`.
- `Services/TransactionReupService.cs`: added shared import schema, NPOI template generation, backend auto-numbering, duplicate invoice-code checks, and resolved batch/result numbering.
- `StarlinkDeviceManager.Tests/PricingCostAndBillingCsvTests.cs`: added Billing UI/CSV regression test.
- `StarlinkDeviceManager.Tests/TransactionReupSelectionTests.cs`: added template, zero-numbering, and duplicate-check regression tests.

## 4. Invoice auto-numbering implementation
`StartInvoiceNumber > 0` keeps manual behavior by using the entered number.

`StartInvoiceNumber == 0` resolves inside the existing `BeginTransactionAsync(IsolationLevel.Serializable)` transaction by calling `GetLatestInvoiceSequenceAsync(...) + 1`.

The MAX source is:
- `dbo.TblSubscriptionInvoice.InvoiceNumber` for real subscription invoices matching `SPN-INV-YY-xxxxx`.
- `dbo.TblTransactionReupImportItem.InvoiceSequence`, falling back to `InvoiceCode` suffix, for prior Excel Transaction Reup imports matching the same invoice-code pattern.

This is the correct source for this change because monthly invoices are persisted in `TblSubscriptionInvoice`, while Excel Transaction Reup-created invoice IDs are persisted on `TblTransactionReupImportItem`. Transaction-selection reup reuses existing invoice codes and does not allocate sequence numbers.

Concurrency is protected by:
- The existing Serializable SQL transaction.
- `WITH (UPDLOCK, HOLDLOCK)` reads on both invoice sources when resolving MAX.
- `WITH (UPDLOCK, HOLDLOCK)` conflict checks against both invoice sources before any batch/item inserts.
- Batch insert, valid-row sequence assignment, item insert, and commit all happen in the same transaction.

Manual duplicate prevention:
- The service builds the real candidate invoice codes for the valid rows.
- Before inserting the batch/items, it checks those codes against `TblSubscriptionInvoice.InvoiceNumber` and `TblTransactionReupImportItem.InvoiceCode`.
- If any conflict exists, import is rejected with: `Invoice ID range conflicts with existing invoice IDs. Please choose another starting ID or enter 0 for automatic numbering.`

Only rows validated as `Valid` receive `InvoiceSequence`/`InvoiceCode`; invalid rows keep sequence `0` and empty invoice code.

## 5. Excel template schema
Generated sheet name: `Transaction Reup`.

Headers:
1. `Thời gian khởi tạo`
2. `Thời gian cập nhật`
3. `Mã giao dịch`
4. `Mã yêu cầu mã hóa đơn`
5. `Mã yêu cầu gốc`
6. `Người tạo hóa đơn`
7. `Loại giao dịch`
8. `Phương thức thanh toán`
9. `Ngân hàng/thương hiệu thẻ`
10. `Tổng giá trị VND`
11. `Phí xử lý`
12. `Nội dung chuyển khoản`
13. `Đối tượng chịu phí`
14. `Số tiền thực nhận`
15. `Trạng thái`

The template generator uses the same `ImportSchema` that `MapRows()` uses for parsing.

## 6. Tests performed
- `dotnet restore StarlinkDeviceManager.sln`: PASS.
- `dotnet build StarlinkDeviceManager.sln --no-restore`: PASS with 6 pre-existing nullable warnings in `Services/DeviceService.cs`.
- `dotnet test StarlinkDeviceManager.sln --no-build`: PASS, 90 passed, 0 failed, 0 skipped.

Added regression coverage for:
- Billing UI/CSV Transaction column.
- Template generation and NPOI workbook open/read.
- `StartInvoiceNumber = 0` model/UI/backend handling.
- Backend Serializable auto-numbering and resolved batch start.
- Conflict checks before insert.

## 7. Build result
PASS.

Build warnings remain in unrelated `Services/DeviceService.cs` lines 2260, 2261, 2317, and 2318. No new build errors were introduced.

## 8. Git
- Branch: `main`.
- Implementation commit SHA: `f891f2f1dcead2158ea3e1622894fb0f4864cace`.
- Implementation push status: pushed to `origin/main`.
- Report commit SHA: `c66b4444baaff94a39f1916b228c871ccda6c688`.

## 9. Remaining risks
- The implementation assumes the numbering space is the numeric suffix of invoice codes matching `SPN-INV-YY-xxxxx`.
- No new database unique index was added because the existing Transaction Reup database script explicitly drops prior unique indexes on published transaction/invoice code, and adding a new uniqueness constraint without production duplicate inspection could break old data.
- Conflict protection relies on Serializable transactions plus `UPDLOCK, HOLDLOCK` reads against the invoice sources rather than a new hard database constraint.
- Very large imports are supported in conflict checking by chunking candidate code checks into 500-code batches to avoid SQL Server parameter limits.

## 10. Follow-up: per-year Transaction Reup numbering

### Numbering rule confirmation
Numbering is confirmed to be yearly, not global.

Evidence:
- `MonthlySubscriptionService.BuildInvoiceNumberAsync()` builds prefix `SPN-INV-{year % 100:00}-`.
- The monthly invoice MAX query filters `TblSubscriptionInvoice` by current year date range and by the same invoice prefix before reading the numeric suffix.
- The existing limit message says `Yearly invoice sequence limit reached. Maximum is 99999 invoices per year.`
- `PaymentTransactionService` names the parsed suffix `InvoiceSequenceInYear`.
- `TransactionReupService.BuildInvoiceCode()` also generates `SPN-INV-{YY}-{xxxxx}`.

### Query change
`TransactionReupService.GetLatestInvoiceSequenceAsync(...)` now accepts `invoiceYear`.

It builds an exact parameterized prefix with `BuildInvoicePrefix(invoiceYear)` and filters both sources by:

`@invoicePrefix + N'[0-9][0-9][0-9][0-9][0-9]'`

Sources remain:
- `dbo.TblSubscriptionInvoice.InvoiceNumber WITH (UPDLOCK, HOLDLOCK)`
- `dbo.TblTransactionReupImportItem.InvoiceCode` / `InvoiceSequence WITH (UPDLOCK, HOLDLOCK)`

The query remains inside the existing Serializable transaction.

### Mixed-year policy
Transaction Reup Excel imports now require all valid rows in one file to belong to the same invoice year.

Reason:
- The batch history schema has only one `InvoiceStartNumber`, `InvoiceEndNumber`, and `NextInvoiceNumber`.
- Allowing mixed-year allocation would make those fields ambiguous without a schema change.

Rejected message:

`All valid rows in one Transaction Reup import must belong to the same invoice year.`

Manual mode remains backward-compatible for single-year imports: `StartInvoiceNumber > 0` still uses the entered sequence and increments per valid row.

### Zero-valid-row policy
If the file has rows but no rows validate as `Valid`, import now fails before saving the source file or creating a batch:

`The input file has no valid rows.`

This avoids misleading batch history like `InvoiceStartNumber = max + 1` and `InvoiceEndNumber = start - 1`.

### Tests and build
- `dotnet restore StarlinkDeviceManager.sln`: PASS.
- `dotnet build StarlinkDeviceManager.sln --no-restore`: PASS.
- `dotnet test StarlinkDeviceManager.sln --no-build`: PASS, 92 passed, 0 failed, 0 skipped.

Added/updated tests for:
- Auto numbering passes the valid row invoice year into the MAX query.
- The MAX query uses parameterized per-year prefix filtering.
- Duplicate checks remain before insert.
- Mixed-year valid imports are rejected before file save / DB insert.
- Zero-valid-row files are rejected before file save / DB insert.
- Invoice code generation uses the year-specific prefix.

No SQL integration test infrastructure for concurrent imports was present, so no real concurrent database test was added. The code path still uses the existing Serializable transaction plus `UPDLOCK, HOLDLOCK` reads and the duplicate conflict check as a second safety layer.

### Follow-up Git
- Fix commit SHA: `13b0310c136818647066b9fe065fd0993fc29024`.
- Push status: pushed to `origin/main`.
