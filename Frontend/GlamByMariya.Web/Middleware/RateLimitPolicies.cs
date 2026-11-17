using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace GlamByMariya.Web.Middleware;

/// <summary>
/// Named ASP.NET Core rate-limit policies for sensitive authentication endpoints.
///
/// Every policy partitions on the client address resolved by ForwardedHeaders
/// (never on a raw X-Forwarded-For header and never on the submitted email), so a
/// throttled response cannot be used to learn whether an address is registered.
/// State is per-process and in-memory only: a restart, redeploy or additional
/// instance starts with fresh counters. See docs/rate-limiting.md.
/// </summary>
public static class RateLimitPolicies
{
    public const string Login = "login";
    public const string Register = "register";
    public const string VerifyCode = "verify-code";
    public const string ResendCode = "resend-code";
    public const string ForgotPassword = "forgot-password";
    public const string ResetPassword = "reset-password";
    public const string AcceptInvite = "accept-invite";
    public const string InviteAdmin = "invite-admin";

    public sealed record Rule(int PermitLimit, TimeSpan Window);

    /// <summary>Policy names and thresholds used by the endpoint attributes and tests.</summary>
    public static IReadOnlyDictionary<string, Rule> All { get; } = new Dictionary<string, Rule>
    {
        // Brute-force protection stays account-aware in LoginAttemptLimiter
        // (10/min per IP+email); this outer window bounds shared-IP bursts.
        [Login] = new(60, TimeSpan.FromMinutes(1)),
        [Register] = new(5, TimeSpan.FromMinutes(10)),
        [VerifyCode] = new(15, TimeSpan.FromMinutes(5)),
        [ResendCode] = new(5, TimeSpan.FromMinutes(5)),
        [ForgotPassword] = new(5, TimeSpan.FromMinutes(10)),
        [ResetPassword] = new(10, TimeSpan.FromMinutes(10)),
        [AcceptInvite] = new(10, TimeSpan.FromMinutes(10)),
        [InviteAdmin] = new(10, TimeSpan.FromMinutes(10)),
    };

    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        foreach (var (name, rule) in All)
            options.AddPolicy(name, context => Partition(context, rule));
        options.OnRejected = WriteRejectionAsync;
    }

    private static RateLimitPartition<string> Partition(HttpContext context, Rule rule) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rule.PermitLimit,
                Window = rule.Window,
                QueueLimit = 0,
            });

    private const string RejectedHtml =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        "<title>Too many requests</title></head>" +
        "<body style=\"font-family:system-ui,sans-serif;max-width:34rem;margin:4rem auto;padding:0 1rem;color:#2b2b2b\">" +
        "<h1 style=\"font-size:1.5rem\">Too many requests</h1>" +
        "<p>For your security, this action is temporarily limited. Please wait a moment and try again.</p>" +
        "<p><a href=\"/\">Return to Glam by Mariya</a></p></body></html>";

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken token)
    {
        var response = context.HttpContext.Response;
        if (response.HasStarted) return;

        var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) && retryAfter > TimeSpan.Zero
            ? retryAfter
            : TimeSpan.FromSeconds(60);

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        response.Headers.CacheControl = "no-store";
        response.ContentType = "text/html; charset=utf-8";
        await response.WriteAsync(RejectedHtml, token);
    }
}
