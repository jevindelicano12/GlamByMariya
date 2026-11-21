using System.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Preparation only: not registered until the schema, grants and isolation have been tested.
public sealed class PostgresWebsiteContentRepository(NpgsqlDataSource source) : IWebsiteContentRepository, IWebsiteImageRepository, IWebsitePublishedAssetLookup, IWebsiteDraftAssetLookup, IWebsiteAssetWriter
{
    private const long WriteLock = 513672914021;

    public async Task<WebsiteContentVersion?> ReadDraft(CancellationToken cancellationToken) => await Read("draft", cancellationToken);
    public async Task<WebsiteContentVersion?> ReadPublished(CancellationToken cancellationToken) => await Read("published", cancellationToken);

    private async Task<WebsiteContentVersion?> Read(string status, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await ReadVersion(connection, transaction, status, false, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task<WebsiteContentVersion?> ReadVersion(NpgsqlConnection connection, NpgsqlTransaction tx, string status, bool forUpdate, CancellationToken ct)
    {
        var sql = "SELECT id,revision,status,hero_headline,hero_description,about_heading,about_biography,home_service_description,service_area_information,contact_details FROM public.website_content_versions WHERE status=@status" + (forUpdate ? " FOR UPDATE" : "");
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("status", status);
        Guid id; long revision; string state; string headline, description, heading, bio, home, area, contact;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            id = reader.GetGuid(0); revision = reader.GetInt64(1); state = reader.GetString(2);
            headline = reader.GetString(3); description = reader.GetString(4); heading = reader.GetString(5);
            bio = reader.GetString(6); home = reader.GetString(7); area = reader.GetString(8); contact = reader.GetString(9);
        }
        var links = new List<WebsiteLinkInput>();
        await using (var linksCommand = new NpgsqlCommand("SELECT label,url FROM public.website_content_links WHERE version_id=@id ORDER BY sort_order,id", connection, tx))
        {
            linksCommand.Parameters.AddWithValue("id", id);
            await using var reader = await linksCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) links.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        var faqs = new List<WebsiteFaqInput>();
        await using (var faqCommand = new NpgsqlCommand("SELECT question,answer FROM public.website_content_faqs WHERE version_id=@id ORDER BY sort_order,id", connection, tx))
        {
            faqCommand.Parameters.AddWithValue("id", id);
            await using var reader = await faqCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) faqs.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        return new(id, revision, state, new(headline, description, heading, bio, home, area, contact, links, faqs));
    }

    private static async Task Lock(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, tx);
        command.Parameters.AddWithValue("key", WriteLock);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<WebsiteImageReference>> ReadDraftImages(Guid draftId, CancellationToken cancellationToken) => ReadImages("draft", draftId, cancellationToken);
    public Task<IReadOnlyList<WebsiteImageReference>> ReadPublishedImages(CancellationToken cancellationToken) => ReadImages("published", null, cancellationToken);

    public async Task<WebsitePublishedAsset?> FindPublishedImage(Guid imageId, CancellationToken ct)
    {
        if (imageId == Guid.Empty) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT a.object_key,a.content_type,a.byte_length
            FROM public.website_content_images i
            JOIN public.website_content_versions v ON v.id=i.version_id
            JOIN public.website_content_assets a ON a.id=i.asset_id
            WHERE i.id=@id AND i.visible AND v.status='published'
            """, connection);
        command.Parameters.AddWithValue("id", imageId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)) : null;
    }

    public async Task<WebsitePublishedAsset?> FindDraftImage(Guid imageId, CancellationToken ct)
    {
        if (imageId == Guid.Empty) return null;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT a.object_key,a.content_type,a.byte_length
            FROM public.website_content_images i
            JOIN public.website_content_versions v ON v.id=i.version_id
            JOIN public.website_content_assets a ON a.id=i.asset_id
            WHERE i.id=@id AND v.status='draft'
            """, connection);
        command.Parameters.AddWithValue("id", imageId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)) : null;
    }

    private async Task<IReadOnlyList<WebsiteImageReference>> ReadImages(string status, Guid? draftId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var sql = """
            SELECT i.id,i.asset_id,i.role,i.alt_text,i.caption,i.sort_order,i.visible
            FROM public.website_content_images i JOIN public.website_content_versions v ON v.id=i.version_id
            WHERE v.status=@status AND (@id IS NULL OR v.id=@id) AND (@status <> 'published' OR i.visible)
            ORDER BY i.sort_order,i.id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)draftId ?? DBNull.Value);
        var items = new List<WebsiteImageReference>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetBoolean(6)));
        return items;
    }

    public async Task ReplaceDraftImages(Guid actor, Guid draftId, long expectedRevision, IReadOnlyList<WebsiteImageReference> images, CancellationToken ct)
    {
        if (actor == Guid.Empty || draftId == Guid.Empty || expectedRevision < 1 || images == null || images.Any(x => x == null) || images.Count > WebsiteContentRules.MaxGalleryImages + 2 ||
            images.Count(x => x.Role == "gallery") > WebsiteContentRules.MaxGalleryImages || images.Count(x => x.Role == "hero") > 1 || images.Count(x => x.Role == "portrait") > 1 ||
            images.Any(x => x.Id == Guid.Empty || x.AssetId == Guid.Empty || x.Role is not ("hero" or "portrait" or "gallery") || string.IsNullOrWhiteSpace(x.AltText) || x.AltText.Length > 250 || x.Caption == null || x.Caption.Length > 500 || x.SortOrder < 0) ||
            images.Select(x => x.Id).Distinct().Count() != images.Count)
            throw new ArgumentException("Invalid website images.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Lock(connection, tx, ct);
        await using (var update = new NpgsqlCommand("UPDATE public.website_content_versions SET revision=revision+1,updated_by=@actor,updated_at=now() WHERE id=@id AND status='draft' AND revision=@revision", connection, tx))
        {
            update.Parameters.AddWithValue("actor", actor); update.Parameters.AddWithValue("id", draftId); update.Parameters.AddWithValue("revision", expectedRevision);
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The draft changed. Reload before saving images.");
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM public.website_content_images WHERE version_id=@id", connection, tx))
        { delete.Parameters.AddWithValue("id", draftId); await delete.ExecuteNonQueryAsync(ct); }
        foreach (var image in images)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO public.website_content_images(id,version_id,asset_id,role,alt_text,caption,sort_order,visible) VALUES(@id,@version,@asset,@role,@alt,@caption,@order,@visible)", connection, tx);
            insert.Parameters.AddWithValue("id", image.Id); insert.Parameters.AddWithValue("version", draftId); insert.Parameters.AddWithValue("asset", image.AssetId);
            insert.Parameters.AddWithValue("role", image.Role); insert.Parameters.AddWithValue("alt", image.AltText); insert.Parameters.AddWithValue("caption", image.Caption);
            insert.Parameters.AddWithValue("order", image.SortOrder); insert.Parameters.AddWithValue("visible", image.Visible); await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<Guid> AddAsset(Guid actor, string objectKey, string contentType, int byteLength, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("INSERT INTO public.website_content_assets(id,object_key,content_type,byte_length,uploaded_by) VALUES(@id,@key,@type,@length,@actor)", connection);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("key", objectKey); command.Parameters.AddWithValue("type", contentType);
        command.Parameters.AddWithValue("length", byteLength); command.Parameters.AddWithValue("actor", actor);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<WebsiteContentVersion> CreateDraft(Guid actor, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Lock(connection, tx, ct);
        var existing = await ReadVersion(connection, tx, "draft", true, ct);
        if (existing != null) { await tx.CommitAsync(ct); return existing; }
        var published = await ReadVersion(connection, tx, "published", false, ct);
        var id = Guid.NewGuid();
        await using (var create = new NpgsqlCommand("""
            INSERT INTO public.website_content_versions(id,status,hero_headline,hero_description,about_heading,about_biography,
            home_service_description,service_area_information,contact_details,created_by,updated_by)
            VALUES(@id,'draft',@headline,@description,@heading,@bio,@home,@area,@contact,@actor,@actor)
            """, connection, tx))
        {
            var text = published?.Text;
            create.Parameters.AddWithValue("id", id); create.Parameters.AddWithValue("headline", text?.HeroHeadline ?? "");
            create.Parameters.AddWithValue("description", text?.HeroDescription ?? ""); create.Parameters.AddWithValue("heading", text?.AboutHeading ?? "");
            create.Parameters.AddWithValue("bio", text?.AboutBiography ?? ""); create.Parameters.AddWithValue("home", text?.HomeServiceDescription ?? "");
            create.Parameters.AddWithValue("area", text?.ServiceAreaInformation ?? ""); create.Parameters.AddWithValue("contact", text?.ContactDetails ?? "");
            create.Parameters.AddWithValue("actor", actor); await create.ExecuteNonQueryAsync(ct);
        }
        if (published != null)
        {
            foreach (var sql in new[] {
                "INSERT INTO public.website_content_links(id,version_id,label,url,sort_order) SELECT gen_random_uuid(),@new,label,url,sort_order FROM public.website_content_links WHERE version_id=@old",
                "INSERT INTO public.website_content_faqs(id,version_id,question,answer,sort_order) SELECT gen_random_uuid(),@new,question,answer,sort_order FROM public.website_content_faqs WHERE version_id=@old",
                "INSERT INTO public.website_content_images(id,version_id,asset_id,role,alt_text,caption,sort_order,visible) SELECT gen_random_uuid(),@new,asset_id,role,alt_text,caption,sort_order,visible FROM public.website_content_images WHERE version_id=@old" })
            {
                await using var copy = new NpgsqlCommand(sql, connection, tx);
                copy.Parameters.AddWithValue("new", id); copy.Parameters.AddWithValue("old", published.Id); await copy.ExecuteNonQueryAsync(ct);
            }
        }
        var draft = await ReadVersion(connection, tx, "draft", false, ct) ?? throw new InvalidOperationException("Draft not found.");
        await tx.CommitAsync(ct); return draft;
    }

    public async Task<WebsiteContentVersion> SaveDraft(Guid actor, Guid draftId, long expectedRevision, WebsiteTextInput text, CancellationToken ct)
    {
        var error = WebsiteContentRules.Validate(text, false);
        if (error != null) throw new ArgumentException(error);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Lock(connection, tx, ct);
        await using (var update = new NpgsqlCommand("""
            UPDATE public.website_content_versions SET hero_headline=@headline, hero_description=@description, about_heading=@heading,
            about_biography=@bio, home_service_description=@home, service_area_information=@area, contact_details=@contact,
            revision=revision+1, updated_by=@actor, updated_at=now()
            WHERE id=@id AND status='draft' AND revision=@revision
            """, connection, tx))
        {
            update.Parameters.AddWithValue("headline", text.HeroHeadline ?? ""); update.Parameters.AddWithValue("description", text.HeroDescription ?? "");
            update.Parameters.AddWithValue("heading", text.AboutHeading ?? ""); update.Parameters.AddWithValue("bio", text.AboutBiography ?? "");
            update.Parameters.AddWithValue("home", text.HomeServiceDescription ?? ""); update.Parameters.AddWithValue("area", text.ServiceAreaInformation ?? "");
            update.Parameters.AddWithValue("contact", text.ContactDetails ?? ""); update.Parameters.AddWithValue("actor", actor);
            update.Parameters.AddWithValue("id", draftId); update.Parameters.AddWithValue("revision", expectedRevision);
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The draft changed. Reload before saving.");
        }
        foreach (var table in new[] { "website_content_links", "website_content_faqs" })
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM public.{table} WHERE version_id=@id", connection, tx);
            delete.Parameters.AddWithValue("id", draftId); await delete.ExecuteNonQueryAsync(ct);
        }
        for (var i = 0; i < text.Links!.Count; i++)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO public.website_content_links(id,version_id,label,url,sort_order) VALUES(@item,@id,@label,@url,@order)", connection, tx);
            insert.Parameters.AddWithValue("item", Guid.NewGuid()); insert.Parameters.AddWithValue("id", draftId);
            insert.Parameters.AddWithValue("label", text.Links[i].Label!); insert.Parameters.AddWithValue("url", text.Links[i].Url!); insert.Parameters.AddWithValue("order", i);
            await insert.ExecuteNonQueryAsync(ct);
        }
        for (var i = 0; i < text.Faqs!.Count; i++)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO public.website_content_faqs(id,version_id,question,answer,sort_order) VALUES(@item,@id,@question,@answer,@order)", connection, tx);
            insert.Parameters.AddWithValue("item", Guid.NewGuid()); insert.Parameters.AddWithValue("id", draftId);
            insert.Parameters.AddWithValue("question", text.Faqs[i].Question!); insert.Parameters.AddWithValue("answer", text.Faqs[i].Answer!); insert.Parameters.AddWithValue("order", i);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var saved = await ReadVersion(connection, tx, "draft", false, ct) ?? throw new InvalidOperationException("Draft not found.");
        await tx.CommitAsync(ct); return saved;
    }

    public async Task<WebsiteContentVersion> Publish(Guid actor, Guid draftId, long expectedRevision, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Lock(connection, tx, ct);
        var draft = await ReadVersion(connection, tx, "draft", true, ct);
        if (draft == null || draft.Id != draftId || draft.Revision != expectedRevision) throw new InvalidOperationException("The draft changed. Reload before publishing.");
        var error = WebsiteContentRules.Validate(draft.Text, true);
        if (error != null) throw new ArgumentException(error);
        await using (var images = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM public.website_content_images i WHERE i.version_id=@id AND NOT EXISTS(SELECT 1 FROM public.website_content_assets a WHERE a.id=i.asset_id))", connection, tx))
        {
            images.Parameters.AddWithValue("id", draftId);
            if ((bool)(await images.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("A website image is missing. Remove it and publish again.");
        }
        await using (var archive = new NpgsqlCommand("UPDATE public.website_content_versions SET status='archived' WHERE status='published'", connection, tx)) await archive.ExecuteNonQueryAsync(ct);
        await using (var publish = new NpgsqlCommand("UPDATE public.website_content_versions SET status='published',published_by=@actor,published_at=now(),updated_by=@actor,updated_at=now() WHERE id=@id AND status='draft' AND revision=@revision", connection, tx))
        {
            publish.Parameters.AddWithValue("actor", actor); publish.Parameters.AddWithValue("id", draftId); publish.Parameters.AddWithValue("revision", expectedRevision);
            if (await publish.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The draft changed. Reload before publishing.");
        }
        var result = draft with { Status = "published" };
        await tx.CommitAsync(ct); return result;
    }
}
