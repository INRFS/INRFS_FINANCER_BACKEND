using INRFS.Financer.Application;
using INRFS.Financer.Domain;
using INRFS.Financer.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace INRFS.Financer.IntegrationTests;

public sealed class FinancerPortalCoverageTests
{
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
                .Build()
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
