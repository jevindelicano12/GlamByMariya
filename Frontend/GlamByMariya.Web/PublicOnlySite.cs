using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Integrations;
using GlamByMariya.Web.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.HttpOverrides;

// Separate host path: no database, provider, authentication or private-file services.
internal static class PublicOnlySite
{
    private static readonly HashSet<string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/Home", "/Home/Index", "/Home/About", "/Home/Services", "/Home/Gallery", "/Home/Contact", "/Home/Terms", "/healthz"
    };
    private static readonly HashSet<string> Assets = new(StringComparer.OrdinalIgnoreCase)
    {
        "/favicon.svg", "/images/botanical.svg", "/css/site.css", "/css/availability.css", "/css/payment-qr.css",
        "/css/booking-refinement.css", "/css/catalog-images.css", "/css/branding.css", "/css/landing.css",
        "/css/workspace-redesign.css", "/css/customer-beauty.css", "/css/typography-system.css", "/css/public-refinement.css",
        "/js/site.js"
    };

    internal static async Task RunAsync(WebApplicationBuilder builder, string[] args)
    {
        if (args.Any(x => x.StartsWith("--database-", StringComparison.OrdinalIgnoreCase) || x.Equals("--storage-migrate", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Database and storage commands are unavailable in information-only mode.");
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            var configured = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").GetChildren().Select(x => x.Value)
                .Concat((builder.Configuration["ForwardedHeaders:KnownProxies"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            foreach (var value in configured.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!System.Net.IPAddress.TryParse(value, out var address))
                    throw new InvalidOperationException("ForwardedHeaders:KnownProxies contains an invalid IP address.");
                options.KnownProxies.Add(address);
            }
        });
        builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
        builder.Services.AddSingleton<IStudioStore>(_ =>
        {
            var store = new DemoStore();
            // Seeded Demo accounts, bookings and sample prices are not public content.
            store.Users.Clear(); store.Bookings.Clear(); store.Payments.Clear(); store.Services.Clear(); store.AddOns.Clear();
            return store;
        });
        builder.Services.AddSingleton(_ => new CatalogImageService(builder.Environment.ContentRootPath));
        var app = builder.Build();
        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler(handler =>
            {
                handler.UseHsts();
                handler.Run(async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync("An unexpected error occurred. Please try again.");
                });
            });
            app.UseHsts();
        }
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) ||
                (!Pages.Contains(path) && !Assets.Contains(path)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Items["InformationOnly"] = true;
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            await next();
        });
        app.UseStaticFiles();
        app.UseRouting();
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "glam-by-mariya-v2", mode = "information-only" }));
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        await app.RunAsync();
    }
}
