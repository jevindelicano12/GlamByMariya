using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class LocalTlsChecks
{
    internal static async Task Run()
    {
        var directory = Path.Combine("temp", "protocol-tls-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var caPath = Path.GetFullPath(Path.Combine(directory, "synthetic-public-root.cer"));
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Local protocol test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddDays(2));
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Local protocol test server", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var publicLeaf = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var leafWithKey = publicLeaf.CopyWithPrivateKey(serverKey);
        var syntheticPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        // Schannel needs an OS-backed server key, unlike the ephemeral test CA.
        using var server = X509CertificateLoader.LoadPkcs12(
            leafWithKey.Export(X509ContentType.Pfx, syntheticPassword), syntheticPassword, X509KeyStorageFlags.DefaultKeySet);
        File.WriteAllBytes(caPath, root.Export(X509ContentType.Cert)); // Public test CA only.
        try
        {
            foreach (var validName in new[] { true, false })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var clientDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var peer = Serve(listener, server, clientDone.Task, deadline.Token);
                    var settings = new NpgsqlConnectionStringBuilder
                    { Host = "127.0.0.1", Port = port, SslMode = SslMode.VerifyFull, RootCertificate = caPath, Timeout = 15 };
                    using var output = new StringWriter();
                    var trace = new DiagnosticTrace(output);
                    using var observer = new NetworkObserver(trace);
                    try
                    {
                        if (validName)
                        {
                            await ProtocolProbe.Run(settings, trace, deadline.Token);
                            Program.Require(trace.SuccessfulTlsHandshakes > 0, "Actual local TLS success event missing.");
                            Program.Require(!output.ToString().Contains("PostTlsLoginOrBootstrap"), "Credential-free TLS probe claimed PostgreSQL login.");
                        }
                        else
                        {
                            using var socket = new TcpClient();
                            await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                            await ProtocolProbe.Exchange(socket.GetStream(), deadline.Token);
                            using var tls = new SslStream(socket.GetStream());
                            var options = ProtocolProbe.Options(settings);
                            options.TargetHost = "mismatched.invalid"; // Name check only; TCP remains loopback.
                            var rejected = false;
                            try { await tls.AuthenticateAsClientAsync(options, deadline.Token); }
                            catch (AuthenticationException) { rejected = true; }
                            finally { foreach (var certificate in options.CertificateChainPolicy!.CustomTrustStore) certificate.Dispose(); }
                            Program.Require(rejected, "Certificate hostname mismatch was not rejected.");
                        }
                    }
                    catch { Console.Write(output.ToString()); throw; }
                    finally { clientDone.TrySetResult(); }
                    try { await peer; }
                    catch (Exception error) when (!validName && error is AuthenticationException or IOException) { }
                }
                finally { listener.Stop(); }
            }
            Console.WriteLine("PASS: loopback SSLRequest bytes, strict TLS with a synthetic CA, real TLS event observation, no false login claim, and hostname mismatch rejection. No hosted/configuration/trust-store access.");
        }
        finally { File.Delete(caPath); Directory.Delete(directory); }
    }

    private static async Task Serve(TcpListener listener, X509Certificate2 certificate, Task clientDone, CancellationToken ct)
    {
        using var socket = await listener.AcceptTcpClientAsync(ct);
        var stream = socket.GetStream();
        var packet = new byte[8];
        await stream.ReadExactlyAsync(packet, ct);
        Program.Require(BinaryPrimitives.ReadInt32BigEndian(packet) == 8 &&
            BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(4)) == 80877103, "Incorrect SSLRequest encoding.");
        await stream.WriteAsync(new byte[] { (byte)'S' }, ct);
        using var tls = new SslStream(stream);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        { ServerCertificate = certificate, ClientCertificateRequired = false }, ct);
        await clientDone.WaitAsync(ct);
    }
}
