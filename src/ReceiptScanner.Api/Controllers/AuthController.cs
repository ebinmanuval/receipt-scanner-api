using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Api.Auth;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Core.Auth;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Controllers;

/// <summary>
/// JWT login for the mobile app. Port of accounts/views.py; shapes from
/// API.md#authentication.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    ReceiptScannerDbContext db,
    TokenService tokens,
    TimeProvider clock,
    ILogger<AuthController> logger) : ControllerBase
{
    public sealed record LoginRequest(string? Username, string? Password);
    public sealed record RefreshRequest(string? Refresh);

    public sealed record UserDto(int Id, string Username, string Email, string FirstName, string LastName, bool IsStaff)
    {
        public static UserDto From(User u) => new(u.Id, u.Username, u.Email, u.FirstName, u.LastName, u.IsStaff);
    }

    public sealed record LoginResponse(
        string Status, string Access, string Refresh, int AccessExpiresIn, int RefreshExpiresIn, string TokenType, UserDto User);

    public sealed record RefreshResponse(string Status, string Access, string TokenType, string Refresh);
    public sealed record MeResponse(string Status, UserDto User);
    public sealed record MessageResponse(string Status, string Message);

    /// <summary>POST /api/auth/login/ — username and password for a token pair.</summary>
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? body, CancellationToken ct)
    {
        // DRF CharField: trims whitespace, rejects missing and blank.
        var username = body?.Username?.Trim();
        var password = body?.Password?.Trim();

        var errors = new Dictionary<string, string[]>();
        if (Missing(body?.Username, username) is { } u) errors["username"] = [u];
        if (Missing(body?.Password, password) is { } p) errors["password"] = [p];
        if (errors.Count > 0)
            throw new ApiException(StatusCodes.Status400BadRequest, "validation_error",
                "Username and password are required.", errors);

        var user = await db.Users.FirstOrDefaultAsync(x => x.Username == username, ct);
        if (user is null)
        {
            DjangoPasswordHasher.SimulateVerify(password!);   // same timing as a real check
        }

        // Deliberately one answer for unknown user, wrong password and
        // inactive account, so the endpoint can't be used to find usernames.
        if (user is null || !DjangoPasswordHasher.Verify(password!, user.Password) || !user.IsActive)
        {
            logger.LogWarning("Failed login for {Username} from {Ip}", username, HttpContext.Connection.RemoteIpAddress);
            throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_credentials", "Incorrect username or password.");
        }

        // Django upgrades the hash on login when the iteration count is behind.
        if (DjangoPasswordHasher.NeedsRehash(user.Password))
            user.Password = DjangoPasswordHasher.Hash(password!);
        user.LastLogin = clock.GetUtcNow().UtcDateTime;   // SIMPLE_JWT UPDATE_LAST_LOGIN

        var issued = await tokens.IssueAsync(user, ct);   // saves the user changes too
        logger.LogInformation("Login: {Username}", user.Username);

        return Ok(new LoginResponse("success", issued.Access, issued.Refresh,
            issued.AccessExpiresIn, issued.RefreshExpiresIn, "Bearer", UserDto.From(user)));
    }

    /// <summary>POST /api/auth/refresh/ — swap a refresh token for a new pair. The old one stops working.</summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Refresh))
            throw ApiException.BadRequest("missing_refresh_token", "A 'refresh' token is required.");

        var rotated = await tokens.RotateAsync(body.Refresh, ct)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "token_not_valid",
                "That refresh token is expired or has been used. Log in again.");

        return Ok(new RefreshResponse("success", rotated.Tokens.Access, "Bearer", rotated.Tokens.Refresh));
    }

    /// <summary>POST /api/auth/logout/ — revoke the refresh token. Needs the access token too.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Refresh))
            throw ApiException.BadRequest("missing_refresh_token", "Send the refresh token as 'refresh' to log out.");

        var user = HttpContext.CurrentUser()!;
        await tokens.RevokeAsync(body.Refresh, user.Id, ct);
        logger.LogInformation("Logout: {Username}", user.Username);

        return Ok(new MessageResponse("success", "Logged out."));
    }

    /// <summary>GET /api/auth/me/ — who this token belongs to.</summary>
    [HttpGet("me")]
    public IActionResult Me() => Ok(new MeResponse("success", UserDto.From(HttpContext.CurrentUser()!)));

    private static string? Missing(string? raw, string? trimmed) =>
        raw is null ? "This field is required."
        : trimmed!.Length == 0 ? "This field may not be blank."
        : null;
}
