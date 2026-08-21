namespace INRFS.Financer.Domain;

public enum AccountStatus
{
    Pending,
    Active,
    Inactive,
    Suspended,
    Locked,
}

public enum VerificationStatus
{
    Pending,
    Submitted,
    NeedsInformation,
    Verified,
    Rejected,
}

public enum LoanApplicationStatus
{
    Draft,
    Submitted,
    EligibilityPassed,
    EligibilityFailed,
    KycPending,
    UnderVerification,
    Verified,
    Approved,
    Rejected,
    Disbursed,
    Cancelled,
}

public enum LoanStatus
{
    Active,
    Closed,
    Overdue,
    WrittenOff,
    Restructured,
    Cancelled,
}

public enum ScheduleStatus
{
    Upcoming,
    Due,
    PartiallyPaid,
    Paid,
    Overdue,
    Waived,
}

public enum PaymentStatus
{
    Pending,
    Completed,
    Failed,
    Reversed,
}

public enum PaymentMode
{
    Cash,
    BankTransfer,
    Upi,
    Cheque,
    Card,
    Other,
}

public enum TransactionType
{
    Disbursement,
    Payment,
    Principal,
    Interest,
    Fee,
    Penalty,
    Refund,
    Reversal,
    Adjustment,
}

public enum InterestMethod
{
    ReducingBalance,
    FlatRate,
    SimpleInterest,
}

public enum RepaymentFrequency
{
    Weekly,
    Fortnightly,
    Monthly,
    Quarterly,
    Daily,
}

public enum LoanDurationUnit { Days, Weeks, Months }

public enum InterestRateBasis { PerAnnum, PerMonth, PerWeek, PerDay }

public enum InterestCollectionFrequency { Daily, Weekly, Monthly, AtMaturity }

public enum LoanPaymentType { InterestOnly, Regular, FullSettlement }

public enum CollectionStatus
{
    Open,
    Contacted,
    PromiseToPay,
    PartiallyCollected,
    Collected,
    Escalated,
    Closed,
}

public enum TicketStatus
{
    Open,
    InProgress,
    WaitingOnCustomer,
    Resolved,
    Closed,
}

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Critical,
}

public enum NotificationChannel
{
    InApp,
    Sms,
    Email,
    Push,
}

public enum DocumentStatus
{
    Pending,
    Verified,
    Rejected,
}
