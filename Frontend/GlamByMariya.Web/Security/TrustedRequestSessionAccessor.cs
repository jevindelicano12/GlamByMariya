using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace GlamByMariya.Web.Security;

// Prepared request boundary only; it does not authorize a database mutation.
internal sealed class TrustedRequestSession
{
    private readonly HttpContext request;
    private readonly TrustedActorSession actor;
    private readonly DateTimeOffset expires;
    private readonly object requestMarker = new();
    private int completed;

    private TrustedRequestSession(HttpContext request, TrustedActorSession actor, DateTimeOffset expires)
    {
        this.request = request;
        this.actor = actor;
        this.expires = expires;
        request.Items[requestMarker] = requestMarker;
        request.Response.OnCompleted(() => { Interlocked.Exchange(ref completed, 1); return Task.CompletedTask; });
    }

    // A request-time check is not a substitute for authorization under database locks.
    internal bool TryGetActor(HttpContext? currentRequest, out TrustedActorSession? result)
    {
        result = null;
        if (!ReferenceEquals(request, currentRequest) || Volatile.Read(ref completed) != 0 ||
            !request.Items.TryGetValue(requestMarker, out var marker) || !ReferenceEquals(marker, requestMarker) ||
            request.RequestAborted.IsCancellationRequested ||
            expires <= DateTimeOffset.UtcNow) return false;
        result = actor;
        return true;
    }

    internal static async Task<TrustedRequestSession?> FromRequestAsync(
        HttpContext? context, IConfiguration configuration,
        IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
    {
        if (context == null || context.RequestAborted.IsCancellationRequested ||
            !configuration.GetValue("Authentication:DurableSessionEnforcement", false))
            return null;
        try
        {
            if (!StudioCookieAuthentication.HasDurableValidation(cookieOptions.Get(CookieAuthenticationDefaults.AuthenticationScheme)))
                return null;
            // Authenticate through the configured handler; never accept context.User or request claims as evidence.
            var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (context.RequestAborted.IsCancellationRequested || !result.Succeeded ||
                result.Ticket?.AuthenticationScheme != CookieAuthenticationDefaults.AuthenticationScheme ||
                result.Properties?.ExpiresUtc is not { } expires || expires <= DateTimeOffset.UtcNow ||
                !TrustedActorSession.TryReadProtectedPrincipal(result.Principal, out var actor) || actor == null)
                return null;
            return new TrustedRequestSession(context, actor, expires);
        }
        catch (Exception) { return null; }
    }
}

internal sealed class TrustedRequestSessionAccessor(
    IHttpContextAccessor contexts, IConfiguration configuration,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
{
    internal Task<TrustedRequestSession?> ResolveAsync() =>
        TrustedRequestSession.FromRequestAsync(contexts.HttpContext, configuration, cookieOptions);
}
