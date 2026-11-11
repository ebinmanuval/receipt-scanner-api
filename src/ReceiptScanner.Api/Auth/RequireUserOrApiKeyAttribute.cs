using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ReceiptScanner.Api.Auth;

/// <summary>
/// A signed-in user (Bearer) or the shared X-API-Key. Port of
/// HasAPIKeyOrIsAuthenticated, used only on POST /api/receipts/ for the iOS
/// Shortcut. Pair it with [AllowAnonymous], which switches off the global
/// login requirement so this filter makes the decision.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireUserOrApiKeyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;

        // The default (Bearer) scheme already ran in UseAuthentication.
        if (http.User.Identity?.IsAuthenticated == true) return;

        var apiKey = await http.AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName);
        if (apiKey.Succeeded)
        {
            http.User = apiKey.Principal!;
            return;
        }

        // The bearer challenge writes the standard 401 envelope.
        context.Result = new ChallengeResult(JwtBearerDefaults.AuthenticationScheme);
    }
}
