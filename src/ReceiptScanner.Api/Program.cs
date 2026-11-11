using ReceiptScanner.Api.Banking;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Authorization;
using ReceiptScanner.Api.Auth;
using ReceiptScanner.Api.Cli;
using ReceiptScanner.Api.Errors;
using ReceiptScanner.Api.Json;
using ReceiptScanner.Api.Receipts;
using ReceiptScanner.Infrastructure;

// `dotnet run -- db <command>` / `-- create-user`: maintenance commands share
// the app's config and DI, but don't start the web server.
var isCli = CliCommands.Matches(args);

var builder = WebApplication.CreateBuilder(isCli ? [] : args);

builder.Services
    // Every endpoint needs a signed-in user unless it says [AllowAnonymous],
    // like DRF's DEFAULT_PERMISSION_CLASSES = IsAuthenticated.
    .AddControllers(o => o.Filters.Add(new AuthorizeFilter()))
    .AddJsonOptions(o => ApiJson.Configure(o.JsonSerializerOptions))
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ApiErrors.FromModelState);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiAuth(builder.Configuration, builder.Environment);
builder.Services.AddScoped<ReceiptWriter>();
builder.Services.Configure<BankingOptions>(builder.Configuration.GetSection(BankingOptions.Section));
builder.Services.AddScoped<Reconciler>();
builder.Services.AddScoped<TransferPairing>();
builder.Services.AddScoped<OfxImporter>();

// nginx in front: trust X-Forwarded-For/Proto from the local proxy so
// pagination links say https://accounts.ebinmanuval.in, not http://127.0.0.1.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
});

// Uploads are capped at 15 MB by the endpoints (413 image_too_large); this is
// the outer limit, matching nginx client_max_body_size.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 20 * 1024 * 1024);

var app = builder.Build();

if (isCli)
    return await CliCommands.RunAsync(app.Services, args);

app.UseForwardedHeaders();
app.UseMiddleware<ApiErrorMiddleware>();
app.UseStatusCodePages(ApiStatusCodePages.WriteAsync);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

await app.RunAsync();
return 0;

/// <summary>Visible to WebApplicationFactory in the tests.</summary>
public partial class Program;
