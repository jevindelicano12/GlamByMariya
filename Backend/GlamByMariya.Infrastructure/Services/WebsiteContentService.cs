using System.Security.Claims;

namespace GlamByMariya.Infrastructure.Services;

public sealed record WebsiteContentVersion(Guid Id, long Revision, string Status, WebsiteTextInput Text);
public sealed record WebsiteImageReference(Guid Id, Guid AssetId, string Role, string AltText, string Caption, int SortOrder, bool Visible);

// Implementations must enforce draft status and revision in the same transaction as every image change.
public interface IWebsiteImageRepository
{
    Task<IReadOnlyList<WebsiteImageReference>> ReadDraftImages(Guid draftId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WebsiteImageReference>> ReadPublishedImages(CancellationToken cancellationToken);
    Task ReplaceDraftImages(Guid actor, Guid draftId, long expectedRevision, IReadOnlyList<WebsiteImageReference> images, CancellationToken cancellationToken);
}

// Implementations must compare the expected revision and write atomically in persistent storage.
public interface IWebsiteContentRepository
{
    Task<WebsiteContentVersion?> ReadDraft(CancellationToken cancellationToken);
    Task<WebsiteContentVersion?> ReadPublished(CancellationToken cancellationToken);
    Task<WebsiteContentVersion> CreateDraft(Guid actor, CancellationToken cancellationToken);
    Task<WebsiteContentVersion> SaveDraft(Guid actor, Guid draftId, long expectedRevision, WebsiteTextInput text, CancellationToken cancellationToken);
    Task<WebsiteContentVersion> Publish(Guid actor, Guid draftId, long expectedRevision, CancellationToken cancellationToken);
}

// No repository implementation or route is registered until the schema and storage have been verified.
public sealed class WebsiteContentService(IWebsiteContentRepository repository)
{
    private static Guid RequireOwner(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true || !principal.IsInRole("SuperAdmin") ||
            !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty)
            throw new UnauthorizedAccessException("SuperAdmin authorization is required.");
        return actor;
    }

    public async Task<WebsiteContentVersion?> Preview(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        RequireOwner(principal);
        var draft = await repository.ReadDraft(cancellationToken);
        if (draft != null && draft.Status != "draft") throw new InvalidOperationException("Invalid draft state.");
        return draft;
    }

    public Task<WebsiteContentVersion> CreateDraft(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
        repository.CreateDraft(RequireOwner(principal), cancellationToken);

    public async Task<WebsiteContentVersion?> PublicContent(CancellationToken cancellationToken = default)
    {
        var published = await repository.ReadPublished(cancellationToken);
        if (published != null && published.Status != "published") throw new InvalidOperationException("Invalid published state.");
        return published;
    }

    public Task<WebsiteContentVersion> Save(ClaimsPrincipal principal, Guid draftId, long expectedRevision, WebsiteTextInput text, CancellationToken cancellationToken = default)
    {
        var actor = RequireOwner(principal);
        if (draftId == Guid.Empty || expectedRevision < 1) throw new ArgumentException("A draft and its revision are required.");
        var error = WebsiteContentRules.Validate(text, false);
        if (error != null) throw new ArgumentException(error);
        return repository.SaveDraft(actor, draftId, expectedRevision, text, cancellationToken);
    }

    public async Task<WebsiteContentVersion> Publish(ClaimsPrincipal principal, Guid draftId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var actor = RequireOwner(principal);
        if (draftId == Guid.Empty || expectedRevision < 1) throw new ArgumentException("A draft and its revision are required.");
        var draft = await repository.ReadDraft(cancellationToken);
        if (draft == null || draft.Status != "draft" || draft.Id != draftId || draft.Revision != expectedRevision)
            throw new InvalidOperationException("The draft changed. Reload before publishing.");
        var error = WebsiteContentRules.Validate(draft.Text, true);
        if (error != null) throw new ArgumentException(error);
        // The repository must recheck the revision inside its publish transaction.
        return await repository.Publish(actor, draftId, expectedRevision, cancellationToken);
    }
}
