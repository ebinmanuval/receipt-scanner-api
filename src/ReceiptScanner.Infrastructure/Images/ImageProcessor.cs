using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReceiptScanner.Core.Images;

namespace ReceiptScanner.Infrastructure.Images;

public sealed class ImageOptions
{
    public const string Section = "Images";

    /// <summary>Upload limit for receipts and scans (Django MAX_UPLOAD_BYTES).</summary>
    public long MaxUploadBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>Receipt photos are downscaled to this (IMAGE_MAX_DIMENSION).</summary>
    public int MaxDimension { get; set; } = 2400;
    public int JpegQuality { get; set; } = 85;

    /// <summary>Scan previews (SCAN_THUMBNAIL_DIMENSION / _QUALITY).</summary>
    public int ThumbnailDimension { get; set; } = 600;
    public int ThumbnailQuality { get; set; } = 75;
}

/// <summary>Width and height as a viewer sees them (EXIF rotation applied), and the format name.</summary>
public sealed record ImageInfo(int Width, int Height, string Format);

/// <summary>A file to store: bytes, plus the extension that matches them.</summary>
public sealed record StoredImage(byte[] Bytes, string Extension, string Note);

public interface IImageProcessor
{
    /// <summary>Null when the bytes aren't an image we accept and can read.</summary>
    ImageInfo? Measure(byte[] data);

    /// <summary>A small JPEG preview, or null. Never throws.</summary>
    byte[]? Thumbnail(byte[] data);

    /// <summary>
    /// A receipt photo: web formats under the size limit pass through
    /// untouched; anything else becomes a JPEG of at most MaxDimension.
    /// Null when the bytes aren't a readable image.
    /// </summary>
    StoredImage? Normalize(byte[] data);
}

/// <summary>
/// Port of receipts/images.py with ImageMagick (Magick.NET) in place of
/// Pillow + pillow-heif. Bytes are identified by <see cref="ImageSniffer"/>
/// first and decoded with that format named explicitly, so ImageMagick never
/// guesses a coder from untrusted input.
/// </summary>
public sealed class ImageProcessor(IOptions<ImageOptions> options, ILogger<ImageProcessor> logger) : IImageProcessor
{
    private readonly ImageOptions _o = options.Value;

    public ImageInfo? Measure(byte[] data)
    {
        var format = ImageSniffer.Detect(data);
        if (format is null) return null;
        try
        {
            using var image = new MagickImage();
            image.Ping(data, Settings(format));   // header only, no full decode
            var (w, h) = ((int)image.Width, (int)image.Height);
            // EXIF orientations 5–8 rotate by 90°: report as displayed.
            return (int)image.Orientation is >= 5 and <= 8 ? new ImageInfo(h, w, format) : new ImageInfo(w, h, format);
        }
        catch (MagickException ex)
        {
            logger.LogWarning("Could not read a {Format} upload: {Message}", format, ex.Message);
            return null;
        }
    }

    public byte[]? Thumbnail(byte[] data)
    {
        var format = ImageSniffer.Detect(data);
        if (format is null) return null;
        try
        {
            using var image = new MagickImage(data, Settings(format));
            return ToJpeg(image, _o.ThumbnailDimension, _o.ThumbnailQuality);
        }
        catch (Exception ex)
        {
            // A missing preview is cosmetic; never fail an upload over it.
            logger.LogWarning("Could not build a thumbnail: {Message}", ex.Message);
            return null;
        }
    }

    public StoredImage? Normalize(byte[] data)
    {
        var format = ImageSniffer.Detect(data);
        if (format is null) return null;
        try
        {
            using var image = new MagickImage(data, Settings(format));
            var originalW = (int)image.Width;
            var originalH = (int)image.Height;
            var tooBig = Math.Max(originalW, originalH) > _o.MaxDimension;

            // Web-viewable and small enough: keep the bytes as sent, just name
            // them by what they really are.
            if (ImageSniffer.IsWebSafe(format) && !tooBig)
                return new StoredImage(data, ImageSniffer.Extension(format), "");

            var jpeg = ToJpeg(image, _o.MaxDimension, _o.JpegQuality);
            return new StoredImage(jpeg, ".jpg",
                $"{format} {originalW}x{originalH} → JPEG {image.Width}x{image.Height}");
        }
        catch (MagickException ex)
        {
            logger.LogWarning("Could not normalise a {Format} upload: {Message}", format, ex.Message);
            return null;
        }
    }

    /// <summary>Rotate per EXIF, shrink to fit, flatten transparency onto white, strip metadata.</summary>
    private static byte[] ToJpeg(MagickImage image, int maxDimension, int quality)
    {
        image.AutoOrient();
        if (Math.Max(image.Width, image.Height) > maxDimension)
            image.Resize((uint)maxDimension, (uint)maxDimension);   // fits inside, keeps aspect ratio

        if (image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }
        if (image.ColorSpace == ColorSpace.CMYK)
            image.ColorSpace = ColorSpace.sRGB;

        image.Strip();
        image.Quality = (uint)quality;
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    private static MagickReadSettings Settings(string format) => new() { Format = MagickFormatFor(format) };

    private static MagickFormat MagickFormatFor(string format) => format switch
    {
        "JPEG" => MagickFormat.Jpeg,
        "PNG" => MagickFormat.Png,
        "GIF" => MagickFormat.Gif,
        "WEBP" => MagickFormat.WebP,
        "HEIF" => MagickFormat.Heic,
        "AVIF" => MagickFormat.Avif,
        "TIFF" => MagickFormat.Tiff,
        "BMP" => MagickFormat.Bmp,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an accepted image format."),
    };
}
