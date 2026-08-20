using INRFS.Financer.Domain;

namespace INRFS.Financer.Application;

public sealed record ApiResult<T>(
    bool Success,
    T? Data,
    string? Message = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? TraceId = null
)
{
    public static ApiResult<T> Ok(T data, string? message = null) => new(true, data, message);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record PageQuery(
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string? SortBy = null,
    string SortDirection = "asc",
    string? Status = null,
    Guid? FinancerId = null,
    DateOnly? From = null,
    DateOnly? To = null
);

public sealed record CurrentUser(
    Guid UserId,
    Guid? FinancerId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions
);

public sealed record LoginRequest(string Email, string Password, string Portal);

public sealed record RegisterFinancerRequest(
    string FullName,
    string BusinessName,
    string Mobile,
    string Email,
    string City,
    string State
);

public sealed record OtpRequest(Guid? ChallengeId, string Destination, string Purpose);

public sealed record VerifyOtpRequest(Guid ChallengeId, string Code);

public sealed record RefreshRequest(string RefreshToken);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword, string ConfirmPassword);

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword,
    string ConfirmPassword
);

public sealed record UpdateMyProfileRequest(
    string FullName,
    string BusinessName,
    string Mobile,
    string Email,
    string City,
    string State,
    string? ProfileImageDataUrl
);

public sealed record AuthChallengeResponse(
    Guid ChallengeId,
    string MaskedDestination,
    DateTimeOffset ExpiresAt
);

public sealed record RegistrationCompletionResponse(
    string UserId,
    string MaskedEmail,
    string Message
);

public sealed record AuthTokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    UserDto User
);

public sealed record UserDto(
    Guid Id,
    Guid? FinancerId,
    string EmployeeNumber,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    AccountStatus Status,
    IReadOnlyList<string> Roles,
    DateTimeOffset? LastLoginAt,
    string? ProfileImage
);

public sealed record CreateUserRequest(
    Guid? FinancerId,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string Password,
    IReadOnlyList<Guid> RoleIds
);

public sealed record UpdateUserRequest(
    string FirstName,
    string LastName,
    string Phone,
    AccountStatus Status
);

public sealed record RoleDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<string> Permissions
);

public sealed record CreateRoleRequest(
    string Name,
    string? Description,
    IReadOnlyList<Guid> PermissionIds
);

public sealed record FinancerDto(
    Guid Id,
    string FinancerNumber,
    string LegalName,
    string DisplayName,
    string OwnerName,
    string Email,
    string Phone,
    string City,
    string State,
    AccountStatus Status,
    VerificationStatus KycStatus,
    decimal? ServiceChargePercentage,
    DateTimeOffset CreatedAt
);

public sealed record CreateFinancerRequest(
    string LegalName,
    string DisplayName,
    string OwnerName,
    string Email,
    string Phone,
    string AddressLine,
    string City,
    string State,
    string PostalCode,
    string? TaxNumber,
    string? RegistrationNumber,
    decimal? ServiceChargePercentage
);

public sealed record ChangeStatusRequest(string Status, string Reason);

public sealed record CustomerDto(
    Guid Id,
    Guid FinancerId,
    string CustomerNumber,
    string FullName,
    DateOnly DateOfBirth,
    string? Gender,
    string Phone,
    string? Email,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string? AadhaarMasked,
    string? PanMasked,
    AccountStatus Status,
    VerificationStatus KycStatus,
    DateTimeOffset CreatedAt
);

public sealed record CreateCustomerRequest(
    Guid? FinancerId,
    string FullName,
    DateOnly DateOfBirth,
    string? Gender,
    string Phone,
    string? Email,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string? Aadhaar,
    string? Pan
);

public sealed record UpdateCustomerRequest(
    string FullName,
    DateOnly DateOfBirth,
    string? Gender,
    string Phone,
    string? Email,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    AccountStatus Status
);

public sealed record AddNoteRequest(string Text);

public sealed record KycSubmissionRequest(
    Guid CustomerId,
    string IdentityType,
    string IdentityNumber,
    string DeclaredName,
    DateOnly? DeclaredDateOfBirth
);

public sealed record KycDecisionRequest(VerificationStatus Status, string Notes);

public sealed record DocumentDecisionRequest(DocumentStatus Status, string Notes);

public sealed record LoanProductDto(
    Guid Id,
    string Code,
    string Name,
    decimal MinimumPrincipal,
    decimal MaximumPrincipal,
    int MinimumTenureMonths,
    int MaximumTenureMonths,
    decimal AnnualInterestRate,
    InterestMethod InterestMethod,
    RepaymentFrequency RepaymentFrequency,
    decimal ProcessingFeePercentage,
    decimal LateFeePercentage,
    decimal MaximumFoirPercentage,
    bool IsActive
);

public sealed record LoanProductRequest(
    string Code,
    string Name,
    decimal MinimumPrincipal,
    decimal MaximumPrincipal,
    int MinimumTenureMonths,
    int MaximumTenureMonths,
    decimal AnnualInterestRate,
    InterestMethod InterestMethod,
    RepaymentFrequency RepaymentFrequency,
    decimal ProcessingFeePercentage,
    decimal LateFeePercentage,
    int MinimumAge,
    int MaximumAgeAtMaturity,
    decimal MaximumFoirPercentage,
    bool IsActive = true
);

public sealed record EligibilityRequest(
    Guid CustomerId,
    Guid LoanProductId,
    decimal RequestedAmount,
    int TenureMonths,
    decimal MonthlyIncome,
    decimal MonthlyObligations
);

public sealed record EligibilityDto(
    Guid Id,
    bool Passed,
    decimal EligibleAmount,
    decimal FoirPercentage,
    IReadOnlyDictionary<string, bool> Rules
);

public sealed record LoanApplicationRequest(
    Guid CustomerId,
    Guid LoanProductId,
    decimal RequestedPrincipal,
    decimal RequestedAnnualRate,
    int RequestedTenureMonths,
    string Purpose,
    decimal MonthlyIncome,
    decimal MonthlyObligations
);

public sealed record LoanApplicationDto(
    Guid Id,
    string ApplicationNumber,
    Guid CustomerId,
    Guid LoanProductId,
    decimal RequestedPrincipal,
    decimal? ApprovedPrincipal,
    int RequestedTenureMonths,
    int? ApprovedTenureMonths,
    decimal? ApprovedAnnualRate,
    LoanApplicationStatus Status,
    string? RejectionCode,
    string? DecisionNotes,
    DateTimeOffset CreatedAt
);

public sealed record LoanDecisionRequest(
    decimal ApprovedPrincipal,
    decimal ApprovedAnnualRate,
    int ApprovedTenureMonths,
    string Notes
);

public sealed record RejectLoanRequest(string ReasonCode, string Notes);

public sealed record DisbursementRequest(
    decimal Amount,
    DateOnly DisbursementDate,
    PaymentMode Mode,
    string BankReference
);

public sealed record DirectLoanRequest(
    Guid CustomerId,
    Guid LoanProductId,
    decimal Principal,
    decimal AnnualInterestRate,
    int TenureMonths,
    DateOnly StartDate,
    int? DurationValue = null,
    LoanDurationUnit DurationUnit = LoanDurationUnit.Months,
    decimal? InterestRate = null,
    InterestRateBasis InterestRateBasis = InterestRateBasis.PerAnnum,
    InterestCollectionFrequency InterestCollectionFrequency = InterestCollectionFrequency.Monthly
);

public sealed record LoanDto(
    Guid Id,
    string LoanNumber,
    Guid CustomerId,
    Guid LoanProductId,
    decimal Principal,
    decimal AnnualInterestRate,
    RepaymentFrequency RepaymentFrequency,
    int TenureMonths,
    DateOnly DisbursementDate,
    DateOnly MaturityDate,
    LoanStatus Status,
    decimal PrincipalOutstanding,
    decimal InterestOutstanding,
    decimal FeesOutstanding
    , int DurationValue
    , LoanDurationUnit DurationUnit
    , decimal InterestRate
    , InterestRateBasis InterestRateBasis
    , InterestCollectionFrequency InterestCollectionFrequency
);

public sealed record ScheduleDto(
    Guid Id,
    int InstallmentNumber,
    DateOnly DueDate,
    decimal OpeningPrincipal,
    decimal PrincipalDue,
    decimal InterestDue,
    decimal FeesDue,
    decimal AmountPaid,
    ScheduleStatus Status
    , DateOnly PeriodStart
    , DateOnly PeriodEnd
    , int InterestDays
    , decimal RemainingAmount
);

public sealed record RecordPaymentRequest(
    Guid LoanId,
    Guid? PaymentScheduleId,
    decimal Amount,
    DateTimeOffset ReceivedAt,
    PaymentMode Mode,
    string? ExternalReference,
    string? Notes
);

public sealed record PaymentDto(
    Guid Id,
    string PaymentNumber,
    Guid FinancerId,
    Guid LoanId,
    decimal Amount,
    decimal PrincipalAmount,
    decimal InterestAmount,
    decimal FeeAmount,
    DateTimeOffset ReceivedAt,
    PaymentMode Mode,
    string? ExternalReference,
    PaymentStatus Status
);

public sealed record ReversePaymentRequest(string Reason);

public sealed record ReschedulePaymentRequest(DateOnly NewDueDate, string Reason);

public sealed record CollectionActionRequest(
    string Type,
    string Notes,
    DateOnly? PromiseToPayDate = null,
    DateOnly? NextFollowUpDate = null,
    Guid? AssignedTo = null,
    CollectionStatus? Status = null
);

public sealed record NotificationRequest(
    Guid? FinancerId,
    Guid? UserId,
    string Title,
    string Message,
    string Type,
    NotificationChannel Channel,
    string? EntityType,
    Guid? EntityId
);

public sealed record TicketRequest(
    string Subject,
    string Category,
    TicketPriority Priority,
    string Description
);

public sealed record TicketMessageRequest(string Message, bool IsInternal = false);

public sealed record TicketStatusRequest(TicketStatus Status, Guid? AssignedTo = null);

public sealed record SettingRequest(
    string Value,
    string ValueType,
    string? Description,
    bool IsSecret,
    uint? ExpectedVersion
);

public sealed record GenerateInvoiceRequest(
    Guid FinancerId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly DueDate
);

public sealed record CollectInvoiceRequest(decimal Amount, string Reference);
public sealed record AdjustInvoiceRequest(decimal CreditAmount, string Reason);
public sealed record AdminSessionDto(Guid Id, Guid UserId, string Family, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, bool IsActive);

public sealed record FinancerBillingUsageDto(
    Guid FinancerId,
    string FinancerNumber,
    string FinancerName,
    AccountStatus Status,
    decimal InterestCollected,
    decimal FeeGenerated,
    decimal FeeCollected,
    decimal Outstanding,
    decimal Overdue
);

public sealed record SubscriptionPlanRequest(
    string Code,
    string Name,
    decimal MonthlyPrice,
    int CustomerLimit,
    int LoanLimit,
    int SmsCredits,
    IReadOnlyList<string> Features,
    bool IsActive = true
);

public sealed record AssignSubscriptionRequest(
    Guid FinancerId,
    Guid PlanId,
    DateOnly StartsOn,
    DateOnly? EndsOn
);

public interface IAuthService
{
    Task<AuthChallengeResponse> RegisterFinancerAsync(
        RegisterFinancerRequest request,
        CancellationToken ct
    );
    Task<AuthChallengeResponse> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<AuthTokenResponse> LoginFinancerAsync(
        LoginRequest request,
        string? ipAddress,
        CancellationToken ct
    );
    Task<AuthChallengeResponse> RequestOtpAsync(OtpRequest request, CancellationToken ct);
    Task<AuthTokenResponse> VerifyOtpAsync(
        VerifyOtpRequest request,
        string? ipAddress,
        CancellationToken ct
    );
    Task<RegistrationCompletionResponse> VerifyRegistrationOtpAsync(
        VerifyOtpRequest request,
        CancellationToken ct
    );
    Task<AuthTokenResponse> RefreshAsync(
        RefreshRequest request,
        string? ipAddress,
        CancellationToken ct
    );
    Task RevokeAsync(RefreshRequest request, CancellationToken ct);
    Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct);
}

public interface IPlatformService
{
    Task<object> GetMyProfileAsync(CurrentUser actor, CancellationToken ct);
    Task<object> UpdateMyProfileAsync(
        UpdateMyProfileRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<PagedResult<FinancerDto>> GetFinancersAsync(
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<FinancerDto> CreateFinancerAsync(
        CreateFinancerRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<FinancerDto> ChangeFinancerStatusAsync(
        Guid id,
        ChangeStatusRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<FinancerDto> DecideFinancerKycAsync(Guid id, KycDecisionRequest request, CurrentUser actor, CancellationToken ct);
    Task<IReadOnlyList<AdminSessionDto>> GetUserSessionsAsync(Guid userId, CurrentUser actor, CancellationToken ct);
    Task RevokeUserSessionAsync(Guid userId, Guid sessionId, CurrentUser actor, CancellationToken ct);
    Task<IReadOnlyList<FinancerBillingUsageDto>> GetFinancerBillingUsageAsync(
        CurrentUser actor,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null
    );
    Task<PagedResult<UserDto>> GetUsersAsync(
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<UserDto> CreateUserAsync(
        CreateUserRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<UserDto> UpdateUserAsync(
        Guid id,
        UpdateUserRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<UserDto> SetUserRolesAsync(
        Guid id,
        IReadOnlyList<Guid> roleIds,
        CurrentUser actor,
        CancellationToken ct
    );
    Task DeleteUserAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct);
    Task<RoleDto> CreateRoleAsync(
        CreateRoleRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<PagedResult<CustomerDto>> GetCustomersAsync(
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<CustomerDto> GetCustomerAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task<CustomerDto> CreateCustomerAsync(
        CreateCustomerRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<CustomerDto> UpdateCustomerAsync(
        Guid id,
        UpdateCustomerRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task DeleteCustomerAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task AddCustomerNoteAsync(
        Guid id,
        AddNoteRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<KycRecord> SubmitKycAsync(
        KycSubmissionRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<KycRecord> DecideKycAsync(
        Guid id,
        KycDecisionRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetKycAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
    Task<IReadOnlyList<LoanProductDto>> GetProductsAsync(
        bool includeInactive,
        CancellationToken ct
    );
    Task<LoanProductDto> SaveProductAsync(
        Guid? id,
        LoanProductRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<EligibilityDto> CheckEligibilityAsync(
        EligibilityRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<PagedResult<LoanApplicationDto>> GetApplicationsAsync(
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<LoanApplicationDto> CreateApplicationAsync(
        LoanApplicationRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<LoanApplicationDto> GetApplicationAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task<object> GetApplicationHistoryAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task<LoanApplicationDto> TransitionApplicationAsync(
        Guid id,
        string action,
        object? request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<PagedResult<LoanDto>> GetLoansAsync(
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<LoanDto> CreateDirectLoanAsync(DirectLoanRequest request, CurrentUser actor, CancellationToken ct);
    Task<LoanDto> GetLoanAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task<IReadOnlyList<ScheduleDto>> GetScheduleAsync(
        Guid id,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetSchedulesAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
    Task<PagedResult<PaymentDto>> GetPaymentsAsync(
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<PaymentDto> GetPaymentAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task<PaymentDto> RecordPaymentAsync(
        RecordPaymentRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<PaymentDto> ReversePaymentAsync(
        Guid id,
        ReversePaymentRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<ScheduleDto> RescheduleAsync(
        Guid id,
        ReschedulePaymentRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetTransactionsAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
    Task<object> ReconcileTransactionAsync(
        Guid id,
        string externalReference,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetCustomerLedgerAsync(
        Guid customerId,
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetCollectionsAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
    Task<object> AddCollectionActionAsync(
        Guid loanId,
        CollectionActionRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetDashboardAsync(
        bool admin,
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetReportAsync(
        string name,
        PageQuery query,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetNotificationsAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
    Task<Notification> CreateNotificationAsync(
        NotificationRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task MarkNotificationsReadAsync(Guid? id, CurrentUser actor, CancellationToken ct);
    Task<object> GetTicketsAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
    Task<SupportTicket> GetTicketAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task<SupportTicket> CreateTicketAsync(
        TicketRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<SupportTicket> UpdateTicketAsync(
        Guid id,
        TicketMessageRequest? message,
        TicketStatusRequest? status,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetSettingsAsync(string? scope, CurrentUser actor, CancellationToken ct);
    Task<PlatformSetting> SaveSettingAsync(
        string scope,
        string key,
        SettingRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetAuditLogsAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
    Task<AuditLog> GetAuditLogAsync(long id, CurrentUser actor, CancellationToken ct);
    Task<object> GetBillingAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
    Task<ServiceChargeInvoice> GenerateInvoiceAsync(
        GenerateInvoiceRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<ServiceChargeInvoice> CollectInvoiceAsync(
        Guid id,
        CollectInvoiceRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<ServiceChargeInvoice> AdjustInvoiceAsync(
        Guid id,
        AdjustInvoiceRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetSubscriptionsAsync(CurrentUser actor, CancellationToken ct);
    Task<SubscriptionPlan> SaveSubscriptionPlanAsync(
        Guid? id,
        SubscriptionPlanRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<FinancerSubscription> AssignSubscriptionAsync(
        AssignSubscriptionRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<object> GetSmsUsageAsync(PageQuery query, CurrentUser actor, CancellationToken ct);
}

public interface IDocumentService
{
    Task<IReadOnlyList<StoredDocument>> ListForFinancerAsync(Guid financerId, CurrentUser actor, CancellationToken ct);
    Task<IReadOnlyList<StoredDocument>> ListForCustomerAsync(
        Guid customerId,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<StoredDocument> UploadAsync(
        Stream content,
        string fileName,
        string contentType,
        long size,
        string category,
        Guid? financerId,
        Guid? customerId,
        Guid? applicationId,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<(StoredDocument Metadata, Stream Content)> DownloadAsync(
        Guid id,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<StoredDocument> VerifyAsync(
        Guid id,
        DocumentDecisionRequest request,
        CurrentUser actor,
        CancellationToken ct
    );
    Task<StoredDocument> GetAsync(Guid id, CurrentUser actor, CancellationToken ct);
    Task DeleteAsync(Guid id, CurrentUser actor, CancellationToken ct);
}

public sealed class DomainException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
