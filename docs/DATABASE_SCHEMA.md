# Database schema

## Provider status

The production database provider is intentionally unresolved. A read-only SSH attempt to `root@187.52.115.32` reached the server but was rejected because no usable key/password was available. Therefore no engine, version, database name, credentials, or schema was observed and no remote action was taken.

The development migration targets SQLite through EF Core 9. Provider registration is isolated in `Infrastructure/DependencyInjection.cs`. After approved production discovery, replace `UseSqlite` with the matching provider, regenerate a provider-specific reviewed migration, and do not reuse SQLite SQL blindly.

## Entity relationship overview

```text
FinancerOrganization
  ├─ UserAccount ─ UserRole ─ Role ─ RolePermission ─ Permission
  ├─ Customer ─ KycRecord / StoredDocument / CustomerNote
  │    └─ LoanApplication ─ LoanStatusHistory
  │         └─ Loan ─ PaymentSchedule ─ PaymentAllocation ─ Payment
  │                    ├─ FinancialTransaction
  │                    └─ CollectionCase ─ CollectionActivity
  ├─ ServiceChargeInvoice
  ├─ FinancerSubscription ─ SubscriptionPlan
  └─ SmsDelivery / Notification / SupportTicket

UserAccount ─ RefreshToken / OtpChallenge
PlatformSetting and AuditLog are platform-wide, optionally tenant-associated.
```

## Tables

| Table | Purpose and important relationships/indexes |
|---|---|
| `Financers` | Tenant/institution; unique financer number and email; status, KYC state and fee override |
| `Users` | Admins and financer employees; unique email/employee number; optional financer FK; password hash and lockout fields |
| `Roles`, `Permissions` | Named RBAC definitions with unique names |
| `UserRoles`, `RolePermissions` | Composite-key many-to-many join tables |
| `RefreshTokens` | SHA-256 token hashes, token family, expiry/revocation and rotation link |
| `OtpChallenges` | Hashed OTP/reset challenges, purpose, expiry, attempt count and consumed time |
| `Customers` | Tenant-scoped borrower identity/contact/address and encrypted identity values; unique tenant/customer number and tenant/phone |
| `CustomerNotes` | Audited notes linked to a customer |
| `KycRecords` | Identity submissions and decision metadata linked to a customer |
| `Documents` | Private document metadata, hash, storage key, links to customer/application and verification decision |
| `LoanProducts` | Product term/rate/eligibility rules; unique code |
| `EligibilityChecks` | Immutable inputs, computed FOIR/eligible amount, outcome and rule-result JSON |
| `LoanApplications` | Requested/approved terms and current workflow state; unique application number |
| `LoanStatusHistory` | Append-only application workflow transitions with actor/time/reason |
| `Loans` | One per disbursed application; unique loan number and unique application FK; current balances/status |
| `PaymentSchedules` | Unique loan/installment pair; amortization values, due/reschedule/payment status |
| `Payments` | Unique receipt number; unique non-null tenant/external reference; allocation totals and reversal state |
| `PaymentAllocations` | Exact fee/interest/principal allocation from a payment to an installment; supports correct reversal |
| `Transactions` | Disbursement/payment/fee/reversal ledger and reconciliation data; unique transaction number |
| `CollectionCases` | One case per loan with ageing, assignment, promise-to-pay and state |
| `CollectionActivities` | Contact/reminder/note/status activity history for a collection case |
| `Notifications` | User or financer messages, delivery channel/reference and read state |
| `SupportTickets`, `TicketMessages` | Tenant ticket lifecycle, assignment, threaded replies and internal messages |
| `Settings` | Unique scope/key configuration with typed value, secret flag and optimistic version |
| `ServiceChargeInvoices` | Unique invoice and unique financer/period, calculated activity/charge, collections and status |
| `SubscriptionPlans` | Unique commercial plan code, limits, credits and feature JSON |
| `FinancerSubscriptions` | Effective-dated plan assignment and status |
| `SmsDeliveries` | Tenant/customer notification delivery state, provider reference and consumed credits |
| `AuditLogs` | Append-only actor/action/entity/before-after/IP/correlation history; indexed by time and entity |

## Cross-cutting columns

Most business tables derive from `Entity` and have UUID `Id`, `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `IsDeleted`, `DeletedAt`, and `DeletedBy`. Global EF query filters exclude soft-deleted rows. Financial decimals use precision 18, scale 2. Audit logs use an auto-incrementing 64-bit key and are never soft-deleted through the API.

## Migration

`InitialCreate` is under `src/INRFS.Financer.Infrastructure/Migrations`. It has not been applied to the remote server. Local application is enabled only when `Database__Initialize=true`.

## Production review checklist

1. Identify engine/version/database/schema through approved read-only access.
2. Inventory existing tables, keys, indexes, row counts, extensions/collations and migration history.
3. Select the matching EF Core provider and naming/schema convention.
4. Generate a new provider-specific migration and idempotent SQL script.
5. Review table-name collisions, type mappings, precision, encrypted columns, indexes and foreign-key delete behavior.
6. Take and verify a production backup using the operator's established procedure.
7. Present the exact script, execution identity, target database, estimated locks and rollback plan for approval.
8. Apply only during an approved maintenance window, then verify migration history and health checks.
