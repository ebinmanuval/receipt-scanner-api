using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ReceiptScanner.Infrastructure.Storage;

public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>Same directory as Django's MEDIA_ROOT, so existing images keep working.</summary>
    public string Root { get; set; } = "";
}

/// <summary>
/// Files under MEDIA_ROOT, addressed by the relative names Django stores in
/// its FileField/ImageField columns (e.g. <c>receipts/2026/08/&lt;hex&gt;.jpg</c>).
/// </summary>
public interface IMediaStorage
{
    string Root { get; }
    /// <summary>Absolute path for a stored name. Throws if the name escapes the root.</summary>
    string ResolvePath(string relativeName);
    bool Exists(string relativeName);
    Stream OpenRead(string relativeName);
    /// <summary>Write a new file, creating folders as needed. Never overwrites.</summary>
    Task SaveAsync(string relativeName, Stream content, CancellationToken ct = default);
    Task SaveAsync(string relativeName, byte[] content, CancellationToken ct = default);
    void Delete(string relativeName);
}

public sealed class MediaStorage : IMediaStorage
{
    /// <param name="env">A relative Media:Root is resolved against the content root, not the shell's cwd.</param>
    public MediaStorage(IOptions<MediaOptions> options, IHostEnvironment env)
    {
        Root = Path.GetFullPath(options.Value.Root, env.ContentRootPath);
    }

    public MediaStorage(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string ResolvePath(string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName))
            throw new ArgumentException("Empty media name.", nameof(relativeName));

        var full = Path.GetFullPath(Path.Combine(Root, relativeName.Replace('\\', '/').TrimStart('/')));
        var rootWithSep = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Media name escapes MEDIA_ROOT: {relativeName}");
        return full;
    }

    public bool Exists(string relativeName) => File.Exists(ResolvePath(relativeName));

    public Stream OpenRead(string relativeName) =>
        new FileStream(ResolvePath(relativeName), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

    public async Task SaveAsync(string relativeName, Stream content, CancellationToken ct = default)
    {
        var path = ResolvePath(relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
    }

    public Task SaveAsync(string relativeName, byte[] content, CancellationToken ct = default) =>
        SaveAsync(relativeName, new MemoryStream(content, writable: false), ct);

    public void Delete(string relativeName)
    {
        var path = ResolvePath(relativeName);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// Ports of the Django <c>upload_to</c> functions, so new files land exactly
/// where the Python app would have put them. Always forward slashes: these
/// strings are stored in the database.
/// </summary>
public static class MediaPaths
{
    /// <summary>receipts/2026/08/&lt;hex&gt;.jpg — receipts/models.py receipt_image_path.</summary>
    public static string ReceiptImage(DateOnly? purchasedOn, string originalFilename) =>
        $"receipts/{(purchasedOn is { } d ? $"{d.Year:D4}/{d.Month:D2}" : "undated")}/{NewName()}{Suffix(originalFilename, ".jpg")}";

    /// <summary>scans/2026/10/&lt;hex&gt;.heic — original, extension preserved.</summary>
    public static string ScanImage(DateTime? capturedAt, string originalFilename, DateTime utcNow) =>
        $"scans/{Month(capturedAt ?? utcNow)}/{NewName()}{Suffix(originalFilename, ".jpg")}";

    /// <summary>scans/2026/10/thumbs/&lt;hex&gt;.jpg</summary>
    public static string ScanThumbnail(DateTime? capturedAt, DateTime utcNow) =>
        $"scans/{Month(capturedAt ?? utcNow)}/thumbs/{NewName()}.jpg";

    /// <summary>ofx/2026/09/&lt;hex&gt;.ofx — banking/models.py ofx_upload_path.</summary>
    public static string OfxFile(DateOnly? endDate, string originalFilename) =>
        $"ofx/{(endDate is { } d ? $"{d.Year:D4}/{d.Month:D2}" : "undated")}/{NewName()}{Suffix(originalFilename, ".ofx")}";

    private static string Month(DateTime value) => $"{value.Year:D4}/{value.Month:D2}";

    private static string NewName() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Python's <c>Path(name).suffix.lower() or fallback</c>: the last ".ext",
    /// but a leading-dot name like ".heic" has no suffix.
    /// </summary>
    internal static string Suffix(string? filename, string fallback)
    {
        var name = Path.GetFileName((filename ?? "").Replace('\\', '/'));
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return fallback;
        return name[dot..].ToLowerInvariant();
    }
}
