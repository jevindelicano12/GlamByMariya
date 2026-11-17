using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

// This harness intentionally leaves synthetic rows in the explicitly named disposable database.
var readOnly = Environment.GetEnvironmentVariable("GLAM_WEBSITE_READ_ONLY") == "YES";
if (!readOnly && Environment.GetEnvironmentVariable("GLAM_WEBSITE_TEST_WRITE") != "YES")
    throw new InvalidOperationException("Set GLAM_WEBSITE_READ_ONLY=YES or explicitly authorize test writes.");

var cs = new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = 5432, Database = "glam_website_content_test", Username = "postgres",
    Pooling = false, Timeout = 5, SslMode = SslMode.Disable
};
var builder = new NpgsqlDataSourceBuilder(cs.ConnectionString);
async Task VerifyConnection(NpgsqlConnection connection)
{
    await using var guard = new NpgsqlCommand("SELECT current_database()='glam_website_content_test' AND current_user='postgres' AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet AND inet_server_port()=5432", connection);
    if ((bool?)await guard.ExecuteScalarAsync() != true) throw new InvalidOperationException("Unsafe database target; no writes permitted.");
}
builder.UsePhysicalConnectionInitializer(
    connection => VerifyConnection(connection).GetAwaiter().GetResult(),
    connection => VerifyConnection(connection));
await using var source = builder.Build();
if (readOnly)
{
    var readRepo = new PostgresWebsiteContentRepository(source);
    var currentDraft = await readRepo.ReadDraft(default);
    if (currentDraft == null || await readRepo.ReadPublished(default) == null) throw new InvalidOperationException("Expected synthetic draft and published content are unavailable.");
    var draftImages = await readRepo.ReadDraftImages(currentDraft.Id, default);
    if (draftImages.Count == 0) throw new InvalidOperationException("Expected a synthetic draft image.");
    foreach (var image in draftImages)
        if (await readRepo.FindPublishedImage(image.Id, default) != null) throw new Exception("Draft image exposed through published lookup.");
    if (await readRepo.FindPublishedImage(Guid.NewGuid(), default) != null) throw new Exception("Unknown image exposed.");
    Console.WriteLine("PASS PostgreSQL published-image lookup excludes draft and unknown references");
    Console.WriteLine("PASS PostgreSQL published and draft state persists across test processes");
    return;
}
await using (var connection = await source.OpenConnectionAsync())
{
    await using var preflight = new NpgsqlCommand("""
        SELECT (SELECT count(*) FROM public.account_identities) + (SELECT count(*) FROM public.website_content_versions)
        + (SELECT count(*) FROM public.website_content_assets) + (SELECT count(*) FROM public.website_content_images)
        + (SELECT count(*) FROM public.website_content_links) + (SELECT count(*) FROM public.website_content_faqs)
        """, connection);
    if ((long)(await preflight.ExecuteScalarAsync())! != 0) throw new InvalidOperationException("Test database is not empty; no writes permitted.");
}
var actor = Guid.NewGuid();
await using (var connection = await source.OpenConnectionAsync())
await using (var insert = new NpgsqlCommand("INSERT INTO public.account_identities(id) VALUES(@id)", connection))
{ insert.Parameters.AddWithValue("id", actor); await insert.ExecuteNonQueryAsync(); }

var repo = new PostgresWebsiteContentRepository(source);
var text = new WebsiteTextInput("Synthetic headline", "Synthetic description", "Synthetic about", "Synthetic biography", "", "", "", [new("Synthetic link", "https://example.test")], [new("Synthetic question", "Synthetic answer")]);
var draft = await repo.CreateDraft(actor, default);
if (draft.Status != "draft" || await repo.ReadPublished(default) != null) throw new Exception("Draft isolation failed.");
Console.WriteLine("PASS PostgreSQL draft creation and no public draft");
var saved = await repo.SaveDraft(actor, draft.Id, draft.Revision, text, default);
if (saved.Revision != draft.Revision + 1 || saved.Text.Links?.Count != 1 || saved.Text.Faqs?.Count != 1) throw new Exception("Save failed.");
try { await repo.SaveDraft(actor, draft.Id, draft.Revision, text, default); throw new Exception("Stale revision accepted."); }
catch (InvalidOperationException) { Console.WriteLine("PASS PostgreSQL stale save rejected"); }
var published = await repo.Publish(actor, saved.Id, saved.Revision, default);
if (published.Status != "published" || (await repo.ReadPublished(default))?.Text.HeroHeadline != text.HeroHeadline) throw new Exception("Publish/read failed.");
Console.WriteLine("PASS PostgreSQL publish and published-only read");
var next = await repo.CreateDraft(actor, default);
if (next.Id == published.Id || next.Text.HeroHeadline != text.HeroHeadline) throw new Exception("Draft copy failed.");
var assetId = Guid.NewGuid();
await using (var connection = await source.OpenConnectionAsync())
await using (var asset = new NpgsqlCommand("INSERT INTO public.website_content_assets(id,object_key,content_type,byte_length,width,height,uploaded_by) VALUES(@id,@key,'image/png',1,1,1,@actor)", connection))
{ asset.Parameters.AddWithValue("id", assetId); asset.Parameters.AddWithValue("key", "website-content/" + Guid.NewGuid().ToString("N") + ".png"); asset.Parameters.AddWithValue("actor", actor); await asset.ExecuteNonQueryAsync(); }
var reference = new WebsiteImageReference(Guid.NewGuid(), assetId, "gallery", "Synthetic test image", "", 0, false);
await repo.ReplaceDraftImages(actor, next.Id, next.Revision, [reference], default);
var imageDraft = await repo.ReadDraft(default) ?? throw new Exception("Draft missing.");
if ((await repo.ReadDraftImages(next.Id, default)).Count != 1 || (await repo.ReadPublishedImages(default)).Count != 0) throw new Exception("Image isolation failed.");
Console.WriteLine("PASS PostgreSQL draft image isolation");
try { await repo.ReplaceDraftImages(actor, next.Id, imageDraft.Revision, [reference with { Id = Guid.NewGuid(), AssetId = Guid.NewGuid() }], default); throw new Exception("Missing asset accepted."); }
catch (PostgresException e) when (e.SqlState == "23503") { }
var afterFailure = await repo.ReadDraft(default) ?? throw new Exception("Draft missing.");
if (afterFailure.Revision != imageDraft.Revision || (await repo.ReadDraftImages(next.Id, default)).Single().Id != reference.Id) throw new Exception("Failed image save did not roll back.");
Console.WriteLine("PASS PostgreSQL failed image save rolled back revision and references");
try { await repo.Publish(actor, next.Id, afterFailure.Revision, default); throw new Exception("Image-bearing publication unexpectedly succeeded."); }
catch (InvalidOperationException) { }
if ((await repo.ReadPublished(default))?.Id != published.Id) throw new Exception("Failed publish changed public content.");
Console.WriteLine("PASS blocked image publication preserves prior published content");
Console.WriteLine("Synthetic data retained in glam_website_content_test; no cleanup performed.");
