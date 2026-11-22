using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.BusinessRules;

// A package booking mixes services, one per person. The admin limits how many times a service may
// appear in one package (e.g. Bridal once); null means no limit.
public static class PackageRules
{
    // A package with different services per person: once the package reaches a service's group minimum,
    // that person pays the service's group price. A service without one (Bridal) keeps its regular price.
    public static decimal PricePerPerson(CatalogItem service, int people) =>
        service.GroupPrice is { } group && people >= service.GroupMinimumPeople ? group : service.Price;
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
