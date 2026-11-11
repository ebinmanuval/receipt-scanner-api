using System.Text;

namespace ReceiptScanner.Core.Images;

/// <summary>
/// Identify a photo by its leading bytes, before any image library touches it.
/// Only these raster formats are ever decoded: the decoder (ImageMagick) can
/// read far more (SVG, PDF, PostScript, MVG), and an upload must never be able
/// to choose one of those.
/// </summary>
public static class ImageSniffer
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Format names as Pillow reports them, so stored values match the Django app.</summary>
    public static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12) return null;

        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG";
        if (b[..8].SequenceEqual(PngSignature)) return "PNG";
        if (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)) return "GIF";
        if (b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "WEBP";
        if (b[..4].SequenceEqual("II*\0"u8) || b[..4].SequenceEqual("MM\0*"u8)) return "TIFF";
        if (b[0] == (byte)'B' && b[1] == (byte)'M') return "BMP";

        // ISO base media (HEIC/HEIF/AVIF): "ftyp" at 4, major brand at 8.
        if (b[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(b[8..12]);
            return brand switch
            {
                "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" => "HEIF",
                "avif" or "avis" => "AVIF",
                _ => null,
            };
        }
        return null;
    }

    /// <summary>Formats every browser displays; anything else becomes JPEG for a receipt photo.</summary>
    public static bool IsWebSafe(string format) => format is "JPEG" or "PNG" or "GIF" or "WEBP";

    public static string Extension(string format) => format switch
    {
        "JPEG" => ".jpg",
        "PNG" => ".png",
        "GIF" => ".gif",
        "WEBP" => ".webp",
        "HEIF" => ".heic",
        "AVIF" => ".avif",
        "TIFF" => ".tif",
        "BMP" => ".bmp",
        _ => ".bin",
    };
}
