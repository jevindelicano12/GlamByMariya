using System.Text.RegularExpressions;

namespace GlamByMariya.Infrastructure.Integrations;

// Lookup must resolve only a visible reference belonging to the current published version.
public interface IWebsitePublishedAssetLookup
{
    Task<WebsitePublishedAsset?> FindPublishedImage(Guid imageId, CancellationToken cancellationToken);
}

public interface IWebsiteDraftAssetLookup
{
    Task<WebsitePublishedAsset?> FindDraftImage(Guid imageId, CancellationToken cancellationToken);
}

public sealed record WebsitePublishedAsset(string ObjectKey, string ContentType, int ByteLength);
public sealed record WebsitePublicImage(byte[] Bytes, string ContentType);

// Not registered until database and private storage have been verified together.
public sealed partial class WebsitePublicImageService(IWebsitePublishedAssetLookup repository, IAppFileStore files)
{
    [GeneratedRegex(@"\Awebsite-content/[0-9a-f]{32}\.(jpg|png)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidObjectKey();

    public async Task<WebsitePublicImage?> Read(Guid imageId, CancellationToken cancellationToken = default)
    {
        if (imageId == Guid.Empty) return null;
        var asset = await repository.FindPublishedImage(imageId, cancellationToken);
        return ReadAsset(asset, files);
    }

    internal static WebsitePublicImage? ReadAsset(WebsitePublishedAsset? asset, IAppFileStore files)
    {
        if (asset == null || string.IsNullOrEmpty(asset.ObjectKey) || !ValidObjectKey().IsMatch(asset.ObjectKey) || asset.ByteLength < 1 || asset.ByteLength > WebsiteContentRules.MaxImageBytes)
            return null;
        var expected = asset.ObjectKey.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg";
        if (!string.Equals(asset.ContentType, expected, StringComparison.Ordinal)) return null;
        var stored = files.Read(asset.ObjectKey);
        if (stored == null || stored.Value.Bytes.Length != asset.ByteLength || !string.Equals(stored.Value.ContentType, expected, StringComparison.OrdinalIgnoreCase))
            return null;
        return new(stored.Value.Bytes, expected);
    }
}
