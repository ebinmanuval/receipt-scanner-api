using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Auth;

public static class AuthSetup
{
    private const string UserItemKey = "ReceiptScanner.User";

    /// <summary>
    /// JWT bearer as the default scheme, plus the API-key scheme. Every
    /// controller requires a signed-in user unless marked [AllowAnonymous]
    /// (the global AuthorizeFilter is added in Program.cs).
    /// </summary>
    public static IServiceCollection AddApiAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.AddOptions<JwtOptions>()
            .Bind(config.GetSection(JwtOptions.Section))
            .Validate(o => (o.SigningKey ?? "").Length >= 32, "Jwt:SigningKey must be set and at least 32 characters.")
            .Validate(o => env.IsDevelopment() || o.SigningKey != JwtOptions.DevelopmentKey,
                "Jwt:SigningKey is the development key. Set a real one outside Development.")
            .Validate(o => o.AccessMinutes > 0 && o.RefreshDays > 0, "Jwt lifetimes must be positive.")
            .ValidateOnStart();
        services.AddOptions<ApiKeyOptions>().Bind(config.GetSection(ApiKeyOptions.Section));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;   // keep "user_id" / "token_type" as-is
                o.Events = BearerEvents();
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { });

        // Validation parameters need the bound JwtOptions, so they're set
        // once options are available rather than at registration time.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((o, jwt) => o.TokenValidationParameters = TokenService.ValidationParameters(jwt.Value));

        return services;
    }

    /// <summary>The signed-in user, loaded once per request when the token was validated. Null for API-key requests.</summary>
    public static User? CurrentUser(this HttpContext http) => http.Items[UserItemKey] as User;

    private static JwtBearerEvents BearerEvents() => new()
    {
        // Like SimpleJWT's JWTAuthentication: only access tokens, and the user
        // must still exist and be active.
        OnTokenValidated = async ctx =>
        {
            var principal = ctx.Principal!;
            if (principal.FindFirst(TokenService.ClaimTokenType)?.Value != TokenService.Access)
            {
                ctx.Fail("Not an access token.");
                return;
            }
            if (!TokenService.TryInt(principal.FindFirst(TokenService.ClaimUserId)?.Value, out var userId))
            {
                ctx.Fail("Token has no user id.");
                return;
            }

            var db = ctx.HttpContext.RequestServices.GetRequiredService<ReceiptScannerDbContext>();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ctx.HttpContext.RequestAborted);
            if (user is null || !user.IsActive)
            {
                ctx.Fail("User not found or inactive.");
                return;
            }
            ctx.HttpContext.Items[UserItemKey] = user;
        },

        // 401 in the standard envelope. API.md: no token, or an expired or
        // invalid one, is not_authenticated — the client refreshes and retries.
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            if (ctx.Response.HasStarted) return;

            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"api\"";
            var message = ctx.AuthenticateFailure is null
                ? "Authentication credentials were not provided."
                : "Given token not valid for any token type.";
            await ApiErrors.WriteAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, "not_authenticated", message);
        },

        OnForbidden = ctx =>
            ApiErrors.WriteAsync(ctx.HttpContext, StatusCodes.Status403Forbidden, "permission_denied", ApiErrors.FallbackMessage(403)),
    };
}
