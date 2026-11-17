using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

[assembly: HostingStartup(typeof(PublicOnlyErrorStartup))]

public static class StartupHook
{
    public static void Initialize() => Environment.SetEnvironmentVariable("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "PublicOnlyErrorFixtures");
}

public sealed class PublicOnlyErrorStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder) => builder.ConfigureServices(services =>
    {
        services.AddTransient<IStartupFilter, PublicOnlyErrorFilter>();
        services.Configure<MvcOptions>(options => options.Filters.Add(new InjectedFailureFilter()));
    });
}

public sealed class InjectedFailureFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Request.Path == "/Home/About" && context.HttpContext.Request.Headers.ContainsKey("X-Public-Error-Test"))
            throw new InvalidOperationException("PRIVATE-EXCEPTION-CANARY");
        return next();
    }
}

// Loaded only by a dedicated isolated test host, never by the application.
public sealed class PublicOnlyErrorFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, proceed) =>
        {
            if (context.Request.Path == "/Home/About" && context.Request.Headers["X-Public-Error-Test"] == "handler-failure")
                context.Response.Body = new FailingWriteStream(context.Response.Body);
            await proceed();
        });
        next(app);
    };

    private sealed class FailingWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("SYNTHETIC-WRITE-FAILURE");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(new IOException("SYNTHETIC-WRITE-FAILURE"));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("SYNTHETIC-WRITE-FAILURE"));
    }
}
