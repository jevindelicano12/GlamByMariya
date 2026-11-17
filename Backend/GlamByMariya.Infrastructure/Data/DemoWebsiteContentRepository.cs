using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Data;

// Demo-only: content is lost on restart; persistent publication needs the reviewed schema.
public sealed class DemoWebsiteContentRepository : IWebsiteContentRepository
{
    private readonly object gate = new();
    private WebsiteContentVersion? draft;
    private WebsiteContentVersion? published;

    private static WebsiteTextInput Copy(WebsiteTextInput text) => text with
    {
        Links = Array.AsReadOnly(text.Links!.ToArray()),
        Faqs = Array.AsReadOnly(text.Faqs!.ToArray())
    };

    public Task<WebsiteContentVersion?> ReadDraft(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(draft);
    }

    public Task<WebsiteContentVersion?> ReadPublished(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(published);
    }

    public Task<WebsiteContentVersion> CreateDraft(Guid actor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            draft ??= new(Guid.NewGuid(), 1, "draft", published == null
                ? new("", "", "", "", "", "", "", Array.Empty<WebsiteLinkInput>(), Array.Empty<WebsiteFaqInput>())
                : Copy(published.Text));
            return Task.FromResult(draft);
        }
    }

    public Task<WebsiteContentVersion> SaveDraft(Guid actor, Guid draftId, long expectedRevision, WebsiteTextInput text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var error = WebsiteContentRules.Validate(text, false);
        if (error != null) throw new ArgumentException(error);
        lock (gate)
        {
            if (draft == null || draft.Id != draftId || draft.Revision != expectedRevision || draft.Revision == long.MaxValue)
                throw new InvalidOperationException("The draft changed. Reload before saving.");
            draft = draft with { Revision = draft.Revision + 1, Text = Copy(text) };
            return Task.FromResult(draft);
        }
    }

    public Task<WebsiteContentVersion> Publish(Guid actor, Guid draftId, long expectedRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (draft == null || draft.Id != draftId || draft.Revision != expectedRevision)
                throw new InvalidOperationException("The draft changed. Reload before publishing.");
            var error = WebsiteContentRules.Validate(draft.Text, true);
            if (error != null) throw new ArgumentException(error);
            published = draft with { Status = "published" };
            draft = null;
            return Task.FromResult(published);
        }
    }
}
