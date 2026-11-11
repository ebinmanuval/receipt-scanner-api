using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ReceiptScanner.Infrastructure.Data;

namespace ReceiptScanner.Api.Cli;

/// <summary>
/// <c>dotnet run --project src/ReceiptScanner.Api -- db &lt;command&gt;</c>
/// <list type="bullet">
/// <item><c>mark-baseline</c>: on a database Django already built, record the
/// InitialBaseline migration as applied without running it.</item>
/// <item><c>verify-schema</c>: check every mapped table/column exists in the
/// connected database. Run it against a copy of production.</item>
/// </list>
/// </summary>
public static class DbCommands
{
    public const string InitialBaselineSuffix = "_InitialBaseline";

    public static bool Matches(string[] args) => args.Length >= 1 && args[0] == "db";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReceiptScannerDbContext>();
        var sub = args.Length >= 2 ? args[1] : "";

        switch (sub)
        {
            case "mark-baseline": return await MarkBaselineAsync(db, dryRun: args.Contains("--dry-run"));
            case "verify-schema": return await VerifySchemaAsync(db);
            default:
                Console.Error.WriteLine("Usage: db mark-baseline [--dry-run] | db verify-schema");
                return 2;
        }
    }

    /// <summary>
    /// Insert the baseline row into __EFMigrationsHistory, guarded so it only
    /// ever happens on a database that already has the Django tables and no EF
    /// history. Safe to run twice.
    /// </summary>
    private static async Task<int> MarkBaselineAsync(ReceiptScannerDbContext db, bool dryRun)
    {
        var baseline = db.Database.GetMigrations().FirstOrDefault(m => m.EndsWith(InitialBaselineSuffix, StringComparison.Ordinal));
        if (baseline is null)
        {
            Console.Error.WriteLine("No *_InitialBaseline migration in the assembly. Generate it first (see README).");
            return 1;
        }

        if (!await TableExistsAsync(db, "receipts") || !await TableExistsAsync(db, "bank_transactions"))
        {
            Console.Error.WriteLine("This database has no Django tables. On a fresh database run `dotnet ef database update` instead.");
            return 1;
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        if (applied.Contains(baseline))
        {
            Console.WriteLine($"{baseline} is already recorded as applied. Nothing to do.");
            return 0;
        }
        if (applied.Count > 0)
        {
            Console.Error.WriteLine($"__EFMigrationsHistory already has {applied.Count} row(s) but not the baseline. Refusing to guess.");
            return 1;
        }

        var history = db.GetService<IHistoryRepository>();
        var statements = new[]
        {
            history.GetCreateIfNotExistsScript(),
            history.GetInsertScript(new HistoryRow(baseline, ProductInfo.GetVersion())),
        };

        if (dryRun)
        {
            Console.WriteLine(string.Join(Environment.NewLine, statements));
            return 0;
        }

        foreach (var sql in statements)
            await db.Database.ExecuteSqlRawAsync(sql);
        Console.WriteLine($"Recorded {baseline} as applied. `dotnet ef migrations list` should now show it as applied.");
        return 0;
    }

    /// <summary>Every mapped (table, column) must exist. Extra DB columns are listed, not failed.</summary>
    private static async Task<int> VerifySchemaAsync(ReceiptScannerDbContext db)
    {
        var actual = new HashSet<(string Table, string Column)>();
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                actual.Add((reader.GetString(0), reader.GetString(1)));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var missing = 0;
        foreach (var entity in db.Model.GetEntityTypes().OrderBy(e => e.GetTableName()))
        {
            var table = entity.GetTableName()!;
            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entity.GetSchema());
            var mapped = entity.GetProperties().Select(p => p.GetColumnName(store)!).ToHashSet();

            foreach (var column in mapped.Where(c => !actual.Contains((table, c))))
            {
                Console.WriteLine($"MISSING  {table}.{column}");
                missing++;
            }
            foreach (var extra in actual.Where(a => a.Table == table && !mapped.Contains(a.Column)).Select(a => a.Column))
                Console.WriteLine($"unmapped {table}.{extra}");
        }

        Console.WriteLine(missing == 0 ? "OK: every mapped column exists." : $"{missing} mapped column(s) missing.");
        return missing == 0 ? 0 : 1;
    }

    private static async Task<bool> TableExistsAsync(ReceiptScannerDbContext db, string table)
    {
        DbConnection conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t";
            var p = cmd.CreateParameter();
            p.ParameterName = "@t";
            p.Value = table;
            cmd.Parameters.Add(p);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
