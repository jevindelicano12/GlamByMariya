using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace GlamByMariya.Infrastructure.Integrations;

// The file store failed (or is unavailable); the draft was not changed.
public sealed class WebsiteImageStorageException(Exception inner) : InvalidOperationException("Photo storage is unavailable.", inner);

// Owner uploads and removes photos on the live website draft.
public sealed class WebsiteImageUploadService(IWebsiteImageRepository images, IWebsiteAssetWriter assets, IAppFileStore files, Microsoft.Extensions.Logging.ILogger<WebsiteImageUploadService> log)
{
    private static Guid RequireOwner(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true || !principal.IsInRole("SuperAdmin") ||
            !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty)
            throw new UnauthorizedAccessException("SuperAdmin authorization is required.");
        return actor;
    }

    private static void RequireDraft(Guid draftId, long revision)
    {
        if (draftId == Guid.Empty || revision < 1) throw new ArgumentException("A draft and its revision are required.");
    }

    public async Task Save(ClaimsPrincipal owner, Guid draftId, long revision, string role, string? altText, string? caption, byte[] bytes, CancellationToken ct = default)
    {
        var actor = RequireOwner(owner);
        RequireDraft(draftId, revision);
        if (role is not ("hero" or "portrait" or "gallery")) throw new ArgumentException("Choose where the photo goes.");
        var alt = altText?.Trim() ?? "";
        if (alt.Length is 0 or > 250) throw new ArgumentException("Describe the photo in up to 250 characters (alternative text).");
        var text = caption?.Trim() ?? "";
        if (text.Length > 500) throw new ArgumentException("Captions can be up to 500 characters.");

        var current = await images.ReadDraftImages(draftId, ct);
        var gallery = current.Where(x => x.Role == "gallery").ToList();
        if (role == "gallery" && gallery.Count >= WebsiteContentRules.MaxGalleryImages) throw new ArgumentException("The gallery already has 20 photos.");

        NormalizedWebsiteImage image;
        try { image = WebsiteImageValidator.Normalize(bytes); }
        catch (InvalidOperationException e) { throw new ArgumentException(e.Message, e); }

        var key = $"website-content/{Guid.NewGuid():N}{image.Extension}";
        try { files.Save(key, image.Bytes); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new WebsiteImageStorageException(e); }
        try
        {
            var assetId = await assets.AddAsset(actor, key, image.ContentType, image.Bytes.Length, ct);
            List<WebsiteImageReference> next = role == "gallery"
                ? [.. current, new(Guid.NewGuid(), assetId, role, alt, text, gallery.Count == 0 ? 0 : gallery.Max(x => x.SortOrder) + 1, true)]
                : [.. current.Where(x => x.Role != role), new(Guid.NewGuid(), assetId, role, alt, text, 0, true)];
            await images.ReplaceDraftImages(actor, draftId, revision, next, ct);
        }
        catch
        {
            // The draft does not reference this file; do not leave it behind.
            try { files.Delete(key); }
            catch (Exception cleanup) { log.LogWarning(cleanup, "Could not delete orphaned website photo {ObjectKey} after a failed draft update.", key); }
            throw;
        }
    }

    // The file is kept: the published version may still show it.
    public async Task Remove(ClaimsPrincipal owner, Guid draftId, long revision, Guid imageId, CancellationToken ct = default)
    {
        var actor = RequireOwner(owner);
        RequireDraft(draftId, revision);
        var current = await images.ReadDraftImages(draftId, ct);
        if (current.All(x => x.Id != imageId)) throw new ArgumentException("That photo is no longer in the draft.");
        await images.ReplaceDraftImages(actor, draftId, revision, current.Where(x => x.Id != imageId).ToList(), ct);
    }
}
