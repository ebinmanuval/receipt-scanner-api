using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Core.Domain;
using ReceiptScanner.Core.Receipts;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;
using ReceiptScanner.Infrastructure.Images;
using ReceiptScanner.Infrastructure.Storage;

namespace ReceiptScanner.Api.Receipts;

public sealed class DuplicateReceiptException() : Exception("This receipt has already been uploaded.");

public sealed record WrittenReceipt(Receipt Receipt, string? ImageNote);

/// <summary>
/// Creates a receipt from validated input. Shared by POST /api/receipts/ and
/// scan approval, as ReceiptSerializer.create is in Django.
/// </summary>
public sealed class ReceiptWriter(
    ReceiptScannerDbContext db,
    IMediaStorage media,
    ILogger<ReceiptWriter> logger)
{
    public const string PhotoNotSaved = "photo NOT saved — the server's media directory is not writable";

    public Task<bool> ClientUuidExistsAsync(Guid uuid, CancellationToken ct) =>
        db.Receipts.AnyAsync(r => r.ClientUuid == uuid, ct);

    /// <summary>
    /// Store the receipt, its items and (if given) its photo, and flag it for
    /// review when the items don't add up. Joins the caller's transaction if
    /// there is one. The data matters more than the photo: if the photo can't
    /// be written, the receipt is saved without it.
    /// </summary>
    /// <param name="categoryId">A category the user chose (source "user"). No automatic categorisation.</param>
    public async Task<WrittenReceipt> CreateAsync(ValidatedReceipt input, StoredImage? photo, long? categoryId, CancellationToken ct)
    {
        if (input.ClientUuid is { } uuid && await ClientUuidExistsAsync(uuid, ct))
            throw new DuplicateReceiptException();

        var receipt = new Receipt
        {
            Shop = input.Shop,
            Total = input.Total,
            PurchasedOn = input.PurchasedOn,
            ClientUuid = input.ClientUuid,
            Items = input.Items.Select(i => new ReceiptItem { Name = i.Name, Qty = i.Qty, LineTotal = i.LineTotal }).ToList(),
        };

        var (needsReview, note) = ReceiptReconciler.Check(input.Total, input.Items.Select(i => i.LineTotal).ToList());
        receipt.NeedsReview = needsReview;
        receipt.ReviewNote = note.Length > 255 ? note[..255] : note;

        if (categoryId is { } cid)
        {
            receipt.CategoryId = cid;
            receipt.CategorySource = CategorySource.User;
        }

        string? imageNote = photo is null || photo.Note.Length == 0 ? null : photo.Note;
        if (photo is not null)
        {
            var name = MediaPaths.ReceiptImage(input.PurchasedOn, "photo" + photo.Extension);
            try
            {
                await media.SaveAsync(name, photo.Bytes, ct);
                receipt.Image = name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not store the receipt photo. Saving the receipt without it. Check Media:Root permissions.");
                imageNote = PhotoNotSaved;
            }
        }

        var ownTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync(ct);
            if (ownTransaction is not null) await ownTransaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            Cleanup(receipt.Image);
            // Lost a race with a retry of the same upload: the other request saved it.
            if (ex is DbUpdateException && input.ClientUuid is { } u && await StillExistsAsync(u))
                throw new DuplicateReceiptException();
            throw;
        }
        finally
        {
            if (ownTransaction is not null) await ownTransaction.DisposeAsync();
        }

        logger.LogInformation("Stored receipt {Id} ({Items} items, image={Image}{Note})",
            receipt.Id, receipt.Items.Count, receipt.Image is not null, imageNote is null ? "" : $", {imageNote}");

        if (receipt.CategoryId is not null)
            await db.Entry(receipt).Reference(r => r.Category).Query().Include(c => c.Parent).LoadAsync(ct);

        return new WrittenReceipt(receipt, imageNote);
    }

    private async Task<bool> StillExistsAsync(Guid uuid)
    {
        db.ChangeTracker.Clear();
        return await db.Receipts.AnyAsync(r => r.ClientUuid == uuid);
    }

    private void Cleanup(string? image)
    {
        if (image is null) return;
        try { media.Delete(image); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not remove orphaned photo {Image}", image); }
    }
}
