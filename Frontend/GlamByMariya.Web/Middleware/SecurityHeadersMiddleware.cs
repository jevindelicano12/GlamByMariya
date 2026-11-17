namespace GlamByMariya.Web.Middleware;

/// <summary>
/// Baseline response headers for every response: HTML, static assets, health
/// checks, redirects, 404s and error pages. Registered before static files so it
/// cannot be bypassed. The callback runs at response start, after any
/// Response.Clear() by later middleware, and does not override a header that a
/// response already set. HSTS remains owned by UseHsts() in production. CSP is
/// added in report-only mode so the policy can be audited without blocking any
/// existing script, stylesheet, font or image.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly IReadOnlyList<string> _imageOrigins = ContentSecurityPolicy.ImageOrigins(configuration);

    public Task InvokeAsync(HttpContext context)
    {
        // Reuse a nonce if the pipeline is re-executed by the exception handler,
        // so the rendered scripts and the response header always agree.
        var nonce = context.Items[ContentSecurityPolicy.NonceItemKey] as string;
        if (string.IsNullOrEmpty(nonce))
        {
            nonce = ContentSecurityPolicy.CreateNonce();
            context.Items[ContentSecurityPolicy.NonceItemKey] = nonce;
        }
        var reportOnlyPolicy = ContentSecurityPolicy.BuildReportOnly(nonce, _imageOrigins);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            if (!headers.ContainsKey("X-Content-Type-Options"))
                headers["X-Content-Type-Options"] = "nosniff";
            if (!headers.ContainsKey("Referrer-Policy"))
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            if (!headers.ContainsKey("X-Frame-Options"))
                headers["X-Frame-Options"] = "DENY";
            if (!headers.ContainsKey(ContentSecurityPolicy.ReportOnlyHeader))
                headers[ContentSecurityPolicy.ReportOnlyHeader] = reportOnlyPolicy;
            return Task.CompletedTask;
        });
        return next(context);
    }
}
