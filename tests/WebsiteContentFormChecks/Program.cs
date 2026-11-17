using System.Security.Claims;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
var auth = (AuthorizeAttribute?)Attribute.GetCustomAttribute(typeof(WebsiteContentController), typeof(AuthorizeAttribute));
Check(auth?.Roles == "SuperAdmin", "controller requires SuperAdmin");
foreach (var method in new[] { "CreateDraftForm", "SaveForm", "PublishForm" })
    Check(Attribute.IsDefined(typeof(WebsiteContentController).GetMethod(method)!, typeof(ValidateAntiForgeryTokenAttribute)), method + " requires antiforgery");

var repo = new FakeRepository();
var registrations = new ServiceCollection();
registrations.AddLogging();
registrations.AddControllersWithViews();
registrations.AddSingleton(new WebsiteContentService(repo));
var services = registrations.BuildServiceProvider();
var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "SuperAdmin")], "test"));
var context = new DefaultHttpContext { RequestServices = services, User = owner };
var controller = new WebsiteContentController { ControllerContext = new ControllerContext { HttpContext = context, RouteData = new RouteData(), ActionDescriptor = new ControllerActionDescriptor() } };
var noDraft = (ViewResult)await controller.Index(default);
Check(((WebsiteContentEditorModel)noDraft.Model!).Enabled && !((WebsiteContentEditorModel)noDraft.Model!).HasDraft, "editor detects no draft using mock");
Check(controller.User.Identity?.IsAuthenticated == true && controller.User.IsInRole("SuperAdmin"), "controller principal setup");
var created = await controller.CreateDraftForm(default);
Check(created is RedirectToActionResult, "draft creation using mock (" + created.GetType().Name + ")");
var index = (ViewResult)await controller.Index(default);
var model = (WebsiteContentEditorModel)index.Model!;
Check(model.HasDraft && model.Links.Count == 10 && model.Faqs.Count == 15, "editor loads bounded fields");
model.HeroHeadline = "Synthetic hero"; model.HeroDescription = "Synthetic description"; model.AboutHeading = "Synthetic about";
model.Links[0] = new() { Label = "Synthetic", Url = "https://example.test" };
model.Faqs[0] = new() { Question = "Question", Answer = "Answer" };
var oldRevision = model.Revision;
Check(await controller.SaveForm(model, default) is RedirectToActionResult && repo.Draft!.Text.Links?.Count == 1 && repo.Draft.Text.Faqs?.Count == 1, "form saves text and removes empty rows using mock");
model.HeroHeadline = "Unsaved text";
var conflict = await controller.SaveForm(model, default) as ViewResult;
Check(conflict != null && ((WebsiteContentEditorModel)conflict.Model!).HeroHeadline == "Unsaved text" && !controller.ModelState.IsValid, "stale revision preserves submitted text");
controller.ModelState.Clear();
Check(await controller.PublishForm(model.DraftId, oldRevision, default) is ConflictObjectResult, "stale publish rejected using mock");
Check(await controller.Preview(default) is ViewResult, "draft preview using mock");
Check(await controller.PublishForm(repo.Draft!.Id, repo.Draft.Revision, default) is RedirectToActionResult && repo.Published?.Status == "published", "publish uses saved revision with mock");
var outsider = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin")], "test"));
try { await services.GetRequiredService<WebsiteContentService>().CreateDraft(outsider); throw new Exception("Admin accepted"); }
catch (UnauthorizedAccessException) { Console.WriteLine("PASS service denies Admin using mock"); }

var memory = new DemoWebsiteContentRepository();
var local = new WebsiteContentService(memory);
var initial = await local.CreateDraft(owner);
var links = new List<WebsiteLinkInput>();
var text = new WebsiteTextInput("Demo headline", "Demo description", "Demo about", "", "", "", "", links, []);
var saved = await local.Save(owner, initial.Id, initial.Revision, text);
links.Add(new("late change", "https://example.test"));
Check((await local.Preview(owner))!.Text.Links!.Count == 0 && await local.PublicContent() == null, "demo draft is isolated and not public");
try { await local.Save(owner, initial.Id, initial.Revision, text); throw new Exception("stale save accepted"); }
catch (InvalidOperationException) { Console.WriteLine("PASS demo stale save rejected"); }
await local.Publish(owner, saved.Id, saved.Revision);
Check((await local.PublicContent())!.Text.HeroHeadline == "Demo headline" && await local.Preview(owner) == null, "demo publish exposes only saved text");
var next = await local.CreateDraft(owner);
Check(next.Id != saved.Id && next.Text.HeroHeadline == "Demo headline" && await new WebsiteContentService(new DemoWebsiteContentRepository()).PublicContent() == null, "new draft copies publication and restart resets demo");

var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "website-text-" + Guid.NewGuid().ToString("N"));
Guid draftId; long draftRevision;
using (var durable = new DemoFileWebsiteContentRepository(folder))
{
    var svc = new WebsiteContentService(durable); var createdDraft = await svc.CreateDraft(owner);
    var written = await svc.Save(owner, createdDraft.Id, createdDraft.Revision, new("Local headline", "Local introduction", "Local about", "Private biography", "", "", "", [], []));
    draftId = written.Id; draftRevision = written.Revision;
    Check(await svc.PublicContent() == null, "persistent draft not public");
    try { using var second = new DemoFileWebsiteContentRepository(folder); throw new Exception("second writer accepted"); }
    catch (IOException) { Console.WriteLine("PASS persistent text single-process lock"); }
}
using (var durable = new DemoFileWebsiteContentRepository(folder))
{
    var svc = new WebsiteContentService(durable);
    Check((await svc.Preview(owner))?.Text.AboutBiography == "Private biography" && await svc.PublicContent() == null, "saved draft survives new repository instance privately");
    try { durable.Discard(draftId, draftRevision - 1); throw new Exception("stale discard accepted"); }
    catch (InvalidOperationException) { Console.WriteLine("PASS stale discard rejected"); }
    await svc.Publish(owner, draftId, draftRevision);
}
using (var durable = new DemoFileWebsiteContentRepository(folder))
{
    var svc = new WebsiteContentService(durable);
    Check((await svc.PublicContent())?.Text.AboutBiography == "Private biography", "publication survives new repository instance");
    var copy = await svc.CreateDraft(owner); durable.Discard(copy.Id, copy.Revision);
    Check(await svc.Preview(owner) == null && (await svc.PublicContent())?.Text.AboutBiography == "Private biography", "discard keeps publication");
}
Console.WriteLine("Retained isolated demo text folder: " + folder);
var corrupt = folder + "-corrupt"; Directory.CreateDirectory(corrupt);
var corruptFile = Path.Combine(corrupt, "content.json"); File.WriteAllText(corruptFile, "{not valid json");
using (var durable = new DemoFileWebsiteContentRepository(corrupt))
{
    try { await durable.CreateDraft(Guid.NewGuid(), default); throw new Exception("corrupt state accepted"); }
    catch (InvalidOperationException) { Check(File.ReadAllText(corruptFile) == "{not valid json", "corrupt state fails closed without overwrite"); }
}

sealed class FakeRepository : IWebsiteContentRepository
{
    public WebsiteContentVersion? Draft { get; private set; }
    public WebsiteContentVersion? Published { get; private set; }
    public Task<WebsiteContentVersion?> ReadDraft(CancellationToken ct) => Task.FromResult(Draft);
    public Task<WebsiteContentVersion?> ReadPublished(CancellationToken ct) => Task.FromResult(Published);
    public Task<WebsiteContentVersion> CreateDraft(Guid actor, CancellationToken ct) => Task.FromResult(Draft ??= new(Guid.NewGuid(), 1, "draft", new("", "", "", "", "", "", "", [], [])));
    public Task<WebsiteContentVersion> SaveDraft(Guid actor, Guid id, long revision, WebsiteTextInput text, CancellationToken ct)
    {
        if (Draft == null || Draft.Id != id || Draft.Revision != revision) throw new InvalidOperationException("Conflict");
        Draft = Draft with { Revision = revision + 1, Text = text }; return Task.FromResult(Draft);
    }
    public Task<WebsiteContentVersion> Publish(Guid actor, Guid id, long revision, CancellationToken ct)
    {
        if (Draft == null || Draft.Id != id || Draft.Revision != revision) throw new InvalidOperationException("Conflict");
        Published = Draft with { Status = "published" }; return Task.FromResult(Published);
    }
}
