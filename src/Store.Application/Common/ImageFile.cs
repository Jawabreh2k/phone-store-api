using Store.Domain.Common;

namespace Store.Application.Common;

/// <summary>
/// Validates uploaded images by their content, not their name or declared content type,
/// both of which the client controls.
/// </summary>
public static class ImageFile
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public static readonly Error TooLarge = Error.Validation("files.too_large", "Images must be 5 MB or smaller.");
    public static readonly Error UnsupportedType = Error.Validation("files.unsupported_type", "Only JPEG, PNG and WebP images are allowed.");

    /// <summary>Returns the file extension to store the image under, or null with an error.</summary>
    public static string? Validate(Stream content, long length, out Error? error)
    {
        ArgumentNullException.ThrowIfNull(content);
        error = null;

        if (length is <= 0 or > MaxBytes)
        {
            error = TooLarge;
            return null;
        }

        Span<byte> header = stackalloc byte[12];
        var read = content.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var extension = Detect(header[..read]);
        if (extension is null)
        {
            error = UnsupportedType;
        }

        return extension;
    }

    private static string? Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
        {
            return ".jpg";
        }

        if (h.Length >= 8 && h[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ".png";
        }

        // RIFF....WEBP
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        return null;
    }
}
