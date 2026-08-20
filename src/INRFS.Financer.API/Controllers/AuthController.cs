using INRFS.Financer.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace INRFS.Financer.API.Controllers;

public sealed record OptionalRefreshRequest(string? RefreshToken = null);

[Route("api/v1/auth")]
public sealed class AuthController(IAuthService service, ICurrentUserAccessor current, IConfiguration configuration, IWebHostEnvironment environment)
    : ApiControllerBase
{
    private const string RefreshCookieName = "inrfs_refresh";

    private void SetRefreshCookie(string token)
    {
        var days = configuration.GetValue<int?>("Jwt:RefreshTokenDays") ?? 14;
        Response.Cookies.Append(
            RefreshCookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !environment.IsDevelopment(),
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddDays(days),
                Path = "/api/v1/auth",
                IsEssential = true,
            }
        );
    }

    private string? ReadRefreshToken(OptionalRefreshRequest? request) =>
        string.IsNullOrWhiteSpace(request?.RefreshToken)
            ? Request.Cookies[RefreshCookieName]
            : request.RefreshToken;

    [AllowAnonymous, HttpPost("register/financer")]
    public async Task<ActionResult<ApiResult<AuthChallengeResponse>>> RegisterFinancer(
        RegisterFinancerRequest request,
        CancellationToken ct
    ) => OkResult(await service.RegisterFinancerAsync(request, ct));

    [AllowAnonymous, HttpPost("login")]
    public async Task<ActionResult<ApiResult<AuthChallengeResponse>>> Login(
        LoginRequest r,
        CancellationToken ct
    ) => OkResult(await service.LoginAsync(r, ct));

    [AllowAnonymous, HttpPost("login/financer")]
    public async Task<ActionResult<ApiResult<AuthTokenResponse>>> LoginFinancer(
        LoginRequest r,
        CancellationToken ct
    )
    {
        var tokens = await service.LoginFinancerAsync(r, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        SetRefreshCookie(tokens.RefreshToken);
        return OkResult(tokens);
    }

    [AllowAnonymous, HttpPost("otp/request")]
    public async Task<ActionResult<ApiResult<AuthChallengeResponse>>> RequestOtp(
        OtpRequest r,
        CancellationToken ct
    ) => OkResult(await service.RequestOtpAsync(r, ct));

    [AllowAnonymous, HttpPost("otp/verify")]
    public async Task<ActionResult<ApiResult<AuthTokenResponse>>> Verify(
        VerifyOtpRequest r,
        CancellationToken ct
    )
    {
        var tokens = await service.VerifyOtpAsync(r, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        SetRefreshCookie(tokens.RefreshToken);
        return OkResult(tokens);
    }

    [AllowAnonymous, HttpPost("otp/verify-registration")]
    public async Task<ActionResult<ApiResult<RegistrationCompletionResponse>>> VerifyRegistration(
        VerifyOtpRequest r,
        CancellationToken ct
    ) => OkResult(await service.VerifyRegistrationOtpAsync(r, ct));

    [AllowAnonymous, HttpPost("refresh")]
    public async Task<ActionResult<ApiResult<AuthTokenResponse>>> Refresh(
        OptionalRefreshRequest? r,
        CancellationToken ct
    )
    {
        var refreshToken = ReadRefreshToken(r) ?? throw new DomainException("Refresh session is missing.", 401);
        var tokens = await service.RefreshAsync(new RefreshRequest(refreshToken), HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        SetRefreshCookie(tokens.RefreshToken);
        return OkResult(tokens);
    }

    [AllowAnonymous, HttpPost("password/forgot")]
    public async Task<ActionResult<ApiResult<object>>> Forgot(
        ForgotPasswordRequest r,
        CancellationToken ct
    )
    {
        await service.ForgotPasswordAsync(r, ct);
        return OkResult<object>(
            new { },
            "If the account exists, reset instructions have been sent."
        );
    }

    [AllowAnonymous, HttpPost("password/reset")]
    public async Task<ActionResult<ApiResult<object>>> Reset(
        ResetPasswordRequest r,
        CancellationToken ct
    )
    {
        await service.ResetPasswordAsync(r, ct);
        return OkResult<object>(new { }, "Password reset successfully.");
    }

    [Authorize, HttpPost("revoke")]
    public async Task<ActionResult<ApiResult<object>>> Revoke(
        OptionalRefreshRequest? r,
        CancellationToken ct
    )
    {
        var refreshToken = ReadRefreshToken(r);
        if (!string.IsNullOrWhiteSpace(refreshToken))
            await service.RevokeAsync(new RefreshRequest(refreshToken), ct);
        Response.Cookies.Delete(RefreshCookieName, new CookieOptions { Path = "/api/v1/auth" });
        return OkResult<object>(new { }, "Session revoked.");
    }

    [Authorize, HttpGet("me")]
    public ActionResult<ApiResult<CurrentUser>> Me() => OkResult(current.User);

    [Authorize, HttpPost("password/change")]
    public async Task<ActionResult<ApiResult<object>>> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken ct
    )
    {
        await service.ChangePasswordAsync(current.User.UserId, request, ct);
        return OkResult<object>(new { }, "Password changed successfully.");
    }
}
