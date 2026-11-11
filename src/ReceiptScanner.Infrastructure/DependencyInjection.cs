using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.EntityFrameworkCore.Extensions;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Extraction;
using ReceiptScanner.Infrastructure.Images;
using ReceiptScanner.Infrastructure.Storage;

namespace ReceiptScanner.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default") ?? "";

        services.AddDbContext<ReceiptScannerDbContext>(options =>
            ConfigureDbContext(options, connectionString));

        services.AddOptions<MediaOptions>()
            .Bind(config.GetSection(MediaOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Root), "Media:Root is required.")
            .ValidateOnStart();
        services.AddSingleton<IMediaStorage, MediaStorage>();

        services.AddOptions<ImageOptions>().Bind(config.GetSection(ImageOptions.Section));
        services.AddSingleton<IImageProcessor, ImageProcessor>();

        services.AddOptions<ExtractionOptions>().Bind(config.GetSection(ExtractionOptions.Section));
        services.AddSingleton<IExtractionQueue, CeleryExtractionQueue>();

        return services;
    }

    /// <summary>
    /// Oracle's provider (MySql.EntityFrameworkCore). Configuring it doesn't
    /// open a connection, so the app and the design-time tools start without
    /// a database.
    /// </summary>
    /// <remarks>
    /// No EnableRetryOnFailure: reconcile (Phase 3) uses explicit transactions
    /// with row locks, which a retrying strategy would force through
    /// CreateExecutionStrategy everywhere.
    /// </remarks>
    public static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseMySQL(connectionString, mysql =>
            mysql.MigrationsAssembly(typeof(ReceiptScannerDbContext).Assembly.FullName));
}
