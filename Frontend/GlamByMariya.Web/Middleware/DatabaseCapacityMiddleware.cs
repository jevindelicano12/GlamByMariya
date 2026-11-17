using GlamByMariya.Infrastructure.Data;

namespace GlamByMariya.Web.Middleware;

// A database rejection is never reported as success. Do not re-run a write
// (its outcome could be uncertain); report temporary unavailability instead.
public sealed class DatabaseCapacityMiddleware(RequestDelegate next, ILogger<DatabaseCapacityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception error) when (!context.Response.HasStarted && DatabaseDiagnostics.IsSessionPoolExhausted(error))
        {
            logger.LogWarning("Supabase session pool rejected a request because connection capacity was exhausted.");
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "30";
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("The booking service is temporarily unavailable. Please check your appointments before retrying any payment or booking submission.");
        }
    }
}
