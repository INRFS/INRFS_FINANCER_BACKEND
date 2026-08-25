using INRFS.Financer.Domain;
using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace INRFS.Financer.Infrastructure;

public sealed class DatabaseInitializer(
    FinancerDbContext db,
    IPasswordHasher<UserAccount> hasher,
    IConfiguration configuration
)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!configuration.GetValue("Database:Initialize", false))
            return;
        var dataSource = db.Database.GetDbConnection().DataSource;
        if (
            db.Database.IsSqlite()
            && !string.IsNullOrWhiteSpace(dataSource)
            && dataSource != ":memory:"
        )
        {
            var fullPath = Path.GetFullPath(dataSource);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }
        // The checked-in migrations target PostgreSQL. SQLite is an isolated local/test
        // provider and must build its schema from the active model instead of attempting
        // to apply provider-specific migrations.
        if (db.Database.IsSqlite())
        {
            await db.Database.EnsureCreatedAsync(ct);
            await EnsureSqliteSchemaCompatibilityAsync(ct);
        }
        else
            await db.Database.MigrateAsync(ct);
        if (await db.Roles.AnyAsync(ct))
        {
            await EnsureDashboardAccessGrantsAsync(ct);
            await EnsureSeedFinancerAsync(ct);
            await db.SaveChangesAsync(ct);
            return;
        }
        var permissionNames = new[]
        {
            "dashboard.read",
            "financers.read",
            "financers.manage",
            "users.manage",
            "roles.manage",
            "customers.read",
            "customers.manage",
            "kyc.verify",
            "documents.verify",
            "products.manage",
            "loans.read",
            "loans.create",
            "loans.verify",
            "loans.approve",
            "loans.disburse",
            "payments.read",
            "payments.record",
            "payments.reverse",
            "collections.read",
            "collections.manage",
            "notifications.manage",
            "support.read",
            "support.create",
            "support.manage",
            "reports.read",
            "settings.read",
            "settings.manage",
            "audit.read",
        };
        var permissions = permissionNames
            .Select(x => new Permission { Name = x, Description = x.Replace('.', ' ') })
            .ToList();
        db.Permissions.AddRange(permissions);
        var super = new Role
        {
            Name = "SuperAdmin",
            Description = "Full platform access",
            IsSystem = true,
        };
        foreach (var p in permissions)
            super.RolePermissions.Add(new RolePermission { Role = super, Permission = p });
        var ownerPermissions = permissions
            .Where(x =>
                x.Name
                    is not (
                        "financers.manage"
                        or "roles.manage"
                        or "kyc.verify"
                        or "loans.approve"
                        or "loans.disburse"
                        or "payments.reverse"
                        or "audit.read"
                    )
            )
            .ToList();
        var owner = new Role
        {
            Name = "FinancerOwner",
            Description = "Financer organization owner",
            IsSystem = true,
        };
        foreach (var p in ownerPermissions)
            owner.RolePermissions.Add(new RolePermission { Role = owner, Permission = p });
        var roleGrants = new Dictionary<string, string[]>
        {
            ["Admin"] = permissionNames.Where(x => x != "roles.manage").ToArray(),
            ["ComplianceOfficer"] =
            [
                "dashboard.read",
                "financers.read",
                "customers.read",
                "kyc.verify",
                "documents.verify",
                "loans.read",
                "loans.verify",
                "reports.read",
                "audit.read",
            ],
            ["FinanceOfficer"] =
            [
                "dashboard.read",
                "financers.read",
                "customers.read",
                "loans.read",
                "loans.approve",
                "loans.disburse",
                "payments.read",
                "payments.record",
                "payments.reverse",
                "collections.read",
                "collections.manage",
                "reports.read",
                "settings.read",
                "audit.read",
            ],
            ["FinancerManager"] =
            [
                "dashboard.read",
                "users.manage",
                "customers.read",
                "customers.manage",
                "loans.read",
                "loans.create",
                "loans.verify",
                "payments.read",
                "payments.record",
                "collections.read",
                "collections.manage",
                "notifications.manage",
                "support.read",
                "support.create",
                "reports.read",
                "settings.read",
                "settings.manage",
            ],
            ["LoanOfficer"] =
            [
                "dashboard.read",
                "customers.read",
                "customers.manage",
                "loans.read",
                "loans.create",
                "loans.verify",
                "payments.read",
                "collections.read",
                "support.read",
                "support.create",
            ],
            ["CollectionAgent"] =
            [
                "dashboard.read",
                "customers.read",
                "loans.read",
                "payments.read",
                "payments.record",
                "collections.read",
                "collections.manage",
                "notifications.manage",
                "support.create",
            ],
            ["SupportAgent"] =
            [
                "dashboard.read",
                "financers.read",
                "customers.read",
                "loans.read",
                "payments.read",
                "notifications.manage",
                "support.read",
                "support.create",
                "support.manage",
                "reports.read",
            ],
            ["Auditor"] =
            [
                "dashboard.read",
                "financers.read",
                "customers.read",
                "loans.read",
                "payments.read",
                "collections.read",
                "reports.read",
                "settings.read",
                "audit.read",
            ],
        };
        foreach (var grant in roleGrants)
        {
            var role = new Role { Name = grant.Key, IsSystem = true };
            foreach (var p in permissions.Where(x => grant.Value.Contains(x.Name)))
                role.RolePermissions.Add(new RolePermission { Role = role, Permission = p });
            db.Roles.Add(role);
        }
        db.Roles.AddRange(super, owner);
        var email = configuration["SeedAdmin:Email"];
        var password = configuration["SeedAdmin:Password"];
        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password))
        {
            var admin = new UserAccount
            {
                EmployeeNumber = NumberGenerator.New("ADM"),
                FirstName = "System",
                LastName = "Administrator",
                Email = email.Trim().ToLowerInvariant(),
                Phone = "",
                Status = AccountStatus.Active,
                MfaRequired = true,
            };
            admin.PasswordHash = hasher.HashPassword(admin, password);
            admin.UserRoles.Add(new UserRole { User = admin, Role = super });
            db.Users.Add(admin);
        }
        db.LoanProducts.Add(
            new LoanProduct
            {
                Code = "STANDARD",
                Name = "Standard Reducing Balance Loan",
                MinimumPrincipal = 1000,
                MaximumPrincipal = 10000000,
                MinimumTenureMonths = 1,
                MaximumTenureMonths = 120,
                AnnualInterestRate = 18,
                InterestMethod = InterestMethod.ReducingBalance,
                RepaymentFrequency = RepaymentFrequency.Monthly,
                ProcessingFeePercentage = 1,
                LateFeePercentage = 2,
                MaximumFoirPercentage = 50,
                IsActive = true,
            }
        );
        await db.SaveChangesAsync(ct);
        await EnsureSeedFinancerAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureDashboardAccessGrantsAsync(CancellationToken ct)
    {
        var grants = new Dictionary<string, string[]>
        {
            ["dashboard.read"] =
            [
                "SuperAdmin", "Admin", "ComplianceOfficer", "FinanceOfficer", "FinancerOwner",
                "FinancerManager", "LoanOfficer", "CollectionAgent", "SupportAgent", "Auditor",
            ],
            ["financers.read"] =
            [
                "SuperAdmin", "Admin", "ComplianceOfficer", "FinanceOfficer", "SupportAgent", "Auditor",
            ],
        };

        var permissionNames = grants.Keys.ToArray();
        var permissions = await db.Permissions
            .Include(permission => permission.RolePermissions)
            .Where(permission => permissionNames.Contains(permission.Name))
            .ToDictionaryAsync(permission => permission.Name, ct);

        foreach (var permissionName in permissionNames)
        {
            if (permissions.ContainsKey(permissionName))
                continue;
            var permission = new Permission
            {
                Name = permissionName,
                Description = permissionName.Replace('.', ' '),
            };
            db.Permissions.Add(permission);
            permissions[permissionName] = permission;
        }

        var roleNames = grants.Values.SelectMany(names => names).Distinct().ToArray();
        var roles = await db.Roles
            .Include(role => role.RolePermissions)
            .Where(role => roleNames.Contains(role.Name))
            .ToDictionaryAsync(role => role.Name, ct);

        foreach (var (permissionName, grantedRoleNames) in grants)
        {
            var permission = permissions[permissionName];
            foreach (var roleName in grantedRoleNames)
            {
                if (!roles.TryGetValue(roleName, out var role))
                    continue;
                if (role.RolePermissions.Any(grant => grant.PermissionId == permission.Id || grant.Permission == permission))
                    continue;
                role.RolePermissions.Add(new RolePermission { Role = role, Permission = permission });
            }
        }
    }

    private async Task EnsureSqliteSchemaCompatibilityAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            async Task<HashSet<string>> GetColumnsAsync(string table)
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{table}\");";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    result.Add(reader.GetString(1));
                return result;
            }

            async Task ExecuteAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(ct);
            }

            var columns = await GetColumnsAsync("Users");

            // EnsureCreated does not update an existing SQLite database when the model
            // gains a column. Keep local developer data while applying small additive
            // compatibility upgrades; production databases use EF migrations instead.
            if (!columns.Contains("ProfileImageDataUrl"))
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "ALTER TABLE \"Users\" ADD COLUMN \"ProfileImageDataUrl\" TEXT NULL;";
                await command.ExecuteNonQueryAsync(ct);
            }

            var loanColumns = await GetColumnsAsync("Loans");
            var loanAdditions = new (string Name, string Definition)[]
            {
                ("DurationValue", "INTEGER NOT NULL DEFAULT 0"),
                ("DurationUnit", "INTEGER NOT NULL DEFAULT 2"),
                ("InterestRate", "TEXT NOT NULL DEFAULT '0'"),
                ("InterestRateBasis", "INTEGER NOT NULL DEFAULT 0"),
                ("InterestCollectionFrequency", "INTEGER NOT NULL DEFAULT 2"),
            };
            foreach (var (name, definition) in loanAdditions)
                if (!loanColumns.Contains(name))
                    await ExecuteAsync($"ALTER TABLE \"Loans\" ADD COLUMN \"{name}\" {definition};");

            var collectionColumns = await GetColumnsAsync("CollectionCases");
            if (!collectionColumns.Contains("NextFollowUpDate"))
                await ExecuteAsync("ALTER TABLE \"CollectionCases\" ADD COLUMN \"NextFollowUpDate\" TEXT NULL;");

            await ExecuteAsync("""
                UPDATE "Loans"
                SET "DurationValue" = CASE WHEN "DurationValue" = 0 THEN "TenureMonths" ELSE "DurationValue" END,
                    "DurationUnit" = CASE WHEN "DurationValue" = 0 THEN 2 ELSE "DurationUnit" END,
                    "InterestRate" = CASE WHEN CAST("InterestRate" AS REAL) = 0 THEN "AnnualInterestRate" ELSE "InterestRate" END,
                    "InterestRateBasis" = CASE WHEN "DurationValue" = 0 THEN 0 ELSE "InterestRateBasis" END,
                    "InterestCollectionFrequency" = CASE WHEN "DurationValue" = 0 THEN 2 ELSE "InterestCollectionFrequency" END;
                """);

            var scheduleColumns = await GetColumnsAsync("PaymentSchedules");
            var scheduleAdditions = new (string Name, string Definition)[]
            {
                ("PeriodStart", "TEXT NOT NULL DEFAULT '0001-01-01'"),
                ("PeriodEnd", "TEXT NOT NULL DEFAULT '0001-01-01'"),
                ("InterestDays", "INTEGER NOT NULL DEFAULT 0"),
            };
            foreach (var (name, definition) in scheduleAdditions)
                if (!scheduleColumns.Contains(name))
                    await ExecuteAsync($"ALTER TABLE \"PaymentSchedules\" ADD COLUMN \"{name}\" {definition};");

            await ExecuteAsync("""
                UPDATE "PaymentSchedules"
                SET "PeriodEnd" = "DueDate",
                    "PeriodStart" = date("DueDate", '-1 month'),
                    "InterestDays" = CAST(julianday("DueDate") - julianday(date("DueDate", '-1 month')) AS INTEGER)
                WHERE "InterestDays" = 0;
                """);

            const string invoicePeriodIndex =
                "IX_ServiceChargeInvoices_FinancerId_PeriodStart_PeriodEnd";
            var invoicePeriodIndexIsUnique = false;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA index_list(\"ServiceChargeInvoices\");";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (string.Equals(reader.GetString(1), invoicePeriodIndex, StringComparison.Ordinal))
                    {
                        invoicePeriodIndexIsUnique = reader.GetInt32(2) == 1;
                        break;
                    }
                }
            }
            if (invoicePeriodIndexIsUnique)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    DROP INDEX "{invoicePeriodIndex}";
                    CREATE INDEX "{invoicePeriodIndex}"
                    ON "ServiceChargeInvoices" ("FinancerId", "PeriodStart", "PeriodEnd");
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private async Task EnsureSeedFinancerAsync(CancellationToken ct)
    {
        var mobile = configuration["SeedFinancer:Mobile"]?.Trim();
        var email = configuration["SeedFinancer:Email"]?.Trim().ToLowerInvariant();
        var password = configuration["SeedFinancer:Password"];
        if (string.IsNullOrWhiteSpace(mobile) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        var ownerRole = await db.Roles.SingleAsync(x => x.Name == "FinancerOwner", ct);
        var user = await db.Users.Include(x => x.Financer).Include(x => x.UserRoles)
            .SingleOrDefaultAsync(x => x.Email == email || x.Phone == mobile, ct);
        if (user is null)
        {
            var financer = new FinancerOrganization
            {
                FinancerNumber = NumberGenerator.New("FIN"),
                LegalName = "INRFS Demo Finance",
                DisplayName = "INRFS Demo Finance",
                OwnerName = "Demo Financer",
                Email = email,
                Phone = mobile,
                City = "Hyderabad",
                State = "Telangana",
                Status = AccountStatus.Active,
                KycStatus = VerificationStatus.Pending,
            };
            user = new UserAccount
            {
                Financer = financer,
                EmployeeNumber = NumberGenerator.New("OWN"),
                FirstName = "Demo",
                LastName = "Financer",
                Email = email,
                Phone = mobile,
                Status = AccountStatus.Active,
            };
            user.UserRoles.Add(new UserRole { User = user, Role = ownerRole });
            db.AddRange(financer, user);
        }
        else
        {
            user.Status = AccountStatus.Active;
            if (user.Financer is not null)
                user.Financer.Status = AccountStatus.Active;
            if (!user.UserRoles.Any(x => x.RoleId == ownerRole.Id))
                user.UserRoles.Add(new UserRole { User = user, Role = ownerRole });
        }
        user.PasswordHash = hasher.HashPassword(user, password);
    }
}
