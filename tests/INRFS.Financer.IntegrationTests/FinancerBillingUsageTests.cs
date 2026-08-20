using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using INRFS.Financer.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace INRFS.Financer.IntegrationTests;

public sealed class FinancerBillingUsageTests
{
    private static PlatformService CreateService(FinancerDbContext db) => new(
        db,
        new PasswordHasher<UserAccount>(),
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DataProtection:Key"] = "integration-key" }
        ).Build()
    );

    [Fact]
    public async Task Closed_loan_payments_are_included_in_collected_interest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>().UseSqlite(connection).Options;
        await using var db = new FinancerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var financer = new FinancerOrganization { DisplayName = "Test Finance", Status = AccountStatus.Active };
        var customer = new Customer { Financer = financer, FullName = "Test Customer" };
        var product = new LoanProduct { Name = "Test Product", Code = "TEST" };
        var application = new LoanApplication { FinancerId = financer.Id, Customer = customer, LoanProduct = product };
        var loan = new Loan
        {
            FinancerId = financer.Id,
            Customer = customer,
            LoanApplicationId = application.Id,
            LoanProduct = product,
            LoanNumber = "LN-CLOSED",
            Status = LoanStatus.Closed,
        };
        db.AddRange(financer, customer, product, application, loan);
        db.Payments.AddRange(
            new Payment { FinancerId = financer.Id, Loan = loan, PaymentNumber = "PAY-1", Amount = 10_750, PrincipalAmount = 10_000, InterestAmount = 750, Status = PaymentStatus.Completed },
            new Payment { FinancerId = financer.Id, Loan = loan, PaymentNumber = "PAY-2", Amount = 10_250, PrincipalAmount = 10_000, InterestAmount = 250, Status = PaymentStatus.Completed },
            new Payment { FinancerId = financer.Id, Loan = loan, PaymentNumber = "PAY-REVERSED", Amount = 500, InterestAmount = 500, Status = PaymentStatus.Reversed }
        );
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var actor = new CurrentUser(Guid.NewGuid(), null, ["SuperAdmin"], []);

        var usage = await service.GetFinancerBillingUsageAsync(actor, default);

        var row = Assert.Single(usage);
        Assert.Equal(1_000m, row.InterestCollected);
        Assert.Equal(10m, row.FeeGenerated);
        Assert.Equal(10m, row.Outstanding);
    }

    [Fact]
    public async Task Regenerating_uncollected_invoice_includes_new_closed_loan_interest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>().UseSqlite(connection).Options;
        await using var db = new FinancerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var financer = new FinancerOrganization
        {
            DisplayName = "Refresh Billing Finance",
            Status = AccountStatus.Active,
            ServiceChargePercentage = 1,
        };
        var customer = new Customer { Financer = financer, FullName = "Test Customer" };
        var product = new LoanProduct { Name = "Test Product", Code = "REFRESH" };
        var application = new LoanApplication
        {
            FinancerId = financer.Id,
            Customer = customer,
            LoanProduct = product,
        };
        var loan = new Loan
        {
            FinancerId = financer.Id,
            Customer = customer,
            LoanApplicationId = application.Id,
            LoanProduct = product,
            LoanNumber = "LN-REFRESH",
            Status = LoanStatus.Closed,
        };
        db.AddRange(financer, customer, product, application, loan);
        db.Payments.Add(new Payment
        {
            FinancerId = financer.Id,
            Loan = loan,
            PaymentNumber = "PAY-FIRST",
            Amount = 10_750,
            PrincipalAmount = 10_000,
            InterestAmount = 750,
            ReceivedAt = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero),
            Status = PaymentStatus.Completed,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var actor = new CurrentUser(Guid.NewGuid(), null, ["SuperAdmin"], []);
        var request = new GenerateInvoiceRequest(
            financer.Id,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 10, 10)
        );
        var original = await service.GenerateInvoiceAsync(request, actor, default);
        Assert.Equal(750m, original.InterestActivity);

        db.Payments.Add(new Payment
        {
            FinancerId = financer.Id,
            Loan = loan,
            PaymentNumber = "PAY-FINAL",
            Amount = 10_250,
            PrincipalAmount = 10_000,
            InterestAmount = 250,
            ReceivedAt = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            Status = PaymentStatus.Completed,
        });
        await db.SaveChangesAsync();

        var refreshed = await service.GenerateInvoiceAsync(request, actor, default);

        Assert.Equal(original.Id, refreshed.Id);
        Assert.Equal(1_000m, refreshed.InterestActivity);
        Assert.Equal(10m, refreshed.ChargeAmount);
        Assert.Equal(1, await db.ServiceChargeInvoices.CountAsync());
        Assert.Contains(await db.AuditLogs.Select(x => x.Action).ToListAsync(), x => x == "Invoice.Regenerated");
    }

    [Fact]
    public async Task Generating_after_collection_creates_invoice_for_only_unbilled_interest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>().UseSqlite(connection).Options;
        await using var db = new FinancerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var financer = new FinancerOrganization
        {
            DisplayName = "Supplement Finance",
            Status = AccountStatus.Active,
            ServiceChargePercentage = 1,
        };
        var customer = new Customer { Financer = financer, FullName = "Supplement Customer" };
        var product = new LoanProduct { Name = "Supplement Product", Code = "SUPPLEMENT" };
        var application = new LoanApplication
        {
            FinancerId = financer.Id,
            Customer = customer,
            LoanProduct = product,
        };
        var loan = new Loan
        {
            FinancerId = financer.Id,
            Customer = customer,
            LoanApplicationId = application.Id,
            LoanProduct = product,
            LoanNumber = "LN-SUPPLEMENT",
            Status = LoanStatus.Closed,
        };
        db.AddRange(financer, customer, product, application, loan);
        db.ServiceChargeInvoices.Add(new ServiceChargeInvoice
        {
            FinancerId = financer.Id,
            InvoiceNumber = "INV-PAID",
            PeriodStart = new DateOnly(2026, 8, 1),
            PeriodEnd = new DateOnly(2026, 8, 31),
            DueDate = new DateOnly(2026, 9, 10),
            InterestActivity = 1_000,
            ChargePercentage = 1,
            ChargeAmount = 10,
            CollectedAmount = 10,
            Status = ScheduleStatus.Paid,
        });
        db.Payments.Add(new Payment
        {
            FinancerId = financer.Id,
            Loan = loan,
            PaymentNumber = "PAY-AUGUST",
            InterestAmount = 1_900,
            Amount = 1_900,
            ReceivedAt = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
            Status = PaymentStatus.Completed,
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var actor = new CurrentUser(Guid.NewGuid(), null, ["SuperAdmin"], []);

        var supplement = await service.GenerateInvoiceAsync(
            new GenerateInvoiceRequest(
                financer.Id,
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 31),
                new DateOnly(2026, 9, 10)
            ),
            actor,
            default
        );

        Assert.Equal(900m, supplement.InterestActivity);
        Assert.Equal(9m, supplement.ChargeAmount);
        Assert.Equal(2, await db.ServiceChargeInvoices.CountAsync());
        Assert.Equal(1_900m, await db.ServiceChargeInvoices.SumAsync(x => x.InterestActivity));
        Assert.Contains(await db.AuditLogs.Select(x => x.Action).ToListAsync(), x => x == "Invoice.SupplementGenerated");
    }

    [Fact]
    public async Task Partial_collection_credit_note_and_reconciliation_are_audited()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>().UseSqlite(connection).Options;
        await using var db = new FinancerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var financer = new FinancerOrganization { DisplayName = "Billing Finance", Status = AccountStatus.Active };
        var invoice = new ServiceChargeInvoice { FinancerId = financer.Id, InvoiceNumber = "INV-TEST", PeriodStart = new DateOnly(2026, 8, 1), PeriodEnd = new DateOnly(2026, 8, 31), DueDate = new DateOnly(2026, 9, 10), ChargeAmount = 100, Status = ScheduleStatus.Due };
        db.AddRange(financer, invoice);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var actor = new CurrentUser(Guid.NewGuid(), null, ["SuperAdmin"], []);

        var partiallyPaid = await service.CollectInvoiceAsync(invoice.Id, new CollectInvoiceRequest(40, "BANK-DEMO"), actor, default);
        Assert.Equal(ScheduleStatus.PartiallyPaid, partiallyPaid.Status);
        var collection = await db.Transactions.SingleAsync(x => x.Type == TransactionType.Fee);
        Assert.False(collection.IsReconciled);
        await service.ReconcileTransactionAsync(collection.Id, "BANK-VERIFIED-DEMO", actor, default);
        Assert.True(collection.IsReconciled);

        var adjusted = await service.AdjustInvoiceAsync(invoice.Id, new AdjustInvoiceRequest(10, "Demo goodwill credit"), actor, default);
        Assert.Equal(90m, adjusted.ChargeAmount);
        Assert.Contains(await db.AuditLogs.Select(x => x.Action).ToListAsync(), x => x == "Invoice.CreditNoteIssued");
        Assert.Contains(await db.AuditLogs.Select(x => x.Action).ToListAsync(), x => x == "Transaction.Reconciled");
        var auditor = new CurrentUser(Guid.NewGuid(), null, ["Auditor"], ["reports.read"]);
        var denied = await Assert.ThrowsAsync<DomainException>(() => service.AdjustInvoiceAsync(invoice.Id, new AdjustInvoiceRequest(1, "Not allowed"), auditor, default));
        Assert.Equal(403, denied.StatusCode);
    }

    [Fact]
    public void Due_schedules_and_partially_paid_invoices_transition_to_overdue()
    {
        var loan = new Loan { Status = LoanStatus.Active };
        var schedule = new PaymentSchedule { Loan = loan, DueDate = new DateOnly(2026, 8, 1), Status = ScheduleStatus.Due };
        var invoice = new ServiceChargeInvoice { DueDate = new DateOnly(2026, 8, 1), Status = ScheduleStatus.PartiallyPaid };

        NotificationReminderWorker.ApplyOverdueTransitions([schedule], [invoice], new DateOnly(2026, 8, 2));

        Assert.Equal(ScheduleStatus.Overdue, schedule.Status);
        Assert.Equal(LoanStatus.Overdue, loan.Status);
        Assert.Equal(ScheduleStatus.Overdue, invoice.Status);
    }
}
