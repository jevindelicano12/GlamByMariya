using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class ProtocolProbe
{
    internal static async Task Run(NpgsqlConnectionStringBuilder settings, DiagnosticTrace trace, CancellationToken ct, long? deadline = null)
    {
        deadline ??= RunDeadline.After(settings.Timeout * 1000);
        Program.CheckBudget(deadline.Value, ct);
        Program.Require(settings.SslMode == SslMode.VerifyFull && settings.SslNegotiation == SslNegotiation.Postgres,
            "Protocol preflight requires unchanged strict PostgreSQL TLS.");
        // Trust-file/configuration failures are fatal safety failures, not recoverable probe results.
        var options = Options(settings);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(settings.Timeout)); // Same 15-second connection budget.
            trace.Begin(Phase.ProtocolConnect);
            using var socket = new TcpClient();
            await socket.ConnectAsync(settings.Host!, settings.Port, limit.Token);
            trace.End(Phase.ProtocolConnect, Outcome.Passed);
            await using var stream = socket.GetStream();
            Program.CheckBudget(deadline.Value, ct);
            trace.Begin(Phase.SslRequest);
            await Exchange(stream, limit.Token);
            trace.End(Phase.SslRequest, Outcome.Passed);
            trace.Begin(Phase.ProtocolTls);
            Program.CheckBudget(deadline.Value, ct);
            using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            Program.CheckBudget(deadline.Value, ct);
            await tls.AuthenticateAsClientAsync(options, limit.Token);
            Program.Require(tls.IsAuthenticated && tls.IsEncrypted, "Strict TLS not established.");
            trace.End(Phase.ProtocolTls, Outcome.Passed);
        }
        catch (Exception error) when (error is SocketException or IOException or AuthenticationException or OperationCanceledException)
        {
            throw new ProtocolFailureException(error);
        }
        finally
        {
            if (options.CertificateChainPolicy is { } policy)
                foreach (var certificate in policy.CustomTrustStore) certificate.Dispose();
        }
        // No PostgreSQL startup message, username, password or SQL is sent on this socket.
    }

    internal static async Task Exchange(Stream stream, CancellationToken ct)
    {
        var request = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(request, 8);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(4), 80877103);
        await stream.WriteAsync(request, ct);
        await stream.FlushAsync(ct);
        var reply = new byte[1];
        await stream.ReadExactlyAsync(reply, ct);
        if (reply[0] != (byte)'S') throw new AuthenticationException("PostgreSQL did not accept the strict TLS request.");
    }

    internal static SslClientAuthenticationOptions Options(NpgsqlConnectionStringBuilder settings)
    {
        Program.Require(settings.SslMode == SslMode.VerifyFull, "TLS verification cannot be weakened.");
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = settings.Host!,
            CertificateRevocationCheckMode = settings.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck
        };
        if (!string.IsNullOrWhiteSpace(settings.RootCertificate))
        {
            var roots = new X509Certificate2Collection();
            if (File.ReadAllText(settings.RootCertificate).Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
                roots.ImportFromPemFile(settings.RootCertificate);
            else roots.Add(X509CertificateLoader.LoadCertificateFromFile(settings.RootCertificate));
            Program.Require(roots.Count > 0 && roots.Cast<X509Certificate2>().All(c => !c.HasPrivateKey), "Invalid public CA file.");
            options.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust, VerificationFlags = X509VerificationFlags.NoFlag,
                RevocationMode = options.CertificateRevocationCheckMode
            };
            options.CertificateChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            options.CertificateChainPolicy.CustomTrustStore.AddRange(roots);
        }
        return options; // Default SslStream validates hostname AND chain; no validation callback.
    }
}

internal sealed class ProtocolFailureException(Exception cause) : Exception("Protocol probe failed.", cause);
