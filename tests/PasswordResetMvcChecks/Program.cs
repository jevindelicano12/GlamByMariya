using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Infrastructure.Integrations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

// Standalone service container, not Program/Main, WebApplication, Kestrel or a
// production host. No HTTP clients or database services are registered. MVC uses
// compiled production views and real form binders. All keys/cookies are ephemeral.
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL (MVC verification): " + label);
    passed++;
    Console.WriteLine("PASS (MVC verification): " + label);
}

var services = new ServiceCollection();
services.AddLogging();
services.AddMemoryCache();
services.AddSingleton<IHttpClientFactory, NoNetworkHttpClientFactory>();
services.AddSingleton<BrevoEmailSender>();
services.AddSingleton<EmailVerificationService>();
services.AddSingleton<SupabaseAuthService>();
services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
var environment = new IsolatedEnvironment();
services.AddSingleton<IWebHostEnvironment>(environment);
services.AddSingleton<IHostEnvironment>(environment);
services.AddSingleton(new DiagnosticListener("IsolatedResetMvc"));
services.AddSingleton<DiagnosticSource>(sp => sp.GetRequiredService<DiagnosticListener>());
services.AddDataProtection().UseEphemeralDataProtectionProvider();
services.AddAntiforgery(options => options.Cookie.Name = "IsolatedResetCsrf");
services.AddControllersWithViews().AddApplicationPart(typeof(AccountController).Assembly);
services.AddSingleton<IStudioStore, DemoStore>();
services.AddSingleton<ITempDataProvider, InMemoryTempData>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var sp = scope.ServiceProvider;
var method = typeof(AccountController).GetMethods().Single(info => info.Name == "ResetPassword" && info.GetParameters().Length == 4);
var descriptor = new ControllerActionDescriptor
{
    ControllerName = "Account", ActionName = "ResetPassword", MethodInfo = method,
    ControllerTypeInfo = typeof(AccountController).GetTypeInfo(),
    Parameters = method.GetParameters().Select(parameter => (Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor)new ControllerParameterDescriptor
    {
        Name = parameter.Name!, ParameterType = parameter.ParameterType, ParameterInfo = parameter
    }).ToList()
};

ActionContext Context(string action = "ResetPassword")
{
    var http = new DefaultHttpContext { RequestServices = sp };
    http.Request.Scheme = "https";
    http.Request.Host = new HostString("localhost");
    http.Request.Path = "/Account/" + action;
    var route = new RouteData();
    route.Values["controller"] = "Account";
    route.Values["action"] = action;
    // Explicitly test-only URL generation, not production route discovery.
    route.Routers.Add(new IsolatedRouter());
    return new ActionContext(http, route, descriptor, new ModelStateDictionary());
}

async Task<Dictionary<string, object?>> Bind(ActionContext context, Dictionary<string, StringValues> fields)
{
    context.HttpContext.Request.Method = "POST";
    context.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
    context.HttpContext.Request.Form = new FormCollection(fields);
    var values = new FormValueProvider(BindingSource.Form, context.HttpContext.Request.Form, CultureInfo.InvariantCulture);
    var metadata = (ModelMetadataProvider)sp.GetRequiredService<IModelMetadataProvider>();
    var factory = sp.GetRequiredService<IModelBinderFactory>();
    var bound = new Dictionary<string, object?>();
    foreach (var parameter in method.GetParameters())
    {
        var info = metadata.GetMetadataForParameter(parameter);
        var binder = factory.CreateBinder(new ModelBinderFactoryContext { Metadata = info, CacheToken = parameter });
        var binding = DefaultModelBindingContext.CreateBindingContext(context, values, info, bindingInfo: null, parameter.Name!);
        await binder.BindModelAsync(binding);
        bound[parameter.Name!] = binding.Result.IsModelSet ? binding.Result.Model : null;
    }
    return bound;
}

async Task<string> Render(ActionContext context, ViewDataDictionary data, string viewPath = "/Views/Account/ResetPassword.cshtml")
{
    var engine = sp.GetRequiredService<IRazorViewEngine>();
    var found = engine.GetView(null, viewPath, isMainPage: true);
    if (!found.Success) throw new InvalidOperationException("Compiled production view unavailable; no source-compilation fallback used.");
    using var writer = new StringWriter(CultureInfo.InvariantCulture);
    var viewContext = new ViewContext(context, found.View, data,
        new TempDataDictionary(context.HttpContext, sp.GetRequiredService<ITempDataProvider>()), writer, new HtmlHelperOptions());
    await found.View.RenderAsync(viewContext);
    return writer.ToString();
}

string Input(string html, string name) => Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase)
    .Select(match => match.Value).Single(tag => Attribute(tag, "name") == name);
string? Attribute(string tag, string name)
{
    var match = Regex.Match(tag, "\\b" + Regex.Escape(name) + "=\"([^\"]*)\"", RegexOptions.IgnoreCase);
    return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
}
bool Required(string tag) => Regex.IsMatch(tag, @"\brequired(?:\s|=|/?>)", RegexOptions.IgnoreCase);

var initial = Context();
initial.HttpContext.Request.Method = "GET";
var initialData = new ViewDataDictionary(sp.GetRequiredService<IModelMetadataProvider>(), initial.ModelState)
{
    ["Email"] = "member@example.test"
};
var initialHtml = await Render(initial, initialData);
var form = Regex.Match(initialHtml, @"<form\b[^>]*>", RegexOptions.IgnoreCase).Value;
Check(Attribute(form, "method")?.Equals("post", StringComparison.OrdinalIgnoreCase) == true, "compiled form submits POST");
Check(Attribute(form, "action") == "/Account/ResetPassword", "compiled form action uses reset route with isolated URL generator");
Check(Attribute(Input(initialHtml, "newPassword"), "type") == "password"
    && Attribute(Input(initialHtml, "confirmPassword"), "type") == "password", "both compiled secret inputs are password fields");
Check(Required(Input(initialHtml, "newPassword")) && Required(Input(initialHtml, "confirmPassword")), "both inputs render required attributes");
Check(Attribute(Input(initialHtml, "newPassword"), "minlength") == "8"
    && Attribute(Input(initialHtml, "confirmPassword"), "minlength") == "8"
    && Attribute(Input(initialHtml, "newPassword"), "maxlength") == "128"
    && Attribute(Input(initialHtml, "confirmPassword"), "maxlength") == "128",
    "compiled reset fields render shared minimum and maximum constraints");
Check(Attribute(Input(initialHtml, "confirmPassword"), "autocomplete") == "new-password", "confirmation has new-password autocomplete");
Check(method.GetParameters().Any(parameter => parameter.Name == Attribute(Input(initialHtml, "confirmPassword"), "name")),
    "rendered confirmation name matches actual action parameter");
var token = Attribute(Input(initialHtml, "__RequestVerificationToken"), "value");
Check(!string.IsNullOrEmpty(token), "FormTagHelper renders hidden antiforgery token");
var cookie = initial.HttpContext.Response.Headers.SetCookie.Select(value => value?.Split(';')[0])
    .Single(value => value?.StartsWith("IsolatedResetCsrf=", StringComparison.Ordinal) == true)!;
var csrf = sp.GetRequiredService<IAntiforgery>();
foreach (var includeToken in new[] { false, true })
{
    var check = Context().HttpContext;
    check.Request.Method = "POST";
    check.Request.ContentType = "application/x-www-form-urlencoded";
    check.Request.Headers.Cookie = cookie;
    check.Request.Form = new FormCollection(includeToken
        ? new Dictionary<string, StringValues> { ["__RequestVerificationToken"] = token }
        : new Dictionary<string, StringValues>());
    var accepted = false;
    try { await csrf.ValidateRequestAsync(check); accepted = true; }
    catch (AntiforgeryValidationException) { }
    Check(accepted == includeToken, includeToken ? "real antiforgery validates rendered token and synthetic cookie" : "real antiforgery rejects missing token");
}

foreach (var register in new[] { true, false })
{
    var action = register ? "Register" : "Login";
    var context = Context(action);
    context.HttpContext.Request.Method = "GET";
    var data = new ViewDataDictionary(sp.GetRequiredService<IModelMetadataProvider>(), context.ModelState)
    {
        ["ReturnUrl"] = null
    };
    var html = await Render(context, data, "/Views/Account/Login.cshtml");
    var passwordInput = Input(html, "password");
    Check(Attribute(passwordInput, "type") == "password", action + ": shared field remains masked");
    if (register)
    {
        Check(Attribute(passwordInput, "minlength") == "8" && Attribute(passwordInput, "maxlength") == "128"
            && Attribute(passwordInput, "autocomplete") == "new-password",
            "compiled registration form renders both new-password length limits");
        Check(Regex.IsMatch(html, @"\.max\(128") && Regex.IsMatch(html, @"\.regex\(/\[0-9\]/"),
            "rendered registration Zod schema contains maximum and explicit ASCII-digit rule");
    }
    else
    {
        Check(Attribute(passwordInput, "minlength") == null && Attribute(passwordInput, "maxlength") == null
            && Attribute(passwordInput, "autocomplete") == "current-password",
            "compiled login form has no new-password length constraints for historical passwords");
        Check(Attribute(Regex.Match(html, @"<form\b[^>]*>").Value, "data-register") == "false",
            "compiled login form disables the registration-only browser validator");
    }
}

var inviteContext = Context("AcceptInvite");
var inviteData = new ViewDataDictionary(sp.GetRequiredService<IModelMetadataProvider>(), inviteContext.ModelState)
{
    ["Email"] = "invitee@example.test",
    ["Token"] = "synthetic-view-token"
};
var inviteHtml = await Render(inviteContext, inviteData, "/Views/Account/AcceptInvite.cshtml");
Check(Attribute(Input(inviteHtml, "password"), "type") == "password"
    && Attribute(Input(inviteHtml, "password"), "minlength") == "8"
    && Attribute(Input(inviteHtml, "password"), "maxlength") == "128",
    "compiled invitation form renders masked input and shared length limits");

var cases = new (string Label, string Password, string? Confirmation, bool IncludeField, string Error)[]
{
    ("missing confirmation", "UniqueResetA123!", null, false, "Confirm the new password."),
    ("empty confirmation", "UniqueResetA123!", "", true, "Confirm the new password."),
    ("whitespace-only confirmation", "UniqueResetA123!", "   ", true, "Confirm the new password."),
    ("mismatched confirmation", "UniqueResetA123!", "UniqueResetB123!", true, "New passwords do not match."),
    ("case mismatch", "UniqueResetA123!", "uniqueResetA123!", true, "New passwords do not match."),
    ("leading whitespace mismatch", "UniqueResetA123!", " UniqueResetA123!", true, "New passwords do not match."),
    ("trailing whitespace mismatch", "UniqueResetA123!", "UniqueResetA123! ", true, "New passwords do not match."),
    ("Unicode representation mismatch", "UniqueResetA1\u00e9", "UniqueResetA1e\u0301", true, "New passwords do not match."),
    // Invalid code is deliberate: matching confirmation must advance to code-format
    // validation without touching any recovery/provider collaborator.
    ("matching confirmation", "UniqueResetA123!", "UniqueResetA123!", true, "Enter the 6-digit code from your email."),
    ("matching surrounding whitespace", " UniqueResetA123! ", " UniqueResetA123! ", true, "Enter the 6-digit code from your email.")
};
foreach (var sample in cases)
{
    var context = Context();
    var fields = new Dictionary<string, StringValues>
    {
        ["email"] = "member@example.test", ["code"] = "bad", ["newPassword"] = sample.Password
    };
    if (sample.IncludeField) fields["confirmPassword"] = sample.Confirmation;
    var bound = await Bind(context, fields);
    Check((string?)bound["newPassword"] == sample.Password, sample.Label + ": real binder preserves password");
    var expected = string.IsNullOrWhiteSpace(sample.Confirmation) ? null : sample.Confirmation;
    Check((string?)bound["confirmPassword"] == expected, sample.Label + ": real binder maps confirmation");
    // Null sentinels make ANY OTP/provider/store access fail this test. Invalid
    // requests must return before accessing even IsConfigured. No real dependency.
    var controller = new AccountController(null!, null!, null!, null!, null!, null!, null!, null!)
    {
        ControllerContext = new ControllerContext(context)
    };
    var result = await controller.ResetPassword((string)bound["email"]!, (string)bound["code"]!,
        (string)bound["newPassword"]!, (string?)bound["confirmPassword"]);
    Check(result is ViewResult && (string?)controller.ViewData["Error"] == sample.Error,
        sample.Label + ": bound values reach authoritative server check before collaborators");
    var html = await Render(context, controller.ViewData);
    var decoded = WebUtility.HtmlDecode(html);
    Check(!decoded.Contains(sample.Password, StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(sample.Confirmation) || !decoded.Contains(sample.Confirmation, StringComparison.Ordinal)),
        sample.Label + ": neither attempted secret is redisplayed");
    Check(string.IsNullOrEmpty(Attribute(Input(html, "newPassword"), "value"))
        && string.IsNullOrEmpty(Attribute(Input(html, "confirmPassword"), "value")), sample.Label + ": secret inputs render empty despite ModelState values");
    Check(decoded.Contains(sample.Error, StringComparison.Ordinal) && Attribute(Input(html, "email"), "value") == "member@example.test"
        && Input(html, "code").Length > 0 && Input(html, "__RequestVerificationToken").Length > 0,
        sample.Label + ": usable recovery form, email and antiforgery retained");
}

// Source-only evidence for production registration, separate from the standalone
// services above (which deliberately do not execute the production startup code).
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !File.Exists(Path.Combine(root.FullName, "MakeupSystem.slnx"))) root = root.Parent;
if (root == null) throw new InvalidOperationException("Source root unavailable.");
var startup = File.ReadAllText(Path.Combine(root.FullName, "Frontend", "GlamByMariya.Web", "Program.cs"));
Check(startup.Contains("options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute())", StringComparison.Ordinal),
    "SOURCE CONTRACT ONLY: production registers global unsafe-method antiforgery validation");
Check(method.IsDefined(typeof(HttpPostAttribute), inherit: true), "actual action metadata restricts POST");
Console.WriteLine($"MVC binding/rendering verification: {passed} passed; 0 failed; 0 skipped.");
Console.WriteLine("No host/socket/browser or production pipeline. URL generation is test-only; full success/recovery proof is covered by separate fake-backed controller tests.");

sealed class InMemoryTempData : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
}
sealed class NoNetworkHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => throw new InvalidOperationException("Network access is not permitted in this rendering test.");
}
sealed class IsolatedEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = typeof(AccountController).Assembly.GetName().Name!;
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
sealed class IsolatedRouter : IRouter
{
    public Task RouteAsync(RouteContext context) => Task.CompletedTask;
    public VirtualPathData? GetVirtualPath(VirtualPathContext context)
    {
        var controller = context.Values["controller"] ?? context.AmbientValues["controller"] ?? "Account";
        var action = context.Values["action"] ?? context.AmbientValues["action"] ?? "ResetPassword";
        return new VirtualPathData(this, "/" + controller + "/" + action);
    }
}
