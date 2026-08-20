# INRFS Financer API

ASP.NET Core 9 Web API backend for the INRFS Financer and Admin portals. It uses Clean Architecture, EF Core, PostgreSQL for deployment (with SQLite supported for isolated local development), FluentValidation, JWT/rotating refresh tokens, RBAC permissions, Swagger, structured console logging, health checks, auditing, migrations, and xUnit tests.

## Projects

- `Domain`: entities, lifecycle enums and invariants.
- `Application`: DTOs, API contracts, validation and service abstractions.
- `Infrastructure`: EF Core context/migration, authentication, documents and business workflows.
- `API`: middleware, JWT/Swagger configuration and versioned controllers.
- `UnitTests` and `IntegrationTests`: validation and API smoke/security coverage.
- `docs/API_REQUIREMENTS.md`: frontend inventory, mappings and contract.
- `docs/DATABASE_SCHEMA.md`: schema/relationships and production migration review.

## Prerequisites

- .NET SDK 9.0.x.
- No database server is required locally; SQLite is used.

## Configuration

Never commit real values. Use environment variables or .NET User Secrets:

```powershell
cd inrfs_financer_api/src/INRFS.Financer.API
dotnet user-secrets init
dotnet user-secrets set "Jwt:Key" "a-long-random-development-signing-key"
dotnet user-secrets set "DataProtection:Key" "a-separate-long-random-data-key"
dotnet user-secrets set "SeedAdmin:Email" "admin@local.test"
dotnet user-secrets set "SeedAdmin:Password" "StrongLocalPassword123!"
dotnet user-secrets set "Database:Initialize" "true"
```

Equivalent environment-variable names are documented in `.env.example`. `appsettings.json` contains safe non-secret placeholders only. `AuthDelivery:Provider` supports `Development` locally and `Webhook` for an approved OTP/email gateway. The API refuses Development delivery outside the Development environment, so one-time values cannot accidentally be logged in production. `SmsGateway:Provider=Webhook` enables the durable queued-SMS dispatcher; `Disabled` leaves records queued without pretending delivery succeeded.

## Restore, build, test, run

```powershell
dotnet restore INRFS.Financer.slnx
dotnet build INRFS.Financer.slnx --no-restore
dotnet test INRFS.Financer.slnx --no-build
dotnet run --project src/INRFS.Financer.API
```

Swagger: `http://localhost:5187/swagger`. Health: `/health`, `/health/ready`, `/health/live`.

The first local Development run applies the included migration only when `Database__Initialize=true`. To apply explicitly to a local database:

```powershell
dotnet ef database update --project src/INRFS.Financer.Infrastructure --startup-project src/INRFS.Financer.API
```

## Authentication flow

1. `POST /api/v1/auth/login` validates password and returns an OTP challenge.
2. Receive the code through the configured auth-delivery provider (Development logs are local-only).
3. `POST /api/v1/auth/otp/verify` returns an access token and sets the rotating refresh token in an HttpOnly cookie.
4. Use `Authorization: Bearer <token>`.
5. Refresh via `/auth/refresh`; logout/revoke via `/auth/revoke`. Browser clients send credentials so JavaScript never persists the refresh token.

The financer portal also supports self-registration at `POST /api/v1/auth/register/financer`, mobile-or-email login, authenticated password changes, and `GET/PUT /api/v1/profile`. SMS actions create auditable queued delivery records that the configured background gateway dispatches and updates with provider references and delivery status.

## Security notes

- Tenant scope is checked in business services; platform roles are separately permission-gated.
- OTP and refresh tokens are stored only as hashes; browser refresh tokens use a scoped HttpOnly, Secure production cookie.
- Aadhaar/PAN values are encrypted before persistence and masked in DTOs. Use a managed KMS/envelope-encryption design in production.
- Document content is private, content-type/size restricted and SHA-256 hashed. Replace local storage with private encrypted object storage in production.
- Approval and disbursement enforce maker-checker separation.
- Payment recording, allocation, ledger entry, reversal and disbursement are transactional.

## Remote database status and approval boundary

The read-only SSH attempt reached `187.52.115.32` but failed authentication. Consequently the production engine/version/database/schema remain unknown. No remote database port was opened, no software/user/database was created, no migration was applied, and no data was touched.

Once access is provided, the first proposed remote operation is read-only: inspect OS, database processes/services/containers/listeners and client versions; then use existing local database authentication to list database names, schemas, tables, columns, keys/indexes and migration history. Before any write, this repository must be switched to the discovered provider and a provider-specific idempotent migration SQL script, target, backup/rollback plan, lock impact and verification commands must be shown for explicit approval.
