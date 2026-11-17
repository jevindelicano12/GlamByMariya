using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using GlamByMariya.Infrastructure.Data;

namespace GlamByMariya.Web.Security;

// An isolated test surface, not a public-auth deployment switch.
internal static class CustomerAuthIntegrationProfile
{
    internal const string Name = "v2-customer-auth-test";
    internal static bool Enabled(IConfiguration configuration) => configuration["Deployment:Profile"] == Name;
    internal static bool AllowedEmail(IConfiguration configuration, string? email) => !Enabled(configuration) ||
        string.Equals(email?.Trim(), configuration["Authentication:IntegrationCustomerEmail"], StringComparison.OrdinalIgnoreCase);

    internal static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.EnvironmentName != "V2CustomerAuthTest" ||
            configuration.GetValue("PublicSite:InformationOnly", false) ||
            !configuration.GetValue("Authentication:DurableSessionEnforcement", false) ||
            !configuration.GetValue("Authentication:RestrictLegacyFallback", false) ||
            configuration.GetValue("Authentication:IdentityLinkingEnabled", false) ||
            !configuration.GetValue("Email:DeliveryDisabled", false) ||
            configuration["Database:Provider"] != "PostgreSql" || configuration["Database:Schema"] != "public" ||
            configuration["Studio:TimeZone"] != "Asia/Manila")
            throw new InvalidOperationException("Customer integration requires the isolated V2CustomerAuthTest environment, restricted public-schema PostgreSQL and durable authentication.");

        if (!Uri.TryCreate(configuration["urls"], UriKind.Absolute, out var address) ||
            address.Scheme != "https" || address.Host != "127.0.0.1" || address.Port is < 1024 or > 65535 ||
            address.AbsolutePath != "/" || address.Query != "" || address.Fragment != "" || address.UserInfo != "" ||
            configuration.GetSection("Kestrel:Endpoints").GetChildren().Any() ||
            !string.IsNullOrEmpty(configuration["PORT"]))
            throw new InvalidOperationException("Customer integration requires one explicit https://127.0.0.1:<port> URL, no endpoint overrides and no hosting PORT.");

        var project = configuration["Deployment:V2ProjectId"];
        if (string.IsNullOrWhiteSpace(project) || !System.Text.RegularExpressions.Regex.IsMatch(project, "^[a-z]{20}$") ||
            configuration["Database:ProjectId"] != project || configuration["Supabase:Url"] != $"https://{project}.supabase.co" ||
            string.IsNullOrWhiteSpace(configuration["Supabase:AnonKey"]))
            throw new InvalidOperationException("Pin the independently verified NEW V2 project reference in Deployment, Database and the exact Supabase HTTPS URL.");
        ValidatePublicKey(configuration["Supabase:AnonKey"]!, project);

        var email = configuration["Authentication:IntegrationCustomerEmail"];
        if (email == null || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email ||
            email != email.Trim() || email.Length > 254)
            throw new InvalidOperationException("Configure exactly one owner-controlled synthetic integration email.");
        foreach (var key in new[] { "Supabase:ServiceKey", "SupabaseStorage:ServiceKey", "B2:KeyId", "B2:AppKey",
            "Email:BrevoApiKey", "Email:SmtpHost", "Email:SmtpPassword" })
            if (!string.IsNullOrWhiteSpace(configuration[key]))
                throw new InvalidOperationException("Customer integration must not possess staff, Storage, Brevo or SMTP credentials.");
        foreach (var key in new[] { "DemoAccounts:Presentation", "DemoGallery:Enabled", "DemoCatalog:Enabled", "DemoAvatar:Enabled" })
            if (configuration.GetValue(key, false)) throw new InvalidOperationException("Demo features are unavailable in Customer integration.");
        if (configuration["FileStorage:Provider"] is not (null or "" or "Local"))
            throw new InvalidOperationException("Customer integration has no cloud file-storage workflow.");

        var connection = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(configuration)
            ?? throw new InvalidOperationException("Configure the restricted V2 backend connection securely."));
        if (connection.Username != "glam_v2_backend" && connection.Username != $"glam_v2_backend.{project}" ||
            connection.Database != "postgres" || connection.Port != 5432)
            throw new InvalidOperationException("Use only glam_v2_backend on the verified V2 Direct or Session endpoint, port 5432.");
        ValidateKeyDirectory(configuration["DataProtection:KeyRingPath"], environment.ContentRootPath);
        if (string.IsNullOrWhiteSpace(configuration["DataProtection:CertificatePfxBase64"]) ||
            string.IsNullOrWhiteSpace(configuration["DataProtection:CertificatePassword"]))
            throw new InvalidOperationException("Customer integration requires an encrypted persistent key ring and certificate secrets.");
    }

    internal static void ValidatePublicKey(string key, string project)
    {
        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal) && key.Length > 20) return;
        try
        {
            var parts = key.Split('.');
            if (parts.Length != 3) throw new FormatException();
            using var payload = JsonDocument.Parse(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(parts[1]));
            var fields = payload.RootElement.EnumerateObject().ToArray();
            if (fields.Count(p => p.Name == "role") == 1 && fields.Count(p => p.Name == "ref") == 1 &&
                payload.RootElement.GetProperty("role").GetString() == "anon" &&
                payload.RootElement.GetProperty("ref").GetString() == project) return;
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException) { }
        // Configuration classification only, not JWT authentication/verification.
        throw new InvalidOperationException("Use the NEW V2 project's public publishable key or its matching legacy anon key, never an elevated secret/service_role key.");
    }

    internal static void ValidateKeyDirectory(string? directory, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("DataProtection:KeyRingPath must be an absolute private directory outside the source workspace.");
        var full = Path.GetFullPath(directory);
        if (full == Path.GetPathRoot(full))
            throw new InvalidOperationException("Use a dedicated private key directory, not a drive root.");
        full = full.TrimEnd(Path.DirectorySeparatorChar);
        DirectoryInfo? source = new(Path.GetFullPath(contentRoot));
        while (source != null && !(Directory.Exists(Path.Combine(source.FullName, "Frontend", "GlamByMariya.Web")) &&
                                   Directory.Exists(Path.Combine(source.FullName, "Backend"))))
            source = source.Parent;
        var workspace = source?.FullName
            ?? throw new InvalidOperationException("Run the isolated integration profile from the active V2 source workspace.");
        if (full.Equals(workspace, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The signing key ring must not be stored in the source workspace.");
        for (var parent = new DirectoryInfo(full); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("The key ring directory must not use symbolic links or junctions.");
    }

    internal static void ConfigureKeyRing(IServiceCollection services, string directory, string project, X509Certificate2 certificate)
    {
        services.AddDataProtection().SetApplicationName($"GlamByMariya.V2.CustomerAuth.{project}")
            .PersistKeysToFileSystem(new DirectoryInfo(directory)).ProtectKeysWithCertificate(certificate);
    }

    internal static X509Certificate2 LoadCertificate(IConfiguration configuration)
    {
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(configuration["DataProtection:CertificatePfxBase64"] ?? ""),
                configuration["DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
            using var rsa = certificate.GetRSAPrivateKey();
            if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
                certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(7) || rsa == null)
            {
                certificate.Dispose();
                throw new CryptographicException();
            }
            return certificate;
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("Data Protection requires a valid private-key PFX, correct password and more than seven days of certificate validity.");
        }
    }

    internal static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "__Host-GlamV2.CustomerAuthTest";
        options.Cookie.Path = "/";
        options.Cookie.Domain = null;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;
    }

    private static readonly HashSet<string> AccountPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Login", "/Account/Register", "/Account/Verify", "/Account/Resend", "/Account/Logout",
        "/Account/ForgotPassword", "/Account/ResetPassword", "/Account/ChangePassword", "/Account/AccessDenied"
    };

    internal static bool AllowedPath(PathString path, string method)
    {
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (read && (path.StartsWithSegments("/css") || path.StartsWithSegments("/js") ||
            path.StartsWithSegments("/images") || path == "/favicon.svg" || path == "/healthz" || path == "/auth-test/session"))
            return true;
        return (read || HttpMethods.IsPost(method)) && AccountPaths.Contains(path.Value ?? "");
    }

    internal static bool LocalHttpsRequest(HttpContext context) =>
        context.Request.IsHttps && context.Request.Host.Host == "127.0.0.1" &&
        context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
        context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local);

    internal static void UseBoundary(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!LocalHttpsRequest(context) || !AllowedPath(context.Request.Path, context.Request.Method))
            { context.Response.StatusCode = 404; return; }
            context.Response.Headers.CacheControl = "no-store";
            await next();
        });
    }

    internal static void UseAuthenticatedPageRedirect(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true && HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.StartsWithSegments("/Account"))
            { context.Response.Redirect("/auth-test/session"); return; }
            if (context.User.Identity?.IsAuthenticated == true && HttpMethods.IsPost(context.Request.Method) &&
                context.Request.Path.StartsWithSegments("/Account") &&
                context.Request.Path != "/Account/Logout" && context.Request.Path != "/Account/ChangePassword")
            { context.Response.StatusCode = 409; return; }
            await next();
        });
    }

    internal static void MapSessionProbe(WebApplication app)
    {
        app.MapGet("/auth-test/session", async (HttpContext context, TrustedRequestSessionAccessor accessor, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var session = await accessor.ResolveAsync();
            if (session == null || !session.TryGetActor(context, out var actor) || actor?.Role != "Customer")
                return Results.Unauthorized();
            return Results.Ok(new { status = "authenticated", role = actor.Role,
                csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken });
        }).RequireAuthorization();
    }
}
