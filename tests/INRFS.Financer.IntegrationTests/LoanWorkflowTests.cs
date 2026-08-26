using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using INRFS.Financer.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace INRFS.Financer.IntegrationTests;

public sealed class LoanWorkflowTests
{
    [Fact]
    public async Task Approved_application_disburses_once_and_generates_schedule()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FinancerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var financer = new FinancerOrganization
        {
            FinancerNumber = "FIN-TEST",
            LegalName = "Test Finance",
            DisplayName = "Test",
            OwnerName = "Owner",
            Email = "owner@test.invalid",
            Phone = "9999999999",
            City = "City",
            State = "State",
            Status = AccountStatus.Active,
            KycStatus = VerificationStatus.Verified,
        };
        var customer = new Customer
        {
            Financer = financer,
            CustomerNumber = "CUS-TEST",
            FullName = "Eligible Customer",
            DateOfBirth = new DateOnly(1985, 1, 1),
            Phone = "9888888888",
            AddressLine1 = "Address",
            City = "City",
            State = "State",
            PostalCode = "123456",
            Status = AccountStatus.Active,
            KycStatus = VerificationStatus.Verified,
        };
        var product = new LoanProduct
        {
            Code = "TEST",
            Name = "Test Product",
            MinimumPrincipal = 1000,
            MaximumPrincipal = 500000,
            MinimumTenureMonths = 3,
            MaximumTenureMonths = 36,
            AnnualInterestRate = 18,
            InterestMethod = InterestMethod.ReducingBalance,
            RepaymentFrequency = RepaymentFrequency.Monthly,
            MaximumFoirPercentage = 50,
            IsActive = true,
        };
        var adminRole = new Role { Name = "SuperAdmin", IsSystem = true };
        var admin = new UserAccount
        {
            EmployeeNumber = "ADM-LOAN",
            FirstName = "Platform",
            LastName = "Admin",
            Email = "loan-admin@test.invalid",
            Phone = "9000000000",
            Status = AccountStatus.Active,
        };
        admin.UserRoles.Add(new UserRole { User = admin, Role = adminRole });
        db.AddRange(financer, customer, product, adminRole, admin);
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { { "DataProtection:Key", "integration-data-key" } }
            )
            .Build();
        var service = new PlatformService(
            db,
            new PasswordHasher<UserAccount>(),
            config,
            new TestAuthMessageSender()
        );
        CurrentUser Actor(Guid id) => new(id, null, ["SuperAdmin"], []);
        var financerActor = new CurrentUser(Guid.NewGuid(), financer.Id, ["FinancerOwner"], ["loans.create"]);
        var created = await service.CreateApplicationAsync(
            new(customer.Id, product.Id, 100000, 18, 12, "Business", 50000, 5000),
            financerActor,
            default
        );
        var applicationAlert = await db.Notifications.SingleAsync();
        Assert.Equal(admin.Id, applicationAlert.UserId);
        Assert.Equal("New loan application", applicationAlert.Title);
        Assert.Equal(nameof(LoanApplication), applicationAlert.EntityType);
        Assert.Equal(created.Id, applicationAlert.EntityId);
        var submitted = await service.TransitionApplicationAsync(
            created.Id,
            "submit",
            null,
            Actor(Guid.NewGuid()),
            default
        );
        Assert.Equal(LoanApplicationStatus.UnderVerification, submitted.Status);
        var verifier = Guid.NewGuid();
        var verified = await service.TransitionApplicationAsync(
            created.Id,
            "verify",
            null,
            Actor(verifier),
            default
        );
        Assert.Equal(LoanApplicationStatus.Verified, verified.Status);
        var approver = Guid.NewGuid();
        await service.TransitionApplicationAsync(
            created.Id,
            "approve",
            new LoanDecisionRequest(100000, 18, 12, "Within policy"),
            Actor(approver),
            default
        );
        var disbursed = await service.TransitionApplicationAsync(
            created.Id,
            "disburse",
            new DisbursementRequest(
                100000,
                DateOnly.FromDateTime(DateTime.UtcNow),
                PaymentMode.BankTransfer,
                "UTR-TEST"
            ),
            Actor(Guid.NewGuid()),
            default
        );
        Assert.Equal(LoanApplicationStatus.Disbursed, disbursed.Status);
        var loan = await db.Loans.Include(x => x.Schedules).SingleAsync();
        Assert.Equal(12, loan.Schedules.Count);
        Assert.Equal(100000, loan.PrincipalOutstanding);
        Assert.Equal(12, await db.PaymentSchedules.CountAsync());
        Assert.Single(db.Transactions);
        await Assert.ThrowsAsync<DomainException>(() =>
            service.TransitionApplicationAsync(
                created.Id,
                "disburse",
                new DisbursementRequest(
                    100000,
                    DateOnly.FromDateTime(DateTime.UtcNow),
                    PaymentMode.BankTransfer,
                    "UTR-SECOND"
                ),
                Actor(Guid.NewGuid()),
                default
            )
        );

        var directLoan = await service.CreateDirectLoanAsync(
            new DirectLoanRequest(
                customer.Id,
                product.Id,
                75000,
                18,
                12,
                DateOnly.FromDateTime(DateTime.UtcNow)
            ),
            financerActor,
            default
        );
        var directLoanAlert = await db.Notifications
            .OrderByDescending(notification => notification.CreatedAt)
            .FirstAsync(notification => notification.EntityType == nameof(Loan));
        Assert.Equal(admin.Id, directLoanAlert.UserId);
        Assert.Equal("New loan created", directLoanAlert.Title);
        Assert.Equal(directLoan.Id, directLoanAlert.EntityId);

        var monthlyLoan = await service.CreateDirectLoanAsync(
            new DirectLoanRequest(
                customer.Id,
                product.Id,
                100000,
                3,
                3,
                new DateOnly(2026, 1, 15),
                DurationValue: 3,
                DurationUnit: LoanDurationUnit.Months,
                InterestRate: 3,
                InterestRateBasis: InterestRateBasis.PerMonth,
                InterestCollectionFrequency: InterestCollectionFrequency.Monthly
            ),
            financerActor,
            default
        );
        var savedMonthlyLoan = await db.Loans
            .Include(item => item.Schedules)
            .SingleAsync(item => item.Id == monthlyLoan.Id);
        Assert.Equal(3, savedMonthlyLoan.Schedules.Count);
        Assert.All(savedMonthlyLoan.Schedules, schedule => Assert.Equal(3000m, schedule.InterestDue));
        Assert.Equal(9000m, savedMonthlyLoan.InterestOutstanding);
    }
}
