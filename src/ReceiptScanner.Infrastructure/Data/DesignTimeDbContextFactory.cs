using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ReceiptScanner.Infrastructure.Data;

/// <summary>
/// Used by <c>dotnet ef</c>. Reads the connection string from the same places
/// the API does in Development: the API's user secrets, then the
/// <c>ConnectionStrings__Default</c> environment variable (which wins).
/// Generating a migration needs no live database; the connection is only used
/// by commands that connect (database update, migrations list).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ReceiptScannerDbContext>
{
    /// <summary>Must match &lt;UserSecretsId&gt; in ReceiptScanner.Api.csproj.</summary>
    private const string ApiUserSecretsId = "receipt-scanner-api";

    public ReceiptScannerDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(ApiUserSecretsId)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "No connection string. Set it with `dotnet user-secrets set \"ConnectionStrings:Default\" \"...\" " +
                "--project src/ReceiptScanner.Api`, or export ConnectionStrings__Default.");

        var builder = new DbContextOptionsBuilder<ReceiptScannerDbContext>();
        DependencyInjection.ConfigureDbContext(builder, connectionString);
        return new ReceiptScannerDbContext(builder.Options);
    }
}
