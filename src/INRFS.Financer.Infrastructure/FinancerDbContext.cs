using INRFS.Financer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace INRFS.Financer.Infrastructure;

public sealed class FinancerDbContext(DbContextOptions<FinancerDbContext> options)
    : DbContext(options)
{
    public DbSet<FinancerOrganization> Financers => Set<FinancerOrganization>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerNote> CustomerNotes => Set<CustomerNote>();
    public DbSet<KycRecord> KycRecords => Set<KycRecord>();
    public DbSet<StoredDocument> Documents => Set<StoredDocument>();
    public DbSet<LoanProduct> LoanProducts => Set<LoanProduct>();
    public DbSet<EligibilityCheck> EligibilityChecks => Set<EligibilityCheck>();
    public DbSet<LoanApplication> LoanApplications => Set<LoanApplication>();
    public DbSet<LoanStatusHistory> LoanStatusHistory => Set<LoanStatusHistory>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<PaymentSchedule> PaymentSchedules => Set<PaymentSchedule>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<FinancialTransaction> Transactions => Set<FinancialTransaction>();
    public DbSet<CollectionCase> CollectionCases => Set<CollectionCase>();
    public DbSet<CollectionActivity> CollectionActivities => Set<CollectionActivity>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
    public DbSet<PlatformSetting> Settings => Set<PlatformSetting>();
    public DbSet<ServiceChargeInvoice> ServiceChargeInvoices => Set<ServiceChargeInvoice>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<FinancerSubscription> FinancerSubscriptions => Set<FinancerSubscription>();
    public DbSet<SmsDelivery> SmsDeliveries => Set<SmsDelivery>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<UserRole>().HasKey(x => new { x.UserId, x.RoleId });
        b.Entity<UserRole>().HasQueryFilter(x => !x.User.IsDeleted);
        b.Entity<RolePermission>().HasKey(x => new { x.RoleId, x.PermissionId });
        b.Entity<UserAccount>().HasIndex(x => x.Email).IsUnique();
        b.Entity<UserAccount>().HasIndex(x => x.EmployeeNumber).IsUnique();
        b.Entity<Role>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Permission>().HasIndex(x => x.Name).IsUnique();
        b.Entity<FinancerOrganization>().HasIndex(x => x.FinancerNumber).IsUnique();
        b.Entity<FinancerOrganization>().HasIndex(x => x.Email).IsUnique();
        b.Entity<Customer>().HasIndex(x => new { x.FinancerId, x.CustomerNumber }).IsUnique();
        b.Entity<Customer>().HasIndex(x => new { x.FinancerId, x.Phone }).IsUnique();
        b.Entity<LoanProduct>().HasIndex(x => x.Code).IsUnique();
        b.Entity<LoanApplication>().HasIndex(x => x.ApplicationNumber).IsUnique();
        b.Entity<Loan>().HasIndex(x => x.LoanNumber).IsUnique();
        b.Entity<Loan>().HasIndex(x => x.LoanApplicationId).IsUnique();
        b.Entity<PaymentSchedule>().HasIndex(x => new { x.LoanId, x.InstallmentNumber }).IsUnique();
        b.Entity<Payment>().HasIndex(x => x.PaymentNumber).IsUnique();
        b.Entity<Payment>()
            .HasIndex(x => new { x.FinancerId, x.ExternalReference })
            .IsUnique()
            .HasFilter("\"ExternalReference\" IS NOT NULL");
        b.Entity<FinancialTransaction>().HasIndex(x => x.TransactionNumber).IsUnique();
        b.Entity<CollectionCase>().HasIndex(x => x.LoanId).IsUnique();
        b.Entity<SupportTicket>().HasIndex(x => x.TicketNumber).IsUnique();
        b.Entity<PlatformSetting>().HasIndex(x => new { x.Scope, x.Key }).IsUnique();
        b.Entity<ServiceChargeInvoice>().HasIndex(x => x.InvoiceNumber).IsUnique();
        b.Entity<ServiceChargeInvoice>()
            .HasIndex(x => new
            {
                x.FinancerId,
                x.PeriodStart,
                x.PeriodEnd,
            });
        b.Entity<SubscriptionPlan>().HasIndex(x => x.Code).IsUnique();
        b.Entity<FinancerSubscription>()
            .HasIndex(x => new
            {
                x.FinancerId,
                x.SubscriptionPlanId,
                x.StartsOn,
            })
            .IsUnique();
        b.Entity<AuditLog>().HasKey(x => x.Id);
        b.Entity<AuditLog>().Property(x => x.Id).ValueGeneratedOnAdd();
        b.Entity<AuditLog>().HasIndex(x => x.Timestamp);
        b.Entity<AuditLog>().HasIndex(x => new { x.EntityType, x.EntityId });

        foreach (
            var type in b
                .Model.GetEntityTypes()
                .Where(x =>
                    typeof(Entity).IsAssignableFrom(x.ClrType)
                    && x.ClrType != typeof(Role)
                    && x.ClrType != typeof(Permission)
                )
        )
        {
            b.Entity(type.ClrType).HasQueryFilter(BuildSoftDeleteFilter(type.ClrType));
        }
        foreach (
            var property in b
                .Model.GetEntityTypes()
                .SelectMany(x => x.GetProperties())
                .Where(x => x.ClrType == typeof(decimal) || x.ClrType == typeof(decimal?))
        )
        {
            property.SetPrecision(18);
            property.SetScale(2);
        }
        if (Database.IsSqlite())
        {
            var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
                value => value.UtcTicks,
                value => new DateTimeOffset(value, TimeSpan.Zero)
            );
            var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
                value => value.HasValue ? value.Value.UtcTicks : null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null
            );
            foreach (var property in b.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
            {
                if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(dateTimeOffsetConverter);
                else if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullableDateTimeOffsetConverter);
            }
        }
    }

    private static System.Linq.Expressions.LambdaExpression BuildSoftDeleteFilter(Type type)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(type, "e");
        var property = System.Linq.Expressions.Expression.Property(
            parameter,
            nameof(Entity.IsDeleted)
        );
        return System.Linq.Expressions.Expression.Lambda(
            System.Linq.Expressions.Expression.Equal(
                property,
                System.Linq.Expressions.Expression.Constant(false)
            ),
            parameter
        );
    }
}
