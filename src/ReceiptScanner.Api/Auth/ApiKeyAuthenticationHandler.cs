using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ReceiptScanner.Api.Auth;

/// <summary>
/// <c>X-API-Key</c> header auth for the iOS Shortcut, which can't hold a login
/// session. Only endpoints marked <see cref="RequireUserOrApiKeyAttribute"/>
/// accept it (POST /api/receipts/), never the rest of the API.
/// </summary>
/// <remarks>
/// Port of receipts/permissions.py HasAPIKeyOrIsAuthenticated, with one
/// tightening: Django treats an empty RECEIPT_API_KEY as "no check". Here that
/// only applies in Development; anywhere else an unset key fails closed.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiKeyOptions> keys,
    IHostEnvironment env)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string Header = "X-API-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = keys.Value.ApiKey ?? "";
        var provided = Request.Headers[Header].ToString();

        if (expected.Length == 0)
        {
            return Task.FromResult(env.IsDevelopment()
                ? AuthenticateResult.Success(Ticket("api-key (development, no key set)"))
                : AuthenticateResult.NoResult());
        }

        if (provided.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());

        return Task.FromResult(Matches(provided, expected)
            ? AuthenticateResult.Success(Ticket("api-key"))
            : AuthenticateResult.Fail("Invalid X-API-Key header."));
    }

    /// <summary>The bearer scheme writes the 401 body; this one stays quiet so it isn't written twice.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => Task.CompletedTask;

    /// <summary>Constant-time, including for keys of different lengths (both sides are hashed first).</summary>
    internal static bool Matches(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private AuthenticationTicket Ticket(string name) =>
        new(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim("auth", "api_key")], SchemeName)), SchemeName);
}
