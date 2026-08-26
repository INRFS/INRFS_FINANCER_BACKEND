using System.Text.Json;
using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace INRFS.Financer.Infrastructure;

public sealed class PlatformService(
    FinancerDbContext db,
    IPasswordHasher<UserAccount> passwordHasher,
    IConfiguration configuration,
    IAuthMessageSender messageSender
) : IPlatformService
{
    private static int Page(PageQuery q) => Math.Max(1, q.Page);

    private static int Size(PageQuery q) => Math.Clamp(q.PageSize, 1, 100);

    private static bool IsPlatform(CurrentUser a) => a.FinancerId is null;

    private static void RequireTenant(Guid tenant, CurrentUser a)
    {
        if (!IsPlatform(a) && a.FinancerId != tenant)
            throw new DomainException("Resource is outside your organization.", 403);
    }

    private static void Require(CurrentUser a, string permission)
    {
        if (!a.Roles.Contains("SuperAdmin") && !a.Permissions.Contains(permission))
            throw new DomainException("Permission denied.", 403);
    }

    private async Task NotifyPlatformAdminsAsync(
        string title,
        string message,
        string type,
        string entityType,
        Guid entityId,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        var adminIds = await db.Users
            .AsNoTracking()
            .Where(user =>
                user.FinancerId == null
                && user.Status == AccountStatus.Active
                && user.UserRoles.Any(userRole =>
                    userRole.Role.Name == "SuperAdmin" || userRole.Role.Name == "Admin"))
            .Select(user => user.Id)
            .ToListAsync(ct);

        db.Notifications.AddRange(adminIds.Select(adminId => new Notification
        {
            UserId = adminId,
            Title = title,
            Message = message,
            Type = type,
            Channel = NotificationChannel.InApp,
            EntityType = entityType,
            EntityId = entityId,
            SentAt = DateTimeOffset.UtcNow,
            CreatedBy = actor.UserId,
        }));
    }

    private string DataKey =>
        configuration["DataProtection:Key"]
        ?? throw new InvalidOperationException("DataProtection:Key is required.");

    private void Audit(
        CurrentUser actor,
        string action,
        string type,
        object id,
        object? before = null,
        object? after = null
    ) =>
        db.AuditLogs.Add(
            new AuditLog
            {
                ActorId = actor.UserId,
                FinancerId = actor.FinancerId,
                Action = action,
                EntityType = type,
                EntityId = id.ToString()!,
                BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
                AfterJson = after is null ? null : JsonSerializer.Serialize(after),
            }
        );

    public async Task<object> GetMyProfileAsync(CurrentUser actor, CancellationToken ct)
    {
        var user =
            await db
                .Users.AsNoTracking()
                .Include(x => x.UserRoles)
                    .ThenInclude(x => x.Role)
                .SingleOrDefaultAsync(x => x.Id == actor.UserId, ct)
            ?? throw new DomainException("User not found.", 404);
        var financer = actor.FinancerId.HasValue
            ? await db
                .Financers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == actor.FinancerId, ct)
            : null;
        var subscription = actor.FinancerId.HasValue
            ? await db
                .FinancerSubscriptions.AsNoTracking()
                .Include(x => x.SubscriptionPlan)
                .Where(x => x.FinancerId == actor.FinancerId && x.Status == AccountStatus.Active)
                .OrderByDescending(x => x.StartsOn)
                .FirstOrDefaultAsync(ct)
            : null;
        var creditsUsed = actor.FinancerId.HasValue
            ? await db
                .SmsDeliveries.Where(x => x.FinancerId == actor.FinancerId)
                .SumAsync(x => (int?)x.CreditsUsed, ct)
                ?? 0
            : 0;
        return new
        {
            user = Map(user),
            financer = financer is null ? null : Map(financer),
            profileImage = user.ProfileImageDataUrl,
            plan = subscription?.SubscriptionPlan.Name,
            smsCredits = subscription is null
                ? 0
                : Math.Max(0, subscription.SubscriptionPlan.SmsCredits - creditsUsed),
        };
    }

    public async Task<object> UpdateMyProfileAsync(
        UpdateMyProfileRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        var user =
            await db
                .Users.Include(x => x.UserRoles)
                    .ThenInclude(x => x.Role)
                .SingleOrDefaultAsync(x => x.Id == actor.UserId, ct)
            ?? throw new DomainException("User not found.", 404);
        var financerId =
            actor.FinancerId ?? throw new DomainException("A financer profile is required.");
        var financer = await db.Financers.SingleAsync(x => x.Id == financerId, ct);
        var email = r.Email.Trim().ToLowerInvariant();
        var mobile = r.Mobile.Trim();
        if (
            await db.Users.AnyAsync(
                x => x.Id != user.Id && (x.Email == email || x.Phone == mobile),
                ct
            )
        )
            throw new DomainException("Email or mobile is already in use.", 409);
        var parts = r.FullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        user.FirstName = parts[0];
        user.LastName = parts.Length > 1 ? parts[1] : "";
        user.Email = financer.Email = email;
        user.Phone = financer.Phone = mobile;
        financer.OwnerName = r.FullName.Trim();
        financer.DisplayName = financer.LegalName = r.BusinessName.Trim();
        financer.City = r.City.Trim();
        financer.State = r.State.Trim();
        if (
            !string.IsNullOrWhiteSpace(r.ProfileImageDataUrl)
            && (
                r.ProfileImageDataUrl.Length > 2_800_000
                || !r.ProfileImageDataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            )
        )
            throw new DomainException("Profile photo must be a valid image no larger than 2 MB.");
        user.ProfileImageDataUrl = string.IsNullOrWhiteSpace(r.ProfileImageDataUrl)
            ? null
            : r.ProfileImageDataUrl;
        Audit(actor, "Profile.Updated", nameof(FinancerOrganization), financer.Id);
        await db.SaveChangesAsync(ct);
        return await GetMyProfileAsync(actor, ct);
    }

    public async Task<PagedResult<FinancerDto>> GetFinancersAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "financers.read");
        var query = db.Financers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(x =>
                x.LegalName.Contains(s)
                || x.DisplayName.Contains(s)
                || x.FinancerNumber.Contains(s)
                || x.Email.Contains(s)
            );
        }
        if (Enum.TryParse<AccountStatus>(q.Status, true, out var status))
            query = query.Where(x => x.Status == status);
        var count = await query.LongCountAsync(ct);
        query = q.SortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase)
            ? query.OrderByDescending(x => x.CreatedAt)
            : query.OrderBy(x => x.DisplayName);
        var items = await query
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .Select(x => new FinancerDto(
                x.Id,
                x.FinancerNumber,
                x.LegalName,
                x.DisplayName,
                x.OwnerName,
                x.Email,
                x.Phone,
                x.City,
                x.State,
                x.Status,
                x.KycStatus,
                x.ServiceChargePercentage,
                x.CreatedAt
            ))
            .ToListAsync(ct);
        return new(items, Page(q), Size(q), count);
    }

    public async Task<FinancerDto> CreateFinancerAsync(
        CreateFinancerRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "financers.manage");
        var email = r.Email.Trim().ToLowerInvariant();
        if (
            await db.Financers.AnyAsync(
                x =>
                    x.Email == email
                    || (
                        !string.IsNullOrEmpty(r.RegistrationNumber)
                        && x.RegistrationNumber == r.RegistrationNumber
                    ),
                ct
            )
        )
            throw new DomainException(
                "A financer with this email or registration number already exists.",
                409
            );
        var x = new FinancerOrganization
        {
            FinancerNumber = NumberGenerator.New("FIN"),
            LegalName = r.LegalName.Trim(),
            DisplayName = r.DisplayName.Trim(),
            OwnerName = r.OwnerName.Trim(),
            Email = email,
            Phone = r.Phone.Trim(),
            AddressLine = r.AddressLine.Trim(),
            City = r.City.Trim(),
            State = r.State.Trim(),
            PostalCode = r.PostalCode.Trim(),
            TaxNumber = r.TaxNumber?.Trim(),
            RegistrationNumber = r.RegistrationNumber?.Trim(),
            ServiceChargePercentage = r.ServiceChargePercentage,
            Status = AccountStatus.Pending,
            KycStatus = VerificationStatus.Pending,
            CreatedBy = actor.UserId,
        };
        db.Financers.Add(x);
        Audit(actor, "Financer.Created", nameof(FinancerOrganization), x.Id, null, x);
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    public async Task<FinancerDto> ChangeFinancerStatusAsync(
        Guid id,
        ChangeStatusRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "financers.manage");
        if (!Enum.TryParse<AccountStatus>(r.Status, true, out var status))
            throw new DomainException("Invalid status.");
        if (string.IsNullOrWhiteSpace(r.Reason))
            throw new DomainException("A reason is required.");
        var x =
            await db.Financers.FindAsync([id], ct)
            ?? throw new DomainException("Financer not found.", 404);
        var before = x.Status;
        x.Status = status;
        x.UpdatedAt = DateTimeOffset.UtcNow;
        x.UpdatedBy = actor.UserId;
        Audit(
            actor,
            "Financer.StatusChanged",
            nameof(FinancerOrganization),
            id,
            new { Status = before },
            new { Status = status, r.Reason }
        );
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    public async Task<FinancerDto> DecideFinancerKycAsync(Guid id, KycDecisionRequest r, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "kyc.verify");
        if (r.Status is not (VerificationStatus.Verified or VerificationStatus.Rejected or VerificationStatus.NeedsInformation))
            throw new DomainException("Invalid KYC decision.");
        var financer = await db.Financers.FindAsync([id], ct) ?? throw new DomainException("Financer not found.", 404);
        var before = financer.KycStatus;
        financer.KycStatus = r.Status;
        Audit(actor, "Financer.KycDecided", nameof(FinancerOrganization), id, new { Status = before }, new { r.Status, r.Notes });
        await db.SaveChangesAsync(ct);
        return Map(financer);
    }

    public async Task<IReadOnlyList<FinancerBillingUsageDto>> GetFinancerBillingUsageAsync(
        CurrentUser actor,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null
    )
    {
        Require(actor, "dashboard.read");
        if (!IsPlatform(actor))
            throw new DomainException("Platform access is required.", 403);

        var financers = await db.Financers.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(ct);
        var paymentQuery = db.Payments.AsNoTracking()
            .Where(x => x.Status == PaymentStatus.Completed);
        if (from.HasValue)
        {
            var fromTimestamp = new DateTimeOffset(from.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            paymentQuery = paymentQuery.Where(x => x.ReceivedAt >= fromTimestamp);
        }
        if (to.HasValue)
        {
            var toTimestamp = new DateTimeOffset(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            paymentQuery = paymentQuery.Where(x => x.ReceivedAt < toTimestamp);
        }
        var interestByFinancer = await paymentQuery
            .GroupBy(x => x.FinancerId)
            .Select(x => new { FinancerId = x.Key, Amount = x.Sum(p => p.InterestAmount) })
            .ToDictionaryAsync(x => x.FinancerId, x => x.Amount, ct);
        var invoiceQuery = db.ServiceChargeInvoices.AsNoTracking().AsQueryable();
        if (from.HasValue)
            invoiceQuery = invoiceQuery.Where(x => x.PeriodStart >= from.Value);
        if (to.HasValue)
            invoiceQuery = invoiceQuery.Where(x => x.PeriodEnd <= to.Value);
        var invoiceTotals = await invoiceQuery
            .GroupBy(x => x.FinancerId)
            .Select(x => new
            {
                FinancerId = x.Key,
                Generated = x.Sum(i => i.ChargeAmount),
                Collected = x.Sum(i => i.CollectedAmount),
                Overdue = x.Sum(i => i.Status == ScheduleStatus.Overdue
                    ? Math.Max(0, i.ChargeAmount - i.CollectedAmount)
                    : 0),
            })
            .ToDictionaryAsync(x => x.FinancerId, ct);
        var settings = await db.Settings.AsNoTracking()
            .Where(x => x.Key == "ServiceChargePercentage")
            .ToListAsync(ct);
        var platformPercentage = decimal.TryParse(
            settings.SingleOrDefault(x => x.Scope == "Platform")?.Value,
            out var configuredPlatformPercentage
        ) ? configuredPlatformPercentage : 1;
        var overrides = settings
            .Where(x => x.Scope.StartsWith("Financer:"))
            .Select(x => new
            {
                Id = Guid.TryParse(x.Scope["Financer:".Length..], out var id) ? id : Guid.Empty,
                Percentage = decimal.TryParse(x.Value, out var value) ? value : (decimal?)null,
            })
            .Where(x => x.Id != Guid.Empty && x.Percentage.HasValue)
            .ToDictionary(x => x.Id, x => x.Percentage!.Value);

        return financers.Select(financer =>
        {
            invoiceTotals.TryGetValue(financer.Id, out var invoices);
            var interest = interestByFinancer.GetValueOrDefault(financer.Id);
            var percentage = overrides.GetValueOrDefault(
                financer.Id,
                financer.ServiceChargePercentage ?? platformPercentage
            );
            var generated = Math.Round(interest * percentage / 100, 2);
            var collected = invoices?.Collected ?? 0;
            return new FinancerBillingUsageDto(
                financer.Id,
                financer.FinancerNumber,
                financer.DisplayName,
                financer.Status,
                interest,
                generated,
                collected,
                Math.Max(0, generated - collected),
                invoices?.Overdue ?? 0
            );
        }).ToList();
    }

    public async Task<PagedResult<UserDto>> GetUsersAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "users.manage");
        var query = db
            .Users.AsNoTracking()
            .Include(x => x.UserRoles)
                .ThenInclude(x => x.Role)
            .AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(x =>
                x.FirstName.Contains(s)
                || x.LastName.Contains(s)
                || x.Email.Contains(s)
                || x.EmployeeNumber.Contains(s)
            );
        }
        var count = await query.LongCountAsync(ct);
        var data = await query
            .OrderBy(x => x.FirstName)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new(data.Select(Map).ToList(), Page(q), Size(q), count);
    }

    public async Task<UserDto> CreateUserAsync(
        CreateUserRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "users.manage");
        var tenant = r.FinancerId ?? actor.FinancerId;
        if (tenant.HasValue)
            RequireTenant(tenant.Value, actor);
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == email, ct))
            throw new DomainException("Email already exists.", 409);
        var roles = await db.Roles.Where(x => r.RoleIds.Contains(x.Id)).ToListAsync(ct);
        if (roles.Count != r.RoleIds.Distinct().Count())
            throw new DomainException("One or more roles do not exist.");
        var x = new UserAccount
        {
            FinancerId = tenant,
            EmployeeNumber = NumberGenerator.New("EMP"),
            FirstName = r.FirstName.Trim(),
            LastName = r.LastName.Trim(),
            Email = email,
            Phone = r.Phone.Trim(),
            Status = AccountStatus.Active,
            MfaRequired = true,
            CreatedBy = actor.UserId,
        };
        x.PasswordHash = passwordHasher.HashPassword(x, r.Password);
        foreach (var role in roles)
            x.UserRoles.Add(new UserRole { User = x, Role = role });
        db.Users.Add(x);
        Audit(
            actor,
            "User.Created",
            nameof(UserAccount),
            x.Id,
            null,
            new
            {
                x.Email,
                x.FinancerId,
                Roles = roles.Select(y => y.Name),
            }
        );
        await db.SaveChangesAsync(ct);
        var isPlatformAdministrator =
            !tenant.HasValue
            && roles.Any(role => role.Name is "SuperAdmin" or "Admin");
        if (isPlatformAdministrator)
            await messageSender.SendWelcomeCredentialsAsync(x.Email, x.Email, r.Password, ct);
        return Map(x);
    }

    public async Task<UserDto> UpdateUserAsync(
        Guid id,
        UpdateUserRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "users.manage");
        var x =
            await db
                .Users.Include(x => x.UserRoles)
                    .ThenInclude(x => x.Role)
                .SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("User not found.", 404);
        if (x.FinancerId.HasValue)
            RequireTenant(x.FinancerId.Value, actor);
        x.FirstName = r.FirstName.Trim();
        x.LastName = r.LastName.Trim();
        x.Phone = r.Phone.Trim();
        x.Status = r.Status;
        x.UpdatedAt = DateTimeOffset.UtcNow;
        x.UpdatedBy = actor.UserId;
        Audit(
            actor,
            "User.Updated",
            nameof(UserAccount),
            id,
            null,
            new
            {
                x.FirstName,
                x.LastName,
                x.Status,
            }
        );
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    public async Task<UserDto> SetUserRolesAsync(
        Guid id,
        IReadOnlyList<Guid> roleIds,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "users.manage");
        var x =
            await db
                .Users.Include(x => x.UserRoles)
                    .ThenInclude(x => x.Role)
                .SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("User not found.", 404);
        if (x.FinancerId.HasValue)
            RequireTenant(x.FinancerId.Value, actor);
        var roles = await db.Roles.Where(x => roleIds.Contains(x.Id)).ToListAsync(ct);
        if (roles.Count != roleIds.Distinct().Count())
            throw new DomainException("One or more roles do not exist.");
        db.UserRoles.RemoveRange(x.UserRoles);
        x.UserRoles = [];
        foreach (var role in roles)
            x.UserRoles.Add(new UserRole { User = x, Role = role });
        Audit(
            actor,
            "User.RolesChanged",
            nameof(UserAccount),
            id,
            null,
            new { Roles = roles.Select(x => x.Name) }
        );
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    public async Task DeleteUserAsync(Guid id, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "users.manage");
        if (id == actor.UserId)
            throw new DomainException("You cannot delete your own account.", 409);
        var x =
            await db.Users.FindAsync([id], ct) ?? throw new DomainException("User not found.", 404);
        if (x.FinancerId.HasValue)
            RequireTenant(x.FinancerId.Value, actor);
        x.IsDeleted = true;
        x.DeletedAt = DateTimeOffset.UtcNow;
        x.DeletedBy = actor.UserId;
        foreach (
            var token in await db
                .RefreshTokens.Where(t => t.UserId == id && t.RevokedAt == null)
                .ToListAsync(ct)
        )
            token.RevokedAt = DateTimeOffset.UtcNow;
        Audit(actor, "User.Deleted", nameof(UserAccount), id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AdminSessionDto>> GetUserSessionsAsync(Guid userId, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "users.manage");
        var target = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new DomainException("User not found.", 404);
        if (target.FinancerId.HasValue) RequireTenant(target.FinancerId.Value, actor);
        var now = DateTimeOffset.UtcNow;
        return await db.RefreshTokens.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new AdminSessionDto(x.Id, x.UserId, x.Family, x.CreatedAt, x.ExpiresAt, x.RevokedAt, x.RevokedAt == null && x.ExpiresAt > now))
            .ToListAsync(ct);
    }

    public async Task RevokeUserSessionAsync(Guid userId, Guid sessionId, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "users.manage");
        var session = await db.RefreshTokens.SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, ct)
            ?? throw new DomainException("Session not found.", 404);
        if (session.RevokedAt == null) session.RevokedAt = DateTimeOffset.UtcNow;
        Audit(actor, "User.SessionRevoked", nameof(RefreshToken), sessionId, null, new { userId });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct) =>
        await db
            .Roles.AsNoTracking()
            .Include(x => x.RolePermissions)
                .ThenInclude(x => x.Permission)
            .OrderBy(x => x.Name)
            .Select(x => new RoleDto(
                x.Id,
                x.Name,
                x.Description,
                x.RolePermissions.Select(p => p.Permission.Name).ToList()
            ))
            .ToListAsync(ct);

    public async Task<RoleDto> CreateRoleAsync(
        CreateRoleRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "roles.manage");
        if (await db.Roles.AnyAsync(x => x.Name == r.Name, ct))
            throw new DomainException("Role already exists.", 409);
        var ps = await db.Permissions.Where(x => r.PermissionIds.Contains(x.Id)).ToListAsync(ct);
        if (ps.Count != r.PermissionIds.Distinct().Count())
            throw new DomainException("One or more permissions do not exist.");
        var role = new Role
        {
            Name = r.Name.Trim(),
            Description = r.Description,
            CreatedBy = actor.UserId,
        };
        foreach (var p in ps)
            role.RolePermissions.Add(new RolePermission { Role = role, Permission = p });
        db.Roles.Add(role);
        Audit(
            actor,
            "Role.Created",
            nameof(Role),
            role.Id,
            null,
            new { role.Name, Permissions = ps.Select(x => x.Name) }
        );
        await db.SaveChangesAsync(ct);
        return new(role.Id, role.Name, role.Description, ps.Select(x => x.Name).ToList());
    }

    public async Task<PagedResult<CustomerDto>> GetCustomersAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "customers.read");
        var query = db.Customers.AsNoTracking().AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(x =>
                x.FullName.Contains(s)
                || x.Phone.Contains(s)
                || x.CustomerNumber.Contains(s)
                || x.City.Contains(s)
            );
        }
        if (Enum.TryParse<AccountStatus>(q.Status, true, out var st))
            query = query.Where(x => x.Status == st);
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new(rows.Select(Map).ToList(), Page(q), Size(q), count);
    }

    public async Task<CustomerDto> GetCustomerAsync(
        Guid id,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "customers.read");
        var x =
            await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(x.FinancerId, actor);
        return Map(x);
    }

    public async Task<CustomerDto> CreateCustomerAsync(
        CreateCustomerRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "customers.manage");
        var tenant =
            r.FinancerId ?? actor.FinancerId ?? throw new DomainException("Financer is required.");
        RequireTenant(tenant, actor);
        if (
            !await db.Financers.AnyAsync(
                x => x.Id == tenant && x.Status == AccountStatus.Active,
                ct
            )
        )
            throw new DomainException("Financer is not active.");
        if (await db.Customers.AnyAsync(x => x.FinancerId == tenant && x.Phone == r.Phone, ct))
            throw new DomainException("Customer phone already exists for this financer.", 409);
        var x = new Customer
        {
            FinancerId = tenant,
            CustomerNumber = NumberGenerator.New("CUS"),
            FullName = r.FullName.Trim(),
            DateOfBirth = r.DateOfBirth,
            Gender = r.Gender,
            Phone = r.Phone.Trim(),
            Email = r.Email?.Trim().ToLowerInvariant(),
            AddressLine1 = r.AddressLine1.Trim(),
            AddressLine2 = r.AddressLine2,
            City = r.City.Trim(),
            State = r.State.Trim(),
            PostalCode = r.PostalCode.Trim(),
            AadhaarEncrypted = Security.Protect(r.Aadhaar, DataKey),
            PanEncrypted = Security.Protect(r.Pan, DataKey),
            CreatedBy = actor.UserId,
        };
        db.Customers.Add(x);
        Audit(
            actor,
            "Customer.Created",
            nameof(Customer),
            x.Id,
            null,
            new
            {
                x.CustomerNumber,
                x.FullName,
                x.Phone,
            }
        );
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    public async Task<CustomerDto> UpdateCustomerAsync(
        Guid id,
        UpdateCustomerRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "customers.manage");
        var x =
            await db.Customers.FindAsync([id], ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(x.FinancerId, actor);
        var before = new
        {
            x.FullName,
            x.Phone,
            x.Status,
        };
        x.FullName = r.FullName.Trim();
        x.DateOfBirth = r.DateOfBirth;
        x.Gender = r.Gender;
        x.Phone = r.Phone.Trim();
        x.Email = r.Email?.Trim().ToLowerInvariant();
        x.AddressLine1 = r.AddressLine1.Trim();
        x.AddressLine2 = r.AddressLine2;
        x.City = r.City.Trim();
        x.State = r.State.Trim();
        x.PostalCode = r.PostalCode.Trim();
        x.Status = r.Status;
        x.UpdatedAt = DateTimeOffset.UtcNow;
        x.UpdatedBy = actor.UserId;
        Audit(
            actor,
            "Customer.Updated",
            nameof(Customer),
            id,
            before,
            new
            {
                x.FullName,
                x.Phone,
                x.Status,
            }
        );
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    public async Task AddCustomerNoteAsync(
        Guid id,
        AddNoteRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "customers.manage");
        if (string.IsNullOrWhiteSpace(r.Text))
            throw new DomainException("Note text is required.");
        var x =
            await db.Customers.FindAsync([id], ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(x.FinancerId, actor);
        db.CustomerNotes.Add(
            new CustomerNote
            {
                CustomerId = id,
                Text = r.Text.Trim(),
                CreatedBy = actor.UserId,
            }
        );
        Audit(
            actor,
            "Customer.NoteAdded",
            nameof(Customer),
            id,
            null,
            new { Length = r.Text.Length }
        );
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteCustomerAsync(Guid id, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "customers.manage");
        var customer =
            await db.Customers.FindAsync([id], ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(customer.FinancerId, actor);
        if (
            await db.Loans.AnyAsync(
                x =>
                    x.CustomerId == id
                    && (x.Status == LoanStatus.Active || x.Status == LoanStatus.Overdue),
                ct
            )
        )
            throw new DomainException("A customer with an active loan cannot be deleted.", 409);
        customer.IsDeleted = true;
        customer.DeletedAt = DateTimeOffset.UtcNow;
        customer.DeletedBy = actor.UserId;
        Audit(actor, "Customer.Deleted", nameof(Customer), id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<KycRecord> SubmitKycAsync(
        KycSubmissionRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "customers.manage");
        var customer =
            await db.Customers.FindAsync([r.CustomerId], ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(customer.FinancerId, actor);
        var existing = await db.KycRecords.AnyAsync(
            x =>
                x.CustomerId == r.CustomerId
                && x.IdentityType == r.IdentityType
                && x.Status == VerificationStatus.Submitted,
            ct
        );
        if (existing)
            throw new DomainException("This identity already has a pending submission.", 409);
        var x = new KycRecord
        {
            CustomerId = r.CustomerId,
            IdentityType = r.IdentityType.Trim(),
            IdentityNumberEncrypted = Security.Protect(r.IdentityNumber, DataKey),
            DeclaredName = r.DeclaredName.Trim(),
            DeclaredDateOfBirth = r.DeclaredDateOfBirth,
            CreatedBy = actor.UserId,
        };
        customer.KycStatus = VerificationStatus.Submitted;
        db.KycRecords.Add(x);
        Audit(
            actor,
            "Kyc.Submitted",
            nameof(KycRecord),
            x.Id,
            null,
            new { x.CustomerId, x.IdentityType }
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<KycRecord> DecideKycAsync(
        Guid id,
        KycDecisionRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "kyc.verify");
        if (
            r.Status
            is not (
                VerificationStatus.Verified
                or VerificationStatus.Rejected
                or VerificationStatus.NeedsInformation
            )
        )
            throw new DomainException("Invalid KYC decision.");
        var x =
            await db.KycRecords.Include(x => x.Customer).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("KYC record not found.", 404);
        RequireTenant(x.Customer.FinancerId, actor);
        x.Status = r.Status;
        x.Notes = r.Notes;
        x.VerifiedBy = actor.UserId;
        x.VerifiedAt = DateTimeOffset.UtcNow;
        x.Customer.KycStatus = r.Status;
        Audit(actor, "Kyc.Decided", nameof(KycRecord), id, null, new { x.Status, x.Notes });
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<object> GetKycAsync(PageQuery q, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "customers.read");
        var query = db.KycRecords.AsNoTracking().Include(x => x.Customer).AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.Customer.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.Customer.FinancerId == q.FinancerId);
        if (Enum.TryParse<VerificationStatus>(q.Status, true, out var st))
            query = query.Where(x => x.Status == st);
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .Select(x => new
            {
                x.Id,
                x.CustomerId,
                Customer = x.Customer.FullName,
                x.IdentityType,
                x.Status,
                x.VerifiedBy,
                x.VerifiedAt,
                x.Notes,
                x.CreatedAt,
            })
            .ToListAsync(ct);
        return new PagedResult<object>(rows.Cast<object>().ToList(), Page(q), Size(q), count);
    }

    public async Task<IReadOnlyList<LoanProductDto>> GetProductsAsync(
        bool includeInactive,
        CancellationToken ct
    ) =>
        await db
            .LoanProducts.AsNoTracking()
            .Where(x => includeInactive || x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new LoanProductDto(
                x.Id,
                x.Code,
                x.Name,
                x.MinimumPrincipal,
                x.MaximumPrincipal,
                x.MinimumTenureMonths,
                x.MaximumTenureMonths,
                x.AnnualInterestRate,
                x.InterestMethod,
                x.RepaymentFrequency,
                x.ProcessingFeePercentage,
                x.LateFeePercentage,
                x.MaximumFoirPercentage,
                x.IsActive
            ))
            .ToListAsync(ct);

    public async Task<LoanProductDto> SaveProductAsync(
        Guid? id,
        LoanProductRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "products.manage");
        LoanProduct x;
        if (id.HasValue)
        {
            x =
                await db.LoanProducts.FindAsync([id.Value], ct)
                ?? throw new DomainException("Loan product not found.", 404);
        }
        else
        {
            x = new LoanProduct { CreatedBy = actor.UserId };
            db.LoanProducts.Add(x);
        }
        if (await db.LoanProducts.AnyAsync(p => p.Code == r.Code && p.Id != x.Id, ct))
            throw new DomainException("Product code already exists.", 409);
        x.Code = r.Code.Trim().ToUpperInvariant();
        x.Name = r.Name.Trim();
        x.MinimumPrincipal = r.MinimumPrincipal;
        x.MaximumPrincipal = r.MaximumPrincipal;
        x.MinimumTenureMonths = r.MinimumTenureMonths;
        x.MaximumTenureMonths = r.MaximumTenureMonths;
        x.AnnualInterestRate = r.AnnualInterestRate;
        x.InterestMethod = r.InterestMethod;
        x.RepaymentFrequency = r.RepaymentFrequency;
        x.ProcessingFeePercentage = r.ProcessingFeePercentage;
        x.LateFeePercentage = r.LateFeePercentage;
        x.MinimumAge = r.MinimumAge;
        x.MaximumAgeAtMaturity = r.MaximumAgeAtMaturity;
        x.MaximumFoirPercentage = r.MaximumFoirPercentage;
        x.IsActive = r.IsActive;
        x.UpdatedAt = DateTimeOffset.UtcNow;
        x.UpdatedBy = actor.UserId;
        Audit(
            actor,
            id.HasValue ? "LoanProduct.Updated" : "LoanProduct.Created",
            nameof(LoanProduct),
            x.Id,
            null,
            r
        );
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    public async Task<EligibilityDto> CheckEligibilityAsync(
        EligibilityRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "loans.create");
        var customer =
            await db.Customers.FindAsync([r.CustomerId], ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(customer.FinancerId, actor);
        var p =
            await db.LoanProducts.FindAsync([r.LoanProductId], ct)
            ?? throw new DomainException("Loan product not found.", 404);
        if (!p.IsActive)
            throw new DomainException("Loan product is inactive.");
        var age =
            DateTime.UtcNow.Year
            - customer.DateOfBirth.Year
            - (
                DateOnly.FromDateTime(DateTime.UtcNow)
                < customer.DateOfBirth.AddYears(DateTime.UtcNow.Year - customer.DateOfBirth.Year)
                    ? 1
                    : 0
            );
        var payment = CalculateEmi(r.RequestedAmount, p.AnnualInterestRate, r.TenureMonths);
        var foir = Math.Round((r.MonthlyObligations + payment) / r.MonthlyIncome * 100, 2);
        var rules = new Dictionary<string, bool>
        {
            {
                "amount",
                r.RequestedAmount >= p.MinimumPrincipal && r.RequestedAmount <= p.MaximumPrincipal
            },
            {
                "tenure",
                r.TenureMonths >= p.MinimumTenureMonths && r.TenureMonths <= p.MaximumTenureMonths
            },
            { "age", age >= p.MinimumAge && age + r.TenureMonths / 12 <= p.MaximumAgeAtMaturity },
            { "kyc", customer.KycStatus == VerificationStatus.Verified },
            { "foir", foir <= p.MaximumFoirPercentage },
        };
        var passed = rules.Values.All(x => x);
        var capacity = Math.Max(
            0,
            r.MonthlyIncome * p.MaximumFoirPercentage / 100 - r.MonthlyObligations
        );
        var eligible = Math.Min(
            p.MaximumPrincipal,
            PresentValue(capacity, p.AnnualInterestRate, r.TenureMonths)
        );
        var x = new EligibilityCheck
        {
            CustomerId = r.CustomerId,
            LoanProductId = r.LoanProductId,
            RequestedAmount = r.RequestedAmount,
            TenureMonths = r.TenureMonths,
            MonthlyIncome = r.MonthlyIncome,
            MonthlyObligations = r.MonthlyObligations,
            FoirPercentage = foir,
            EligibleAmount = Math.Round(eligible, 2),
            Passed = passed,
            RuleResultsJson = JsonSerializer.Serialize(rules),
            CreatedBy = actor.UserId,
        };
        db.EligibilityChecks.Add(x);
        Audit(
            actor,
            "Eligibility.Checked",
            nameof(EligibilityCheck),
            x.Id,
            null,
            new
            {
                x.Passed,
                x.EligibleAmount,
                x.FoirPercentage,
            }
        );
        await db.SaveChangesAsync(ct);
        return new(x.Id, passed, x.EligibleAmount, foir, rules);
    }

    public async Task<PagedResult<LoanApplicationDto>> GetApplicationsAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "loans.read");
        var query = db.LoanApplications.AsNoTracking().AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(x =>
                x.ApplicationNumber.Contains(s) || x.Customer.FullName.Contains(s)
            );
        }
        if (Enum.TryParse<LoanApplicationStatus>(q.Status, true, out var st))
            query = query.Where(x => x.Status == st);
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new(rows.Select(Map).ToList(), Page(q), Size(q), count);
    }

    public async Task<LoanApplicationDto> CreateApplicationAsync(
        LoanApplicationRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "loans.create");
        var customer =
            await db.Customers.FindAsync([r.CustomerId], ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(customer.FinancerId, actor);
        var product =
            await db.LoanProducts.FindAsync([r.LoanProductId], ct)
            ?? throw new DomainException("Loan product not found.", 404);
        if (
            !product.IsActive
            || r.RequestedPrincipal < product.MinimumPrincipal
            || r.RequestedPrincipal > product.MaximumPrincipal
            || r.RequestedTenureMonths < product.MinimumTenureMonths
            || r.RequestedTenureMonths > product.MaximumTenureMonths
        )
            throw new DomainException("Requested terms are outside the product limits.");
        var x = new LoanApplication
        {
            FinancerId = customer.FinancerId,
            CustomerId = customer.Id,
            LoanProductId = product.Id,
            ApplicationNumber = NumberGenerator.New("APP"),
            RequestedPrincipal = r.RequestedPrincipal,
            // Until final approval this column holds the rate proposed by the financer.
            // Approval may retain or replace it with the sanctioned rate.
            ApprovedAnnualRate = r.RequestedAnnualRate,
            RequestedTenureMonths = r.RequestedTenureMonths,
            Purpose = r.Purpose.Trim(),
            MonthlyIncome = r.MonthlyIncome,
            MonthlyObligations = r.MonthlyObligations,
            CreatedBy = actor.UserId,
        };
        db.LoanApplications.Add(x);
        var financerName = await db.Financers.AsNoTracking()
            .Where(financer => financer.Id == customer.FinancerId)
            .Select(financer => financer.DisplayName)
            .SingleAsync(ct);
        await NotifyPlatformAdminsAsync(
            "New loan application",
            $"{financerName} created a loan application for {customer.FullName} for {r.RequestedPrincipal:N2}.",
            "LoanCreated",
            nameof(LoanApplication),
            x.Id,
            actor,
            ct
        );
        Audit(
            actor,
            "LoanApplication.Created",
            nameof(LoanApplication),
            x.Id,
            null,
            new
            {
                x.ApplicationNumber,
                x.CustomerId,
                x.RequestedPrincipal,
            }
        );
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    public async Task<LoanApplicationDto> GetApplicationAsync(
        Guid id,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "loans.read");
        var x =
            await db.LoanApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Loan application not found.", 404);
        RequireTenant(x.FinancerId, actor);
        return Map(x);
    }

    public async Task<object> GetApplicationHistoryAsync(
        Guid id,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        await GetApplicationAsync(id, actor, ct);
        return await db
            .LoanStatusHistory.AsNoTracking()
            .Where(x => x.LoanApplicationId == id)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<LoanApplicationDto> TransitionApplicationAsync(
        Guid id,
        string action,
        object? body,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        var x =
            await db
                .LoanApplications.Include(x => x.Customer)
                .Include(x => x.LoanProduct)
                .SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Loan application not found.", 404);
        RequireTenant(x.FinancerId, actor);
        var from = x.Status;
        string? reason = null;
        switch (action.ToLowerInvariant())
        {
            case "submit":
                Require(actor, "loans.create");
                Ensure(x, LoanApplicationStatus.Draft);
                x.Status = LoanApplicationStatus.Submitted;
                AddHistory(x, from, x.Status, "Submitted", actor);
                var eligibility = await CheckEligibilityAsync(
                    new(
                        x.CustomerId,
                        x.LoanProductId,
                        x.RequestedPrincipal,
                        x.RequestedTenureMonths,
                        x.MonthlyIncome,
                        x.MonthlyObligations
                    ),
                    actor,
                    ct
                );
                x.EligibilityCheckId = eligibility.Id;
                from = x.Status;
                x.Status = eligibility.Passed
                    ? (
                        x.Customer.KycStatus == VerificationStatus.Verified
                            ? LoanApplicationStatus.UnderVerification
                            : LoanApplicationStatus.KycPending
                    )
                    : LoanApplicationStatus.EligibilityFailed;
                reason = eligibility.Passed ? "Eligibility passed" : "Eligibility failed";
                break;
            case "verify":
                Require(actor, "loans.verify");
                if (
                    x.Status == LoanApplicationStatus.KycPending
                    && x.Customer.KycStatus == VerificationStatus.Verified
                )
                    x.Status = LoanApplicationStatus.UnderVerification;
                Ensure(x, LoanApplicationStatus.UnderVerification);
                x.Status = LoanApplicationStatus.Verified;
                x.VerifiedBy = actor.UserId;
                reason = "Verification completed";
                break;
            case "approve":
                Require(actor, "loans.approve");
                Ensure(x, LoanApplicationStatus.Verified);
                if (x.VerifiedBy == actor.UserId)
                    throw new DomainException(
                        "The verifier cannot approve the same application.",
                        409
                    );
                var decision =
                    body as LoanDecisionRequest
                    ?? throw new DomainException("Approval terms are required.");
                if (
                    decision.ApprovedPrincipal <= 0
                    || decision.ApprovedPrincipal > x.RequestedPrincipal
                    || decision.ApprovedTenureMonths <= 0
                )
                    throw new DomainException("Invalid approval terms.");
                x.ApprovedPrincipal = decision.ApprovedPrincipal;
                x.ApprovedAnnualRate = decision.ApprovedAnnualRate;
                x.ApprovedTenureMonths = decision.ApprovedTenureMonths;
                x.DecisionNotes = decision.Notes;
                x.ApprovedBy = actor.UserId;
                x.Status = LoanApplicationStatus.Approved;
                reason = decision.Notes;
                break;
            case "reject":
                Require(actor, "loans.approve");
                if (
                    x.Status
                    is LoanApplicationStatus.Rejected
                        or LoanApplicationStatus.Disbursed
                        or LoanApplicationStatus.Cancelled
                )
                    throw new DomainException("Application is in a terminal state.", 409);
                var rejection =
                    body as RejectLoanRequest
                    ?? throw new DomainException("Rejection details are required.");
                if (
                    string.IsNullOrWhiteSpace(rejection.ReasonCode)
                    || string.IsNullOrWhiteSpace(rejection.Notes)
                )
                    throw new DomainException("Rejection reason and notes are required.");
                x.RejectionCode = rejection.ReasonCode;
                x.DecisionNotes = rejection.Notes;
                x.Status = LoanApplicationStatus.Rejected;
                reason = rejection.Notes;
                break;
            case "disburse":
                Require(actor, "loans.disburse");
                Ensure(x, LoanApplicationStatus.Approved);
                if (x.ApprovedBy == actor.UserId)
                    throw new DomainException(
                        "The approver cannot disburse the same application.",
                        409
                    );
                var dis =
                    body as DisbursementRequest
                    ?? throw new DomainException("Disbursement details are required.");
                if (
                    dis.Amount != x.ApprovedPrincipal
                    || string.IsNullOrWhiteSpace(dis.BankReference)
                )
                    throw new DomainException(
                        "Disbursement must equal the approved principal and include a bank reference."
                    );
                await DisburseAsync(x, dis, actor, ct);
                x.DisbursedBy = actor.UserId;
                x.Status = LoanApplicationStatus.Disbursed;
                reason = $"Disbursed: {dis.BankReference}";
                break;
            default:
                throw new DomainException("Unsupported workflow action.", 404);
        }
        AddHistory(x, from, x.Status, reason, actor);
        x.UpdatedAt = DateTimeOffset.UtcNow;
        x.UpdatedBy = actor.UserId;
        Audit(
            actor,
            $"LoanApplication.{action}",
            nameof(LoanApplication),
            id,
            new { Status = from },
            new { x.Status, reason }
        );
        await db.SaveChangesAsync(ct);
        return Map(x);
    }

    private async Task<Loan> DisburseAsync(
        LoanApplication app,
        DisbursementRequest r,
        CurrentUser actor,
        CancellationToken ct,
        DirectLoanRequest? direct = null
    )
    {
        if (await db.Loans.AnyAsync(x => x.LoanApplicationId == app.Id, ct))
            throw new DomainException("Application has already been disbursed.", 409);
        var tenure = app.ApprovedTenureMonths!.Value;
        var rate = app.ApprovedAnnualRate!.Value;
        var principal = app.ApprovedPrincipal!.Value;
        var loan = new Loan
        {
            FinancerId = app.FinancerId,
            CustomerId = app.CustomerId,
            LoanApplicationId = app.Id,
            LoanProductId = app.LoanProductId,
            LoanProduct = app.LoanProduct,
            LoanNumber = NumberGenerator.New("LN"),
            Principal = principal,
            AnnualInterestRate = rate,
            InterestMethod = app.LoanProduct.InterestMethod,
            TenureMonths = tenure,
            DurationValue = tenure,
            DurationUnit = LoanDurationUnit.Months,
            InterestRate = rate,
            InterestRateBasis = InterestRateBasis.PerAnnum,
            InterestCollectionFrequency = InterestCollectionFrequency.Monthly,
            DisbursementDate = r.DisbursementDate,
            MaturityDate = r.DisbursementDate.AddMonths(tenure),
            PrincipalOutstanding = principal,
            Status = LoanStatus.Active,
            CreatedBy = actor.UserId,
        };
        if (direct is not null)
        {
            ConfigureInterestOnlyLoan(loan, direct, actor.UserId);
            db.Loans.Add(loan);
            db.Transactions.Add(new FinancialTransaction
            {
                FinancerId = app.FinancerId, CustomerId = app.CustomerId, LoanId = loan.Id,
                TransactionNumber = NumberGenerator.New("TXN"), Type = TransactionType.Disbursement,
                Amount = principal,
                TransactionAt = new DateTimeOffset(r.DisbursementDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                ExternalReference = r.BankReference, CreatedBy = actor.UserId,
            });
            return loan;
        }
        var balance = principal;
        var emi = CalculateEmi(principal, rate, tenure);
        for (var i = 1; i <= tenure; i++)
        {
            var interest =
                app.LoanProduct.InterestMethod == InterestMethod.FlatRate
                    ? principal * rate / 1200
                    : balance * rate / 1200;
            var principalDue =
                i == tenure ? balance : Math.Min(balance, Math.Round(emi - interest, 2));
            loan.Schedules.Add(
                new PaymentSchedule
                {
                    InstallmentNumber = i,
                    PeriodStart = r.DisbursementDate.AddMonths(i - 1),
                    PeriodEnd = r.DisbursementDate.AddMonths(i),
                    InterestDays = r.DisbursementDate.AddMonths(i).DayNumber - r.DisbursementDate.AddMonths(i - 1).DayNumber,
                    DueDate = r.DisbursementDate.AddMonths(i),
                    OpeningPrincipal = balance,
                    PrincipalDue = principalDue,
                    InterestDue = Math.Round(interest, 2),
                    Status = ScheduleStatus.Upcoming,
                    CreatedBy = actor.UserId,
                }
            );
            balance -= principalDue;
        }
        loan.InterestOutstanding = loan.Schedules.Sum(x => x.InterestDue);
        db.Loans.Add(loan);
        db.Transactions.Add(
            new FinancialTransaction
            {
                FinancerId = app.FinancerId,
                CustomerId = app.CustomerId,
                LoanId = loan.Id,
                TransactionNumber = NumberGenerator.New("TXN"),
                Type = TransactionType.Disbursement,
                Amount = principal,
                TransactionAt = new DateTimeOffset(
                    r.DisbursementDate.ToDateTime(TimeOnly.MinValue),
                    TimeSpan.Zero
                ),
                ExternalReference = r.BankReference,
                CreatedBy = actor.UserId,
            }
        );
        await Task.CompletedTask;
        return loan;
    }

    public async Task<LoanDto> CreateDirectLoanAsync(
        DirectLoanRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "loans.create");
        var customer = await db.Customers.FindAsync([r.CustomerId], ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(customer.FinancerId, actor);
        var product = await db.LoanProducts.FindAsync([r.LoanProductId], ct)
            ?? throw new DomainException("Loan product not found.", 404);
        if (!product.IsActive || r.Principal < product.MinimumPrincipal || r.Principal > product.MaximumPrincipal)
            throw new DomainException("Requested principal is outside the product limits.");
        if (r.TenureMonths < product.MinimumTenureMonths || r.TenureMonths > product.MaximumTenureMonths)
            throw new DomainException("Requested tenure is outside the product limits.");

        var app = new LoanApplication
        {
            FinancerId = customer.FinancerId,
            CustomerId = customer.Id,
            Customer = customer,
            LoanProductId = product.Id,
            LoanProduct = product,
            ApplicationNumber = NumberGenerator.New("APP"),
            RequestedPrincipal = r.Principal,
            RequestedTenureMonths = r.TenureMonths,
            Purpose = "Financer-sanctioned customer loan",
            MonthlyIncome = r.Principal,
            ApprovedPrincipal = r.Principal,
            ApprovedAnnualRate = r.AnnualInterestRate,
            ApprovedTenureMonths = r.TenureMonths,
            Status = LoanApplicationStatus.Disbursed,
            VerifiedBy = actor.UserId,
            ApprovedBy = actor.UserId,
            DisbursedBy = actor.UserId,
            DecisionNotes = "Eligibility assessed and loan sanctioned directly by financer",
            CreatedBy = actor.UserId,
        };
        db.LoanApplications.Add(app);
        var loan = await DisburseAsync(app, new(r.Principal, r.StartDate, PaymentMode.Other, $"DIRECT-{app.ApplicationNumber}"), actor, ct, r);
        loan.AdminCollectionMonitoring = r.AdminCollectionMonitoring;
        var financerName = await db.Financers.AsNoTracking()
            .Where(financer => financer.Id == customer.FinancerId)
            .Select(financer => financer.DisplayName)
            .SingleAsync(ct);
        await NotifyPlatformAdminsAsync(
            "New loan created",
            $"{financerName} created loan {loan.LoanNumber} for {customer.FullName} for {r.Principal:N2}.",
            "LoanCreated",
            nameof(Loan),
            loan.Id,
            actor,
            ct
        );
        Audit(actor, "Loan.DirectCreated", nameof(Loan), loan.Id, null, new { loan.LoanNumber, loan.CustomerId, loan.Principal, loan.AnnualInterestRate });
        await db.SaveChangesAsync(ct);
        return Map(loan);
    }

    public async Task<PagedResult<LoanDto>> GetLoansAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "loans.read");
        var query = db.Loans.AsNoTracking().Include(x => x.LoanProduct).AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(x => x.LoanNumber.Contains(s) || x.Customer.FullName.Contains(s));
        }
        if (Enum.TryParse<LoanStatus>(q.Status, true, out var st))
            query = query.Where(x => x.Status == st);
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new(rows.Select(Map).ToList(), Page(q), Size(q), count);
    }

    public async Task<LoanDto> GetLoanAsync(Guid id, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "loans.read");
        var x =
            await db.Loans.AsNoTracking().Include(x => x.LoanProduct).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Loan not found.", 404);
        RequireTenant(x.FinancerId, actor);
        return Map(x);
    }

    public async Task<IReadOnlyList<ScheduleDto>> GetScheduleAsync(
        Guid id,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        var loan =
            await db.Loans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Loan not found.", 404);
        RequireTenant(loan.FinancerId, actor);
        return await db
            .PaymentSchedules.AsNoTracking()
            .Where(x => x.LoanId == id)
            .OrderBy(x => x.InstallmentNumber)
            .Select(x => new ScheduleDto(
                x.Id,
                x.InstallmentNumber,
                x.DueDate,
                x.OpeningPrincipal,
                x.PrincipalDue,
                x.InterestDue,
                x.FeesDue,
                x.AmountPaid,
                x.Status,
                x.PeriodStart,
                x.PeriodEnd,
                x.InterestDays,
                x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid
            ))
            .ToListAsync(ct);
    }

    public async Task<object> GetSchedulesAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "payments.read");
        var query = db
            .PaymentSchedules.AsNoTracking()
            .Include(x => x.Loan)
                .ThenInclude(x => x.Customer)
            .AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.Loan.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.Loan.FinancerId == q.FinancerId);
        if (q.From.HasValue)
            query = query.Where(x => x.DueDate >= q.From);
        if (q.To.HasValue)
            query = query.Where(x => x.DueDate <= q.To);
        if (Enum.TryParse<ScheduleStatus>(q.Status, true, out var status))
            query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var search = q.Search.Trim();
            query = query.Where(x =>
                x.Loan.LoanNumber.Contains(search) || x.Loan.Customer.FullName.Contains(search)
            );
        }
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderBy(x => x.DueDate)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .Select(x => new
            {
                x.Id,
                x.LoanId,
                x.Loan.LoanNumber,
                x.Loan.CustomerId,
                CustomerName = x.Loan.Customer.FullName,
                x.InstallmentNumber,
                x.PeriodStart,
                x.PeriodEnd,
                x.InterestDays,
                x.DueDate,
                x.OpeningPrincipal,
                x.PrincipalDue,
                x.InterestDue,
                x.FeesDue,
                TotalDue = x.PrincipalDue + x.InterestDue + x.FeesDue,
                x.AmountPaid,
                Balance = x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid,
                Method = db.PaymentAllocations
                    .Where(a => a.PaymentScheduleId == x.Id && a.Payment.Status == PaymentStatus.Completed)
                    .OrderByDescending(a => a.Payment.ReceivedAt)
                    .Select(a => (PaymentMode?)a.Payment.Mode)
                    .FirstOrDefault(),
                PaymentDate = db.PaymentAllocations
                    .Where(a => a.PaymentScheduleId == x.Id && a.Payment.Status == PaymentStatus.Completed)
                    .OrderByDescending(a => a.Payment.ReceivedAt)
                    .Select(a => (DateTimeOffset?)a.Payment.ReceivedAt)
                    .FirstOrDefault(),
                x.Status,
            })
            .ToListAsync(ct);
        return new PagedResult<object>(rows.Cast<object>().ToList(), Page(q), Size(q), count);
    }

    public async Task<PagedResult<PaymentDto>> GetPaymentsAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "payments.read");
        var query = db.Payments.AsNoTracking().AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (q.From.HasValue)
            query = query.Where(x =>
                x.ReceivedAt
                >= new DateTimeOffset(q.From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            );
        if (q.To.HasValue)
            query = query.Where(x =>
                x.ReceivedAt
                < new DateTimeOffset(
                    q.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),
                    TimeSpan.Zero
                )
            );
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(x =>
                x.PaymentNumber.Contains(s)
                || (x.ExternalReference != null && x.ExternalReference.Contains(s))
                || x.Loan.LoanNumber.Contains(s)
            );
        }
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.ReceivedAt)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new(rows.Select(Map).ToList(), Page(q), Size(q), count);
    }

    public async Task<PaymentDto> GetPaymentAsync(Guid id, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "payments.read");
        var x =
            await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Payment not found.", 404);
        RequireTenant(x.FinancerId, actor);
        return Map(x);
    }

    public async Task<SettlementQuoteDto> GetSettlementQuoteAsync(
        Guid loanId,
        DateOnly settlementDate,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "payments.read");
        var loan = await db.Loans.Include(x => x.Schedules).AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == loanId, ct)
            ?? throw new DomainException("Loan not found.", 404);
        RequireTenant(loan.FinancerId, actor);
        if (loan.Status is LoanStatus.Closed or LoanStatus.WrittenOff or LoanStatus.Cancelled)
            throw new DomainException("A settlement quote is not available for this loan status.", 409);
        return BuildSettlementQuote(loan, settlementDate);
    }

    public async Task<PaymentDto> RecordPaymentAsync(
        RecordPaymentRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "payments.record");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan =
            await db.Loans.Include(x => x.Schedules).SingleOrDefaultAsync(x => x.Id == r.LoanId, ct)
            ?? throw new DomainException("Loan not found.", 404);
        RequireTenant(loan.FinancerId, actor);
        if (loan.Status is LoanStatus.Closed or LoanStatus.WrittenOff or LoanStatus.Cancelled)
            throw new DomainException("Payments cannot be recorded for this loan status.", 409);
        var schedules = loan
            .Schedules.Where(x => x.Status != ScheduleStatus.Paid)
            .OrderBy(x => x.DueDate)
            .ThenBy(x => x.InstallmentNumber)
            .ToList();
        var scheduleOutstanding = schedules.Sum(x =>
            Math.Max(0, x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid)
        );
        if (r.PaymentType == LoanPaymentType.FullSettlement)
        {
            var settlementDate = DateOnly.FromDateTime(r.ReceivedAt.UtcDateTime);
            var quote = BuildSettlementQuote(loan, settlementDate);
            if (Math.Abs(r.Amount - quote.SettlementAmount) > 0.01m)
                throw new DomainException($"Settlement amount changed. The current quote is â‚¹{quote.SettlementAmount:N2}.", 409);

            foreach (var schedule in schedules)
            {
                var earnedInterest = EarnedInterestThrough(schedule, settlementDate);
                var interestAlreadyPaid = Math.Min(schedule.InterestDue, Math.Max(0, schedule.AmountPaid - schedule.FeesDue));
                schedule.InterestDue = Math.Max(interestAlreadyPaid, earnedInterest);
            }
            loan.InterestOutstanding = quote.AccruedInterest;
            scheduleOutstanding = schedules.Sum(x => Math.Max(0, x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid));
        }
        else if (r.PaymentType == LoanPaymentType.InterestOnly)
        {
            if (loan.FeesOutstanding > 0)
                throw new DomainException("Outstanding fees must be cleared with a regular payment before an interest-only payment.", 409);
            var interestAvailable = schedules.Sum(s => Math.Max(0, s.InterestDue - Math.Max(0, s.AmountPaid - s.FeesDue)));
            if (r.Amount > interestAvailable)
                throw new DomainException("Payment exceeds the outstanding interest amount.");
            scheduleOutstanding = interestAvailable;
        }
        if (r.Amount > scheduleOutstanding)
            throw new DomainException("Payment exceeds the total outstanding amount.");
        if (
            !string.IsNullOrWhiteSpace(r.ExternalReference)
            && await db.Payments.AnyAsync(
                x => x.FinancerId == loan.FinancerId && x.ExternalReference == r.ExternalReference,
                ct
            )
        )
            throw new DomainException("External payment reference already exists.", 409);
        var payment = new Payment
        {
            FinancerId = loan.FinancerId,
            LoanId = loan.Id,
            PaymentScheduleId = r.PaymentScheduleId,
            PaymentNumber = NumberGenerator.New("PAY"),
            Amount = r.Amount,
            ReceivedAt = r.ReceivedAt,
            Mode = r.Mode,
            ExternalReference = r.ExternalReference?.Trim(),
            Notes = r.Notes,
            CreatedBy = actor.UserId,
        };
        var remaining = r.Amount;
        if (r.PaymentScheduleId.HasValue)
        {
            var selected =
                schedules.SingleOrDefault(x => x.Id == r.PaymentScheduleId)
                ?? throw new DomainException(
                    "Schedule does not belong to the loan or is already paid."
                );
            schedules.Remove(selected);
            schedules.Insert(0, selected);
        }
        foreach (var s in schedules.Where(_ => remaining > 0))
        {
            var existing = s.AmountPaid;
            var feePaid = r.PaymentType == LoanPaymentType.InterestOnly ? 0 : Math.Min(remaining, Math.Max(0, s.FeesDue - existing));
            remaining -= feePaid;
            existing += feePaid;
            var interestPaid = Math.Min(
                remaining,
                Math.Max(0, s.InterestDue + s.FeesDue - existing)
            );
            remaining -= interestPaid;
            existing += interestPaid;
            var principalPaid = r.PaymentType == LoanPaymentType.InterestOnly ? 0 : Math.Min(
                remaining,
                Math.Max(0, s.PrincipalDue + s.InterestDue + s.FeesDue - existing)
            );
            remaining -= principalPaid;
            s.AmountPaid += feePaid + interestPaid + principalPaid;
            s.Status =
                s.AmountPaid >= s.PrincipalDue + s.InterestDue + s.FeesDue
                    ? ScheduleStatus.Paid
                    : ScheduleStatus.PartiallyPaid;
            if (s.Status == ScheduleStatus.Paid)
                s.PaidAt = r.ReceivedAt;
            payment.FeeAmount += feePaid;
            payment.InterestAmount += interestPaid;
            payment.PrincipalAmount += principalPaid;
            payment.Allocations.Add(
                new PaymentAllocation
                {
                    PaymentSchedule = s,
                    FeeAmount = feePaid,
                    InterestAmount = interestPaid,
                    PrincipalAmount = principalPaid,
                    CreatedBy = actor.UserId,
                }
            );
        }
        if (remaining > 0)
            throw new DomainException("Unable to allocate the full payment.");
        // Schedule allocations are the accounting source of truth. Rebuilding
        // the loan aggregates here also repairs older loans whose cached totals
        // drifted and incorrectly prevented an early installment payment.
        loan.FeesOutstanding = loan.Schedules.Sum(s =>
            Math.Max(0, s.FeesDue - s.AmountPaid)
        );
        loan.InterestOutstanding = loan.Schedules.Sum(s =>
            Math.Max(0, s.InterestDue - Math.Max(0, s.AmountPaid - s.FeesDue))
        );
        loan.PrincipalOutstanding = loan.Schedules.Sum(s =>
            Math.Max(
                0,
                s.PrincipalDue - Math.Max(0, s.AmountPaid - s.FeesDue - s.InterestDue)
            )
        );
        var collectionCase = await db.CollectionCases.Include(x => x.Activities).SingleOrDefaultAsync(x => x.LoanId == loan.Id, ct);
        if (collectionCase is not null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            collectionCase.DueAmount = loan.Schedules.Where(x => x.DueDate <= today && x.Status != ScheduleStatus.Paid)
                .Sum(x => x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid);
            collectionCase.OverdueAmount = loan.Schedules.Where(x => x.DueDate < today && x.Status != ScheduleStatus.Paid)
                .Sum(x => x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid);
            collectionCase.Status = collectionCase.DueAmount <= 0 ? CollectionStatus.Collected : CollectionStatus.PartiallyCollected;
            db.CollectionActivities.Add(new CollectionActivity
            {
                CollectionCaseId = collectionCase.Id,
                Type = collectionCase.DueAmount <= 0 ? "PaymentCollected" : "PartialPayment",
                Notes = $"Payment of ₹{r.Amount:N2} recorded; ₹{collectionCase.DueAmount:N2} remains due.",
                CreatedBy = actor.UserId,
            });
        }
        if (
            loan.PrincipalOutstanding == 0
            && loan.InterestOutstanding == 0
            && loan.FeesOutstanding == 0
        )
            loan.Status = LoanStatus.Closed;
        else if (r.PaymentType == LoanPaymentType.FullSettlement)
            throw new DomainException("The settlement payment did not clear the loan.", 409);
        db.Payments.Add(payment);
        db.Notifications.Add(
            new Notification
            {
                FinancerId = loan.FinancerId,
                UserId = actor.UserId,
                Title = $"Payment {payment.PaymentNumber} recorded",
                Message = $"Payment of ₹{payment.Amount:N2} was recorded for loan {loan.LoanNumber}.",
                Type = "Payments",
                Channel = NotificationChannel.InApp,
                EntityType = nameof(Payment),
                EntityId = payment.Id,
                SentAt = DateTimeOffset.UtcNow,
                CreatedBy = actor.UserId,
            }
        );
        db.Transactions.Add(
            new FinancialTransaction
            {
                FinancerId = loan.FinancerId,
                CustomerId = loan.CustomerId,
                LoanId = loan.Id,
                PaymentId = payment.Id,
                TransactionNumber = NumberGenerator.New("TXN"),
                Type = TransactionType.Payment,
                Amount = r.Amount,
                TransactionAt = r.ReceivedAt,
                ExternalReference = r.ExternalReference,
                CreatedBy = actor.UserId,
            }
        );
        Audit(
            actor,
            "Payment.Recorded",
            nameof(Payment),
            payment.Id,
            null,
            new
            {
                payment.PaymentNumber,
                payment.Amount,
                payment.PrincipalAmount,
                payment.InterestAmount,
                payment.FeeAmount,
            }
        );
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException concurrency)
        {
            // The reminder worker can transition a due schedule while a user is
            // recording it. Keep this payment's values, refresh only the
            // database originals used for optimistic concurrency, and retry
            // once inside the same transaction.
            foreach (var entry in concurrency.Entries)
            {
                if (entry.State == EntityState.Added)
                    continue;
                var databaseValues = await entry.GetDatabaseValuesAsync(ct);
                if (databaseValues is null)
                    throw new DomainException(
                        $"The {entry.Metadata.ClrType.Name} changed or was removed while recording the payment. Refresh the dues and try again.",
                        409
                    );
                entry.OriginalValues.SetValues(databaseValues);
            }
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Map(payment);
    }

    public async Task<PaymentDto> ReversePaymentAsync(
        Guid id,
        ReversePaymentRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "payments.reverse");
        if (string.IsNullOrWhiteSpace(r.Reason))
            throw new DomainException("Reversal reason is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var p =
            await db
                .Payments.Include(x => x.Loan)
                .Include(x => x.Allocations)
                    .ThenInclude(x => x.PaymentSchedule)
                .SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Payment not found.", 404);
        RequireTenant(p.FinancerId, actor);
        if (p.Status != PaymentStatus.Completed)
            throw new DomainException("Only completed payments can be reversed.", 409);
        foreach (var a in p.Allocations)
        {
            var s = a.PaymentSchedule;
            s.AmountPaid -= a.FeeAmount + a.InterestAmount + a.PrincipalAmount;
            s.PaidAt = null;
            s.Status =
                s.AmountPaid > 0
                    ? ScheduleStatus.PartiallyPaid
                    : (
                        s.DueDate < DateOnly.FromDateTime(DateTime.UtcNow)
                            ? ScheduleStatus.Overdue
                            : ScheduleStatus.Upcoming
                    );
        }
        p.Loan.FeesOutstanding += p.FeeAmount;
        p.Loan.InterestOutstanding += p.InterestAmount;
        p.Loan.PrincipalOutstanding += p.PrincipalAmount;
        p.Loan.Status = p.Loan.Schedules.Any(x => x.Status == ScheduleStatus.Overdue)
            ? LoanStatus.Overdue
            : LoanStatus.Active;
        p.Status = PaymentStatus.Reversed;
        p.Notes = $"{p.Notes}\nReversed: {r.Reason}".Trim();
        db.Notifications.Add(
            new Notification
            {
                FinancerId = p.FinancerId,
                UserId = actor.UserId,
                Title = $"Payment {p.PaymentNumber} reversed",
                Message = $"Payment of ₹{p.Amount:N2} was reversed. Reason: {r.Reason.Trim()}",
                Type = "Payments",
                Channel = NotificationChannel.InApp,
                EntityType = nameof(Payment),
                EntityId = p.Id,
                SentAt = DateTimeOffset.UtcNow,
                CreatedBy = actor.UserId,
            }
        );
        db.Transactions.Add(
            new FinancialTransaction
            {
                FinancerId = p.FinancerId,
                CustomerId = p.Loan.CustomerId,
                LoanId = p.LoanId,
                PaymentId = p.Id,
                TransactionNumber = NumberGenerator.New("TXN"),
                Type = TransactionType.Reversal,
                Amount = -p.Amount,
                TransactionAt = DateTimeOffset.UtcNow,
                CreatedBy = actor.UserId,
            }
        );
        Audit(actor, "Payment.Reversed", nameof(Payment), id, null, new { r.Reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(p);
    }

    public async Task<ScheduleDto> RescheduleAsync(
        Guid id,
        ReschedulePaymentRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "collections.manage");
        if (string.IsNullOrWhiteSpace(r.Reason))
            throw new DomainException("Reschedule reason is required.");
        var s =
            await db.PaymentSchedules.Include(x => x.Loan).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Schedule not found.", 404);
        RequireTenant(s.Loan.FinancerId, actor);
        if (s.Status == ScheduleStatus.Paid)
            throw new DomainException("Paid schedules cannot be rescheduled.", 409);
        if (r.NewDueDate <= DateOnly.FromDateTime(DateTime.UtcNow))
            throw new DomainException("New due date must be in the future.");
        var before = s.DueDate;
        s.OriginalDueDate ??= s.DueDate;
        s.DueDate = r.NewDueDate;
        s.RescheduleReason = r.Reason;
        s.Status = ScheduleStatus.Upcoming;
        Audit(
            actor,
            "Schedule.Rescheduled",
            nameof(PaymentSchedule),
            id,
            new { DueDate = before },
            new { s.DueDate, r.Reason }
        );
        await db.SaveChangesAsync(ct);
        return Map(s);
    }

    public async Task<object> GetTransactionsAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "payments.read");
        var query = db.Transactions.AsNoTracking().AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (q.From.HasValue)
            query = query.Where(x =>
                x.TransactionAt
                >= new DateTimeOffset(q.From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            );
        if (q.To.HasValue)
            query = query.Where(x =>
                x.TransactionAt
                < new DateTimeOffset(
                    q.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),
                    TimeSpan.Zero
                )
            );
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(x =>
                x.TransactionNumber.Contains(s)
                || (x.ExternalReference != null && x.ExternalReference.Contains(s))
            );
        }
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.TransactionAt)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new PagedResult<FinancialTransaction>(rows, Page(q), Size(q), count);
    }

    public async Task<object> ReconcileTransactionAsync(
        Guid id,
        string externalReference,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "payments.record");
        if (string.IsNullOrWhiteSpace(externalReference))
            throw new DomainException("External reconciliation reference is required.");
        var x =
            await db.Transactions.FindAsync([id], ct)
            ?? throw new DomainException("Transaction not found.", 404);
        RequireTenant(x.FinancerId, actor);
        if (x.IsReconciled)
            throw new DomainException("Transaction is already reconciled.", 409);
        x.IsReconciled = true;
        x.ReconciledAt = DateTimeOffset.UtcNow;
        x.ReconciledBy = actor.UserId;
        x.ExternalReference = externalReference.Trim();
        Audit(
            actor,
            "Transaction.Reconciled",
            nameof(FinancialTransaction),
            id,
            null,
            new { x.ExternalReference, x.ReconciledAt }
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<object> GetCustomerLedgerAsync(
        Guid customerId,
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "customers.read");
        var customer =
            await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct)
            ?? throw new DomainException("Customer not found.", 404);
        RequireTenant(customer.FinancerId, actor);
        var query = db.Transactions.AsNoTracking().Where(x => x.CustomerId == customerId);
        if (q.From.HasValue)
            query = query.Where(x =>
                x.TransactionAt
                >= new DateTimeOffset(q.From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            );
        if (q.To.HasValue)
            query = query.Where(x =>
                x.TransactionAt
                < new DateTimeOffset(
                    q.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),
                    TimeSpan.Zero
                )
            );
        var rows = await query.OrderBy(x => x.TransactionAt).ToListAsync(ct);
        decimal balance = 0;
        var ledger = rows.Select(x => new
            {
                x.Id,
                x.TransactionNumber,
                x.TransactionAt,
                x.Type,
                Debit = x.Type == TransactionType.Disbursement ? x.Amount : 0,
                Credit = x.Type != TransactionType.Disbursement ? x.Amount : 0,
                Balance = balance += x.Type == TransactionType.Disbursement ? x.Amount : -x.Amount,
                x.ExternalReference,
            })
            .ToList();
        return new { customer = Map(customer), entries = ledger };
    }

    public async Task<object> GetCollectionsAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "collections.read");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var reminderSetting = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Scope == "Platform" && x.Key == "CollectionReminderDaysBefore", ct);
        var reminderDays = reminderSetting is not null && int.TryParse(reminderSetting.Value, out var configuredReminderDays)
            ? Math.Clamp(configuredReminderDays, 0, 30) : 1;
        var queueThrough = today.AddDays(reminderDays);
        var query = db
            .Loans.AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Schedules)
            .Where(x => x.Status == LoanStatus.Active || x.Status == LoanStatus.Overdue);
        if (IsPlatform(actor))
            query = query.Where(x => x.AdminCollectionMonitoring);
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        var loans = await query.ToListAsync(ct);
        var loanIds = loans.Select(x => x.Id).ToList();
        var cases = await db.CollectionCases.AsNoTracking()
            .Include(x => x.Activities)
            .Where(x => loanIds.Contains(x.LoanId))
            .ToDictionaryAsync(x => x.LoanId, ct);
        var agentIds = cases.Values.Where(x => x.AssignedTo.HasValue).Select(x => x.AssignedTo!.Value).Distinct().ToList();
        var agentNames = await db.Users.AsNoTracking().Where(x => agentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => (x.FirstName + " " + x.LastName).Trim(), ct);
        var financerIds = loans.Select(x => x.FinancerId).Distinct().ToList();
        var financerNames = await db.Financers.AsNoTracking().Where(x => financerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var rows = loans
            .Select(x =>
            {
                cases.TryGetValue(x.Id, out var collectionCase);
                var dueSchedule = x.Schedules.Where(s => s.DueDate <= queueThrough && s.Status != ScheduleStatus.Paid)
                    .OrderBy(s => s.DueDate).ThenBy(s => s.InstallmentNumber).FirstOrDefault();
                return new
                {
                x.Id,
                x.LoanNumber,
                x.CustomerId,
                Customer = x.Customer.FullName,
                CustomerPhone = x.Customer.Phone,
                x.FinancerId,
                Financer = financerNames.GetValueOrDefault(x.FinancerId),
                PaymentScheduleId = dueSchedule?.Id,
                DueDate = x
                    .Schedules.Where(s => s.DueDate <= queueThrough && s.Status != ScheduleStatus.Paid)
                    .Select(s => (DateOnly?)s.DueDate)
                    .Min(),
                Due = x
                    .Schedules.Where(s => s.DueDate <= queueThrough && s.Status != ScheduleStatus.Paid)
                    .Sum(s => s.PrincipalDue + s.InterestDue + s.FeesDue - s.AmountPaid),
                DueNow = x
                    .Schedules.Where(s => s.DueDate <= today && s.Status != ScheduleStatus.Paid)
                    .Sum(s => s.PrincipalDue + s.InterestDue + s.FeesDue - s.AmountPaid),
                NextDue = dueSchedule == null
                    ? 0
                    : dueSchedule.PrincipalDue + dueSchedule.InterestDue + dueSchedule.FeesDue - dueSchedule.AmountPaid,
                UpcomingDue = x
                    .Schedules.Where(s => s.DueDate > today && s.DueDate <= queueThrough && s.Status != ScheduleStatus.Paid)
                    .Sum(s => s.PrincipalDue + s.InterestDue + s.FeesDue - s.AmountPaid),
                QueueThrough = queueThrough,
                DaysPastDue = x
                    .Schedules.Where(s => s.DueDate < today && s.Status != ScheduleStatus.Paid)
                    .Select(s => today.DayNumber - s.DueDate.DayNumber)
                    .DefaultIfEmpty(0)
                    .Max(),
                DaysUntilDue = dueSchedule is not null && dueSchedule.DueDate > today ? dueSchedule.DueDate.DayNumber - today.DayNumber : 0,
                x.PrincipalOutstanding,
                x.InterestOutstanding,
                Status = x.Status,
                CaseId = collectionCase?.Id,
                CaseStatus = collectionCase?.Status.ToString(),
                AssignedTo = collectionCase?.AssignedTo,
                AssignedToName = collectionCase?.AssignedTo is Guid assigned ? agentNames.GetValueOrDefault(assigned) : null,
                PromiseToPayDate = collectionCase?.PromiseToPayDate,
                NextFollowUpDate = collectionCase?.NextFollowUpDate,
                LastContactAt = collectionCase?.LastContactAt,
                Activities = collectionCase?.Activities.OrderByDescending(a => a.OccurredAt).Select(a => new
                    { a.Id, a.Type, a.Notes, a.OccurredAt, a.CreatedBy }).ToList() ?? [],
                };
            })
            .Where(x => x.Due > 0)
            .OrderByDescending(x => x.DaysPastDue)
            .ToList();
        return new PagedResult<object>(
            rows.Skip((Page(q) - 1) * Size(q)).Take(Size(q)).Cast<object>().ToList(),
            Page(q),
            Size(q),
            rows.Count
        );
    }

    public async Task<object> AddCollectionActionAsync(
        Guid loanId,
        CollectionActionRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "collections.manage");
        var loan =
            await db
                .Loans.Include(x => x.Schedules)
                .Include(x => x.Customer)
                .SingleOrDefaultAsync(x => x.Id == loanId, ct)
            ?? throw new DomainException("Loan not found.", 404);
        RequireTenant(loan.FinancerId, actor);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var c = await db
            .CollectionCases.Include(x => x.Activities)
            .SingleOrDefaultAsync(x => x.LoanId == loanId, ct);
        if (c is null)
        {
            c = new CollectionCase
            {
                LoanId = loanId,
                DueAmount = loan
                    .Schedules.Where(x => x.DueDate <= today && x.Status != ScheduleStatus.Paid)
                    .Sum(x => x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid),
                OverdueAmount = loan
                    .Schedules.Where(x => x.DueDate < today && x.Status != ScheduleStatus.Paid)
                    .Sum(x => x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid),
                DaysPastDue = loan
                    .Schedules.Where(x => x.DueDate < today && x.Status != ScheduleStatus.Paid)
                    .Select(x => today.DayNumber - x.DueDate.DayNumber)
                    .DefaultIfEmpty()
                    .Max(),
                CreatedBy = actor.UserId,
            };
            db.CollectionCases.Add(c);
        }
        c.AssignedTo = r.AssignedTo ?? c.AssignedTo;
        c.PromiseToPayDate = r.PromiseToPayDate ?? c.PromiseToPayDate;
        c.NextFollowUpDate = r.NextFollowUpDate ?? c.NextFollowUpDate;
        c.Status = r.Status ?? c.Status;
        c.LastContactAt = DateTimeOffset.UtcNow;
        c.Activities.Add(
            new CollectionActivity
            {
                Type = r.Type,
                Notes = r.Notes,
                CreatedBy = actor.UserId,
            }
        );
        if (r.Type.Contains("reminder", StringComparison.OrdinalIgnoreCase))
        {
            var phone = loan.Customer.Phone;
            db.SmsDeliveries.Add(
                new SmsDelivery
                {
                    FinancerId = loan.FinancerId,
                    CustomerId = loan.CustomerId,
                    DestinationMasked = phone.Length > 4 ? $"***{phone[^4..]}" : "***",
                    MessageType = "PaymentReminder",
                    Status = "Queued",
                    CreditsUsed = 1,
                    CreatedBy = actor.UserId,
                }
            );
        }
        Audit(actor, "Collection.ActionAdded", nameof(CollectionCase), c.Id, null, r);
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<object> GetDashboardAsync(
        bool admin,
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "dashboard.read");
        var tenant = admin && IsPlatform(actor) ? q.FinancerId : actor.FinancerId;
        var customers = db
            .Customers.AsNoTracking()
            .Where(x => !tenant.HasValue || x.FinancerId == tenant);
        var loans = db.Loans.AsNoTracking().Where(x => !tenant.HasValue || x.FinancerId == tenant);
        var payments = db
            .Payments.AsNoTracking()
            .Where(x => !tenant.HasValue || x.FinancerId == tenant);
        var from = q.From ?? new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var loanStatusData = await loans
            .GroupBy(x => x.Status)
            .Select(x => new { status = x.Key, count = x.Count() })
            .ToListAsync(ct);
        var upcomingPayments = await db
            .PaymentSchedules.AsNoTracking()
            .Where(x =>
                (!tenant.HasValue || x.Loan.FinancerId == tenant)
                && x.DueDate >= today
                && x.Status != ScheduleStatus.Paid
            )
            .OrderBy(x => x.DueDate)
            .Take(10)
            .Select(x => new
            {
                x.Id,
                x.LoanId,
                x.Loan.LoanNumber,
                customer = x.Loan.Customer.FullName,
                x.DueDate,
                x.Status,
                amount = x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid,
            })
            .ToListAsync(ct);
        var trendStart = DateTimeOffset.UtcNow.AddMonths(-5);
        var completedPayments = await payments
            .Where(x => x.Status == PaymentStatus.Completed)
            .Select(x => new { x.ReceivedAt, x.Amount })
            .ToListAsync(ct);
        var collectionRows = completedPayments.Where(x => x.ReceivedAt >= trendStart).ToList();
        var monthlyCollections = Enumerable
            .Range(0, 6)
            .Select(offset =>
            {
                var month = DateTime.UtcNow.AddMonths(offset - 5);
                return new
                {
                    month = month.ToString("MMM yyyy"),
                    amount = collectionRows
                        .Where(x =>
                            x.ReceivedAt.Year == month.Year && x.ReceivedAt.Month == month.Month
                        )
                        .Sum(x => x.Amount),
                };
            })
            .ToList();
        return new
        {
            totalCustomers = await customers.CountAsync(ct),
            activeLoans = await loans.CountAsync(x => x.Status == LoanStatus.Active, ct),
            overdueLoans = await loans.CountAsync(x => x.Status == LoanStatus.Overdue, ct),
            totalPrincipal = await loans.SumAsync(x => (decimal?)x.Principal, ct) ?? 0,
            principalOutstanding = await loans.SumAsync(x => (decimal?)x.PrincipalOutstanding, ct)
                ?? 0,
            interestOutstanding = await loans.SumAsync(x => (decimal?)x.InterestOutstanding, ct)
                ?? 0,
            collections = completedPayments.Where(x => x.ReceivedAt >= start).Sum(x => x.Amount),
            loanStatusData,
            monthlyCollections,
            upcomingPayments,
            totalFinancers = admin && IsPlatform(actor)
                ? await db.Financers.CountAsync(ct)
                : (int?)null,
        };
    }

    public async Task<object> GetReportAsync(
        string name,
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "reports.read");
        return name.ToLowerInvariant() switch
        {
            "portfolio" or "loan" or "loans" => await GetLoansAsync(
                q with
                {
                    PageSize = Math.Min(q.PageSize, 100),
                },
                actor,
                ct
            ),
            "payments" => await GetPaymentsAsync(
                q with
                {
                    PageSize = Math.Min(q.PageSize, 100),
                },
                actor,
                ct
            ),
            "collections" or "overdue" => await GetCollectionsAsync(
                q with
                {
                    PageSize = Math.Min(q.PageSize, 100),
                },
                actor,
                ct
            ),
            "customer" or "customers" => await GetCustomersAsync(
                q with
                {
                    PageSize = Math.Min(q.PageSize, 100),
                },
                actor,
                ct
            ),
            "interest" or "interest-schedule" => await GetSchedulesAsync(
                q with
                {
                    PageSize = Math.Min(q.PageSize, 100),
                },
                actor,
                ct
            ),
            "service-charges" => await GetInvoicesAsync(q, actor, ct),
            "audit" => await GetAuditLogsAsync(q, actor, ct),
            _ => throw new DomainException("Unknown report.", 404),
        };
    }

    private async Task<object> GetInvoicesAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        var query = db.ServiceChargeInvoices.AsNoTracking().AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        return await query.OrderByDescending(x => x.PeriodStart).Take(100).ToListAsync(ct);
    }

    public async Task<object> GetNotificationsAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        var query = db
            .Notifications.AsNoTracking()
            .Where(x =>
                x.UserId == actor.UserId || (x.UserId == null && x.FinancerId == actor.FinancerId)
            );
        if (
            !string.IsNullOrWhiteSpace(q.Status)
            && q.Status.Equals("unread", StringComparison.OrdinalIgnoreCase)
        )
            query = query.Where(x => x.ReadAt == null);
        var count = await query.LongCountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new PagedResult<Notification>(items, Page(q), Size(q), count);
    }

    public async Task<Notification> CreateNotificationAsync(
        NotificationRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "notifications.manage");
        if (r.FinancerId.HasValue)
            RequireTenant(r.FinancerId.Value, actor);
        var x = new Notification
        {
            FinancerId = r.FinancerId,
            UserId = r.UserId,
            Title = r.Title.Trim(),
            Message = r.Message.Trim(),
            Type = r.Type,
            Channel = r.Channel,
            EntityType = r.EntityType,
            EntityId = r.EntityId,
            SentAt = DateTimeOffset.UtcNow,
            CreatedBy = actor.UserId,
        };
        db.Notifications.Add(x);
        if (r.Channel == NotificationChannel.Sms)
        {
            Customer? customer = null;
            if (
                r.EntityType?.Equals("Customer", StringComparison.OrdinalIgnoreCase) == true
                && r.EntityId.HasValue
            )
                customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == r.EntityId, ct);
            if (customer is null)
                throw new DomainException("An SMS notification requires a customer entity.");
            RequireTenant(customer.FinancerId, actor);
            var phone = customer.Phone;
            db.SmsDeliveries.Add(
                new SmsDelivery
                {
                    FinancerId = customer.FinancerId,
                    CustomerId = customer.Id,
                    NotificationId = x.Id,
                    DestinationMasked = phone.Length > 4 ? $"***{phone[^4..]}" : "***",
                    MessageType = r.Type,
                    Status = "Queued",
                    CreditsUsed = 1,
                    CreatedBy = actor.UserId,
                }
            );
        }
        Audit(
            actor,
            "Notification.Created",
            nameof(Notification),
            x.Id,
            null,
            new { x.Title, x.Channel }
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task MarkNotificationsReadAsync(Guid? id, CurrentUser actor, CancellationToken ct)
    {
        var query = db.Notifications.Where(x =>
            x.UserId == actor.UserId || (x.UserId == null && x.FinancerId == actor.FinancerId)
        );
        if (id.HasValue)
            query = query.Where(x => x.Id == id);
        var rows = await query.ToListAsync(ct);
        foreach (var x in rows)
            x.ReadAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> GetTicketsAsync(PageQuery q, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "support.read");
        var query = db.SupportTickets.AsNoTracking().Include(x => x.Messages).AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (Enum.TryParse<TicketStatus>(q.Status, true, out var st))
            query = query.Where(x => x.Status == st);
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new PagedResult<SupportTicket>(rows, Page(q), Size(q), count);
    }

    public async Task<SupportTicket> GetTicketAsync(
        Guid id,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "support.read");
        var x =
            await db
                .SupportTickets.AsNoTracking()
                .Include(x => x.Messages)
                .SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Ticket not found.", 404);
        if (!IsPlatform(actor) && x.FinancerId != actor.FinancerId)
            throw new DomainException("Ticket is outside your organization.", 403);
        return x;
    }

    public async Task<SupportTicket> CreateTicketAsync(
        TicketRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "support.create");
        var x = new SupportTicket
        {
            FinancerId = actor.FinancerId,
            OpenedBy = actor.UserId,
            TicketNumber = NumberGenerator.New("TKT"),
            Subject = r.Subject.Trim(),
            Category = r.Category.Trim(),
            Priority = r.Priority,
            Description = r.Description.Trim(),
            CreatedBy = actor.UserId,
        };
        db.SupportTickets.Add(x);
        Audit(
            actor,
            "Ticket.Created",
            nameof(SupportTicket),
            x.Id,
            null,
            new { x.TicketNumber, x.Subject }
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<SupportTicket> UpdateTicketAsync(
        Guid id,
        TicketMessageRequest? message,
        TicketStatusRequest? status,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        if (status is not null)
            Require(actor, "support.manage");
        if (message is not null && !actor.Roles.Contains("SuperAdmin") && !actor.Permissions.Contains("support.create") && !actor.Permissions.Contains("support.manage"))
            throw new DomainException("Permission denied.", 403);
        var x =
            await db
                .SupportTickets.Include(x => x.Messages)
                .SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Ticket not found.", 404);
        if (!IsPlatform(actor))
            RequireTenant(x.FinancerId ?? Guid.Empty, actor);
        if (message is not null)
        {
            if (string.IsNullOrWhiteSpace(message.Message))
                throw new DomainException("Message is required.");
            if (!IsPlatform(actor) && message.IsInternal)
                throw new DomainException("Internal messages are restricted to platform support staff.", 403);
            db.TicketMessages.Add(
                new TicketMessage
                {
                    SupportTicketId = x.Id,
                    SenderId = actor.UserId,
                    Message = message.Message.Trim(),
                    IsInternal = message.IsInternal,
                    CreatedBy = actor.UserId,
                }
            );

            if (IsPlatform(actor) && !message.IsInternal)
                db.Notifications.Add(
                    new Notification
                    {
                        FinancerId = x.FinancerId,
                        UserId = x.OpenedBy,
                        Title = $"Support replied to {x.TicketNumber}",
                        Message = $"INRFS Support replied to your ticket: {x.Subject}",
                        Type = "Support",
                        Channel = NotificationChannel.InApp,
                        EntityType = nameof(SupportTicket),
                        EntityId = x.Id,
                        SentAt = DateTimeOffset.UtcNow,
                        CreatedBy = actor.UserId,
                    }
                );
        }
        if (status is not null)
        {
            var statusChanged = x.Status != status.Status;
            x.Status = status.Status;
            x.AssignedTo = status.AssignedTo;

            if (statusChanged)
                db.Notifications.Add(
                new Notification
                {
                    FinancerId = x.FinancerId,
                    UserId = x.OpenedBy,
                    Title = $"Ticket {x.TicketNumber} is now {status.Status}",
                    Message = $"The status of your support ticket “{x.Subject}” changed to {status.Status}.",
                    Type = "Support",
                    Channel = NotificationChannel.InApp,
                    EntityType = nameof(SupportTicket),
                    EntityId = x.Id,
                    SentAt = DateTimeOffset.UtcNow,
                    CreatedBy = actor.UserId,
                }
                );
        }
        Audit(
            actor,
            "Ticket.Updated",
            nameof(SupportTicket),
            id,
            null,
            new
            {
                x.Status,
                x.AssignedTo,
                HasMessage = message is not null,
            }
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<object> GetSettingsAsync(
        string? scope,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "settings.read");
        var allowed = string.IsNullOrWhiteSpace(scope)
            ? (IsPlatform(actor) ? "Platform" : $"Financer:{actor.FinancerId}")
            : scope;
        if (
            !IsPlatform(actor)
            && allowed != $"Financer:{actor.FinancerId}"
            && allowed != $"User:{actor.UserId}"
        )
            throw new DomainException("Setting scope is not allowed.", 403);
        return await db
            .Settings.AsNoTracking()
            .Where(x => x.Scope == allowed)
            .OrderBy(x => x.Key)
            .Select(x => new
            {
                x.Scope,
                x.Key,
                Value = x.IsSecret ? "********" : x.Value,
                x.ValueType,
                x.Description,
                x.IsSecret,
                x.Version,
            })
            .ToListAsync(ct);
    }

    public async Task<PlatformSetting> SaveSettingAsync(
        string scope,
        string key,
        SettingRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "settings.manage");
        if (
            !IsPlatform(actor)
            && scope != $"Financer:{actor.FinancerId}"
            && scope != $"User:{actor.UserId}"
        )
            throw new DomainException("Setting scope is not allowed.", 403);
        var x = await db.Settings.SingleOrDefaultAsync(x => x.Scope == scope && x.Key == key, ct);
        if (x is null)
        {
            x = new PlatformSetting
            {
                Scope = scope,
                Key = key,
                CreatedBy = actor.UserId,
            };
            db.Settings.Add(x);
        }
        else if (r.ExpectedVersion.HasValue && x.Version != r.ExpectedVersion)
            throw new DomainException("Setting was modified by another user.", 409);
        var before = new { x.Value, x.Version };
        x.Value = r.Value;
        x.ValueType = r.ValueType;
        x.Description = r.Description;
        x.IsSecret = r.IsSecret;
        x.Version++;
        x.UpdatedAt = DateTimeOffset.UtcNow;
        x.UpdatedBy = actor.UserId;
        Audit(
            actor,
            "Setting.Saved",
            nameof(PlatformSetting),
            x.Id,
            before,
            new { Value = x.IsSecret ? "[REDACTED]" : x.Value, x.Version }
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<object> GetAuditLogsAsync(
        PageQuery q,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "audit.read");
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (q.From.HasValue)
            query = query.Where(x =>
                x.Timestamp
                >= new DateTimeOffset(q.From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            );
        if (q.To.HasValue)
            query = query.Where(x =>
                x.Timestamp
                < new DateTimeOffset(
                    q.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),
                    TimeSpan.Zero
                )
            );
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(x =>
                x.Action.Contains(s) || x.EntityType.Contains(s) || x.EntityId.Contains(s)
            );
        }
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.Timestamp)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new PagedResult<AuditLog>(rows, Page(q), Size(q), count);
    }

    public async Task<AuditLog> GetAuditLogAsync(long id, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "audit.read");
        var x =
            await db.AuditLogs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("Audit log not found.", 404);
        if (!IsPlatform(actor) && x.FinancerId != actor.FinancerId)
            throw new DomainException("Audit log is outside your organization.", 403);
        return x;
    }

    public async Task<object> GetBillingAsync(PageQuery q, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "reports.read");
        var query = db.ServiceChargeInvoices.AsNoTracking().AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (q.From.HasValue)
            query = query.Where(x => x.PeriodStart >= q.From);
        if (q.To.HasValue)
            query = query.Where(x => x.PeriodEnd <= q.To);
        if (Enum.TryParse<ScheduleStatus>(q.Status, true, out var st))
            query = query.Where(x => x.Status == st);
        var count = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.PeriodStart)
            .Skip((Page(q) - 1) * Size(q))
            .Take(Size(q))
            .ToListAsync(ct);
        return new PagedResult<ServiceChargeInvoice>(rows, Page(q), Size(q), count);
    }

    public async Task<ServiceChargeInvoice> GenerateInvoiceAsync(
        GenerateInvoiceRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "settings.manage");
        if (r.PeriodEnd < r.PeriodStart || r.DueDate < r.PeriodEnd)
            throw new DomainException("Invalid invoice dates.");
        var financer =
            await db.Financers.FindAsync([r.FinancerId], ct)
            ?? throw new DomainException("Financer not found.", 404);
        var existingInvoices = await db.ServiceChargeInvoices
            .Where(
            x =>
                x.FinancerId == r.FinancerId
                && x.PeriodStart == r.PeriodStart
                && x.PeriodEnd == r.PeriodEnd
            )
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        var from = new DateTimeOffset(r.PeriodStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(
            r.PeriodEnd.AddDays(1).ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero
        );
        var interest =
            await db
                .Payments.Where(x =>
                    x.FinancerId == r.FinancerId
                    && x.Status == PaymentStatus.Completed
                    && x.ReceivedAt >= from
                    && x.ReceivedAt < to
                )
                .SumAsync(x => (decimal?)x.InterestAmount, ct)
            ?? 0;
        var platformDefault = await db
            .Settings.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Scope == "Platform" && x.Key == "ServiceChargePercentage",
                ct
            );
        var financerOverride = await db.Settings.AsNoTracking().SingleOrDefaultAsync(
            x => x.Scope == $"Financer:{r.FinancerId}" && x.Key == "ServiceChargePercentage",
            ct
        );
        var percentage = decimal.TryParse(financerOverride?.Value, out var overridePercentage)
            ? overridePercentage
            : financer.ServiceChargePercentage
                ?? (decimal.TryParse(platformDefault?.Value, out var p) ? p : 1);
        var mutableInvoice = existingInvoices.LastOrDefault(x => x.CollectedAmount == 0);
        var lockedInterest = existingInvoices
            .Where(x => x.Id != mutableInvoice?.Id)
            .Sum(x => x.InterestActivity);
        var unbilledInterest = Math.Max(0, interest - lockedInterest);

        if (mutableInvoice is not null)
        {
            var before = new
            {
                mutableInvoice.InterestActivity,
                mutableInvoice.ChargePercentage,
                mutableInvoice.ChargeAmount,
                mutableInvoice.DueDate,
            };
            mutableInvoice.DueDate = r.DueDate;
            mutableInvoice.InterestActivity = unbilledInterest;
            mutableInvoice.ChargePercentage = percentage;
            mutableInvoice.ChargeAmount = Math.Round(unbilledInterest * percentage / 100, 2);
            mutableInvoice.Status = ScheduleStatus.Due;
            mutableInvoice.UpdatedAt = DateTimeOffset.UtcNow;
            mutableInvoice.UpdatedBy = actor.UserId;
            Audit(
                actor,
                "Invoice.Regenerated",
                nameof(ServiceChargeInvoice),
                mutableInvoice.Id,
                before,
                new
                {
                    mutableInvoice.InterestActivity,
                    mutableInvoice.ChargePercentage,
                    mutableInvoice.ChargeAmount,
                    mutableInvoice.DueDate,
                }
            );
            await db.SaveChangesAsync(ct);
            return mutableInvoice;
        }

        if (existingInvoices.Count > 0 && unbilledInterest == 0)
            return existingInvoices[^1];

        var x = new ServiceChargeInvoice
        {
            FinancerId = r.FinancerId,
            InvoiceNumber = NumberGenerator.New("INV"),
            PeriodStart = r.PeriodStart,
            PeriodEnd = r.PeriodEnd,
            DueDate = r.DueDate,
            InterestActivity = unbilledInterest,
            ChargePercentage = percentage,
            ChargeAmount = Math.Round(unbilledInterest * percentage / 100, 2),
            Status = ScheduleStatus.Due,
            CreatedBy = actor.UserId,
        };
        db.ServiceChargeInvoices.Add(x);
        db.Notifications.Add(
            new Notification
            {
                FinancerId = x.FinancerId,
                Title = $"Service-charge invoice {x.InvoiceNumber}",
                Message = $"A service-charge invoice for ₹{x.ChargeAmount:N2} was generated and is due on {x.DueDate:dd MMM yyyy}.",
                Type = "Service Charges",
                Channel = NotificationChannel.InApp,
                EntityType = nameof(ServiceChargeInvoice),
                EntityId = x.Id,
                SentAt = DateTimeOffset.UtcNow,
                CreatedBy = actor.UserId,
            }
        );
        Audit(
            actor,
            existingInvoices.Count == 0 ? "Invoice.Generated" : "Invoice.SupplementGenerated",
            nameof(ServiceChargeInvoice),
            x.Id,
            null,
            new { x.InvoiceNumber, x.ChargeAmount }
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<ServiceChargeInvoice> CollectInvoiceAsync(
        Guid id,
        CollectInvoiceRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "payments.record");
        if (r.Amount <= 0 || string.IsNullOrWhiteSpace(r.Reference))
            throw new DomainException("Positive amount and reference are required.");
        var x =
            await db.ServiceChargeInvoices.FindAsync([id], ct)
            ?? throw new DomainException("Invoice not found.", 404);
        RequireTenant(x.FinancerId, actor);
        if (x.Status == ScheduleStatus.Paid)
            throw new DomainException("Invoice is already paid.", 409);
        if (x.CollectedAmount + r.Amount > x.ChargeAmount)
            throw new DomainException("Collection exceeds invoice amount.");
        x.CollectedAmount += r.Amount;
        x.Status =
            x.CollectedAmount == x.ChargeAmount
                ? ScheduleStatus.Paid
                : ScheduleStatus.PartiallyPaid;
        db.Transactions.Add(
            new FinancialTransaction
            {
                FinancerId = x.FinancerId,
                TransactionNumber = NumberGenerator.New("TXN"),
                Type = TransactionType.Fee,
                Amount = r.Amount,
                TransactionAt = DateTimeOffset.UtcNow,
                ExternalReference = r.Reference,
                IsReconciled = false,
                CreatedBy = actor.UserId,
            }
        );
        Audit(
            actor,
            "Invoice.CollectionRecorded",
            nameof(ServiceChargeInvoice),
            id,
            null,
            new
            {
                r.Amount,
                r.Reference,
                x.Status,
            }
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<ServiceChargeInvoice> AdjustInvoiceAsync(
        Guid id,
        AdjustInvoiceRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "settings.manage");
        if (r.CreditAmount <= 0 || string.IsNullOrWhiteSpace(r.Reason))
            throw new DomainException("Positive credit amount and reason are required.");
        var invoice = await db.ServiceChargeInvoices.FindAsync([id], ct)
            ?? throw new DomainException("Invoice not found.", 404);
        RequireTenant(invoice.FinancerId, actor);
        if (invoice.ChargeAmount - r.CreditAmount < invoice.CollectedAmount)
            throw new DomainException("Credit cannot reduce the invoice below its collected amount.");
        var before = new { invoice.ChargeAmount, invoice.Status };
        invoice.ChargeAmount -= r.CreditAmount;
        invoice.Status = invoice.ChargeAmount == invoice.CollectedAmount ? ScheduleStatus.Paid : ScheduleStatus.Due;
        var creditNumber = NumberGenerator.New("CN");
        db.Transactions.Add(new FinancialTransaction
        {
            FinancerId = invoice.FinancerId,
            TransactionNumber = NumberGenerator.New("TXN"),
            Type = TransactionType.Adjustment,
            Amount = r.CreditAmount,
            TransactionAt = DateTimeOffset.UtcNow,
            ExternalReference = $"{creditNumber}|{invoice.InvoiceNumber}|{r.Reason.Trim()}",
            IsReconciled = true,
            ReconciledAt = DateTimeOffset.UtcNow,
            ReconciledBy = actor.UserId,
            CreatedBy = actor.UserId,
        });
        Audit(actor, "Invoice.CreditNoteIssued", nameof(ServiceChargeInvoice), id, before,
            new { CreditNumber = creditNumber, r.CreditAmount, Reason = r.Reason.Trim(), invoice.ChargeAmount, invoice.Status });
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<object> GetSubscriptionsAsync(CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "settings.read");
        var plans = await db
            .SubscriptionPlans.AsNoTracking()
            .OrderBy(x => x.MonthlyPrice)
            .ToListAsync(ct);
        var query = db
            .FinancerSubscriptions.AsNoTracking()
            .Include(x => x.SubscriptionPlan)
            .AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        return new
        {
            plans,
            subscriptions = await query.OrderByDescending(x => x.StartsOn).ToListAsync(ct),
        };
    }

    public async Task<SubscriptionPlan> SaveSubscriptionPlanAsync(
        Guid? id,
        SubscriptionPlanRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "settings.manage");
        if (r.MonthlyPrice < 0 || r.CustomerLimit < 0 || r.LoanLimit < 0 || r.SmsCredits < 0)
            throw new DomainException("Plan limits and price cannot be negative.");
        SubscriptionPlan x;
        if (id.HasValue)
            x =
                await db.SubscriptionPlans.FindAsync([id.Value], ct)
                ?? throw new DomainException("Plan not found.", 404);
        else
        {
            x = new SubscriptionPlan { CreatedBy = actor.UserId };
            db.SubscriptionPlans.Add(x);
        }
        if (await db.SubscriptionPlans.AnyAsync(p => p.Code == r.Code && p.Id != x.Id, ct))
            throw new DomainException("Plan code already exists.", 409);
        x.Code = r.Code.Trim().ToUpperInvariant();
        x.Name = r.Name.Trim();
        x.MonthlyPrice = r.MonthlyPrice;
        x.CustomerLimit = r.CustomerLimit;
        x.LoanLimit = r.LoanLimit;
        x.SmsCredits = r.SmsCredits;
        x.FeaturesJson = JsonSerializer.Serialize(r.Features);
        x.IsActive = r.IsActive;
        Audit(
            actor,
            id.HasValue ? "SubscriptionPlan.Updated" : "SubscriptionPlan.Created",
            nameof(SubscriptionPlan),
            x.Id,
            null,
            r
        );
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<FinancerSubscription> AssignSubscriptionAsync(
        AssignSubscriptionRequest r,
        CurrentUser actor,
        CancellationToken ct
    )
    {
        Require(actor, "settings.manage");
        if (
            !await db.Financers.AnyAsync(x => x.Id == r.FinancerId, ct)
            || !await db.SubscriptionPlans.AnyAsync(x => x.Id == r.PlanId && x.IsActive, ct)
        )
            throw new DomainException("Active plan and valid financer are required.");
        var active = await db
            .FinancerSubscriptions.Where(x =>
                x.FinancerId == r.FinancerId && x.Status == AccountStatus.Active
            )
            .ToListAsync(ct);
        foreach (var item in active)
        {
            item.Status = AccountStatus.Inactive;
            item.EndsOn = r.StartsOn.AddDays(-1);
        }
        var x = new FinancerSubscription
        {
            FinancerId = r.FinancerId,
            SubscriptionPlanId = r.PlanId,
            StartsOn = r.StartsOn,
            EndsOn = r.EndsOn,
            Status = AccountStatus.Active,
            CreatedBy = actor.UserId,
        };
        db.FinancerSubscriptions.Add(x);
        Audit(actor, "Subscription.Assigned", nameof(FinancerSubscription), x.Id, null, r);
        await db.SaveChangesAsync(ct);
        return x;
    }

    public async Task<object> GetSmsUsageAsync(PageQuery q, CurrentUser actor, CancellationToken ct)
    {
        Require(actor, "reports.read");
        var query = db.SmsDeliveries.AsNoTracking().AsQueryable();
        if (!IsPlatform(actor))
            query = query.Where(x => x.FinancerId == actor.FinancerId);
        if (q.FinancerId.HasValue)
            query = query.Where(x => x.FinancerId == q.FinancerId);
        if (q.From.HasValue)
            query = query.Where(x =>
                x.CreatedAt
                >= new DateTimeOffset(q.From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            );
        if (q.To.HasValue)
            query = query.Where(x =>
                x.CreatedAt
                < new DateTimeOffset(
                    q.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),
                    TimeSpan.Zero
                )
            );
        var rows = await query
            .GroupBy(x => new { x.FinancerId, x.Status })
            .Select(x => new
            {
                x.Key.FinancerId,
                x.Key.Status,
                Messages = x.Count(),
                Credits = x.Sum(y => y.CreditsUsed),
            })
            .ToListAsync(ct);
        return new
        {
            summary = new
            {
                sent = rows.Sum(x => x.Messages),
                credits = rows.Sum(x => x.Credits),
                delivered = rows.Where(x => x.Status == "Delivered").Sum(x => x.Messages),
                failed = rows.Where(x => x.Status == "Failed").Sum(x => x.Messages),
            },
            byFinancer = rows,
        };
    }

    private static void Ensure(LoanApplication x, LoanApplicationStatus expected)
    {
        if (x.Status != expected)
            throw new DomainException(
                $"Action requires status {expected}; current status is {x.Status}.",
                409
            );
    }

    private void AddHistory(
        LoanApplication x,
        LoanApplicationStatus from,
        LoanApplicationStatus to,
        string? reason,
        CurrentUser actor
    )
    {
        if (from == to)
            return;
        var history = new LoanStatusHistory
        {
            LoanApplicationId = x.Id,
            LoanApplication = x,
            FromStatus = from,
            ToStatus = to,
            Reason = reason,
            CreatedBy = actor.UserId,
        };
        db.LoanStatusHistory.Add(history);
        x.StatusHistory.Add(history);
    }

    private static decimal CalculateEmi(decimal principal, decimal annualRate, int months)
    {
        if (months <= 0)
            return 0;
        var r = (double)(annualRate / 1200);
        return r == 0
            ? Math.Round(principal / months, 2)
            : Math.Round(
                principal * (decimal)(r * Math.Pow(1 + r, months) / (Math.Pow(1 + r, months) - 1)),
                2
            );
    }

    private static decimal PresentValue(decimal payment, decimal annualRate, int months)
    {
        var r = (double)(annualRate / 1200);
        return r == 0 ? payment * months : payment * (decimal)((1 - Math.Pow(1 + r, -months)) / r);
    }

    private static FinancerDto Map(FinancerOrganization x) =>
        new(
            x.Id,
            x.FinancerNumber,
            x.LegalName,
            x.DisplayName,
            x.OwnerName,
            x.Email,
            x.Phone,
            x.City,
            x.State,
            x.Status,
            x.KycStatus,
            x.ServiceChargePercentage,
            x.CreatedAt
        );

    private static UserDto Map(UserAccount x) =>
        new(
            x.Id,
            x.FinancerId,
            x.EmployeeNumber,
            x.FirstName,
            x.LastName,
            x.Email,
            x.Phone,
            x.Status,
            x.UserRoles.Select(y => y.Role.Name).ToList(),
            x.LastLoginAt,
            x.ProfileImageDataUrl
        );

    private static CustomerDto Map(Customer x) =>
        new(
            x.Id,
            x.FinancerId,
            x.CustomerNumber,
            x.FullName,
            x.DateOfBirth,
            x.Gender,
            x.Phone,
            x.Email,
            x.AddressLine1,
            x.AddressLine2,
            x.City,
            x.State,
            x.PostalCode,
            Security.Mask(x.AadhaarEncrypted),
            Security.Mask(x.PanEncrypted),
            x.Status,
            x.KycStatus,
            x.CreatedAt
        );

    private static LoanProductDto Map(LoanProduct x) =>
        new(
            x.Id,
            x.Code,
            x.Name,
            x.MinimumPrincipal,
            x.MaximumPrincipal,
            x.MinimumTenureMonths,
            x.MaximumTenureMonths,
            x.AnnualInterestRate,
            x.InterestMethod,
            x.RepaymentFrequency,
            x.ProcessingFeePercentage,
            x.LateFeePercentage,
            x.MaximumFoirPercentage,
            x.IsActive
        );

    private static LoanApplicationDto Map(LoanApplication x) =>
        new(
            x.Id,
            x.ApplicationNumber,
            x.CustomerId,
            x.LoanProductId,
            x.RequestedPrincipal,
            x.ApprovedPrincipal,
            x.RequestedTenureMonths,
            x.ApprovedTenureMonths,
            x.ApprovedAnnualRate,
            x.Status,
            x.RejectionCode,
            x.DecisionNotes,
            x.CreatedAt
        );

    private static LoanDto Map(Loan x) =>
        new(
            x.Id,
            x.LoanNumber,
            x.CustomerId,
            x.LoanProductId,
            x.Principal,
            x.AnnualInterestRate,
            x.LoanProduct.RepaymentFrequency,
            x.TenureMonths,
            x.DisbursementDate,
            x.MaturityDate,
            x.Status,
            x.PrincipalOutstanding,
            x.InterestOutstanding,
            x.FeesOutstanding,
            x.DurationValue,
            x.DurationUnit,
            x.InterestRate,
            x.InterestRateBasis,
            x.InterestCollectionFrequency,
            x.AdminCollectionMonitoring
        );

    private static PaymentDto Map(Payment x) =>
        new(
            x.Id,
            x.PaymentNumber,
            x.FinancerId,
            x.LoanId,
            x.Amount,
            x.PrincipalAmount,
            x.InterestAmount,
            x.FeeAmount,
            x.ReceivedAt,
            x.Mode,
            x.ExternalReference,
            x.Status
        );

    private static ScheduleDto Map(PaymentSchedule x) =>
        new(
            x.Id,
            x.InstallmentNumber,
            x.DueDate,
            x.OpeningPrincipal,
            x.PrincipalDue,
            x.InterestDue,
            x.FeesDue,
            x.AmountPaid,
            x.Status,
            x.PeriodStart,
            x.PeriodEnd,
            x.InterestDays,
            x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid
        );

    private static void ConfigureInterestOnlyLoan(Loan loan, DirectLoanRequest request, Guid actorId)
    {
        var duration = request.DurationValue ?? request.TenureMonths;
        var enteredRate = request.InterestRate ?? request.AnnualInterestRate;
        var annualRate = request.InterestRateBasis switch
        {
            InterestRateBasis.PerMonth => enteredRate * 12m,
            InterestRateBasis.PerWeek => enteredRate * 365m / 7m,
            InterestRateBasis.PerDay => enteredRate * 365m,
            _ => enteredRate,
        };
        var maturity = request.DurationUnit switch
        {
            LoanDurationUnit.Days => request.StartDate.AddDays(duration),
            LoanDurationUnit.Weeks => request.StartDate.AddDays(duration * 7),
            _ => request.StartDate.AddMonths(duration),
        };
        loan.DurationValue = duration;
        loan.DurationUnit = request.DurationUnit;
        loan.InterestRate = enteredRate;
        loan.InterestRateBasis = request.InterestRateBasis;
        loan.InterestCollectionFrequency = request.InterestCollectionFrequency;
        loan.AnnualInterestRate = annualRate;
        loan.MaturityDate = maturity;
        loan.TenureMonths = Math.Max(1, (int)Math.Ceiling((maturity.DayNumber - request.StartDate.DayNumber) / 30.4375m));

        var dueDates = new List<DateOnly>();
        if (request.InterestCollectionFrequency == InterestCollectionFrequency.Daily)
            for (var date = request.StartDate.AddDays(1); date <= maturity; date = date.AddDays(1)) dueDates.Add(date);
        else if (request.InterestCollectionFrequency == InterestCollectionFrequency.Weekly)
            for (var date = request.StartDate.AddDays(7); date < maturity; date = date.AddDays(7)) dueDates.Add(date);
        else if (request.InterestCollectionFrequency == InterestCollectionFrequency.Monthly)
            for (var period = 1; request.StartDate.AddMonths(period) < maturity; period++) dueDates.Add(request.StartDate.AddMonths(period));
        if (dueDates.Count == 0 || dueDates[^1] != maturity) dueDates.Add(maturity);

        var periodStart = request.StartDate;
        for (var i = 0; i < dueDates.Count; i++)
        {
            var due = dueDates[i];
            var days = due.DayNumber - periodStart.DayNumber;
            var interest = request.InterestRateBasis switch
            {
                // A complete calendar month always earns exactly the entered monthly rate,
                // regardless of whether that month contains 28, 29, 30 or 31 days.
                InterestRateBasis.PerMonth when due == periodStart.AddMonths(1) =>
                    Math.Round(loan.Principal * enteredRate / 100m, 2),
                InterestRateBasis.PerMonth =>
                    Math.Round(loan.Principal * enteredRate / 100m * days / 30m, 2),
                InterestRateBasis.PerWeek =>
                    Math.Round(loan.Principal * enteredRate / 100m * days / 7m, 2),
                InterestRateBasis.PerDay =>
                    Math.Round(loan.Principal * enteredRate / 100m * days, 2),
                _ => Math.Round(loan.Principal * enteredRate / 100m * days / 365m, 2),
            };
            loan.Schedules.Add(new PaymentSchedule
            {
                InstallmentNumber = i + 1, PeriodStart = periodStart, PeriodEnd = due,
                InterestDays = days, DueDate = due, OpeningPrincipal = loan.Principal,
                InterestDue = interest, PrincipalDue = i == dueDates.Count - 1 ? loan.Principal : 0,
                Status = ScheduleStatus.Upcoming, CreatedBy = actorId,
            });
            periodStart = due;
        }
        loan.InterestOutstanding = loan.Schedules.Sum(schedule => schedule.InterestDue);
    }

    private static SettlementQuoteDto BuildSettlementQuote(Loan loan, DateOnly settlementDate)
    {
        var accruedInterest = loan.Schedules.Sum(schedule =>
        {
            var earned = EarnedInterestThrough(schedule, settlementDate);
            var paid = Math.Min(schedule.InterestDue, Math.Max(0, schedule.AmountPaid - schedule.FeesDue));
            return Math.Max(0, earned - paid);
        });
        accruedInterest = Math.Round(accruedInterest, 2);
        var futureInterestWaived = Math.Max(0, loan.InterestOutstanding - accruedInterest);
        var amount = loan.PrincipalOutstanding + accruedInterest + loan.FeesOutstanding;
        return new SettlementQuoteDto(
            loan.Id,
            settlementDate,
            loan.PrincipalOutstanding,
            accruedInterest,
            loan.FeesOutstanding,
            Math.Round(futureInterestWaived, 2),
            Math.Round(amount, 2)
        );
    }

    private static decimal EarnedInterestThrough(PaymentSchedule schedule, DateOnly date)
    {
        if (date <= schedule.PeriodStart) return 0;
        if (date >= schedule.PeriodEnd || schedule.InterestDays <= 0) return schedule.InterestDue;
        var elapsedDays = date.DayNumber - schedule.PeriodStart.DayNumber;
        return Math.Round(schedule.InterestDue * elapsedDays / schedule.InterestDays, 2);
    }
}
