namespace ReceiptScanner.Api.Auth;

/// <summary>Config section <c>Jwt</c>. Lifetimes match Django's SIMPLE_JWT defaults.</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>
    /// The value in appsettings.Development.json. Refused outside Development
    /// so a deployed app can't run with a key that's in git.
    /// </summary>
    public const string DevelopmentKey = "dev-only-signing-key-change-me-in-production-000000";

    /// <summary>HS256 key, at least 32 characters. Set via user-secrets or env (<c>Jwt__SigningKey</c>).</summary>
    public string SigningKey { get; set; } = "";
    public int AccessMinutes { get; set; } = 60;
    public int RefreshDays { get; set; } = 30;

    public TimeSpan AccessLifetime => TimeSpan.FromMinutes(AccessMinutes);
    public TimeSpan RefreshLifetime => TimeSpan.FromDays(RefreshDays);
}

/// <summary>Config section <c>Auth</c>.</summary>
public sealed class ApiKeyOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Shared secret for the iOS Shortcut (<c>X-API-Key</c>), accepted on
    /// POST /api/receipts/ only. Django's RECEIPT_API_KEY.
    /// </summary>
    public string ApiKey { get; set; } = "";
}
