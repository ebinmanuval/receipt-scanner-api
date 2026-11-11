using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReceiptScanner.Core.Auth;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;
using ReceiptScanner.Infrastructure.Extraction;

namespace ReceiptScanner.Tests.Api;

/// <summary>
/// The real app, with MySQL swapped for an in-memory SQLite database that
/// lives as long as the factory. The schema comes from the EF model.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key";
    public const string Password = "correct horse battery";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    /// <summary>A fresh media folder per factory, deleted on dispose.</summary>
    public string MediaRoot { get; } = Path.Combine(Path.GetTempPath(), "rs-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Records what would have gone to the extraction worker.</summary>
    public FakeExtractionQueue Queue { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Added last, so these win over appsettings*.json and user secrets.
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-test-signing-key-0123456789",
            ["Auth:ApiKey"] = ApiKey,
            ["Media:Root"] = MediaRoot,
            ["TimeZone"] = "UTC",
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExtractionQueue>();
            services.AddSingleton<IExtractionQueue>(Queue);

            // AddDbContext registers both the options and a configuration
            // callback (UseMySQL); remove both or the two providers clash.
            services.RemoveAll<DbContextOptions<ReceiptScannerDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ReceiptScannerDbContext>>();

            _connection.Open();
            services.AddDbContext<ReceiptScannerDbContext>(o => o.UseSqlite(_connection));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ReceiptScannerDbContext>().Database.EnsureCreated();
        return host;
    }

    /// <summary>
    /// Add a user. Hashed with a low iteration count so tests stay fast; the
    /// first login upgrades it, as Django would. Returns the existing user if the name is taken.
    /// </summary>
    public async Task<User> AddUserAsync(string username, string password = Password, bool active = true, string? hash = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReceiptScannerDbContext>();
        // Tests share one database per class; a theory may add the same user twice.
        if (await db.Users.FirstOrDefaultAsync(u => u.Username == username) is { } existing) return existing;

        var user = new User
        {
            Username = username,
            Email = $"{username}@example.com",
            IsActive = active,
            IsStaff = true,
            Password = hash ?? DjangoPasswordHasher.Hash(password, iterations: 1000),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<T> WithDbAsync<T>(Func<ReceiptScannerDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ReceiptScannerDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _connection.Dispose();
        try { Directory.Delete(MediaRoot, recursive: true); } catch (IOException) { }

    }
}

/// <summary>Stands in for Redis + the Celery worker.</summary>
public sealed class FakeExtractionQueue : IExtractionQueue
{
    private readonly List<long> _queued = [];

    /// <summary>Simulate the broker being unreachable.</summary>
    public bool Down { get; set; }

    public IReadOnlyList<long> Queued { get { lock (_queued) return [.. _queued]; } }

    public Task<string?> EnqueueAsync(long scanId, CancellationToken ct = default)
    {
        if (Down) return Task.FromResult<string?>(null);
        lock (_queued) _queued.Add(scanId);
        return Task.FromResult<string?>($"task-{scanId}");
    }
}
