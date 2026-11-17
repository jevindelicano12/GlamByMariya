namespace GlamByMariya.Web.Security;

internal static class V2DeploymentConfiguration
{
    internal static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var profile = configuration["Deployment:Profile"];
        if (profile == null) return; // Existing loopback/test hosts keep their own guards.
        if (CustomerAuthIntegrationProfile.Enabled(configuration))
        {
            CustomerAuthIntegrationProfile.Validate(configuration, environment);
            return;
        }
        if (profile != "v2-public-only" || !environment.IsProduction() ||
            !configuration.GetValue("PublicSite:InformationOnly", false) ||
            configuration.GetValue("Authentication:DurableSessionEnforcement", false) ||
            !configuration.GetValue("Email:DeliveryDisabled", false))
            throw new InvalidOperationException("The V2 public deployment requires Production, information-only mode, disabled email and closed authentication gates.");

        var hosts = (configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.TrimEntries);
        if (hosts.Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains('*')))
            throw new InvalidOperationException("The V2 public deployment requires explicit AllowedHosts hostnames, without wildcards, schemes or ports.");

        // The public service has no reason to possess any provider credential.
        foreach (var key in new[] {
            "ConnectionStrings:MakeupDatabase", "Database:Password", "Database:Host", "Database:ProjectId",
            "Supabase:Url", "Supabase:AnonKey", "Supabase:ServiceKey", "SupabaseStorage:ServiceKey",
            "B2:KeyId", "B2:AppKey", "Email:BrevoApiKey", "Email:SmtpHost", "Email:SmtpPassword",
            "DataProtection:CertificatePfxBase64", "DataProtection:CertificatePassword", "DataProtection:KeyRingPath" })
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
                throw new InvalidOperationException("Provider configuration must not be attached to the V2 public-only service.");
        }
    }
}
