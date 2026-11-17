using System.Security.Cryptography;

namespace GlamByMariya.Web.Middleware;

/// <summary>
/// Builds the audit-only Content Security Policy. The header is emitted as
/// Content-Security-Policy-Report-Only, so a missed origin or inline style is
/// reported in the browser console instead of breaking the studio UI.
/// </summary>
public static class ContentSecurityPolicy
{
    public const string NonceItemKey = "csp-nonce";
    public const string ReportOnlyHeader = "Content-Security-Policy-Report-Only";

    private const string GoogleFontStylesOrigin = "https://fonts.googleapis.com";
    private const string GoogleFontFilesOrigin = "https://fonts.gstatic.com";

    /// <summary>128-bit, base64url-safe per-request nonce.</summary>
    public static string CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Only explicitly configured image origins are added. Invalid, relative and
    /// non-HTTP(S) values are ignored, and no provider domain is guessed.
    /// </summary>
    public static IReadOnlyList<string> ImageOrigins(IConfiguration configuration)
    {
        var origins = new List<string>();
        foreach (var key in new[] { "Supabase:Url", "B2:Endpoint" })
        {
            if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out var uri))
                continue;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                continue;

            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                origins.Add(origin);
        }
        return origins;
    }

    public static string BuildReportOnly(string nonce, IEnumerable<string> imageOrigins)
    {
        var images = string.Join(' ', new[] { "'self'" }.Concat(imageOrigins));
        return string.Join("; ",
            "default-src 'self'",
            // Zod 3.23.8 is vendored under wwwroot and needs no third-party origin.
            $"script-src 'self' 'nonce-{nonce}'",
            $"script-src-elem 'self' 'nonce-{nonce}'",
            "script-src-attr 'none'",
            $"style-src 'self' {GoogleFontStylesOrigin}",
            $"style-src-elem 'self' {GoogleFontStylesOrigin}",
            // The audited inline style attributes now use CSS classes, so style
            // attributes are denied in the report-only policy.
            "style-src-attr 'none'",
            $"font-src 'self' {GoogleFontFilesOrigin}",
            $"img-src {images}",
            "connect-src 'self'",
            "form-action 'self'",
            "base-uri 'self'",
            "object-src 'none'",
            "frame-src 'none'",
            "frame-ancestors 'none'",
            "worker-src 'none'",
            "media-src 'none'",
            "manifest-src 'none'");
    }
}
