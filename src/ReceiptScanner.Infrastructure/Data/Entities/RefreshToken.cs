namespace ReceiptScanner.Infrastructure.Data.Entities;

/// <summary>
/// Table <c>auth_refresh_tokens</c> (new, EF-managed). One row per refresh
/// token issued. A token is usable while its row exists, isn't revoked and
/// hasn't expired. Rotation revokes the old row and inserts a new one;
/// logout revokes. Replaces SimpleJWT's token_blacklist tables.
/// </summary>
public class RefreshToken
{
    public long Id { get; set; }

    /// <summary>The token's <c>jti</c> claim: 32 hex chars.</summary>
    public string Jti { get; set; } = "";

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
