using System.Security.Claims;

namespace GlamByMariya.Infrastructure.Integrations;

// Not registered in normal mode until persistence and public-image access are verified.
public sealed class WebsiteImageService(IWebsiteImageRepository repository)
{
    private static Guid RequireOwner(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true || !principal.IsInRole("SuperAdmin") ||
            !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty)
            throw new UnauthorizedAccessException("SuperAdmin authorization is required.");
        return actor;
    }

    public Task<IReadOnlyList<WebsiteImageReference>> DraftImages(ClaimsPrincipal principal, Guid draftId, CancellationToken cancellationToken = default)
    {
        RequireOwner(principal);
        if (draftId == Guid.Empty) throw new ArgumentException("A draft is required.");
        return repository.ReadDraftImages(draftId, cancellationToken);
    }

    public async Task<IReadOnlyList<WebsiteImageReference>> PublishedImages(CancellationToken cancellationToken = default)
    {
        var images = await repository.ReadPublishedImages(cancellationToken);
        // Fail closed if a faulty repository returns hidden images.
        if (images.Any(image => image == null || !image.Visible)) throw new InvalidOperationException("Invalid published image state.");
        return images;
    }

    public Task ReplaceDraftImages(ClaimsPrincipal principal, Guid draftId, long expectedRevision, IReadOnlyList<WebsiteImageReference> images, CancellationToken cancellationToken = default)
    {
        var actor = RequireOwner(principal);
        if (draftId == Guid.Empty || expectedRevision < 1 || images == null) throw new ArgumentException("A draft, its revision and images are required.");
        return repository.ReplaceDraftImages(actor, draftId, expectedRevision, images, cancellationToken);
    }
}
