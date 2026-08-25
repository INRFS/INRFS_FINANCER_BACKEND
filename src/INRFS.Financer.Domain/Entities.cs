using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace INRFS.Financer.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}

public sealed class FinancerOrganization : Entity
{
    [MaxLength(32)]
    public string FinancerNumber { get; set; } = "";

    [MaxLength(200)]
    public string LegalName { get; set; } = "";

    [MaxLength(160)]
    public string DisplayName { get; set; } = "";

    [MaxLength(160)]
    public string OwnerName { get; set; } = "";

    [MaxLength(254)]
    public string Email { get; set; } = "";

    [MaxLength(24)]
    public string Phone { get; set; } = "";

    [MaxLength(250)]
    public string AddressLine { get; set; } = "";

    [MaxLength(100)]
    public string City { get; set; } = "";

    [MaxLength(100)]
    public string State { get; set; } = "";

    [MaxLength(12)]
    public string PostalCode { get; set; } = "";

    [MaxLength(32)]
    public string? TaxNumber { get; set; }

    [MaxLength(64)]
    public string? RegistrationNumber { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Pending;
    public VerificationStatus KycStatus { get; set; } = VerificationStatus.Pending;
    public decimal? ServiceChargePercentage { get; set; }
    public List<UserAccount> Users { get; set; } = [];
    public List<Customer> Customers { get; set; } = [];
}

public sealed class UserAccount : Entity
{
    public Guid? FinancerId { get; set; }
    public FinancerOrganization? Financer { get; set; }

    [MaxLength(32)]
    public string EmployeeNumber { get; set; } = "";

    [MaxLength(100)]
    public string FirstName { get; set; } = "";

    [MaxLength(100)]
    public string LastName { get; set; } = "";

    [MaxLength(254)]
    public string Email { get; set; } = "";

    [MaxLength(24)]
    public string Phone { get; set; } = "";
    public string? ProfileImageDataUrl { get; set; }
    public string PasswordHash { get; set; } = "";
    public AccountStatus Status { get; set; } = AccountStatus.Pending;
    public bool MfaRequired { get; set; } = true;
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<UserRole> UserRoles { get; set; } = [];
}

public sealed class Role : Entity
{
    [MaxLength(80)]
    public string Name { get; set; } = "";

    [MaxLength(300)]
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<UserRole> UserRoles { get; set; } = [];
    public List<RolePermission> RolePermissions { get; set; } = [];
}

public sealed class Permission : Entity
{
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(300)]
    public string? Description { get; set; }
    public List<RolePermission> RolePermissions { get; set; } = [];
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public UserAccount User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public Guid PermissionId { get; set; }
    public Permission Permission { get; set; } = null!;
}

public sealed class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public UserAccount User { get; set; } = null!;

    [MaxLength(128)]
    public string TokenHash { get; set; } = "";

    [MaxLength(64)]
    public string Family { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

public sealed class OtpChallenge : Entity
{
    public Guid? UserId { get; set; }

    [MaxLength(254)]
    public string Destination { get; set; } = "";

    [MaxLength(32)]
    public string Purpose { get; set; } = "Login";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public int Attempts { get; set; }
}

public sealed class Customer : Entity
{
    public Guid FinancerId { get; set; }
    public FinancerOrganization Financer { get; set; } = null!;

    [MaxLength(32)]
    public string CustomerNumber { get; set; } = "";

    [MaxLength(160)]
    public string FullName { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }

    [MaxLength(20)]
    public string? Gender { get; set; }

    [MaxLength(24)]
    public string Phone { get; set; } = "";

    [MaxLength(254)]
    public string? Email { get; set; }

    [MaxLength(250)]
    public string AddressLine1 { get; set; } = "";

    [MaxLength(120)]
    public string? AddressLine2 { get; set; }

    [MaxLength(100)]
    public string City { get; set; } = "";

    [MaxLength(100)]
    public string State { get; set; } = "";

    [MaxLength(12)]
    public string PostalCode { get; set; } = "";
    public string? AadhaarEncrypted { get; set; }
    public string? PanEncrypted { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public VerificationStatus KycStatus { get; set; } = VerificationStatus.Pending;
    public List<CustomerNote> Notes { get; set; } = [];
    public List<LoanApplication> Applications { get; set; } = [];
}

public sealed class CustomerNote : Entity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    [MaxLength(2000)]
    public string Text { get; set; } = "";
}

public sealed class KycRecord : Entity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    [MaxLength(40)]
    public string IdentityType { get; set; } = "";
    public string IdentityNumberEncrypted { get; set; } = "";

    [MaxLength(160)]
    public string DeclaredName { get; set; } = "";
    public DateOnly? DeclaredDateOfBirth { get; set; }
    public VerificationStatus Status { get; set; } = VerificationStatus.Submitted;
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }
}

public sealed class StoredDocument : Entity
{
    public Guid? FinancerId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? LoanApplicationId { get; set; }

    [MaxLength(80)]
    public string Category { get; set; } = "";

    [MaxLength(260)]
    public string OriginalFileName { get; set; } = "";

    [MaxLength(120)]
    public string ContentType { get; set; } = "";
    public long Size { get; set; }

    [MaxLength(64)]
    public string Sha256 { get; set; } = "";

    [MaxLength(500)]
    public string StorageKey { get; set; } = "";
    public DocumentStatus Status { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    [MaxLength(1000)]
    public string? VerificationNotes { get; set; }
}

public sealed class LoanProduct : Entity
{
    [MaxLength(32)]
    public string Code { get; set; } = "";

    [MaxLength(160)]
    public string Name { get; set; } = "";
    public decimal MinimumPrincipal { get; set; }
    public decimal MaximumPrincipal { get; set; }
    public int MinimumTenureMonths { get; set; }
    public int MaximumTenureMonths { get; set; }
    public decimal AnnualInterestRate { get; set; }
    public InterestMethod InterestMethod { get; set; }
    public RepaymentFrequency RepaymentFrequency { get; set; } = RepaymentFrequency.Monthly;
    public decimal ProcessingFeePercentage { get; set; }
    public decimal LateFeePercentage { get; set; }
    public int MinimumAge { get; set; } = 18;
    public int MaximumAgeAtMaturity { get; set; } = 70;
    public decimal MaximumFoirPercentage { get; set; } = 50;
    public bool IsActive { get; set; } = true;
}

public sealed class EligibilityCheck : Entity
{
    public Guid CustomerId { get; set; }
    public Guid LoanProductId { get; set; }
    public LoanProduct LoanProduct { get; set; } = null!;
    public decimal RequestedAmount { get; set; }
    public int TenureMonths { get; set; }
    public decimal MonthlyIncome { get; set; }
    public decimal MonthlyObligations { get; set; }
    public decimal FoirPercentage { get; set; }
    public decimal EligibleAmount { get; set; }
    public bool Passed { get; set; }
    public string RuleResultsJson { get; set; } = "{}";
}

public sealed class LoanApplication : Entity
{
    public Guid FinancerId { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid LoanProductId { get; set; }
    public LoanProduct LoanProduct { get; set; } = null!;
    public Guid? EligibilityCheckId { get; set; }

    [MaxLength(32)]
    public string ApplicationNumber { get; set; } = "";
    public decimal RequestedPrincipal { get; set; }
    public int RequestedTenureMonths { get; set; }

    [MaxLength(500)]
    public string Purpose { get; set; } = "";
    public decimal MonthlyIncome { get; set; }
    public decimal MonthlyObligations { get; set; }
    public decimal? ApprovedPrincipal { get; set; }
    public int? ApprovedTenureMonths { get; set; }
    public decimal? ApprovedAnnualRate { get; set; }
    public LoanApplicationStatus Status { get; set; } = LoanApplicationStatus.Draft;
    public Guid? VerifiedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? DisbursedBy { get; set; }

    [MaxLength(80)]
    public string? RejectionCode { get; set; }

    [MaxLength(1000)]
    public string? DecisionNotes { get; set; }
    public List<LoanStatusHistory> StatusHistory { get; set; } = [];
}

public sealed class LoanStatusHistory : Entity
{
    public Guid LoanApplicationId { get; set; }
    public LoanApplication LoanApplication { get; set; } = null!;
    public LoanApplicationStatus FromStatus { get; set; }
    public LoanApplicationStatus ToStatus { get; set; }

    [MaxLength(1000)]
    public string? Reason { get; set; }
}

public sealed class Loan : Entity
{
    public Guid FinancerId { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid LoanApplicationId { get; set; }
    public Guid LoanProductId { get; set; }
    public LoanProduct LoanProduct { get; set; } = null!;

    [MaxLength(32)]
    public string LoanNumber { get; set; } = "";
    public decimal Principal { get; set; }
    public decimal AnnualInterestRate { get; set; }
    public InterestMethod InterestMethod { get; set; }
    public int TenureMonths { get; set; }
    public int DurationValue { get; set; }
    public LoanDurationUnit DurationUnit { get; set; } = LoanDurationUnit.Months;
    public decimal InterestRate { get; set; }
    public InterestRateBasis InterestRateBasis { get; set; } = InterestRateBasis.PerAnnum;
    public InterestCollectionFrequency InterestCollectionFrequency { get; set; } = InterestCollectionFrequency.Monthly;
    public DateOnly DisbursementDate { get; set; }
    public DateOnly MaturityDate { get; set; }
    public LoanStatus Status { get; set; } = LoanStatus.Active;
    public decimal PrincipalOutstanding { get; set; }
    public decimal InterestOutstanding { get; set; }
    public decimal FeesOutstanding { get; set; }
    public bool AdminCollectionMonitoring { get; set; }
    public List<PaymentSchedule> Schedules { get; set; } = [];
}

public sealed class PaymentSchedule : Entity
{
    public Guid LoanId { get; set; }
    public Loan Loan { get; set; } = null!;
    public int InstallmentNumber { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public int InterestDays { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal OpeningPrincipal { get; set; }
    public decimal PrincipalDue { get; set; }
    public decimal InterestDue { get; set; }
    public decimal FeesDue { get; set; }
    public decimal AmountPaid { get; set; }
    public ScheduleStatus Status { get; set; } = ScheduleStatus.Upcoming;
    public DateTimeOffset? PaidAt { get; set; }
    public DateOnly? OriginalDueDate { get; set; }

    [MaxLength(500)]
    public string? RescheduleReason { get; set; }
}

public sealed class Payment : Entity
{
    public Guid FinancerId { get; set; }
    public Guid LoanId { get; set; }
    public Loan Loan { get; set; } = null!;
    public Guid? PaymentScheduleId { get; set; }

    [MaxLength(32)]
    public string PaymentNumber { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal PrincipalAmount { get; set; }
    public decimal InterestAmount { get; set; }
    public decimal FeeAmount { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public PaymentMode Mode { get; set; }

    [MaxLength(100)]
    public string? ExternalReference { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Completed;
    public Guid? ReversedPaymentId { get; set; }
    public List<PaymentAllocation> Allocations { get; set; } = [];
}

public sealed class PaymentAllocation : Entity
{
    public Guid PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;
    public Guid PaymentScheduleId { get; set; }
    public PaymentSchedule PaymentSchedule { get; set; } = null!;
    public decimal PrincipalAmount { get; set; }
    public decimal InterestAmount { get; set; }
    public decimal FeeAmount { get; set; }
}

public sealed class FinancialTransaction : Entity
{
    public Guid FinancerId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? LoanId { get; set; }
    public Guid? PaymentId { get; set; }

    [MaxLength(32)]
    public string TransactionNumber { get; set; } = "";
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset TransactionAt { get; set; }

    [MaxLength(100)]
    public string? ExternalReference { get; set; }
    public bool IsReconciled { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
    public Guid? ReconciledBy { get; set; }
}

public sealed class CollectionCase : Entity
{
    public Guid LoanId { get; set; }
    public Loan Loan { get; set; } = null!;
    public Guid? AssignedTo { get; set; }
    public CollectionStatus Status { get; set; } = CollectionStatus.Open;
    public decimal DueAmount { get; set; }
    public decimal OverdueAmount { get; set; }
    public int DaysPastDue { get; set; }
    public DateOnly? PromiseToPayDate { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public DateTimeOffset? LastContactAt { get; set; }
    public List<CollectionActivity> Activities { get; set; } = [];
}

public sealed class CollectionActivity : Entity
{
    public Guid CollectionCaseId { get; set; }
    public CollectionCase CollectionCase { get; set; } = null!;

    [MaxLength(40)]
    public string Type { get; set; } = "Note";

    [MaxLength(2000)]
    public string Notes { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Notification : Entity
{
    public Guid? FinancerId { get; set; }
    public Guid? UserId { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(2000)]
    public string Message { get; set; } = "";

    [MaxLength(50)]
    public string Type { get; set; } = "Info";
    public NotificationChannel Channel { get; set; }

    [MaxLength(80)]
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    [MaxLength(100)]
    public string? DeliveryReference { get; set; }
}

public sealed class SupportTicket : Entity
{
    public Guid? FinancerId { get; set; }
    public Guid OpenedBy { get; set; }
    public Guid? AssignedTo { get; set; }

    [MaxLength(32)]
    public string TicketNumber { get; set; } = "";

    [MaxLength(200)]
    public string Subject { get; set; } = "";

    [MaxLength(80)]
    public string Category { get; set; } = "";
    public TicketPriority Priority { get; set; }

    [MaxLength(4000)]
    public string Description { get; set; } = "";
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public List<TicketMessage> Messages { get; set; } = [];
}

public sealed class TicketMessage : Entity
{
    public Guid SupportTicketId { get; set; }
    [JsonIgnore]
    public SupportTicket SupportTicket { get; set; } = null!;
    public Guid SenderId { get; set; }

    [MaxLength(4000)]
    public string Message { get; set; } = "";
    public bool IsInternal { get; set; }
}

public sealed class PlatformSetting : Entity
{
    [MaxLength(80)]
    public string Scope { get; set; } = "Platform";

    [MaxLength(120)]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";

    [MaxLength(40)]
    public string ValueType { get; set; } = "String";

    [MaxLength(300)]
    public string? Description { get; set; }
    public bool IsSecret { get; set; }
    public uint Version { get; set; }
}

public sealed class ServiceChargeInvoice : Entity
{
    public Guid FinancerId { get; set; }

    [MaxLength(32)]
    public string InvoiceNumber { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal ChargePercentage { get; set; }
    public decimal InterestActivity { get; set; }
    public decimal ChargeAmount { get; set; }
    public decimal CollectedAmount { get; set; }
    public DateOnly DueDate { get; set; }
    public ScheduleStatus Status { get; set; }
}

public sealed class SubscriptionPlan : Entity
{
    [MaxLength(40)]
    public string Code { get; set; } = "";

    [MaxLength(120)]
    public string Name { get; set; } = "";
    public decimal MonthlyPrice { get; set; }
    public int CustomerLimit { get; set; }
    public int LoanLimit { get; set; }
    public int SmsCredits { get; set; }
    public string FeaturesJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

public sealed class FinancerSubscription : Entity
{
    public Guid FinancerId { get; set; }
    public Guid SubscriptionPlanId { get; set; }
    public SubscriptionPlan SubscriptionPlan { get; set; } = null!;
    public DateOnly StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
}

public sealed class SmsDelivery : Entity
{
    public Guid FinancerId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? NotificationId { get; set; }

    [MaxLength(24)]
    public string DestinationMasked { get; set; } = "";

    [MaxLength(80)]
    public string MessageType { get; set; } = "";

    [MaxLength(40)]
    public string Status { get; set; } = "Queued";

    [MaxLength(100)]
    public string? ProviderReference { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int CreditsUsed { get; set; } = 1;
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorId { get; set; }
    public Guid? FinancerId { get; set; }

    [MaxLength(120)]
    public string Action { get; set; } = "";

    [MaxLength(100)]
    public string EntityType { get; set; } = "";

    [MaxLength(80)]
    public string EntityId { get; set; } = "";
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    [MaxLength(500)]
    public string? UserAgent { get; set; }

    [MaxLength(100)]
    public string? CorrelationId { get; set; }
}
