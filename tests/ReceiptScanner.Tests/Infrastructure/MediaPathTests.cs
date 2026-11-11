using System.Text.RegularExpressions;
using ReceiptScanner.Infrastructure.Storage;

namespace ReceiptScanner.Tests.Infrastructure;

public class MediaPathTests
{
    [Fact]
    public void Receipt_images_go_in_dated_folders()
    {
        Assert.Matches(new Regex("^receipts/2026/08/[0-9a-f]{32}\\.jpg$"),
            MediaPaths.ReceiptImage(new DateOnly(2026, 8, 3), "IMG_0001.JPG"));
        Assert.Matches(new Regex("^receipts/undated/[0-9a-f]{32}\\.jpg$"),
            MediaPaths.ReceiptImage(null, "photo"));
    }

    [Fact]
    public void Scans_keep_their_extension_and_thumbs_are_jpeg()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Matches(new Regex("^scans/2026/10/[0-9a-f]{32}\\.heic$"), MediaPaths.ScanImage(null, "IMG_1.HEIC", now));
        Assert.Matches(new Regex("^scans/2026/10/thumbs/[0-9a-f]{32}\\.jpg$"), MediaPaths.ScanThumbnail(null, now));
    }

    [Theory]
    [InlineData("a.OFX", ".ofx")]
    [InlineData("noext", ".ofx")]
    [InlineData(".hidden", ".ofx")]   // Python: Path(".hidden").suffix == ""
    [InlineData("a.b.qfx", ".qfx")]
    public void Suffix_matches_python(string name, string expected) =>
        Assert.Equal(expected, MediaPaths.Suffix(name, ".ofx"));

    [Fact]
    public void Storage_refuses_paths_outside_the_root()
    {
        var storage = new MediaStorage(Path.Combine(Path.GetTempPath(), "rs-media-test"));
        Assert.Throws<UnauthorizedAccessException>(() => storage.ResolvePath("../etc/passwd"));
        Assert.EndsWith(Path.Combine("receipts", "2026", "08", "x.jpg"), storage.ResolvePath("receipts/2026/08/x.jpg"));
    }
}
