using System.Text.Json;
using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Data;

// One local Demo process owns this folder; the single snapshot commits draft and
// publication together. Corrupt state fails closed rather than being overwritten.
public sealed class DemoFileWebsiteContentRepository : IWebsiteContentRepository, IDisposable
{
    private sealed record State(WebsiteContentVersion? Draft, WebsiteContentVersion? Published,
        DemoContentImage? DraftHero = null, DemoContentImage? DraftPortrait = null,
        DemoContentImage? PublishedHero = null, DemoContentImage? PublishedPortrait = null);
    public sealed record DemoContentImage(Guid Id, string Extension, string AltText);
    private readonly object gate = new();
    private readonly string path;
    private readonly FileStream lease;
    private State state;
    private bool damaged;
    public DemoFileWebsiteContentRepository(string root)
    {
        Directory.CreateDirectory(root);
        lease = new FileStream(Path.Combine(root, "content.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        path = Path.Combine(root, "content.json");
        try
        {
            if (!File.Exists(path)) { state = new(null, null); return; }
            if (new FileInfo(path).Length > 1_000_000) throw new InvalidDataException("Demo content file is too large.");
            state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid demo content.");
            Validate(state.Draft, "draft"); Validate(state.Published, "published");
            foreach (var image in new[] { state.DraftHero, state.DraftPortrait, state.PublishedHero, state.PublishedPortrait }) ValidateImage(image);
            if ((state.Draft == null && (state.DraftHero != null || state.DraftPortrait != null)) ||
                (state.Published == null && (state.PublishedHero != null || state.PublishedPortrait != null))) throw new InvalidDataException("Orphaned demo image reference.");
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        { state = new(null, null); damaged = true; }
        catch { lease.Dispose(); throw; }
    }
    private void EnsureValid() { if (damaged) throw new InvalidOperationException("Demo content is unavailable; existing files were not changed."); }
    private static void Validate(WebsiteContentVersion? value, string status)
    {
        if (value != null && (value.Id == Guid.Empty || value.Revision < 1 || value.Status != status || WebsiteContentRules.Validate(value.Text, status == "published") != null))
            throw new InvalidDataException("Invalid demo content state; existing files were not changed.");
    }
    private static WebsiteTextInput Copy(WebsiteTextInput text) => text with { Links = Array.AsReadOnly(text.Links!.ToArray()), Faqs = Array.AsReadOnly(text.Faqs!.ToArray()) };
    private static void ValidateImage(DemoContentImage? image)
    {
        if (image != null && (image.Id == Guid.Empty || image.Extension is not (".png" or ".jpg") || string.IsNullOrWhiteSpace(image.AltText) || image.AltText.Length > 250))
            throw new InvalidDataException("Invalid demo image reference.");
    }
    private string ImagePath(DemoContentImage image) => Path.Combine(Path.GetDirectoryName(path)!, image.Id.ToString("N") + image.Extension);
    private void Commit(State next)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, next); file.Flush(true); }
        File.Move(temp, path, true);
        state = next;
    }
    public Task<WebsiteContentVersion?> ReadDraft(CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (gate) { EnsureValid(); return Task.FromResult(state.Draft); } }
    public Task<WebsiteContentVersion?> ReadPublished(CancellationToken ct) { ct.ThrowIfCancellationRequested(); lock (gate) { EnsureValid(); return Task.FromResult(state.Published); } }
    public Task<WebsiteContentVersion> CreateDraft(Guid actor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (gate)
        {
            EnsureValid();
            if (state.Draft != null) return Task.FromResult(state.Draft);
            var text = state.Published == null ? new WebsiteTextInput("", "", "", "", "", "", "", [], []) : Copy(state.Published.Text);
            var draft = new WebsiteContentVersion(Guid.NewGuid(), 1, "draft", text);
            Commit(state with { Draft = draft, DraftHero = state.PublishedHero, DraftPortrait = state.PublishedPortrait }); return Task.FromResult(draft);
        }
    }
    public Task<WebsiteContentVersion> SaveDraft(Guid actor, Guid id, long revision, WebsiteTextInput text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var error = WebsiteContentRules.Validate(text, false); if (error != null) throw new ArgumentException(error);
        lock (gate)
        {
            EnsureValid();
            var current = state.Draft;
            if (current == null || current.Id != id || current.Revision != revision || revision == long.MaxValue) throw new InvalidOperationException("The draft changed.");
            var next = current with { Revision = revision + 1, Text = Copy(text) };
            Commit(state with { Draft = next }); return Task.FromResult(next);
        }
    }
    public Task<WebsiteContentVersion> Publish(Guid actor, Guid id, long revision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (gate)
        {
            EnsureValid();
            var draft = state.Draft;
            if (draft == null || draft.Id != id || draft.Revision != revision) throw new InvalidOperationException("The draft changed.");
            var error = WebsiteContentRules.Validate(draft.Text, true); if (error != null) throw new ArgumentException(error);
            var published = draft with { Status = "published" };
            foreach (var image in new[] { state.DraftHero, state.DraftPortrait })
                if (image != null && (!File.Exists(ImagePath(image)) || new FileInfo(ImagePath(image)).Length > WebsiteContentRules.MaxImageBytes)) throw new InvalidOperationException("A draft image is unavailable.");
            Commit(new(null, published, PublishedHero: state.DraftHero, PublishedPortrait: state.DraftPortrait)); return Task.FromResult(published);
        }
    }
    public void Discard(Guid id, long revision)
    {
        lock (gate)
        {
            EnsureValid();
            if (state.Draft == null || state.Draft.Id != id || state.Draft.Revision != revision) throw new InvalidOperationException("The draft changed.");
            Commit(state with { Draft = null, DraftHero = null, DraftPortrait = null });
        }
    }
    public IReadOnlyList<WebsiteImageReference> Images(bool draft)
    {
        lock (gate)
        {
            EnsureValid();
            return new[] { (Role: "hero", Image: draft ? state.DraftHero : state.PublishedHero), (Role: "portrait", Image: draft ? state.DraftPortrait : state.PublishedPortrait) }
                .Where(x => x.Image != null).Select(x => new WebsiteImageReference(x.Image!.Id, x.Image.Id, x.Role, x.Image.AltText, "", 0, true)).ToArray();
        }
    }
    public void SaveImage(Guid draftId, long revision, string role, string? altText, byte[] bytes)
    {
        if (role is not ("hero" or "portrait") || string.IsNullOrWhiteSpace(altText) || altText.Trim().Length > 250) throw new ArgumentException("Choose an image and provide alternative text up to 250 characters.");
        var normalized = WebsiteImageValidator.Normalize(bytes);
        lock (gate)
        {
            EnsureValid();
            if (state.Draft == null || state.Draft.Id != draftId || state.Draft.Revision != revision || revision == long.MaxValue) throw new InvalidOperationException("The draft changed.");
            var image = new DemoContentImage(Guid.NewGuid(), normalized.Extension, altText.Trim());
            using (var output = new FileStream(ImagePath(image), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(normalized.Bytes); output.Flush(true); }
            var next = state with { Draft = state.Draft with { Revision = revision + 1 } };
            next = role == "hero" ? next with { DraftHero = image } : next with { DraftPortrait = image };
            Commit(next);
        }
    }
    public (byte[] Bytes, string Type)? ReadImage(Guid id, bool draft)
    {
        lock (gate)
        {
            EnsureValid();
            var image = (draft ? new[] { state.DraftHero, state.DraftPortrait } : new[] { state.PublishedHero, state.PublishedPortrait }).FirstOrDefault(x => x?.Id == id);
            if (image == null) return null;
            var file = ImagePath(image);
            if (!File.Exists(file) || new FileInfo(file).Length > WebsiteContentRules.MaxImageBytes) return null;
            return (File.ReadAllBytes(file), image.Extension == ".png" ? "image/png" : "image/jpeg");
        }
    }
    public void Dispose() => lease.Dispose();
}
