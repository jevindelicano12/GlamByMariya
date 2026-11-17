using GlamByMariya.Infrastructure.Services;
using System.Security.Claims;
using System.Text.RegularExpressions;

static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
var valid = new WebsiteTextInput("Beauty", "Home-service makeup", "About", "", "", "", "", [], []);
Check(WebsiteContentRules.Validate(valid, true) == null, "valid published content");
Check(WebsiteContentRules.Validate(valid with { HeroHeadline = "" }, true) != null, "missing required published headline");
Check(WebsiteContentRules.Validate(valid with { HeroHeadline = "" }, false) == null, "incomplete draft allowed");
Check(WebsiteContentRules.Validate(valid with { AboutBiography = new string('x', 5001) }, false) != null, "text length limit");
Check(WebsiteContentRules.Validate(valid with { Links = [new("Social", "https://example.test/profile")] }, false) == null, "https social link");
foreach (var url in new[] { "http://example.test", "javascript:alert(1)", "https://user:pass@example.test" })
    Check(WebsiteContentRules.Validate(valid with { Links = [new("Social", url)] }, false) != null, "reject " + url);
Check(WebsiteContentRules.Validate(valid with { Links = Enumerable.Repeat(new WebsiteLinkInput("Link", "https://example.test"), 11).ToArray() }, false) != null, "link count limit");
Check(WebsiteContentRules.Validate(valid with { Faqs = Enumerable.Repeat(new WebsiteFaqInput("Question", "Answer"), 16).ToArray() }, false) != null, "FAQ count limit");
Check(WebsiteContentRules.Validate(valid with { Faqs = [new("", "Answer")] }, false) != null, "FAQ question required");
Check(WebsiteContentRules.MaxGalleryImages == 20 && WebsiteContentRules.MaxDimension == 4000 && WebsiteContentRules.MaxPixels == 16000000 && WebsiteContentRules.MaxImageBytes == 5000000, "provisional image limits defined");

var repo = new TestRepository(valid);
var service = new WebsiteContentService(repo);
ClaimsPrincipal Principal(string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)], "test"));
var owner = Principal("SuperAdmin");
foreach (var user in new[] { Principal("Admin"), Principal("Customer"), new ClaimsPrincipal() })
{
    try { await service.Save(user, repo.Draft.Id, repo.Draft.Revision, valid); throw new Exception("Unauthorized save succeeded"); }
    catch (UnauthorizedAccessException) { }
    try { await service.Preview(user); throw new Exception("Unauthorized preview succeeded"); }
    catch (UnauthorizedAccessException) { }
    try { await service.Publish(user, repo.Draft.Id, repo.Draft.Revision); throw new Exception("Unauthorized publish succeeded"); }
    catch (UnauthorizedAccessException) { }
    try { await service.CreateDraft(user); throw new Exception("Unauthorized draft creation succeeded"); }
    catch (UnauthorizedAccessException) { }
}
Check(repo.Writes == 0, "Admin, Customer and guest denied before repository writes");
Check((await service.Preview(owner))?.Status == "draft", "owner can preview draft using test double");
Check((await service.CreateDraft(owner)).Status == "draft", "owner draft creation reaches test double");
Check(await service.PublicContent() == null, "public receives no draft using test double");
var original = repo.Draft;
await service.Save(owner, original.Id, original.Revision, valid with { HeroHeadline = "Updated" });
try { await service.Save(owner, original.Id, original.Revision, valid); throw new Exception("Stale edit succeeded"); }
catch (InvalidOperationException) { Console.WriteLine("PASS stale draft rejected by test double"); }
var draft = repo.Draft;
await service.Publish(owner, draft.Id, draft.Revision);
Check((await service.PublicContent())?.Text.HeroHeadline == "Updated", "published content visible using test double");
repo.Published = repo.Draft;
try { await service.PublicContent(); throw new Exception("Draft leaked"); }
catch (InvalidOperationException) { Console.WriteLine("PASS public read rejects wrong repository status"); }
var imageRepo = new TestImageRepository();
var imageService = new WebsiteImageService(imageRepo);
foreach (var user in new[] { Principal("Admin"), Principal("Customer"), new ClaimsPrincipal() })
{
    try { await imageService.DraftImages(user, original.Id); throw new Exception("Unauthorized image read succeeded"); }
    catch (UnauthorizedAccessException) { }
    try { await imageService.ReplaceDraftImages(user, original.Id, 1, []); throw new Exception("Unauthorized image write succeeded"); }
    catch (UnauthorizedAccessException) { }
}
Check(imageRepo.Reads == 0 && imageRepo.Writes == 0, "Admin, Customer and guest denied gallery draft access");
await imageService.ReplaceDraftImages(owner, original.Id, 1, []);
Check(imageRepo.Writes == 1, "owner gallery edit reaches test repository");
imageRepo.Items = [new(Guid.NewGuid(), Guid.NewGuid(), "gallery", "Synthetic", "", 0, false)];
try { await imageService.PublishedImages(); throw new Exception("Hidden image exposed"); }
catch (InvalidOperationException) { Console.WriteLine("PASS published read rejects hidden image from test double"); }
var storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "website-content-storage-" + Guid.NewGuid().ToString("N"));
var storage = new IsolatedWebsiteStorage(storageRoot);
var objectKey = "website-content/" + Guid.NewGuid().ToString("N") + ".jpg";
storage.Save(objectKey, [1, 2, 3]);
Check(storage.Read(objectKey)!.Value.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "isolated private test storage round trip");
try { storage.Save(objectKey, [4]); throw new Exception("Storage overwrote an object"); }
catch (IOException) { Console.WriteLine("PASS isolated test storage refuses overwrite"); }
try { storage.Save("../outside.jpg", [1]); throw new Exception("Unsafe key accepted"); }
catch (ArgumentException) { Console.WriteLine("PASS isolated test storage rejects traversal"); }
Console.WriteLine("Test storage retained at " + storageRoot);

// Test double only. It is never registered by the application.
sealed class TestRepository(WebsiteTextInput text) : IWebsiteContentRepository
{
    public WebsiteContentVersion Draft { get; private set; } = new(Guid.NewGuid(), 1, "draft", text);
    public WebsiteContentVersion? Published { get; set; }
    public int Writes { get; private set; }
    public Task<WebsiteContentVersion?> ReadDraft(CancellationToken ct) => Task.FromResult<WebsiteContentVersion?>(Draft);
    public Task<WebsiteContentVersion?> ReadPublished(CancellationToken ct) => Task.FromResult(Published);
    public Task<WebsiteContentVersion> CreateDraft(Guid actor, CancellationToken ct) => Task.FromResult(Draft);
    public Task<WebsiteContentVersion> SaveDraft(Guid actor, Guid id, long revision, WebsiteTextInput input, CancellationToken ct)
    {
        if (id != Draft.Id || revision != Draft.Revision) throw new InvalidOperationException("Conflict");
        Writes++; Draft = Draft with { Revision = revision + 1, Text = input }; return Task.FromResult(Draft);
    }
    public Task<WebsiteContentVersion> Publish(Guid actor, Guid id, long revision, CancellationToken ct)
    {
        if (id != Draft.Id || revision != Draft.Revision) throw new InvalidOperationException("Conflict");
        Writes++; Published = Draft with { Status = "published" }; return Task.FromResult(Published);
    }
}

sealed class TestImageRepository : IWebsiteImageRepository
{
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public IReadOnlyList<WebsiteImageReference> Items { get; set; } = [];
    public Task<IReadOnlyList<WebsiteImageReference>> ReadDraftImages(Guid id, CancellationToken ct) { Reads++; return Task.FromResult(Items); }
    public Task<IReadOnlyList<WebsiteImageReference>> ReadPublishedImages(CancellationToken ct) => Task.FromResult(Items);
    public Task ReplaceDraftImages(Guid actor, Guid id, long revision, IReadOnlyList<WebsiteImageReference> images, CancellationToken ct) { Writes++; return Task.CompletedTask; }
}

// Test-only storage retains all files; it is never registered by the application.
sealed class IsolatedWebsiteStorage(string root)
{
    private string PathFor(string key)
    {
        if (!Regex.IsMatch(key, @"\Awebsite-content/[0-9a-f]{32}\.(jpg|png)\z", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid test key");
        return Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
    }
    public void Save(string key, byte[] bytes)
    {
        var path = PathFor(key); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
    public (byte[] Bytes, string ContentType)? Read(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? (File.ReadAllBytes(path), key.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg") : null;
    }
}
