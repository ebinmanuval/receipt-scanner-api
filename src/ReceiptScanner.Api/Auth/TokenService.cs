using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Auth;

public sealed record IssuedTokens(string Access, string Refresh, int AccessExpiresIn, int RefreshExpiresIn);

/// <summary>
/// Issues, rotates and revokes JWT pairs. HS256, same claim names as
/// SimpleJWT (<c>token_type</c>, <c>jti</c>, <c>user_id</c>, <c>iat</c>,
/// <c>exp</c>). Every refresh token has a row in auth_refresh_tokens; a
/// refresh token without a live row is refused even if its signature is good.
/// </summary>
public sealed class TokenService(ReceiptScannerDbContext db, IOptions<JwtOptions> options, TimeProvider clock)
{
    public const string ClaimTokenType = "token_type";
    public const string ClaimUserId = "user_id";
    public const string Access = "access";
    public const string Refresh = "refresh";

    private static readonly JsonWebTokenHandler Handler = new();
    private readonly JwtOptions _jwt = options.Value;

    /// <summary>Shared by the bearer handler (access tokens) and refresh-token checks.</summary>
    public static TokenValidationParameters ValidationParameters(JwtOptions jwt) => new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(jwt),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.Zero,   // SimpleJWT has no leeway by default
    };

    /// <summary>New pair for a user who just logged in. Saves the refresh row.</summary>
    public async Task<IssuedTokens> IssueAsync(User user, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var refreshJti = NewJti();

        db.RefreshTokens.Add(new RefreshToken
        {
            Jti = refreshJti,
            UserId = user.Id,
            CreatedAt = now,
            ExpiresAt = now + _jwt.RefreshLifetime,
        });
        await db.SaveChangesAsync(ct);

        return new IssuedTokens(
            Access: Create(user.Id, Access, NewJti(), now, _jwt.AccessLifetime),
            Refresh: Create(user.Id, Refresh, refreshJti, now, _jwt.RefreshLifetime),
            AccessExpiresIn: (int)_jwt.AccessLifetime.TotalSeconds,
            RefreshExpiresIn: (int)_jwt.RefreshLifetime.TotalSeconds);
    }

    /// <summary>
    /// Swap a refresh token for a new pair, revoking the old one. Null when the
    /// token is invalid, expired, already used or revoked, or the user is gone
    /// or inactive. The revoke is a single conditional UPDATE, so two
    /// concurrent refreshes with the same token can't both succeed.
    /// </summary>
    public async Task<(User User, IssuedTokens Tokens)?> RotateAsync(string refreshToken, CancellationToken ct = default)
    {
        var claims = await ReadRefreshAsync(refreshToken);
        if (claims is not { } c) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var revoked = await db.RefreshTokens
            .Where(t => t.Jti == c.Jti && t.UserId == c.UserId && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        if (revoked != 1) return null;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == c.UserId, ct);
        if (user is null || !user.IsActive) return null;   // rolls back on dispose; token stays unusable either way

        var tokens = await IssueAsync(user, ct);
        await tx.CommitAsync(ct);
        return (user, tokens);
    }

    /// <summary>
    /// Logout. Revokes the token if it's a valid refresh token belonging to
    /// <paramref name="userId"/>; otherwise does nothing (already expired or
    /// revoked is the goal anyway, as in Django).
    /// </summary>
    public async Task RevokeAsync(string refreshToken, int userId, CancellationToken ct = default)
    {
        var claims = await ReadRefreshAsync(refreshToken);
        if (claims is not { } c || c.UserId != userId) return;

        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.Jti == c.Jti && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private async Task<(string Jti, int UserId)?> ReadRefreshAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parameters = ValidationParameters(_jwt);
        parameters.LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return expires is { } exp && exp > now && (notBefore is not { } nbf || nbf <= now);
        };

        var result = await Handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid) return null;

        if (!result.Claims.TryGetValue(ClaimTokenType, out var type) || type as string != Refresh) return null;
        if (!result.Claims.TryGetValue(JwtRegisteredClaimNames.Jti, out var jti) || jti is not string jtiText) return null;
        if (!result.Claims.TryGetValue(ClaimUserId, out var uid) || !TryInt(uid, out var userId)) return null;

        return (jtiText, userId);
    }

    private string Create(int userId, string type, string jti, DateTime now, TimeSpan lifetime) =>
        Handler.CreateToken(new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                [ClaimTokenType] = type,
                [JwtRegisteredClaimNames.Jti] = jti,
                [ClaimUserId] = userId,
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            SigningCredentials = new SigningCredentials(SigningKey(_jwt), SecurityAlgorithms.HmacSha256),
        });

    private static SymmetricSecurityKey SigningKey(JwtOptions jwt) => new(Encoding.UTF8.GetBytes(jwt.SigningKey));

    private static string NewJti() => Guid.NewGuid().ToString("N");

    internal static bool TryInt(object? value, out int result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: result = (int)l; return true;
            case string s: return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            default: result = 0; return false;
        }
    }
}
