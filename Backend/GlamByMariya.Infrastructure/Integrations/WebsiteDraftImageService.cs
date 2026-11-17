using System.Security.Claims;

namespace GlamByMariya.Infrastructure.Integrations;

// Not registered until private storage and the complete workflow are verified.
public sealed class WebsiteDraftImageService(IWebsiteDraftAssetLookup repository, IAppFileStore files)
{
    public async Task<WebsitePublicImage?> Read(ClaimsPrincipal? principal, Guid id, CancellationToken ct = default)
    {
        if (principal?.Identity?.IsAuthenticated != true || !principal.IsInRole("SuperAdmin") ||
            !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty)
            throw new UnauthorizedAccessException("SuperAdmin authorization is required.");
        if (id == Guid.Empty) return null;
        return WebsitePublicImageService.ReadAsset(await repository.FindDraftImage(id, ct), files);
    }
}
