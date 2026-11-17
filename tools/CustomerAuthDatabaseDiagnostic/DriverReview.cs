using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class DriverReview
{
    // Installed binaries/metadata only; no configuration, DNS, sockets or providers.
    internal static void Run()
    {
        var assembly = typeof(NpgsqlConnection).Assembly;
        var events = new List<object>();
        foreach (var (runtime, source, names) in new[]
        {
            (typeof(System.Net.Dns).Assembly, "System.Net.NameResolution", new[] { "ResolutionStart", "ResolutionStop", "ResolutionFailed" }),
            (typeof(System.Net.Sockets.Socket).Assembly, "System.Net.Sockets", new[] { "ConnectStart", "ConnectStop", "ConnectFailed" }),
            (typeof(System.Net.Security.SslStream).Assembly, "System.Net.Security", new[] { "HandshakeStart", "HandshakeStop", "HandshakeFailed" })
        })
        {
            var telemetry = runtime.GetTypes().Single(t => t.IsAssignableTo(typeof(EventSource)) &&
                t.GetCustomAttribute<EventSourceAttribute>()?.Name == source);
            foreach (var name in names)
            {
                var attribute = telemetry.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .First(m => m.Name == name && m.GetCustomAttribute<EventAttribute>() != null)
                    .GetCustomAttribute<EventAttribute>()!;
                events.Add(new { Source = source, Event = name, Level = attribute.Level.ToString(),
                    EnabledByCurrentObserver = attribute.Level <= EventLevel.Informational });
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Framework = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            NpgsqlVersion = assembly.GetName().Version?.ToString(),
            Target = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName,
            Defaults = new { Gss = new NpgsqlConnectionStringBuilder().GssEncryptionMode.ToString(),
                SslNegotiation = new NpgsqlConnectionStringBuilder().SslNegotiation.ToString(),
                ChannelBinding = new NpgsqlConnectionStringBuilder().ChannelBinding.ToString(),
                RequireAuth = new NpgsqlConnectionStringBuilder().RequireAuth?.ToString() ?? "Unspecified",
                CheckCertificateRevocation = new NpgsqlConnectionStringBuilder().CheckCertificateRevocation },
            Events = events
        }));
    }
}
