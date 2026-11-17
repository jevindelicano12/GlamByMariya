using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Web.ViewModels;

public sealed class WebsiteContentEditorModel
{
    public bool Enabled { get; set; }
    public bool HasDraft { get; set; }
    public Guid DraftId { get; set; }
    public long Revision { get; set; }
    public string? HeroHeadline { get; set; }
    public string? HeroDescription { get; set; }
    public string? AboutHeading { get; set; }
    public string? AboutBiography { get; set; }
    public string? HomeServiceDescription { get; set; }
    public string? ServiceAreaInformation { get; set; }
    public string? ContactDetails { get; set; }
    public List<WebsiteLinkEditor> Links { get; set; } = [];
    public List<WebsiteFaqEditor> Faqs { get; set; } = [];

    public static WebsiteContentEditorModel FromDraft(WebsiteContentVersion? draft)
    {
        var model = new WebsiteContentEditorModel { Enabled = true, HasDraft = draft != null };
        if (draft == null) return model;
        model.DraftId = draft.Id; model.Revision = draft.Revision;
        var text = draft.Text;
        model.HeroHeadline = text.HeroHeadline; model.HeroDescription = text.HeroDescription;
        model.AboutHeading = text.AboutHeading; model.AboutBiography = text.AboutBiography;
        model.HomeServiceDescription = text.HomeServiceDescription; model.ServiceAreaInformation = text.ServiceAreaInformation; model.ContactDetails = text.ContactDetails;
        model.Links = (text.Links ?? []).Select(x => new WebsiteLinkEditor { Label = x.Label, Url = x.Url }).ToList();
        model.Faqs = (text.Faqs ?? []).Select(x => new WebsiteFaqEditor { Question = x.Question, Answer = x.Answer }).ToList();
        model.AddEmptySlots();
        return model;
    }

    public void AddEmptySlots()
    {
        while (Links.Count < WebsiteContentRules.MaxLinks) Links.Add(new());
        while (Faqs.Count < WebsiteContentRules.MaxFaqs) Faqs.Add(new());
    }

    public WebsiteTextInput ToText()
    {
        if (Links == null || Faqs == null || Links.Count > WebsiteContentRules.MaxLinks || Faqs.Count > WebsiteContentRules.MaxFaqs || Links.Any(x => x == null) || Faqs.Any(x => x == null))
            throw new ArgumentException("Too many or invalid links or FAQs.");
        return new(HeroHeadline, HeroDescription, AboutHeading, AboutBiography, HomeServiceDescription, ServiceAreaInformation, ContactDetails,
            Links.Where(x => !string.IsNullOrWhiteSpace(x.Label) || !string.IsNullOrWhiteSpace(x.Url)).Select(x => new WebsiteLinkInput(x.Label, x.Url)).ToArray(),
            Faqs.Where(x => !string.IsNullOrWhiteSpace(x.Question) || !string.IsNullOrWhiteSpace(x.Answer)).Select(x => new WebsiteFaqInput(x.Question, x.Answer)).ToArray());
    }
}

public sealed class WebsiteLinkEditor { public string? Label { get; set; } public string? Url { get; set; } }
public sealed class WebsiteFaqEditor { public string? Question { get; set; } public string? Answer { get; set; } }
