using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace INRFS.Financer.Infrastructure;

public sealed class AuthService(
    FinancerDbContext db,
    IOptions<JwtOptions> jwtOptions,
    IOptions<OtpOptions> otpOptions,
    IPasswordHasher<UserAccount> passwordHasher,
    IAuthMessageSender messageSender,
    IHostEnvironment? environment = null
) : IAuthService
{
    private readonly JwtOptions _jwt = jwtOptions.Value;
    private readonly OtpOptions _otp = otpOptions.Value;
    private readonly bool _isDevelopment = environment?.IsDevelopment() ?? true;

    public async Task<AuthChallengeResponse> RegisterFinancerAsync(
        RegisterFinancerRequest request,
        CancellationToken ct
    )
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var mobile = request.Mobile.Trim();
        var existing = await db.Users.Include(x => x.Financer)
            .FirstOrDefaultAsync(x => x.Email == email || x.Phone == mobile, ct);
        if (existing is not null)
        {
            if (existing.Status != AccountStatus.Pending || existing.Email != email || existing.Phone != mobile)
                throw new DomainException("An account with this email or mobile already exists.", 409);
            var lastChallenge = await db.OtpChallenges.OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(x => x.UserId == existing.Id && x.Purpose == "Registration", ct);
            if (lastChallenge is not null && lastChallenge.CreatedAt.AddSeconds(_otp.MinimumResendSeconds) > DateTimeOffset.UtcNow)
                throw new DomainException("Please wait before requesting another code.", 429);

            var existingName = request.FullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            existing.FirstName = existingName[0];
            existing.LastName = existingName.Length > 1 ? existingName[1] : "";
            existing.Financer!.LegalName = request.BusinessName.Trim();
            existing.Financer.DisplayName = request.BusinessName.Trim();
            existing.Financer.OwnerName = request.FullName.Trim();
            existing.Financer.City = request.City.Trim();
            existing.Financer.State = request.State.Trim();
            var retryChallenge = await CreateChallengeAsync(existing.Id, mobile, "Registration", ct);
            await db.SaveChangesAsync(ct);
            return retryChallenge;
        }

        var ownerRole =
            await db.Roles.SingleOrDefaultAsync(x => x.Name == "FinancerOwner", ct)
            ?? throw new DomainException("Financer registration is not configured.", 503);
        var nameParts = request
            .FullName.Trim()
            .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var financer = new FinancerOrganization
        {
            FinancerNumber = NumberGenerator.New("FIN"),
            LegalName = request.BusinessName.Trim(),
            DisplayName = request.BusinessName.Trim(),
            OwnerName = request.FullName.Trim(),
            Email = email,
            Phone = mobile,
            City = request.City.Trim(),
            State = request.State.Trim(),
            Status = AccountStatus.Pending,
            KycStatus = VerificationStatus.Pending,
        };
        var user = new UserAccount
        {
            Financer = financer,
            EmployeeNumber = NumberGenerator.New("OWN"),
            FirstName = nameParts[0],
            LastName = nameParts.Length > 1 ? nameParts[1] : "",
            Email = email,
            Phone = mobile,
            Status = AccountStatus.Pending,
        };
        // A login password is generated only after mobile verification succeeds.
        user.PasswordHash = passwordHasher.HashPassword(user, Security.Token());
        user.UserRoles.Add(new UserRole { User = user, Role = ownerRole });
        db.AddRange(financer, user);
        var challenge = await CreateChallengeAsync(user.Id, mobile, "Registration", ct);
        await db.SaveChangesAsync(ct);
        return challenge;
    }

    public async Task<AuthChallengeResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var (user, isAdmin) = await ValidatePasswordLoginAsync(request, ct);
        var loginDestination = isAdmin || string.IsNullOrWhiteSpace(user.Phone) ? user.Email : user.Phone;
        var useBootstrapAdminOtp =
            isAdmin
            && _otp.BootstrapAdminEnabled
            && !string.IsNullOrWhiteSpace(_otp.BootstrapAdminEmail)
            && user.Email.Equals(_otp.BootstrapAdminEmail.Trim(), StringComparison.OrdinalIgnoreCase);
        var challenge = await CreateChallengeAsync(
            user.Id,
            loginDestination,
            "Login",
            ct,
            useBootstrapAdminOtp ? _otp.BootstrapAdminCode : null,
            useBootstrapAdminOtp
        );
        await db.SaveChangesAsync(ct);
        return challenge;
    }

    public async Task<AuthTokenResponse> LoginFinancerAsync(
        LoginRequest request,
        string? ipAddress,
        CancellationToken ct
    )
    {
        if (request.Portal != "financer")
            throw new DomainException("Invalid portal for financer login.", 400);
        var (validatedUser, _) = await ValidatePasswordLoginAsync(request, ct);
        var user = await LoadUserAsync(validatedUser.Id, ct);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        return await IssueTokensAsync(user, null, ipAddress, ct);
    }

    private async Task<(UserAccount User, bool IsAdmin)> ValidatePasswordLoginAsync(
        LoginRequest request,
        CancellationToken ct
    )
    {
        var identifier = request.Email.Trim().ToLowerInvariant();
        var user =
            await db
                .Users.Include(x => x.Financer)
                .SingleOrDefaultAsync(
                    x => x.Email == identifier || x.Phone == request.Email.Trim(),
                    ct
                )
            ?? throw new DomainException("Invalid credentials.", 401);
        if (user.LockedUntil > DateTimeOffset.UtcNow || user.Status != AccountStatus.Active)
            throw new DomainException("Account is not available.", 403);
        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= 5)
                user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            await db.SaveChangesAsync(ct);
            throw new DomainException("Invalid credentials.", 401);
        }
        var roles = await db
            .UserRoles.Where(x => x.UserId == user.Id)
            .Select(x => x.Role.Name)
            .ToListAsync(ct);
        var isAdmin = roles.Any(x =>
            x
                is "SuperAdmin"
                    or "Admin"
                    or "ComplianceOfficer"
                    or "FinanceOfficer"
                    or "Auditor"
                    or "SupportAgent"
        );
        if ((request.Portal == "admin") != isAdmin)
            throw new DomainException("Account is not authorized for this portal.", 403);
        user.FailedLoginAttempts = 0;
        return (user, isAdmin);
    }

    public async Task<AuthChallengeResponse> RequestOtpAsync(
        OtpRequest request,
        CancellationToken ct
    )
    {
        var destination = request.Destination.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(
            x => x.Email == destination || x.Phone == destination,
            ct
        );
        if (user is null)
            return new AuthChallengeResponse(
                Guid.Empty,
                Mask(destination),
                DateTimeOffset.UtcNow.AddMinutes(_otp.ExpiryMinutes)
            );
        var last = await db
            .OtpChallenges.OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.UserId == user.Id && x.Purpose == request.Purpose, ct);
        if (
            last is not null
            && last.CreatedAt.AddSeconds(_otp.MinimumResendSeconds) > DateTimeOffset.UtcNow
        )
            throw new DomainException("Please wait before requesting another code.", 429);
        var response = await CreateChallengeAsync(user.Id, destination, request.Purpose, ct);
        await db.SaveChangesAsync(ct);
        return response;
    }

    private async Task<AuthChallengeResponse> CreateChallengeAsync(
        Guid userId,
        string destination,
        string purpose,
        CancellationToken ct,
        string? overrideCode = null,
        bool skipDelivery = false
    )
    {
        if (!string.IsNullOrWhiteSpace(_otp.FixedDevelopmentCode) && !_isDevelopment)
            throw new InvalidOperationException("A fixed OTP code is permitted only in Development.");
        var code = !string.IsNullOrWhiteSpace(overrideCode)
            ? overrideCode
            : string.IsNullOrWhiteSpace(_otp.FixedDevelopmentCode)
                ? Security.Otp()
                : _otp.FixedDevelopmentCode;
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[0-9]{6}$"))
            throw new InvalidOperationException("The configured OTP code must contain exactly six digits.");
        var challenge = new OtpChallenge
        {
            UserId = userId,
            Destination = destination,
            Purpose = purpose,
            CodeHash = Security.Hash(code),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_otp.ExpiryMinutes),
        };
        db.OtpChallenges.Add(challenge);
        if (!skipDelivery)
            await messageSender.SendOtpAsync(destination, code, purpose, ct);
        return new(challenge.Id, Mask(destination), challenge.ExpiresAt);
    }

    public async Task<AuthTokenResponse> VerifyOtpAsync(
        VerifyOtpRequest request,
        string? ipAddress,
        CancellationToken ct
    )
    {
        var challenge = await ValidateChallengeAsync(request, "Login", ct);
        challenge.UsedAt = DateTimeOffset.UtcNow;
        var user = await LoadUserAsync(challenge.UserId!.Value, ct);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        return await IssueTokensAsync(user, null, ipAddress, ct);
    }

    public async Task<RegistrationCompletionResponse> VerifyRegistrationOtpAsync(
        VerifyOtpRequest request,
        CancellationToken ct
    )
    {
        var challenge = await ValidateChallengeAsync(request, "Registration", ct);
        var user = await LoadUserAsync(challenge.UserId!.Value, ct);
        var password = Security.TemporaryPassword();
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        user.Status = AccountStatus.Active;
        if (user.Financer is not null)
            user.Financer.Status = AccountStatus.Active;
        challenge.UsedAt = DateTimeOffset.UtcNow;

        await messageSender.SendWelcomeCredentialsAsync(user.Email, user.Phone, password, ct);
        await db.SaveChangesAsync(ct);
        return new RegistrationCompletionResponse(
            user.Phone,
            Mask(user.Email),
            "Your User ID and password have been sent to your registered email address. Please check your email and log in."
        );
    }

    private async Task<OtpChallenge> ValidateChallengeAsync(
        VerifyOtpRequest request,
        string expectedPurpose,
        CancellationToken ct
    )
    {
        var challenge = await db.OtpChallenges.SingleOrDefaultAsync(x => x.Id == request.ChallengeId, ct)
            ?? throw new DomainException("Invalid OTP challenge.", 404);
        if (!challenge.Purpose.Equals(expectedPurpose, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("OTP challenge is not valid for this action.", 400);
        if (challenge.UsedAt.HasValue || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new DomainException("OTP challenge has expired.", 400);
        challenge.Attempts++;
        if (challenge.Attempts > _otp.MaximumAttempts)
        {
            challenge.UsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw new DomainException("OTP attempt limit exceeded.", 429);
        }
        if (
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(challenge.CodeHash),
                Convert.FromHexString(Security.Hash(request.Code))
            )
        )
        {
            await db.SaveChangesAsync(ct);
            throw new DomainException("Invalid OTP.", 400);
        }
        return challenge;
    }

    public async Task<AuthTokenResponse> RefreshAsync(
        RefreshRequest request,
        string? ipAddress,
        CancellationToken ct
    )
    {
        var hash = Security.Hash(request.RefreshToken);
        var token =
            await db
                .RefreshTokens.Include(x => x.User)
                .SingleOrDefaultAsync(x => x.TokenHash == hash, ct)
            ?? throw new DomainException("Invalid refresh token.", 401);
        if (token.RevokedAt.HasValue || token.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await RevokeFamilyAsync(token.Family, ct);
            throw new DomainException("Refresh token is expired or revoked.", 401);
        }
        token.RevokedAt = DateTimeOffset.UtcNow;
        var user = await LoadUserAsync(token.UserId, ct);
        return await IssueTokensAsync(user, token, ipAddress, ct);
    }

    public async Task RevokeAsync(RefreshRequest request, CancellationToken ct)
    {
        var token = await db.RefreshTokens.SingleOrDefaultAsync(
            x => x.TokenHash == Security.Hash(request.RefreshToken),
            ct
        );
        if (token is not null)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct)
    {
        var identifier = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(
            x => x.Email == identifier || x.Phone == request.Email.Trim(),
            ct
        );
        if (user is null)
            return;
        var resetToken = Security.Token(32);
        db.OtpChallenges.Add(
            new OtpChallenge
            {
                UserId = user.Id,
                Destination = user.Email,
                Purpose = "PasswordReset",
                CodeHash = Security.Hash(resetToken),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            }
        );
        await db.SaveChangesAsync(ct);
        await messageSender.SendPasswordResetAsync(user.Email, resetToken, ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        if (request.NewPassword != request.ConfirmPassword || request.NewPassword.Length < 10)
            throw new DomainException("Passwords must match and contain at least 10 characters.");
        var challenge =
            await db.OtpChallenges.SingleOrDefaultAsync(
                x => x.Purpose == "PasswordReset" && x.CodeHash == Security.Hash(request.Token),
                ct
            ) ?? throw new DomainException("Invalid reset token.");
        if (challenge.UsedAt.HasValue || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new DomainException("Reset token has expired.");
        var user =
            await db.Users.FindAsync([challenge.UserId!.Value], ct)
            ?? throw new DomainException("User not found.", 404);
        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        challenge.UsedAt = DateTimeOffset.UtcNow;
        foreach (
            var token in await db
                .RefreshTokens.Where(x => x.UserId == user.Id && x.RevokedAt == null)
                .ToListAsync(ct)
        )
            token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken ct
    )
    {
        if (request.NewPassword != request.ConfirmPassword || request.NewPassword.Length < 10)
            throw new DomainException("Passwords must match and contain at least 10 characters.");
        var user =
            await db.Users.FindAsync([userId], ct)
            ?? throw new DomainException("User not found.", 404);
        if (
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword)
            == PasswordVerificationResult.Failed
        )
            throw new DomainException("Current password is incorrect.", 400);
        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        foreach (
            var token in await db
                .RefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null)
                .ToListAsync(ct)
        )
            token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<UserAccount> LoadUserAsync(Guid id, CancellationToken ct) =>
        await db
            .Users.Include(x => x.Financer)
            .Include(x => x.UserRoles)
                .ThenInclude(x => x.Role)
                    .ThenInclude(x => x.RolePermissions)
                        .ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new DomainException("User not found.", 404);

    private async Task<AuthTokenResponse> IssueTokensAsync(
        UserAccount user,
        RefreshToken? oldToken,
        string? ip,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(_jwt.Key) || _jwt.Key.Length < 32)
            throw new InvalidOperationException("Jwt:Key must contain at least 32 characters.");
        var roles = user.UserRoles.Select(x => x.Role.Name).Distinct().ToList();
        var permissions = user
            .UserRoles.SelectMany(x => x.Role.RolePermissions)
            .Select(x => x.Permission.Name)
            .Distinct()
            .ToList();
        var expires = DateTimeOffset.UtcNow.AddMinutes(_jwt.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("financer_id", user.FinancerId?.ToString() ?? ""),
        };
        claims.AddRange(roles.Select(x => new Claim(ClaimTypes.Role, x)));
        claims.AddRange(permissions.Select(x => new Claim("permission", x)));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key)),
            SecurityAlgorithms.HmacSha256
        );
        var jwt = new JwtSecurityToken(
            _jwt.Issuer,
            _jwt.Audience,
            claims,
            expires: expires.UtcDateTime,
            signingCredentials: credentials
        );
        var refreshRaw = Security.Token();
        var family = oldToken?.Family ?? Guid.NewGuid().ToString("N");
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Security.Hash(refreshRaw),
            Family = family,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenDays),
        };
        db.RefreshTokens.Add(refresh);
        if (oldToken is not null)
            oldToken.ReplacedById = refresh.Id;
        db.AuditLogs.Add(
            new AuditLog
            {
                ActorId = user.Id,
                FinancerId = user.FinancerId,
                Action = "Authentication.TokenIssued",
                EntityType = nameof(UserAccount),
                EntityId = user.Id.ToString(),
                IpAddress = ip,
            }
        );
        await db.SaveChangesAsync(ct);
        return new(
            new JwtSecurityTokenHandler().WriteToken(jwt),
            refreshRaw,
            expires,
            new UserDto(
                user.Id,
                user.FinancerId,
                user.EmployeeNumber,
                user.FirstName,
                user.LastName,
                user.Email,
                user.Phone,
                user.Status,
                roles,
                user.LastLoginAt,
                user.ProfileImageDataUrl
            )
        );
    }

    private async Task RevokeFamilyAsync(string family, CancellationToken ct)
    {
        foreach (
            var item in await db
                .RefreshTokens.Where(x => x.Family == family && x.RevokedAt == null)
                .ToListAsync(ct)
        )
            item.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string Mask(string value)
    {
        var at = value.IndexOf('@');
        return at > 1 ? $"{value[0]}***{value[(at - 1)..]}"
            : value.Length > 4 ? $"***{value[^4..]}"
            : "***";
    }
}
