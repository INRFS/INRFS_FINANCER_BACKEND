using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using INRFS.Financer.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace INRFS.Financer.IntegrationTests;

public sealed class FinancerPortalCoverageTests
{
    [Fact]
    public async Task Configured_bootstrap_admin_can_use_fixed_otp_in_production_without_delivery()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>().UseSqlite(connection).Options;
        await using var db = new FinancerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var role = new Role { Name = "SuperAdmin", IsSystem = true };
        var admin = new UserAccount
        {
            Email = "admin@local.test",
            EmployeeNumber = "ADM-TEST-001",
            FirstName = "Bootstrap",
            LastName = "Admin",
            Status = AccountStatus.Active,
        };
        admin.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(admin, "StrongLocalPassword123!");
        admin.UserRoles.Add(new UserRole { User = admin, Role = role });
        var unrelatedUserWithoutPhone = new UserAccount
        {
            Email = "another-user@local.test",
            EmployeeNumber = "USR-TEST-001",
            FirstName = "Another",
            LastName = "User",
            Status = AccountStatus.Active,
        };
        unrelatedUserWithoutPhone.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(
            unrelatedUserWithoutPhone,
            "AnotherStrongPassword123!"
        );
        db.AddRange(role, admin, unrelatedUserWithoutPhone);
        await db.SaveChangesAsync();

        var sender = new TestAuthMessageSender();
        var service = new AuthService(
            db,
            Options.Create(new JwtOptions { Key = "integration-test-key-at-least-32-characters" }),
            Options.Create(new OtpOptions
            {
                BootstrapAdminEnabled = true,
                BootstrapAdminEmail = "admin@local.test",
                BootstrapAdminCode = "123456",
            }),
            new PasswordHasher<UserAccount>(),
            sender,
            new TestHostEnvironment("Production")
        );

        var challenge = await service.LoginAsync(
            new LoginRequest("admin@local.test", "StrongLocalPassword123!", "admin"),
            default
        );

        Assert.Null(sender.Otp);
        var tokens = await service.VerifyOtpAsync(
            new VerifyOtpRequest(challenge.ChallengeId, "123456"),
            "127.0.0.1",
            default
        );
        Assert.Contains("SuperAdmin", tokens.User.Roles);
    }

    [Fact]
    public async Task Financer_can_register_with_the_fields_used_by_the_UI()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FinancerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Roles.Add(new Role { Name = "FinancerOwner", IsSystem = true });
        await db.SaveChangesAsync();

        var sender = new TestAuthMessageSender();
        var service = new AuthService(
            db,
            Options.Create(new JwtOptions { Key = "integration-test-key-at-least-32-characters" }),
            Options.Create(new OtpOptions()),
            new PasswordHasher<UserAccount>(),
            sender
        );

        var challenge = await service.RegisterFinancerAsync(
            new RegisterFinancerRequest(
                "Suresh Patel",
                "Patel Finance",
                "+919876543210",
                "suresh@example.com",
                "Ahmedabad",
                "Gujarat"
            ),
            default
        );

        Assert.NotEqual(Guid.Empty, challenge.ChallengeId);
        Assert.Equal(1, await db.Financers.CountAsync());
        var user = await db.Users.Include(x => x.UserRoles).ThenInclude(x => x.Role).SingleAsync();
        Assert.Equal(AccountStatus.Pending, user.Status);
        Assert.Equal("+919876543210", user.Phone);
        Assert.Contains(user.UserRoles, x => x.Role.Name == "FinancerOwner");

        var completion = await service.VerifyRegistrationOtpAsync(
            new VerifyOtpRequest(challenge.ChallengeId, sender.Otp!),
            default
        );
        Assert.Equal("+919876543210", completion.UserId);
        Assert.Equal("+919876543210", sender.CredentialUserId);
        Assert.False(string.IsNullOrWhiteSpace(sender.CredentialPassword));
        Assert.Equal(AccountStatus.Active, user.Status);
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            new PasswordHasher<UserAccount>().VerifyHashedPassword(user, user.PasswordHash, sender.CredentialPassword!)
        );
        var tokens = await service.LoginFinancerAsync(
            new LoginRequest(user.Phone, sender.CredentialPassword!, "financer"),
            "127.0.0.1",
            default
        );
        Assert.Contains("FinancerOwner", tokens.User.Roles);

        var platform = new PlatformService(
            db,
            new PasswordHasher<UserAccount>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["DataProtection:Key"] = "integration-key" }
                )
                .Build(),
            sender
        );
        var actor = new CurrentUser(
            user.Id,
            user.FinancerId,
            ["FinancerOwner"],
            ["dashboard.read", "payments.read"]
        );
        db.ChangeTracker.Clear();
        var profile = await platform.GetMyProfileAsync(actor, default);
        Assert.Contains("FinancerOwner", JsonSerializer.Serialize(profile));
        var updatedProfile = await platform.UpdateMyProfileAsync(
            new UpdateMyProfileRequest(
                "Suresh Patel",
                "Patel Finance",
                "+919876543210",
                "suresh@example.com",
                "Ahmedabad",
                "Gujarat",
                null
            ),
            actor,
            default
        );
        Assert.Contains("FinancerOwner", JsonSerializer.Serialize(updatedProfile));
        Assert.NotNull(await platform.GetDashboardAsync(false, new PageQuery(), actor, default));
        Assert.NotNull(await platform.GetSchedulesAsync(new PageQuery(), actor, default));
    }

    [Fact]
    public async Task Pending_registration_can_be_resumed_with_the_same_email()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinancerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FinancerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Roles.Add(new Role { Name = "FinancerOwner", IsSystem = true });
        await db.SaveChangesAsync();

        var sender = new TestAuthMessageSender();
        var service = new AuthService(
            db,
            Options.Create(new JwtOptions { Key = "integration-test-key-at-least-32-characters" }),
            Options.Create(new OtpOptions { MinimumResendSeconds = 60 }),
            new PasswordHasher<UserAccount>(),
            sender
        );
        var request = new RegisterFinancerRequest(
            "Suresh Patel", "Patel Finance", "+919876543210",
            "suresh@example.com", "Ahmedabad", "Gujarat"
        );

        var first = await service.RegisterFinancerAsync(request, default);
        var immediateRetry = await service.RegisterFinancerAsync(request, default);

        Assert.Equal(first.ChallengeId, immediateRetry.ChallengeId);
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.Financers.CountAsync());
        Assert.Equal(1, await db.OtpChallenges.CountAsync());

        var changedMobile = await service.RegisterFinancerAsync(
            request with { Mobile = "+919999999999" },
            default
        );
        Assert.Equal(first.ChallengeId, changedMobile.ChallengeId);
        var pendingUser = await db.Users.Include(x => x.Financer).SingleAsync();
        Assert.Equal("+919999999999", pendingUser.Phone);
        Assert.Equal("+919999999999", pendingUser.Financer!.Phone);
    }

    [Fact]
    public async Task Swagger_exposes_all_financer_UI_supporting_routes()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var json = await client.GetStringAsync("/swagger/v1/swagger.json");
        string[] routes =
        [
            "/api/v1/auth/register/financer",
            "/api/v1/auth/password/change",
            "/api/v1/profile",
            "/api/v1/payment-schedules",
            "/api/v1/customers/{id}",
            "/api/v1/collections/{loanId}/reminders",
            "/api/v1/reports/{name}",
        ];
        foreach (var route in routes)
            Assert.Contains(route, json);
    }
}

internal sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "INRFS.Financer.IntegrationTests";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class TestAuthMessageSender : IAuthMessageSender
{
    public string? Otp { get; private set; }
    public string? CredentialUserId { get; private set; }
    public string? CredentialPassword { get; private set; }

    public Task SendOtpAsync(string destination, string code, string purpose, CancellationToken ct)
    {
        Otp = code;
        return Task.CompletedTask;
    }
    public Task SendPasswordResetAsync(string destination, string token, CancellationToken ct) => Task.CompletedTask;
    public Task SendWelcomeCredentialsAsync(string destination, string userId, string password, CancellationToken ct)
    {
        CredentialUserId = userId;
        CredentialPassword = password;
        return Task.CompletedTask;
    }
}
