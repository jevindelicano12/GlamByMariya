using ImageMagick;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace GlamByMariya.Infrastructure.Integrations;

public sealed record NormalizedPaymentProof(byte[] Bytes, string Extension, string ContentType);

// Payment proofs are decoded and re-encoded before they enter either local or
// private cloud storage. Historical stored objects are never rewritten.
public static class PaymentProofImageValidator
{
    public const int MaximumBytes = 5_000_000;
    public const int MaximumDimension = 8_000;
    public const long MaximumPixels = 40_000_000;

    // Demo retry identity: oriented decoded RGBA pixels, not re-encoded JPEG
    // bytes or a randomly generated storage path.
    public static string PixelFingerprint(byte[] bytes)
    {
        if (bytes is not { Length: > 0 and <= MaximumBytes }) throw new InvalidOperationException("Upload a JPG or PNG image up to 5 MB.");
        try
        {
            using var image = new MagickImage(bytes);
            if (image.Format is not MagickFormat.Jpeg and not MagickFormat.Png || image.Width is 0 or > MaximumDimension || image.Height is 0 or > MaximumDimension || (long)image.Width * image.Height > MaximumPixels)
                throw new InvalidOperationException("Proof must be a valid JPG or PNG image with supported dimensions.");
            image.AutoOrient();
            image.Strip();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> dimensions = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(dimensions, image.Width);
            BinaryPrimitives.WriteUInt32BigEndian(dimensions[4..], image.Height);
            hash.AppendData(dimensions);
            using var pixels = image.GetPixels();
            for (var y = 0; y < image.Height; y++)
                hash.AppendData(pixels.ToByteArray(0, (int)y, image.Width, 1, "RGBA") ?? throw new ArgumentNullException("data"));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (MagickException) { throw new InvalidOperationException("Proof must be a valid, decodable JPG or PNG image."); }
    }

    public static NormalizedPaymentProof Normalize(byte[] bytes)
    {
        if (bytes is not { Length: > 0 and <= MaximumBytes })
            throw new InvalidOperationException("Upload a JPG or PNG image up to 5 MB.");
        try
        {
            using var image = new MagickImage(bytes);
            if (image.Format is not MagickFormat.Jpeg and not MagickFormat.Png)
                throw new InvalidOperationException("Proof must be a valid JPG or PNG image.");
            if (image.Width is 0 or > MaximumDimension || image.Height is 0 or > MaximumDimension
                || (long)image.Width * image.Height > MaximumPixels)
                throw new InvalidOperationException("The proof image dimensions are too large.");

            image.AutoOrient();
            image.Strip();
            var png = image.Format == MagickFormat.Png;
            image.Format = png ? MagickFormat.Png : MagickFormat.Jpeg;
            if (!png) image.Quality = 90;
            var normalized = image.ToByteArray();
            if (normalized.Length > MaximumBytes) throw new InvalidOperationException("The normalized proof image is larger than 5 MB.");
            return new(normalized, png ? ".png" : ".jpg", png ? "image/png" : "image/jpeg");
        }
        catch (InvalidOperationException) { throw; }
        catch (MagickException) { throw new InvalidOperationException("Proof must be a valid, decodable JPG or PNG image."); }
    }
}
