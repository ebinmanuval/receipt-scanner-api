using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReceiptScanner.Infrastructure;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Tests.Infrastructure;

/// <summary>
/// Checks the EF model against the Django schema. Building the model needs no
/// database connection.
/// </summary>
public class ModelMappingTests
{
    private static ReceiptScannerDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<ReceiptScannerDbContext>();
        DependencyInjection.ConfigureDbContext(builder, "Server=127.0.0.1;Database=unused;User Id=x;Password=y;");
        return new ReceiptScannerDbContext(builder.Options);
    }

    /// <summary>The design-time model keeps column types, precision and index names.</summary>
    private static IModel Model(ReceiptScannerDbContext db) => db.GetService<IDesignTimeModel>().Model;

    private static string Column<T>(ReceiptScannerDbContext db, string property)
    {
        var e = Model(db).FindEntityType(typeof(T))!;
        return e.FindProperty(property)!.GetColumnName(StoreObjectIdentifier.Table(e.GetTableName()!, null))!;
    }

    [Theory]
    [InlineData(typeof(Receipt), "receipts")]
    [InlineData(typeof(ReceiptItem), "receipt_items")]
    [InlineData(typeof(ReceiptScan), "receipt_scans")]
    [InlineData(typeof(BankAccount), "bank_accounts")]
    [InlineData(typeof(OfxStatement), "ofx_statements")]
    [InlineData(typeof(BankTransaction), "bank_transactions")]
    [InlineData(typeof(MatchRejection), "receipt_match_rejections")]
    [InlineData(typeof(TransactionConflict), "bank_transaction_conflicts")]
    [InlineData(typeof(Category), "categories")]
    [InlineData(typeof(CategoryRule), "category_rules")]
    [InlineData(typeof(User), "auth_user")]
    public void Tables_keep_their_django_names(Type type, string table)
    {
        using var db = NewContext();
        Assert.Equal(table, Model(db).FindEntityType(type)!.GetTableName());
    }

    [Fact]
    public void Columns_are_snake_case()
    {
        using var db = NewContext();
        Assert.Equal("client_uuid", Column<Receipt>(db, nameof(Receipt.ClientUuid)));
        Assert.Equal("purchased_on", Column<Receipt>(db, nameof(Receipt.PurchasedOn)));
        Assert.Equal("uploaded_by_id", Column<ReceiptScan>(db, nameof(ReceiptScan.UploadedById)));
        Assert.Equal("transfer_pair_id", Column<BankTransaction>(db, nameof(BankTransaction.TransferPairId)));
        Assert.Equal("transactions_in_file", Column<OfxStatement>(db, nameof(OfxStatement.TransactionsInFile)));
        Assert.Equal("line_total", Column<ReceiptItem>(db, nameof(ReceiptItem.LineTotal)));
        Assert.Equal("is_superuser", Column<User>(db, nameof(User.IsSuperuser)));
    }

    [Fact]
    public void Uuids_are_django_char32()
    {
        using var db = NewContext();
        var p = Model(db).FindEntityType(typeof(Receipt))!.FindProperty(nameof(Receipt.ClientUuid))!;
        Assert.Equal("char(32)", p.GetColumnType());

        var converter = DjangoConventions.DjangoUuid;
        var g = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        Assert.Equal("6f9619ff8b86d011b42d00c04fc964ff", converter.ConvertToProvider(g));
        Assert.Equal(g, converter.ConvertFromProvider("6f9619ff8b86d011b42d00c04fc964ff"));
    }

    [Fact]
    public void Django_index_names_are_kept()
    {
        using var db = NewContext();
        var names = Model(db).GetEntityTypes().SelectMany(e => e.GetIndexes()).Select(i => i.GetDatabaseName()).ToHashSet();
        foreach (var expected in new[]
                 {
                     "receipts_shop_1c0884_idx", "receipts_purchas_144f66_idx", "receipt_ite_name_b17b8f_idx",
                     "receipt_sca_status_fab29b_idx", "ofx_stateme_start_d_7ef5c2_idx",
                     "bank_transa_posted__5529ce_idx", "bank_transa_account_d9e0db_idx",
                     "uniq_bank_account", "uniq_account_dedupe_key", "uniq_match_rejection", "uniq_category_rule",
                 })
            Assert.Contains(expected, names);
    }

    [Fact]
    public void Money_and_quantities_have_django_precision()
    {
        using var db = NewContext();
        var total = Model(db).FindEntityType(typeof(Receipt))!.FindProperty(nameof(Receipt.Total))!;
        var qty = Model(db).FindEntityType(typeof(ReceiptItem))!.FindProperty(nameof(ReceiptItem.Qty))!;
        var amount = Model(db).FindEntityType(typeof(BankTransaction))!.FindProperty(nameof(BankTransaction.Amount))!;
        Assert.Equal((12, 2), (total.GetPrecision(), total.GetScale()));
        Assert.Equal((10, 3), (qty.GetPrecision(), qty.GetScale()));
        Assert.Equal((14, 2), (amount.GetPrecision(), amount.GetScale()));
    }

    [Fact]
    public void Receipt_and_transfer_links_are_one_to_one()
    {
        using var db = NewContext();
        var txn = Model(db).FindEntityType(typeof(BankTransaction))!;
        Assert.True(txn.GetIndexes().Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(BankTransaction.ReceiptId)).IsUnique);
        Assert.True(txn.GetIndexes().Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(BankTransaction.TransferPairId)).IsUnique);
    }
}
