using ImageMagick;

namespace GlamByMariya.Infrastructure.Integrations;

public sealed record NormalizedWebsiteImage(byte[] Bytes, string Extension, string ContentType, uint Width, uint Height);

// Not connected to an upload endpoint until decoder resource limits and storage have been verified.
public static class WebsiteImageValidator
{
    public static NormalizedWebsiteImage Normalize(byte[] bytes)
    {
        if (bytes is not { Length: > 0 and <= WebsiteContentRules.MaxImageBytes })
            throw new InvalidOperationException("Choose a JPG or PNG image up to 5 MB.");
        var jpeg = bytes.Length > 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (!jpeg && !png) throw new InvalidOperationException("Choose a valid JPG or PNG image.");
        try
        {
            using var info = new MagickImageCollection();
            info.Ping(bytes);
            if (info.Count != 1 || (jpeg && info[0].Format != MagickFormat.Jpeg) || (png && info[0].Format != MagickFormat.Png))
                throw new InvalidOperationException("Choose a single JPG or PNG image.");
            CheckSize(info[0].Width, info[0].Height);
            using var image = new MagickImage(bytes);
            if ((jpeg && image.Format != MagickFormat.Jpeg) || (png && image.Format != MagickFormat.Png))
                throw new InvalidOperationException("Choose a valid JPG or PNG image.");
            CheckSize(image.Width, image.Height);
            image.AutoOrient(); image.Strip();
            CheckSize(image.Width, image.Height);
            image.Format = jpeg ? MagickFormat.Jpeg : MagickFormat.Png;
            if (jpeg) image.Quality = 90;
            var output = image.ToByteArray();
            if (output.Length == 0 || output.Length > WebsiteContentRules.MaxImageBytes) throw new InvalidOperationException("Processed image exceeds 5 MB.");
            return new(output, jpeg ? ".jpg" : ".png", jpeg ? "image/jpeg" : "image/png", image.Width, image.Height);
        }
        catch (MagickException) { throw new InvalidOperationException("Choose a valid, decodable JPG or PNG image."); }
    }

    private static void CheckSize(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > WebsiteContentRules.MaxDimension || height > WebsiteContentRules.MaxDimension ||
            (long)width * height > WebsiteContentRules.MaxPixels) throw new InvalidOperationException("The image dimensions are too large.");
    }
}
