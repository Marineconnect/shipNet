-- Payment allocation transfer history. Also provisioned by PaymentTransactionService.
            IF OBJECT_ID(N'dbo.TblPaymentTransfer', N'U') IS NULL
            CREATE TABLE dbo.TblPaymentTransfer (
                ID int IDENTITY PRIMARY KEY, SourceInvoiceId int NOT NULL, TargetInvoiceId int NOT NULL UNIQUE,
                TransactionId int NOT NULL, PaymentNo nvarchar(100) NOT NULL,
                SourceCode nvarchar(100) NOT NULL, TargetCode nvarchar(100) NOT NULL,
                Amount decimal(18,2) NOT NULL CHECK (Amount > 0), Reason nvarchar(1000) NOT NULL,
                CreatedBy nvarchar(256) NOT NULL, CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TblPaymentTransfer') AND name = N'IX_PaymentTransfer_Source')
                CREATE INDEX IX_PaymentTransfer_Source ON dbo.TblPaymentTransfer(SourceInvoiceId);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.TblPaymentTransfer') AND name = N'IX_PaymentTransfer_PaymentNo')
                CREATE INDEX IX_PaymentTransfer_PaymentNo ON dbo.TblPaymentTransfer(PaymentNo);
