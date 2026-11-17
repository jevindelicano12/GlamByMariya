namespace GlamByMariya.Core.BusinessRules;

// Validation only; no editor or storage endpoint is enabled until persistence is verified.
public sealed record WebsiteLinkInput(string? Label, string? Url);
public sealed record WebsiteFaqInput(string? Question, string? Answer);
public sealed record WebsiteTextInput(
    string? HeroHeadline, string? HeroDescription, string? AboutHeading, string? AboutBiography,
    string? HomeServiceDescription, string? ServiceAreaInformation, string? ContactDetails,
    IReadOnlyList<WebsiteLinkInput>? Links, IReadOnlyList<WebsiteFaqInput>? Faqs);

public static class WebsiteContentRules
{
    public const int MaxGalleryImages = 20;
    public const int MaxLinks = 10;
    public const int MaxFaqs = 15;
    public const int MaxImageBytes = 5_000_000;
    public const int MaxDimension = 4_000;
    public const long MaxPixels = 16_000_000;

    public static string? Validate(WebsiteTextInput? input, bool publishing)
    {
        if (input == null) return "Content is required.";
        var fields = new (string Name, string? Value, int Max, bool Required)[]
        {
            ("Hero headline", input.HeroHeadline, 160, true), ("Hero description", input.HeroDescription, 1000, true),
            ("About heading", input.AboutHeading, 160, true), ("Biography", input.AboutBiography, 5000, false),
            ("Home-service description", input.HomeServiceDescription, 2000, false),
            ("Service area", input.ServiceAreaInformation, 1000, false), ("Contact details", input.ContactDetails, 2000, false)
        };
        foreach (var (name, value, max, required) in fields)
        {
            if (value?.Length > max) return $"{name} is too long.";
            if (publishing && required && string.IsNullOrWhiteSpace(value)) return $"{name} is required to publish.";
        }
        if (input.Links == null || input.Faqs == null) return "Links and FAQs must be provided.";
        if (input.Links.Count > MaxLinks) return "Use at most 10 social links.";
        if (input.Faqs.Count > MaxFaqs) return "Use at most 15 FAQs.";
        foreach (var link in input.Links)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.Label) || link.Label.Length > 100 ||
                string.IsNullOrWhiteSpace(link.Url) || link.Url.Length > 2048 ||
                !Uri.TryCreate(link.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrEmpty(url.Host) || !string.IsNullOrEmpty(url.UserInfo)) return "Enter a label and a valid HTTPS social link without credentials.";
        }
        foreach (var faq in input.Faqs)
            if (faq == null || string.IsNullOrWhiteSpace(faq.Question) || faq.Question.Length > 300 ||
                string.IsNullOrWhiteSpace(faq.Answer) || faq.Answer.Length > 3000) return "Enter a question and answer within the length limits.";
        return null;
    }
}
