using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.BusinessRules;

// A package booking mixes services, one per person. The admin limits how many times a service may
// appear in one package (e.g. Bridal once); null means no limit.
public static class PackageRules
{
    public static string? Check(IReadOnlyList<CatalogItem> servicePerPerson)
    {
        if (servicePerPerson.Count is < 2 or > BookingPricing.MaximumPeople)
            return $"Choose between 2 and {BookingPricing.MaximumPeople} people.";
        foreach (var group in servicePerPerson.GroupBy(s => s.Name))
        {
            var limit = group.First().MaxPerPackage;
            if (limit is { } most && group.Count() > most)
                return $"Only {most} {group.Key} {(most == 1 ? "is" : "are")} allowed per package booking.";
        }
        return null;
    }
}
