using System.Security.Claims;

namespace GlamByMariya.Infrastructure.Integrations;

public sealed record StoredWebsiteImage(string Key, string ContentType, int ByteLength, uint Width, uint Height);

// Preparation only: never return an object key to a public caller or register an upload route before storage is verified.
public sealed class WebsitePrivateImageStore(IAppFileStore files)
{
    public StoredWebsiteImage Save(ClaimsPrincipal? principal, byte[] bytes)
    {
        if (principal?.Identity?.IsAuthenticated != true || !principal.IsInRole("SuperAdmin") ||
            !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty)
            throw new UnauthorizedAccessException("SuperAdmin authorization is required.");
        var image = WebsiteImageValidator.Normalize(bytes);
        var key = "website-content/" + Guid.NewGuid().ToString("N") + image.Extension;
        files.Save(key, image.Bytes);
        return new(key, image.ContentType, image.Bytes.Length, image.Width, image.Height);
    }
}
