using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
V2DeploymentConfiguration.Validate(builder.Configuration, builder.Environment);
// Render (and similar hosts) assign the port through $PORT. Local runs keep
// the default unless --urls is passed explicitly.
if (Environment.GetEnvironmentVariable("PORT") is { } port && int.TryParse(port, out var portNumber))
    builder.WebHost.UseUrls($"http://0.0.0.0:{portNumber}");
if (builder.Configuration.GetValue<bool>("PublicSite:InformationOnly"))
{
    await PublicOnlySite.RunAsync(builder, args);
    return;
}
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Keep ASP.NET Core's default loopback trust. Render proxy addresses are
    // deployment-specific and must be supplied explicitly when known; never
    // clear the trust lists and silently trust arbitrary forwarded headers.
    var configured = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").GetChildren()
        .Select(x => x.Value)
        .Concat((builder.Configuration["ForwardedHeaders:KnownProxies"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    foreach (var value in configured.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!System.Net.IPAddress.TryParse(value, out var address))
            throw new InvalidOperationException("ForwardedHeaders:KnownProxies contains an invalid IP address.");
        options.KnownProxies.Add(address);
    }
});
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
// Antiforgery otherwise adds X-Frame-Options: SAMEORIGIN to HTML responses.
// The app sets a stricter DENY baseline in SecurityHeadersMiddleware; suppress
// the framework copy so browsers never receive two conflicting frame policies.
builder.Services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);
builder.Services.AddStudioCookieAuthentication(builder.Environment.IsDevelopment());
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(RateLimitPolicies.Configure);
builder.Services.AddHttpContextAccessor();
var databaseCommand = args.FirstOrDefault(x => x is "--database-init" or "--database-check" or "--database-create-admin" or "--database-create-superadmin" or "--database-inspect" or "--database-profile" or "--database-migrate" or "--database-reset-password" or "--database-smoke" or "--storage-migrate");
var provider = builder.Configuration["Database:Provider"] ?? "Auto";
if (provider is not ("Auto" or "Demo" or "PostgreSql")) throw new InvalidOperationException("Database:Provider must be Auto, Demo or PostgreSql.");
var connection = provider == "Demo" ? null : DatabaseConfiguration.ConnectionString(builder.Configuration);
if ((provider == "PostgreSql" || databaseCommand != null || !builder.Environment.IsDevelopment()) && connection == null && provider != "Demo")
    throw new InvalidOperationException("Configure Database__Password or ConnectionStrings__MakeupDatabase securely. PostgreSQL has no usable password yet; refusing to silently run demo data in production.");
var schema = builder.Configuration["Database:Schema"] ?? "makeup";
if (connection != null)
{
    // Explicit singleton data source = exactly one client pool per app process,
    // including the separate Data Protection DbContext. DI disposes it at exit.
    builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connection));
}
if (connection != null && schema == "public" && databaseCommand == null)
{
    var pfxBase64 = builder.Configuration["DataProtection:CertificatePfxBase64"];
    var pfxPassword = builder.Configuration["DataProtection:CertificatePassword"];
    if (string.IsNullOrWhiteSpace(pfxBase64) || string.IsNullOrWhiteSpace(pfxPassword))
    {
        if (!builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Production Data Protection requires DataProtection__CertificatePfxBase64 and DataProtection__CertificatePassword as server secrets.");
    }
    else
    {
        var certificate = DataProtectionCertificate.Load(builder.Configuration);
        {
            builder.Services.AddDbContext<DataProtectionKeyContext>((sp, options) =>
                options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));
            builder.Services.AddDataProtection()
                .SetApplicationName("GlamByMariyaMakeupSystem")
                .PersistKeysToDbContext<DataProtectionKeyContext>()
                .ProtectKeysWithCertificate(certificate);
        }
    }
}
if (connection != null)
{
    if (schema == "public")
    {
        builder.Services.AddDbContext<MakeupDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));
        builder.Services.AddDbContext<PublicMakeupDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));
        builder.Services.AddScoped<PublicStudioStore>();
        builder.Services.AddScoped<PostgresStudioStore>(sp => sp.GetRequiredService<PublicStudioStore>());
        builder.Services.AddScoped<IStudioStore>(sp => sp.GetRequiredService<PublicStudioStore>());
    }
    else
    {
        builder.Services.AddDbContext<MakeupDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));
        builder.Services.AddScoped<PostgresStudioStore>();
        builder.Services.AddScoped<IStudioStore>(sp => sp.GetRequiredService<PostgresStudioStore>());
    }
}
else
{
    if (databaseCommand != null) throw new InvalidOperationException("Database commands require PostgreSQL configuration.");
    var presentationAccounts = builder.Configuration.GetValue<bool>("DemoAccounts:Presentation");
    if (!builder.Environment.IsDevelopment() || provider != "Demo" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PORT")) ||
        !Uri.TryCreate(builder.Configuration["urls"], UriKind.Absolute, out var demoUrl) || demoUrl.Scheme != "http" || demoUrl.Host != "127.0.0.1" || demoUrl.AbsolutePath != "/" ||
        builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        throw new InvalidOperationException("Demo mode requires a local Development Demo with an explicit loopback URL.");
    // Demo mode seeds no accounts. Only the opt-in presentation accounts need local passwords.
    string?[]? passwords = null;
    if (presentationAccounts)
    {
        passwords = new[] { Environment.GetEnvironmentVariable("GLAM_DEMO_ADMIN_PASSWORD"), Environment.GetEnvironmentVariable("GLAM_DEMO_OWNER_PASSWORD"), Environment.GetEnvironmentVariable("GLAM_DEMO_CUSTOMER_PASSWORD") };
        foreach (var (password, role) in passwords.Zip(new[] { "Admin", "SuperAdmin", "Customer" }))
        {
            var error = PasswordPolicy.ValidateNewPassword(password);
            if (error != null) throw new InvalidOperationException($"{role} presentation password: {error}");
        }
        if (passwords.Distinct(StringComparer.Ordinal).Count() != passwords.Length) throw new InvalidOperationException("Choose a different local password for each account.");
    }
    if (builder.Configuration.GetValue<bool>("DemoCatalog:Enabled"))
    {
        var catalogRoot = builder.Configuration["DemoCatalog:Root"];
        var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlamByMariya");
        if (!builder.Environment.IsDevelopment() || provider != "Demo" || !builder.Configuration.GetValue<bool>("DemoGallery:Enabled") ||
            string.IsNullOrWhiteSpace(catalogRoot) || !Path.IsPathFullyQualified(catalogRoot) ||
            !Path.GetFullPath(catalogRoot).StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Durable Demo catalog requires loopback Demo and an isolated local folder.");
        var fullRoot = Path.GetFullPath(catalogRoot).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var other in new[] { builder.Configuration["DemoGallery:Root"], builder.Configuration["DemoAvatar:Root"], builder.Configuration["WebsiteContent:DemoRoot"] })
        {
            if (string.IsNullOrWhiteSpace(other)) continue;
            var fullOther = Path.GetFullPath(other).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(fullRoot, fullOther, StringComparison.OrdinalIgnoreCase) || fullRoot.StartsWith(fullOther + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || fullOther.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Durable Demo catalog must use a separate folder.");
        }
        builder.Services.AddSingleton(_ => new DemoCatalogSnapshot(fullRoot));
        builder.Services.AddSingleton(sp => new DemoCatalogMedia(Path.Combine(fullRoot, "media"), sp.GetRequiredService<DemoStore>()));
    }
    builder.Services.AddSingleton(sp => new DemoStore(sp.GetService<DemoCatalogSnapshot>(), passwords as string[], presentationAccounts));
    builder.Services.AddSingleton<IStudioStore>(sp => sp.GetRequiredService<DemoStore>());
}
if (connection != null && builder.Configuration.GetValue<bool>("DemoCatalog:Enabled")) throw new InvalidOperationException("Durable Demo catalog cannot be enabled with PostgreSQL.");
if (builder.Environment.IsDevelopment() && provider == "Demo" && builder.Configuration.GetValue<bool>("WebsiteContent:DemoTextEditing"))
{
    if (builder.Configuration.GetValue<bool>("WebsiteContent:DemoPersistentText"))
    {
        var root = builder.Configuration["WebsiteContent:DemoRoot"];
        var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlamByMariya");
        if (!builder.Configuration.GetValue<bool>("DemoGallery:Enabled") || string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
            !Path.GetFullPath(root).StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Persistent Demo text requires loopback Demo and an isolated local folder.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var other in new[] { builder.Configuration["DemoGallery:Root"], builder.Configuration["DemoAvatar:Root"] })
        {
            if (string.IsNullOrWhiteSpace(other)) continue;
            var fullOther = Path.GetFullPath(other).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(fullRoot, fullOther, StringComparison.OrdinalIgnoreCase) || fullRoot.StartsWith(fullOther + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || fullOther.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Persistent Demo text must use a separate folder.");
        }
        builder.Services.AddSingleton<IWebsiteContentRepository>(_ => new DemoFileWebsiteContentRepository(fullRoot));
    }
    else builder.Services.AddSingleton<IWebsiteContentRepository, DemoWebsiteContentRepository>();
    builder.Services.AddSingleton<WebsiteContentService>();
}
else if (builder.Configuration.GetValue<bool>("WebsiteContent:DemoPersistentText")) throw new InvalidOperationException("Persistent Demo text requires the Demo text editor.");
var demoGallery = builder.Configuration.GetValue<bool>("DemoGallery:Enabled");
if (demoGallery)
{
    var address = builder.Configuration["urls"];
    var allowedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlamByMariya");
    var root = builder.Configuration["DemoGallery:Root"];
    if (!builder.Environment.IsDevelopment() || provider != "Demo" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PORT")) ||
        !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" ||
        builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any() ||
        string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
        !Path.GetFullPath(root).StartsWith(Path.GetFullPath(allowedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Demo gallery requires Development, Demo, an explicit loopback URL, and isolated local storage.");
    builder.Services.AddSingleton(new DemoGalleryStore(Path.GetFullPath(root)));
    if (builder.Configuration.GetValue<bool>("DemoAvatar:Enabled"))
    {
        var avatarRoot = builder.Configuration["DemoAvatar:Root"];
        if (string.IsNullOrWhiteSpace(avatarRoot) || !Path.IsPathFullyQualified(avatarRoot) ||
            !Path.GetFullPath(avatarRoot).StartsWith(Path.GetFullPath(allowedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(avatarRoot).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(avatarRoot).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Demo avatars require a separate isolated local folder.");
        builder.Services.AddSingleton(new DemoAvatarStore(Path.GetFullPath(avatarRoot), builder.Configuration.GetValue<bool>("DemoAccounts:Presentation")));
    }
}
else if (builder.Configuration.GetValue<bool>("DemoAvatar:Enabled")) throw new InvalidOperationException("Demo avatars require the loopback Demo gallery guard.");
builder.Services.AddScoped<IServiceRepository>(sp => sp.GetRequiredService<IStudioStore>());
builder.Services.AddScoped<IAddOnRepository>(sp => sp.GetRequiredService<IStudioStore>());
builder.Services.AddScoped<IBookingRepository>(sp => sp.GetRequiredService<IStudioStore>());
builder.Services.AddScoped<IPaymentRepository>(sp => sp.GetRequiredService<IStudioStore>());
builder.Services.AddScoped<BookingService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<LoginAttemptLimiter>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<BrevoEmailSender>();
builder.Services.AddSingleton<GlamByMariya.Infrastructure.Integrations.XenditClient>();
// Email codes: PostgreSQL when a database is configured (survives restarts), memory otherwise.
builder.Services.AddSingleton<IEmailCodeStore>(sp => sp.GetService<NpgsqlDataSource>() is { } emailCodeSource && builder.Configuration["Database:Schema"] == "public"
    ? new PostgresEmailCodeStore(emailCodeSource)
    : new MemoryEmailCodeStore(sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
builder.Services.AddScoped<EmailVerificationService>();
builder.Services.AddScoped<AccountSettingsService>();
builder.Services.AddScoped<AvatarStorageService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MemoryInvitationService>();
builder.Services.AddScoped<IInvitationService>(sp =>
{
    // Public-schema PostgreSQL persists invitations; every other mode (Demo,
    // legacy makeup schema) uses shared process memory. Accept flows are identical.
    var pdb = sp.GetService<PublicMakeupDbContext>();
    var schema = sp.GetRequiredService<IConfiguration>()["Database:Schema"] ?? "makeup";
    if (pdb != null && schema == "public") return new PostgresInvitationService(pdb, sp.GetRequiredService<IStudioStore>(), sp.GetRequiredService<TimeProvider>());
    return sp.GetRequiredService<MemoryInvitationService>();
});
builder.Services.AddScoped<IAppFileStore>(sp =>
{
    // Render Free has no persistent disk: file bytes live in Supabase Storage.
    // Local disk remains the default for development.
    return AppFileStoreFactory.Create(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IWebHostEnvironment>(),
        sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<SupabaseAppFileStore>>());
});
if (connection != null)
{
    builder.Services.AddScoped(sp => new CatalogImageService(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, sp.GetRequiredService<IStudioStore>() as IPersistentStudioStore));
}
else
{
    builder.Services.AddSingleton(sp => new CatalogImageService(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, demoMedia: sp.GetService<DemoCatalogMedia>()));
}
if (connection != null && schema == "public")
{
    // Live website editing: one repository instance backs every website content interface.
    builder.Services.AddSingleton<PostgresWebsiteContentRepository>(sp => new PostgresWebsiteContentRepository(sp.GetRequiredService<NpgsqlDataSource>()));
    builder.Services.AddSingleton<IWebsiteContentRepository>(sp => sp.GetRequiredService<PostgresWebsiteContentRepository>());
    builder.Services.AddSingleton<IWebsiteImageRepository>(sp => sp.GetRequiredService<PostgresWebsiteContentRepository>());
    builder.Services.AddSingleton<IWebsitePublishedAssetLookup>(sp => sp.GetRequiredService<PostgresWebsiteContentRepository>());
    builder.Services.AddSingleton<IWebsiteDraftAssetLookup>(sp => sp.GetRequiredService<PostgresWebsiteContentRepository>());
    builder.Services.AddSingleton<IWebsiteAssetWriter>(sp => sp.GetRequiredService<PostgresWebsiteContentRepository>());
    builder.Services.AddSingleton<WebsiteContentService>();
    builder.Services.AddSingleton<WebsiteImageService>();
    // These use the scoped IAppFileStore, so they must be scoped too.
    builder.Services.AddScoped<WebsitePublicImageService>();
    builder.Services.AddScoped<WebsiteDraftImageService>();
    builder.Services.AddScoped<WebsiteImageUploadService>();
}
var app = builder.Build();
if (databaseCommand != null)
{
    try { await DatabaseSetup.RunAsync(app, databaseCommand); }
    catch (Exception e)
    {
        // Do not print raw provider exceptions/connection strings or credentials.
        Console.Error.WriteLine(DatabaseDiagnostics.Describe(e));
        Environment.ExitCode = 1;
    }
    return;
}
if (connection != null)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var verificationSchema = app.Configuration["Database:Schema"] ?? "makeup";
        if (verificationSchema == "public")
        {
            var pdb = scope.ServiceProvider.GetRequiredService<PublicMakeupDbContext>();
            await using var connectionLease = await DatabaseConnectionLease.OpenAsync(pdb.Database);
            _ = await pdb.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync();
            var views = await pdb.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_views WHERE schemaname='public' AND viewname LIKE 'mvc_%'").SingleAsync();
            if (views < 9) throw new InvalidOperationException($"Database access succeeded, but public integration views are missing (found {views}). Run --database-migrate first; no demo fallback or application writes were allowed.");
            _ = await pdb.Set<SettingsRow>().AsNoTracking().SingleAsync(x => x.Id == 1);
        }
        else await DatabaseInspection.VerifyApplicationAsync(scope.ServiceProvider.GetRequiredService<MakeupDbContext>(), app.Configuration);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine(DatabaseDiagnostics.Describe(e));
        Environment.ExitCode = 1;
        return;
    }
}
// Resolve the scheme from explicitly trusted proxies before HSTS or any other
// scheme-dependent middleware. Keep the trust configuration above unchanged.
if (demoGallery)
    app.Use(async (context, next) =>
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote) ||
            context.Connection.LocalIpAddress is not { } local || !System.Net.IPAddress.IsLoopback(local) ||
            !string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.Ordinal))
        { context.Response.StatusCode = 403; return; }
        await next();
    });
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseMiddleware<SecurityHeadersMiddleware>();
// Empty 4xx/5xx responses (a wrong link, a missing booking) show a page with a way back instead of a blank screen.
app.UseStatusCodePagesWithReExecute("/Home/Status/{0}");
app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<DatabaseCapacityMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// Render health check. Anonymous, dependency-free, safe to hit every few seconds.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "lumiere-makeup" }));
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
