using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Local preparation only. No networking, trust-store changes, app startup or
// database access. Existing provider/password values are opaque, unmodified text.
internal static class Program
{
    private const string Project = "hqllkbjhufhvyooacajy";
    private static readonly string[] CertificateFields = [
        "DataProtection__CertificatePfxBase64", "DataProtection__CertificatePassword",
        "Kestrel__Certificates__Default__Path", "Kestrel__Certificates__Default__Password"
    ];

    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--self-test"]) { SelfTest(); return 0; }
            if (args is not ["--prepare"] || !OperatingSystem.IsWindows())
                throw new InvalidOperationException();
            Prepare();
            Console.WriteLine("PASS: external V2 certificate configuration prepared; unrelated fields/ACLs preserved. HTTPS trust and application startup were NOT performed.");
            return 0;
        }
        catch
        {
            // Never print exception messages, JSON, PFX/passwords or input values.
            Console.Error.WriteLine("BLOCKED: local certificate preparation did not complete. Preserve private files; do not retry or overwrite without inspecting preparation state privately.");
            return 1;
        }
    }

    private static Regex Field(string name) => new(
        @"(?m)^[ \t]*""" + Regex.Escape(name) + @"""[ \t]*:[ \t]*(""(?:\\.|[^""\\])*"")");

    private static string ReadField(string text, string name)
    {
        var matches = Field(name).Matches(text);
        if (matches.Count != 1) throw new InvalidOperationException();
        return JsonSerializer.Deserialize<string>(matches[0].Groups[1].Value)
            ?? throw new InvalidOperationException();
    }

    private static string ReplaceField(string text, string name, string value)
    {
        var matches = Field(name).Matches(text);
        if (matches.Count != 1) throw new InvalidOperationException();
        var group = matches[0].Groups[1];
        return text.Remove(group.Index, group.Length).Insert(group.Index, JsonSerializer.Serialize(value));
    }

    private static X509Certificate2 Certificate(bool https)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            https ? "CN=Glam by Mariya V2 local HTTPS" : "CN=Glam by Mariya V2 Data Protection",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (https)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        }
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string PrivateAcl(string path, bool directory)
    {
        for (DirectoryInfo? parent = new(directory ? path : Path.GetDirectoryName(path)!);
             parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException();
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException();
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
        var allowed = new[] { owner.Value, "S-1-5-18" };
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2 || rules.Any(rule => !allowed.Contains(rule.IdentityReference.Value) ||
            rule.AccessControlType != AccessControlType.Allow ||
            (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl))
            throw new InvalidOperationException();
        return acl.GetSecurityDescriptorSddlForm(AccessControlSections.All);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Prepare()
    {
        var workspace = Directory.GetCurrentDirectory();
        if (!Directory.Exists(Path.Combine(workspace, "Frontend", "GlamByMariya.Web")) ||
            !Directory.Exists(Path.Combine(workspace, "Backend"))) throw new InvalidOperationException();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlamByMariyaV2", "CustomerAuthOwner");
        if (Path.GetFullPath(root).StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        var config = Path.Combine(root, "config", "secrets.json");
        var certificates = Path.Combine(root, "certificates");
        var keys = Path.Combine(root, "keys");
        foreach (var directory in new[] { root, Path.GetDirectoryName(config)!, certificates, keys })
            _ = PrivateAcl(directory, true);
        var aclBefore = PrivateAcl(config, false);
        // Do not silently change the discriminator for a pre-existing key ring.
        if (Directory.EnumerateFileSystemEntries(keys).Any()) throw new InvalidOperationException();
        var dpPath = Path.Combine(certificates, "customer-auth-dp.pfx");
        var tlsPath = Path.Combine(certificates, "customer-auth-https.pfx");
        var publicPath = Path.Combine(certificates, "customer-auth-https.cer");
        if (new[] { dpPath, tlsPath, publicPath }.Any(File.Exists)) throw new InvalidOperationException();
        string original;
        using (var file = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(file, Encoding.UTF8))
            original = reader.ReadToEnd();
        using (JsonDocument.Parse(original)) { }
        if (ReadField(original, "Deployment__V2ProjectId") != Project ||
            ReadField(original, "Database__ProjectId") != Project ||
            ReadField(original, "Supabase__Url") != $"https://{Project}.supabase.co" ||
            ReadField(original, "Database__Username") != $"glam_v2_backend.{Project}" ||
            ReadField(original, "Deployment__Profile") != "v2-customer-auth-test" ||
            ReadField(original, "DataProtection__KeyRingPath") != keys ||
            CertificateFields.Any(name => !string.IsNullOrEmpty(ReadField(original, name))))
            throw new InvalidOperationException();

        using var dp = Certificate(false);
        using var tls = Certificate(true);
        var dpPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var tlsPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var dpBytes = dp.Export(X509ContentType.Pfx, dpPassword);
        var tlsBytes = tls.Export(X509ContentType.Pfx, tlsPassword);
        var updated = ReplaceField(original, CertificateFields[0], Convert.ToBase64String(dpBytes));
        updated = ReplaceField(updated, CertificateFields[1], dpPassword);
        updated = ReplaceField(updated, CertificateFields[2], tlsPath);
        updated = ReplaceField(updated, CertificateFields[3], tlsPassword);
        using (JsonDocument.Parse(updated)) { }
        var before = original;
        var after = updated;
        foreach (var name in CertificateFields)
        {
            before = ReplaceField(before, name, "");
            after = ReplaceField(after, name, "");
        }
        if (before != after) throw new InvalidOperationException();
        foreach (var (path, bytes) in new[] {
            (dpPath, dpBytes), (tlsPath, tlsBytes), (publicPath, tls.Export(X509ContentType.Cert)) })
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(bytes);
            _ = PrivateAcl(path, false);
        }
        CommitConfig(config, original, updated, aclBefore);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CommitConfig(string path, string original, string updated, string aclBefore)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".certificate-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Same private directory; no in-place truncation of the credential file.
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            _ = PrivateAcl(temporary, false);
            using (var output = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                output.Write(new UTF8Encoding(false).GetBytes(updated));
                output.Flush(flushToDisk: true);
            }
            if (File.ReadAllText(path) != original || PrivateAcl(path, false) != aclBefore)
                throw new InvalidOperationException();
            File.Replace(temporary, path, null); // Atomic; Windows retains target ACLs.
            if (File.ReadAllText(path) != updated || PrivateAcl(path, false) != aclBefore)
                throw new InvalidOperationException();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void SelfTest()
    {
        const string original = """
        {
          "Database__Password": "synthetic-unchanged",
          "Supabase__AnonKey": "synthetic-unchanged",
          "DataProtection__CertificatePfxBase64": "",
          "DataProtection__CertificatePassword": "",
          "Kestrel__Certificates__Default__Path": "",
          "Kestrel__Certificates__Default__Password": ""
        }
        """;
        var text = original;
        foreach (var name in CertificateFields) text = ReplaceField(text, name, "synthetic\"\\value");
        using (JsonDocument.Parse(text)) { }
        foreach (var name in CertificateFields)
        {
            if (ReadField(text, name) != "synthetic\"\\value") throw new InvalidOperationException();
            text = ReplaceField(text, name, "");
        }
        if (text != original) throw new InvalidOperationException();
        foreach (var invalid in new[] { "{}", original + original })
        {
            try { _ = ReplaceField(invalid, CertificateFields[0], "x"); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException();
        }
        foreach (var https in new[] { false, true })
        {
            using var certificate = Certificate(https);
            var bytes = certificate.Export(X509ContentType.Pfx, "synthetic-test-only");
            using var loaded = X509CertificateLoader.LoadPkcs12(bytes, "synthetic-test-only", X509KeyStorageFlags.EphemeralKeySet);
            using var rsa = loaded.GetRSAPrivateKey();
            if (rsa == null || loaded.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(7))
                throw new InvalidOperationException();
            if (https && (loaded.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
                .EnumerateIPAddresses().Single().Equals(IPAddress.Loopback) != true ||
                !loaded.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages
                    .Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1")))
                throw new InvalidOperationException();
        }
        if (OperatingSystem.IsWindows()) ConfigCommitTest(original);
        Console.WriteLine("PASS: certificate/PFX and exact-field preservation self-tests; no owner configuration or trust store accessed.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ConfigCommitTest(string original)
    {
        var directory = Path.GetFullPath(Path.Combine("temp", "certificate-config-check-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
        acl.SetOwner(owner);
        foreach (var sid in new[] { owner, new SecurityIdentifier("S-1-5-18") })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
        var path = Path.Combine(directory, "synthetic.json");
        File.WriteAllText(path, original);
        try
        {
            var descriptor = PrivateAcl(path, false);
            var updated = ReplaceField(original, CertificateFields[0], "synthetic");
            CommitConfig(path, original, updated, descriptor);
            if (PrivateAcl(path, false) != descriptor) throw new InvalidOperationException();
            var rejected = false;
            try { CommitConfig(path, original, updated, descriptor); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || File.ReadAllText(path) != updated) throw new InvalidOperationException();
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
